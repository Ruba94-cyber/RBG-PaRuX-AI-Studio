using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Search-space guidance for NVIDIA Blackwell/GeForce RTX 50-series hardware.
///
/// This is deliberately NOT a table of magic production settings. PaRuX only uses this envelope to
/// decide which candidates are worth benchmarking first; the measured runtime tuner remains the
/// sole authority allowed to persist/promote a launch profile. Actual VRAM is always taken from the
/// host inventory/telemetry, so desktop/laptop/OEM variants and unusual board configurations remain
/// safe. Non-RTX-50 hardware receives no special envelope and continues through the generic path.
/// </summary>
public static class Rtx50SeriesOptimization
{
    public const uint NvidiaVendorId = 0x10DE;
    public const double BlackwellComputeCapability = 12.0;

    public sealed record Envelope(
        string Family,
        int AdapterIndex,
        long DedicatedVramBytes,
        int BenchmarkBatchSize,
        int BenchmarkUbatchSize,
        int FitTargetMib,
        IReadOnlyList<int> ParallelCandidates,
        IReadOnlyList<string> KvCacheCandidates,
        bool IncludeBackendSampling,
        bool IncludeMtpWhenModelSupportsIt);

    public static Envelope? Detect(GpuInventoryStatus? inventory)
    {
        if (inventory == null || !inventory.Available) return null;
        GpuAdapterStatus? adapter = inventory.Adapters
            .Where(item => !item.IsSoftware
                && item.VendorId == NvidiaVendorId
                && IsRtx50Name(item.Name))
            .OrderByDescending(item => item.DedicatedVideoMemoryBytes)
            .ThenBy(item => item.Index)
            .FirstOrDefault();
        return adapter == null ? null : Build(adapter.Index, adapter.Name, adapter.DedicatedVideoMemoryBytes);
    }

    /// <summary>Runtime optimization already owns NVIDIA telemetry, so it can use the same envelope
    /// without doing a second DXGI inventory. Name and compute capability corroborate one another;
    /// neither is used to infer VRAM, which always comes from the measured device.</summary>
    public static Envelope? Detect(IReadOnlyList<GpuPerformanceTelemetryDto>? telemetry)
    {
        if (telemetry == null) return null;
        GpuPerformanceTelemetryDto? adapter = telemetry
            .Where(item => IsRtx50Name(item.Name)
                && (!item.ComputeCapability.HasValue
                    || Math.Abs(item.ComputeCapability.Value - BlackwellComputeCapability) < 0.01))
            .OrderByDescending(item => item.MemoryTotalMib ?? 0)
            .ThenBy(item => item.Index)
            .FirstOrDefault();
        if (adapter == null) return null;
        long vram = Math.Max(0, (long)(adapter.MemoryTotalMib ?? 0) * 1024L * 1024L);
        return Build(adapter.Index, adapter.Name, vram);
    }

    internal static bool IsRtx50Name(string? name)
    {
        string normalized = (name ?? string.Empty).ToUpperInvariant()
            .Replace("GEFORCE", string.Empty, StringComparison.Ordinal)
            .Replace("NVIDIA", string.Empty, StringComparison.Ordinal)
            .Replace("LAPTOP GPU", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (!normalized.Contains("RTX", StringComparison.Ordinal)) return false;
        return normalized.Contains("5090", StringComparison.Ordinal)
            || normalized.Contains("5080", StringComparison.Ordinal)
            || normalized.Contains("5070 TI", StringComparison.Ordinal)
            || normalized.Contains("5070TI", StringComparison.Ordinal)
            || normalized.Contains("5070", StringComparison.Ordinal)
            || normalized.Contains("5060 TI", StringComparison.Ordinal)
            || normalized.Contains("5060TI", StringComparison.Ordinal)
            || normalized.Contains("5060", StringComparison.Ordinal)
            || normalized.Contains("5050", StringComparison.Ordinal);
    }

    private static Envelope Build(int index, string? name, long dedicatedVramBytes)
    {
        const long GiB = 1024L * 1024L * 1024L;
        long vram = Math.Max(0, dedicatedVramBytes);
        string family = ClassifyFamily(name);

        // Wider candidates are intentionally attempted only as benchmark candidates. The generic
        // baseline is always retained and can win if a specific driver/runtime/model combination
        // does not benefit from Blackwell's extra memory bandwidth or parallelism.
        if (vram >= 28 * GiB)
        {
            return new Envelope(
                family, index, vram,
                BenchmarkBatchSize: 4096,
                BenchmarkUbatchSize: 1024,
                FitTargetMib: 1536,
                ParallelCandidates: new[] { 1, 2, 4, 8 },
                KvCacheCandidates: new[] { "q8_0", "q5_0", "q4_0", "f16" },
                IncludeBackendSampling: true,
                IncludeMtpWhenModelSupportsIt: true);
        }
        if (vram >= 15 * GiB)
        {
            return new Envelope(
                family, index, vram,
                BenchmarkBatchSize: 3072,
                BenchmarkUbatchSize: 768,
                FitTargetMib: 1024,
                ParallelCandidates: new[] { 1, 2, 4, 8 },
                KvCacheCandidates: new[] { "q8_0", "q5_0", "q4_0", "f16" },
                IncludeBackendSampling: true,
                IncludeMtpWhenModelSupportsIt: true);
        }
        if (vram >= 11 * GiB)
        {
            return new Envelope(
                family, index, vram,
                BenchmarkBatchSize: 2048,
                BenchmarkUbatchSize: 512,
                FitTargetMib: 768,
                ParallelCandidates: new[] { 1, 2, 4 },
                KvCacheCandidates: new[] { "q8_0", "q5_0", "q4_0" },
                IncludeBackendSampling: true,
                IncludeMtpWhenModelSupportsIt: true);
        }

        return new Envelope(
            family, index, vram,
            BenchmarkBatchSize: 1536,
            BenchmarkUbatchSize: 384,
            FitTargetMib: 640,
            ParallelCandidates: new[] { 1, 2, 4 },
            KvCacheCandidates: new[] { "q8_0", "q5_0", "q4_0" },
            IncludeBackendSampling: true,
            IncludeMtpWhenModelSupportsIt: true);
    }

    private static string ClassifyFamily(string? name)
    {
        string value = (name ?? string.Empty).ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (value.Contains("5090", StringComparison.Ordinal)) return "rtx-5090";
        if (value.Contains("5080", StringComparison.Ordinal)) return "rtx-5080";
        if (value.Contains("5070TI", StringComparison.Ordinal)) return "rtx-5070-ti";
        if (value.Contains("5070", StringComparison.Ordinal)) return "rtx-5070";
        if (value.Contains("5060TI", StringComparison.Ordinal)) return "rtx-5060-ti";
        if (value.Contains("5060", StringComparison.Ordinal)) return "rtx-5060";
        if (value.Contains("5050", StringComparison.Ordinal)) return "rtx-5050";
        return "rtx-50-series";
    }
}
