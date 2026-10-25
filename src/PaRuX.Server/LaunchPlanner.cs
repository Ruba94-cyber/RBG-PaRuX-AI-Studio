using System.Globalization;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

public enum LaunchModelClass { Unknown, Dense, MixtureOfExperts }

/// <summary>One launch choice and the evidence behind it. <see cref="Flag"/> is the llama-server flag (or the
/// profile knob when it maps to several flags); <see cref="Value"/> is empty for a pure note.</summary>
public sealed record LaunchPlanDecision(string Flag, string Value, string Reason);

/// <summary>
/// The memory and CPU facts a launch is planned against, already reduced to plain numbers so the planner is a pure
/// function. <see cref="GpuMemoryBytes"/> is the FREE dedicated VRAM for a discrete adapter, or the bounded
/// shared-memory aperture for an integrated one (the same physical RAM as <see cref="SystemRamAvailableBytes"/>,
/// never added to it a second time).
/// </summary>
public sealed record LaunchHardware(
    bool AcceleratorEligible,
    string Backend,
    bool SharedMemoryPrimary,
    long GpuMemoryBytes,
    long SystemRamTotalBytes,
    long SystemRamAvailableBytes,
    int PhysicalCores,
    int LogicalCores)
{
    /// <summary>
    /// Share of a discrete adapter's dedicated memory the planner assumes is free when no live free-memory
    /// sample exists (telemetry failed, timed out or could not be matched to this adapter). Unknown is not
    /// "all free": another process, a game that just exited or a second model may still hold the rest.
    /// </summary>
    public const double UnknownFreeVramPlanningFraction = 0.5;

    /// <summary>
    /// Builds the planner view from the unified hardware profile (the same one ContextMemoryBudget uses).
    /// <paramref name="freeDedicatedBytes"/> is the live FREE dedicated memory of a discrete adapter; a measured
    /// 0 plans against 0, and <c>null</c> (unknown) plans conservatively against
    /// <see cref="UnknownFreeVramPlanningFraction"/> of the dedicated capacity.
    /// </summary>
    public static LaunchHardware From(
        RuntimeHardwareProfile profile, long? freeDedicatedBytes, long ramAvailableBytes, int physicalCores, int logicalCores)
    {
        long gpu = 0;
        if (profile.AcceleratorEligible)
        {
            long dedicated = profile.DedicatedVideoMemoryBytes;
            gpu = profile.UsesSharedSystemMemoryAsPrimary
                ? Math.Min(profile.UsableSystemRamBytes, profile.SharedSystemMemoryLimitBytes)
                : freeDedicatedBytes is long free
                    ? Math.Min(free, dedicated > 0 ? dedicated : long.MaxValue)
                    : (long)(dedicated * UnknownFreeVramPlanningFraction);
        }
        return new LaunchHardware(
            profile.AcceleratorEligible, profile.Backend, profile.UsesSharedSystemMemoryAsPrimary,
            Math.Max(0, gpu), profile.SystemRamBytes, Math.Max(0, ramAvailableBytes),
            Math.Max(0, physicalCores), Math.Max(0, logicalCores));
    }
}

public sealed record LaunchPlanInput(
    GgufModelInfo? Model,
    LaunchHardware Hardware,
    LlamaServerCapabilities Capabilities,
    LlamaPerformanceProfileDto Baseline);

