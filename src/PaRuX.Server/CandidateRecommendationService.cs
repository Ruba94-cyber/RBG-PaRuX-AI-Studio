using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Runtime operations the recommendation transition needs, all under ONE held exclusive writer.</summary>
internal interface IRecommendationRuntime
{
    bool GamingActive { get; }
    /// <summary>Hard STOP (Core shutting down): no model may be started or replaced.</summary>
    bool StopRequested { get; }
    bool TextRuntimeRunning { get; }
    /// <summary>Acquires the exclusive text-transition writer; the transition holds it from proof to commit.</summary>
    Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken);
    /// <summary>Fresh exact load (leased bytes, hash, health, Gaming admission) under the already held writer.</summary>
    Task<RuntimeLoadResult> LoadExactAsync(IAsyncDisposable writer, string role, string candidateKey, string path, string sha256,
        CancellationToken cancellationToken);
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
    bool StopText();
}

internal interface IRecommendationProofSource
{
    /// <summary>A positive pre-activation proof for the challenger against the live incumbent, or the refusal.</summary>
    Task<CandidateUtilityProofResult> CreateProofAsync(string candidateKey, string managedFileName,
        CancellationToken cancellationToken);
    /// <summary>Checks the stored license evidence, optionally against the exact evaluated challenger identity.</summary>
    string? CheckLicenseEvidence(string candidateKey, string managedFileName, CandidateProofSide? evaluatedSide);
    /// <summary>Holds the evaluated exact bytes stable through the Pending compare-and-swap.</summary>
    Task<IDisposable?> CaptureLicenseEvidenceReadLeaseAsync(string candidateKey, string managedFileName,
        CandidateProofSide? evaluatedSide, CancellationToken cancellationToken);
    /// <summary>True when the proof's immutable snapshot (policy, provenance, ledger, files) is still current.</summary>
    bool IsSnapshotCurrent(CandidateUtilityComparisonProof proof);
    /// <summary>Tri-state currency of the side's exact bytes/provenance: null means unknown (transient failure).</summary>
    bool? IsSideCurrent(CandidateProofSide side);
    /// <summary>True when the proof was made under the policy that is configured now.</summary>
    bool IsPolicyCurrent(CandidateUtilityComparisonProof proof);
}

internal sealed record RecommendationTransitionResult(string Outcome, string Reason, long Generation,
    bool? RuntimeRestored = null)
{
    internal const string Committed = "Committed";
    internal const string Refused = "Refused";
    internal const string FailedRestored = "FailedRestored";
    internal const string FailedRuntimeStopped = "FailedRuntimeStopped";
    internal const string RetryLater = "RetryLater";
    internal bool IsCommitted => Outcome == Committed;
    internal bool IsRetryLater => Outcome == RetryLater;
}

/// <summary>
/// Production coordinator for the durable Pending -> Active recommendation transaction, explicit rollback and
/// restart reconciliation (see the P0 promotion transaction plan). It grants no routing authority itself and
/// refuses unless every proof, scope, pin, Gaming/STOP and exact-bytes requirement holds at the final CAS.
///
/// Ordering (one exclusive text-transition writer is held from step 2 to the end):
///  1. consent / Gaming / STOP / expected generation / existing Pending / manual pin preflight (cheap, no writer);
///  2. acquire the writer, re-check Gaming/STOP;
///  3. proof against the LIVE incumbent (fence, host attestation, exact bytes);
///  4. durable Pending by generation CAS (recovery marker; grants nothing);
///  5. fresh exact load of the challenger, bounded health check (a hung-but-alive runtime fails here);
///  6. final gated CAS Pending -> Active (fence + exact live bytes + snapshot + no user pin);
///  failure after step 4: abort Pending by CAS FIRST (durable state stays consistent), then restore the incumbent
///  only if Gaming/STOP allow and only through the same fresh exact admission; otherwise report a stopped runtime.
/// Cancellation before the commit aborts; after the durable commit the committed result is returned.
/// Recovery steps run on their own bounded token, independent of the (possibly cancelled) request.
/// The transition never writes a role assignment, pin or routing preference.
/// </summary>
internal sealed class CandidateRecommendationService
{
    /// <summary>Recovery reloads a model, so the budget scales with its size (assumes a slow 20 MB/s worst case
    /// after a fixed minute), bounded to [1, 15] minutes.</summary>
    internal static TimeSpan RecoveryBudgetFor(long modelSizeBytes) => TimeSpan.FromSeconds(Math.Clamp(
        60 + Math.Max(0, modelSizeBytes) / (20L * 1024 * 1024), 60, 15 * 60));
    internal static readonly TimeSpan HealthBudget = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DefaultReconcileWaitBound = TimeSpan.FromSeconds(5);

