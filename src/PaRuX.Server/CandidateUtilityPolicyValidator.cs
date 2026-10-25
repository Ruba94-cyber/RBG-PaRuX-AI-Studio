using PaRuX.Contracts;

namespace PaRuX.Server;

internal enum CandidateUtilityPolicyReadiness
{
    NotConfigured,
    UnsupportedVersion,
    UnsupportedScope,
    Invalid,
    WellFormed
}

internal readonly record struct CandidateUtilityPolicyValidation(
    CandidateUtilityPolicyReadiness Readiness,
    string ReasonCode);

/// <summary>Checks policy completeness only; it does not rank models or assert that evidence exists.</summary>
internal static class CandidateUtilityPolicyValidator
{
    internal static CandidateUtilityPolicyValidation Validate(CandidateUtilityPolicyDefinition? policy)
    {
        if (policy is null)
            return new(CandidateUtilityPolicyReadiness.NotConfigured, "policy_not_configured");

        if (policy.SchemaVersion != CandidateUtilityPolicyDefinition.CurrentSchemaVersion)
            return new(CandidateUtilityPolicyReadiness.UnsupportedVersion, "unsupported_policy_schema");

        if (!CandidateEvaluationProfileCatalog.TryResolve(policy, out CandidateEvaluationProfileDefinition profile))
            return new(CandidateUtilityPolicyReadiness.UnsupportedScope, "unsupported_evidence_scope");

        if (!ValidToken(policy.PolicyId)
            || !ValidFingerprint(policy.HardwareFingerprint)
            || !ValidFingerprint(policy.RuntimeFingerprint)
            || !ValidFingerprint(policy.BinaryFingerprint)
            || !ValidFingerprint(policy.ConfigurationFingerprint)
            || !ValidToken(policy.PerformanceMetricId)
            || string.IsNullOrWhiteSpace(policy.PerformanceMetricUnit)
            || policy.QualitySuiteVersion is <= 0 || policy.QualitySuiteVersion > profile.MaximumScopeVersion
            || policy.QualityEvidenceProfileVersion is <= 0 || policy.QualityEvidenceProfileVersion > profile.MaximumScopeVersion
            || ModelDiscoveryIdentity.NormalizeSha256(policy.QualityEvidenceProfileSha256)
                != policy.QualityEvidenceProfileSha256
            || policy.PerformanceWorkloadVersion is <= 0 || policy.PerformanceWorkloadVersion > profile.MaximumScopeVersion
            || policy.MinimumQualityPassRateBasisPoints is null or < 1 or > 10000
            || policy.MinimumQualityReliabilityBasisPoints is null or < 1 or > 10000
            || policy.MinimumQualityPassRateBasisPoints < profile.MinimumPolicyPassRateBasisPoints
            || policy.MinimumQualityReliabilityBasisPoints < profile.MinimumPolicyReliabilityBasisPoints
            || policy.QualityReliabilityEstimatorId != CandidateQualityReliabilityEstimator.FreshProcessSuitePassRateV1
            || policy.MinimumQualityRunCount is null or < 2
            || policy.MinimumQualityRunCount < profile.MinimumPolicyRunCount
            || policy.MinimumQualityRunCount > profile.MaximumQualityRunCount
            || policy.HigherPerformanceMetricIsBetter is null
            || policy.MinimumCompletionLatencyMarginBasisPoints is null or < 1
            || policy.MinimumCompletionLatencyMarginBasisPoints > profile.MaximumUtilityMarginBasisPoints
            || policy.TimeToFirstTokenMetricId != CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId
            || policy.TimeToFirstTokenMetricUnit != CandidatePerformanceMetric.MillisecondsUnit
            || policy.MinimumTimeToFirstTokenMarginBasisPoints is null or < 1
            || policy.MinimumTimeToFirstTokenMarginBasisPoints > profile.MaximumUtilityMarginBasisPoints
            || policy.PerformanceMetricId != CandidatePerformanceMetric.CompletionMillisecondsId
            || policy.PerformanceMetricUnit != CandidatePerformanceMetric.MillisecondsUnit
            || policy.HigherPerformanceMetricIsBetter != false
            || !IsSupportedMetric(policy))
            return new(CandidateUtilityPolicyReadiness.Invalid, "incomplete_or_invalid_policy");

        return new(CandidateUtilityPolicyReadiness.WellFormed, "policy_contract_well_formed");
    }

    private static bool ValidToken(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool ValidFingerprint(string? value) => value is { Length: > 0 and <= 256 }
        && !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "unavailable", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "n/a", StringComparison.OrdinalIgnoreCase)
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static bool IsSupportedMetric(CandidateUtilityPolicyDefinition policy) =>
        policy.TimeToFirstTokenMetricId == CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId
        && policy.TimeToFirstTokenMetricUnit == CandidatePerformanceMetric.MillisecondsUnit;
}
