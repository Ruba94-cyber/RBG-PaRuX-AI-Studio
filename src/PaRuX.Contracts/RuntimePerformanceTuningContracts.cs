namespace PaRuX.Contracts;

/// <summary>One bounded llama.cpp configuration PaRuX can benchmark before choosing runtime
/// launch parameters. Every field represents a real llama.cpp control; unsupported/experimental
/// combinations are benchmark candidates only and are never persisted as a winner unless they
/// complete successfully on the user's exact runtime and hardware.</summary>
public sealed class LlamaPerformanceProfileDto
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int BatchSize { get; set; }
    public int UBatchSize { get; set; }
    public string CacheTypeK { get; set; } = "q8_0";
    public string CacheTypeV { get; set; } = "q8_0";
    public string FlashAttention { get; set; } = "on";
    public int GpuLayers { get; set; } = -1;
    public string SplitMode { get; set; } = "none";
    public int MainGpu { get; set; }
    public string Device { get; set; } = string.Empty;
    public string TensorSplit { get; set; } = string.Empty;
    public bool EnableCudaP2P { get; set; }
    public int ContextSize { get; set; } = 8192;
    public int FitTargetMib { get; set; } = 1024;
    public int Threads { get; set; }
    public int ThreadsBatch { get; set; }
    public int ParallelSlots { get; set; } = 1;
    public bool ContinuousBatching { get; set; } = true;
    public double SlotPromptSimilarity { get; set; } = 0.10;
    public string ReasoningEffort { get; set; } = "default";
    public int ReasoningBudget { get; set; } = -1;
    public string LoadMode { get; set; } = "auto";
    public string TensorReadLazy { get; set; } = "auto";
    public int ProcessPriority { get; set; }
    public int PollPercent { get; set; } = 50;
    public bool CpuStrict { get; set; }
    public string NumaMode { get; set; } = "none";
    public string SpeculativeType { get; set; } = "none";
    public int SpeculativeDraftMax { get; set; }
    public string? SpeculativeDraftModelPath { get; set; }
    public string? SpeculativeDraftDevice { get; set; }
    public bool BackendSampling { get; set; }

    // Benchmark-only prompt length. A long-context launch may expose up to 1M tokens, while quick
    // candidate ranking deliberately pre-fills a bounded competitive checkpoint (currently 180k)
    // instead of paying a 1M prefill for every matrix row. It is never serialized as launch policy.
    [System.Text.Json.Serialization.JsonIgnore]
    public int BenchmarkPromptTokens { get; set; } = 1024;

    // Launch-only context controls. Runtime tuning deliberately does not persist these as a
    // benchmark winner: ContextPreferenceLaunch derives them from the user's explicit per-model
    // context preference each time a model is launched. Keeping them on the concrete launch
    // profile means every loader (default, arbitrary, routed candidate and hot-pool child) can
    // build the same llama.cpp arguments instead of only the default StartText path knowing about
    // RAM-backed KV cache or RoPE extension.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DisableKvOffload { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public double RopeScale { get; set; } = 1.0;

    /// <summary>
    /// True when <see cref="ContextSize"/> is the user's explicit PER-INFERENCE window, taken from
    /// ContextPreferenceStore - the quantity Runtime Manager describes as "how many tokens the
    /// selected model can attend to simultaneously in one inference". False when it is only an
    /// adaptive resource envelope, which is a TOTAL that llama.cpp may legitimately divide across
    /// its slots.
    ///
    /// The distinction is load-bearing: <c>--ctx-size</c> is a total, so a per-inference number
    /// handed to it verbatim gets silently divided by the slot count. Measured on the physical PC
    /// (2026-09-21), that divided a user-selected window down to 8192 tokens per request and
    /// starved the coding agent. Launch-only, like the two fields above.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ContextIsPerRequest { get; set; }

    // Same launch profile can pass through legacy/default code after the common resolver. This
    // transient marker prevents a second ApplyTo call from projecting the same advanced flags
    // again. It is deliberately excluded from API/storage serialization and benchmark winners.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ContextPolicyApplied { get; set; }

    /// <summary>
    /// Launch-only: keep the routed-expert (MoE) weights of the first N layers in system memory
    /// (<c>--n-cpu-moe N</c>); attention and shared tensors stay on the accelerator. 0 emits nothing.
    /// Only the launch planner sets it, and only for a runtime build whose flag has been verified.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int CpuMoeLayers { get; set; }

    /// <summary>
    /// Launch-only: a verified <c>--override-tensor</c> pattern list, used instead of
    /// <see cref="CpuMoeLayers"/> only when the runtime lacks <c>--n-cpu-moe</c>. Empty emits nothing.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string OverrideTensor { get; set; } = string.Empty;
}

