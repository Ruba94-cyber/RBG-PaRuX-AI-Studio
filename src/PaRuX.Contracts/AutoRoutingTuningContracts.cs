namespace PaRuX.Contracts;

/// <summary>
/// User-owned tuning for the Custom AUTO intelligence profile. Values are intentionally generic
/// routing signals rather than model-family-specific magic numbers, and are bounded by Core before
/// persistence. The four scoring weights are normalized at ranking time.
/// </summary>
public sealed class AutoRoutingTuningDto
{
    public double MinimumCapabilityRatio { get; set; } = 0.68;
    public double ComplexityFloorBoost { get; set; } = 0.24;
    public double BaseCapabilityWeight { get; set; } = 0.46;
    public double BasePerformanceWeight { get; set; } = 0.31;
    public double CompatibilityWeight { get; set; } = 0.16;
    public double LoadedReuseWeight { get; set; } = 0.07;
    public double ComplexityCapabilityBoost { get; set; } = 0.26;
    public double ComplexityPerformancePenalty { get; set; } = 0.18;
}

public sealed class SetAutoRoutingTuningRequest
{
    public double MinimumCapabilityRatio { get; set; } = 0.68;
    public double ComplexityFloorBoost { get; set; } = 0.24;
    public double BaseCapabilityWeight { get; set; } = 0.46;
    public double BasePerformanceWeight { get; set; } = 0.31;
    public double CompatibilityWeight { get; set; } = 0.16;
    public double LoadedReuseWeight { get; set; } = 0.07;
    public double ComplexityCapabilityBoost { get; set; } = 0.26;
    public double ComplexityPerformancePenalty { get; set; } = 0.18;
}