/// <summary>The result of planning one launch. The planned knobs live on <see cref="Profile"/>; the arguments
/// are produced by the one existing builder, so a plan cannot emit a flag the builder does not know.</summary>
public sealed class LaunchPlan
{
    public bool Applied { get; init; }
    public string NotAppliedReason { get; init; } = string.Empty;
    public LaunchModelClass ModelClass { get; init; }
    /// <summary>False when even the best candidate was estimated not to fit; the launch is still attempted and the
    /// health-checked ladder decides, but the plan never reports it as supported.</summary>
    public bool Fits { get; init; }
    public LlamaPerformanceProfileDto Profile { get; init; } = new();
    public IReadOnlyList<LaunchPlanDecision> Decisions { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Every figure in a plan is a memory-fit estimate. Speed is not claimed until the local benchmark has measured it.</summary>
    public const string PerformanceClaim =
        "unmeasured: the plan is a memory-fit estimate from the GGUF header and current free memory; no speed is claimed until the local benchmark measures it";

    public string Summary => !Applied
        ? "launch plan not applied: " + NotAppliedReason
        : (ModelClass == LaunchModelClass.MixtureOfExperts ? "MoE" : "dense") + " plan, "
            + (Fits ? "estimated to fit" : "estimated NOT to fit") + ", " + Decisions.Count + " decisions";

    public string Explain()
    {
        var text = new StringBuilder();
        text.AppendLine(Summary);
        foreach (LaunchPlanDecision decision in Decisions)
            text.AppendLine("  " + (decision.Value.Length == 0 ? decision.Flag : decision.Flag + " " + decision.Value) + " - " + decision.Reason);
        foreach (string warning in Warnings) text.AppendLine("  WARNING: " + warning);
        text.AppendLine("  " + PerformanceClaim);
        return text.ToString().TrimEnd();
    }

    internal static LaunchPlan NotApplied(string reason, LlamaPerformanceProfileDto baseline, LaunchModelClass modelClass = LaunchModelClass.Unknown) =>
        new() { Applied = false, NotAppliedReason = reason, ModelClass = modelClass, Profile = baseline };
}

/// <summary>
/// Chooses llama.cpp launch knobs from the model's own GGUF header and the machine's current memory, so the
/// default launch is the one that fits this hardware instead of one tuned for somebody else's.
///
/// Pure and deterministic: no probing, clock or randomness; the same inputs always give the same plan and the
/// same explanation. It never raises a quality knob and never lowers one that the default launch fits with:
/// KV-cache quantization and a smaller context are tried only when the less lossy launch does not fit.
/// Optional flags are emitted only when <see cref="LlamaServerCapabilities"/> lists them for the runtime build.
/// </summary>
public static class LaunchPlanner
{
    private const long MiB = 1024L * 1024L;
    private const long GiB = 1024L * MiB;
    private const long ComputeBufferReserveBytes = 512L * MiB;
    private const int ContextFloor = 16384;

