using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Order of effects for promote/rollback against a fake runtime: the previous runtime is replaced (stopped) by the
/// exact load, the new one is health-checked while the transition is still Pending, and only then is it committed.</summary>
public sealed class CandidateRecommendationOrderingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-rec-order", Guid.NewGuid().ToString("N"));
    private readonly CandidateUtilityComparisonProof _proof =
        RecFx.Proof("org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
    private readonly FakeRecRuntime _inner = new();
    private readonly CandidateRecommendationStore _store;
    private readonly RecordingRuntime _runtime;
    private readonly FakeRecProofs _proofs;
    private readonly FakeRecFence _fence;

    public CandidateRecommendationOrderingTests()
    {
        Directory.CreateDirectory(_root);
        _store = new CandidateRecommendationStore(Path.Combine(_root, "state"));
        _runtime = new RecordingRuntime(_inner, _store);
        _proofs = new FakeRecProofs(_proof);
        _fence = new FakeRecFence(_inner);
        _inner.Serve(_proof.Incumbent);
    }

    private CandidateRecommendationService Service() => new(_store, _runtime, _proofs, _fence, RecFx.Models, null,
        TimeSpan.FromMilliseconds(300), _root, healthRecoveryBudgetFor: _ => TimeSpan.FromMilliseconds(300));

    [Fact]
    public async Task Promote_replaces_the_old_runtime_then_health_checks_the_new_one_while_Pending_and_only_then_commits()
    {
        Assert.True(_inner.Running);
        RecommendationTransitionResult result = await Service().PromoteAsync(
            _proof.Challenger.Identity.CandidateKey, "challenger.gguf", 0, true, CancellationToken.None);

        Assert.True(result.IsCommitted, result.Reason);
        string[] events = _runtime.Events.ToArray();
        int load = Array.FindIndex(events, e => e == "load:challenger.gguf:pending");
        int health = Array.FindIndex(events, e => e == "health:pending");
        Assert.True(load >= 0, string.Join(",", events));
        Assert.True(health > load, string.Join(",", events));
        Assert.True(_runtime.OldRuntimeWasResidentWhenLoadStarted);
        Assert.Equal(RecFx.PathOf(_proof.Challenger), _inner.Loaded!.Value.Path);
        Assert.NotNull(_store.Load().GetActiveRecord());
        Assert.Null(_store.Load().Pending);
    }

    [Fact]
    public async Task Rollback_to_an_unhealthy_target_stops_it_and_restores_the_current_winner_without_committing()
    {
        Assert.True((await Service().PromoteAsync(_proof.Challenger.Identity.CandidateKey, "challenger.gguf", 0, true,
            CancellationToken.None)).IsCommitted);
        CandidateUtilityComparisonProof second = RecFx.Proof("org/second", "second.gguf", 'c', "org/challenger", "challenger.gguf", 'a');
        _proofs.Proof = second;
        Assert.True((await Service().PromoteAsync(second.Challenger.Identity.CandidateKey, "second.gguf",
            _store.Load().Generation, true, CancellationToken.None)).IsCommitted);
        long generation = _store.Load().Generation;
        string activeProof = _store.Load().GetActiveRecord()!.Proof.ProofSha256;

        // The rollback target (challenger.gguf) comes up unhealthy; anything else is healthy.
        _inner.OnLoad = path =>
        {
            _inner.Health = path.EndsWith("challenger.gguf", StringComparison.OrdinalIgnoreCase)
                ? FakeHealth.Unhealthy : FakeHealth.Healthy;
            return true;
        };
        _runtime.Events.Clear();
        RecommendationTransitionResult failed = await Service().RollbackAsync(generation, true, CancellationToken.None);

        Assert.Equal(RecommendationTransitionResult.FailedRestored, failed.Outcome);
        Assert.Equal("challenger_unhealthy", failed.Reason);
        Assert.Equal(activeProof, _store.Load().GetActiveRecord()!.Proof.ProofSha256);
        Assert.Null(_store.Load().Pending);
        Assert.Equal(RecFx.PathOf(second.Challenger), _inner.Loaded!.Value.Path);
        Assert.True(_inner.Running);
        Assert.Contains("load:challenger.gguf:pending", _runtime.Events);
        Assert.Contains("load:second.gguf:aborted", _runtime.Events);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class RecordingRuntime(FakeRecRuntime inner, CandidateRecommendationStore store) : IRecommendationRuntime
    {
        internal readonly List<string> Events = new();
        internal bool OldRuntimeWasResidentWhenLoadStarted;

        public bool GamingActive => inner.GamingActive;
        public bool StopRequested => inner.StopRequested;
        public bool TextRuntimeRunning => inner.TextRuntimeRunning;
        public Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken) =>
            inner.AcquireWriterAsync(cancellationToken);

        public async Task<RuntimeLoadResult> LoadExactAsync(IAsyncDisposable writer, string role, string candidateKey,
            string path, string sha256, CancellationToken cancellationToken)
        {
            if (Events.Count == 0) OldRuntimeWasResidentWhenLoadStarted = inner.Running;
            RuntimeLoadResult result = await inner.LoadExactAsync(writer, role, candidateKey, path, sha256, cancellationToken);
            Events.Add("load:" + Path.GetFileName(path) + ":" + (store.Load().Pending is null ? "aborted" : "pending"));
            return result;
        }

        public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
        {
            bool healthy = await inner.IsHealthyAsync(cancellationToken);
            Events.Add("health:" + (store.Load().Pending is null ? "settled" : "pending"));
            return healthy;
        }

        public bool StopText()
        {
            Events.Add("stop");
            return inner.StopText();
        }
    }
}
