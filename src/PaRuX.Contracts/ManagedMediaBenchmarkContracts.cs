namespace PaRuX.Contracts;

/// <summary>
/// Explicit cold-versus-resident benchmark for the PaRuX-managed Diffusers engine. This is a
/// measurement surface, not AUTO routing: the same normalized prompt/seed/dimensions/steps are
/// reused so the warm numbers cannot quietly describe a different challenge.
/// </summary>
public sealed class ManagedDiffusersWarmBenchmarkRequest
{
    public MediaJobRequest Job { get; set; } = new() { Type = "image" };
    public int WarmRuns { get; set; } = 3;
    public bool IncludeFinalArtifact { get; set; }
}

public sealed class ManagedDiffusersWarmBenchmarkResult
{
    public string EngineId { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelRevision { get; set; } = string.Empty;
    public string Mode { get; set; } = "cold-plus-resident-warm";
    public long Seed { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Steps { get; set; }
    public long ColdTotalMilliseconds { get; set; }
    public long ResidentLoadMilliseconds { get; set; }
    public long ResidentLoadAllocatedBytes { get; set; }
    public long ResidentLoadReservedBytes { get; set; }
    public List<ManagedMediaWarmRunMeasurement> WarmRuns { get; set; } = new();
    public MediaAsset? FinalArtifact { get; set; }
}

public sealed class ManagedMediaWarmRunMeasurement
{
    public int Run { get; set; }
    public long TotalMilliseconds { get; set; }
    public long WorkerGenerationMilliseconds { get; set; }
    public long PeakAllocatedBytes { get; set; }
    public long PeakReservedBytes { get; set; }
}
