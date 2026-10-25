using System.Net;
using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Exercises the immutable Candidate identity across discovery, managed acquisition and evidence evaluation.
/// Transfer and evaluation boundaries are deterministic fakes; this test never starts a model runtime.</summary>
public sealed class CandidateAcquisitionEvaluationIntegrationTests : IDisposable
{
    private static readonly DateTime EvaluationUtc = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Revision = new('c', 40);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-candidate-acquisition-e2e-", Guid.NewGuid().ToString("N"));
    private readonly string _models;
    private readonly string _state;

    public CandidateAcquisitionEvaluationIntegrationTests()
    {
        _models = Path.Combine(_root, "models");
        _state = Path.Combine(_root, "state");
        Directory.CreateDirectory(_state);
    }

    [Fact]
    public async Task Discovered_candidate_download_and_evaluation_keep_one_verified_identity()
    {
        using IDisposable modelsScope = ModelDownloadService.UseModelsDirectoryForTests(_models);
        byte[] payload = Encoding.UTF8.GetBytes("deterministic test fixture; not a model");
        string sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        const string repoId = "org/e2e-model";
        const string repoPath = "weights/model.gguf";
        string candidateKey = ModelDiscoveryIdentity.Key(repoId, Revision, repoPath);
        var discoveredCandidate = new ModelDiscoveryCandidate
        {
            Key = candidateKey,
            RepoId = repoId,
            Revision = Revision,
            RepoFilePath = repoPath,
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, Revision, repoPath),
            DeclaredLicense = "custom-research-only",
            DeclaredPipelineTag = "text-generation",
            DeclaredTags = new List<string> { "gguf", "text-generation" },
            DeclaredSizeBytes = payload.Length,
            DeclaredSha256 = sha256
        };
        var catalog = new ModelDiscoveryCatalogStore(_state);
        var discovery = new ModelDiscoveryService(new FixedProvider(discoveredCandidate), catalog);

        ModelDiscoveryRefreshResult refresh = await discovery.RefreshAsync(CancellationToken.None);
        Assert.Equal("refreshed", refresh.Outcome);
        ModelDiscoveryCandidate candidate = Assert.Single(discovery.GetCatalog().Candidates);
        Assert.Equal(candidateKey, candidate.Key);
        Assert.Equal(Revision, candidate.Revision);
        Assert.Equal("active", candidate.Disposition);

