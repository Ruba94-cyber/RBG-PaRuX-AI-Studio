using System.Globalization;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal enum CandidateUtilityComparisonDisposition
{
    NoWinner,
    CandidateMeetsReferenceMargin
}

internal sealed record CandidateUtilityComparisonResult(
    CandidateUtilityComparisonDisposition Disposition,
    string ReasonCode);

/// <summary>Exact, already-collected evidence for one side of a comparison. It has no route or state-change authority.</summary>
internal sealed record CandidateUtilityComparisonSubject(
    CandidateEvidenceIdentity Identity,
    CandidateQualityReliabilityCohort Quality,
    CandidatePerformanceEvidence? Performance,
    bool PerformanceAccountingComplete);

/// <summary>Pure fail-closed comparison. A positive result means only that the explicit policy margin is met.</summary>
internal static class CandidateUtilityComparisonEvaluator
{
    private const double MaximumComparableMetric = 1e20;

    internal static CandidateUtilityComparisonResult Compare(CandidateUtilityPolicyDefinition? policy,
        CandidateUtilityComparisonSubject? candidate, CandidateUtilityComparisonSubject? referenceCandidate)
    {
        CandidateUtilityPolicyValidation policyValidation = CandidateUtilityPolicyValidator.Validate(policy);
        if (policyValidation.Readiness != CandidateUtilityPolicyReadiness.WellFormed)
            return NoWinner(policyValidation.ReasonCode);
        if (candidate is null || referenceCandidate is null)
            return NoWinner("candidate_or_reference_evidence_missing");
        if (!CandidateEvidenceStore.ValidIdentity(candidate.Identity)
            || !CandidateEvidenceStore.ValidIdentity(referenceCandidate.Identity)
            || candidate.Identity.CandidateKey == referenceCandidate.Identity.CandidateKey
            || candidate.Identity.LocalSha256 == referenceCandidate.Identity.LocalSha256)
            return NoWinner("candidate_and_reference_are_not_distinct");

        string? candidateQualityFailure = QualityFailure(candidate, policy!);
        string? referenceQualityFailure = QualityFailure(referenceCandidate, policy!);
        if (candidateQualityFailure is not null || referenceQualityFailure is not null)
            return NoWinner(candidateQualityFailure ?? referenceQualityFailure!);
        // Speed can never buy back task quality: a challenger that clears the floor but is measurably less
        // reliable than the incumbent is not an improvement, however fast it is.
        if ((candidate.Quality.ReliabilityBasisPoints ?? 0) < (referenceCandidate.Quality.ReliabilityBasisPoints ?? 0))
            return NoWinner("candidate_quality_below_incumbent");
        if (!PerformanceReady(candidate, policy!) || !PerformanceReady(referenceCandidate, policy!))
            return NoWinner("performance_evidence_incomplete_or_incomparable");

        // completion_ms of a length-capped sample is right-censored (the answer was cut at max_tokens), so
        // it can never support a completion-latency win or loss against a naturally finished sample.
        if (candidate.Performance!.Samples.Any(sample => sample.FinishReason != "stop")
            || referenceCandidate.Performance!.Samples.Any(sample => sample.FinishReason != "stop"))
            return NoWinner("completion_latency_censored");

        double candidateMetric = Metric(candidate.Performance!, policy!.PerformanceMetricId, policy.PerformanceMetricUnit);
        double referenceMetric = Metric(referenceCandidate.Performance!, policy.PerformanceMetricId, policy.PerformanceMetricUnit);
        double candidateTtft = Metric(candidate.Performance!, policy.TimeToFirstTokenMetricId, policy.TimeToFirstTokenMetricUnit);
        double referenceTtft = Metric(referenceCandidate.Performance!, policy.TimeToFirstTokenMetricId, policy.TimeToFirstTokenMetricUnit);
        if (!IsComparableMetric(candidateMetric) || !IsComparableMetric(referenceMetric)
            || !IsComparableMetric(candidateTtft) || !IsComparableMetric(referenceTtft))
            return NoWinner("performance_metric_invalid_or_out_of_range");

        // Bound the decimal operands before cross-multiplication, then compare the explicit margin
        // without floating-point rounding. This remains a policy comparison, never a hidden score.
        if (!TryExactDecimal(candidateMetric, out decimal candidateValue)
            || !TryExactDecimal(referenceMetric, out decimal referenceValue)
            || !TryExactDecimal(candidateTtft, out decimal candidateTtftValue)
            || !TryExactDecimal(referenceTtft, out decimal referenceTtftValue))
            return NoWinner("performance_metric_invalid_or_out_of_range");
        decimal margin = policy.MinimumCompletionLatencyMarginBasisPoints!.Value;
        decimal ttftMargin = policy.MinimumTimeToFirstTokenMarginBasisPoints!.Value;
        bool marginMet = candidateValue * 10000m <= referenceValue * (10000m - margin)
            && candidateTtftValue * 10000m <= referenceTtftValue * (10000m - ttftMargin);
        return marginMet
            ? new(CandidateUtilityComparisonDisposition.CandidateMeetsReferenceMargin, "candidate_meets_reference_margin")
            : NoWinner("candidate_does_not_meet_reference_margin");
    }

