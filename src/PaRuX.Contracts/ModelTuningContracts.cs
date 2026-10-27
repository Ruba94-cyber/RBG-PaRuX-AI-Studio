namespace PaRuX.Contracts;

/// <summary>States of one automatic "tuning for your PC" job. Exactly these five values are ever published.</summary>
public static class ModelTuningStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Applied = "applied";
    public const string Skipped = "skipped";
    public const string Failed = "failed";

    public static bool IsTerminal(string? state) => state is Applied or Skipped or Failed;
}

/// <summary>A suggestion the user may act on (for example a draft head that would speed the model up). Never downloaded silently.</summary>
public sealed class ModelTuningProposalDto
{
    /// <summary>"draft_head".</summary>
    public string Kind { get; set; } = "draft_head";
    public string RepoId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

public sealed class ModelTuningItemDto
{
    public string Id { get; set; } = string.Empty;
    /// <summary>"text" (GGUF chat model) or "media" (image/video checkpoint).</summary>
    public string Kind { get; set; } = "text";
    public string ModelLabel { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public string Workload { get; set; } = string.Empty;
    public string State { get; set; } = ModelTuningStates.Queued;
    /// <summary>Measured speed-up in percent; set only for <see cref="ModelTuningStates.Applied"/>.</summary>
    public double? GainPercent { get; set; }
    /// <summary>Why it waits, was skipped or failed, or what was applied (plain language).</summary>
    public string? Reason { get; set; }
    public ModelTuningProposalDto? Proposal { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>GET /api/v1/models/tuning.</summary>
public sealed class ModelTuningStatusDto
{
    public List<ModelTuningItemDto> Items { get; set; } = new();
    /// <summary>True while at least one job is queued or running.</summary>
    public bool Active { get; set; }
}
