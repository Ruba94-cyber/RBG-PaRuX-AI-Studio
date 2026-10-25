namespace PaRuX.Contracts;

/// <summary>Read-only comparison between PaRuX's managed llama.cpp path and one already-running
/// local OpenAI-compatible USER_MANAGED backend. PaRuX never starts, stops, reconfigures or updates
/// the external backend. SameWeightsConfirmed is required before a winner is presented as an
/// apples-to-apples result.</summary>
public sealed class RuntimeBackendRaceRequest
{
    public string ManagedModelPath { get; set; } = string.Empty;
    public string Workload { get; set; } = "chat";
    public string ExternalEndpoint { get; set; } = string.Empty;
    public string ExternalModel { get; set; } = string.Empty;
    public bool SameWeightsConfirmed { get; set; }
    public bool SustainedValidation { get; set; }
}

public sealed class RuntimeBackendRaceCandidateDto
{
    public string Backend { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public double? PromptTokensPerSecond { get; set; }
    public double? DecodeTokensPerSecond { get; set; }
    public double? TimeToFirstTokenMilliseconds { get; set; }
    public double? SustainedDecodeTokensPerSecond { get; set; }
    public double? Score { get; set; }
    public string? Error { get; set; }
}

public sealed class RuntimeBackendRaceResult
{
    public bool Comparable { get; set; }
    public string? Winner { get; set; }
    public string Explanation { get; set; } = string.Empty;
    public List<RuntimeBackendRaceCandidateDto> Candidates { get; set; } = new();
    public DateTime MeasuredUtc { get; set; }
}