    internal static TimeSpan ChallengerHealthBudgetFor(long expectedSizeBytes, TimeSpan configuredHealthBudget,
        Func<long, TimeSpan>? recoveryBudgetFor = null)
    {
        TimeSpan sizeBudget = (recoveryBudgetFor ?? RecoveryBudgetFor)(expectedSizeBytes);
        return configuredHealthBudget >= sizeBudget ? configuredHealthBudget : sizeBudget;
    }

    private readonly ICandidateRecommendationStore _store;
    private readonly IRecommendationRuntime _runtime;
    private readonly IRecommendationProofSource _proofs;
    private readonly ICandidateLaunchFenceSource _fence;
    private readonly string _modelsDirectory;
    private readonly Action<string>? _crashPoint;
    private readonly TimeSpan _healthBudget;
    private readonly Func<long, TimeSpan> _healthRecoveryBudgetFor;
    private readonly string? _pinRoot;
    private readonly TimeSpan _reconcileWaitBound;
    private readonly string _role = CandidateGeneralChatQualitySuite.RouteRole;
    private readonly string _profile = CandidateGeneralChatQualitySuite.AutoProfile;
    // One transition per Core process at a time, on top of the cross-process store CAS.
    private readonly SemaphoreSlim _single = new(1, 1);

    internal CandidateRecommendationService(ICandidateRecommendationStore store, IRecommendationRuntime runtime,
        IRecommendationProofSource proofs, ICandidateLaunchFenceSource fence, string modelsDirectory,
        Action<string>? crashPoint = null, TimeSpan? healthBudget = null,
        string? pinRoot = null, TimeSpan? reconcileWaitBound = null,
        Func<long, TimeSpan>? healthRecoveryBudgetFor = null)
    {
        _reconcileWaitBound = reconcileWaitBound ?? DefaultReconcileWaitBound;
        if (_reconcileWaitBound <= TimeSpan.Zero || _reconcileWaitBound.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(reconcileWaitBound), "The reconcile wait bound must be finite and positive.");
        _pinRoot = pinRoot;
        _healthBudget = healthBudget ?? HealthBudget;
        _healthRecoveryBudgetFor = healthRecoveryBudgetFor ?? RecoveryBudgetFor;
        _store = store;
        _runtime = runtime;
        _proofs = proofs;
        _fence = fence;
        _modelsDirectory = modelsDirectory;
        _crashPoint = crashPoint;
    }

    private RecommendationTransitionChecks PromoteChecks() => new(_fence.ReadVerifiedFence, _fence.IsLiveExactModel,
        _proofs.IsSnapshotCurrent, () => !ManualSelectionGate.HasUserPin(_role, _profile, _pinRoot));

    private RecommendationTransitionChecks RollbackChecks() => new(_fence.ReadVerifiedFence, _fence.IsLiveExactModel,
        proof => _proofs.IsPolicyCurrent(proof) && _proofs.IsSideCurrent(proof.Challenger) == true,
        () => !ManualSelectionGate.HasUserPin(_role, _profile, _pinRoot));

    private RecommendationTransitionResult Refuse(string reason, long generation) =>
        new(RecommendationTransitionResult.Refused, reason, generation);

    private static RecommendationTransitionResult RetryLater(string reason, long generation) =>
        new(RecommendationTransitionResult.RetryLater, reason, generation);

    private string? Preflight(bool consent)
    {
        if (!consent) return "consent_required";
        if (_runtime.StopRequested) return "server_stopping";
        if (_runtime.GamingActive) return "gaming_active";
        return ManualSelectionGate.GetUserPinReason(_role, _profile, _pinRoot);
    }

