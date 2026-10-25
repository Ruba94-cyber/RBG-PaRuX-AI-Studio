using System.Collections.Immutable;
using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateReconcileBoundTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-reconcile-bound-" + Guid.NewGuid().ToString("N"));
    private readonly CandidateUtilityComparisonProof _proof =
        RecFx.Proof("org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
    private readonly FakeRecRuntime _runtime = new();
    private readonly FakeRecProofs _proofs;
    private readonly CandidateRecommendationStore _store;
    private readonly FakeRecFence _fence;

    public CandidateReconcileBoundTests()
    {
        Directory.CreateDirectory(_root);
        _proofs = new FakeRecProofs(_proof);
        _store = new CandidateRecommendationStore(Path.Combine(_root, "state"));
        _fence = new FakeRecFence(_runtime);
        _runtime.Serve(_proof.Incumbent);
    }

    [Fact]
    public async Task Reconcile_returns_busy_without_writing_when_the_transition_gate_is_held()
    {
        var runtime = new FirstWriterBlockingRuntime(_runtime);
        CandidateRecommendationService service = CreateService(runtime, TimeSpan.FromMilliseconds(120));
        Task<RecommendationTransitionResult> promotion = service.PromoteAsync(
            _proof.Challenger.Identity.CandidateKey, _proof.Challenger.Identity.ManagedFileName, 0, true,
            CancellationToken.None);

        try
        {
            await runtime.WriterStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            CandidateRecommendationSnapshot before = _store.Load();
            Stopwatch elapsed = Stopwatch.StartNew();
            RecommendationTransitionResult result = await service.ReconcileAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(RecommendationTransitionResult.Refused, result.Outcome);
            Assert.Equal("reconcile_busy", result.Reason);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(before.Generation, _store.Load().Generation);
            Assert.Null(_store.Load().Pending);
            Assert.Null(_store.Load().Active);
            Assert.Empty(_runtime.Loads);
            Assert.Equal(0, _runtime.Stops);

            runtime.ReleaseFirstWriter();
            RecommendationTransitionResult promoted = await promotion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(promoted.IsCommitted, promoted.Reason);
        }
        finally
        {
            runtime.ReleaseFirstWriter();
            try { await promotion.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { }
        }
    }

    [Fact]
    public async Task Reconcile_times_out_waiting_for_writer_and_keeps_pending_untouched()
    {
        CandidateRecommendationSnapshot before = SeedPendingWithChallengerLive();
        var runtime = new CancellationWaitingWriterRuntime(_runtime);
        CandidateRecommendationService service = CreateService(runtime, TimeSpan.FromMilliseconds(120));
        Stopwatch elapsed = Stopwatch.StartNew();

        RecommendationTransitionResult result = await service.ReconcileAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(await runtime.WriterCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(RecommendationTransitionResult.Refused, result.Outcome);
        Assert.Equal("reconcile_writer_busy", result.Reason);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
        AssertPendingUnchanged(before);
        Assert.Empty(_runtime.Loads);
        Assert.Equal(0, _runtime.Stops);
        Assert.True(_runtime.Running);
    }

    [Fact]
    public async Task Reconcile_propagates_caller_cancellation_during_writer_wait_and_keeps_pending()
    {
        CandidateRecommendationSnapshot before = SeedPendingWithChallengerLive();
        var runtime = new CancellationWaitingWriterRuntime(_runtime);
        CandidateRecommendationService service = CreateService(runtime, TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        Task<RecommendationTransitionResult> reconcile = service.ReconcileAsync(cancellation.Token);

        await runtime.WriterStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await reconcile.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(await runtime.WriterCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        AssertPendingUnchanged(before);
        Assert.Empty(_runtime.Loads);
        Assert.Equal(0, _runtime.Stops);
    }

    [Fact]
    public async Task Reconcile_does_not_restore_when_hard_off_starts_during_writer_wait()
    {
        SeedPendingWithChallengerLive();
        var runtime = new HardOffDuringWriterWaitRuntime(_runtime);
        CandidateRecommendationService service = CreateService(runtime, TimeSpan.FromSeconds(1));
        Task<RecommendationTransitionResult> reconcile = service.ReconcileAsync(CancellationToken.None);

        await runtime.WriterStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        _runtime.Gaming = true;
        runtime.ReleaseWriter();

        RecommendationTransitionResult result = await reconcile.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(RecommendationTransitionResult.FailedRuntimeStopped, result.Outcome);
        Assert.Equal("pending_aborted_runtime_stopped", result.Reason);
        Assert.Empty(_runtime.Loads);
        Assert.Null(_store.Load().Pending);
        Assert.False(_runtime.Running);
    }

    [Fact]
    public async Task Reconcile_stops_the_unverified_runtime_when_the_writer_is_unavailable_and_keeps_pending()
    {
        CandidateRecommendationSnapshot before = SeedPendingWithChallengerLive();
        var runtime = new UnavailableWriterRuntime(_runtime);
        CandidateRecommendationService service = CreateService(runtime, TimeSpan.FromSeconds(1));

        RecommendationTransitionResult result = await service.ReconcileAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RecommendationTransitionResult.Refused, result.Outcome);
        Assert.Equal("reconcile_writer_unavailable", result.Reason);
        AssertPendingUnchanged(before);
        Assert.Empty(_runtime.Loads);
        Assert.Equal(1, _runtime.Stops);
        Assert.False(_runtime.Running);
    }

    private CandidateRecommendationService CreateService(IRecommendationRuntime runtime, TimeSpan waitBound) =>
        new(_store, runtime, _proofs, _fence, RecFx.Models,
            healthBudget: TimeSpan.FromMilliseconds(300), pinRoot: _root, reconcileWaitBound: waitBound);

    private CandidateRecommendationSnapshot SeedPendingWithChallengerLive()
    {
        Assert.True(_store.TryBeginPending(0, Guid.NewGuid(), _proof, Admission(_proof.Incumbent), Checks(_proof.Incumbent)));
        _runtime.Serve(_proof.Challenger);
        return _store.Load();
    }

    private static CandidateLaunchAdmission Admission(CandidateProofSide side) => new(RecFx.ActualConfig, "text-cuda",
        RecFx.Host.HardwareFingerprint, RecFx.Host.RuntimeFingerprint, RecFx.Host.BinaryFingerprint,
        RecFx.PathOf(side), DateTime.UtcNow);

    private static RecommendationTransitionChecks Checks(CandidateProofSide side) => new(
        () => new TextLaunchDescriptor(1, "text-cuda", ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty, RecFx.ActualConfig, RecFx.PathOf(side)),
        (_, _) => true, _ => true, () => true);

    private void AssertPendingUnchanged(CandidateRecommendationSnapshot before)
    {
        CandidateRecommendationSnapshot after = _store.Load();
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Pending?.TransactionId, after.Pending?.TransactionId);
        Assert.Equal(before.Active?.Proof.ProofSha256, after.Active?.Proof.ProofSha256);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private class RuntimeAdapter(FakeRecRuntime inner) : IRecommendationRuntime
    {
        protected FakeRecRuntime Inner { get; } = inner;
        public bool GamingActive => Inner.GamingActive;
        public bool StopRequested => Inner.StopRequested;
        public bool TextRuntimeRunning => Inner.TextRuntimeRunning;
        public virtual Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken) =>
            Inner.AcquireWriterAsync(cancellationToken);
        public Task<RuntimeLoadResult> LoadExactAsync(IAsyncDisposable writer, string role, string candidateKey, string path,
            string sha256, CancellationToken cancellationToken) =>
            Inner.LoadExactAsync(writer, role, candidateKey, path, sha256, cancellationToken);
        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Inner.IsHealthyAsync(cancellationToken);
        public bool StopText() => Inner.StopText();
    }

    private sealed class UnavailableWriterRuntime(FakeRecRuntime inner) : RuntimeAdapter(inner)
    {
        public override Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken) =>
            Task.FromException<IAsyncDisposable>(new InvalidOperationException("The text-transition writer is unavailable."));
    }

    private sealed class FirstWriterBlockingRuntime(FakeRecRuntime inner) : RuntimeAdapter(inner)
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        internal TaskCompletionSource<bool> WriterStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                WriterStarted.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return await base.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        }

        internal void ReleaseFirstWriter() => _release.TrySetResult(true);
    }

    private sealed class CancellationWaitingWriterRuntime(FakeRecRuntime inner) : RuntimeAdapter(inner)
    {
        internal TaskCompletionSource<bool> WriterStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> WriterCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken)
        {
            WriterStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WriterCancelled.TrySetResult(true);
                throw;
            }
            throw new InvalidOperationException("The writer wait should finish only through cancellation.");
        }
    }

    private sealed class HardOffDuringWriterWaitRuntime(FakeRecRuntime inner) : RuntimeAdapter(inner)
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> WriterStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken)
        {
            WriterStarted.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        }

        internal void ReleaseWriter() => _release.TrySetResult(true);
    }
}
