namespace PaRuX.Contracts;

/// <summary>Exact discovery and installed-byte identity for local Candidate evaluation.</summary>
public sealed class CandidateEvidenceIdentity
{
    public string Provider { get; set; } = string.Empty;
    public string CandidateKey { get; set; } = string.Empty;
    public string RepoId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string RepoFilePath { get; set; } = string.Empty;
    public string PinnedSourceUrl { get; set; } = string.Empty;
    public string ManagedFileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string LocalSha256 { get; set; } = string.Empty;
}

public sealed class CandidateQualityCaseResult
{
    public string CaseId { get; set; } = string.Empty;
    /// <summary>pass, fail, inconclusive, or error.</summary>
    public string Outcome { get; set; } = string.Empty;
    public bool Critical { get; set; }
    /// <summary>Only stop permits a passing v2 case; missing is represented by an empty string.</summary>
    public string FinishReason { get; set; } = string.Empty;
    public bool Truncated { get; set; }
}

/// <summary>Independent, deterministic task-quality evidence; no self-judge score.</summary>
public sealed class CandidateQualityEvidence
{
    public int RecordVersion { get; set; } = 1;
    /// <summary>Unique identity for one quality-suite invocation; present in record version 3.</summary>
    public string InvocationId { get; set; } = string.Empty;
    public CandidateEvidenceIdentity Identity { get; set; } = new();
    public string Capability { get; set; } = string.Empty;
    public string SuiteId { get; set; } = string.Empty;
    public int SuiteVersion { get; set; }
    public string SuiteSha256 { get; set; } = string.Empty;
    /// <summary>Exact fixed generation profile; deliberately distinct from user-facing execution profiles.</summary>
    public string QualityProfileId { get; set; } = string.Empty;
    public int QualityProfileVersion { get; set; }
    public string QualityProfileSha256 { get; set; } = string.Empty;
    /// <summary>Required in v2. Quality workload is distinct from performance workload.</summary>
    public CandidateEvaluationEnvironment? Environment { get; set; }
    /// <summary>Completed, Failed, Cancelled, or Unavailable in v2.</summary>
    public string ExecutionStatus { get; set; } = string.Empty;
    public int ExpectedCaseCount { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public int InconclusiveCount { get; set; }
    public int ErrorCount { get; set; }
    public List<CandidateQualityCaseResult> Cases { get; set; } = new();
    public bool Completed { get; set; }
    public bool Cancelled { get; set; }
    public bool Truncated { get; set; }
    public bool ExternalFallbackUsed { get; set; }
    public DateTime RecordedUtc { get; set; }
}

/// <summary>Exact context in which measured performance evidence was produced.</summary>
public sealed class CandidateEvaluationEnvironment
{
    public string HardwareFingerprint { get; set; } = string.Empty;
    public string RuntimeFingerprint { get; set; } = string.Empty;
    public string BinaryFingerprint { get; set; } = string.Empty;
    public string ConfigurationFingerprint { get; set; } = string.Empty;
    public string WorkloadFingerprint { get; set; } = string.Empty;
}

public sealed class CandidateMeasuredMetric
{
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public double Value { get; set; }
}

public sealed class CandidatePerformanceSample
{
    public int Repetition { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string FinishReason { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public double PromptMilliseconds { get; set; }
    public int GeneratedTokens { get; set; }
    public double DecodeMilliseconds { get; set; }
    public double CompletionMilliseconds { get; set; }
    /// <summary>Request-submit to first non-empty visible content in the streamed response.</summary>
    public double TimeToFirstTokenMilliseconds { get; set; }
}

/// <summary>Measured local performance, separate from task-quality evidence.</summary>
public sealed class CandidatePerformanceEvidence
{
    public int RecordVersion { get; set; } = 1;
    public CandidateEvidenceIdentity Identity { get; set; } = new();
    public string Capability { get; set; } = string.Empty;
    public CandidateEvaluationEnvironment Environment { get; set; } = new();
    public string WorkloadId { get; set; } = string.Empty;
    public int WorkloadVersion { get; set; }
    public string WorkloadSha256 { get; set; } = string.Empty;
    public string ExecutionStatus { get; set; } = string.Empty;
    /// <summary>Bounded diagnostic code for a Failed run (empty otherwise). Never contains model text.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureReason { get; set; }
    public int ExpectedRepetitions { get; set; }
    public List<CandidatePerformanceSample> Samples { get; set; } = new();
    public bool Completed { get; set; }
    public bool Cancelled { get; set; }
    public bool Truncated { get; set; }
    public bool ExternalFallbackUsed { get; set; }
    public int Repetitions { get; set; }
    public long TokenCount { get; set; }
    public List<CandidateMeasuredMetric> Metrics { get; set; } = new();
    public DateTime RecordedUtc { get; set; }
}

public sealed class CandidateEvidenceLedger
{
    public int SchemaVersion { get; set; } = 1;
    public List<CandidateQualityEvidence> Quality { get; set; } = new();
    public List<CandidatePerformanceEvidence> Performance { get; set; } = new();
}

public static class CandidateEligibilityOutcome
{
    public const string Ineligible = "Ineligible";
    public const string EligibleForFurtherReview = "EligibleForFurtherReview";
}
