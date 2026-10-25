using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Canonical server-internal definition of one currently supported Candidate evidence scope.</summary>
internal sealed record CandidateEvaluationProfileDefinition(
    string Capability,
    string QualitySuiteId,
    int QualitySuiteVersion,
    string QualitySuiteSha256,
    string QualityProfileId,
    int QualityProfileVersion,
    string QualityProfileSha256,
    string QualityWorkloadFingerprint,
    IReadOnlyList<CandidateTextQualitySuite.Case> QualityCases,
    int QualityExpectedCaseCount,
    string PerformanceWorkloadId,
    int PerformanceWorkloadVersion,
    string PerformanceWorkloadSha256,
    string PerformanceWorkloadFingerprint,
    int PerformanceExpectedRepetitions,
    int MaximumPerformancePromptTokens,
    int MaximumPerformanceGeneratedTokens,
    IReadOnlyList<CandidateEvaluationMetricDefinition> PerformanceMetrics,
    int MaximumScopeVersion,
    int MaximumQualityRunCount,
    int MaximumUtilityMarginBasisPoints,
    string? PerformanceCapability = null,
    string? RouteRole = null,
    string? AutoProfile = null,
    int MinimumPolicyPassRateBasisPoints = 0,
    int MinimumPolicyReliabilityBasisPoints = 0,
    int MinimumPolicyRunCount = 2,
    int MinimumRunPassCount = 0,
    int MinimumPassesPerCategory = 0)
{
    /// <summary>Recommendation scopes are evaluated only under the actual AUTO launch (ld1) with production
    /// request parity; the historical instruction-following smoke keeps its fixed evaluator profile.</summary>
    internal bool ActualLaunchScope => RouteRole is not null;

    internal bool MatchesPolicy(CandidateUtilityPolicyDefinition policy) =>
        policy.Capability == Capability
        && policy.QualitySuiteId == QualitySuiteId
        && policy.QualitySuiteVersion == QualitySuiteVersion
        && policy.QualityEvidenceProfileId == QualityProfileId
        && policy.QualityEvidenceProfileVersion == QualityProfileVersion
        && policy.QualityEvidenceProfileSha256 == QualityProfileSha256
        && policy.PerformanceWorkloadId == PerformanceWorkloadId
        && policy.PerformanceWorkloadVersion == PerformanceWorkloadVersion;

    internal bool MatchesQuality(CandidateQualityEvidence evidence) =>
        MatchesQualitySuite(evidence)
        && evidence.QualityProfileId == QualityProfileId
        && evidence.QualityProfileVersion == QualityProfileVersion
        && evidence.QualityProfileSha256 == QualityProfileSha256
        && evidence.ExpectedCaseCount == QualityExpectedCaseCount;

    internal bool MatchesQualitySuite(CandidateQualityEvidence evidence) =>
        MatchesQualitySuiteIdentity(evidence) && evidence.ExpectedCaseCount == QualityExpectedCaseCount;

    internal bool MatchesQualityProfile(CandidateQualityEvidence evidence) =>
        evidence.QualityProfileId == QualityProfileId
        && evidence.QualityProfileVersion == QualityProfileVersion
        && evidence.QualityProfileSha256 == QualityProfileSha256;

    internal bool MatchesQualitySuiteIdentity(CandidateQualityEvidence evidence) =>
        evidence.Capability == Capability && evidence.SuiteId == QualitySuiteId
        && evidence.SuiteVersion == QualitySuiteVersion && evidence.SuiteSha256 == QualitySuiteSha256;

    internal bool MatchesPerformance(CandidatePerformanceEvidence evidence) =>
        evidence.Capability == (PerformanceCapability ?? Capability) && evidence.WorkloadId == PerformanceWorkloadId
        && evidence.WorkloadVersion == PerformanceWorkloadVersion
        && evidence.WorkloadSha256 == PerformanceWorkloadSha256
        && evidence.ExpectedRepetitions == PerformanceExpectedRepetitions;
}

