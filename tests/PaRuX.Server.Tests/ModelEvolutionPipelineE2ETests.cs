using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-16 end-to-end evidence with an unknown-after-build model, fully hermetic: a fake Hugging Face (HTTP handler,
/// no network) publishes new repositories AFTER the "build"; the real discovery service finds them, ONE grouped notice
/// is produced, the user's choice stages them through the real consent-gated download service, a scripted fake runtime
/// supplies the benchmark outcomes into the real evidence ledger, and the real proof/coordinator decide under the
/// declared policy with a real child runtime process swapped on promotion.
/// </summary>
public sealed class ModelEvolutionPipelineE2ETests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-evolution-e2e", Guid.NewGuid().ToString("N"));
    private readonly string _models;
    private readonly string _state;
    private readonly FakeHub _hub = new();

    public ModelEvolutionPipelineE2ETests()
    {
        _models = Path.Combine(_root, "models");
        _state = Path.Combine(_root, "state");
        Directory.CreateDirectory(_state);
    }

    private sealed record Behavior(int Runs, int FailedRuns, double CompletionMs, double TtftMs);

    [Fact]
    public async Task Unknown_candidate_is_discovered_staged_benchmarked_and_promoted_only_on_quality_and_speed_margins()
    {
        using IDisposable modelsScope = ModelDownloadService.UseModelsDirectoryForTests(_models);
        DateTime clock = Now;
        var catalog = new ModelDiscoveryCatalogStore(_state);
        var discovery = new ModelDiscoveryService(
            new HuggingFaceModelDiscoveryProvider(new HttpClient(_hub)), catalog, () => clock) { GroupNoticeRoot = _state };
        var downloads = new ModelDownloadService(new HttpClient(_hub), new HttpClient(_hub));
        var evidence = new CandidateEvidenceService(catalog, new CandidateEvidenceStore(_state), _models, () => Now);
        var store = new CandidateRecommendationStore(_state);

        // ---- Build time: only the incumbent exists. It is staged, benchmarked and served by a real runtime process.
        _hub.Publish("org/incumbent-chat-gguf", 'a', "weights/incumbent.gguf", "apache-2.0");
        await discovery.RefreshAsync(CancellationToken.None);
        ModelDiscoveryCandidate incumbent = Assert.Single(discovery.GetCatalog().Candidates);
        await Stage(downloads, incumbent);
        await Benchmark(evidence, incumbent, new Behavior(5, 0, 450, 90));
        await discovery.MarkNoticeHandledAsync(incumbent.Key, CancellationToken.None);

        using var runtime = new ProcessSwapRuntime(Path.Combine(_root, "runtime.pid"));
        CandidateEvidenceIdentity incumbentIdentity = (await evidence.TryCaptureSnapshotAsync(
            incumbent.Key, "incumbent.gguf", CancellationToken.None))!;
        await runtime.StartAsync(Path.Combine(_models, "incumbent.gguf"), incumbentIdentity.LocalSha256, default);
        int incumbentPid = runtime.CurrentPid!.Value;
        int downloadsBeforeRelease = _hub.ModelDownloads;

        // ---- After the build: the hub publishes models this binary has never heard of (no app update).
        _hub.Publish("org/hyped-popular-gguf", 'b', "weights/hyped.gguf", "apache-2.0");     // popularity/recency only
        _hub.Publish("org/speedy-lite-gguf", 'c', "weights/speedy.gguf", "apache-2.0");      // faster but fails quality
        _hub.Publish("org/almost-there-gguf", 'd', "weights/almost.gguf", "apache-2.0");     // above floor, below incumbent
        _hub.Publish("org/better-chat-gguf", 'e', "weights/better.gguf", "apache-2.0",       // clearly better and faster
            "../evil.gguf", "README.md", "model.safetensors");
        clock = clock.AddMinutes(10);                                         // past the refresh cooldown
        ModelDiscoveryRefreshResult refreshed = await discovery.RefreshAsync(CancellationToken.None);
        Assert.Equal("refreshed", refreshed.Outcome);
        ModelDiscoveryCatalog found = discovery.GetCatalog();
        Assert.Equal(5, found.Candidates.Count);                                  // unsafe / non-GGUF variants rejected
        Assert.DoesNotContain(found.Candidates, c => c.RepoFilePath.Contains("evil") || c.RepoFilePath.EndsWith(".md"));
        Assert.All(found.Candidates, c => Assert.Equal(40, c.Revision.Length));
        ModelDiscoveryCandidate hyped = Pick(found, "org/hyped-popular-gguf");
        Assert.Contains("popular", hyped.DiscoveryCohorts);
        Assert.Contains("new", hyped.DiscoveryCohorts);

        // ONE grouped notice for the refresh, and nothing downloaded or changed yet.
        ModelDiscoveryGroupedNotice notice = Assert.IsType<ModelDiscoveryGroupedNotice>(
            ModelDiscoveryGroupedNoticeBuilder.Build(found, autoEvaluate: false, clock, _state));
        Assert.Equal(4, notice.Groups.Count);
        Assert.False(notice.AutoEvaluate);
        Assert.Equal(downloadsBeforeRelease, _hub.ModelDownloads);
        Assert.Single(downloads.ListManagedModels());
        Assert.Null(store.Load().GetActiveRecord());

        // The user chooses Evaluate for every group; the choice is persisted. Only then are files staged.
        foreach (ModelDiscoveryNoticeGroup group in notice.Groups)
            Assert.Equal("evaluate", (await discovery.SetGroupChoiceAsync(group.GroupKey, "evaluate", default))!.Choice);
        Assert.Null(ModelDiscoveryGroupedNoticeBuilder.Build(discovery.GetCatalog(), false, clock, _state));

        var behaviors = new (string Repo, Behavior Behavior)[]
        {
            ("org/hyped-popular-gguf", new Behavior(5, 0, 440, 88)),   // 2.2% faster: below the 5% margin
            ("org/speedy-lite-gguf", new Behavior(5, 1, 200, 40)),      // 80% reliability: below the 95% floor
            ("org/almost-there-gguf", new Behavior(20, 1, 300, 60)),    // 95% reliability (floor) < 100% incumbent
            ("org/better-chat-gguf", new Behavior(5, 0, 380, 70))
        };
        foreach ((string repo, Behavior behavior) in behaviors)
        {
            ModelDiscoveryCandidate candidate = Pick(discovery.GetCatalog(), repo);
            await Stage(downloads, candidate);
            await Benchmark(evidence, candidate, behavior);
        }
        Assert.Equal(5, downloads.ListManagedModels().Count);

        var proofs = new LedgerProofSource(evidence, Policy(), new SwapActiveSource(runtime), new SwapFence(runtime));
        var service = new CandidateRecommendationService(store, runtime, proofs, new SwapFence(runtime), _models,
            healthBudget: TimeSpan.FromSeconds(5), pinRoot: Path.Combine(_state, "pins"));

        // (c)+(b): popularity/recency, speed-without-quality and a quality regression are all refused with a plain reason.
        foreach ((string repo, string reason, string plain) in new[]
                 {
                     ("org/hyped-popular-gguf", "proof_candidate_does_not_meet_reference_margin", "not clearly faster"),
                     ("org/speedy-lite-gguf", "proof_quality_below_reliability_floor", "even if it is faster"),
                     ("org/almost-there-gguf", "proof_candidate_quality_below_incumbent", "less reliably")
                 })
        {
            ModelDiscoveryCandidate candidate = Pick(discovery.GetCatalog(), repo);
            RecommendationTransitionResult refused = await service.PromoteAsync(candidate.Key,
                Path.GetFileName(candidate.RepoFilePath), store.Load().Generation, true, CancellationToken.None);
            Assert.Equal(RecommendationTransitionResult.Refused, refused.Outcome);
            Assert.Equal(reason, refused.Reason);
            Assert.Contains(plain, CandidateReasonText.Explain(refused.Reason), StringComparison.Ordinal);
            // Persisted state and the running model are untouched.
            CandidateRecommendationSnapshot persisted = new CandidateRecommendationStore(_state).Load();
            Assert.Null(persisted.Pending);
            Assert.Null(persisted.GetActiveRecord());
            Assert.Equal(incumbentPid, runtime.CurrentPid);
            Assert.Equal(new[] { incumbentPid }, runtime.StartedPids.Where(ProcessSwapRuntime.IsAlive).ToArray());
        }

        // (a): the better, faster candidate wins; a NEW process serves it and the old one is really dead.
        ModelDiscoveryCandidate better = Pick(discovery.GetCatalog(), "org/better-chat-gguf");
        RecommendationTransitionResult promoted = await service.PromoteAsync(better.Key, "better.gguf",
            store.Load().Generation, true, CancellationToken.None);
        Assert.True(promoted.IsCommitted, promoted.Outcome + ":" + promoted.Reason);
        CandidateRecommendationRecord active = Assert.IsType<CandidateRecommendationRecord>(
            new CandidateRecommendationStore(_state).Load().GetActiveRecord());
        Assert.Equal(better.Key, active.Proof.Challenger.Identity.CandidateKey);
        Assert.Equal(incumbent.Key, active.Proof.Incumbent.Identity.CandidateKey);
        int betterPid = runtime.CurrentPid!.Value;
        Assert.NotEqual(incumbentPid, betterPid);
        Assert.False(ProcessSwapRuntime.IsAlive(incumbentPid));
        var status = Assert.IsType<CandidateRecommendationRealRuntimeKillTests.RuntimeProcessStatus>(runtime.TryStatus());
        Assert.Equal(Path.Combine(_models, "better.gguf"), status.ModelPath, ignoreCase: true);
        Assert.Equal(active.Proof.Challenger.Identity.LocalSha256, status.Sha256, ignoreCase: true);
        // Licence evidence used for the download is persisted with the file.
        Assert.Equal("apache-2.0", ModelDownloadProvenanceManifestStore.Get(_models, "better.gguf")!.PublisherDeclaredLicense);
        // The evidence behind the win is stored and names the margin that was met.
        Assert.Equal(5, active.Proof.Challenger.QualityInvocationIds.Length);
        Assert.Equal(64, active.Proof.ProofSha256.Length);

        // A later, even better model replaces the first winner; the explicit rollback restores the previous winner in a
        // fresh process and the replaced one is really dead.
        _hub.Publish("org/best-chat-gguf", 'f', "weights/best.gguf", "apache-2.0");
        clock = clock.AddMinutes(10);
        await discovery.RefreshAsync(CancellationToken.None);
        ModelDiscoveryCandidate best = Pick(discovery.GetCatalog(), "org/best-chat-gguf");
        await Stage(downloads, best);
        await Benchmark(evidence, best, new Behavior(5, 0, 330, 60));
        RecommendationTransitionResult second = await service.PromoteAsync(best.Key, "best.gguf",
            store.Load().Generation, true, CancellationToken.None);
        Assert.True(second.IsCommitted, second.Outcome + ":" + second.Reason);
        int bestPid = runtime.CurrentPid!.Value;
        Assert.False(ProcessSwapRuntime.IsAlive(betterPid));

        RecommendationTransitionResult back = await service.RollbackAsync(store.Load().Generation, true, default);
        Assert.True(back.IsCommitted, back.Outcome + ":" + back.Reason);
        Assert.False(ProcessSwapRuntime.IsAlive(bestPid), "the rolled-back runtime process must be really dead");
        Assert.NotEqual(bestPid, runtime.CurrentPid);
        Assert.Equal(Path.Combine(_models, "better.gguf"), runtime.TryStatus()!.ModelPath, ignoreCase: true);
        Assert.Equal(better.Key, new CandidateRecommendationStore(_state).Load().GetActiveRecord()!
            .Proof.Challenger.Identity.CandidateKey);
    }

    private static ModelDiscoveryCandidate Pick(ModelDiscoveryCatalog catalog, string repo) =>
        catalog.Candidates.Single(c => c.RepoId == repo);

    /// <summary>Consent-gated staging: the licence review is acknowledged only when the metadata requires it.</summary>
    private static async Task Stage(ModelDownloadService downloads, ModelDiscoveryCandidate candidate)
    {
        DateTime observed = DateTime.UtcNow.AddSeconds(-1);
        bool review = ModelLicenseReviewRules.RequiresReview(candidate.DeclaredLicense, metadataRetrieved: true);
        var license = new ModelDownloadLicenseEvidence
        {
            MetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = candidate.DeclaredLicense,
            MetadataObservedUtc = observed, ReviewRequired = review, ReviewAcknowledged = review,
            ReviewAcknowledgedUtc = review ? DateTime.UtcNow : null
        };
        ModelDownloadStatus started = await downloads.CreateCandidateDownloadAsync(candidate, license, observed, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ModelDownloadStatus? status;
        while ((status = downloads.Get(started.Id))?.Status is not ("completed" or "failed" or "cancelled"))
            await Task.Delay(20, timeout.Token);
        Assert.Equal("completed", status!.Status);
        Assert.Equal("verified", status.VerificationStatus);
    }

    /// <summary>The scripted fake runtime: records the benchmark outcome of each fresh-process run and the speed workload.</summary>
    private static async Task Benchmark(CandidateEvidenceService evidence, ModelDiscoveryCandidate candidate, Behavior behavior)
    {
        string file = Path.GetFileName(candidate.RepoFilePath);
        CandidateEvidenceIdentity identity = (await evidence.TryCaptureSnapshotAsync(candidate.Key, file, default))!;
        for (int run = 0; run < behavior.Runs; run++)
        {
            CandidateQualityEvidence quality = CandidateEvidenceServiceTests.GeneralChatQuality(identity);
            quality.RecordedUtc = Now.AddSeconds(run);
            if (run < behavior.FailedRuns)
            {
                for (int index = 0; index < 4; index++) quality.Cases[index].Outcome = "fail";
                quality.PassCount = quality.ExpectedCaseCount - 4;
                quality.FailCount = 4;
            }
            Assert.True(await evidence.TryCommitQualityAsync(quality, default));
        }
        CandidatePerformanceEvidence performance = CandidateEvidenceServiceTests.Performance(identity);
        foreach (CandidatePerformanceSample sample in performance.Samples)
        {
            sample.CompletionMilliseconds = behavior.CompletionMs;
            sample.TimeToFirstTokenMilliseconds = behavior.TtftMs;
        }
        performance.Metrics.Single(m => m.Name == CandidatePerformanceMetric.CompletionMillisecondsId).Value = behavior.CompletionMs;
        performance.Metrics.Single(m => m.Name == CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId).Value = behavior.TtftMs;
        performance.RecordedUtc = Now.AddMinutes(1);
        Assert.True(await evidence.TryCommitPerformanceAsync(performance, default));
    }

    private static CandidateUtilityPolicyDefinition Policy() => new()
    {
        PolicyId = "parux.utility.chat-general.e2e",
        Capability = CandidateGeneralChatQualitySuite.Capability,
        QualitySuiteId = CandidateGeneralChatQualitySuite.Id,
        QualitySuiteVersion = CandidateGeneralChatQualitySuite.Version,
        QualityEvidenceProfileId = CandidateGeneralChatQualitySuite.ProfileId,
        QualityEvidenceProfileVersion = CandidateGeneralChatQualitySuite.ProfileVersion,
        QualityEvidenceProfileSha256 = CandidateGeneralChatQualitySuite.ProfileSha256,
        PerformanceWorkloadId = CandidateTextPerformanceWorkload.Id,
        PerformanceWorkloadVersion = CandidateTextPerformanceWorkload.Version,
        HardwareFingerprint = "gpu-1", RuntimeFingerprint = "text-cuda:runtime-v1", BinaryFingerprint = "binary-sha",
        ConfigurationFingerprint = CandidateEvidenceServiceTests.ActualLaunchConfiguration,
        MinimumQualityPassRateBasisPoints = 9000, MinimumQualityReliabilityBasisPoints = 9500,
        QualityReliabilityEstimatorId = CandidateQualityReliabilityEstimator.FreshProcessSuitePassRateV1,
        MinimumQualityRunCount = 5,
        PerformanceMetricId = CandidatePerformanceMetric.CompletionMillisecondsId,
        PerformanceMetricUnit = CandidatePerformanceMetric.MillisecondsUnit,
        HigherPerformanceMetricIsBetter = false, MinimumUtilityMarginBasisPoints = null,
        MinimumCompletionLatencyMarginBasisPoints = 500,
        TimeToFirstTokenMetricId = CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId,
        TimeToFirstTokenMetricUnit = CandidatePerformanceMetric.MillisecondsUnit,
        MinimumTimeToFirstTokenMarginBasisPoints = 500
    };

    /// <summary>Production proof wiring (policy + live incumbent + ledger) with a fixed policy and the swap runtime.</summary>
    private sealed class LedgerProofSource(CandidateEvidenceService evidence, CandidateUtilityPolicyDefinition policy,
        IActiveTextModelIdentitySource active, ICandidateLaunchFenceSource fence) : IRecommendationProofSource
    {
        public Task<CandidateUtilityProofResult> CreateProofAsync(string candidateKey, string managedFileName,
            CancellationToken cancellationToken) => evidence.CreateActiveReferenceProofAsync(policy, candidateKey,
            managedFileName, GeneralChatEnvironment(), CandidateEvidenceServiceTests.PerformanceEnvironment(), active,
            fence, cancellationToken);

        private static CandidateEvaluationEnvironment GeneralChatEnvironment()
        {
            CandidateEvaluationEnvironment environment = CandidateEvidenceServiceTests.Environment();
            environment.WorkloadFingerprint = CandidateGeneralChatQualitySuite.WorkloadFingerprint;
            return environment;
        }

        public string? CheckLicenseEvidence(string candidateKey, string managedFileName, CandidateProofSide? evaluatedSide) =>
            evidence.CheckActivationLicenseEvidence(candidateKey, managedFileName, evaluatedSide);

        public Task<IDisposable?> CaptureLicenseEvidenceReadLeaseAsync(string candidateKey, string managedFileName,
            CandidateProofSide? evaluatedSide, CancellationToken cancellationToken) =>
            evidence.CaptureActivationLicenseEvidenceReadLeaseAsync(candidateKey, managedFileName, evaluatedSide,
                cancellationToken).ContinueWith(t => (IDisposable?)t.Result, cancellationToken);

        public bool IsSnapshotCurrent(CandidateUtilityComparisonProof proof) => evidence.IsProofSnapshotCurrent(proof, policy);
        public bool? IsSideCurrent(CandidateProofSide side) => evidence.IsProofSideCurrent(side);
        public bool IsPolicyCurrent(CandidateUtilityComparisonProof proof) =>
            CandidateUtilityProofDigest.Policy(policy) == proof.PolicySha256;
    }

    /// <summary>Deterministic stand-in for huggingface.co: JSON metadata plus pinned file bytes, no network.</summary>
    private sealed class FakeHub : HttpMessageHandler
    {
        private sealed record Repo(string Id, string Revision, string File, byte[] Payload, string License, string[] Extra);
        private readonly List<Repo> _repos = new();
        internal int ModelDownloads;

        internal void Publish(string id, char revisionSeed, string file, string license, params string[] extraSiblings) =>
            _repos.Insert(0, new Repo(id, new string(revisionSeed, 40), file,
                Encoding.UTF8.GetBytes("weights of " + id), license, extraSiblings));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/models")
                return Task.FromResult(Json(JsonSerializer.Serialize(_repos.Select(r => new { id = r.Id }))));
            if (path.StartsWith("/api/models/", StringComparison.Ordinal))
            {
                Repo? repo = _repos.SingleOrDefault(r => "/api/models/" + r.Id == path);
                if (repo is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                string sha = Convert.ToHexString(SHA256.HashData(repo.Payload)).ToLowerInvariant();
                var siblings = new List<object> { new { rfilename = repo.File, size = repo.Payload.Length,
                    lfs = new { oid = sha, size = repo.Payload.Length } } };
                siblings.AddRange(repo.Extra.Select(name => (object)new { rfilename = name }));
                return Task.FromResult(Json(JsonSerializer.Serialize(new
                {
                    id = repo.Id, sha = repo.Revision, pipeline_tag = "text-generation",
                    tags = new[] { "text-generation", "gguf" }, cardData = new { license = repo.License }, siblings
                })));
            }
            foreach (Repo repo in _repos)
            {
                if (path != $"/{repo.Id}/resolve/{repo.Revision}/{repo.File}") continue;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = request.Method == HttpMethod.Head ? new ByteArrayContent(Array.Empty<byte>())
                        : new ByteArrayContent(repo.Payload)
                };
                if (request.Method == HttpMethod.Head)
                {
                    response.Headers.TryAddWithoutValidation("X-Linked-ETag",
                        "\"" + Convert.ToHexString(SHA256.HashData(repo.Payload)).ToLowerInvariant() + "\"");
                    response.Headers.TryAddWithoutValidation("X-Linked-Size", repo.Payload.Length.ToString());
                }
                else Interlocked.Increment(ref ModelDownloads);
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    public void Dispose()
    {
        ProcessSwapRuntime.KillEverythingStarted();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
