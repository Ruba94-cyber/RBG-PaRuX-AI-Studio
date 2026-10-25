namespace PaRuX.Contracts;

/// <summary>Persistent evidence produced when a newly installed managed llama.cpp runtime is
/// compared against the previously active build. It is diagnostic evidence, not user state.</summary>
public sealed class RuntimeRegressionEvidenceDto
{
    public string Id { get; set; } = string.Empty;
    public string Backend { get; set; } = string.Empty;
    public string OriginalTag { get; set; } = string.Empty;
    public string CandidateTag { get; set; } = string.Empty;
    public string ActiveTag { get; set; } = string.Empty;
    /// <summary>first-install | accepted | rejected | deferred.</summary>
    public string Decision { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    /// <summary>Only the local file name is persisted/exposed; the full model path is unnecessary
    /// evidence and would leak workstation layout into diagnostics.</summary>
    public string? ModelFileName { get; set; }
    public double? OriginalPromptTokensPerSecond { get; set; }
    public double? OriginalDecodeTokensPerSecond { get; set; }
    public double? CandidatePromptTokensPerSecond { get; set; }
    public double? CandidateDecodeTokensPerSecond { get; set; }
    public double? OriginalTimeToFirstTokenMilliseconds { get; set; }
    public double? CandidateTimeToFirstTokenMilliseconds { get; set; }
    public double? OriginalLoadMilliseconds { get; set; }
    public double? CandidateLoadMilliseconds { get; set; }
    public double? OriginalSustainedDecodeTokensPerSecond { get; set; }
    public double? CandidateSustainedDecodeTokensPerSecond { get; set; }
    public double? OriginalPeakGpuMemoryUsedMib { get; set; }
    public double? CandidatePeakGpuMemoryUsedMib { get; set; }
    public double? OriginalMaxGpuTemperatureC { get; set; }
    public double? CandidateMaxGpuTemperatureC { get; set; }
    public double? OriginalAverageGpuPowerWatts { get; set; }
    public double? CandidateAverageGpuPowerWatts { get; set; }
    public double? BaselineCompositeScore { get; set; }
    public double? CandidateCompositeScore { get; set; }
    public double? ImprovementPercent { get; set; }
    public DateTime MeasuredUtc { get; set; }
}
