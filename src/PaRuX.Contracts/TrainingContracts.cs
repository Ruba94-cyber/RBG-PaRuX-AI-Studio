namespace PaRuX.Contracts;

// Training Lab is entirely PaRuX-managed: Python/uv/venv live under PaRuXDataRoot, never the
// system PATH. Methods are closed strings because they are a wire/API surface used by Windows,
// Android and CLI, while the Python runner is the execution source of truth.

public sealed class TrainingEngineStatus
{
    public bool Installed { get; set; }
    /// <summary>True only when the pinned PaRuX runtime identity is present and at least one method
    /// is genuinely supported by the detected execution backend.</summary>
    public bool Ready { get; set; }
    public string? PythonVersion { get; set; }
    public string? UnslothVersion { get; set; }
    public string? TorchVersion { get; set; }
    /// <summary>Legacy compatibility bit. True only for the native CUDA backend; callers should use
    /// Backend + AvailableMethods for new capability decisions.</summary>
    public bool CudaVerified { get; set; }
    /// <summary>cuda | rocm-cuda-api | cpu | xpu-unvalidated | unknown.</summary>
    public string Backend { get; set; } = "unknown";
    /// <summary>SHA-256 of the canonical complete resolved pip-freeze captured at installation.</summary>
    public string? ResolvedPackagesSha256 { get; set; }
    public string Detail { get; set; } = string.Empty;
    public List<string> AvailableMethods { get; set; } = new();
    /// <summary>Honest hardware/backend classification: cuda-validated | rocm-cuda-api |
    /// cpu-tts-only | accelerator-unvalidated | not-installed | unknown. Never a promise that an
    /// unlisted accelerator "will work".</summary>
    public string HardwareProfile { get; set; } = "unknown";
    /// <summary>Every method that is NOT advertised on this backend with the concrete reason, so an
    /// unsupported profile (for example an AMD iGPU on Windows) is recorded instead of hidden.</summary>
    public List<TrainingUnsupportedMethod> UnsupportedMethods { get; set; } = new();
}

public sealed class TrainingUnsupportedMethod
{
    public string Method { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Outcome of removing the managed training runtime. Provisioning several gigabytes on
/// demand is only acceptable if the disk can be handed back.</summary>
public sealed class TrainingEngineRemovalResult
{
    public bool Removed { get; set; }
    public long FreedBytes { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public static class TrainingMethod
{
    public const string Sft = "sft";
    public const string Dpo = "dpo";
    public const string ContinuedPretraining = "continued-pretraining";
    public const string Grpo = "grpo";
    public const string VisionSft = "vision-sft";
    public const string TtsSft = "tts-sft";
    public static readonly string[] All = { Sft, Dpo, ContinuedPretraining, Grpo, VisionSft, TtsSft };

    public static string Normalize(string? value)
    {
        string method = string.IsNullOrWhiteSpace(value) ? Sft : value.Trim().ToLowerInvariant();
        if (!All.Contains(method, StringComparer.Ordinal))
            throw new InvalidDataException("Unknown training method. Known methods: " + string.Join(", ", All) + ".");
        return method;
    }
}

public static class GrpoRewardMode
{
    public const string ExactReference = "exact-reference";
    public const string ContainsReference = "contains-reference";
    public const string XmlFormat = "xml-format";
    public const string JsonFormat = "json-format";
    public static readonly string[] All = { ExactReference, ContainsReference, XmlFormat, JsonFormat };

    public static string Normalize(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? ExactReference : value.Trim().ToLowerInvariant();
        if (!All.Contains(normalized, StringComparer.Ordinal))
            throw new InvalidDataException("Unknown GRPO reward mode. Known modes: " + string.Join(", ", All) + ".");
        return normalized;
    }
}

public sealed class TrainingJobRequest
{
    /// <summary>
    /// JSONL dataset. SFT: {messages:[...]}; DPO: {prompt,chosen,rejected}; continued pretraining:
    /// {text}; GRPO: {prompt,reference} for reference rewards (format rewards need prompt only);
    /// Vision SFT: {image,messages:[...]}; TTS SFT: {audio,text}.
    /// Relative media paths are resolved from the JSONL directory.
    /// </summary>
    public string TrainJsonlPath { get; set; } = string.Empty;
    public string BaseModel { get; set; } = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit";
    public int LoraRank { get; set; } = 16;
    public int MaxSteps { get; set; } = 60;
    public double LearningRate { get; set; } = 0.0002;
    public string Method { get; set; } = TrainingMethod.Sft;
    public double DpoBeta { get; set; } = 0.1;
    public int MaxSequenceLength { get; set; } = 2048;

    // GRPO is deliberately restricted to built-in deterministic reward functions. Training data may
    // not inject Python/code into the managed runner.
    public string GrpoRewardMode { get; set; } = Contracts.GrpoRewardMode.ExactReference;
    public int GrpoGenerations { get; set; } = 4;

    // Generic field names are configurable only as JSON property names, never expressions/scripts.
    public string TextField { get; set; } = "text";
    public string ImageField { get; set; } = "image";
    public string AudioField { get; set; } = "audio";
}

public sealed class TrainingJobStatus
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "Queued";
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public double? LastLoss { get; set; }
    public double? LastGradientNorm { get; set; }
    public double? LastLearningRate { get; set; }
    public double? LastEpoch { get; set; }
    public List<TrainingMetricSample> History { get; set; } = new();
    public string? Error { get; set; }
    public string? AdapterPath { get; set; }
    public string Method { get; set; } = TrainingMethod.Sft;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class TrainingMetricSample
{
    public int Step { get; set; }
    public double? Loss { get; set; }
    public double? GradientNorm { get; set; }
    public double? LearningRate { get; set; }
    public double? Epoch { get; set; }
}

public sealed class TrainingPlanRequest
{
    public string TrainJsonlPath { get; set; } = string.Empty;
    public string Method { get; set; } = TrainingMethod.Sft;
    public string BaseModel { get; set; } = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit";
    public int LoraRank { get; set; } = 16;
    public int MaxSteps { get; set; } = 60;
    public double LearningRate { get; set; } = 0.0002;
    public int MaxSequenceLength { get; set; } = 2048;
    public string GrpoRewardMode { get; set; } = Contracts.GrpoRewardMode.ExactReference;
}

public sealed class TrainingPlanFinding
{
    public string Severity { get; set; } = "Warning";
    public string Field { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class TrainingPlanResult
{
    public bool CanStart { get; set; }
    public string DetectedMethod { get; set; } = TrainingMethod.Sft;

    public bool DatasetSupportsSupervised { get; set; }
    public bool DatasetSupportsPreference { get; set; }
    public bool DatasetSupportsContinuedPretraining { get; set; }
    public bool DatasetSupportsGrpo { get; set; }
    public bool DatasetSupportsVision { get; set; }
    public bool DatasetSupportsTts { get; set; }
    public int DatasetExamples { get; set; }
    public int DatasetUnparseableLines { get; set; }
    public bool DatasetSampleTruncated { get; set; }
    public string DatasetSummary { get; set; } = string.Empty;

    public int RecommendedLoraRank { get; set; }
    public int RecommendedMaxSteps { get; set; }
    public double RecommendedLearningRate { get; set; }
    public string RecommendationRationale { get; set; } = string.Empty;
    public int RecommendedMaxSequenceLength { get; set; }

    public double EstimatedGib { get; set; }
    public double AvailableGib { get; set; }
    public List<TrainingPlanFinding> Findings { get; set; } = new();
}
