using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Measured-or-read facts that decide which hardware-specific launch candidates are worth a server run on this PC.</summary>
internal sealed record HardwareSearchFacts(
    long ModelBytes, long LargestVramBytes, long SystemRamBytes, int LogicalCores, int PhysicalCores, long? BlockCount,
    bool HasNvidiaGpu);

/// <summary>
/// The hardware-aware part of the text tuning search space: GPU layer offload for models that do not fit VRAM, CPU-only for
/// hopeless GPUs, flash attention off where it may be slower (non-NVIDIA), smaller context for tight memory, and thread counts
/// whenever the CPU is on the critical path. Pure candidate generation plus a pure selection rule; every candidate is still
/// measured through an isolated llama-server by <see cref="RuntimeOptimizationService"/>, so a failed (OOM) one is only evidence.
/// A PC where the model fits entirely in a NVIDIA GPU gets no extra candidates: nothing here would help and nothing is wasted.
/// </summary>
internal static class RuntimeHardwareSearchSpace
{
    /// <summary>Server runs per stage; the whole hardware phase is at most <c>MaximumOffloadCandidates + MaximumThreadCandidates</c>.</summary>
    internal const int MaximumOffloadCandidates = 4;
    internal const int MaximumThreadCandidates = 2;
    private const long GiB = 1024L * 1024 * 1024;

    internal static bool CpuOnCriticalPath(HardwareSearchFacts f, LlamaPerformanceProfileDto profile)
    {
        if (f.LargestVramBytes <= 0 || profile.GpuLayers == 0) return true;
        return !FitsGpu(f);
    }

    internal static bool FitsGpu(HardwareSearchFacts f) =>
        f.LargestVramBytes > 0 && TuningSuggestions.EstimatedResidentBytes(f.ModelBytes) <= (long)(f.LargestVramBytes * 0.92);

    /// <summary>Stage A: offload / backend-fit / attention / context candidates, relative to the KV-race winner.</summary>
    internal static List<LlamaPerformanceProfileDto> BuildOffloadCandidates(LlamaPerformanceProfileDto baseline, HardwareSearchFacts f, string workload)
    {
        var list = new List<LlamaPerformanceProfileDto>();
        void Add(LlamaPerformanceProfileDto c) { if (list.Count < MaximumOffloadCandidates) list.Add(c); }
        bool gpu = f.LargestVramBytes > 0;

        if (gpu && !FitsGpu(f) && f.BlockCount is > 1 && f.ModelBytes > 0)
        {
            // Layers that fit: usable VRAM after a compute/KV reserve, divided by the average layer size.
            long usable = (long)(f.LargestVramBytes * 0.90) - 1536L * 1024 * 1024;
            double fraction = Math.Clamp(usable / (double)f.ModelBytes, 0.0, 1.0);
            int blocks = (int)f.BlockCount.Value;
            int fit = Math.Clamp((int)Math.Floor(blocks * fraction), 0, blocks - 1);
            if (fit >= 1)
            {
                Add(With(baseline, "-hw-ngl" + fit, $" · {fit}/{blocks} layers on GPU", c => c.GpuLayers = fit));
                int lean = Math.Max(1, (int)(fit * 0.8));
                if (lean < fit) Add(With(baseline, "-hw-ngl" + lean, $" · {lean}/{blocks} layers on GPU", c => c.GpuLayers = lean));
            }
        }
        if (gpu && TuningSuggestions.EstimatedResidentBytes(f.ModelBytes) > f.LargestVramBytes * 3L || gpu && f.LargestVramBytes < 2 * GiB)
            Add(With(baseline, "-hw-cpu", " · CPU only", c => c.GpuLayers = 0));

        // Flash attention is the right default on NVIDIA; elsewhere (Vulkan, iGPU, CPU) the non-flash path can be faster.
        // Quantised V cache needs flash attention, so the comparison uses the full-precision cache.
        if (!f.HasNvidiaGpu && !string.Equals(baseline.FlashAttention, "off", StringComparison.OrdinalIgnoreCase))
            Add(With(baseline, "-hw-fa-off", " · flash attention off", c => { c.FlashAttention = "off"; c.CacheTypeK = c.CacheTypeV = "f16"; }));

        // Context right-sizing: only for plain chat, only when memory is tight, never below the 8k floor PaRuX needs to answer.
        if (RuntimePerformanceTuner.NormalizeWorkload(workload) == "chat" && (!gpu || !FitsGpu(f)) && baseline.ContextSize >= 16384)
        {
            int half = Math.Max(8192, baseline.ContextSize / 2);
            Add(With(baseline, "-hw-ctx" + half, $" · context {half:N0}", c => c.ContextSize = half));
        }
        return list;
    }

    /// <summary>Stage B: thread counts, only when the CPU decides the speed (CPU-only, or part of the model off the GPU).</summary>
    internal static List<LlamaPerformanceProfileDto> BuildThreadCandidates(LlamaPerformanceProfileDto baseline, HardwareSearchFacts f)
    {
        var list = new List<LlamaPerformanceProfileDto>();
        if (!CpuOnCriticalPath(f, baseline) || f.PhysicalCores < 2) return list;
        int physical = f.PhysicalCores;
        int logical = Math.Max(physical, f.LogicalCores);
        int headroom = physical >= 8 ? 2 : 1;
        foreach ((int threads, string label) in new[] { (physical, "physical cores"), (Math.Max(1, physical - headroom), "physical cores minus " + headroom) })
        {
            if (list.Count >= MaximumThreadCandidates || threads == baseline.Threads) continue;
            if (list.Any(c => c.Threads == threads)) continue;
            list.Add(With(baseline, "-hw-t" + threads, $" · {threads} threads ({label})", c => { c.Threads = threads; c.ThreadsBatch = logical; c.CpuStrict = false; }));
        }
        return list;
    }

    /// <summary>
    /// Adopt a hardware candidate only on a clear, safe win over the current winner: at least 5% faster decoding, first token not
    /// more than 15% slower, no new thermal regression. Ties and everything else keep the baseline.
    /// </summary>
    internal static RuntimeOptimizationServerResult Choose(RuntimeOptimizationServerResult baseline, IEnumerable<RuntimeOptimizationServerResult> candidates)
    {
        double baseDecode = baseline.DecodeTokensPerSecond ?? 0;
        double baseTtft = baseline.TimeToFirstTokenMilliseconds ?? double.MaxValue;
        RuntimeOptimizationServerResult? best = null;
        foreach (RuntimeOptimizationServerResult c in candidates)
        {
            if (c.Status != "completed" || c.DecodeTokensPerSecond is not > 0) continue;
            if (c.DecodeTokensPerSecond < baseDecode * RuntimeOptimizationService.MinimumSpeculativeGain) continue;
            if ((c.TimeToFirstTokenMilliseconds ?? double.MaxValue) > baseTtft * 1.15) continue;
            if (c.ThermalRegressionSuspected && !baseline.ThermalRegressionSuspected) continue;
            if (best is null || c.DecodeTokensPerSecond > best.DecodeTokensPerSecond) best = c;
        }
        return best ?? baseline;
    }

    private static LlamaPerformanceProfileDto With(LlamaPerformanceProfileDto baseline, string idSuffix, string nameSuffix, Action<LlamaPerformanceProfileDto> change)
    {
        LlamaPerformanceProfileDto c = RuntimePerformanceProfileCopy.Copy(baseline);
        c.Id += idSuffix;
        c.DisplayName += nameSuffix;
        change(c);
        return c;
    }
}
