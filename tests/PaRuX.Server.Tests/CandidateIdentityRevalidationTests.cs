using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateIdentityRevalidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-identity-revalidation", Guid.NewGuid().ToString("N"));
    private readonly string _state;
    private readonly string _models;
    private readonly IDisposable _localRootScope;
    private readonly IDisposable _stateScope;

    public CandidateIdentityRevalidationTests()
    {
        Directory.CreateDirectory(_root);
        _state = Path.Combine(_root, "state");
        _models = Path.Combine(_root, "models");
        _localRootScope = LocalAiPaths.UseRootForTests(Path.Combine(_root, "localai"));
        _stateScope = LocalAiPaths.UseStateDirForTests(Path.Combine(_root, "state-root"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Identity_change_bypasses_active_and_creates_one_passive_request(bool hardwareChanged)
    {
        CandidateUtilityComparisonProof proof = Proof();
        SeedActive(proof);
        string activePath = Path.Combine(_state, CandidateRecommendationStore.FileName);
        byte[] activeBefore = File.ReadAllBytes(activePath);
        CandidateRecommendationEvaluationIdentity previous = CandidateRecommendationEvaluationIdentity.From(proof.QualityEnvironment);
        CandidateRecommendationEvaluationIdentity current = hardwareChanged
            ? previous with { HardwareFingerprint = Fingerprint(EvaluationHardwareFingerprint.CurrentPrefix, 'c') }
            : previous with { RuntimeFingerprint = Fingerprint(RuntimeDependencyFingerprint.Prefix, 'd') };
        var consult = Consult(() => current);

        (RecommendedRoute? route, string? reason) = await consult.ConsultAsync();

        Assert.Null(route);
        Assert.Equal("active_identity_changed", reason);
        Assert.Equal(activeBefore, File.ReadAllBytes(activePath));
        string[] requestPaths = Directory.GetFiles(Path.Combine(_state, CandidateIdentityRevalidationRequestStore.DirectoryName), "*.json");
        string requestPath = Assert.Single(requestPaths);
        byte[] requestBefore = File.ReadAllBytes(requestPath);
        string requestId = Path.GetFileNameWithoutExtension(requestPath)[CandidateIdentityRevalidationRequestStore.RequestFilePrefix.Length..];
        CandidateIdentityRevalidationRequest request = Assert.IsType<CandidateIdentityRevalidationRequest>(
            new CandidateIdentityRevalidationRequestStore(_state).Load(requestId));
        Assert.Equal("AwaitingPolicy", request.State);
        Assert.Equal("active_identity_changed", request.Reason);
        Assert.Null(request.EvaluationJobId);

        (route, reason) = await consult.ConsultAsync();

        Assert.Null(route);
        Assert.Equal("active_identity_changed", reason);
        Assert.Equal(1, Directory.GetFiles(Path.Combine(_state, CandidateIdentityRevalidationRequestStore.DirectoryName), "*.json").Length);
        Assert.Equal(requestBefore, File.ReadAllBytes(requestPath));
        Assert.Equal(activeBefore, File.ReadAllBytes(activePath));
    }

    [Fact]
    public async Task Active_winner_is_automatically_used_again_when_identity_matches()
    {
        CandidateUtilityComparisonProof proof = Proof();
        SeedActive(proof);
        CandidateRecommendationEvaluationIdentity previous = CandidateRecommendationEvaluationIdentity.From(proof.QualityEnvironment);
        CandidateRecommendationEvaluationIdentity current = previous with
        { HardwareFingerprint = Fingerprint(EvaluationHardwareFingerprint.CurrentPrefix, 'e') };
        var consult = Consult(() => current);

        (RecommendedRoute? staleRoute, string? staleReason) = await consult.ConsultAsync();
        Assert.Null(staleRoute);
        Assert.Equal("active_identity_changed", staleReason);

        current = previous;
        (RecommendedRoute? restoredRoute, string? restoredReason) = await consult.ConsultAsync();

        Assert.Null(restoredReason);
        Assert.Equal(proof.Challenger.Identity.CandidateKey, restoredRoute!.CandidateKey);
        Assert.Equal(proof.Challenger.Identity.Revision, restoredRoute.Revision);
        Assert.Equal(proof.Challenger.Identity.ManagedFileName, restoredRoute.ManagedFileName);
    }

    [Fact]
    public async Task Router_uses_ordinary_ranker_and_does_not_load_stale_active_winner()
    {
        CandidateUtilityComparisonProof proof = Proof();
        SeedActive(proof);
        CandidateRecommendationEvaluationIdentity previous = CandidateRecommendationEvaluationIdentity.From(proof.QualityEnvironment);
        CandidateRecommendationEvaluationIdentity current = previous with
        { RuntimeFingerprint = Fingerprint(RuntimeDependencyFingerprint.Prefix, 'f') };
        var process = new LocalAiProcessManager();
        using IDisposable vram = process.OverrideVramMibForTests(0);
        using IDisposable models = ModelDownloadService.UseModelsDirectoryForTests(_models);
        var router = new SmartRouterService(
            new RuntimeManagerService(process),
            new ModelBenchmarkService(new ModelBenchmarkStore(_state), new NeverRunBenchmark()),
            new PaRuX.Server.CapabilityProfiler.CapabilityProfilerStore(_state),
            new CandidateEvidenceService(new ModelDiscoveryCatalogStore(_state), new CandidateEvidenceStore(_state), _models),
            Consult(() => current));

        RouteDecisionDto decision = await router.ResolveAsync(new RouteRequest
        {
            Mode = RouteMode.Auto,
            AutoProfile = AutoIntelligenceProfile.Balanced,
            PromptText = "test identity revalidation"
        }, CancellationToken.None);

        Assert.False(decision.Success);
        Assert.Equal(0, decision.CandidatesConsidered);
        Assert.Null(decision.ModelPath);
        Assert.StartsWith("No candidate pool exists", decision.SelectionReason);
        Assert.Contains("recommendation not used: active_identity_changed", decision.SelectionReason);
        Assert.Null(process.GetTextProcessId());
    }

    private RecommendationRouteConsult Consult(Func<CandidateRecommendationEvaluationIdentity> current) =>
        new(_state, _ => true, () => false, _root,
            (_, _) => Task.FromResult<CandidateRecommendationEvaluationIdentity?>(current()));

    private void SeedActive(CandidateUtilityComparisonProof proof)
    {
        var store = new CandidateRecommendationStore(_state);
        CandidateLaunchAdmission Admission(CandidateProofSide side) => new(RecFx.ActualConfig, "text-cuda",
            proof.QualityEnvironment.HardwareFingerprint, proof.QualityEnvironment.RuntimeFingerprint,
            proof.QualityEnvironment.BinaryFingerprint, RecFx.PathOf(side), DateTime.UtcNow);
        RecommendationTransitionChecks Checks(CandidateProofSide side) => new(
            () => new TextLaunchDescriptor(1, "text-cuda", System.Collections.Immutable.ImmutableArray<string>.Empty,
                System.Collections.Immutable.ImmutableArray<string>.Empty, RecFx.ActualConfig, RecFx.PathOf(side)),
            (_, _) => true, _ => true, () => true);
        Guid transaction = Guid.NewGuid();
        Assert.True(store.TryBeginPending(0, transaction, proof, Admission(proof.Incumbent), Checks(proof.Incumbent)));
        Assert.True(store.TryCommitPending(1, transaction, Admission(proof.Challenger), Checks(proof.Challenger)));
    }

    private static CandidateUtilityComparisonProof Proof()
    {
        CandidateUtilityComparisonProof proof = RecFx.Proof(
            "org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
        string hardware = Fingerprint(EvaluationHardwareFingerprint.CurrentPrefix, 'a');
        string runtime = Fingerprint(RuntimeDependencyFingerprint.Prefix, 'b');
        CandidateProofEnvironment quality = proof.QualityEnvironment with
        { HardwareFingerprint = hardware, BinaryFingerprint = runtime };
        CandidateProofEnvironment performance = proof.PerformanceEnvironment with
        { HardwareFingerprint = hardware, BinaryFingerprint = runtime };
        proof = proof with
        { QualityEnvironment = quality, PerformanceEnvironment = performance, ProofSha256 = string.Empty };
        return proof with { ProofSha256 = CandidateUtilityProofDigest.Proof(proof) };
    }

    private static string Fingerprint(string prefix, char value) => prefix + new string(value, 64);

    private sealed class NeverRunBenchmark : IModelBenchmarkRunner
    {
        public Task<ModelBenchmarkMeasurement> RunAsync(string modelPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The empty ordinary candidate pool must not run a benchmark.");
    }

    public void Dispose()
    {
        _stateScope.Dispose();
        _localRootScope.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