    public static LaunchPlan Plan(LaunchPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        LlamaPerformanceProfileDto baseline = input.Baseline;
        GgufModelInfo? model = input.Model;
        LaunchHardware hw = input.Hardware;

        if (model is not { ParsedSuccessfully: true })
            return LaunchPlan.NotApplied("GGUF metadata could not be read, so the launch keeps its default arguments", baseline);
        long blocks = model.BlockCount ?? 0;
        if (blocks <= 0 || model.SizeBytes <= 0)
            return LaunchPlan.NotApplied("GGUF header does not declare the layer count or the file size is unknown", baseline);

        bool moe = model.IsMixtureOfExperts;
        LaunchModelClass modelClass = moe ? LaunchModelClass.MixtureOfExperts : LaunchModelClass.Dense;
        long weights = model.SizeBytes;

        var decisions = new List<LaunchPlanDecision>();
        var warnings = new List<string>();
        LlamaPerformanceProfileDto profile = RuntimePerformanceProfileCopy.Copy(baseline);
        profile.ContextIsPerRequest = baseline.ContextIsPerRequest;

        long? expertBytes = null;
        string expertBasis = string.Empty;
        if (moe)
        {
            (expertBytes, expertBasis) = ResolveExpertBytes(model);
            decisions.Add(new("model-class", "moe", "GGUF declares " + model.ExpertCount + " routed experts"
                + (model.ExpertUsedCount is > 0 ? " (" + model.ExpertUsedCount + " active per token)" : string.Empty)
                + (expertBytes is { } e ? "; expert tensors " + Gib(e) + " of " + Gib(weights) + " (" + expertBasis + ")" : "; expert size unknown, so experts are not split from the rest")));
        }
        else
        {
            decisions.Add(new("model-class", "dense", "GGUF declares no routed experts"));
        }

        int slots = baseline.ParallelSlots;
        bool pinned = baseline.ContextIsPerRequest;
        string cache0 = NormalizeCache(baseline.CacheTypeK);
        bool cachesMatch = string.Equals(NormalizeCache(baseline.CacheTypeV), cache0, StringComparison.Ordinal);
        bool flashAllowed = !string.Equals(baseline.FlashAttention, "off", StringComparison.OrdinalIgnoreCase);
        bool quantAllowed = !pinned && cachesMatch && flashAllowed && hw.AcceleratorEligible
            && string.Equals(hw.Backend, "text-cuda", StringComparison.OrdinalIgnoreCase)
            && QuantizedKvGeometrySupported(model);

        List<(string Cache, int Context)> candidates = BuildCandidates(baseline, cache0, quantAllowed, pinned, slots);

        long reserve = (Math.Max(1024, baseline.FitTargetMib) * MiB) + ComputeBufferReserveBytes;
        long gpuBudget = hw.AcceleratorEligible ? Math.Max(0, hw.GpuMemoryBytes - reserve) : 0;
        long hostBudget = Math.Max(0, Math.Min(
            RuntimeHardwareProfileResolver.ConservativeUsableSystemRamBytes(hw.SystemRamTotalBytes), hw.SystemRamAvailableBytes));
        bool nCpuMoePossible = moe && expertBytes is > 0 && !hw.SharedMemoryPrimary && hw.AcceleratorEligible
            && (input.Capabilities.NCpuMoe || input.Capabilities.OverrideTensor);

        var evaluated = new List<(string Cache, int Context, Placement Place)>();
        foreach ((string cache, int context) in candidates)
        {
            long? kv = KvCacheSizeEstimator.Estimate(model, RuntimeLaunchOptimizer.TotalContextFor(context, slots, pinned), cache);
            if (kv == null)
                return LaunchPlan.NotApplied("GGUF header does not declare the attention geometry needed to size the KV cache", baseline, modelClass);
            evaluated.Add((cache, context, Place(model, hw, baseline.DisableKvOffload, kv.Value, blocks, gpuBudget, hostBudget,
                nCpuMoePossible ? expertBytes : null)));
        }

        // 1. The least lossy candidate that needs no offload at all. 2. Otherwise the first candidate whose offloaded
        // placement fits memory. 3. Otherwise the smallest one, reported honestly as not fitting.
        int chosenIndex = evaluated.FindIndex(c => c.Place.Fits && c.Place.GpuLayers < 0 && c.Place.CpuMoeLayers == 0);
        if (chosenIndex < 0) chosenIndex = evaluated.FindIndex(c => c.Place.Fits);
        bool fits = chosenIndex >= 0;
        if (!fits) chosenIndex = evaluated.Count - 1;
        (string chosenCache, int chosenContext, Placement placement) = evaluated[chosenIndex];

        // ---- context and KV cache ----
        profile.ContextSize = chosenContext;
        if (chosenContext != baseline.ContextSize)
            decisions.Add(new("--ctx-size", RuntimeLaunchOptimizer.TotalContextFor(chosenContext, slots, pinned).ToString(CultureInfo.InvariantCulture),
                "context lowered from " + baseline.ContextSize + " to " + chosenContext + " because the larger window does not fit the free memory (estimate); a user-chosen window is never lowered"));
        else
            decisions.Add(new("--ctx-size", RuntimeLaunchOptimizer.TotalContextFor(chosenContext, slots, pinned).ToString(CultureInfo.InvariantCulture),
                pinned ? "kept: explicit user context preference" : "kept: the requested window fits"));

        profile.CacheTypeK = chosenCache;
        profile.CacheTypeV = cachesMatch ? chosenCache : NormalizeCache(baseline.CacheTypeV);
        if (!string.Equals(chosenCache, cache0, StringComparison.Ordinal))
        {
            if (string.Equals(profile.FlashAttention, "auto", StringComparison.OrdinalIgnoreCase)) profile.FlashAttention = "on";
            decisions.Add(new("--cache-type-k/--cache-type-v", chosenCache,
                "KV cache quantized from " + cache0 + " because " + cache0 + " does not fit the free memory (estimate); quantized V cache needs flash attention, which stays on; slight quality cost"));
        }
        else
        {
            decisions.Add(new("--cache-type-k/--cache-type-v", chosenCache,
                pinned ? "kept: explicit user KV cache preference"
                    : quantAllowed || evaluated.Count == 1 ? "kept: no quality reduction is needed to fit"
                    : "kept: KV quantization is only planned on CUDA with flash attention and a verified head size"));
        }

        // ---- placement ----
        if (!hw.AcceleratorEligible)
        {
            profile.GpuLayers = 0;
            decisions.Add(new("--gpu-layers", "0", "no usable accelerator backend is installed or detected; all layers run on the CPU"));
        }
        else if (placement.GpuLayers < 0 && placement.CpuMoeLayers == 0)
        {
            profile.GpuLayers = -1;
            decisions.Add(new("--gpu-layers", "all", hw.SharedMemoryPrimary
                ? "weights and KV fit the shared-memory aperture (" + Gib(gpuBudget) + " after reserve); the same system RAM is counted once"
                : "weights and KV fit the free VRAM (" + Gib(gpuBudget) + " after reserve)"));
        }
        else if (placement.CpuMoeLayers > 0)
        {
            profile.GpuLayers = placement.GpuLayers;
            string layers = placement.GpuLayers < 0 ? "all" : placement.GpuLayers.ToString(CultureInfo.InvariantCulture);
            decisions.Add(new("--gpu-layers", layers, "attention, shared and non-expert tensors stay on the GPU"
                + (placement.GpuLayers >= 0 ? "; only " + placement.GpuLayers + " of " + blocks + " layers fit even without experts" : string.Empty)));
            if (input.Capabilities.NCpuMoe)
            {
                profile.CpuMoeLayers = placement.CpuMoeLayers;
                decisions.Add(new("--n-cpu-moe", placement.CpuMoeLayers.ToString(CultureInfo.InvariantCulture),
                    "routed experts of the first " + placement.CpuMoeLayers + " of " + blocks + " layers stay in system memory so the rest fit VRAM (placement is an estimate; expert size "
                    + expertBasis + "); flag verified for this runtime (" + input.Capabilities.Source + ")"));
            }
            else
            {
                profile.OverrideTensor = ExpertOverridePattern(placement.CpuMoeLayers, blocks);
                decisions.Add(new("--override-tensor", profile.OverrideTensor,
                    "--n-cpu-moe is not verified for this runtime, so expert tensors of the first " + placement.CpuMoeLayers
                    + " layers are pinned to CPU with --override-tensor (verified present: " + input.Capabilities.Source + "); pattern semantics are not runtime-verified"));
            }
        }
        else
        {
            profile.GpuLayers = placement.GpuLayers;
            string reason = hw.SharedMemoryPrimary
                ? "model does not fit the shared-memory aperture; remaining layers run on the CPU from the same RAM"
                : "model does not fit the free VRAM; the rest of the layers run on the CPU";
            if (moe && expertBytes is > 0 && !nCpuMoePossible && !hw.SharedMemoryPrimary)
                reason += "; expert offload flags are not verified for this runtime build, so whole layers are offloaded instead";
            else if (moe && hw.SharedMemoryPrimary)
                reason += "; expert offload gives no benefit when the accelerator and CPU share one memory";
            decisions.Add(new("--gpu-layers", placement.GpuLayers.ToString(CultureInfo.InvariantCulture),
                reason + " (" + placement.GpuLayers + " of " + blocks + " layers, estimate)"));
        }

        if (!fits)
            warnings.Add("estimated host memory need " + Gib(placement.HostBytes) + " exceeds the safe budget " + Gib(hostBudget)
                + (hw.SharedMemoryPrimary ? " (shared pool counted once)" : string.Empty)
                + "; the launch may page heavily or fail, and PaRuX does not report this model as supported on this machine");

        // ---- CPU threads: only when CPU compute is on the critical path ----
        bool cpuInvolved = !hw.AcceleratorEligible || placement.HostBytes > 0 && (placement.GpuLayers >= 0 || placement.CpuMoeLayers > 0);
        if (cpuInvolved && hw.PhysicalCores > 0)
        {
            int physical = hw.PhysicalCores;
            int headroom = physical >= 8 ? 2 : physical >= 4 ? 1 : 0;
            profile.Threads = Math.Max(1, physical - headroom);
            profile.ThreadsBatch = physical;
            // Strict placement pins thread i to logical CPU i, and Windows numbers SMT siblings consecutively,
            // so N threads would land on N/2 physical cores. Leave placement to the scheduler.
            profile.CpuStrict = false;
            decisions.Add(new("--threads", profile.Threads.ToString(CultureInfo.InvariantCulture),
                "physical cores (" + physical + ", not " + hw.LogicalCores + " logical) minus " + headroom + " kept free for the UI and the server"));
            decisions.Add(new("--threads-batch", profile.ThreadsBatch.ToString(CultureInfo.InvariantCulture),
                "all physical cores for prompt processing; strict CPU pinning is off because it would map threads onto SMT siblings (unmeasured)"));
        }

        // ---- load mode: mlock only with proven RAM headroom ----
        PlanLoadMode(profile, baseline, hw, placement, input.Capabilities, decisions);

        // ---- batch sizes by remaining accelerator memory ----
        if (hw.AcceleratorEligible)
        {
            long headroom = gpuBudget - placement.GpuBytes;
            if (headroom < GiB && (baseline.BatchSize > 1024 || baseline.UBatchSize > 256))
            {
                profile.BatchSize = Math.Min(baseline.BatchSize, 1024);
                profile.UBatchSize = Math.Min(baseline.UBatchSize, 256);
                decisions.Add(new("--batch-size/--ubatch-size", profile.BatchSize + "/" + profile.UBatchSize,
                    "less than 1 GiB of accelerator memory remains after the plan, so the compute buffers are kept small"));
            }
            else
            {
                decisions.Add(new("--batch-size/--ubatch-size", baseline.BatchSize + "/" + baseline.UBatchSize,
                    headroom < GiB
                        ? "kept: already small enough for the little accelerator memory that remains (" + Gib(Math.Max(0, headroom)) + ", estimate)"
                        : "kept: enough accelerator memory remains for the default compute buffers (" + Gib(headroom) + " free, estimate)"));
            }
        }

        return new LaunchPlan
        {
            Applied = true,
            ModelClass = modelClass,
            Fits = fits,
            Profile = profile,
            Decisions = decisions,
            Warnings = warnings
        };
    }

