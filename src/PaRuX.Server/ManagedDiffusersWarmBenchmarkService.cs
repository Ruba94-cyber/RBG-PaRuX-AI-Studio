using System.Diagnostics;
using System.Runtime.ExceptionServices;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Measures the real per-request cold Diffusers path against a scoped resident worker on one exact
/// challenge. The service owns an exclusive media lease for the measured interval and restores the
/// default text resident only after that lease has been released.
/// </summary>
public sealed class ManagedDiffusersWarmBenchmarkService
{
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(8);

    private readonly LocalRuntimeService _runtime;
    private readonly LocalAiProcessManager _process;

    public ManagedDiffusersWarmBenchmarkService(LocalRuntimeService runtime, LocalAiProcessManager process)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _process = process ?? throw new ArgumentNullException(nameof(process));
    }

    public async Task<ManagedDiffusersWarmBenchmarkResult> RunAsync(
        ManagedDiffusersWarmBenchmarkRequest request,
        CancellationToken cancellationToken)
    {
        ManagedDiffusersWarmBenchmarkPolicy.NormalizedDiffusersBenchmark normalized =
            ManagedDiffusersWarmBenchmarkPolicy.Normalize(request);
        var diffusers = new ManagedDiffusersImageEngine(_runtime);
        ManagedMediaEngineStatus status = diffusers.GetStatus();
        if (!status.Ready)
            throw new InvalidOperationException(status.Detail ?? "The managed Diffusers engine is not ready.");
        if (_runtime.GamingMode)
            throw new InvalidOperationException("Gaming Mode is active, so the Diffusers cold/warm benchmark is unavailable.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _runtime.GamingCancellationToken);
        ManagedDiffusersWarmBenchmarkResult? result = null;
        bool establishedMediaBoundary = false;
        Exception? operationFailure = null;

        try
        {
            await using (MediaExecutionCoordinator.Lease mediaLease = await MediaExecutionCoordinator.Current
                .AcquireAsync("benchmark:diffusers-cold-warm", linked.Token).ConfigureAwait(false))
            {
                if (_runtime.GamingMode)
                    throw new InvalidOperationException("Gaming Mode became active while the Diffusers benchmark was queued.");

                await _runtime.PrepareForExclusiveMediaAsync(linked.Token).ConfigureAwait(false);
                bool mediaStopped = _process.StopMedia();
                bool textStopped = _process.StopText();
                if (!mediaStopped || !textStopped)
                    throw new InvalidOperationException("Could not establish an empty GPU residency before the Diffusers benchmark.");
                establishedMediaBoundary = true;

                MediaJobRequest challenge = normalized.Challenge;
                var coldClock = Stopwatch.StartNew();
                MediaAsset coldAsset = await diffusers.GenerateAsync(challenge, null, linked.Token).ConfigureAwait(false);
                coldClock.Stop();
                // The cold artifact is not part of the response. Drop its large in-memory payload before
                // loading the resident pipeline; the file remains available through the normal media path.
                coldAsset.Base64Data = string.Empty;

                await using ManagedDiffusersResidentSession resident = await ManagedDiffusersResidentSession
                    .StartAsync(_runtime, linked.Token).ConfigureAwait(false);

                result = new ManagedDiffusersWarmBenchmarkResult
                {
                    EngineId = ManagedDiffusersImageEngine.EngineId,
                    EngineVersion = ManagedDiffusersImageEngine.EngineVersion,
                    ModelId = ManagedDiffusersImageEngine.ModelId,
                    ModelRevision = ManagedDiffusersImageEngine.ModelRevision,
                    Seed = challenge.Seed,
                    Width = challenge.Width,
                    Height = challenge.Height,
                    Steps = challenge.Steps,
                    ColdTotalMilliseconds = coldClock.ElapsedMilliseconds,
                    ResidentLoadMilliseconds = resident.LoadMilliseconds,
                    ResidentLoadAllocatedBytes = resident.LoadAllocatedBytes,
                    ResidentLoadReservedBytes = resident.LoadReservedBytes
                };

                for (int run = 1; run <= normalized.WarmRuns; run++)
                {
                    var warmClock = Stopwatch.StartNew();
                    ManagedDiffusersResidentSession.ResidentRenderResult measurement = await resident
                        .GenerateAsync(challenge, linked.Token).ConfigureAwait(false);
                    warmClock.Stop();
                    result.WarmRuns.Add(new ManagedMediaWarmRunMeasurement
                    {
                        Run = run,
                        TotalMilliseconds = warmClock.ElapsedMilliseconds,
                        WorkerGenerationMilliseconds = measurement.WorkerGenerationMilliseconds,
                        PeakAllocatedBytes = measurement.PeakAllocatedBytes,
                        PeakReservedBytes = measurement.PeakReservedBytes
                    });

                    if (run == normalized.WarmRuns)
                    {
                        result.FinalArtifact = measurement.Asset;
                        if (!normalized.IncludeFinalArtifact)
                            result.FinalArtifact.Base64Data = string.Empty;
                    }
                    else
                    {
                        measurement.Asset.Base64Data = string.Empty;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            operationFailure = ex;
        }

        // Cleanup is independent of the caller's cancelled token. A normal STOP/cancel should leave
        // Chat usable again; Gaming Mode is the exception and deliberately keeps every AI resident off.
        Exception? restoreFailure = null;
        if (establishedMediaBoundary && !_runtime.GamingMode)
        {
            try
            {
                using var restoreCts = new CancellationTokenSource(RestoreTimeout);
                LocalRuntimeService.RuntimeResult restored = await _runtime
                    .RestoreDefaultTextExclusivelyAsync(restoreCts.Token).ConfigureAwait(false);
                if (!restored.Success)
                    throw new InvalidOperationException("Default text runtime restore failed: " + restored.Message);
            }
            catch (Exception ex)
            {
                restoreFailure = ex;
            }
        }

        if (operationFailure != null)
        {
            if (restoreFailure != null)
                operationFailure.Data["PaRuXTextRestoreFailure"] = restoreFailure.Message;
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }
        if (restoreFailure != null)
            throw new InvalidOperationException(
                "Diffusers benchmark completed, but the default text runtime could not be restored: " + restoreFailure.Message,
                restoreFailure);

        return result ?? throw new InvalidOperationException("Diffusers benchmark finished without a result.");
    }
}