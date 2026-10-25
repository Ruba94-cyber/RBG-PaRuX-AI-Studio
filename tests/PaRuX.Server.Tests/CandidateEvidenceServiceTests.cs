using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateEvidenceServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Revision = new('a', 40);
    private static readonly string SuiteHash = CandidateTextQualitySuite.Sha256;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-candidate-evidence", Guid.NewGuid().ToString("N"));
    private readonly string _models;
    private readonly string _state;
    private readonly ModelDiscoveryCandidate _candidate;
    private readonly ModelDiscoveryCatalogStore _catalog;
    private readonly CandidateEvidenceStore _store;
    private readonly CandidateEvidenceService _service;

    public CandidateEvidenceServiceTests()
    {
        _models = Path.Combine(_root, "models");
        _state = Path.Combine(_root, "state");
        Directory.CreateDirectory(_models);
        _candidate = new ModelDiscoveryCandidate
        {
            Provider = ModelDiscoveryIdentity.Provider,
            ProviderAdapterVersion = ModelDiscoveryIdentity.AdapterVersion,
            RepoId = "org/model", Revision = Revision, RepoFilePath = "weights/model.gguf",
            Key = ModelDiscoveryIdentity.Key("org/model", Revision, "weights/model.gguf"),
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl("org/model", Revision, "weights/model.gguf"),
            DeclaredLicense = "MIT",
            FirstSeenUtc = Now, LastSeenUtc = Now
        };
        _catalog = new ModelDiscoveryCatalogStore(_state);
        _catalog.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { _candidate } });
        _store = new CandidateEvidenceStore(_state);
        _service = new CandidateEvidenceService(_catalog, _store, _models, () => Now);
        File.WriteAllText(Path.Combine(_models, "model.gguf"), "payload-1");
        WriteProvenance();
    }

    [Fact]
    public void Managed_model_read_lease_rejects_missing_models_and_unsafe_names()
    {
        const string fileName = "lease-check.gguf";
        byte[] payload = Encoding.UTF8.GetBytes("managed payload");
        string linkPath = Path.Combine(_models, fileName);
        using (FileStream? missing = _service.TryOpenManagedFileReadLease(fileName, payload.Length))
            Assert.Null(missing);
        Assert.False(_service.IsManagedFilePathCurrent("../outside.gguf", payload.Length));

        File.WriteAllBytes(linkPath, payload);
        using (FileStream? accepted = _service.TryOpenManagedFileReadLease(fileName, payload.Length))
            Assert.NotNull(accepted);

        File.Delete(linkPath);
        Assert.False(_service.IsManagedFilePathCurrent(fileName, payload.Length));
    }

    [Fact]
    public async Task Candidate_snapshot_read_lease_pins_the_exact_bytes_until_comparison_finishes()
    {
        using CandidateEvidenceIdentityReadLease? pinned = await _service.TryCaptureSnapshotReadLeaseAsync(
            _candidate.Key, "model.gguf", CancellationToken.None);
        Assert.NotNull(pinned);
        Assert.Equal((await Snapshot())!.LocalSha256, pinned.Identity.LocalSha256);

        string path = Path.Combine(_models, "model.gguf");
        Assert.ThrowsAny<IOException>(() =>
        {
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        });

        pinned.Dispose();
        File.WriteAllText(path, "payload-changed");
        Assert.Null(await _service.TryCaptureSnapshotAsync(_candidate.Key, "model.gguf", CancellationToken.None));
    }

    [Fact]
    public async Task MatchingCompleteIndependentRecordsPermitOnlyFurtherReviewAndSurviveRestart()
    {
        CandidateEvidenceIdentity snapshot = (await Snapshot())!;
        Assert.NotNull(snapshot);
        Assert.True(await _service.TryCommitQualityAsync(Quality(snapshot), CancellationToken.None));
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
        Assert.True(await _service.TryCommitPerformanceAsync(Performance(snapshot), CancellationToken.None));
        var restarted = new CandidateEvidenceService(new ModelDiscoveryCatalogStore(_state),
            new CandidateEvidenceStore(_state), _models, () => Now);
        Assert.Equal(CandidateEligibilityOutcome.EligibleForFurtherReview, await Eligibility(restarted));
    }

    [Fact]
    public async Task FinalQualityPersistenceGateRechecksGamingAndCancellation()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        Assert.False(await _service.TryCommitQualityAsync(Quality(identity), CancellationToken.None, () => true));
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.TryCommitQualityAsync(
            Quality(identity), cts.Token, () => { cts.Cancel(); return false; }));
        Assert.Empty(_store.Load().Quality);
    }

    [Fact]
    public async Task FinalPerformancePersistenceGateRechecksGamingAndCancellation()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        Assert.False(await _service.TryCommitPerformanceAsync(Performance(identity), CancellationToken.None, () => true));
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.TryCommitPerformanceAsync(
            Performance(identity), cts.Token, () => { cts.Cancel(); return false; }));
        Assert.Empty(_store.Load().Performance);
    }

    [Fact]
    public async Task LegacyPerformanceRemainsReadableButCannotQualify()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        Assert.True(await _service.TryCommitQualityAsync(Quality(identity), CancellationToken.None));
        CandidatePerformanceEvidence legacy = Performance(identity);
        legacy.RecordVersion = 1;
        Assert.True(await _service.TryCommitPerformanceAsync(legacy, CancellationToken.None));
        Assert.Single(_store.Load().Performance);
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
    }

    [Fact]
    public async Task ArbitraryPerformanceWorkloadCannotBePersisted()
    {
        CandidatePerformanceEvidence evidence = Performance((await Snapshot())!);
        evidence.WorkloadId = "invented";
        Assert.Throws<InvalidDataException>(() => CandidateEvidenceStore.Validate(evidence));
        evidence = Performance((await Snapshot())!);
        evidence.Environment.WorkloadFingerprint = "arbitrary";
        Assert.Throws<InvalidDataException>(() => CandidateEvidenceStore.Validate(evidence));
    }

    [Fact]
    public async Task LegacyV1QualityRemainsReadableButCannotAuthorizeEligibility()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        CandidateQualityEvidence legacy = Quality(identity);
        legacy.RecordVersion = 1;
        legacy.Environment = null;
        legacy.ExecutionStatus = string.Empty;
        foreach (CandidateQualityCaseResult item in legacy.Cases) item.FinishReason = string.Empty;
        Assert.True(await _service.TryCommitQualityAsync(legacy, CancellationToken.None));
        Assert.True(await _service.TryCommitPerformanceAsync(Performance(identity), CancellationToken.None));
        Assert.Single(_store.Load().Quality);
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
    }

    [Fact]
    public async Task Historical_general_chat_v1_remains_readable_but_cannot_authorize_v2_eligibility()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        CandidateQualityEvidence historical = GeneralChatQuality(identity, historicalV1: true);
        Assert.True(await _service.TryCommitQualityAsync(historical, CancellationToken.None));
        Assert.True(await _service.TryCommitPerformanceAsync(Performance(identity), CancellationToken.None));

        CandidateEvaluationProfileDefinition current = CandidateEvaluationProfileCatalog.GeneralChat;
        CandidateEvaluationEnvironment qualityEnvironment = Environment();
        qualityEnvironment.WorkloadFingerprint = current.QualityWorkloadFingerprint;
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await _service.CheckEligibilityAsync(
            _candidate.Key, "model.gguf", current.Capability, current.QualitySuiteId,
            current.QualitySuiteVersion, current.QualitySuiteSha256, qualityEnvironment,
            PerformanceEnvironment(), CancellationToken.None));
    }

    [Fact]
    public async Task InventedSuiteHashAndWrongPerformanceWorkloadCannotAuthorizeEligibility()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        Assert.True(await _service.TryCommitQualityAsync(Quality(identity), CancellationToken.None));
        Assert.True(await _service.TryCommitPerformanceAsync(Performance(identity), CancellationToken.None));
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await _service.CheckEligibilityAsync(
            _candidate.Key, "model.gguf", CandidateTextQualitySuite.Capability, CandidateTextQualitySuite.Id,
            CandidateTextQualitySuite.Version, new string('b', 64), Environment(), PerformanceEnvironment(),
            CancellationToken.None));
        CandidateEvaluationEnvironment wrongPerformance = PerformanceEnvironment();
        wrongPerformance.WorkloadFingerprint = "another-performance-workload";
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await _service.CheckEligibilityAsync(
            _candidate.Key, "model.gguf", CandidateTextQualitySuite.Capability, CandidateTextQualitySuite.Id,
            CandidateTextQualitySuite.Version, SuiteHash, Environment(), wrongPerformance,
            CancellationToken.None));
    }

    [Fact]
    public async Task QualityOnlyAndPerformanceOnlyAreIneligible()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        Assert.True(await _service.TryCommitQualityAsync(Quality(identity), CancellationToken.None));
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
        var otherStore = new CandidateEvidenceStore(Path.Combine(_root, "other-state"));
        var other = new CandidateEvidenceService(_catalog, otherStore, _models, () => Now);
        Assert.True(await other.TryCommitPerformanceAsync(Performance(identity), CancellationToken.None));
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility(other));
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("inconclusive")]
    [InlineData("error")]
    [InlineData("missing")]
    [InlineData("cancelled")]
    [InlineData("truncated")]
    [InlineData("external")]
    [InlineData("incomplete")]
    public async Task UnsafeQualityOutcomeNeverPasses(string defect)
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        CandidateQualityEvidence quality = Quality(identity);
        switch (defect)
        {
            case "fail": quality.Cases[0].Outcome = "fail"; quality.PassCount--; quality.FailCount++; break;
            case "inconclusive": quality.Cases[0].Outcome = "inconclusive"; quality.PassCount--; quality.InconclusiveCount++; break;
            case "error": quality.Cases[0].Outcome = "error"; quality.PassCount--; quality.ErrorCount++; break;
            case "missing": quality.Cases.RemoveAt(quality.Cases.Count - 1); quality.PassCount--; quality.Completed = false; quality.ExecutionStatus = "Failed"; break;
            case "cancelled": quality.Cancelled = true; quality.Completed = false; quality.ExecutionStatus = "Cancelled"; break;
            case "truncated": quality.Truncated = true; break;
            case "external": quality.ExternalFallbackUsed = true; break;
            case "incomplete": quality.Completed = false; quality.ExecutionStatus = "Failed"; break;
        }
        if (defect == "external") Assert.Throws<InvalidDataException>(() => CandidateEvidenceStore.Validate(quality));
        else Assert.True(await _service.TryCommitQualityAsync(quality, CancellationToken.None));
        Assert.True(await _service.TryCommitPerformanceAsync(Performance(identity), CancellationToken.None));
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("truncated")]
    [InlineData("external")]
    [InlineData("incomplete")]
    public async Task PartialPerformanceNeverPasses(string defect)
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        CandidatePerformanceEvidence performance = Performance(identity);
        switch (defect)
        {
            case "cancelled": performance.Cancelled = true; performance.Completed = false; performance.ExecutionStatus = "Cancelled"; break;
            case "truncated": performance.Truncated = true; break;
            case "external": performance.ExternalFallbackUsed = true; break;
            case "incomplete": performance.Completed = false; performance.ExecutionStatus = "Failed"; break;
        }
        Assert.True(await _service.TryCommitQualityAsync(Quality(identity), CancellationToken.None));
        if (defect is "external" or "truncated")
            Assert.Throws<InvalidDataException>(() => CandidateEvidenceStore.Validate(performance));
        else Assert.True(await _service.TryCommitPerformanceAsync(performance, CancellationToken.None));
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
    }

    [Fact]
    public async Task NewerFailedEvidenceSupersedesPriorPassingEvidence()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        Assert.True(await _service.TryCommitQualityAsync(Quality(identity), CancellationToken.None));
        Assert.True(await _service.TryCommitPerformanceAsync(Performance(identity), CancellationToken.None));
        Assert.Equal(CandidateEligibilityOutcome.EligibleForFurtherReview, await Eligibility());
        CandidateQualityEvidence failed = Quality(identity);
        failed.Cases[0].Outcome = "fail";
        failed.PassCount--;
        failed.FailCount = 1;
        Assert.True(await _service.TryCommitQualityAsync(failed, CancellationToken.None));
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
    }

    [Fact]
    public async Task ReliabilityCohortCountsUniqueFailedAttemptsAndRequiresExactThreshold()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        _store.AddQuality(Quality(identity));
        CandidateQualityEvidence failed = Quality(identity);
        failed.Cases[0].Outcome = "fail";
        failed.PassCount--;
        failed.FailCount++;
        _store.AddQuality(failed);

        CandidateQualityReliabilityCohort cohort = _service.BuildQualityReliabilityCohort(
            identity, Environment(), minimumQualityPassRateBasisPoints: 7500, minimumQualityRunCount: 2);

        Assert.Equal(CandidateQualityReliabilityEstimator.FreshProcessSuitePassRateV1, cohort.EstimatorId);
        Assert.Equal(2, cohort.AttemptCount);
        Assert.Equal(1, cohort.SuccessfulRunCount); // critical-case failure is never a success
        Assert.Equal(5000, cohort.ReliabilityBasisPoints);
        Assert.True(cohort.AccountingComplete);
        Assert.True(cohort.MeetsMinimumRunCount);
    }

    [Fact]
    public async Task LegacyOrUnscopedAttemptMakesReliabilityAccountingIncomplete()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        _store.AddQuality(Quality(identity));
        _store.AddQuality(Quality(identity));
        CandidateQualityEvidence legacy = Quality(identity);
        legacy.RecordVersion = 2;
        legacy.InvocationId = string.Empty;
        legacy.QualityProfileId = string.Empty;
        legacy.QualityProfileVersion = 0;
        legacy.QualityProfileSha256 = string.Empty;
        _store.AddQuality(legacy);

        CandidateQualityReliabilityCohort withLegacy = _service.BuildQualityReliabilityCohort(
            identity, Environment(), minimumQualityPassRateBasisPoints: 10000, minimumQualityRunCount: 2);
        Assert.False(withLegacy.AccountingComplete);
        Assert.Null(withLegacy.ReliabilityBasisPoints);
        Assert.False(withLegacy.MeetsMinimumRunCount);
        Assert.Equal(2, withLegacy.AttemptCount); // legacy remains readable, never counted as a distinct run

        CandidateQualityEvidence unscoped = new()
        {
            RecordVersion = 3,
            InvocationId = Guid.NewGuid().ToString("N"),
            Identity = identity,
            Capability = CandidateTextQualitySuite.Capability,
            SuiteId = CandidateTextQualitySuite.Id,
            SuiteVersion = CandidateTextQualitySuite.Version,
            SuiteSha256 = CandidateTextQualitySuite.Sha256,
            QualityProfileId = CandidateTextQualitySuite.ProfileId,
            QualityProfileVersion = CandidateTextQualitySuite.ProfileVersion,
            QualityProfileSha256 = CandidateTextQualitySuite.ProfileSha256,
            ExecutionStatus = "Unavailable",
            ExpectedCaseCount = CandidateTextQualitySuite.Cases.Count,
            RecordedUtc = Now
        };
        _store.AddQuality(unscoped);

        CandidateQualityReliabilityCohort withUnknownScope = _service.BuildQualityReliabilityCohort(
            identity, Environment(), minimumQualityPassRateBasisPoints: 10000, minimumQualityRunCount: 2);
        Assert.False(withUnknownScope.AccountingComplete);
        Assert.Null(withUnknownScope.ReliabilityBasisPoints);
    }

    [Fact]
    public async Task LegacyV1SuiteOverlapWithDifferentCaseCountStillInvalidatesCohort()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        _store.AddQuality(Quality(identity));
        _store.AddQuality(Quality(identity));
        CandidateQualityEvidence legacy = Quality(identity);
        legacy.RecordVersion = 1;
        legacy.ExpectedCaseCount++;
        _store.AddQuality(legacy);

        CandidateQualityReliabilityCohort cohort = _service.BuildQualityReliabilityCohort(
            identity, Environment(), minimumQualityPassRateBasisPoints: 10000,
            minimumQualityRunCount: 2);

        Assert.False(cohort.AccountingComplete);
        Assert.Null(cohort.ReliabilityBasisPoints);
        Assert.False(cohort.MeetsMinimumRunCount);
        Assert.Equal(2, cohort.AttemptCount);
    }

    [Fact]
    public async Task OtherHardwareEnvironmentDoesNotJoinAnExactReliabilityCohort()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        CandidateQualityEvidence differentMachine = Quality(identity);
        differentMachine.Environment = Environment("gpu-other");
        differentMachine.Cases[0].Outcome = "fail";
        differentMachine.PassCount--;
        differentMachine.FailCount++;
        _store.AddQuality(differentMachine);

        CandidateQualityReliabilityCohort cohort = _service.BuildQualityReliabilityCohort(
            identity, Environment(), minimumQualityPassRateBasisPoints: 10000, minimumQualityRunCount: 2);

        Assert.Equal(0, cohort.AttemptCount);
        Assert.Equal(0, cohort.SuccessfulRunCount);
        Assert.Null(cohort.ReliabilityBasisPoints);
        Assert.True(cohort.AccountingComplete);
        Assert.False(cohort.MeetsMinimumRunCount);
    }

    [Fact]
    public async Task WrongQualityProfileDoesNotJoinTheSupportedReliabilityCohort()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        CandidateQualityEvidence wrongProfile = Quality(identity);
        wrongProfile.QualityProfileId = "parux.other.profile";
        CandidateEvidenceLedger mixedScope = new() { Quality = [wrongProfile] };

        CandidateQualityReliabilityCohort cohort = _service.BuildQualityReliabilityCohort(
            identity, Environment(), minimumQualityPassRateBasisPoints: 10000,
            minimumQualityRunCount: 2, knownLedger: mixedScope);

        Assert.Equal(0, cohort.AttemptCount);
        Assert.Null(cohort.ReliabilityBasisPoints);
        Assert.True(cohort.AccountingComplete);
        Assert.False(cohort.MeetsMinimumRunCount);
    }

    [Fact]
    public async Task CandidateComparisonUsesOnlyExactCandidatesAndCompleteScopedEvidence()
    {
        CandidateEvidenceIdentity candidate = (await Snapshot())!;
        ModelDiscoveryCandidate referenceCatalogEntry = AddComparisonCandidate("org/reference", "reference.gguf");
        CandidateEvidenceIdentity reference = (await _service.TryCaptureSnapshotAsync(
            referenceCatalogEntry.Key, "reference.gguf", CancellationToken.None))!;

        for (int run = 0; run < 3; run++)
        {
            CandidateQualityEvidence candidateQuality = Quality(candidate);
            candidateQuality.RecordedUtc = Now.AddSeconds(run);
            _store.AddQuality(candidateQuality);
            CandidateQualityEvidence referenceQuality = Quality(reference);
            referenceQuality.RecordedUtc = Now.AddSeconds(run);
            _store.AddQuality(referenceQuality);
        }

        CandidatePerformanceEvidence candidatePerformance = PerformanceWithDecode(candidate, 105);
        candidatePerformance.RecordedUtc = Now.AddMinutes(1);
        CandidatePerformanceEvidence referencePerformance = Performance(reference);
        referencePerformance.RecordedUtc = Now.AddMinutes(2);
        _store.AddPerformance(candidatePerformance);
        _store.AddPerformance(referencePerformance);

        CandidateUtilityComparisonResult result = await _service.CompareCandidateToReferenceCandidateAsync(
            UtilityPolicy(), candidate.CandidateKey, candidate.ManagedFileName,
            reference.CandidateKey, reference.ManagedFileName, Environment(), PerformanceEnvironment(),
            CancellationToken.None);

        Assert.Equal(CandidateUtilityComparisonDisposition.CandidateMeetsReferenceMargin, result.Disposition);
        Assert.Equal("candidate_meets_reference_margin", result.ReasonCode);
    }

    [Fact]
    public async Task CandidateComparisonWithoutReferenceEvidenceProducesNoWinner()
    {
        CandidateEvidenceIdentity candidate = (await Snapshot())!;
        ModelDiscoveryCandidate referenceCatalogEntry = AddComparisonCandidate("org/reference", "reference.gguf");
        CandidateEvidenceIdentity reference = (await _service.TryCaptureSnapshotAsync(
            referenceCatalogEntry.Key, "reference.gguf", CancellationToken.None))!;
        for (int run = 0; run < 3; run++)
        {
            CandidateQualityEvidence candidateQuality = Quality(candidate);
            candidateQuality.RecordedUtc = Now.AddSeconds(run);
            _store.AddQuality(candidateQuality);
        }
        _store.AddPerformance(Performance(candidate));

        CandidateUtilityComparisonResult result = await _service.CompareCandidateToReferenceCandidateAsync(
            UtilityPolicy(), candidate.CandidateKey, candidate.ManagedFileName,
            reference.CandidateKey, reference.ManagedFileName, Environment(), PerformanceEnvironment(),
            CancellationToken.None);

        Assert.Equal(CandidateUtilityComparisonDisposition.NoWinner, result.Disposition);
        Assert.Equal("quality_insufficient_runs", result.ReasonCode);
    }

    [Fact]
    public async Task ActiveReferenceUsesTheActuallyLoadedDifferentCandidate()
    {
        CandidateEvidenceIdentity candidate = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity active = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        SeedComparisonEvidence(candidate, active);
        var source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"));

        CandidateUtilityComparisonResult result = await _service.CompareCandidateToActiveReferenceCandidateAsync(
            UtilityPolicy(), candidate.CandidateKey, candidate.ManagedFileName, Environment(),
            PerformanceEnvironment(), source, CancellationToken.None);

        Assert.Equal(CandidateUtilityComparisonDisposition.CandidateMeetsReferenceMargin, result.Disposition);
    }

    [Fact]
    public async Task General_chat_active_comparison_proof_binds_exact_scope_and_rechecks_fresh_evidence()
    {
        CandidateEvidenceIdentity challenger = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity incumbent = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        SeedGeneralChatComparisonEvidence(challenger, incumbent);
        var source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"));
        CandidateUtilityPolicyDefinition policy = GeneralChatUtilityPolicy();
        CandidateEvaluationEnvironment quality = GeneralChatEnvironment();
        CandidateEvaluationEnvironment performance = PerformanceEnvironment();

        CandidateUtilityProofResult result = await _service.CreateActiveReferenceProofAsync(policy,
            challenger.CandidateKey, challenger.ManagedFileName, quality, performance, source, Fence(),
            CancellationToken.None);

        Assert.Equal(CandidateUtilityComparisonDisposition.CandidateMeetsReferenceMargin,
            result.Comparison.Disposition);
        CandidateUtilityComparisonProof proof = Assert.IsType<CandidateUtilityComparisonProof>(result.Proof);
        Assert.Equal(challenger.CandidateKey, proof.Challenger.Identity.CandidateKey);
        Assert.Equal(incumbent.CandidateKey, proof.Incumbent.Identity.CandidateKey);
        Assert.Equal(challenger.LocalSha256, proof.Challenger.Identity.LocalSha256);
        Assert.Equal(CandidateGeneralChatQualitySuite.Sha256, proof.QualitySuiteSha256);
        Assert.Equal(CandidateGeneralChatQualitySuite.ProfileSha256, proof.QualityProfileSha256);
        Assert.Equal(CandidateTextPerformanceWorkload.Sha256, proof.PerformanceWorkloadSha256);
        Assert.Equal(CandidateGeneralChatQualitySuite.WorkloadFingerprint,
            proof.QualityEnvironment.WorkloadFingerprint);
        Assert.Equal(5, proof.Challenger.QualityInvocationIds.Length);
        Assert.Equal(5, proof.Incumbent.QualityInvocationIds.Length);
        Assert.Equal(5, proof.Challenger.QualityInvocationIds.Distinct().Count());
        Assert.Equal(64, proof.Challenger.CompleteQualityCohortSha256.Length);
        Assert.Equal(64, proof.Challenger.PerformanceRecordSha256.Length);
        Assert.Equal(64, proof.ProofSha256.Length);
        Assert.True(await _service.RecheckActiveReferenceProofAsync(proof, policy, quality,
            performance, source, Fence(), CancellationToken.None));

        Assert.False(await _service.RecheckActiveReferenceProofAsync(
            proof with { PolicySha256 = new string('a', 64) }, policy, quality,
            performance, source, Fence(), CancellationToken.None));
        policy.MinimumCompletionLatencyMarginBasisPoints = 1000;
        Assert.False(await _service.RecheckActiveReferenceProofAsync(proof, policy, quality,
            performance, source, Fence(), CancellationToken.None));
        policy.MinimumCompletionLatencyMarginBasisPoints = 500;
        CandidateEvaluationEnvironment wrongWorkload = Environment();
        Assert.False(await _service.RecheckActiveReferenceProofAsync(proof, policy, wrongWorkload,
            performance, source, Fence(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_models, challenger.ManagedFileName), "changed model bytes");
        Assert.False(await _service.RecheckActiveReferenceProofAsync(proof, policy, quality,
            performance, source, Fence(), CancellationToken.None));
    }

    [Fact]
    public async Task General_chat_proof_requires_live_fence_same_planned_challenger_launch_and_unchanged_host()
    {
        CandidateEvidenceIdentity challenger = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity incumbent = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        SeedGeneralChatComparisonEvidence(challenger, incumbent);
        var source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"));
        CandidateUtilityPolicyDefinition policy = GeneralChatUtilityPolicy();
        CandidateEvaluationEnvironment quality = GeneralChatEnvironment();
        CandidateEvaluationEnvironment performance = PerformanceEnvironment();
        async Task<CandidateUtilityProofResult> Prove(FakeLaunchFence fence) => await _service.CreateActiveReferenceProofAsync(
            policy, challenger.CandidateKey, challenger.ManagedFileName, quality, performance, source, fence,
            CancellationToken.None);

        FakeLaunchFence noFence = Fence();
        noFence.LiveConfiguration = null;
        Assert.Equal("incumbent_actual_launch_unavailable", (await Prove(noFence)).Comparison.ReasonCode);

        FakeLaunchFence otherLive = Fence();
        otherLive.LiveConfiguration = "ld1-sha256:" + new string('d', 64);
        Assert.Equal("incumbent_launch_configuration_mismatch", (await Prove(otherLive)).Comparison.ReasonCode);

        FakeLaunchFence otherPlan = Fence();
        otherPlan.PlannedConfiguration = "ld1-sha256:" + new string('e', 64);
        Assert.Equal("challenger_launch_configuration_mismatch", (await Prove(otherPlan)).Comparison.ReasonCode);

        FakeLaunchFence refusedPlan = Fence();
        refusedPlan.PlannedConfiguration = null;
        Assert.Equal("challenger_launch_descriptor_unknown_argument", (await Prove(refusedPlan)).Comparison.ReasonCode);

        FakeLaunchFence driver = Fence();
        driver.Host = driver.Host! with { HardwareFingerprint = "gpu-2" };
        Assert.Equal("incumbent_hardware_changed", (await Prove(driver)).Comparison.ReasonCode);

        FakeLaunchFence dependencies = Fence();
        dependencies.Host = dependencies.Host! with { BinaryFingerprint = "binary-other" };
        Assert.Equal("incumbent_runtime_dependencies_changed", (await Prove(dependencies)).Comparison.ReasonCode);

        // The live launch is replaced after the scope check but before the final snapshot: no proof.
        FakeLaunchFence replaced = Fence();
        replaced.OnRead = read => { if (read == 2) replaced.LiveConfiguration = "ld1-sha256:" + new string('c', 64); };
        CandidateUtilityProofResult raced = await Prove(replaced);
        Assert.Null(raced.Proof);
        Assert.Equal("proof_changed_during_comparison", raced.Comparison.ReasonCode);

        Assert.NotNull((await Prove(Fence())).Proof);
    }
    [Fact]
    public async Task General_chat_proof_refuses_evidence_not_measured_under_an_actual_launch_descriptor()
    {
        CandidateEvidenceIdentity challenger = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity incumbent = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        SeedGeneralChatComparisonEvidence(challenger, incumbent);
        var source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"));
        CandidateUtilityPolicyDefinition policy = GeneralChatUtilityPolicy();
        CandidateEvaluationEnvironment quality = GeneralChatEnvironment();
        CandidateEvaluationEnvironment performance = PerformanceEnvironment();

        // Historical fixed evaluator profile: never proof-eligible, even with otherwise winning evidence.
        quality.ConfigurationFingerprint = LlamaTextQualitySession.ConfigurationFingerprint;
        performance.ConfigurationFingerprint = LlamaTextQualitySession.ConfigurationFingerprint;
        CandidateUtilityProofResult fixedProfile = await _service.CreateActiveReferenceProofAsync(policy,
            challenger.CandidateKey, challenger.ManagedFileName, quality, performance, source, Fence(), CancellationToken.None);
        Assert.Null(fixedProfile.Proof);
        Assert.Equal("evaluation_launch_profile_not_actual", fixedProfile.Comparison.ReasonCode);

        // Quality and performance measured under two different actual launches are not one scope.
        quality.ConfigurationFingerprint = ActualLaunchConfiguration;
        performance.ConfigurationFingerprint = "ld1-sha256:" + new string('e', 64);
        CandidateUtilityProofResult split = await _service.CreateActiveReferenceProofAsync(policy,
            challenger.CandidateKey, challenger.ManagedFileName, quality, performance, source, Fence(), CancellationToken.None);
        Assert.Null(split.Proof);
        Assert.Equal("evaluation_launch_profile_not_actual", split.Comparison.ReasonCode);

        performance.ConfigurationFingerprint = ActualLaunchConfiguration;
        Assert.NotNull((await _service.CreateActiveReferenceProofAsync(policy, challenger.CandidateKey,
            challenger.ManagedFileName, quality, performance, source, Fence(), CancellationToken.None)).Proof);
    }

    [Fact]
    public async Task General_chat_proof_rejects_new_failure_or_changed_performance_record()
    {
        CandidateEvidenceIdentity challenger = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity incumbent = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        SeedGeneralChatComparisonEvidence(challenger, incumbent);
        var source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"));
        CandidateUtilityPolicyDefinition policy = GeneralChatUtilityPolicy();
        CandidateEvaluationEnvironment quality = GeneralChatEnvironment();
        CandidateEvaluationEnvironment performance = PerformanceEnvironment();
        CandidateUtilityComparisonProof proof = Assert.IsType<CandidateUtilityComparisonProof>(
            (await _service.CreateActiveReferenceProofAsync(policy, challenger.CandidateKey,
                challenger.ManagedFileName, quality, performance, source, Fence(), CancellationToken.None)).Proof);

        CandidatePerformanceEvidence newer = PerformanceWithDecode(challenger, 105);
        newer.RecordedUtc = Now.AddMinutes(4);
        _store.AddPerformance(newer);
        Assert.False(await _service.RecheckActiveReferenceProofAsync(proof, policy, quality,
            performance, source, Fence(), CancellationToken.None));

        CandidateQualityEvidence failed = GeneralChatQuality(challenger);
        failed.RecordedUtc = Now.AddMinutes(3);
        for (int index = 0; index < 4; index++) failed.Cases[index].Outcome = "fail";
        failed.PassCount = 26;
        failed.FailCount = 4;
        _store.AddQuality(failed);
        Assert.False(await _service.RecheckActiveReferenceProofAsync(proof, policy, quality,
            performance, source, Fence(), CancellationToken.None));

        // Both the new performance record and failed run change the exact ledger proof.
        Assert.NotEqual(proof.EvidenceLedgerSha256, CandidateUtilityProofDigest.Ledger(_store.Load()));
    }

    [Fact]
    public async Task General_chat_proof_digest_includes_failed_attempts_and_rejects_ambiguous_performance()
    {
        CandidateEvidenceIdentity challenger = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity incumbent = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        for (int run = 0; run < 20; run++)
        {
            foreach (CandidateEvidenceIdentity identity in new[] { challenger, incumbent })
            {
                CandidateQualityEvidence attempt = GeneralChatQuality(identity);
                attempt.RecordedUtc = Now.AddSeconds(run);
                if (run == 0)
                {
                    for (int index = 0; index < 4; index++) attempt.Cases[index].Outcome = "fail";
                    attempt.PassCount = 26;
                    attempt.FailCount = 4;
                }
                _store.AddQuality(attempt);
            }
        }
        CandidatePerformanceEvidence candidatePerformance = PerformanceWithDecode(challenger, 105);
        candidatePerformance.RecordedUtc = Now.AddMinutes(1);
        _store.AddPerformance(candidatePerformance);
        CandidatePerformanceEvidence referencePerformance = Performance(incumbent);
        referencePerformance.RecordedUtc = Now.AddMinutes(2);
        _store.AddPerformance(referencePerformance);
        var source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"));
        CandidateUtilityPolicyDefinition policy = GeneralChatUtilityPolicy();
        CandidateEvaluationEnvironment quality = GeneralChatEnvironment();
        CandidateEvaluationEnvironment performance = PerformanceEnvironment();
        CandidateUtilityComparisonProof proof = Assert.IsType<CandidateUtilityComparisonProof>(
            (await _service.CreateActiveReferenceProofAsync(policy, challenger.CandidateKey,
                challenger.ManagedFileName, quality, performance, source, Fence(), CancellationToken.None)).Proof);

        CandidateQualityEvidence[] all = _store.Load().Quality
            .Where(item => item.Identity.CandidateKey == challenger.CandidateKey).ToArray();
        Assert.Equal(20, proof.Challenger.QualityInvocationIds.Length);
        Assert.Equal(CandidateUtilityProofDigest.QualityCohort(all), proof.Challenger.CompleteQualityCohortSha256);
        Assert.NotEqual(CandidateUtilityProofDigest.QualityCohort(all.Where(item => item.FailCount == 0)),
            proof.Challenger.CompleteQualityCohortSha256);

        CandidatePerformanceEvidence ambiguous = PerformanceWithDecode(challenger, 105);
        ambiguous.RecordedUtc = candidatePerformance.RecordedUtc;
        _store.AddPerformance(ambiguous);
        CandidateUtilityProofResult refused = await _service.CreateActiveReferenceProofAsync(policy,
            challenger.CandidateKey, challenger.ManagedFileName, quality, performance, source, Fence(),
            CancellationToken.None);
        Assert.Null(refused.Proof);
        Assert.False(await _service.RecheckActiveReferenceProofAsync(proof, policy, quality,
            performance, source, Fence(), CancellationToken.None));
    }

    [Fact]
    public async Task General_chat_proof_refuses_smoke_scope_and_changed_active_runtime()
    {
        var source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "model.gguf"));
        CandidateUtilityProofResult smoke = await _service.CreateActiveReferenceProofAsync(
            UtilityPolicy(), _candidate.Key, "model.gguf", Environment(), PerformanceEnvironment(),
            source, Fence(), CancellationToken.None);
        Assert.Null(smoke.Proof);
        Assert.Equal("unsupported_recommendation_scope", smoke.Comparison.ReasonCode);

        CandidateEvidenceIdentity challenger = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity incumbent = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        SeedGeneralChatComparisonEvidence(challenger, incumbent);
        source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"))
            { ChangeOnSecondRead = true };
        CandidateUtilityProofResult changed = await _service.CreateActiveReferenceProofAsync(
            GeneralChatUtilityPolicy(), challenger.CandidateKey, challenger.ManagedFileName,
            GeneralChatEnvironment(), PerformanceEnvironment(), source, Fence(), CancellationToken.None);
        Assert.Null(changed.Proof);
    }

    [Fact]
    public async Task General_chat_proof_recheck_rejects_still_eligible_provenance_change()
    {
        CandidateEvidenceIdentity challenger = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity incumbent = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        SeedGeneralChatComparisonEvidence(challenger, incumbent);
        var source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"));
        CandidateUtilityPolicyDefinition policy = GeneralChatUtilityPolicy();
        CandidateEvaluationEnvironment quality = GeneralChatEnvironment();
        CandidateEvaluationEnvironment performance = PerformanceEnvironment();
        CandidateUtilityComparisonProof proof = Assert.IsType<CandidateUtilityComparisonProof>(
            (await _service.CreateActiveReferenceProofAsync(policy, challenger.CandidateKey,
                challenger.ManagedFileName, quality, performance, source, Fence(), CancellationToken.None)).Proof);

        ModelDownloadProvenance changed = ModelDownloadProvenanceManifestStore.Get(_models,
            challenger.ManagedFileName)!;
        changed.MetadataObservedUtc = changed.MetadataObservedUtc!.Value.AddSeconds(-1);
        ModelDownloadProvenanceManifestStore.Upsert(_models, challenger.ManagedFileName, changed);
        Assert.NotNull(_service.TryCaptureProvenanceSnapshot(challenger.CandidateKey,
            challenger.ManagedFileName));
        Assert.False(await _service.RecheckActiveReferenceProofAsync(proof, policy, quality,
            performance, source, Fence(), CancellationToken.None));
    }

    [Fact]
    public async Task General_chat_proof_recheck_rejects_still_eligible_license_change()
    {
        CandidateEvidenceIdentity challenger = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity incumbent = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        SeedGeneralChatComparisonEvidence(challenger, incumbent);
        var source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"));
        CandidateUtilityPolicyDefinition policy = GeneralChatUtilityPolicy();
        CandidateEvaluationEnvironment quality = GeneralChatEnvironment();
        CandidateEvaluationEnvironment performance = PerformanceEnvironment();
        CandidateUtilityComparisonProof proof = Assert.IsType<CandidateUtilityComparisonProof>(
            (await _service.CreateActiveReferenceProofAsync(policy, challenger.CandidateKey,
                challenger.ManagedFileName, quality, performance, source, Fence(), CancellationToken.None)).Proof);

        _candidate.DeclaredLicense = "Apache-2.0";
        _catalog.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate>
            { _candidate, activeCandidate } });
        ModelDownloadProvenance changed = ModelDownloadProvenanceManifestStore.Get(_models,
            challenger.ManagedFileName)!;
        changed.PublisherDeclaredLicense = "Apache-2.0";
        ModelDownloadProvenanceManifestStore.Upsert(_models, challenger.ManagedFileName, changed);
        Assert.NotNull(_service.TryCaptureProvenanceSnapshot(challenger.CandidateKey,
            challenger.ManagedFileName));
        Assert.False(await _service.RecheckActiveReferenceProofAsync(proof, policy, quality,
            performance, source, Fence(), CancellationToken.None));
    }

    [Fact]
    public async Task General_chat_proof_refuses_provenance_change_or_cancellation_at_final_active_read()
    {
        CandidateEvidenceIdentity challenger = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity incumbent = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        SeedGeneralChatComparisonEvidence(challenger, incumbent);
        CandidateUtilityPolicyDefinition policy = GeneralChatUtilityPolicy();
        CandidateEvaluationEnvironment quality = GeneralChatEnvironment();
        CandidateEvaluationEnvironment performance = PerformanceEnvironment();

        var changedSource = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"))
        {
            OnRead = read =>
            {
                if (read != 4) return;
                ModelDownloadProvenance changed = ModelDownloadProvenanceManifestStore.Get(_models,
                    challenger.ManagedFileName)!;
                changed.CompletedUtc = changed.CompletedUtc.AddSeconds(1);
                ModelDownloadProvenanceManifestStore.Upsert(_models, challenger.ManagedFileName, changed);
            }
        };
        CandidateUtilityProofResult mutated = await _service.CreateActiveReferenceProofAsync(policy,
            challenger.CandidateKey, challenger.ManagedFileName, quality, performance,
            changedSource, Fence(), CancellationToken.None);
        Assert.Null(mutated.Proof);

        using var cancellation = new CancellationTokenSource();
        var cancellingSource = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"))
        {
            OnRead = read => { if (read == 4) cancellation.Cancel(); }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.CreateActiveReferenceProofAsync(
            policy, challenger.CandidateKey, challenger.ManagedFileName, quality, performance,
            cancellingSource, Fence(), cancellation.Token));
    }

    [Fact]
    public async Task General_chat_proof_refuses_failed_evidence_appended_at_final_active_read()
    {
        CandidateEvidenceIdentity challenger = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity incumbent = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        SeedGeneralChatComparisonEvidence(challenger, incumbent);
        var source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"))
        {
            OnRead = read =>
            {
                if (read != 4) return;
                CandidateQualityEvidence failed = GeneralChatQuality(challenger);
                failed.RecordedUtc = Now.AddMinutes(3);
                for (int index = 0; index < 4; index++) failed.Cases[index].Outcome = "fail";
                failed.PassCount = 26;
                failed.FailCount = 4;
                _store.AddQuality(failed);
            }
        };

        CandidateUtilityProofResult result = await _service.CreateActiveReferenceProofAsync(
            GeneralChatUtilityPolicy(), challenger.CandidateKey, challenger.ManagedFileName,
            GeneralChatEnvironment(), PerformanceEnvironment(), source, Fence(), CancellationToken.None);
        Assert.Null(result.Proof);
    }

    [Fact]
    public async Task General_chat_proof_refuses_new_active_basename_collision_at_final_read()
    {
        CandidateEvidenceIdentity challenger = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity incumbent = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        SeedGeneralChatComparisonEvidence(challenger, incumbent);
        var source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"))
        {
            OnRead = read =>
            {
                if (read != 4) return;
                const string repoId = "org/collision";
                const string revision = "cccccccccccccccccccccccccccccccccccccccc";
                const string repoFilePath = "other/active.gguf";
                ModelDiscoveryCatalog catalog = _catalog.Load();
                catalog.Candidates.Add(new ModelDiscoveryCandidate
                {
                    RepoId = repoId,
                    Revision = revision,
                    RepoFilePath = repoFilePath,
                    Key = ModelDiscoveryIdentity.Key(repoId, revision, repoFilePath),
                    PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, repoFilePath),
                    FirstSeenUtc = Now,
                    LastSeenUtc = Now
                });
                _catalog.Save(catalog);
            }
        };

        CandidateUtilityProofResult result = await _service.CreateActiveReferenceProofAsync(
            GeneralChatUtilityPolicy(), challenger.CandidateKey, challenger.ManagedFileName,
            GeneralChatEnvironment(), PerformanceEnvironment(), source, Fence(), CancellationToken.None);
        Assert.Null(result.Proof);
    }

    [Fact]
    public async Task General_chat_proof_ignores_unrelated_catalog_row_and_last_seen_refresh()
    {
        CandidateEvidenceIdentity challenger = (await Snapshot())!;
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        CandidateEvidenceIdentity incumbent = (await _service.TryCaptureSnapshotAsync(
            activeCandidate.Key, "active.gguf", CancellationToken.None))!;
        SeedGeneralChatComparisonEvidence(challenger, incumbent);
        var source = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"))
        {
            OnRead = read =>
            {
                if (read != 4) return;
                const string repoId = "org/unrelated";
                const string revision = "cccccccccccccccccccccccccccccccccccccccc";
                const string repoFilePath = "other/unrelated.gguf";
                ModelDiscoveryCatalog catalog = _catalog.Load();
                catalog.Candidates.Single(item => item.Key == activeCandidate.Key).LastSeenUtc = Now.AddSeconds(1);
                catalog.Candidates.Add(new ModelDiscoveryCandidate
                {
                    RepoId = repoId,
                    Revision = revision,
                    RepoFilePath = repoFilePath,
                    Key = ModelDiscoveryIdentity.Key(repoId, revision, repoFilePath),
                    PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, repoFilePath),
                    FirstSeenUtc = Now,
                    LastSeenUtc = Now
                });
                _catalog.Save(catalog);
            }
        };

        CandidateUtilityProofResult result = await _service.CreateActiveReferenceProofAsync(
            GeneralChatUtilityPolicy(), challenger.CandidateKey, challenger.ManagedFileName,
            GeneralChatEnvironment(), PerformanceEnvironment(), source, Fence(), CancellationToken.None);
        Assert.NotNull(result.Proof);
    }

    [Fact]
    public async Task ActiveReferenceFailsClosedForAbsentOrUnhealthyRuntimeProof()
    {
        var missing = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "model.gguf")) { Valid = false };
        Assert.Equal("active_model_proof_stale", (await CompareActive(missing)).ReasonCode);
        var unhealthy = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "model.gguf")) { Healthy = false };
        Assert.Equal("active_runtime_identity_unavailable", (await CompareActive(unhealthy)).ReasonCode);
    }

    [Fact]
    public async Task ActiveReferenceRejectsChangedBytesOutsideRootAndMissingMapping()
    {
        var changed = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "model.gguf"));
        File.WriteAllText(Path.Combine(_models, "model.gguf"), "payload-x");
        Assert.Equal("active_model_proof_stale", (await CompareActive(changed)).ReasonCode);

        var outside = new FakeActiveTextModelIdentitySource(Path.Combine(_root, "outside.gguf"));
        Assert.Equal("active_runtime_identity_unavailable", (await CompareActive(outside)).ReasonCode);

        var absent = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "absent.gguf"));
        Assert.Equal("active_candidate_mapping_missing", (await CompareActive(absent)).ReasonCode);
    }

    [Fact]
    public async Task ActiveReferenceRejectsAmbiguousMappingAndRuntimeChangeAfterComparison()
    {
        ModelDiscoveryCandidate activeCandidate = AddComparisonCandidate("org/active", "active.gguf");
        ModelDiscoveryCandidate duplicate = new()
        {
            Provider = activeCandidate.Provider, ProviderAdapterVersion = activeCandidate.ProviderAdapterVersion,
            RepoId = "org/other", Revision = new string('c', 40), RepoFilePath = activeCandidate.RepoFilePath,
            Key = ModelDiscoveryIdentity.Key("org/other", new string('c', 40), activeCandidate.RepoFilePath),
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl("org/other", new string('c', 40), activeCandidate.RepoFilePath),
            FirstSeenUtc = Now, LastSeenUtc = Now
        };
        ModelDiscoveryCatalog catalog = _catalog.Load();
        catalog.Candidates.Add(duplicate);
        _catalog.Save(catalog);
        var ambiguous = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"));
        Assert.Equal("active_candidate_mapping_ambiguous", (await CompareActive(ambiguous)).ReasonCode);

        // Restore a single exact active mapping and mutate runtime identity after the evidence comparison.
        catalog.Candidates.Remove(duplicate);
        _catalog.Save(catalog);
        CandidateEvidenceIdentity candidate = (await Snapshot())!;
        CandidateEvidenceIdentity active = (await _service.TryCaptureSnapshotAsync(activeCandidate.Key,
            "active.gguf", CancellationToken.None))!;
        SeedComparisonEvidence(candidate, active);
        var changedRuntime = new FakeActiveTextModelIdentitySource(Path.Combine(_models, "active.gguf"))
        {
            ChangeOnSecondRead = true
        };
        Assert.Equal("active_runtime_changed_during_comparison",
            (await _service.CompareCandidateToActiveReferenceCandidateAsync(UtilityPolicy(), candidate.CandidateKey,
                candidate.ManagedFileName, Environment(), PerformanceEnvironment(), changedRuntime,
                CancellationToken.None)).ReasonCode);
    }

    private async Task<CandidateUtilityComparisonResult> CompareActive(FakeActiveTextModelIdentitySource source) =>
        await _service.CompareCandidateToActiveReferenceCandidateAsync(UtilityPolicy(), _candidate.Key,
            "model.gguf", Environment(), PerformanceEnvironment(), source, CancellationToken.None);

    private void SeedComparisonEvidence(CandidateEvidenceIdentity candidate, CandidateEvidenceIdentity reference)
    {
        for (int run = 0; run < 3; run++)
        {
            CandidateQualityEvidence cq = Quality(candidate); cq.RecordedUtc = Now.AddSeconds(run); _store.AddQuality(cq);
            CandidateQualityEvidence rq = Quality(reference); rq.RecordedUtc = Now.AddSeconds(run); _store.AddQuality(rq);
        }
        CandidatePerformanceEvidence cp = PerformanceWithDecode(candidate, 105); cp.RecordedUtc = Now.AddMinutes(1);
        CandidatePerformanceEvidence rp = Performance(reference); rp.RecordedUtc = Now.AddMinutes(2);
        _store.AddPerformance(cp); _store.AddPerformance(rp);
    }

    private void SeedGeneralChatComparisonEvidence(CandidateEvidenceIdentity candidate,
        CandidateEvidenceIdentity reference)
    {
        for (int run = 0; run < 5; run++)
        {
            CandidateQualityEvidence cq = GeneralChatQuality(candidate);
            cq.RecordedUtc = Now.AddSeconds(run);
            _store.AddQuality(cq);
            CandidateQualityEvidence rq = GeneralChatQuality(reference);
            rq.RecordedUtc = Now.AddSeconds(run);
            _store.AddQuality(rq);
        }
        CandidatePerformanceEvidence cp = PerformanceWithDecode(candidate, 105);
        cp.RecordedUtc = Now.AddMinutes(1);
        CandidatePerformanceEvidence rp = Performance(reference);
        rp.RecordedUtc = Now.AddMinutes(2);
        _store.AddPerformance(cp);
        _store.AddPerformance(rp);
    }

    private static CandidateEvaluationEnvironment GeneralChatEnvironment()
    {
        CandidateEvaluationEnvironment environment = Environment();
        environment.WorkloadFingerprint = CandidateGeneralChatQualitySuite.WorkloadFingerprint;
        return environment;
    }

    private static CandidateUtilityPolicyDefinition GeneralChatUtilityPolicy()
    {
        CandidateUtilityPolicyDefinition policy = UtilityPolicy();
        policy.PolicyId = "parux.utility.chat-general.test";
        policy.Capability = CandidateGeneralChatQualitySuite.Capability;
        policy.QualitySuiteId = CandidateGeneralChatQualitySuite.Id;
        policy.QualitySuiteVersion = CandidateGeneralChatQualitySuite.Version;
        policy.QualityEvidenceProfileId = CandidateGeneralChatQualitySuite.ProfileId;
        policy.QualityEvidenceProfileVersion = CandidateGeneralChatQualitySuite.ProfileVersion;
        policy.QualityEvidenceProfileSha256 = CandidateGeneralChatQualitySuite.ProfileSha256;
        policy.MinimumQualityPassRateBasisPoints = 9000;
        policy.MinimumQualityReliabilityBasisPoints = 9500;
        policy.MinimumQualityRunCount = 5;
        return policy;
    }

    private sealed class FakeActiveTextModelIdentitySource(string path) : IActiveTextModelIdentitySource
    {
        private int _reads;
        public bool Healthy { get; set; } = true;
        public bool Valid { get; set; } = true;
        public bool ChangeOnSecondRead { get; set; }
        public Action<int>? OnRead { get; set; }
        public async Task<(bool Healthy, string? ActivePath)> ReadAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            _reads++;
            OnRead?.Invoke(_reads);
            return (Healthy, ChangeOnSecondRead && _reads > 1 ? Path.Combine(Path.GetTempPath(), "changed.gguf") : path);
        }
        public bool IsSameLiveModel(string activePath, string expectedSha256, bool healthy) => Valid && healthy
            && expectedSha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.GetFileName(activePath) == "active.gguf" ? "comparison payload for active.gguf" : "payload-1")))
                .ToLowerInvariant();
    }

    [Theory]
    [InlineData("quality")]
    [InlineData("performance")]
    public async Task PersistedOrderCannotMakeOlderPassingEvidenceWinOverNewerFailure(string evidenceKind)
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        CandidateQualityEvidence olderQuality = Quality(identity);
        olderQuality.RecordedUtc = Now.AddMinutes(-2);
        CandidatePerformanceEvidence olderPerformance = Performance(identity);
        olderPerformance.RecordedUtc = Now.AddMinutes(-2);
        _store.AddQuality(olderQuality);
        _store.AddPerformance(olderPerformance);

        CandidateQualityEvidence newerFailure = Quality(identity);
        newerFailure.RecordedUtc = Now.AddMinutes(-1);
        newerFailure.Cases[0].Outcome = "fail";
        newerFailure.PassCount--;
        newerFailure.FailCount = 1;
        CandidatePerformanceEvidence newerIncomplete = Performance(identity);
        newerIncomplete.RecordedUtc = Now.AddMinutes(-1);
        newerIncomplete.Completed = false;
        newerIncomplete.ExecutionStatus = "Failed";
        if (evidenceKind == "quality") _store.AddQuality(newerFailure);
        else _store.AddPerformance(newerIncomplete);

        CandidateEvidenceLedger ledger = _store.Load();
        ledger.Quality = ledger.Quality.OrderBy(item => item.RecordedUtc).ToList();
        ledger.Performance = ledger.Performance.OrderBy(item => item.RecordedUtc).ToList();
        File.WriteAllText(Path.Combine(_state, CandidateEvidenceStore.FileName),
            JsonSerializer.Serialize(ledger, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
    }

    [Fact]
    public async Task ConflictingNewestTimestampFailsClosed()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        CandidateQualityEvidence passing = Quality(identity);
        CandidateQualityEvidence failing = Quality(identity);
        failing.Cases[0].Outcome = "fail";
        failing.PassCount--;
        failing.FailCount = 1;
        _store.AddQuality(passing);
        _store.AddQuality(failing);
        Assert.True(await _service.TryCommitPerformanceAsync(Performance(identity), CancellationToken.None));

        CandidateEvidenceLedger ledger = _store.Load();
        ledger.Quality = ledger.Quality.OrderBy(item => item.FailCount).ToList();
        File.WriteAllText(Path.Combine(_state, CandidateEvidenceStore.FileName),
            JsonSerializer.Serialize(ledger, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
    }

    [Fact]
    public async Task SameSizeByteChangeAfterSnapshotBlocksCommitAndAfterCommitBlocksEligibility()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        File.WriteAllText(Path.Combine(_models, "model.gguf"), "payload-2");
        Assert.False(await _service.TryCommitQualityAsync(Quality(identity), CancellationToken.None));
        Assert.Null(await Snapshot());
        File.WriteAllText(Path.Combine(_models, "model.gguf"), "payload-1");
        Assert.True(await _service.TryCommitQualityAsync(Quality(identity), CancellationToken.None));
        Assert.True(await _service.TryCommitPerformanceAsync(Performance(identity), CancellationToken.None));
        File.WriteAllText(Path.Combine(_models, "model.gguf"), "payload-2");
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
    }

    [Fact]
    public async Task ChangedCandidateIdentityEnvironmentAndSuiteAreIneligible()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        Assert.True(await _service.TryCommitQualityAsync(Quality(identity), CancellationToken.None));
        Assert.True(await _service.TryCommitPerformanceAsync(Performance(identity), CancellationToken.None));
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility(environment: Environment("new-driver")));
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await _service.CheckEligibilityAsync(
            _candidate.Key, "model.gguf", CandidateTextQualitySuite.Capability, CandidateTextQualitySuite.Id, 2, SuiteHash,
            Environment(), PerformanceEnvironment(), CancellationToken.None));
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await _service.CheckEligibilityAsync(
            new string('f', 64), "model.gguf", CandidateTextQualitySuite.Capability, CandidateTextQualitySuite.Id, 1, SuiteHash,
            Environment(), PerformanceEnvironment(), CancellationToken.None));
        _candidate.Disposition = "stale";
        _catalog.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { _candidate } });
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
    }

    [Fact]
    public async Task MismatchedRevisionPathAndDigestOrLegacyProvenanceCannotBind()
    {
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        CandidateQualityEvidence wrong = Quality(identity);
        wrong.Identity.LocalSha256 = new string('c', 64);
        Assert.False(await _service.TryCommitQualityAsync(wrong, CancellationToken.None));
        WriteProvenance(revision: new string('d', 40));
        Assert.Null(await Snapshot());
        WriteProvenance(path: "other/model.gguf");
        Assert.Null(await Snapshot());
        WriteProvenance(legacy: true);
        Assert.Null(await Snapshot());
    }

    [Fact]
    public void Candidate_evidence_requires_current_revision_license_and_persisted_review_state()
    {
        Assert.NotNull(_service.TryCaptureProvenanceSnapshot(_candidate.Key, "model.gguf", out string? initialRejection));
        Assert.Null(initialRejection);

        _candidate.DeclaredLicense = "custom-research-only";
        _catalog.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { _candidate } });
        Assert.Null(_service.TryCaptureProvenanceSnapshot(_candidate.Key, "model.gguf", out string? changedRejection));
        Assert.Equal("LicenseMetadataChanged", changedRejection);

        _candidate.DeclaredLicense = "MIT";
        _catalog.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { _candidate } });
        WriteProvenance(missingLicenseEvidence: true);
        Assert.Null(_service.TryCaptureProvenanceSnapshot(_candidate.Key, "model.gguf", out string? missingRejection));
        Assert.Equal("LicenseEvidenceMissing", missingRejection);
    }

    [Fact]
    public async Task MissingProvenanceAndUnsafeManagedPathCannotBind()
    {
        Assert.Null(await _service.TryCaptureSnapshotAsync(_candidate.Key, "..\\escape.gguf", CancellationToken.None));
        Assert.True(ModelDownloadProvenanceManifestStore.Remove(_models, "model.gguf"));
        Assert.Null(await Snapshot());
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
    }

    [Fact]
    public async Task MalformedLedgerFailsClosedWithoutOverwrite()
    {
        string path = Path.Combine(_state, CandidateEvidenceStore.FileName);
        File.WriteAllText(path, "{broken");
        CandidateEvidenceIdentity identity = (await Snapshot())!;
        Assert.Equal(CandidateEligibilityOutcome.Ineligible, await Eligibility());
        Assert.Throws<InvalidDataException>(() => _store.AddQuality(Quality(identity)));
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public async Task Same_size_byte_swap_with_preserved_timestamp_is_not_trusted_at_the_evidence_consumer()
    {
        string path = Path.Combine(_models, "model.gguf");
        Assert.NotNull(await Snapshot());
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "payload-2"); // same length as "payload-1"
        File.SetLastWriteTimeUtc(path, stamp);
        // Provenance metadata (size + timestamp) still matches, but the consumer re-hashes the bytes.
        Assert.Null(await Snapshot());
    }

    private Task<CandidateEvidenceIdentity?> Snapshot() => _service.TryCaptureSnapshotAsync(
        _candidate.Key, "model.gguf", CancellationToken.None);

    private Task<string> Eligibility(CandidateEvidenceService? service = null,
        CandidateEvaluationEnvironment? environment = null) => (service ?? _service).CheckEligibilityAsync(
        _candidate.Key, "model.gguf", CandidateTextQualitySuite.Capability,
        CandidateTextQualitySuite.Id, CandidateTextQualitySuite.Version, SuiteHash,
        environment ?? Environment(), PerformanceEnvironment(), CancellationToken.None);

    internal static CandidateQualityEvidence Quality(CandidateEvidenceIdentity identity) => new()
    {
        RecordVersion = 3, InvocationId = Guid.NewGuid().ToString("N"),
        Identity = identity, Capability = CandidateTextQualitySuite.Capability,
        SuiteId = CandidateTextQualitySuite.Id, SuiteVersion = CandidateTextQualitySuite.Version,
        SuiteSha256 = SuiteHash, QualityProfileId = CandidateTextQualitySuite.ProfileId,
        QualityProfileVersion = CandidateTextQualitySuite.ProfileVersion,
        QualityProfileSha256 = CandidateTextQualitySuite.ProfileSha256,
        Environment = Environment(), ExecutionStatus = "Completed",
        ExpectedCaseCount = CandidateTextQualitySuite.Cases.Count,
        PassCount = CandidateTextQualitySuite.Cases.Count, Completed = true,
        Cases = CandidateTextQualitySuite.Cases.Select(c => new CandidateQualityCaseResult
            { CaseId = c.Id, Outcome = "pass", Critical = c.Critical, FinishReason = "stop" }).ToList(),
        RecordedUtc = Now
    };

    internal static CandidateQualityEvidence GeneralChatQuality(CandidateEvidenceIdentity identity,
        bool historicalV1 = false)
    {
        CandidateEvaluationProfileDefinition profile = CandidateEvaluationProfileCatalog.GeneralChat;
        var environment = Environment();
        if (historicalV1)
            environment.WorkloadFingerprint = "quality-workload-sha256:ca7ee8a13fa3fe854f571f32f9b1aa9dc56b69c3abaf38e2bc6fcddb804734e9";
        else
            environment.WorkloadFingerprint = profile.QualityWorkloadFingerprint;
        return new CandidateQualityEvidence
        {
            RecordVersion = 3, InvocationId = Guid.NewGuid().ToString("N"), Identity = identity,
            Capability = profile.Capability, SuiteId = profile.QualitySuiteId,
            SuiteVersion = historicalV1 ? 1 : profile.QualitySuiteVersion,
            SuiteSha256 = historicalV1
                ? "46112438167d7ffb202af89db8c12aea2c66c9c9c36cb903921822555dc613a4"
                : profile.QualitySuiteSha256,
            QualityProfileId = profile.QualityProfileId, QualityProfileVersion = profile.QualityProfileVersion,
            QualityProfileSha256 = profile.QualityProfileSha256, Environment = environment,
            ExecutionStatus = "Completed", ExpectedCaseCount = profile.QualityExpectedCaseCount,
            PassCount = profile.QualityExpectedCaseCount, Completed = true,
            Cases = profile.QualityCases.Select(item => new CandidateQualityCaseResult
            {
                CaseId = item.Id, Outcome = "pass", Critical = item.Critical, FinishReason = "stop"
            }).ToList(),
            RecordedUtc = Now
        };
    }

    internal static CandidatePerformanceEvidence Performance(CandidateEvidenceIdentity identity) => new()
    {
        RecordVersion = 3, Identity = identity, Capability = CandidateTextQualitySuite.Capability,
        Environment = PerformanceEnvironment(), WorkloadId = CandidateTextPerformanceWorkload.Id,
        WorkloadVersion = CandidateTextPerformanceWorkload.Version,
        WorkloadSha256 = CandidateTextPerformanceWorkload.Sha256,
        ExecutionStatus = "Completed", ExpectedRepetitions = CandidateTextPerformanceWorkload.Repetitions,
        Completed = true, Repetitions = 3, TokenCount = 90,
        Samples = Enumerable.Range(1, 3).Select(i => new CandidatePerformanceSample
        {
            Repetition = i, Outcome = "pass", FinishReason = "stop", PromptTokens = 20,
            PromptMilliseconds = 100, GeneratedTokens = 30, DecodeMilliseconds = 300,
            CompletionMilliseconds = 450, TimeToFirstTokenMilliseconds = 90
        }).ToList(),
        Metrics = new List<CandidateMeasuredMetric>
        {
            new() { Name = "prompt_tok_per_s", Unit = "tokens/s", Value = 200 },
            new() { Name = "decode_tok_per_s", Unit = "tokens/s", Value = 100 },
            new() { Name = "completion_ms", Unit = "ms", Value = 450 },
            new() { Name = "ttft_ms", Unit = "ms", Value = 90 }
        },
        RecordedUtc = Now
    };

    private static CandidatePerformanceEvidence PerformanceWithDecode(CandidateEvidenceIdentity identity,
        double decodeTokensPerSecond)
    {
        CandidatePerformanceEvidence evidence = Performance(identity);
        double decodeMillisecondsPerSample = 1000d * evidence.TokenCount / decodeTokensPerSecond
            / evidence.Samples.Count;
        foreach (CandidatePerformanceSample sample in evidence.Samples)
            sample.DecodeMilliseconds = decodeMillisecondsPerSample;
        evidence.Metrics.Single(metric => metric.Name == CandidatePerformanceMetric.DecodeTokensPerSecondId)
            .Value = 1000d * evidence.TokenCount / evidence.Samples.Sum(sample => sample.DecodeMilliseconds);
        foreach (CandidatePerformanceSample sample in evidence.Samples)
        {
            sample.CompletionMilliseconds = 400;
            sample.TimeToFirstTokenMilliseconds = 80;
        }
        evidence.Metrics.Single(metric => metric.Name == CandidatePerformanceMetric.CompletionMillisecondsId).Value = 400;
        evidence.Metrics.Single(metric => metric.Name == CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId).Value = 80;
        return evidence;
    }

    private ModelDiscoveryCandidate AddComparisonCandidate(string repoId, string managedFileName)
    {
        const string revision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        string repoFilePath = "weights/" + managedFileName;
        ModelDiscoveryCandidate candidate = new()
        {
            Provider = ModelDiscoveryIdentity.Provider,
            ProviderAdapterVersion = ModelDiscoveryIdentity.AdapterVersion,
            RepoId = repoId,
            Revision = revision,
            RepoFilePath = repoFilePath,
            Key = ModelDiscoveryIdentity.Key(repoId, revision, repoFilePath),
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, repoFilePath),
            DeclaredLicense = "MIT",
            FirstSeenUtc = Now,
            LastSeenUtc = Now
        };
        ModelDiscoveryCatalog catalog = _catalog.Load();
        catalog.Candidates.Add(candidate);
        _catalog.Save(catalog);

        byte[] payload = Encoding.UTF8.GetBytes("comparison payload for " + managedFileName);
        File.WriteAllBytes(Path.Combine(_models, managedFileName), payload);
        ModelDownloadProvenanceManifestStore.Upsert(_models, managedFileName, new ModelDownloadProvenance
        {
            RepoId = repoId,
            Revision = revision,
            ModelCardUrl = "https://huggingface.co/" + repoId + "/tree/" + revision,
            RepoFilePath = repoFilePath,
            PinnedSourceUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, repoFilePath),
            LicenseMetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "MIT",
            MetadataObservedUtc = Now.AddSeconds(-1),
            ComputedSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            IntegrityStatus = "unavailable",
            SizeBytes = payload.Length,
            FileLastWriteUtc = Now,
            CompletedUtc = Now
        });
        return candidate;
    }

    private static CandidateUtilityPolicyDefinition UtilityPolicy() => new()
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
        ConfigurationFingerprint = ActualLaunchConfiguration,
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

    /// <summary>Live actual-launch facts matching the fixture evidence: the active model is served by the
    /// exact ld1 launch, and any planned Candidate launch uses the same descriptor.</summary>
    private FakeLaunchFence Fence() => new(Path.Combine(_models, "active.gguf"));

    internal sealed class FakeLaunchFence : ICandidateLaunchFenceSource
    {
        private readonly string _activePath;
        internal string? LiveConfiguration = ActualLaunchConfiguration;
        internal string? PlannedConfiguration = ActualLaunchConfiguration;
        internal CandidateLaunchHost? Host = new("text-cuda", "gpu-1", "text-cuda:runtime-v1", "binary-sha");
        internal int Reads;
        internal Action<int>? OnRead;

        internal FakeLaunchFence(string activePath) => _activePath = activePath;

        internal static TextLaunchDescriptor Descriptor(string configuration, string modelPath) =>
            new(1, "text-cuda", System.Collections.Immutable.ImmutableArray<string>.Empty,
                System.Collections.Immutable.ImmutableArray<string>.Empty, configuration, modelPath);

        public TextLaunchDescriptor? ReadVerifiedFence()
        {
            OnRead?.Invoke(++Reads);
            return LiveConfiguration is null ? null : Descriptor(LiveConfiguration, _activePath);
        }

        public Task<CandidateLaunchHost?> CaptureHostAsync(string backend, CancellationToken cancellationToken) =>
            Task.FromResult(Host);

        public bool IsLiveExactModel(string modelPath, string expectedSha256) => true;

        public Task<TextLaunchDescriptorResult> PlanAsync(string modelPath, CancellationToken cancellationToken) =>
            Task.FromResult(PlannedConfiguration is null
                ? new TextLaunchDescriptorResult(null, "unknown_argument")
                : new TextLaunchDescriptorResult(Descriptor(PlannedConfiguration, modelPath), null));
    }
    // Evidence must be measured under an actual (ld1) launch descriptor to be proof-eligible.
    internal const string ActualLaunchConfiguration = "ld1-sha256:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static CandidateEvaluationEnvironment Environment(string hardware = "gpu-1") => new()
    {
        HardwareFingerprint = hardware, RuntimeFingerprint = "text-cuda:runtime-v1",
        BinaryFingerprint = "binary-sha", ConfigurationFingerprint = ActualLaunchConfiguration,
        WorkloadFingerprint = CandidateTextQualitySuite.WorkloadFingerprint
    };

    internal static CandidateEvaluationEnvironment PerformanceEnvironment(string hardware = "gpu-1") => new()
    {
        HardwareFingerprint = hardware, RuntimeFingerprint = "text-cuda:runtime-v1",
        BinaryFingerprint = "binary-sha", ConfigurationFingerprint = ActualLaunchConfiguration,
        WorkloadFingerprint = CandidateTextPerformanceWorkload.WorkloadFingerprint
    };

    private void WriteProvenance(string? revision = null, string? path = null, bool legacy = false,
        bool missingLicenseEvidence = false)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("payload-1"))).ToLowerInvariant();
        string sourcePath = path ?? _candidate.RepoFilePath;
        var provenance = new ModelDownloadProvenance
        {
            RepoId = _candidate.RepoId, Revision = revision ?? Revision,
            ModelCardUrl = "https://huggingface.co/org/model/tree/" + (revision ?? Revision),
            RepoFilePath = legacy ? null : sourcePath,
            PinnedSourceUrl = legacy ? null : ModelDiscoveryIdentity.PinnedUrl("org/model", revision ?? Revision, sourcePath),
            LicenseMetadataStatus = missingLicenseEvidence
                ? ModelLicenseMetadataStatus.NotProvided : ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = missingLicenseEvidence ? null : "MIT",
            MetadataObservedUtc = missingLicenseEvidence ? null : Now.AddSeconds(-1),
            ComputedSha256 = hash, IntegrityStatus = "unavailable",
            SizeBytes = 9, FileLastWriteUtc = Now, CompletedUtc = Now
        };
        ModelDownloadProvenanceManifestStore.Upsert(_models, "model.gguf", provenance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
