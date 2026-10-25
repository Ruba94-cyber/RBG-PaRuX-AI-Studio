namespace PaRuX.Contracts;

public sealed class RuntimeOptimizationRequest
{
    public string ModelPath { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Workload { get; set; } = "chat";
    public bool IncludeSpeculative { get; set; } = true;
    public bool IncludeMultiGpu { get; set; } = true;
    public bool SustainedValidation { get; set; } = true;
    /// <summary>"manual" (the Optimize for this PC button / API) or "idle" (the automatic idle-time calibration). Both run the same pipeline.</summary>
    public string Trigger { get; set; } = "manual";
}

public sealed class RuntimeOptimizationServerResult
{
    public LlamaPerformanceProfileDto Profile { get; set; } = new();
    public string Status { get; set; } = "queued";
    public double? PromptTokensPerSecond { get; set; }
    public double? DecodeTokensPerSecond { get; set; }
    public double? TimeToFirstTokenMilliseconds { get; set; }
    public double? LoadMilliseconds { get; set; }
    public double? SustainedDecodeTokensPerSecond { get; set; }
    /// <summary>Total decoded tokens/second across simultaneous requests when ParallelSlots > 1.
    /// Null means no concurrency race was applicable or completed.</summary>
    public double? ConcurrentDecodeTokensPerSecond { get; set; }
    public int? ConcurrentRequests { get; set; }
    public double? SustainedDecodeRegressionPercent { get; set; }
    public bool ThermalRegressionSuspected { get; set; }
    public double? AverageGpuPowerWatts { get; set; }
    public double? PeakGpuPowerWatts { get; set; }
    public double? MaxGpuTemperatureC { get; set; }
    public long? PeakGpuMemoryUsedMib { get; set; }
    public int DraftTokens { get; set; }
    public int DraftTokensAccepted { get; set; }
    public double? DraftAcceptancePercent { get; set; }
    /// <summary>Hash of the greedy (temperature 0) calibration output. A speculative mode is enabled only if it equals the baseline's.</summary>
    public string? OutputDigest { get; set; }
    /// <summary>First characters of the same greedy calibration output; lets a KV-cache precision change be judged by how far its text diverges from the reference instead of only equal/not equal.</summary>
    public string? OutputSample { get; set; }
    public double? PromptCacheColdTtftMilliseconds { get; set; }
    public double? PromptCacheWarmTtftMilliseconds { get; set; }
    public double? PromptCacheImprovementPercent { get; set; }
    public bool? PromptCacheRecommended { get; set; }
    public double? Score { get; set; }
    public string? Error { get; set; }
}

public sealed class RuntimeOptimizationRun
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "Queued";
    public string ModelPath { get; set; } = string.Empty;
    public string Workload { get; set; } = "chat";
    public string PerformanceMode { get; set; } = "balanced";
    public string? QuickTuneRunId { get; set; }
    public RuntimePerformanceTuneRun? QuickTune { get; set; }
    public List<RuntimeOptimizationServerResult> ServerCandidates { get; set; } = new();
    public LlamaPerformanceProfileDto? Winner { get; set; }
    public bool SpeculativeEnabled { get; set; }
    /// <summary>Why the KV-cache precision was kept (measured speed, VRAM need, output-quality guard).</summary>
    public string? KvDecision { get; set; }
    public string Trigger { get; set; } = "manual";
    public string? Error { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