internal sealed record CandidateEvaluationMetricDefinition(string Name, string Unit);

/// <summary>Only scopes explicitly listed here are eligible for utility evidence accounting.</summary>
internal static class CandidateEvaluationProfileCatalog
{
    // Storage compatibility only: this is the immediately previous GeneralChat identity emitted
    // by suite v1. It may be loaded as history but is never returned by a current-scope resolver.
    private const string GeneralChatV1SuiteSha256 = "46112438167d7ffb202af89db8c12aea2c66c9c9c36cb903921822555dc613a4";
    private const string GeneralChatV1WorkloadFingerprint = "quality-workload-sha256:ca7ee8a13fa3fe854f571f32f9b1aa9dc56b69c3abaf38e2bc6fcddb804734e9";
    // Storage compatibility only: suite v2 / quality profile v1 used the fixed temperature-0 seeded request
    // shape. Its records (including retained failed evidence) stay readable but never current.
    private const string GeneralChatV2SuiteSha256 = "780def6e90be8cd80a17fd857868550a714c7050f5ab1c1c3855c49b3af76c17";
    private const string GeneralChatV2WorkloadFingerprint = "quality-workload-sha256:788f3c14eafb4c99cafa8df239d45f08bc99ee8cc3421bf47e06272863bd8c13";
    private const string GeneralChatV2ProfileSha256 = "ce279f61d12fe3729ae722767f78bf2f85b301be9c4f87e9daef079a0b22a13b";
    // Storage compatibility only: performance workload v2 (temperature 0, seed 23).
    private const string PerformanceV2Sha256 = "fe27546dc5907c249d519d3fc4923b7a5c4eb991be99f594f61822e75e152ef6";

    internal static CandidateEvaluationProfileDefinition ChatInstructionFollowingSmoke { get; } = new(
        CandidateEvaluationScope.ChatInstructionFollowingCapability,
        CandidateEvaluationScope.ChatInstructionFollowingSuiteId,
        CandidateEvaluationScope.ChatInstructionFollowingSuiteVersion,
        CandidateTextQualitySuite.Sha256,
        CandidateTextQualitySuite.ProfileId,
        CandidateTextQualitySuite.ProfileVersion,
        CandidateTextQualitySuite.ProfileSha256,
        CandidateTextQualitySuite.WorkloadFingerprint,
        CandidateTextQualitySuite.Cases,
        CandidateTextQualitySuite.Cases.Count,
        CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId,
        CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadVersion,
        CandidateTextPerformanceWorkload.Sha256,
        CandidateTextPerformanceWorkload.WorkloadFingerprint,
        CandidateTextPerformanceWorkload.Repetitions,
        MaximumPerformancePromptTokens: 2048,
        MaximumPerformanceGeneratedTokens: CandidateTextPerformanceWorkload.MaximumGeneratedTokens,
        PerformanceMetrics: Array.AsReadOnly(new[]
        {
            new CandidateEvaluationMetricDefinition(CandidatePerformanceMetric.PromptTokensPerSecondId,
                CandidatePerformanceMetric.TokensPerSecondUnit),
            new CandidateEvaluationMetricDefinition(CandidatePerformanceMetric.DecodeTokensPerSecondId,
                CandidatePerformanceMetric.TokensPerSecondUnit),
            new CandidateEvaluationMetricDefinition(CandidatePerformanceMetric.CompletionMillisecondsId,
                CandidatePerformanceMetric.MillisecondsUnit),
            new CandidateEvaluationMetricDefinition(CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId,
                CandidatePerformanceMetric.MillisecondsUnit)
        }),
        MaximumScopeVersion: 100000,
        MaximumQualityRunCount: CandidateEvidenceStore.MaximumRecordsPerKind - 1,
        MaximumUtilityMarginBasisPoints: 1000000);