        DateTime observedUtc = DateTime.UtcNow.AddSeconds(-1);
        DateTime acknowledgedUtc = DateTime.UtcNow;
        var license = new ModelDownloadLicenseEvidence
        {
            MetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "custom-research-only",
            MetadataObservedUtc = observedUtc,
            ReviewRequired = true,
            ReviewAcknowledged = true,
            ReviewAcknowledgedUtc = acknowledgedUtc
        };
        var transfer = new TransferHandler(payload, sha256);
        var downloads = new ModelDownloadService(new HttpClient(transfer), new HttpClient(transfer));
        ModelDownloadStatus started = await downloads.CreateCandidateDownloadAsync(
            candidate, license, observedUtc, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForDownloadAsync(downloads, started.Id);
        Assert.Equal("completed", completed.Status);
        Assert.Equal("verified", completed.VerificationStatus);

        ManagedModelInfo managed = Assert.Single(downloads.ListManagedModels());
        ModelDownloadProvenance provenance = Assert.IsType<ModelDownloadProvenance>(managed.Provenance);
        Assert.Equal(candidate.RepoId, provenance.RepoId);
        Assert.Equal(candidate.Revision, provenance.Revision);
        Assert.Equal(candidate.RepoFilePath, provenance.RepoFilePath);
        Assert.Equal(candidate.PinnedUrl, provenance.PinnedSourceUrl);
        Assert.Equal(sha256, provenance.ExpectedSha256);
        Assert.Equal(sha256, provenance.ComputedSha256);
        Assert.Equal(payload.Length, provenance.SizeBytes);
        Assert.Equal("verified", provenance.IntegrityStatus);
        Assert.Equal(acknowledgedUtc, provenance.ReviewAcknowledgedUtc);

        string managedPath = Path.Combine(_models, "model.gguf");
        var evidence = new CandidateEvidenceService(catalog, new CandidateEvidenceStore(_state), _models,
            () => EvaluationUtc);
        CandidateEvidenceIdentity identity = Assert.IsType<CandidateEvidenceIdentity>(
            await evidence.TryCaptureSnapshotAsync(candidateKey, "model.gguf", CancellationToken.None));
        Assert.Equal(candidateKey, identity.CandidateKey);
        Assert.Equal(Revision, identity.Revision);
        Assert.Equal(repoPath, identity.RepoFilePath);
        Assert.Equal(sha256, identity.LocalSha256);

        // A byte change under the same managed name must invalidate the snapshot before any evaluation runs.
        byte[] corrupted = payload.ToArray();
        corrupted[0] ^= 0x20;
        await File.WriteAllBytesAsync(managedPath, corrupted);
        Assert.Null(await evidence.TryCaptureSnapshotAsync(candidateKey, "model.gguf", CancellationToken.None));
        await File.WriteAllBytesAsync(managedPath, payload);

        var quality = new PersistingQualityRunner(evidence);
        var performance = new PersistingPerformanceRunner(evidence);
        using var jobs = new CandidateEvaluationJobService(catalog, evidence, quality, performance,
            CancellationToken.None, () => EvaluationUtc, new CandidateEvaluationJobStore(_state));
        CandidateEvaluationStartResult accepted = jobs.TryStart(candidateKey, "model.gguf", CancellationToken.None);
        Assert.Equal("Accepted", accepted.Outcome);
        CandidateEvaluationJobStatus result = await WaitForEvaluationAsync(jobs, accepted.JobId!);

        Assert.Equal("Completed", result.Status);
        Assert.Equal("EligibleForFurtherReview", result.Eligibility);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingCapability, result.Capability);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingSuiteId, result.QualitySuiteId);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId, result.PerformanceWorkloadId);
        Assert.Equal(1, quality.Calls);
        Assert.Equal(1, performance.Calls);
        CandidateEvidenceLedger ledger = new CandidateEvidenceStore(_state).Load();
        CandidateQualityEvidence qualityEvidence = Assert.Single(ledger.Quality);
        CandidatePerformanceEvidence performanceEvidence = Assert.Single(ledger.Performance);
        Assert.Equal(identity.CandidateKey, qualityEvidence.Identity.CandidateKey);
        Assert.Equal(identity.LocalSha256, qualityEvidence.Identity.LocalSha256);
        Assert.Equal(identity.CandidateKey, performanceEvidence.Identity.CandidateKey);
        Assert.Equal(identity.LocalSha256, performanceEvidence.Identity.LocalSha256);
    }

    private static async Task<ModelDownloadStatus> WaitForDownloadAsync(ModelDownloadService service, string id)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.JobCompletion);
        while (true)
        {
            ModelDownloadStatus? status = service.Get(id);
            if (status?.Status is "completed" or "failed" or "cancelled") return status;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<CandidateEvaluationJobStatus> WaitForEvaluationAsync(
        CandidateEvaluationJobService service, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            CandidateEvaluationJobStatus status = service.Get(id)!;
            if (status.CompletedUtc is not null) return status;
            await Task.Delay(10, timeout.Token);
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FixedProvider(ModelDiscoveryCandidate candidate) : IModelDiscoveryProvider
    {
        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ModelDiscoveryCandidate>>(new[] { candidate });
        }
    }

    private sealed class TransferHandler(byte[] payload, string sha256) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.Method == HttpMethod.Head
                    ? new ByteArrayContent(Array.Empty<byte>())
                    : new ByteArrayContent(payload)
            };
            if (request.Method == HttpMethod.Head)
            {
                response.Headers.TryAddWithoutValidation("X-Linked-ETag", "\"" + sha256 + "\"");
                response.Headers.TryAddWithoutValidation("X-Linked-Size", payload.Length.ToString());
            }
            return Task.FromResult(response);
        }
    }

    private sealed class PersistingQualityRunner(CandidateEvidenceService evidence) : ICandidateTextQualityRunner
    {
        internal int Calls { get; private set; }

        public async Task<CandidateTextQualityRunResult> RunAsync(string candidateKey, string managedFileName,
            CancellationToken cancellationToken)
        {
            Calls++;
            CandidateEvidenceIdentity? identity = await evidence.TryCaptureSnapshotAsync(candidateKey,
                managedFileName, cancellationToken);
            if (identity is null) return new("Unavailable", false, null);
            CandidateQualityEvidence record = CandidateEvidenceServiceTests.Quality(identity);
            bool committed = await evidence.TryCommitQualityAsync(record, cancellationToken);
            return new("Completed", committed, record);
        }
    }

    private sealed class PersistingPerformanceRunner(CandidateEvidenceService evidence) : ICandidateTextPerformanceRunner
    {
        internal int Calls { get; private set; }

        public async Task<CandidateTextPerformanceRunResult> RunAsync(string candidateKey, string managedFileName,
            CancellationToken cancellationToken)
        {
            Calls++;
            CandidateEvidenceIdentity? identity = await evidence.TryCaptureSnapshotAsync(candidateKey,
                managedFileName, cancellationToken);
            if (identity is null) return new("Unavailable", false, null);
            CandidatePerformanceEvidence record = CandidateEvidenceServiceTests.Performance(identity);
            bool committed = await evidence.TryCommitPerformanceAsync(record, cancellationToken);
            return new("Completed", committed, record);
        }
    }
}