    // ---------------------------------------------------------------- placement

    private sealed record Placement(bool Fits, int GpuLayers, int CpuMoeLayers, long GpuBytes, long HostBytes);

    private static Placement Place(
        GgufModelInfo model, LaunchHardware hw, bool kvOnHost, long kv, long blocks,
        long gpuBudget, long hostBudget, long? expertBytes)
    {
        long weights = model.SizeBytes;
        long gpuKv = kvOnHost ? 0 : kv;

        if (!hw.AcceleratorEligible)
            return new Placement(weights + kv <= hostBudget, 0, 0, 0, weights + kv);

        // Shared-memory accelerators draw on the same RAM, so the WHOLE model must fit the host budget.
        long hostCap = hw.SharedMemoryPrimary ? hostBudget : long.MaxValue;

        if (weights + gpuKv <= gpuBudget)
        {
            long host = kvOnHost ? kv : 0;
            long total = weights + kv;
            return new Placement(host <= hostBudget && (!hw.SharedMemoryPrimary || total <= hostCap), -1, 0, weights + gpuKv, host);
        }

        if (expertBytes is > 0)
        {
            long experts = Math.Min(expertBytes.Value, weights);
            long nonExpert = weights - experts;
            long perLayerExperts = Math.Max(1, experts / blocks);
            if (nonExpert + gpuKv <= gpuBudget)
            {
                long deficit = weights + gpuKv - gpuBudget;
                int n = (int)Math.Clamp((deficit + perLayerExperts - 1) / perLayerExperts, 1, blocks);
                long hostBytes = n * perLayerExperts + (kvOnHost ? kv : 0);
                return new Placement(hostBytes <= hostBudget, -1, n, weights + gpuKv - n * perLayerExperts, hostBytes);
            }

            long perLayer = Math.Max(1, (nonExpert + gpuKv) / blocks);
            int gpuLayers = (int)Math.Clamp(gpuBudget / perLayer, 0, blocks - 1);
            long gpuBytes = gpuLayers * perLayer;
            long host2 = experts + (nonExpert + gpuKv - gpuBytes) + (kvOnHost ? kv : 0);
            return new Placement(host2 <= hostBudget, gpuLayers, (int)blocks, gpuBytes, host2);
        }

        long perLayerAll = Math.Max(1, (weights + gpuKv) / blocks);
        int layers = (int)Math.Clamp(gpuBudget / perLayerAll, 0, blocks - 1);
        long gpuPart = layers * perLayerAll;
        long hostPart = weights + kv - gpuPart;
        bool fitsHost = hw.SharedMemoryPrimary ? weights + kv <= hostCap : hostPart <= hostBudget;
        return new Placement(fitsHost, layers, 0, gpuPart, hostPart);
    }

