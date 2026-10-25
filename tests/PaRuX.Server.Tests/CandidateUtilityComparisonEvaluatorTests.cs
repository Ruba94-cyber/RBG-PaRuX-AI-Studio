using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateUtilityComparisonEvaluatorTests
{
    [Fact]
    public void BothCompletionAndTtftMarginsMustPass()
    {
        CandidateUtilityComparisonSubject candidate = Subject(Identity("candidate"), 10000, 427.5);
        CandidateUtilityComparisonSubject reference = Subject(Identity("reference"), 10000, 450);
        Completion(candidate.Performance!, 427.5);
        Completion(reference.Performance!, 450);
        Ttft(candidate.Performance!, 90);
        Ttft(reference.Performance!, 100);
        CandidateUtilityComparisonResult result = CandidateUtilityComparisonEvaluator.Compare(Policy(), candidate, reference);

        Assert.Equal("candidate_meets_reference_margin", result.ReasonCode);
        Assert.Equal(CandidateUtilityComparisonDisposition.CandidateMeetsReferenceMargin, result.Disposition);
    }

    [Theory]
    [InlineData(427.5, true)]
    [InlineData(427.51, false)]
    public void CompletionLatencyUsesAnExactLowerIsBetterBoundary(double candidateMilliseconds, bool expectedWinner)
    {
        CandidateUtilityComparisonSubject candidate = Subject(Identity("candidate"), 10000, candidateMilliseconds);
        CandidateUtilityComparisonSubject reference = Subject(Identity("reference"), 10000, 450);
        Completion(candidate.Performance!, candidateMilliseconds);
        Completion(reference.Performance!, 450);
        Ttft(candidate.Performance!, 90);
        Ttft(reference.Performance!, 100);
        CandidateUtilityComparisonResult result = CandidateUtilityComparisonEvaluator.Compare(Policy(), candidate, reference);

        Assert.Equal(expectedWinner ? "candidate_meets_reference_margin" : "candidate_does_not_meet_reference_margin",
            result.ReasonCode);
        Assert.Equal(expectedWinner, result.Disposition == CandidateUtilityComparisonDisposition.CandidateMeetsReferenceMargin);
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(95.01, false)]
    public void TtftLatencyUsesAnExactLowerIsBetterBoundary(double candidateTtft, bool expectedWinner)
    {
        CandidateUtilityComparisonSubject candidate = Subject(Identity("candidate"), 10000, 427.5);
        CandidateUtilityComparisonSubject reference = Subject(Identity("reference"), 10000, 450);
        Completion(candidate.Performance!, 427.5); Completion(reference.Performance!, 450);
        Ttft(candidate.Performance!, candidateTtft); Ttft(reference.Performance!, 100);
        CandidateUtilityComparisonResult result = CandidateUtilityComparisonEvaluator.Compare(Policy(), candidate, reference);
        Assert.Equal(expectedWinner, result.Disposition == CandidateUtilityComparisonDisposition.CandidateMeetsReferenceMargin);
    }

    [Fact]
    public void EqualMetricsBelowExactDecimalRangeCannotProduceAFalseMarginWin()
    {
        CandidateUtilityComparisonSubject candidate = Subject(Identity("candidate"), 10000, 1e-30);
        CandidateUtilityComparisonSubject reference = Subject(Identity("reference"), 10000, 1e-30);
        Ttft(candidate.Performance!, 1e-30);
        Ttft(reference.Performance!, 1e-30);

        CandidateUtilityComparisonResult result = CandidateUtilityComparisonEvaluator.Compare(
            Policy(), candidate, reference);

        Assert.Equal(CandidateUtilityComparisonDisposition.NoWinner, result.Disposition);
        Assert.Equal("performance_metric_invalid_or_out_of_range", result.ReasonCode);
    }

    [Theory]
    [InlineData("quality-accounting", "quality_accounting_incomplete")]
    [InlineData("quality-scope", "quality_policy_scope_mismatch")]
    [InlineData("performance-accounting", "performance_evidence_incomplete_or_incomparable")]
    [InlineData("same-model", "candidate_and_reference_are_not_distinct")]
    public void UnknownIncompleteOrAliasedEvidenceProducesNoWinner(string defect, string reason)
    {
        CandidateUtilityComparisonSubject candidate = Subject(Identity("candidate"), 10000, 100);
        CandidateUtilityComparisonSubject referenceCandidate = Subject(Identity("reference"), 10000, 100);
        CandidateUtilityPolicyDefinition policy = Policy();
        switch (defect)
        {
            case "quality-accounting":
                candidate = candidate with { Quality = candidate.Quality with { AccountingComplete = false } };
                break;
            case "quality-scope":
                candidate = candidate with { Quality = candidate.Quality with
                    { QualityPassRateFloorBasisPoints = 9000 } };
                break;
            case "performance-accounting":
                referenceCandidate = referenceCandidate with { PerformanceAccountingComplete = false };
                break;
            case "same-model":
                referenceCandidate = Subject(candidate.Identity, 10000, 100);
                break;
        }

        CandidateUtilityComparisonResult result = CandidateUtilityComparisonEvaluator.Compare(
            policy, candidate, referenceCandidate);

        Assert.Equal(CandidateUtilityComparisonDisposition.NoWinner, result.Disposition);
        Assert.Equal(reason, result.ReasonCode);
    }

    [Fact]
    public void MissingOrUnsupportedPolicyCannotProduceWinner()
    {
        CandidateUtilityComparisonSubject candidate = Subject(Identity("candidate"), 10000, 100);
        CandidateUtilityComparisonSubject referenceCandidate = Subject(Identity("reference"), 10000, 100);

        CandidateUtilityComparisonResult missing = CandidateUtilityComparisonEvaluator.Compare(null,
            candidate, referenceCandidate);
        CandidateUtilityPolicyDefinition unsupported = Policy();
        unsupported.QualityEvidenceProfileSha256 = new string('0', 64);
        CandidateUtilityComparisonResult mismatched = CandidateUtilityComparisonEvaluator.Compare(unsupported,
            candidate, referenceCandidate);

        Assert.Equal(CandidateUtilityComparisonDisposition.NoWinner, missing.Disposition);
        Assert.Equal("policy_not_configured", missing.ReasonCode);
        Assert.Equal(CandidateUtilityComparisonDisposition.NoWinner, mismatched.Disposition);
        Assert.Equal("unsupported_evidence_scope", mismatched.ReasonCode);
    }

    [Fact]
    public void WrongPerformanceWorkloadCannotJoinAChatSmokeComparison()
    {
        CandidateUtilityComparisonSubject candidate = Subject(Identity("candidate"), 10000, 105);
        CandidateUtilityComparisonSubject referenceCandidate = Subject(Identity("reference"), 10000, 100);
        candidate.Performance!.WorkloadId = "parux.other.workload";

        CandidateUtilityComparisonResult result = CandidateUtilityComparisonEvaluator.Compare(
            Policy(), candidate, referenceCandidate);

        Assert.Equal(CandidateUtilityComparisonDisposition.NoWinner, result.Disposition);
        Assert.Equal("performance_evidence_incomplete_or_incomparable", result.ReasonCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LengthCappedSamplesOnEitherSideAreCensoredAndCannotWin(bool candidateCapped)
    {
        CandidateUtilityComparisonSubject candidate = Subject(Identity("candidate"), 10000, 300);
        CandidateUtilityComparisonSubject reference = Subject(Identity("reference"), 10000, 450);
        Completion(candidate.Performance!, 300); Completion(reference.Performance!, 450);
        Ttft(candidate.Performance!, 50); Ttft(reference.Performance!, 100);
        CandidatePerformanceEvidence capped = (candidateCapped ? candidate : reference).Performance!;
        capped.Samples[1].FinishReason = "length";
        capped.Samples[1].GeneratedTokens = CandidateTextPerformanceWorkload.MaxTokens;
        capped.TokenCount = capped.Samples.Sum(s => (long)s.GeneratedTokens);
        capped.Metrics.Single(m => m.Name == CandidatePerformanceMetric.DecodeTokensPerSecondId).Value =
            1000d * capped.TokenCount / capped.Samples.Sum(s => s.DecodeMilliseconds);

        CandidateUtilityComparisonResult result = CandidateUtilityComparisonEvaluator.Compare(Policy(), candidate, reference);

        Assert.Equal(CandidateUtilityComparisonDisposition.NoWinner, result.Disposition);
        Assert.Equal("completion_latency_censored", result.ReasonCode);
    }

    [Fact]
    public void FasterCandidateThatClearsTheFloorButIsLessReliableThanTheIncumbentCannotWin()
    {
        CandidateUtilityComparisonSubject candidate = Subject(Identity("candidate"), 9500, 200);
        CandidateUtilityComparisonSubject reference = Subject(Identity("reference"), 10000, 450);
        Ttft(candidate.Performance!, 40);
        Ttft(reference.Performance!, 100);

        CandidateUtilityComparisonResult result = CandidateUtilityComparisonEvaluator.Compare(Policy(), candidate, reference);

        Assert.Equal(CandidateUtilityComparisonDisposition.NoWinner, result.Disposition);
        Assert.Equal("candidate_quality_below_incumbent", result.ReasonCode);
    }

    private static CandidateUtilityComparisonSubject Subject(CandidateEvidenceIdentity identity,
        int reliabilityBasisPoints, double completion)
    {
        CandidateQualityReliabilityCohort quality = new(
            CandidateQualityReliabilityEstimator.FreshProcessSuitePassRateV1,
            AttemptCount: 100,
            SuccessfulRunCount: reliabilityBasisPoints * 100 / 10000,
            ReliabilityBasisPoints: reliabilityBasisPoints,
            AccountingComplete: true,
            MeetsMinimumRunCount: true,
            Identity: identity,
            Environment: CandidateEvidenceServiceTests.Environment(),
            QualityPassRateFloorBasisPoints: 9500);
        CandidatePerformanceEvidence performance = CandidateEvidenceServiceTests.Performance(identity);
        Completion(performance, completion);
        return new(identity, quality, performance, PerformanceAccountingComplete: true);
    }

    private static CandidatePerformanceEvidence Completion(CandidatePerformanceEvidence evidence, double value)
    {
        foreach (CandidatePerformanceSample sample in evidence.Samples)
            sample.CompletionMilliseconds = value;
        evidence.Metrics.Single(metric => metric.Name == CandidatePerformanceMetric.CompletionMillisecondsId)
            .Value = evidence.Samples.Average(sample => sample.CompletionMilliseconds);
        return evidence;
    }

    private static CandidatePerformanceEvidence Ttft(CandidatePerformanceEvidence evidence, double value)
    {
        foreach (CandidatePerformanceSample sample in evidence.Samples) sample.TimeToFirstTokenMilliseconds = value;
        evidence.Metrics.Single(metric => metric.Name == CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId)
            .Value = value;
        return evidence;
    }

    private static CandidateUtilityPolicyDefinition Policy() => new()
    {
        PolicyId = "parux.utility.chat-smoke.fast.test",
        Capability = CandidateEvaluationScope.ChatInstructionFollowingCapability,
        QualitySuiteId = CandidateTextQualitySuite.Id,
        QualitySuiteVersion = CandidateTextQualitySuite.Version,
        QualityEvidenceProfileId = CandidateTextQualitySuite.ProfileId,
        QualityEvidenceProfileVersion = CandidateTextQualitySuite.ProfileVersion,
        QualityEvidenceProfileSha256 = CandidateTextQualitySuite.ProfileSha256,
        PerformanceWorkloadId = CandidateTextPerformanceWorkload.Id,
        PerformanceWorkloadVersion = CandidateTextPerformanceWorkload.Version,
        HardwareFingerprint = "gpu-1",
        RuntimeFingerprint = "text-cuda:runtime-v1",
        BinaryFingerprint = "binary-sha",
        ConfigurationFingerprint = CandidateEvidenceServiceTests.ActualLaunchConfiguration,
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

    private static CandidateEvidenceIdentity Identity(string name)
    {
        const string revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string repoId = "org/" + name;
        string repoFilePath = "weights/" + name + ".gguf";
        string managedFileName = name + ".gguf";
        return new CandidateEvidenceIdentity
        {
            Provider = ModelDiscoveryIdentity.Provider,
            CandidateKey = ModelDiscoveryIdentity.Key(repoId, revision, repoFilePath),
            RepoId = repoId,
            Revision = revision,
            RepoFilePath = repoFilePath,
            PinnedSourceUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, repoFilePath),
            ManagedFileName = managedFileName,
            SizeBytes = 1024,
            LocalSha256 = name == "candidate" ? new string('b', 64) : new string('c', 64)
        };
    }
}