    internal static CandidateEvaluationProfileDefinition GeneralChat { get; } = new(
        CandidateGeneralChatQualitySuite.Capability,
        CandidateGeneralChatQualitySuite.Id,
        CandidateGeneralChatQualitySuite.Version,
        CandidateGeneralChatQualitySuite.Sha256,
        CandidateGeneralChatQualitySuite.ProfileId,
        CandidateGeneralChatQualitySuite.ProfileVersion,
        CandidateGeneralChatQualitySuite.ProfileSha256,
        CandidateGeneralChatQualitySuite.WorkloadFingerprint,
        Array.AsReadOnly(CandidateGeneralChatQualitySuite.Cases.Select(item =>
            new CandidateTextQualitySuite.Case(item.Id, item.Prompt, item.Expected, item.Mandatory,
                item.Json, CandidateGeneralChatQualitySuite.SystemPrompt,
                CandidateGeneralChatQualitySuite.Seed, CandidateGeneralChatQualitySuite.MaxTokens,
                item.Category.ToString(), ProductionRequestParity: true)).ToArray()),
        CandidateGeneralChatQualitySuite.Cases.Count,
        // Both scopes measure the exact same fixed local completion latency workload. It is a
        // shared performance dimension, not general-chat quality evidence.
        ChatInstructionFollowingSmoke.PerformanceWorkloadId,
        ChatInstructionFollowingSmoke.PerformanceWorkloadVersion,
        ChatInstructionFollowingSmoke.PerformanceWorkloadSha256,
        ChatInstructionFollowingSmoke.PerformanceWorkloadFingerprint,
        ChatInstructionFollowingSmoke.PerformanceExpectedRepetitions,
        ChatInstructionFollowingSmoke.MaximumPerformancePromptTokens,
        ChatInstructionFollowingSmoke.MaximumPerformanceGeneratedTokens,
        ChatInstructionFollowingSmoke.PerformanceMetrics,
        MaximumScopeVersion: 100000,
        MaximumQualityRunCount: CandidateEvidenceStore.MaximumRecordsPerKind - 1,
        MaximumUtilityMarginBasisPoints: 1000000,
        PerformanceCapability: ChatInstructionFollowingSmoke.Capability,
        RouteRole: CandidateGeneralChatQualitySuite.RouteRole,
        AutoProfile: CandidateGeneralChatQualitySuite.AutoProfile,
        MinimumPolicyPassRateBasisPoints: CandidateGeneralChatQualitySuite.MinimumSuccessRateBasisPoints,
        MinimumPolicyReliabilityBasisPoints: CandidateGeneralChatQualitySuite.MinimumReliabilityBasisPoints,
        MinimumPolicyRunCount: CandidateGeneralChatQualitySuite.MinimumFreshProcessRuns,
        MinimumRunPassCount: CandidateGeneralChatQualitySuite.MinimumPassCount,
        MinimumPassesPerCategory: CandidateGeneralChatQualitySuite.MinimumPassesPerCategory);

    private static CandidateEvaluationProfileDefinition GeneralChatV1Historical { get; } = GeneralChat with
    {
        QualitySuiteVersion = 1,
        QualitySuiteSha256 = GeneralChatV1SuiteSha256,
        QualityWorkloadFingerprint = GeneralChatV1WorkloadFingerprint
    };

    private static CandidateEvaluationProfileDefinition GeneralChatV2Historical { get; } = GeneralChat with
    {
        QualitySuiteVersion = 2,
        QualitySuiteSha256 = GeneralChatV2SuiteSha256,
        QualityProfileVersion = 1,
        QualityProfileSha256 = GeneralChatV2ProfileSha256,
        QualityWorkloadFingerprint = GeneralChatV2WorkloadFingerprint
    };