    // ---------------------------------------------------------------- context/cache candidates

    private static List<(string Cache, int Context)> BuildCandidates(
        LlamaPerformanceProfileDto baseline, string cache0, bool quantAllowed, bool pinned, int slots)
    {
        var ladder = new List<string> { cache0 };
        if (quantAllowed)
        {
            foreach (string lower in new[] { "q8_0", "q4_0" })
                if (CacheRank(lower) < CacheRank(ladder[^1])) ladder.Add(lower);
        }

        var reduced = new List<int>();
        if (!pinned)
        {
            int floor = Math.Max(ContextFloor, RuntimeLaunchOptimizer.MinimumTotalContextFor(slots));
            int context = baseline.ContextSize;
            for (int i = 0; i < 4; i++)
            {
                context = context / 2 / 1024 * 1024;
                if (context < floor) break;
                reduced.Add(context);
            }
        }

        // Quality order: default cache at full context, q8_0 at full context, q8_0 with a smaller window,
        // then q4_0 at full and reduced windows. A smaller window is tried before the lossiest cache.
        var result = new List<(string, int)>();
        for (int i = 0; i < ladder.Count; i++)
        {
            result.Add((ladder[i], baseline.ContextSize));
            bool lastBeforeLossy = i == ladder.Count - 1 || CacheRank(ladder[i + 1]) < 1.5;
            if (lastBeforeLossy) foreach (int context in reduced) result.Add((ladder[i], context));
        }
        return result;
    }