    private static string? QualityFailure(CandidateUtilityComparisonSubject subject,
        CandidateUtilityPolicyDefinition policy)
    {
        if (!CandidateEvaluationProfileCatalog.TryResolveCapability(policy.Capability,
                out CandidateEvaluationProfileDefinition profile))
            return "unsupported_evidence_scope";
        CandidateQualityReliabilityCohort quality = subject.Quality;
        if (quality.Identity is null || !SameIdentity(quality.Identity, subject.Identity))
            return "quality_identity_mismatch";
        if (quality.Environment is null || !EnvironmentMatchesPolicy(quality.Environment, policy,
                profile.QualityWorkloadFingerprint))
            return "quality_environment_mismatch";
        if (quality.EstimatorId != policy.QualityReliabilityEstimatorId
            || quality.QualityPassRateFloorBasisPoints != policy.MinimumQualityPassRateBasisPoints)
            return "quality_policy_scope_mismatch";
        if (!quality.AccountingComplete)
            return "quality_accounting_incomplete";
        if (!quality.MeetsMinimumRunCount || quality.AttemptCount < policy.MinimumQualityRunCount)
            return "quality_insufficient_runs";
        if (quality.AttemptCount <= 0 || quality.SuccessfulRunCount is < 0
            || quality.SuccessfulRunCount > quality.AttemptCount
            || quality.ReliabilityBasisPoints is < 0 or > 10000
            || quality.ReliabilityBasisPoints != (int)((long)quality.SuccessfulRunCount * 10000 / quality.AttemptCount))
            return "quality_reliability_invalid";
        return quality.ReliabilityBasisPoints >= policy.MinimumQualityReliabilityBasisPoints
            ? null : "quality_below_reliability_floor";
    }

    private static bool PerformanceReady(CandidateUtilityComparisonSubject subject,
        CandidateUtilityPolicyDefinition policy)
    {
        if (!CandidateEvaluationProfileCatalog.TryResolveCapability(policy.Capability,
                out CandidateEvaluationProfileDefinition profile))
            return false;
        CandidatePerformanceEvidence? evidence = subject.Performance;
        if (!subject.PerformanceAccountingComplete || evidence is null) return false;
        try { CandidateEvidenceStore.Validate(evidence); }
        catch (InvalidDataException) { return false; }
        return evidence.RecordVersion == 3 && SameIdentity(evidence.Identity, subject.Identity)
            && profile.MatchesPerformance(evidence)
            && evidence.Environment is not null
            && EnvironmentMatchesPolicy(evidence.Environment, policy,
                profile.PerformanceWorkloadFingerprint)
            && evidence.ExecutionStatus == "Completed" && evidence.Completed && !evidence.Cancelled
            && !evidence.Truncated && !evidence.ExternalFallbackUsed
            && evidence.ExpectedRepetitions == profile.PerformanceExpectedRepetitions
            && evidence.Repetitions == evidence.ExpectedRepetitions
            && evidence.Samples.Count == evidence.ExpectedRepetitions;
    }

    private static bool EnvironmentMatchesPolicy(CandidateEvaluationEnvironment environment,
        CandidateUtilityPolicyDefinition policy, string workloadFingerprint) =>
        CandidateEvidenceStore.ValidEnvironment(environment)
        && environment.HardwareFingerprint == policy.HardwareFingerprint
        && environment.RuntimeFingerprint == policy.RuntimeFingerprint
        && environment.BinaryFingerprint == policy.BinaryFingerprint
        && environment.ConfigurationFingerprint == policy.ConfigurationFingerprint
        && environment.WorkloadFingerprint == workloadFingerprint;

    private static double Metric(CandidatePerformanceEvidence evidence, string metricId, string unit)
    {
        CandidateMeasuredMetric[] matches = evidence.Metrics
            .Where(metric => metric.Name == metricId && metric.Unit == unit).ToArray();
        return matches.Length == 1 ? matches[0].Value : double.NaN;
    }

    private static bool IsComparableMetric(double value) => double.IsFinite(value)
        && value > 0 && value <= MaximumComparableMetric;

    private static bool TryExactDecimal(double metric, out decimal value)
    {
        value = 0;
        string roundTripText = metric.ToString("R", CultureInfo.InvariantCulture);
        return decimal.TryParse(roundTripText, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && value > 0 && (double)value == metric;
    }

    private static bool SameIdentity(CandidateEvidenceIdentity a, CandidateEvidenceIdentity b) =>
        a.Provider == b.Provider && a.CandidateKey == b.CandidateKey && a.RepoId == b.RepoId
        && a.Revision == b.Revision && a.RepoFilePath == b.RepoFilePath
        && a.PinnedSourceUrl == b.PinnedSourceUrl && a.ManagedFileName == b.ManagedFileName
        && a.SizeBytes == b.SizeBytes && a.LocalSha256 == b.LocalSha256;

    private static CandidateUtilityComparisonResult NoWinner(string reasonCode) =>
        new(CandidateUtilityComparisonDisposition.NoWinner, reasonCode);
}
