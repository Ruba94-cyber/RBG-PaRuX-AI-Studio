namespace PaRuX.Contracts;

/// <summary>Desktop-only explicit discovery input. Repository IDs are validated again by Core;
/// publisher URLs, revisions, files and credentials are never client-selected here.</summary>
public sealed class ModelDiscoveryExplicitRefreshRequest
{
    public List<string>? RepositoryIds { get; set; }
}

/// <summary>Untrusted publisher metadata observed during an explicit discovery refresh.
/// Candidate is not an installed, validated, or recommended model.</summary>
public sealed class ModelDiscoveryCandidate
{
    public string Key { get; set; } = string.Empty;
    public string Provider { get; set; } = "huggingface";
    public int ProviderAdapterVersion { get; set; } = 1;
    public string RepoId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string RepoFilePath { get; set; } = string.Empty;
    public string PinnedUrl { get; set; } = string.Empty;
    public string Lifecycle { get; set; } = "Candidate";
    public string Disposition { get; set; } = "active";
    public string? DeclaredLicense { get; set; }
    public string? DeclaredPipelineTag { get; set; }
    public List<string> DeclaredTags { get; set; } = new();
    public long? DeclaredSizeBytes { get; set; }
    public string? DeclaredSha256 { get; set; }
    /// <summary>Provenance labels of the public feeds that listed this repository revision
    /// (popular, new, recently_updated). Discovery provenance only: never quality evidence.</summary>
    public List<string> DiscoveryCohorts { get; set; } = new();
    public string IntegrityStatus { get; set; } = "unverified";
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? IgnoredUtc { get; set; }
    public bool NoticePending { get; set; }
    public DateTime? NoticeDeferredUntilUtc { get; set; }
}

public sealed class ModelDiscoveryCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<ModelDiscoveryCandidate> Candidates { get; set; } = new();
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? NextRefreshUtc { get; set; }
    public string? LastErrorCode { get; set; }
}

public sealed class ModelDiscoveryRefreshResult
{
    public string Outcome { get; set; } = string.Empty;
    public int CandidateCount { get; set; }
    public DateTime? NextRefreshUtc { get; set; }
    public string? ErrorCode { get; set; }
    public string? ReasonCode { get; set; }
    public string? Message { get; set; }
}

public static class ModelDiscoveryPolicyValues
{
    public const string EvaluationAsk = "ask";
    public const string EvaluationAlwaysEvaluateSuitable = "always_evaluate_suitable";
    public const string PromotionAskBeforeReplacing = "ask_before_replacing";
    public const string PromotionAutoPromoteClearlyBetter = "auto_promote_clearly_better";
    public const string PromotionNeverReplacePinnedOrManual = "never_replace_pinned_or_manual";
}

public sealed class ModelDiscoveryPolicySettings
{
    public int SchemaVersion { get; set; } = 1;
    public string EvaluationPolicy { get; set; } = ModelDiscoveryPolicyValues.EvaluationAsk;
    public string PromotionPolicy { get; set; } = ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing;
}

public sealed record ModelDiscoveryPolicyLoadResult(
    ModelDiscoveryPolicySettings Settings,
    string? WarningCode);

public enum ModelDiscoveryPromotionAction
{
    AskBeforeReplacing,
    AutoPromote,
    NeverReplace
}

public sealed record ModelDiscoveryPolicyAction(bool Enabled, string ReasonCode);

public sealed record ModelDiscoveryPolicyDecision(
    ModelDiscoveryPolicyAction Notify,
    ModelDiscoveryPolicyAction AutoEvaluate,
    ModelDiscoveryPromotionAction PromotionAction,
    string PromotionReasonCode)
{
    public bool AutoPromote => PromotionAction == ModelDiscoveryPromotionAction.AutoPromote;
    public bool NeverReplace => PromotionAction == ModelDiscoveryPromotionAction.NeverReplace;
}