    private static string NormalizeCache(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "f16" => "f16", "bf16" => "bf16", "q8_0" => "q8_0", "q5_0" => "q5_0", "q5_1" => "q5_1",
        "q4_0" => "q4_0", "q4_1" => "q4_1", "iq4_nl" => "iq4_nl", _ => "q8_0"
    };

    /// <summary>Higher is closer to lossless. Only used to order the ladder.</summary>
    private static double CacheRank(string cache) => cache switch
    {
        "f16" or "bf16" => 3,
        "q8_0" => 2,
        "q5_0" or "q5_1" => 1.5,
        _ => 1
    };

    /// <summary>Quantized K/V cache goes through flash-attention kernels that exist for the common head sizes only.</summary>
    private static bool QuantizedKvGeometrySupported(GgufModelInfo model)
    {
        long key = model.KeyLength is > 0 ? model.KeyLength.Value
            : model.EmbeddingLength is > 0 && model.AttentionHeadCount is > 0 ? model.EmbeddingLength.Value / model.AttentionHeadCount.Value : 0;
        long value = model.ValueLength is > 0 ? model.ValueLength.Value : key;
        return key == value && key is 64 or 80 or 96 or 112 or 128 or 256;
    }

    // ---------------------------------------------------------------- experts

    private static (long? Bytes, string Basis) ResolveExpertBytes(GgufModelInfo model)
    {
        if (model.ExpertTensorBytes is > 0 && model.ExpertTensorBytes < model.SizeBytes)
            return (model.ExpertTensorBytes, "measured from the GGUF tensor directory");

        if (model is { BlockCount: > 0, EmbeddingLength: > 0, ExpertFeedForwardLength: > 0, ExpertCount: > 1, ParameterCount: > 0 })
        {
            double expertParams = (double)model.BlockCount.Value * 3 * model.EmbeddingLength.Value
                * model.ExpertFeedForwardLength.Value * model.ExpertCount.Value;
            double fraction = Math.Min(0.95, expertParams / model.ParameterCount.Value);
            long bytes = (long)(model.SizeBytes * fraction);
            if (bytes > 0) return (bytes, "estimated from expert parameter share");
        }
        return (null, "unknown");
    }

