using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateUtilityPolicyValidatorTests
{
    [Fact]
    public void MissingPolicyFailsClosedAsNotConfigured()
    {
        CandidateUtilityPolicyValidation result = CandidateUtilityPolicyValidator.Validate(null);

        Assert.Equal(CandidateUtilityPolicyReadiness.NotConfigured, result.Readiness);
        Assert.Equal("policy_not_configured", result.ReasonCode);
    }

    [Fact]
    public void CompleteExplicitPolicyIsOnlyWellFormedNotARecommendation()
    {
        CandidateUtilityPolicyValidation result = CandidateUtilityPolicyValidator.Validate(CreatePolicy());

        Assert.Equal(CandidateUtilityPolicyReadiness.WellFormed, result.Readiness);
        Assert.Equal("policy_contract_well_formed", result.ReasonCode);

        CandidateUtilityPolicyDefinition lowerIsBetter = CreatePolicy();
        lowerIsBetter.PerformanceMetricId = CandidatePerformanceMetric.CompletionMillisecondsId;
        lowerIsBetter.PerformanceMetricUnit = CandidatePerformanceMetric.MillisecondsUnit;
        lowerIsBetter.HigherPerformanceMetricIsBetter = false;
        Assert.Equal(CandidateUtilityPolicyReadiness.WellFormed,
            CandidateUtilityPolicyValidator.Validate(lowerIsBetter).Readiness);
    }

    [Fact]
    public void UnsupportedSchemaAndEvidenceScopesCannotBeUsed()
    {
        CandidateUtilityPolicyDefinition policy = CreatePolicy();
        policy.SchemaVersion = 1;
        Assert.Equal(CandidateUtilityPolicyReadiness.UnsupportedVersion,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.SchemaVersion++;
        Assert.Equal(CandidateUtilityPolicyReadiness.UnsupportedVersion,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.Capability = "coding";
        Assert.Equal(CandidateUtilityPolicyReadiness.UnsupportedScope,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);
    }

    [Fact]
    public void EveryPolicyScopeFieldMustMatchTheSingleSupportedProfile()
    {
        Action<CandidateUtilityPolicyDefinition>[] mismatches =
        [
            p => p.Capability = "chat.other",
            p => p.QualitySuiteId = "parux.other.suite",
            p => p.QualitySuiteVersion++,
            p => p.QualityEvidenceProfileId = "parux.other.profile",
            p => p.QualityEvidenceProfileVersion++,
            p => p.QualityEvidenceProfileSha256 = new string('0', 64),
            p => p.PerformanceWorkloadId = "parux.other.workload",
            p => p.PerformanceWorkloadVersion++
        ];

        foreach (Action<CandidateUtilityPolicyDefinition> mismatch in mismatches)
        {
            CandidateUtilityPolicyDefinition policy = CreatePolicy();
            mismatch(policy);
            CandidateUtilityPolicyValidation result = CandidateUtilityPolicyValidator.Validate(policy);
            Assert.Equal(CandidateUtilityPolicyReadiness.UnsupportedScope, result.Readiness);
            Assert.Equal("unsupported_evidence_scope", result.ReasonCode);
        }
    }

    [Fact]
    public void MissingFloorsMarginOrReliabilityRunCountFailClosed()
    {
        CandidateUtilityPolicyDefinition policy = CreatePolicy();
        policy.MinimumQualityPassRateBasisPoints = null;
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.MinimumCompletionLatencyMarginBasisPoints = 0;
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.MinimumQualityRunCount = 1;
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.MinimumQualityRunCount = CandidateEvidenceStore.MaximumRecordsPerKind - 1;
        Assert.Equal(CandidateUtilityPolicyReadiness.WellFormed,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy.MinimumQualityRunCount = CandidateEvidenceStore.MaximumRecordsPerKind;
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.QualityReliabilityEstimatorId = "unknown-estimator";
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.QualityReliabilityEstimatorId = string.Empty;
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.MinimumTimeToFirstTokenMarginBasisPoints = null;
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid, CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.MinimumCompletionLatencyMarginBasisPoints = null;
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid, CandidateUtilityPolicyValidator.Validate(policy).Readiness);
    }

    [Fact]
    public void MalformedPolicyTokenAndMetricCannotBecomeAnActivePolicy()
    {
        CandidateUtilityPolicyDefinition policy = CreatePolicy();
        policy.PolicyId = "invalid policy id";
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.PerformanceMetricUnit = "tokens/sec";
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.PerformanceMetricId = "unsupported_metric";
        policy.PerformanceMetricUnit = "widget";
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.HigherPerformanceMetricIsBetter = true;
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

    }

    [Fact]
    public void PolicyMustBeBoundToKnownEnvironmentFingerprints()
    {
        CandidateUtilityPolicyDefinition policy = CreatePolicy();
        policy.HardwareFingerprint = "unknown";
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy = CreatePolicy();
        policy.RuntimeFingerprint = "runtime:current-123";
        Assert.Equal(CandidateUtilityPolicyReadiness.WellFormed,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);
    }

    [Fact]
    public void GeneralChatScopeRequiresItsPredeclaredQualityReliabilityAndFreshRunFloors()
    {
        CandidateUtilityPolicyDefinition policy = CreatePolicy();
        CandidateEvaluationProfileDefinition profile = CandidateEvaluationProfileCatalog.GeneralChat;
        policy.PolicyId = "parux.utility.chat-general-balanced.explicit.v1";
        policy.Capability = profile.Capability;
        policy.QualitySuiteId = profile.QualitySuiteId;
        policy.QualitySuiteVersion = profile.QualitySuiteVersion;
        policy.QualityEvidenceProfileId = profile.QualityProfileId;
        policy.QualityEvidenceProfileVersion = profile.QualityProfileVersion;
        policy.QualityEvidenceProfileSha256 = profile.QualityProfileSha256;
        policy.PerformanceWorkloadId = profile.PerformanceWorkloadId;
        policy.PerformanceWorkloadVersion = profile.PerformanceWorkloadVersion;
        policy.MinimumQualityPassRateBasisPoints = 9000;
        policy.MinimumQualityReliabilityBasisPoints = 9500;
        policy.MinimumQualityRunCount = 5;
        Assert.Equal(CandidateUtilityPolicyReadiness.WellFormed,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        policy.MinimumQualityPassRateBasisPoints = 8999;
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);
        policy.MinimumQualityPassRateBasisPoints = 9000;
        policy.MinimumQualityReliabilityBasisPoints = 9499;
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);
        policy.MinimumQualityReliabilityBasisPoints = 9500;
        policy.MinimumQualityRunCount = 4;
        Assert.Equal(CandidateUtilityPolicyReadiness.Invalid,
            CandidateUtilityPolicyValidator.Validate(policy).Readiness);

        Assert.Equal("standard", profile.RouteRole);
        Assert.Equal("balanced", profile.AutoProfile);
    }

    // Synthetic calibration values exercise schema validation only; they are not PaRuX defaults.
    private static CandidateUtilityPolicyDefinition CreatePolicy() => new()
    {
        PolicyId = "parux.utility.chat-smoke.fast.v1",
        Capability = CandidateEvaluationScope.ChatInstructionFollowingCapability,
        QualitySuiteId = CandidateEvaluationScope.ChatInstructionFollowingSuiteId,
        QualitySuiteVersion = CandidateEvaluationScope.ChatInstructionFollowingSuiteVersion,
        QualityEvidenceProfileId = CandidateTextQualitySuite.ProfileId,
        QualityEvidenceProfileVersion = CandidateTextQualitySuite.ProfileVersion,
        QualityEvidenceProfileSha256 = CandidateTextQualitySuite.ProfileSha256,
        PerformanceWorkloadId = CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId,
        PerformanceWorkloadVersion = CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadVersion,
        HardwareFingerprint = "sha256:synthetic-hardware",
        RuntimeFingerprint = "sha256:synthetic-runtime",
        BinaryFingerprint = "sha256:synthetic-parux-build",
        ConfigurationFingerprint = "sha256:synthetic-runtime-config",
        MinimumQualityPassRateBasisPoints = 9500,
        MinimumQualityReliabilityBasisPoints = 9500,
        QualityReliabilityEstimatorId = CandidateQualityReliabilityEstimator.FreshProcessSuitePassRateV1,
        MinimumQualityRunCount = 3,
        PerformanceMetricId = CandidatePerformanceMetric.CompletionMillisecondsId,
        PerformanceMetricUnit = CandidatePerformanceMetric.MillisecondsUnit,
        HigherPerformanceMetricIsBetter = false,
        MinimumUtilityMarginBasisPoints = null,
        MinimumCompletionLatencyMarginBasisPoints = 500,
        TimeToFirstTokenMetricId = CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId,
        TimeToFirstTokenMetricUnit = CandidatePerformanceMetric.MillisecondsUnit,
        MinimumTimeToFirstTokenMarginBasisPoints = 500
    };
}
