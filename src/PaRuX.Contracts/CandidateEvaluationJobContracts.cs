using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaRuX.Contracts;

public sealed class StartCandidateEvaluationJobRequest
{
    public string ManagedFileName { get; set; } = string.Empty;
    /// <summary>Exact built-in quality profile; omission preserves the legacy smoke evaluation.</summary>
    public string? QualityProfileId { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? OtherFields { get; set; }
}

public sealed class CandidateEvaluationJobAccepted
{
    public string JobId { get; set; } = string.Empty;
}

/// <summary>Stable identifiers for the built-in Candidate evidence scope. These identify
/// evaluation workloads, not a general model capability or recommendation profile.</summary>
public static class CandidateEvaluationScope
{
    public const string ChatInstructionFollowingCapability = "chat.instruction_following.smoke";
    public const string ChatInstructionFollowingSuiteId = "parux.chat.instruction_following.smoke";
    public const int ChatInstructionFollowingSuiteVersion = 1;
    /// <summary>A fixed deterministic smoke profile, not one of the user's execution-quality profiles.</summary>
    public const string ChatInstructionFollowingQualityProfileId = "parux.candidate-quality.fixed-smoke";
    public const int ChatInstructionFollowingQualityProfileVersion = 1;
    public const string ChatInstructionFollowingPerformanceWorkloadId = "parux.chat.instruction_following.performance";
    public const int ChatInstructionFollowingPerformanceWorkloadVersion = 3;

    // The general-chat qualification is a separate, held-out suite. Its declared routing scope is
    // deliberately one existing text role/profile pair; it makes no coding, vision, or best-in-class claim.
    public const string GeneralChatCapability = "chat.general.v1";
    public const string GeneralChatSuiteId = "parux.chat.general.heldout";
    public const int GeneralChatSuiteVersion = 3;
    public const string GeneralChatQualityProfileId = "parux.candidate-quality.general-chat";
    public const int GeneralChatQualityProfileVersion = 2;
    public const string GeneralChatRouteRole = "standard";
    public const string GeneralChatAutoProfile = "balanced";
    public const int GeneralChatMinimumSuccessRateBasisPoints = 9000;
    public const int GeneralChatMinimumReliabilityBasisPoints = 9500;
    public const int GeneralChatMinimumFreshProcessRuns = 5;
    public const int GeneralChatMinimumPassesPerCategory = 5;
}

/// <summary>Safe control-plane summary. Generated text, model paths and raw evidence stay internal.</summary>
public sealed class CandidateEvaluationJobStatus
{
    public string JobId { get; set; } = string.Empty;
    public string CandidateKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? QualityStatus { get; set; }
    public int? QualityPassCount { get; set; }
    public int? QualityExpectedCount { get; set; }
    public string? PerformanceStatus { get; set; }
    public int? Repetitions { get; set; }
    public long? GeneratedTokenCount { get; set; }
    public double? PromptTokensPerSecond { get; set; }
    public double? DecodeTokensPerSecond { get; set; }
    public double? CompletionMilliseconds { get; set; }
    public double? TimeToFirstTokenMilliseconds { get; set; }
    /// <summary>Optional for legacy summaries; when present, identifies the exact quality capability/suite
    /// and paired local performance workload used by this job.</summary>
    public string? Capability { get; set; }
    public string? QualitySuiteId { get; set; }
    public int? QualitySuiteVersion { get; set; }
    public string? QualityProfileId { get; set; }
    public int? QualityProfileVersion { get; set; }
    public string? QualityInvocationId { get; set; }
    public string? PerformanceWorkloadId { get; set; }
    public int? PerformanceWorkloadVersion { get; set; }
    public string? Eligibility { get; set; }
    public string? ErrorCode { get; set; }
    /// <summary>Optional: the versioned (ld1) launch identity the evidence was measured under, its canonical
    /// semantic arguments, and the names (never values) of launch-relevant environment variables.</summary>
    public string? EvaluationLaunchFingerprint { get; set; }
    public List<string>? EvaluationLaunchArguments { get; set; }
    public List<string>? EvaluationLaunchEnvironmentNames { get; set; }
    /// <summary>Bounded reason when the actual launch could not be described or reproduced exactly.</summary>
    public string? EvaluationLaunchRefusal { get; set; }
    /// <summary>Request shaping contract used for the evidence (e.g. production-chat-request-v1).</summary>
    public string? RequestParity { get; set; }
    /// <summary>Optional: ids of the quality cases that did not pass in this run (never prompts or answers).</summary>
    public List<string>? QualityFailedCaseIds { get; set; }
    /// <summary>True when at least one latency sample hit the answer-token cap, so the completion time is
    /// only a lower bound (and cannot win a latency comparison).</summary>
    public bool? CompletionLatencyIsLowerBound { get; set; }
}