    /// <summary>Regex list for --override-tensor pinning routed-expert tensors of layers 0..count-1 to the CPU buffer.</summary>
    internal static string ExpertOverridePattern(int layers, long blocks)
    {
        if (layers >= blocks) return @"ffn_.*_exps\.=CPU";
        var numbers = string.Join('|', Enumerable.Range(0, Math.Max(1, layers)).Select(i => i.ToString(CultureInfo.InvariantCulture)));
        return @"blk\.(" + numbers + @")\.ffn_.*_exps\.=CPU";
    }

    // ---------------------------------------------------------------- load mode

    private static void PlanLoadMode(
        LlamaPerformanceProfileDto profile, LlamaPerformanceProfileDto baseline, LaunchHardware hw, Placement placement,
        LlamaServerCapabilities caps, List<LaunchPlanDecision> decisions)
    {
        if (!caps.LoadMode) return;

        bool hostResident = placement.HostBytes > 0 || !hw.AcceleratorEligible;
        if (!hostResident)
        {
            // Measured on the RTX 5080 reference (.ai/artifacts/llama-load-mode-20260929): a fully offloaded model
            // with the default mmap kept its file pages resident in RAM for nothing; "none" freed them.
            if (!hw.SharedMemoryPrimary
                && string.Equals(hw.Backend, "text-cuda", StringComparison.OrdinalIgnoreCase)
                && string.Equals(baseline.LoadMode, "auto", StringComparison.OrdinalIgnoreCase))
            {
                profile.LoadMode = "none";
                decisions.Add(new("--load-mode", "none", "everything lives in VRAM, so no file pages need to stay resident in RAM (measured on the reference RTX 5080 machine, 2026-09-29)"));
            }
            return;
        }

        if (hw.SharedMemoryPrimary)
        {
            decisions.Add(new("--load-mode", baseline.LoadMode, "kept: the accelerator and CPU share one memory, so pinning pages would only shrink what the system can use"));
            return;
        }

        long afterResident = hw.SystemRamAvailableBytes - placement.HostBytes;
        long required = Math.Max(4 * GiB, hw.SystemRamTotalBytes / 4);
        if (afterResident >= required && string.Equals(baseline.LoadMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            profile.LoadMode = "mmap+mlock";
            decisions.Add(new("--load-mode", "mmap+mlock",
                "CPU-resident weights (" + Gib(placement.HostBytes) + ") leave " + Gib(afterResident) + " of RAM free, above the " + Gib(required)
                + " floor, so keeping them from being paged out is safe (unmeasured)"));
        }
        else
        {
            decisions.Add(new("--load-mode", baseline.LoadMode,
                "kept: RAM headroom after the CPU-resident weights (" + Gib(Math.Max(0, afterResident)) + ") is not proven above the " + Gib(required) + " floor, so memory is not pinned"));
        }
    }

    private static string Gib(long bytes) => (bytes / (double)GiB).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";
}
