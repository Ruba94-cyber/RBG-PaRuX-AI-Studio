using PaRuX.Contracts;
using PaRuX.Server;
using System.Diagnostics;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Activation paths proven with fixture candidates; production AUTO stays closed (no evidence qualifies).</summary>
public sealed class CandidateRecommendationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-rec-service", Guid.NewGuid().ToString("N"));
    private readonly CandidateUtilityComparisonProof _proof =
        RecFx.Proof("org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
    private readonly FakeRecRuntime _runtime = new();
    private readonly FakeRecProofs _proofs;
    private readonly CandidateRecommendationStore _store;
    private readonly FakeRecFence _fence;

    public CandidateRecommendationServiceTests()
    {
        Directory.CreateDirectory(_root);
        _proofs = new FakeRecProofs(_proof);
        _store = new CandidateRecommendationStore(Path.Combine(_root, "state"));
        _fence = new FakeRecFence(_runtime);
        _runtime.Serve(_proof.Incumbent);
    }

    private CandidateRecommendationService Service(Action<string>? crash = null, TimeSpan? health = null,
        ICandidateRecommendationStore? store = null, IRecommendationRuntime? runtime = null,
        Func<long, TimeSpan>? healthRecoveryBudgetFor = null) =>
        new(store ?? _store, runtime ?? _runtime, _proofs, _fence, RecFx.Models, crash,
            health ?? TimeSpan.FromMilliseconds(300), _root, healthRecoveryBudgetFor: healthRecoveryBudgetFor);

    private Task<RecommendationTransitionResult> Promote(CandidateRecommendationService service, long generation = 0,
        bool consent = true, CancellationToken ct = default) =>
        service.PromoteAsync(_proof.Challenger.Identity.CandidateKey, "challenger.gguf", generation, consent, ct);

    [Fact]
    public async Task Promote_commits_Active_with_the_challenger_live_and_writes_no_pin_or_role_assignment()
    {
        string? roleBefore = LocalAiPaths.GetRoleOverride("standard");
        RecommendationTransitionResult result = await Promote(Service());
        Assert.True(result.IsCommitted, result.Reason);
        Assert.Equal(2, result.Generation);
        CandidateRecommendationSnapshot snapshot = _store.Load();
        Assert.Equal(_proof.ProofSha256, snapshot.GetActiveRecord()!.Proof.ProofSha256);
        Assert.Equal(_proof.Incumbent.Identity, snapshot.Fallback!.Identity);
        Assert.True(_fence.IsLiveExactModel(RecFx.PathOf(_proof.Challenger), _proof.Challenger.Identity.LocalSha256));
        Assert.Equal(1, _runtime.MaxWritersHeld);
        Assert.Equal(0, _runtime.WritersHeld);
        Assert.Equal(roleBefore, LocalAiPaths.GetRoleOverride("standard"));
        Assert.False(ManualSelectionGate.HasUserPin("standard", "balanced", _root) && roleBefore is null);
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("gaming")]
    [InlineData("stop")]
    [InlineData("generation")]
    [InlineData("pending")]
    [InlineData("noproof")]
    [InlineData("pin")]
    public async Task Promote_refuses_without_touching_runtime_or_store(string scenario)
    {
        long generation = 0;
        bool consent = true;
        switch (scenario)
        {
            case "consent": consent = false; break;
            case "gaming": _runtime.Gaming = true; break;
            case "stop": _runtime.Stop = true; break;
            case "generation": generation = 5; break;
            case "pending":
                Assert.True(_store.TryBeginPending(0, Guid.NewGuid(), _proof, Admission(_proof.Incumbent), Checks(_proof.Incumbent)));
                generation = 1; break;
            case "noproof": _proofs.Proof = null; break;
            case "pin":
                CapabilityRoutingPreferenceStore.Set("standard", "balanced", new SetScopedRoutingOverrideRequest
                { Mode = "force", CandidateKey = new string('a', 64), Revision = new string('b', 40), ManagedFileName = "x.gguf" }, _root);
                break;
        }
        long storeGenerationBefore = _store.Load().Generation;
        RecommendationTransitionResult result = await Promote(Service(), generation, consent);
        Assert.Equal(RecommendationTransitionResult.Refused, result.Outcome);
        Assert.Empty(_runtime.Loads);
        Assert.Equal(0, _runtime.Stops);
        Assert.Equal(storeGenerationBefore, _store.Load().Generation);
        Assert.Null(_store.Load().Active);
        if (scenario == "pin") Assert.Equal("manual_pin_scoped", result.Reason);
        if (scenario == "gaming") Assert.Equal("gaming_active", result.Reason);
        if (scenario == "stop") Assert.Equal("server_stopping", result.Reason);
    }

    [Fact]
    public async Task Failed_challenger_load_aborts_Pending_and_restores_the_exact_incumbent()
    {
        _runtime.OnLoad = path => !path.EndsWith("challenger.gguf", StringComparison.OrdinalIgnoreCase);
        RecommendationTransitionResult result = await Promote(Service());
        Assert.Equal(RecommendationTransitionResult.FailedRestored, result.Outcome);
        Assert.Equal("challenger_load_failed", result.Reason);
        Assert.Null(_store.Load().Pending);
        Assert.Null(_store.Load().Active);
        Assert.True(_fence.IsLiveExactModel(RecFx.PathOf(_proof.Incumbent), _proof.Incumbent.Identity.LocalSha256));
    }

    [Fact]
    public async Task Gaming_started_during_the_transition_never_reloads_and_leaves_a_stopped_runtime()
    {
        _runtime.OnLoad = _ => { _runtime.Gaming = true; return false; };
        RecommendationTransitionResult result = await Promote(Service());
        Assert.Equal(RecommendationTransitionResult.FailedRuntimeStopped, result.Outcome);
        Assert.Single(_runtime.Loads); // the incumbent restore was NOT attempted
        Assert.False(_runtime.Running);
        Assert.Null(_store.Load().Pending);
        Assert.Null(_store.Load().Active);
    }

    [Theory]
    [InlineData(FakeHealth.Unhealthy)]
    [InlineData(FakeHealth.Hang)]
    public async Task Unhealthy_or_hung_challenger_fails_activation_phase_B_and_recovers(FakeHealth health)
    {
        // Healthy only for the incumbent restore: the challenger load flips health the moment it starts.
        _runtime.OnLoad = path => { if (path.EndsWith("challenger.gguf", StringComparison.OrdinalIgnoreCase)) _runtime.Health = health; else _runtime.Health = FakeHealth.Healthy; return true; };
        var timer = System.Diagnostics.Stopwatch.StartNew();
        RecommendationTransitionResult result = await Promote(Service(
            healthRecoveryBudgetFor: _ => TimeSpan.FromMilliseconds(300)));
        Assert.Equal("challenger_unhealthy", result.Reason);
        Assert.Equal(RecommendationTransitionResult.FailedRestored, result.Outcome);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "the hung runtime must not block activation");
        Assert.Null(_store.Load().Active);
        Assert.True(_fence.IsLiveExactModel(RecFx.PathOf(_proof.Incumbent), _proof.Incumbent.Identity.LocalSha256));
    }

    [Fact]
    public async Task Slow_challenger_health_within_size_budget_is_accepted()
    {
        const long largeModelSizeBytes = 256L * 1024 * 1024;
        CandidateProofSide challenger = _proof.Challenger with
        {
            Identity = _proof.Challenger.Identity with { SizeBytes = largeModelSizeBytes }
        };
        CandidateUtilityComparisonProof sizedProof = _proof with { Challenger = challenger };
        _proofs.Proof = sizedProof with { ProofSha256 = CandidateUtilityProofDigest.Proof(sizedProof) };

        TimeSpan configuredBudget = TimeSpan.FromMilliseconds(50);
        TimeSpan probeDelay = TimeSpan.FromMilliseconds(150);
        long? observedSize = null;
        Assert.True(CandidateRecommendationService.RecoveryBudgetFor(largeModelSizeBytes)
            > CandidateRecommendationService.RecoveryBudgetFor(_proof.Challenger.Identity.SizeBytes));
        Assert.True(probeDelay > configuredBudget);
        Assert.True(probeDelay < CandidateRecommendationService.RecoveryBudgetFor(largeModelSizeBytes));

        var runtime = new DelayedHealthRuntime(_runtime, probeDelay);
        RecommendationTransitionResult result = await Promote(Service(health: configuredBudget, runtime: runtime,
            healthRecoveryBudgetFor: size =>
            {
                observedSize = size;
                return size == largeModelSizeBytes ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromMilliseconds(25);
            }));

        Assert.True(result.IsCommitted, result.Reason);
        Assert.Equal(largeModelSizeBytes, observedSize);
    }

    [Fact]
    public async Task A_pin_created_after_Pending_blocks_the_commit_and_is_left_intact()
    {
        _runtime.OnLoad = _ =>
        {
            CapabilityRoutingPreferenceStore.Set("standard", "balanced", new SetScopedRoutingOverrideRequest
            { Mode = "force", CandidateKey = new string('a', 64), Revision = new string('b', 40), ManagedFileName = "x.gguf" }, _root);
            return true;
        };
        RecommendationTransitionResult result = await Promote(Service());
        Assert.Equal("commit_rejected", result.Reason);
        Assert.Null(_store.Load().Active);
        Assert.Null(_store.Load().Pending);
        Assert.NotNull(CapabilityRoutingPreferenceStore.Get("standard", "balanced", _root)); // the user's pin survives
        Assert.True(_fence.IsLiveExactModel(RecFx.PathOf(_proof.Incumbent), _proof.Incumbent.Identity.LocalSha256));
    }

    [Fact]
    public async Task Stale_snapshot_at_commit_refuses_activation()
    {
        _runtime.OnLoad = _ => { _proofs.SnapshotCurrent = false; return true; };
        RecommendationTransitionResult result = await Promote(Service());
        Assert.Equal("commit_rejected", result.Reason);
        Assert.Null(_store.Load().Active);
    }

    [Fact]
    public async Task Cancellation_before_commit_aborts_and_restores_but_after_commit_returns_Committed()
    {
        using var early = new CancellationTokenSource();
        _runtime.OnLoad = path => { if (path.EndsWith("challenger.gguf", StringComparison.OrdinalIgnoreCase)) early.Cancel(); return true; };
        RecommendationTransitionResult cancelled = await Promote(Service(), ct: early.Token);
        Assert.Equal("cancelled", cancelled.Reason);
        Assert.Equal(RecommendationTransitionResult.FailedRestored, cancelled.Outcome);
        Assert.Null(_store.Load().Active);

        _runtime.OnLoad = null;
        using var late = new CancellationTokenSource();
        RecommendationTransitionResult committed = await Promote(
            Service(crash: point => { if (point == "after_commit") late.Cancel(); }),
            generation: _store.Load().Generation, ct: late.Token);
        Assert.True(committed.IsCommitted, committed.Reason);
        Assert.NotNull(_store.Load().GetActiveRecord());
    }

    [Fact]
    public async Task Concurrent_promotions_commit_exactly_one()
    {
        CandidateRecommendationService service = Service();
        RecommendationTransitionResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Task.Run(() => Promote(service))));
        Assert.Single(results, item => item.IsCommitted);
        Assert.Equal(1, _runtime.MaxWritersHeld);
        Assert.NotNull(_store.Load().GetActiveRecord());
    }

    [Fact]
    public async Task Rollback_restores_the_previous_winner_and_a_failed_rollback_keeps_the_current_one()
    {
        // first -> second: two committed promotions so that Previous exists.
        CandidateUtilityComparisonProof second = RecFx.Proof("org/second", "second.gguf", 'c', "org/challenger", "challenger.gguf", 'a');
        Assert.True((await Promote(Service())).IsCommitted);
        _proofs.Proof = second;
        RecommendationTransitionResult promoted = await Service().PromoteAsync(second.Challenger.Identity.CandidateKey,
            "second.gguf", _store.Load().Generation, true, CancellationToken.None);
        Assert.True(promoted.IsCommitted, promoted.Reason);
        Assert.Equal(second.ProofSha256, _store.Load().GetActiveRecord()!.Proof.ProofSha256);

        // failed rollback: target load fails -> restores the current winner, authority unchanged
        _runtime.OnLoad = path => !path.EndsWith("challenger.gguf", StringComparison.OrdinalIgnoreCase);
        RecommendationTransitionResult failed = await Service().RollbackAsync(_store.Load().Generation, true, CancellationToken.None);
        Assert.Equal(RecommendationTransitionResult.FailedRestored, failed.Outcome);
        Assert.Equal(second.ProofSha256, _store.Load().GetActiveRecord()!.Proof.ProofSha256);
        Assert.True(_fence.IsLiveExactModel(RecFx.PathOf(second.Challenger), second.Challenger.Identity.LocalSha256));

        _runtime.OnLoad = null;
        // no consent / stale policy / Gaming all refuse before anything changes
        Assert.Equal("consent_required", (await Service().RollbackAsync(_store.Load().Generation, false, CancellationToken.None)).Reason);
        _proofs.PolicyCurrent = false;
        Assert.Equal("rollback_rejected", (await Service().RollbackAsync(_store.Load().Generation, true, CancellationToken.None)).Reason);
        _proofs.PolicyCurrent = true;
        _runtime.Gaming = true;
        Assert.Equal("gaming_active", (await Service().RollbackAsync(_store.Load().Generation, true, CancellationToken.None)).Reason);
        _runtime.Gaming = false;
        Assert.Equal(second.ProofSha256, _store.Load().GetActiveRecord()!.Proof.ProofSha256);

        RecommendationTransitionResult back = await Service().RollbackAsync(_store.Load().Generation, true, CancellationToken.None);
        Assert.True(back.IsCommitted, back.Reason);
        Assert.Equal(_proof.ProofSha256, _store.Load().GetActiveRecord()!.Proof.ProofSha256);
        Assert.Null(_store.Load().Previous);
        Assert.True(_fence.IsLiveExactModel(RecFx.PathOf(_proof.Challenger), _proof.Challenger.Identity.LocalSha256));
    }

    [Fact]
    public async Task Rollback_without_a_previous_winner_is_refused()
    {
        Assert.True((await Promote(Service())).IsCommitted);
        Assert.Equal("no_previous_winner",
            (await Service().RollbackAsync(_store.Load().Generation, true, CancellationToken.None)).Reason);
    }

    // ---- restart reconciliation ----

    private void SeedPendingWithChallengerLive()
    {
        Assert.True(_store.TryBeginPending(0, Guid.NewGuid(), _proof, Admission(_proof.Incumbent), Checks(_proof.Incumbent)));
        _runtime.Serve(_proof.Challenger); // the half-activated challenger outlived Core
    }

    [Fact]
    public async Task Reconcile_never_infers_success_from_a_live_challenger_and_restores_the_incumbent()
    {
        SeedPendingWithChallengerLive();
        RecommendationTransitionResult result = await Service().ReconcileAsync(CancellationToken.None);
        Assert.Equal(RecommendationTransitionResult.FailedRestored, result.Outcome);
        Assert.Null(_store.Load().Pending);
        Assert.Null(_store.Load().Active);
        Assert.True(_fence.IsLiveExactModel(RecFx.PathOf(_proof.Incumbent), _proof.Incumbent.Identity.LocalSha256));
    }

    [Fact]
    public async Task Reconcile_returns_retry_later_and_preserves_pending_when_store_io_fails_then_resolves_when_healthy()
    {
        SeedPendingWithChallengerLive();
        CandidateRecommendationSnapshot before = _store.Load();
        var flakyStore = new IOExceptionRecommendationStore(_store, failingAbortAttempts: 1);

        RecommendationTransitionResult retry = await Service(store: flakyStore).ReconcileAsync(CancellationToken.None);

        Assert.Equal(RecommendationTransitionResult.RetryLater, retry.Outcome);
        Assert.True(retry.IsRetryLater);
        CandidateRecommendationSnapshot afterTransient = _store.Load();
        Assert.Equal(before.Generation, afterTransient.Generation);
        Assert.Equal(before.Pending?.TransactionId, afterTransient.Pending?.TransactionId);
        Assert.Equal(1, flakyStore.AbortAttempts);

        RecommendationTransitionResult resolved = await Service(store: flakyStore).ReconcileAsync(CancellationToken.None);

        Assert.Equal(RecommendationTransitionResult.FailedRestored, resolved.Outcome);
        Assert.Null(_store.Load().Pending);
        Assert.True(_fence.IsLiveExactModel(RecFx.PathOf(_proof.Incumbent), _proof.Incumbent.Identity.LocalSha256));
    }

    [Fact]
    public async Task Reconcile_preserves_pending_when_clearing_stale_active_authority_hits_store_io()
    {
        Assert.True((await Promote(Service())).IsCommitted);
        CandidateUtilityComparisonProof next =
            RecFx.Proof("org/next", "next.gguf", 'c', "org/challenger", "challenger.gguf", 'a');
        CandidateRecommendationSnapshot beforePending = _store.Load();
        Assert.True(_store.TryBeginPending(beforePending.Generation, Guid.NewGuid(), next,
            Admission(_proof.Challenger), Checks(_proof.Challenger)));
        _proofs.StaleFiles.Add("challenger.gguf");
        CandidateRecommendationSnapshot beforeReconcile = _store.Load();
        var flakyStore = new IOExceptionRecommendationStore(_store, failingClearAttempts: 1);

        RecommendationTransitionResult retry = await Service(store: flakyStore).ReconcileAsync(CancellationToken.None);

        Assert.True(retry.IsRetryLater);
        CandidateRecommendationSnapshot afterTransient = _store.Load();
        Assert.Equal(beforeReconcile.Generation, afterTransient.Generation);
        Assert.Equal(beforeReconcile.Pending?.TransactionId, afterTransient.Pending?.TransactionId);
        Assert.Equal(beforeReconcile.Active?.Proof.ProofSha256, afterTransient.Active?.Proof.ProofSha256);
        Assert.Equal(1, flakyStore.ClearAttempts);
    }

    [Fact]
    public async Task Hosted_reconcile_retries_transient_store_io_five_times_then_logs_and_stops_with_pending()
    {
        SeedPendingWithChallengerLive();
        CandidateRecommendationSnapshot before = _store.Load();
        var flakyStore = new IOExceptionRecommendationStore(_store, failingAbortAttempts: 5);
        var hosted = new RecommendationReconcileHostedService(Service(store: flakyStore));
        using var log = new StringWriter();
        using var listener = new TextWriterTraceListener(log);
        Trace.Listeners.Add(listener);
        try
        {
            await hosted.StartAsync(CancellationToken.None);
            Task completion = Assert.IsAssignableFrom<Task>(hosted.Completion);
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            await hosted.StopAsync(CancellationToken.None);
            Trace.Flush();

            Assert.Equal(5, flakyStore.AbortAttempts);
            Assert.Contains("retry limit reached after 5 attempts", log.ToString());
            CandidateRecommendationSnapshot afterRetries = _store.Load();
            Assert.Equal(before.Generation, afterRetries.Generation);
            Assert.Equal(before.Pending?.TransactionId, afterRetries.Pending?.TransactionId);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 200)]
    [InlineData(3, 400)]
    [InlineData(4, 800)]
    [InlineData(5, 800)]
    public void Hosted_reconcile_retry_backoff_is_exponential_and_capped(int attempt, int expectedMilliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds),
            RecommendationReconcileHostedService.RetryDelayForAttempt(attempt));

    [Fact]
    public async Task Reconcile_keeps_pending_and_retries_while_the_active_winner_currency_is_unknown()
    {
        Assert.True((await Promote(Service())).IsCommitted);
        CandidateUtilityComparisonProof next =
            RecFx.Proof("org/next", "next.gguf", 'c', "org/challenger", "challenger.gguf", 'a');
        CandidateRecommendationSnapshot beforePending = _store.Load();
        Assert.True(_store.TryBeginPending(beforePending.Generation, Guid.NewGuid(), next,
            Admission(_proof.Challenger), Checks(_proof.Challenger)));
        CandidateRecommendationSnapshot before = _store.Load();
        _proofs.Transient = true;

        RecommendationTransitionResult retry = await Service().ReconcileAsync(CancellationToken.None);

        Assert.True(retry.IsRetryLater);
        Assert.Equal("active_winner_unverified_pending_kept", retry.Reason);
        CandidateRecommendationSnapshot after = _store.Load();
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Pending?.TransactionId, after.Pending?.TransactionId);
        Assert.Equal(before.Active?.Proof.ProofSha256, after.Active?.Proof.ProofSha256);
    }

    [Fact]
    public async Task Reconcile_retries_when_clearing_stale_authority_loses_the_generation_race()
    {
        Assert.True((await Promote(Service())).IsCommitted);
        CandidateUtilityComparisonProof next =
            RecFx.Proof("org/next", "next.gguf", 'c', "org/challenger", "challenger.gguf", 'a');
        CandidateRecommendationSnapshot beforePending = _store.Load();
        Assert.True(_store.TryBeginPending(beforePending.Generation, Guid.NewGuid(), next,
            Admission(_proof.Challenger), Checks(_proof.Challenger)));
        _proofs.StaleFiles.Add("challenger.gguf");
        CandidateRecommendationSnapshot before = _store.Load();
        var refusing = new IOExceptionRecommendationStore(_store, refuseClear: true);

        RecommendationTransitionResult retry = await Service(store: refusing).ReconcileAsync(CancellationToken.None);

        Assert.True(retry.IsRetryLater);
        Assert.Equal("store_changed_during_reconcile", retry.Reason);
        CandidateRecommendationSnapshot after = _store.Load();
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Pending?.TransactionId, after.Pending?.TransactionId);
        Assert.Equal(before.Active?.Proof.ProofSha256, after.Active?.Proof.ProofSha256);
    }

    [Fact]
    public async Task Reconcile_during_Gaming_or_stop_stops_the_unverified_runtime_and_never_reloads()
    {
        SeedPendingWithChallengerLive();
        _runtime.Gaming = true;
        RecommendationTransitionResult result = await Service().ReconcileAsync(CancellationToken.None);
        Assert.Equal(RecommendationTransitionResult.FailedRuntimeStopped, result.Outcome);
        Assert.Empty(_runtime.Loads);
        Assert.False(_runtime.Running);
        Assert.Null(_store.Load().Pending);
    }

    [Fact]
    public async Task Reconcile_with_no_runtime_running_does_not_start_one()
    {
        SeedPendingWithChallengerLive();
        _runtime.StopText();
        RecommendationTransitionResult result = await Service().ReconcileAsync(CancellationToken.None);
        Assert.Equal("pending_aborted", result.Reason);
        Assert.Empty(_runtime.Loads);
        Assert.Null(_store.Load().Pending);
    }

    [Fact]
    public async Task Reconcile_clears_authority_of_an_Active_winner_whose_bytes_changed()
    {
        Assert.True((await Promote(Service())).IsCommitted);
        _proofs.StaleFiles.Add("challenger.gguf");
        RecommendationTransitionResult result = await Service().ReconcileAsync(CancellationToken.None);
        Assert.Equal("active_winner_stale_authority_cleared", result.Reason);
        Assert.Null(_store.Load().Active);
        Assert.Null(_store.Load().Previous);
    }

    [Fact]
    public async Task Reconcile_keeps_authority_when_currency_is_unknown_because_of_a_transient_failure()
    {
        Assert.True((await Promote(Service())).IsCommitted);
        _proofs.Transient = true;
        RecommendationTransitionResult result = await Service().ReconcileAsync(CancellationToken.None);
        Assert.Equal("active_winner_unverified_kept", result.Reason);
        Assert.False(result.IsCommitted);
        Assert.NotEqual(RecommendationTransitionResult.Committed, result.Outcome);
        Assert.NotNull(_store.Load().GetActiveRecord());
        Assert.NotNull(_store.Load().Previous is null ? _store.Load().Fallback : _store.Load().Previous);
    }

    [Fact]
    public async Task Reconcile_refuses_when_writer_is_unavailable_and_preserves_pending()
    {
        SeedPendingWithChallengerLive();
        var blocked = new BlockedWriterRuntime(_runtime);
        var service = new CandidateRecommendationService(_store, blocked, _proofs, _fence, RecFx.Models, null,
            TimeSpan.FromMilliseconds(300), _root, TimeSpan.FromMilliseconds(100));
        RecommendationTransitionResult result = await service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(RecommendationTransitionResult.Refused, result.Outcome);
        Assert.Equal("reconcile_writer_busy", result.Reason);
        Assert.True(_runtime.Running);
        Assert.Empty(_runtime.Loads);
        Assert.Equal(0, _runtime.Stops);
        Assert.NotNull(_store.Load().Pending);
    }

    [Theory]
    [InlineData(0L, 60)]
    [InlineData(2_000_000_000L, 60 + 95)]
    [InlineData(long.MaxValue, 900)]
    public void Recovery_budget_scales_with_model_size_and_is_bounded(long size, int expectedSeconds) =>
        Assert.Equal(expectedSeconds, (int)CandidateRecommendationService.RecoveryBudgetFor(size).TotalSeconds);

    [Fact]
    public void Challenger_health_budget_uses_the_larger_configured_or_size_budget()
    {
        long smallSize = _proof.Challenger.Identity.SizeBytes;
        const long largeSize = 2_000_000_000L;
        TimeSpan configured = CandidateRecommendationService.HealthBudget;
        TimeSpan smallBudget = CandidateRecommendationService.ChallengerHealthBudgetFor(smallSize, configured);
        TimeSpan largeBudget = CandidateRecommendationService.ChallengerHealthBudgetFor(largeSize, configured);

        Assert.Equal(CandidateRecommendationService.RecoveryBudgetFor(smallSize), smallBudget);
        Assert.Equal(CandidateRecommendationService.RecoveryBudgetFor(largeSize), largeBudget);
        Assert.True(largeBudget > smallBudget);

        TimeSpan injectedBudget = TimeSpan.FromSeconds(200);
        Assert.Equal(injectedBudget,
            CandidateRecommendationService.ChallengerHealthBudgetFor(largeSize, injectedBudget));
    }

    private sealed class BlockedWriterRuntime(FakeRecRuntime inner) : IRecommendationRuntime
    {
        public bool GamingActive => inner.GamingActive;
        public bool StopRequested => inner.StopRequested;
        public bool TextRuntimeRunning => inner.TextRuntimeRunning;
        public Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken) =>
            throw new OperationCanceledException("writer wait timed out");
        public Task<RuntimeLoadResult> LoadExactAsync(IAsyncDisposable writer, string role, string candidateKey, string path,
            string sha256, CancellationToken cancellationToken) => inner.LoadExactAsync(writer, role, candidateKey, path, sha256, cancellationToken);
        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => inner.IsHealthyAsync(cancellationToken);
        public bool StopText() => inner.StopText();
    }

    private sealed class IOExceptionRecommendationStore(ICandidateRecommendationStore inner,
        int failingAbortAttempts = 0, int failingClearAttempts = 0, bool refuseClear = false)
        : ICandidateRecommendationStore
    {
        private int _abortAttempts;
        private int _clearAttempts;
        internal int AbortAttempts => Volatile.Read(ref _abortAttempts);
        internal int ClearAttempts => Volatile.Read(ref _clearAttempts);

        public CandidateRecommendationSnapshot Load() => inner.Load();
        public bool TryBeginPending(long expectedGeneration, Guid transactionId, CandidateUtilityComparisonProof proof,
            CandidateLaunchAdmission admission, RecommendationTransitionChecks checks) =>
            inner.TryBeginPending(expectedGeneration, transactionId, proof, admission, checks);
        public bool TryCommitPending(long expectedGeneration, Guid transactionId, CandidateLaunchAdmission admission,
            RecommendationTransitionChecks checks) => inner.TryCommitPending(expectedGeneration, transactionId, admission, checks);
        public bool TryBeginRollback(long expectedGeneration, Guid transactionId, CandidateLaunchAdmission liveAdmission,
            RecommendationTransitionChecks checks) => inner.TryBeginRollback(expectedGeneration, transactionId, liveAdmission, checks);
        public bool TryClearAuthority(long expectedGeneration)
        {
            int attempt = Interlocked.Increment(ref _clearAttempts);
            if (attempt <= failingClearAttempts) throw new IOException("Transient recommendation store I/O.");
            if (refuseClear) return false; // another writer won the generation CAS
            return inner.TryClearAuthority(expectedGeneration);
        }
        public bool TryAbortPending(long expectedGeneration, Guid transactionId)
        {
            int attempt = Interlocked.Increment(ref _abortAttempts);
            if (attempt <= failingAbortAttempts) throw new IOException("Transient recommendation store I/O.");
            return inner.TryAbortPending(expectedGeneration, transactionId);
        }
    }

    private sealed class DelayedHealthRuntime(FakeRecRuntime inner, TimeSpan delay) : IRecommendationRuntime
    {
        public bool GamingActive => inner.GamingActive;
        public bool StopRequested => inner.StopRequested;
        public bool TextRuntimeRunning => inner.TextRuntimeRunning;
        public Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken) => inner.AcquireWriterAsync(cancellationToken);
        public Task<RuntimeLoadResult> LoadExactAsync(IAsyncDisposable writer, string role, string candidateKey, string path,
            string sha256, CancellationToken cancellationToken) => inner.LoadExactAsync(writer, role, candidateKey, path, sha256, cancellationToken);
        public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
        {
            if (inner.Loaded?.Path.EndsWith("challenger.gguf", StringComparison.OrdinalIgnoreCase) == true)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return await inner.IsHealthyAsync(cancellationToken).ConfigureAwait(false);
        }
        public bool StopText() => inner.StopText();
    }

    // ---- helpers ----

    private CandidateLaunchAdmission Admission(CandidateProofSide side) => new(RecFx.ActualConfig, "text-cuda",
        RecFx.Host.HardwareFingerprint, RecFx.Host.RuntimeFingerprint, RecFx.Host.BinaryFingerprint,
        RecFx.PathOf(side), DateTime.UtcNow);

    private RecommendationTransitionChecks Checks(CandidateProofSide liveSide) => new(
        () => new TextLaunchDescriptor(1, "text-cuda", System.Collections.Immutable.ImmutableArray<string>.Empty,
            System.Collections.Immutable.ImmutableArray<string>.Empty, RecFx.ActualConfig, RecFx.PathOf(liveSide)),
        (_, _) => true, _ => true, () => true);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
