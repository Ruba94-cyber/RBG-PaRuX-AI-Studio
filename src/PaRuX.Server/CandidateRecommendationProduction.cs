using Microsoft.Extensions.Hosting;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Production runtime adapter: the exclusive text-transition writer and the fresh exact load.</summary>
internal sealed class ManagedRecommendationRuntime(RuntimeManagerService runtime, LocalAiProcessManager process,
    CandidateEvidenceService evidence, IHostApplicationLifetime lifetime) : IRecommendationRuntime
{
    private sealed class Writer(MediaExecutionCoordinator.Lease lease) : IAsyncDisposable
    {
        internal MediaExecutionCoordinator.Lease Lease { get; } = lease;
        public ValueTask DisposeAsync() => Lease.DisposeAsync();
    }

    public bool GamingActive => process.GamingActive;
    public bool StopRequested => lifetime.ApplicationStopping.IsCancellationRequested;
    public bool TextRuntimeRunning => process.GetTextProcessId() is not null;

    public async Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken) =>
        new Writer(await MediaExecutionCoordinator.Current
            .AcquireAsync("text-transition:recommendation", cancellationToken).ConfigureAwait(false));

    public Task<RuntimeLoadResult> LoadExactAsync(IAsyncDisposable writer, string role, string candidateKey, string path, string sha256,
        CancellationToken cancellationToken)
    {
        string file = Path.GetFileName(path);
        CandidateEvidenceIdentity? initial = evidence.TryCaptureProvenanceSnapshot(candidateKey, file);
        if (initial is null || !string.Equals(initial.LocalSha256, sha256, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new RuntimeLoadResult { Success = false, Role = role,
                Message = "The exact Candidate identity is no longer current; no model was substituted." });
        return runtime.EnsureFreshExactCandidateLoadedHeldAsync(role, path, sha256,
            () => evidence.TryOpenManagedFileReadLease(file, initial.SizeBytes),
            token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(evidence.IsProvenanceSnapshotCurrent(initial)
                    && evidence.IsManagedFilePathCurrent(file, initial.SizeBytes));
            }, ((Writer)writer).Lease, cancellationToken);
    }

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => process.IsTextHealthyAsync(cancellationToken);
    public bool StopText() => process.StopText();
}

/// <summary>Production proof source: current policy, actual-launch environments and live incumbent identity.</summary>
internal sealed class ProductionRecommendationProofSource(CandidateEvidenceService evidence,
    CandidateUtilityPolicyStore policies, CandidateUtilityPolicyCalibrationService calibration,
    IActiveTextModelIdentitySource activeSource, ICandidateLaunchFenceSource fence) : IRecommendationProofSource
{
    public async Task<CandidateUtilityProofResult> CreateProofAsync(string candidateKey, string managedFileName,
        CancellationToken cancellationToken)
    {
        CandidateUtilityPolicyDefinition? policy = policies.Load().Policy;
        CandidateEvaluationProfileDefinition profile = CandidateEvaluationProfileCatalog.GeneralChat;
        string path = Path.Combine(evidence.ModelsDirectory, managedFileName);
        CandidateUtilityPolicyCalibrationService.EvaluationScope scope = await calibration.ResolveScopeAsync(profile,
            path, cancellationToken).ConfigureAwait(false);
        if (scope.Quality is not { } quality || scope.Performance is not { } performance)
            return new(new CandidateUtilityComparisonResult(CandidateUtilityComparisonDisposition.NoWinner,
                scope.ReasonCode), null);
        return await evidence.CreateActiveReferenceProofAsync(policy, candidateKey, managedFileName,
            quality.Environment, performance.Environment, activeSource, fence, cancellationToken).ConfigureAwait(false);
    }

    public string? CheckLicenseEvidence(string candidateKey, string managedFileName, CandidateProofSide? evaluatedSide) =>
        evidence.CheckActivationLicenseEvidence(candidateKey, managedFileName, evaluatedSide);

    public async Task<IDisposable?> CaptureLicenseEvidenceReadLeaseAsync(string candidateKey, string managedFileName,
        CandidateProofSide? evaluatedSide, CancellationToken cancellationToken) =>
        await evidence.CaptureActivationLicenseEvidenceReadLeaseAsync(candidateKey, managedFileName,
            evaluatedSide, cancellationToken).ConfigureAwait(false);

    public bool IsSnapshotCurrent(CandidateUtilityComparisonProof proof) =>
        evidence.IsProofSnapshotCurrent(proof, policies.Load().Policy);

    public bool? IsSideCurrent(CandidateProofSide side) => evidence.IsProofSideCurrent(side);

    public bool IsPolicyCurrent(CandidateUtilityComparisonProof proof) =>
        policies.Load().Policy is { } policy && CandidateUtilityProofDigest.Policy(policy) == proof.PolicySha256;
}

/// <summary>Reconciles a durable Pending (a crash mid-transition) once at startup, off the startup path.
/// It never promotes anything and never reloads during Gaming or shutdown.</summary>
internal sealed class RecommendationReconcileHostedService(CandidateRecommendationService service) : IHostedService
{
    private const int MaximumAttempts = 5;
    private const int RetryDelayBaseMilliseconds = 100;
    private const int RetryDelayCapMilliseconds = 800;
    private CancellationTokenSource? _cts;
    private Task? _run;
    internal Task? Completion => _run;

    internal static TimeSpan RetryDelayForAttempt(int completedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(completedAttempts, 1);
        int exponent = Math.Min(completedAttempts - 1, 3);
        return TimeSpan.FromMilliseconds(Math.Min(RetryDelayBaseMilliseconds * (1 << exponent),
            RetryDelayCapMilliseconds));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _run = Task.Run(() => ReconcileWithRetryAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private static void LogWarning(string message) => System.Diagnostics.Trace.TraceWarning(message);

    private async Task ReconcileWithRetryAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested) return;
            string retryReason;
            try
            {
                RecommendationTransitionResult result = await service.ReconcileAsync(cancellationToken).ConfigureAwait(false);
                if (!result.IsRetryLater) return;
                retryReason = result.Reason;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (IOException ex) { retryReason = ex.GetType().Name; }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidDataException
                or UnauthorizedAccessException or InvalidOperationException)
            {
                LogWarning("Recommendation reconcile did not complete: " + ex.GetType().Name);
                return;
            }

            if (attempt == MaximumAttempts)
            {
                LogWarning($"Recommendation reconcile retry limit reached after {attempt} attempts: {retryReason}");
                return;
            }

            try { await Task.Delay(RetryDelayForAttempt(attempt), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_run is not null) await Task.WhenAny(_run, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)).ConfigureAwait(false);
    }
}
