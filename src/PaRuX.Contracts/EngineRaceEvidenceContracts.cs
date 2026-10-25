namespace PaRuX.Contracts;

public sealed class EngineRaceCandidateEvidenceDto
{
    public string EngineId { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public bool Success { get; set; }
    public long? ElapsedMs { get; set; }

    // Media races keep renderer and independent-judge reliability as separate evidence domains.
    // Nullable preserves backward compatibility with runtime-race and historical JSON rows.
    public bool? QualityEvaluationSucceeded { get; set; }
    public long? QualityEvaluationElapsedMs { get; set; }
    public bool? QualityAccepted { get; set; }
    public double? QualityScore { get; set; }
    public string? GenerationError { get; set; }
    public string? QualityError { get; set; }

    public double? PromptTokensPerSecond { get; set; }
    public double? DecodeTokensPerSecond { get; set; }
    public double? SustainedDecodeTokensPerSecond { get; set; }
    public double? TimeToFirstTokenMilliseconds { get; set; }
    public double? PeakVramMib { get; set; }
    public double? MaxGpuTemperatureC { get; set; }
    public double? ErrorRate { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Compact benchmark evidence only. Media payload bytes are deliberately never persisted
/// here; artifacts remain owned by their existing media flow.</summary>
public sealed class EngineRaceEvidenceDto
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? WinnerEngineId { get; set; }
    public string? WinnerEngineVersion { get; set; }
    public string SelectionReason { get; set; } = string.Empty;
    public DateTime MeasuredUtc { get; set; }
    public List<EngineRaceCandidateEvidenceDto> Candidates { get; set; } = new();
}