    private static CandidateEvaluationProfileDefinition PerformanceV2Historical { get; } = ChatInstructionFollowingSmoke with
    {
        PerformanceWorkloadVersion = 2,
        PerformanceWorkloadSha256 = PerformanceV2Sha256,
        PerformanceWorkloadFingerprint = "performance-workload-sha256:" + PerformanceV2Sha256,
        MaximumPerformanceGeneratedTokens = CandidateTextPerformanceWorkload.MaxTokens
    };

    /// <summary>Current or retained-historical scope for a stored performance record; history is never current.</summary>
    internal static CandidateEvaluationProfileDefinition ResolveStoredPerformanceEvidence(CandidatePerformanceEvidence evidence) =>
        evidence.WorkloadVersion == PerformanceV2Historical.PerformanceWorkloadVersion
            && evidence.WorkloadSha256 == PerformanceV2Historical.PerformanceWorkloadSha256
            ? PerformanceV2Historical : ChatInstructionFollowingSmoke;

    internal static bool TryResolveStoredQualityEvidence(CandidateQualityEvidence evidence,
        out CandidateEvaluationProfileDefinition profile)
    {
        if (GeneralChat.MatchesQualitySuiteIdentity(evidence))
        {
            profile = GeneralChat;
            return true;
        }
        if (GeneralChatV2Historical.MatchesQualitySuiteIdentity(evidence))
        {
            profile = GeneralChatV2Historical;
            return true;
        }
        if (GeneralChatV1Historical.MatchesQualitySuiteIdentity(evidence))
        {
            profile = GeneralChatV1Historical;
            return true;
        }
        profile = evidence.Capability == ChatInstructionFollowingSmoke.Capability
            ? ChatInstructionFollowingSmoke : GeneralChat;
        return evidence.Capability == ChatInstructionFollowingSmoke.Capability;
    }

    internal static bool TryResolveQualityEvidence(CandidateQualityEvidence evidence,
        out CandidateEvaluationProfileDefinition profile)
    {
        profile = evidence.Capability == GeneralChat.Capability ? GeneralChat : ChatInstructionFollowingSmoke;
        return evidence.Capability == profile.Capability
            && profile.MatchesQualitySuite(evidence)
            && profile.MatchesQualityProfile(evidence);
    }

    internal static bool TryResolveQualityProfileId(string? profileId,
        out CandidateEvaluationProfileDefinition profile)
    {
        profile = profileId == GeneralChat.QualityProfileId ? GeneralChat : ChatInstructionFollowingSmoke;
        return profileId == profile.QualityProfileId;
    }

    internal static bool TryResolveQualityWorkload(string? workloadFingerprint,
        out CandidateEvaluationProfileDefinition profile)
    {
        profile = workloadFingerprint == GeneralChat.QualityWorkloadFingerprint
            ? GeneralChat : ChatInstructionFollowingSmoke;
        return workloadFingerprint == profile.QualityWorkloadFingerprint;
    }

    internal static CandidateEvaluationEnvironment WithQualityWorkload(
        CandidateEvaluationEnvironment environment, CandidateEvaluationProfileDefinition profile) => new()
    {
        HardwareFingerprint = environment.HardwareFingerprint,
        RuntimeFingerprint = environment.RuntimeFingerprint,
        BinaryFingerprint = environment.BinaryFingerprint,
        ConfigurationFingerprint = environment.ConfigurationFingerprint,
        WorkloadFingerprint = profile.QualityWorkloadFingerprint
    };

    internal static bool TryResolve(CandidateUtilityPolicyDefinition policy,
        out CandidateEvaluationProfileDefinition profile)
    {
        profile = policy.Capability == GeneralChat.Capability ? GeneralChat : ChatInstructionFollowingSmoke;
        return profile.MatchesPolicy(policy);
    }

    internal static bool TryResolveCapability(string? capability,
        out CandidateEvaluationProfileDefinition profile)
    {
        profile = capability == GeneralChat.Capability ? GeneralChat : ChatInstructionFollowingSmoke;
        return capability == profile.Capability;
    }
}