    /// <summary>Promote <paramref name="candidateKey"/> (exact managed file) over the live incumbent.</summary>
    internal async Task<RecommendationTransitionResult> PromoteAsync(string candidateKey, string managedFileName,
        long expectedGeneration, bool consent, CancellationToken cancellationToken)
    {
        if (Preflight(consent) is { } early) return Refuse(early, expectedGeneration);
        if (!await _single.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Refuse("transition_in_progress", expectedGeneration);
        try
        {
            CandidateRecommendationSnapshot snapshot = _store.Load();
            if (snapshot.Generation != expectedGeneration) return Refuse("stale_generation", snapshot.Generation);
            if (snapshot.Pending is not null) return Refuse("pending_unreconciled", snapshot.Generation);

            await using IAsyncDisposable writer = await _runtime.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
            if (_runtime.StopRequested) return Refuse("server_stopping", expectedGeneration);
            if (_runtime.GamingActive) return Refuse("gaming_active", expectedGeneration);

            if (_proofs.CheckLicenseEvidence(candidateKey, managedFileName, evaluatedSide: null) is { } licenseReason)
                return Refuse(licenseReason, expectedGeneration);

            CandidateUtilityProofResult proofResult = await _proofs.CreateProofAsync(candidateKey, managedFileName,
                cancellationToken).ConfigureAwait(false);
            if (proofResult.Proof is not { } proof)
            {
                if (_proofs.CheckLicenseEvidence(candidateKey, managedFileName, evaluatedSide: null) is { } proofFailure)
                    return Refuse(proofFailure, expectedGeneration);
                if (proofResult.Comparison.ReasonCode == "candidate_or_reference_identity_unavailable")
                {
                    using IDisposable? verifiedFile = await _proofs.CaptureLicenseEvidenceReadLeaseAsync(
                        candidateKey, managedFileName, evaluatedSide: null, cancellationToken).ConfigureAwait(false);
                    if (verifiedFile is null)
                        return Refuse("licence_evidence_changed", expectedGeneration);
                }
                return Refuse("proof_" + proofResult.Comparison.ReasonCode, expectedGeneration);
            }
            if (_proofs.CheckLicenseEvidence(candidateKey, managedFileName, proof.Challenger) is { } changedLicense)
                return Refuse(changedLicense, expectedGeneration);
            if (proof.RouteRole != _role || proof.AutoProfile != _profile) return Refuse("unsupported_scope", expectedGeneration);

            string incumbentPath = ManagedPath(proof.Incumbent);
            string challengerPath = ManagedPath(proof.Challenger);
            CandidateLaunchAdmission? admission = await AdmitLiveAsync(proof, incumbentPath, cancellationToken)
                .ConfigureAwait(false);
            if (admission is null) return Refuse("incumbent_not_admissible", expectedGeneration);

            Guid transaction = Guid.NewGuid();
            using (IDisposable? licenseReadLease = await _proofs.CaptureLicenseEvidenceReadLeaseAsync(
                candidateKey, managedFileName, proof.Challenger, cancellationToken).ConfigureAwait(false))
            {
                if (licenseReadLease is null)
                {
                    string reason = _proofs.CheckLicenseEvidence(candidateKey, managedFileName, proof.Challenger)
                        ?? "licence_evidence_changed";
                    return Refuse(reason, expectedGeneration);
                }

                Crash("before_pending");
                if (!_store.TryBeginPending(expectedGeneration, transaction, proof, admission, PromoteChecks()))
                {
                    string reason = _proofs.CheckLicenseEvidence(candidateKey, managedFileName, proof.Challenger)
                        ?? "intent_rejected";
                    return Refuse(reason, expectedGeneration);
                }
            }
            Crash("after_pending");

            return await RunLoadAndCommitAsync(writer, transaction, expectedGeneration + 1, proof,
                liveBefore: proof.Incumbent, target: proof.Challenger,
                targetPath: challengerPath, incumbentPath, PromoteChecks(), cancellationToken).ConfigureAwait(false);
        }
        finally { _single.Release(); }
    }

    /// <summary>Explicit rollback to the stored previous winner (validated, not re-compared).</summary>
    internal async Task<RecommendationTransitionResult> RollbackAsync(long expectedGeneration, bool consent,
        CancellationToken cancellationToken)
    {
        if (Preflight(consent) is { } early) return Refuse(early, expectedGeneration);
        if (!await _single.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Refuse("transition_in_progress", expectedGeneration);
        try
        {
            CandidateRecommendationSnapshot snapshot = _store.Load();
            if (snapshot.Generation != expectedGeneration) return Refuse("stale_generation", snapshot.Generation);
            if (snapshot.Pending is not null) return Refuse("pending_unreconciled", snapshot.Generation);
            if (snapshot.Active is null || snapshot.Previous is null) return Refuse("no_previous_winner", snapshot.Generation);
            CandidateUtilityComparisonProof active = snapshot.Active.Proof, target = snapshot.Previous.Proof;

            await using IAsyncDisposable writer = await _runtime.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
            if (_runtime.StopRequested) return Refuse("server_stopping", expectedGeneration);
            if (_runtime.GamingActive) return Refuse("gaming_active", expectedGeneration);

            string livePath = ManagedPath(active.Challenger);
            CandidateLaunchAdmission? admission = await AdmitLiveAsync(active, livePath, cancellationToken)
                .ConfigureAwait(false);
            if (admission is null) return Refuse("live_winner_not_admissible", expectedGeneration);

            Guid transaction = Guid.NewGuid();
            Crash("before_pending");
            if (!_store.TryBeginRollback(expectedGeneration, transaction, admission, RollbackChecks()))
                return Refuse("rollback_rejected", expectedGeneration);
            Crash("after_pending");

            return await RunLoadAndCommitAsync(writer, transaction, expectedGeneration + 1, target,
                liveBefore: active.Challenger, target: target.Challenger,
                targetPath: ManagedPath(target.Challenger), livePath, RollbackChecks(), cancellationToken)
                .ConfigureAwait(false);
        }
        finally { _single.Release(); }
    }

    private async Task<RecommendationTransitionResult> RunLoadAndCommitAsync(IAsyncDisposable writer, Guid transaction,
        long pendingGeneration, CandidateUtilityComparisonProof pendingProof,
        CandidateProofSide liveBefore, CandidateProofSide target, string targetPath, string liveBeforePath,
        RecommendationTransitionChecks checks, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeLoadResult loaded = await _runtime.LoadExactAsync(writer, _role, target.Identity.CandidateKey, targetPath,
                target.Identity.LocalSha256, cancellationToken).ConfigureAwait(false);
            if (!loaded.Success)
                return await FailAsync(writer, transaction, pendingGeneration, liveBefore, liveBeforePath, "challenger_load_failed");
            Crash("after_load");
            if (!await HealthyAsync(cancellationToken, target.Identity.SizeBytes).ConfigureAwait(false))
                return await FailAsync(writer, transaction, pendingGeneration, liveBefore, liveBeforePath, "challenger_unhealthy");
            cancellationToken.ThrowIfCancellationRequested();
            if (_runtime.StopRequested || _runtime.GamingActive)
                return await FailAsync(writer, transaction, pendingGeneration, liveBefore, liveBeforePath,
                    _runtime.StopRequested ? "server_stopping" : "gaming_active");
            CandidateLaunchAdmission? admission = await AdmitLiveAsync(pendingProof, targetPath, cancellationToken)
                .ConfigureAwait(false);
            if (admission is null)
                return await FailAsync(writer, transaction, pendingGeneration, liveBefore, liveBeforePath, "challenger_not_admissible");
            Crash("before_commit");
            if (!_store.TryCommitPending(pendingGeneration, transaction, admission, checks))
                return await FailAsync(writer, transaction, pendingGeneration, liveBefore, liveBeforePath, "commit_rejected");
            Crash("after_commit");
            // Durable commit done: from here the result is Committed whatever the request token does.
            return new RecommendationTransitionResult(RecommendationTransitionResult.Committed, "committed",
                pendingGeneration + 1);
        }
        catch (OperationCanceledException)
        {
            return await FailAsync(writer, transaction, pendingGeneration, liveBefore, liveBeforePath, "cancelled");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return await FailAsync(writer, transaction, pendingGeneration, liveBefore, liveBeforePath, "transition_error");
        }
    }

    /// <summary>Abort Pending by CAS first, then restore the model that must be live, if Gaming/STOP allow.</summary>
    private async Task<RecommendationTransitionResult> FailAsync(IAsyncDisposable writer, Guid transaction,
        long pendingGeneration, CandidateProofSide restore, string restorePath, string reason)
    {
        using var recovery = new CancellationTokenSource(RecoveryBudgetFor(restore.Identity.SizeBytes));
        bool aborted = _store.TryAbortPending(pendingGeneration, transaction);
        long generation = aborted ? pendingGeneration + 1 : _store.Load().Generation;
        string failure = aborted ? reason : reason + "_pending_abort_failed";
        bool? restored = null;
        try
        {
            if (_fence.IsLiveExactModel(restorePath, restore.Identity.LocalSha256)) restored = true; // never swapped away
            else
            {
                _runtime.StopText();
                if (_runtime.StopRequested || _runtime.GamingActive) restored = false;
                else
                {
                    RuntimeLoadResult back = await _runtime.LoadExactAsync(writer, _role, restore.Identity.CandidateKey, restorePath,
                        restore.Identity.LocalSha256, recovery.Token).ConfigureAwait(false);
                    restored = back.Success && await HealthyAsync(recovery.Token).ConfigureAwait(false);
                    if (restored != true) _runtime.StopText(); // a stopped runtime is safer than an unverified one
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or OperationCanceledException)
        {
            _runtime.StopText();
            restored = false;
        }
        return new RecommendationTransitionResult(restored == true
            ? RecommendationTransitionResult.FailedRestored : RecommendationTransitionResult.FailedRuntimeStopped,
            failure, generation, restored);
    }

    /// <summary>
    /// Restart reconciliation. A Pending is NEVER a winner and success is never inferred from a process or bytes
    /// being present: Pending is aborted, the prior Active (or first-incumbent fallback) stays, and the runtime is
    /// restored to that model only when Gaming/STOP allow. An Active whose bytes/provenance changed loses authority.
    /// </summary>
    internal async Task<RecommendationTransitionResult> ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!await _single.WaitAsync(_reconcileWaitBound, cancellationToken).ConfigureAwait(false))
            return Refuse("reconcile_busy", -1);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CandidateRecommendationSnapshot snapshot;
            try { snapshot = _store.Load(); }
            catch (IOException)
            { return RetryLater("store_io_retry_later", -1); }
            catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException)
            { return Refuse("store_unreadable", -1); }

            if (snapshot.Pending is not { } pending)
            {
                bool? activeCurrent = snapshot.Active is null ? true : _proofs.IsSideCurrent(snapshot.Active.Proof.Challenger);
                if (activeCurrent is null) // unknown (transient): keep authority, decide next time
                    return new(RecommendationTransitionResult.Refused, "active_winner_unverified_kept", snapshot.Generation);
                if (activeCurrent == false)
                {
                    bool cleared;
                    try { cleared = _store.TryClearAuthority(snapshot.Generation); }
                    catch (IOException)
                    { return RetryLater("store_io_retry_later", snapshot.Generation); }
                    if (!cleared) return RetryLater("store_changed_during_reconcile", snapshot.Generation);
                    return new(RecommendationTransitionResult.Refused, "active_winner_stale_authority_cleared",
                        snapshot.Generation + 1);
                }
                return new(RecommendationTransitionResult.Committed, "nothing_to_reconcile", snapshot.Generation);
            }

            bool activeStale = false;
            if (snapshot.Active is not null)
            {
                bool? activeCurrent;
                try { activeCurrent = _proofs.IsSideCurrent(snapshot.Active.Proof.Challenger); }
                catch (IOException)
                { return RetryLater("proof_io_retry_later", snapshot.Generation); }
                if (activeCurrent is null)
                    return RetryLater("active_winner_unverified_pending_kept", snapshot.Generation);
                activeStale = activeCurrent == false;
            }

            // The model that must be serving after recovery: prior Active winner, else the first incumbent.
            CandidateProofIdentity? expected = snapshot.Active?.Proof.Challenger.Identity ?? snapshot.Fallback?.Identity
                ?? pending.Proof.Incumbent.Identity;
            if (activeStale) expected = pending.Proof.Incumbent.Identity;

            string expectedPath = Path.GetFullPath(Path.Combine(_modelsDirectory, expected.ManagedFileName));
            bool expectedAlreadyServing = !_runtime.TextRuntimeRunning
                || _fence.IsLiveExactModel(expectedPath, expected.LocalSha256);
            bool hardOff = _runtime.StopRequested || _runtime.GamingActive;
            using var recovery = new CancellationTokenSource(RecoveryBudgetFor(expected.SizeBytes));
            IAsyncDisposable? writer = null;
            if (!expectedAlreadyServing && !hardOff)
            {
                try { writer = await AcquireReconcileWriterAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
                { return Refuse("reconcile_writer_busy", snapshot.Generation); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    // Unavailable, unlike busy: no other transition is known to drive the runtime, so a possibly
                    // half-activated challenger is stopped. Pending is kept for the next reconcile to decide.
                    _runtime.StopText();
                    return Refuse("reconcile_writer_unavailable", snapshot.Generation);
                }
            }

            await using IAsyncDisposable? heldWriter = writer;
            cancellationToken.ThrowIfCancellationRequested();
            // STOP/Gaming and the live runtime may change while writer acquisition is pending.
            if (writer is not null)
            {
                expectedAlreadyServing = !_runtime.TextRuntimeRunning
                    || _fence.IsLiveExactModel(expectedPath, expected.LocalSha256);
                hardOff = _runtime.StopRequested || _runtime.GamingActive;
            }
            long generation;
            if (activeStale)
            {
                // Clear stale authority and Pending in one durable transition so a failed replace preserves Pending.
                bool cleared;
                try { cleared = _store.TryClearAuthority(snapshot.Generation); }
                catch (IOException)
                { return RetryLater("store_io_retry_later", snapshot.Generation); }
                if (!cleared) return RetryLater("store_changed_during_reconcile", snapshot.Generation);
                generation = snapshot.Generation + 1;
            }
            else
            {
                bool aborted;
                try { aborted = _store.TryAbortPending(snapshot.Generation, pending.TransactionId); }
                catch (IOException)
                { return RetryLater("store_io_retry_later", snapshot.Generation); }
                if (!aborted) return RetryLater("store_changed_during_reconcile", snapshot.Generation);
                generation = snapshot.Generation + 1;
            }

            if (expectedAlreadyServing)
                return new(RecommendationTransitionResult.FailedRestored, "pending_aborted", generation, true);
            if (hardOff || _runtime.StopRequested || _runtime.GamingActive)
            {
                // Hard-off: never reload. The unverified runtime is stopped rather than left serving a model that
                // may be the half-activated challenger.
                _runtime.StopText();
                return new(RecommendationTransitionResult.FailedRuntimeStopped, "pending_aborted_runtime_stopped",
                    generation, false);
            }

            _runtime.StopText();
            if (_runtime.StopRequested || _runtime.GamingActive)
            {
                _runtime.StopText();
                return new(RecommendationTransitionResult.FailedRuntimeStopped, "pending_aborted_runtime_stopped",
                    generation, false);
            }

            bool restored = false;
            try
            {
                RuntimeLoadResult back = await _runtime.LoadExactAsync(heldWriter!, _role, expected.CandidateKey, expectedPath, expected.LocalSha256,
                    recovery.Token).ConfigureAwait(false);
                restored = back.Success && await HealthyAsync(recovery.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                or InvalidOperationException or OperationCanceledException) { }
            if (!restored) _runtime.StopText();
            return new(restored ? RecommendationTransitionResult.FailedRestored
                : RecommendationTransitionResult.FailedRuntimeStopped, "pending_aborted", generation, restored);
        }
        finally { _single.Release(); }
    }

    private async Task<IAsyncDisposable> AcquireReconcileWriterAsync(CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(_reconcileWaitBound);
        Task<IAsyncDisposable> acquisition = _runtime.AcquireWriterAsync(wait.Token);
        try
        {
            return await acquisition.WaitAsync(_reconcileWaitBound, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            wait.Cancel();
            _ = DisposeLateWriterAsync(acquisition);
            throw;
        }
    }

    private static async Task DisposeLateWriterAsync(Task<IAsyncDisposable> acquisition)
    {
        try
        {
            IAsyncDisposable writer = await acquisition.ConfigureAwait(false);
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Observe a timed-out acquisition and release any writer it yields after the refusal.
        }
    }

    private async Task<bool> HealthyAsync(CancellationToken cancellationToken, long? expectedSizeBytes = null)
    {
        // A hung-but-alive runtime never answers: bound the check so it fails the activation phase instead of
        // blocking it, and never trust "process exists" as health.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan healthBudget = expectedSizeBytes is { } sizeBytes
            ? ChallengerHealthBudgetFor(sizeBytes, _healthBudget, _healthRecoveryBudgetFor)
            : _healthBudget;
        budget.CancelAfter(healthBudget);
        try { return await _runtime.IsHealthyAsync(budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException) { return false; }
    }

    private async Task<CandidateLaunchAdmission?> AdmitLiveAsync(CandidateUtilityComparisonProof proof, string modelPath,
        CancellationToken cancellationToken)
    {
        TextLaunchDescriptor? fence = _fence.ReadVerifiedFence();
        if (fence is null) return null;
        CandidateLaunchHost? host = await _fence.CaptureHostAsync(fence.Backend, cancellationToken).ConfigureAwait(false);
        CandidateLaunchScopeResult scope = CandidateLaunchScope.Check(proof.QualityEnvironment,
            proof.PerformanceEnvironment, fence, modelPath, host);
        return scope.Admissible ? scope.Admission : null;
    }

    private string ManagedPath(CandidateProofSide side) =>
        Path.GetFullPath(Path.Combine(_modelsDirectory, side.Identity.ManagedFileName));

    private void Crash(string point) => _crashPoint?.Invoke(point);
}
