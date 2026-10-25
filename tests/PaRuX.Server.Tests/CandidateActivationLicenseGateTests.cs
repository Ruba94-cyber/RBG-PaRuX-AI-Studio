using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateActivationLicenseGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-activation-license-gate", Guid.NewGuid().ToString("N"));
    private readonly string _models;
    private readonly string _state;
    private readonly byte[] _candidateBytes = Encoding.UTF8.GetBytes("candidate-model-payload");
    private readonly byte[] _incumbentBytes = Encoding.UTF8.GetBytes("incumbent-model-payload");
    private readonly byte[] _previousBytes = Encoding.UTF8.GetBytes("previous-model-payload");
    private readonly ModelDiscoveryCandidate _candidate;
    private readonly ModelDiscoveryCandidate _incumbent;
    private readonly ModelDiscoveryCatalogStore _catalog;
    private readonly CandidateEvidenceStore _ledger;
    private readonly CandidateEvidenceService _evidence;
    private readonly CandidateUtilityComparisonProof _proof;
    private readonly CandidateRecommendationStore _store;
    private readonly CountingStore _countingStore;
    private readonly ActivationRuntime _runtime;
    private readonly ActivationFence _fence;
    private readonly FixedProofSource _proofs;

    public CandidateActivationLicenseGateTests()
    {
        _models = Path.Combine(_root, "models");
        _state = Path.Combine(_root, "state");
        Directory.CreateDirectory(_models);

        WriteModel("challenger.gguf", _candidateBytes);
        WriteModel("baseline.gguf", _incumbentBytes);
        WriteModel("previous.gguf", _previousBytes);

        _candidate = Candidate("org/challenger", "challenger.gguf");
        _incumbent = Candidate("org/baseline", "baseline.gguf");
        ModelDiscoveryCandidate previous = Candidate("org/previous", "previous.gguf");
        _catalog = new ModelDiscoveryCatalogStore(_state);
        _catalog.Save(new ModelDiscoveryCatalog
        {
            Candidates = new List<ModelDiscoveryCandidate> { _candidate, _incumbent, previous }
        });
        _ledger = new CandidateEvidenceStore(_state);
        _evidence = new CandidateEvidenceService(_catalog, _ledger, _models);
        WriteProvenance(_candidate, _candidateBytes);
        WriteProvenance(_incumbent, _incumbentBytes);
        WriteProvenance(previous, _previousBytes);

        _proof = Proof("org/challenger", "challenger.gguf", _candidateBytes,
            "org/baseline", "baseline.gguf", _incumbentBytes);
        _store = new CandidateRecommendationStore(Path.Combine(_state, "recommendations"));
        SeedActiveIncumbent();
        _countingStore = new CountingStore(_store);
        _runtime = new ActivationRuntime(_models, _proof.Incumbent);
        _fence = new ActivationFence(_runtime);
        _proofs = new FixedProofSource(_proof, _evidence);
    }

    [Fact]
    public async Task Missing_license_evidence_refuses_before_pending_and_preserves_active_incumbent()
    {
        WriteProvenance(_candidate, _candidateBytes, missingLicenseEvidence: true);

        CandidateRecommendationSnapshot before = _store.Load();
        RecommendationTransitionResult result = await PromoteAsync();

        Assert.Equal(RecommendationTransitionResult.Refused, result.Outcome);
        Assert.Equal("licence_evidence_missing", result.Reason);
        AssertRefusalDidNotWriteOrChangeActive(before);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("hash")]
    [InlineData("file")]
    public async Task Changed_license_provenance_revision_or_hash_refuses_before_pending(string changedPart)
    {
        if (changedPart == "revision")
            WriteProvenance(_candidate, _candidateBytes, revision: new string('b', 40));
        else if (changedPart == "hash")
            WriteProvenance(_candidate, _candidateBytes, computedSha256: new string('f', 64));
        else
            WriteModel("challenger.gguf", Encoding.UTF8.GetBytes("changed-model-payload"));

        CandidateRecommendationSnapshot before = _store.Load();
        RecommendationTransitionResult result = await PromoteAsync();

        Assert.Equal(RecommendationTransitionResult.Refused, result.Outcome);
        Assert.Equal("licence_evidence_changed", result.Reason);
        AssertRefusalDidNotWriteOrChangeActive(before);
    }

    [Fact]
    public async Task Matching_license_provenance_keeps_successful_promotion_behavior()
    {
        CandidateRecommendationSnapshot before = _store.Load();

        RecommendationTransitionResult result = await PromoteAsync();

        Assert.True(result.IsCommitted, result.Reason);
        CandidateRecommendationSnapshot after = _store.Load();
        Assert.Equal(before.Generation + 2, after.Generation);
        Assert.Equal(_candidate.Key, after.GetActiveRecord()!.Proof.Challenger.Identity.CandidateKey);
        Assert.Null(after.Pending);
        Assert.Equal(1, _runtime.Loads.Count);
        Assert.Equal(Path.Combine(_models, "challenger.gguf"), _runtime.Loads[0]);
    }

    [Fact]
    public void Restarted_service_enforces_persisted_licence_consent_with_typed_reasons()
    {
        // "Restart": a brand-new service over the same on-disk catalog, ledger and provenance sidecar.
        CandidateEvidenceService Restarted() => new(new ModelDiscoveryCatalogStore(_state), new CandidateEvidenceStore(_state), _models);
        Assert.Null(Restarted().CheckActivationLicenseEvidence(_candidate.Key, "challenger.gguf", null));

        // The catalog now declares a licence that needs review while the stored record still names the old one: refused.
        ModelDiscoveryCatalog catalog = _catalog.Load();
        catalog.Candidates.Single(item => item.Key == _candidate.Key).DeclaredLicense = "llama3";
        _catalog.Save(catalog);
        Assert.Equal("licence_evidence_changed", Restarted().CheckActivationLicenseEvidence(_candidate.Key, "challenger.gguf", null));

        ModelDownloadProvenance provenance = ModelDownloadProvenanceManifestStore.Get(_models, "challenger.gguf")!;
        provenance.PublisherDeclaredLicense = "llama3";
        provenance.LicenseMetadataStatus = ModelLicenseReviewRules.GetMetadataStatus("llama3", metadataRetrieved: true);
        provenance.ReviewRequired = true;
        provenance.ReviewAcknowledged = true;
        provenance.ReviewAcknowledgedUtc = provenance.CompletedUtc.AddMinutes(-1);
        // Re-recorded with the user's consent: accepted again after another restart.
        ModelDownloadProvenanceManifestStore.Upsert(_models, "challenger.gguf", provenance);
        Assert.Null(Restarted().CheckActivationLicenseEvidence(_candidate.Key, "challenger.gguf", null));

        // Evidence removed entirely: a different typed reason than a changed record.
        WriteProvenance(_candidate, _candidateBytes, missingLicenseEvidence: true);
        Assert.Equal("licence_evidence_missing", Restarted().CheckActivationLicenseEvidence(_candidate.Key, "challenger.gguf", null));
    }

    [Fact]
    public async Task Stale_evaluation_bytes_return_changed_license_reason_when_proof_has_no_winner()
    {
        CandidateEvidenceIdentity current = (await _evidence.TryCaptureSnapshotAsync(
            _candidate.Key, "challenger.gguf", CancellationToken.None))!;
        CandidateEvidenceIdentity stale = WithHash(current, Sha256(_previousBytes));
        _proofs.ReturnNoProof = true;
        _proofs.OnCreateProof = () => _ledger.AddQuality(CandidateEvidenceServiceTests.GeneralChatQuality(stale));

        CandidateRecommendationSnapshot before = _store.Load();
        RecommendationTransitionResult result = await PromoteAsync();

        Assert.Equal(RecommendationTransitionResult.Refused, result.Outcome);
        Assert.Equal("licence_evidence_changed", result.Reason);
        Assert.Equal(1, _proofs.CreateProofCalls);
        AssertRefusalDidNotWriteOrChangeActive(before);
    }

    [Fact]
    public async Task Stale_performance_bytes_are_not_masked_by_current_quality_evidence()
    {
        CandidateEvidenceIdentity current = (await _evidence.TryCaptureSnapshotAsync(
            _candidate.Key, "challenger.gguf", CancellationToken.None))!;
        CandidateEvidenceIdentity stale = WithHash(current, Sha256(_previousBytes));
        _ledger.AddQuality(CandidateEvidenceServiceTests.GeneralChatQuality(current));
        _ledger.AddPerformance(CandidateEvidenceServiceTests.Performance(stale));
        _proofs.ReturnNoProof = true;

        CandidateRecommendationSnapshot before = _store.Load();
        RecommendationTransitionResult result = await PromoteAsync();

        Assert.Equal(RecommendationTransitionResult.Refused, result.Outcome);
        Assert.Equal("licence_evidence_changed", result.Reason);
        Assert.Equal(0, _proofs.CreateProofCalls);
        AssertRefusalDidNotWriteOrChangeActive(before);
    }

    private static CandidateEvidenceIdentity WithHash(CandidateEvidenceIdentity identity, string hash) => new()
    {
        Provider = identity.Provider,
        CandidateKey = identity.CandidateKey,
        RepoId = identity.RepoId,
        Revision = identity.Revision,
        RepoFilePath = identity.RepoFilePath,
        PinnedSourceUrl = identity.PinnedSourceUrl,
        ManagedFileName = identity.ManagedFileName,
        SizeBytes = identity.SizeBytes,
        LocalSha256 = hash
    };

    private async Task<RecommendationTransitionResult> PromoteAsync()
    {
        var service = new CandidateRecommendationService(_countingStore, _runtime, _proofs, _fence,
            _models, healthBudget: TimeSpan.FromMilliseconds(100), pinRoot: _root);
        return await service.PromoteAsync(_candidate.Key, "challenger.gguf", _store.Load().Generation,
            consent: true, CancellationToken.None);
    }

    private void AssertRefusalDidNotWriteOrChangeActive(CandidateRecommendationSnapshot before)
    {
        CandidateRecommendationSnapshot after = _store.Load();
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Active!.Proof.ProofSha256, after.Active!.Proof.ProofSha256);
        Assert.Equal(_incumbent.Key, after.GetActiveRecord()!.Proof.Challenger.Identity.CandidateKey);
        Assert.Null(after.Pending);
        Assert.Equal(0, _countingStore.WriteAttempts);
        Assert.Empty(_runtime.Loads);
        Assert.Equal(0, _runtime.Stops);
    }

    private void SeedActiveIncumbent()
    {
        CandidateUtilityComparisonProof seedProof = Proof("org/baseline", "baseline.gguf", _incumbentBytes,
            "org/previous", "previous.gguf", _previousBytes);
        Guid transaction = Guid.NewGuid();
        Assert.True(_store.TryBeginPending(0, transaction, seedProof, Admission(seedProof, seedProof.Incumbent),
            Checks(seedProof, seedProof.Incumbent)));
        Assert.True(_store.TryCommitPending(1, transaction, Admission(seedProof, seedProof.Challenger),
            Checks(seedProof, seedProof.Challenger)));
    }

    private CandidateLaunchAdmission Admission(CandidateUtilityComparisonProof proof, CandidateProofSide side) => new(
        proof.QualityEnvironment.ConfigurationFingerprint, "text-cuda",
        proof.QualityEnvironment.HardwareFingerprint, proof.QualityEnvironment.RuntimeFingerprint,
        proof.QualityEnvironment.BinaryFingerprint, Path.Combine(_models, side.Identity.ManagedFileName), DateTime.UtcNow);

    private RecommendationTransitionChecks Checks(CandidateUtilityComparisonProof proof, CandidateProofSide side) => new(
        () => new TextLaunchDescriptor(1, "text-cuda", ImmutableArray<string>.Empty, ImmutableArray<string>.Empty,
            proof.QualityEnvironment.ConfigurationFingerprint, Path.Combine(_models, side.Identity.ManagedFileName)),
        (_, _) => true, _ => true, () => true);

    private static ModelDiscoveryCandidate Candidate(string repoId, string managedFileName)
    {
        const string revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string repoFilePath = "weights/" + managedFileName;
        DateTime now = DateTime.UtcNow;
        return new ModelDiscoveryCandidate
        {
            Provider = ModelDiscoveryIdentity.Provider,
            ProviderAdapterVersion = ModelDiscoveryIdentity.AdapterVersion,
            RepoId = repoId,
            Revision = revision,
            RepoFilePath = repoFilePath,
            Key = ModelDiscoveryIdentity.Key(repoId, revision, repoFilePath),
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, repoFilePath),
            DeclaredLicense = "MIT",
            Lifecycle = "Candidate",
            Disposition = "active",
            FirstSeenUtc = now,
            LastSeenUtc = now
        };
    }

    private void WriteProvenance(ModelDiscoveryCandidate candidate, byte[] bytes, string? revision = null,
        string? computedSha256 = null, bool missingLicenseEvidence = false)
    {
        string recordedRevision = revision ?? candidate.Revision;
        string path = candidate.RepoFilePath;
        ModelDownloadProvenanceManifestStore.Upsert(_models, Path.GetFileName(path), new ModelDownloadProvenance
        {
            RepoId = candidate.RepoId,
            Revision = recordedRevision,
            ModelCardUrl = "https://huggingface.co/" + candidate.RepoId + "/tree/" + recordedRevision,
            RepoFilePath = path,
            PinnedSourceUrl = ModelDiscoveryIdentity.PinnedUrl(candidate.RepoId, recordedRevision, path),
            LicenseMetadataStatus = missingLicenseEvidence
                ? ModelLicenseMetadataStatus.NotProvided : ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = missingLicenseEvidence ? null : candidate.DeclaredLicense,
            MetadataObservedUtc = missingLicenseEvidence ? null : DateTime.UtcNow,
            ComputedSha256 = computedSha256 ?? Sha256(bytes),
            IntegrityStatus = "unavailable",
            SizeBytes = bytes.Length,
            FileLastWriteUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        });
    }

    private void WriteModel(string fileName, byte[] bytes) => File.WriteAllBytes(Path.Combine(_models, fileName), bytes);

    private static CandidateUtilityComparisonProof Proof(string challengerRepo, string challengerFile, byte[] challengerBytes,
        string incumbentRepo, string incumbentFile, byte[] incumbentBytes)
    {
        CandidateUtilityComparisonProof template = RecFx.Proof(challengerRepo, challengerFile, 'a', incumbentRepo,
            incumbentFile, 'b');
        CandidateProofSide challenger = RewriteSide(template.Challenger, challengerRepo, challengerFile, challengerBytes);
        CandidateProofSide incumbent = RewriteSide(template.Incumbent, incumbentRepo, incumbentFile, incumbentBytes);
        CandidateUtilityComparisonProof proof = template with
        {
            Challenger = challenger,
            Incumbent = incumbent,
            ProofSha256 = string.Empty
        };
        return proof with { ProofSha256 = CandidateUtilityProofDigest.Proof(proof) };
    }

    private static CandidateProofSide RewriteSide(CandidateProofSide side, string repoId, string managedFileName, byte[] bytes)
    {
        const string revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string repoFilePath = "weights/" + managedFileName;
        var identity = new CandidateProofIdentity(ModelDiscoveryIdentity.Provider,
            ModelDiscoveryIdentity.Key(repoId, revision, repoFilePath), repoId, revision, repoFilePath,
            ModelDiscoveryIdentity.PinnedUrl(repoId, revision, repoFilePath), managedFileName, bytes.Length, Sha256(bytes));
        return side with { Identity = identity };
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FixedProofSource(CandidateUtilityComparisonProof proof, CandidateEvidenceService evidence)
        : IRecommendationProofSource
    {
        public int CreateProofCalls { get; private set; }
        public bool ReturnNoProof { get; set; }
        public Action? OnCreateProof { get; set; }

        public Task<CandidateUtilityProofResult> CreateProofAsync(string candidateKey, string managedFileName,
            CancellationToken cancellationToken)
        {
            CreateProofCalls++;
            OnCreateProof?.Invoke();
            if (ReturnNoProof)
                return Task.FromResult(new CandidateUtilityProofResult(new CandidateUtilityComparisonResult(
                    CandidateUtilityComparisonDisposition.NoWinner, "quality_below_reliability_floor"), null));
            return Task.FromResult(new CandidateUtilityProofResult(new CandidateUtilityComparisonResult(
                CandidateUtilityComparisonDisposition.CandidateMeetsReferenceMargin, "candidate_meets_reference_margin"), proof));
        }

        public string? CheckLicenseEvidence(string candidateKey, string managedFileName, CandidateProofSide? evaluatedSide) =>
            evidence.CheckActivationLicenseEvidence(candidateKey, managedFileName, evaluatedSide);

        public async Task<IDisposable?> CaptureLicenseEvidenceReadLeaseAsync(string candidateKey, string managedFileName,
            CandidateProofSide? evaluatedSide, CancellationToken cancellationToken) =>
            await evidence.CaptureActivationLicenseEvidenceReadLeaseAsync(candidateKey, managedFileName,
                evaluatedSide, cancellationToken).ConfigureAwait(false);
        public bool IsSnapshotCurrent(CandidateUtilityComparisonProof current) => true;
        public bool? IsSideCurrent(CandidateProofSide side) => true;
        public bool IsPolicyCurrent(CandidateUtilityComparisonProof current) => true;
    }

    private sealed class CountingStore(ICandidateRecommendationStore inner) : ICandidateRecommendationStore
    {
        public int WriteAttempts { get; private set; }
        public CandidateRecommendationSnapshot Load() => inner.Load();
        public bool TryBeginPending(long expectedGeneration, Guid transactionId, CandidateUtilityComparisonProof proof,
            CandidateLaunchAdmission admission, RecommendationTransitionChecks checks)
        {
            WriteAttempts++;
            return inner.TryBeginPending(expectedGeneration, transactionId, proof, admission, checks);
        }
        public bool TryCommitPending(long expectedGeneration, Guid transactionId, CandidateLaunchAdmission admission,
            RecommendationTransitionChecks checks)
        {
            WriteAttempts++;
            return inner.TryCommitPending(expectedGeneration, transactionId, admission, checks);
        }
        public bool TryBeginRollback(long expectedGeneration, Guid transactionId, CandidateLaunchAdmission liveAdmission,
            RecommendationTransitionChecks checks)
        {
            WriteAttempts++;
            return inner.TryBeginRollback(expectedGeneration, transactionId, liveAdmission, checks);
        }
        public bool TryClearAuthority(long expectedGeneration)
        {
            WriteAttempts++;
            return inner.TryClearAuthority(expectedGeneration);
        }
        public bool TryAbortPending(long expectedGeneration, Guid transactionId)
        {
            WriteAttempts++;
            return inner.TryAbortPending(expectedGeneration, transactionId);
        }
    }

    private sealed class ActivationRuntime(string modelsDirectory, CandidateProofSide initiallyServing) : IRecommendationRuntime
    {
        private string? _currentPath = Path.GetFullPath(Path.Combine(modelsDirectory, initiallyServing.Identity.ManagedFileName));
        private string? _currentSha = initiallyServing.Identity.LocalSha256;
        public bool GamingActive => false;
        public bool StopRequested => false;
        public bool TextRuntimeRunning => _currentPath is not null;
        public List<string> Loads { get; } = new();
        public int Stops { get; private set; }
        public string? CurrentPath => _currentPath;
        public string? CurrentSha => _currentSha;

        public Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());

        public Task<RuntimeLoadResult> LoadExactAsync(IAsyncDisposable writer, string role, string candidateKey,
            string path, string sha256, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.GetFullPath(path);
            Loads.Add(fullPath);
            if (!File.Exists(fullPath) || Sha256(File.ReadAllBytes(fullPath)) != sha256)
                return Task.FromResult(new RuntimeLoadResult { Success = false, Role = role, Message = "exact file mismatch" });
            _currentPath = fullPath;
            _currentSha = sha256;
            return Task.FromResult(new RuntimeLoadResult { Success = true, Role = role, ModelPath = fullPath });
        }

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public bool StopText()
        {
            Stops++;
            _currentPath = null;
            _currentSha = null;
            return true;
        }
    }

    private sealed class ActivationFence(ActivationRuntime runtime) : ICandidateLaunchFenceSource
    {
        public TextLaunchDescriptor? ReadVerifiedFence() => runtime.CurrentPath is null ? null : Descriptor(runtime.CurrentPath);
        public Task<CandidateLaunchHost?> CaptureHostAsync(string backend, CancellationToken cancellationToken) =>
            Task.FromResult<CandidateLaunchHost?>(RecFx.Host);
        public Task<TextLaunchDescriptorResult> PlanAsync(string modelPath, CancellationToken cancellationToken) =>
            Task.FromResult(new TextLaunchDescriptorResult(Descriptor(Path.GetFullPath(modelPath)), null));
        public bool IsLiveExactModel(string modelPath, string expectedSha256) => runtime.CurrentPath is not null
            && PathEquals(runtime.CurrentPath, modelPath)
            && string.Equals(runtime.CurrentSha, expectedSha256, StringComparison.Ordinal);

        private TextLaunchDescriptor Descriptor(string modelPath) => new(1, "text-cuda", ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty, RecFx.ActualConfig, modelPath);
        private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left),
            Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
