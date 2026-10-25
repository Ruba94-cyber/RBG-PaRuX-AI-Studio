namespace PaRuX.Contracts;

// ---- Observability: source-backed operational evidence. Timeline remains a merged read-only view
// ---- over records owned by the real subsystems; Dashboard adds a compact cross-surface snapshot
// ---- and benchmark evidence without introducing cloud telemetry. ----

public sealed class TimelineEntry
{
    /// <summary>Which existing source/store this entry came from.</summary>
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Detail { get; set; }
    /// <summary>The underlying record's own id, so a client can jump to that subsystem's own
    /// detail view - Observability itself has no detail view of its own to duplicate one.</summary>
    public string RelatedId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

public sealed class TimelineResponse
{
    public List<TimelineEntry> Entries { get; set; } = new();
}

public sealed class ObservabilityStatusCount
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class ObservabilityMetric
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public sealed class ModelBenchmarkLeaderboardEntry
{
    public string ModelPath { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Backend { get; set; } = string.Empty;
    public string HardwareKey { get; set; } = string.Empty;
    public double TokensPerSecond { get; set; }
    public int Repetitions { get; set; }
    public DateTime MeasuredUtc { get; set; }
}

/// <summary>
/// Cross-surface operational snapshot. Every value is computed from an existing authoritative
/// store/service at request time; there is no sampling agent and no analytics upload.
/// </summary>
public sealed class ObservabilityDashboardResponse
{
    public DateTime CapturedUtc { get; set; }
    public ExecutionProfileStatus ExecutionProfile { get; set; } = new();
    public List<ObservabilityMetric> Metrics { get; set; } = new();
    public List<ObservabilityStatusCount> TaskStatuses { get; set; } = new();
    public List<ObservabilityStatusCount> TrainingStatuses { get; set; } = new();
    public List<ModelBenchmarkLeaderboardEntry> ModelBenchmarks { get; set; } = new();
    public List<EngineRaceEvidenceDto> EngineRaces { get; set; } = new();
    public List<TimelineEntry> RecentActivity { get; set; } = new();
}