public sealed class RuntimePerformanceTuneRequest
{
    public string ModelPath { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Workload { get; set; } = "chat";
    public bool IncludeSpeculative { get; set; } = true;
    public bool IncludeMultiGpu { get; set; } = true;
    public bool SustainedValidation { get; set; }
}

public sealed class RuntimePerformanceCandidateResult
{
    public LlamaPerformanceProfileDto Profile { get; set; } = new();
    public string Status { get; set; } = "queued";
    public double? PromptTokensPerSecond { get; set; }
    public double? DecodeTokensPerSecond { get; set; }
    public double? TimeToFirstTokenMilliseconds { get; set; }
    public double? LoadMilliseconds { get; set; }
    public double? SustainedDecodeTokensPerSecond { get; set; }
    public double? ConcurrentDecodeTokensPerSecond { get; set; }
    public int? ConcurrentRequests { get; set; }
    public double? AverageGpuPowerWatts { get; set; }
    public double? PeakGpuPowerWatts { get; set; }
    public double? MaxGpuTemperatureC { get; set; }
    public long? PeakGpuMemoryUsedMib { get; set; }
    public double? Score { get; set; }
    public string? Backend { get; set; }
    public string? HardwareKey { get; set; }
    public int? ActualGpuLayers { get; set; }
    public string? Error { get; set; }
}

public sealed class RuntimePerformanceTuneRun
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "Queued";
    public string ModelPath { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Workload { get; set; } = "chat";
    public List<RuntimePerformanceCandidateResult> Candidates { get; set; } = new();
    public LlamaPerformanceProfileDto? Winner { get; set; }
    public string? Backend { get; set; }
    public string? HardwareKey { get; set; }
    public string? Error { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class RuntimePerformanceProfileSelection
{
    public string Key { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public long ModelSizeBytes { get; set; }
    public long ModelLastWriteUtcTicks { get; set; }
    public string Workload { get; set; } = "chat";
    public string RuntimeBuildTag { get; set; } = string.Empty;
    public string Backend { get; set; } = string.Empty;
    public string HardwareKey { get; set; } = string.Empty;
    public LlamaPerformanceProfileDto Profile { get; set; } = new();
    public double PromptTokensPerSecond { get; set; }
    public double DecodeTokensPerSecond { get; set; }
    public double? TimeToFirstTokenMilliseconds { get; set; }
    public double? LoadMilliseconds { get; set; }
    public double? SustainedDecodeTokensPerSecond { get; set; }
    public double? ConcurrentDecodeTokensPerSecond { get; set; }
    public int? ConcurrentRequests { get; set; }
    public double? SustainedDecodeRegressionPercent { get; set; }
    public bool ThermalRegressionSuspected { get; set; }
    public double? AverageGpuPowerWatts { get; set; }
    public double? MaxGpuTemperatureC { get; set; }
    public long? PeakGpuMemoryUsedMib { get; set; }
    public double? PromptCacheColdTtftMilliseconds { get; set; }
    public double? PromptCacheWarmTtftMilliseconds { get; set; }
    public double? PromptCacheImprovementPercent { get; set; }
    public bool? PromptCacheRecommended { get; set; }
    public DateTime TunedUtc { get; set; }
}

public sealed class GpuPerformanceTelemetryDto
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? MemoryTotalMib { get; set; }
    public int? MemoryUsedMib { get; set; }
    public int? MemoryFreeMib { get; set; }
    public int? UtilizationPercent { get; set; }
    public double? TemperatureC { get; set; }
    public double? PowerWatts { get; set; }
    public int? SmClockMhz { get; set; }
    public double? ComputeCapability { get; set; }
    public DateTime SampledUtc { get; set; }
}

public sealed class RuntimePerformanceStatusDto
{
    public string Mode { get; set; } = "balanced";
    public List<RuntimePerformanceProfileSelection> Profiles { get; set; } = new();
    public List<GpuPerformanceTelemetryDto> Gpus { get; set; } = new();
    public string? ActiveRuntimeTag { get; set; }
    public string? ActiveModelPath { get; set; }
}

public sealed class SetRuntimePerformanceModeRequest
{
    public string Mode { get; set; } = "balanced";
}

public sealed class RuntimeBuildRaceRequest
{
    public string Backend { get; set; } = "text-cuda";
    public string ModelPath { get; set; } = string.Empty;
    public string Workload { get; set; } = "chat";
    public double MinimumImprovementPercent { get; set; } = 3.0;
    /// <summary>True by default because a runtime build may look fast during a short burst and
    /// regress after thermals/clocks settle. Disable only for an explicit quick diagnostic.</summary>
    public bool SustainedValidation { get; set; } = true;
}

public sealed class RuntimeBuildRaceCandidateDto
{
    public string Tag { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public double? PromptTokensPerSecond { get; set; }
    public double? DecodeTokensPerSecond { get; set; }
    public double? TimeToFirstTokenMilliseconds { get; set; }
    public double? LoadMilliseconds { get; set; }
    public double? SustainedDecodeTokensPerSecond { get; set; }
    public double? PeakGpuMemoryUsedMib { get; set; }
    public double? MaxGpuTemperatureC { get; set; }
    public double? AverageGpuPowerWatts { get; set; }
    public double? Score { get; set; }
    public double? ImprovementPercent { get; set; }
    public bool EligibleForPromotion { get; set; }
    public string? DecisionReason { get; set; }
    public string? Error { get; set; }
}

public sealed class RuntimeBuildRaceStatusDto
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "Queued";
    public string Backend { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public string OriginalTag { get; set; } = string.Empty;
    public string? WinnerTag { get; set; }
    public bool Switched { get; set; }
    public List<RuntimeBuildRaceCandidateDto> Candidates { get; set; } = new();
    public string? Error { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
