using PaRuX.Contracts;

namespace PaRuX.Server;

public static class ManagedMediaV2Endpoints
{
    public static void MapManagedMediaV2Endpoints(this WebApplication app)
    {
        app.MapVisualContextV2Endpoints();
        app.MapObservabilityV2Endpoints();
        app.MapOpenAiVideoCompatibilityEndpoints();
        app.MapContinuousSpeechV2Endpoints();
        app.MapTrainingRuntimeV2Endpoints();

        app.MapGet("/api/v2/media/engines", (LocalRuntimeService runtime) =>
            Results.Ok(new MediaEngineCatalogService().GetCatalog(runtime)));

        app.MapGet("/api/v2/media/engines/ref2va", () =>
            Results.Ok(Ref2VaManagedExecutor.GetStatus()));

        app.MapPost("/api/v2/media/engines/ref2va/install", async (
            LocalRuntimeService runtime,
            LocalAiProcessManager processManager,
            CancellationToken ct) =>
        {
            try
            {
                var executor = new Ref2VaManagedExecutor(runtime, processManager);
                Ref2VaManagedStatus status = await executor.InstallSourceAsync(ct).ConfigureAwait(false);
                return Results.Ok(status);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
                or IOException or UnauthorizedAccessException or TimeoutException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireDesktopOwner();

        app.MapPost("/api/v2/media/engines/ref2va/generate", async (
            Ref2VaGenerationRequest request,
            LocalRuntimeService runtime,
            LocalAiProcessManager processManager,
            CancellationToken ct) =>
        {
            try
            {
                if (runtime.GamingMode)
                    return Results.Conflict(new { error = "Gaming Mode is active, so Ref2VA generation is unavailable." });

                var executor = new Ref2VaManagedExecutor(runtime, processManager);
                Ref2VaManagedStatus status = Ref2VaManagedExecutor.GetStatus();
                if (!status.Callable)
                    return Results.Conflict(new { error = status.Reason, engine = status });

                Ref2VaGenerationResult result = await executor.GenerateAsync(request, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (OperationCanceledException) when (runtime.GamingMode)
            {
                return Results.Conflict(new { error = "Ref2VA generation was cancelled because Gaming Mode became active." });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
                or IOException or UnauthorizedAccessException or TimeoutException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireDesktopOwner();

        app.MapGet("/api/v2/media/engines/diffusers", (LocalRuntimeService runtime) =>
            Results.Ok(new ManagedDiffusersImageEngine(runtime).GetStatus()));

        app.MapGet("/api/v2/media/engine-race/judge", () =>
            Results.Ok(new ManagedClipImageJudge().GetStatus()));

        app.MapPost("/api/v2/media/engines/diffusers/install", async (
            LocalRuntimeService runtime, CancellationToken ct) =>
        {
            try
            {
                ManagedMediaEngineInstallResult result = await new ManagedDiffusersImageEngine(runtime)
                    .InstallAsync(null, ct).ConfigureAwait(false);

                await using var semanticJudge = new ManagedClipImageJudge();
                string judgeValidation = await semanticJudge.InstallAsync(null, ct).ConfigureAwait(false);
                result.Validation = string.IsNullOrWhiteSpace(result.Validation)
                    ? "semanticJudge=" + judgeValidation
                    : result.Validation + "; semanticJudge=" + judgeValidation;
                return Results.Ok(result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
                or IOException or UnauthorizedAccessException or TimeoutException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireDesktopOwner();

        app.MapPost("/api/v2/media/engines/diffusers/benchmark/warm", async (
            ManagedDiffusersWarmBenchmarkRequest request,
            LocalRuntimeService runtime,
            LocalAiProcessManager processManager,
            CancellationToken ct) =>
        {
            try
            {
                ArgumentNullException.ThrowIfNull(request);
                ManagedMediaEngineStatus status = new ManagedDiffusersImageEngine(runtime).GetStatus();
                if (!status.Ready)
                    return Results.Conflict(new
                    {
                        error = "The PaRuX-managed Diffusers engine is not ready. Install it before measuring cold/resident-warm behavior.",
                        engine = status
                    });
                if (runtime.GamingMode)
                    return Results.Conflict(new { error = "Gaming Mode is active, so the Diffusers cold/warm benchmark is unavailable." });

                ManagedDiffusersWarmBenchmarkResult result = await new ManagedDiffusersWarmBenchmarkService(runtime, processManager)
                    .RunAsync(request, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (OperationCanceledException) when (runtime.GamingMode)
            {
                return Results.Conflict(new { error = "Diffusers cold/warm benchmark was cancelled because Gaming Mode became active." });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
                or IOException or TimeoutException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireDesktopOwner();

        app.MapPost("/api/v2/media/engine-race/image", async (
            ImageEngineRaceRequest request,
            bool? includeArtifact,
            MediaGenerationService generation,
            LocalRuntimeService runtime,
            LocalAiProcessManager processManager,
            CancellationToken ct) =>
        {
            try
            {
                ArgumentNullException.ThrowIfNull(request);
                if (runtime.GamingMode)
                    return Results.Conflict(new { error = "Gaming Mode is active, so media Engine Race is unavailable." });

                request.Job ??= new MediaJobRequest { Type = "image" };
                request.Job.Type = "image";
                var diffusers = new ManagedDiffusersImageEngine(runtime);
                ManagedMediaEngineStatus status = diffusers.GetStatus();
                if (!status.Ready)
                    return Results.Conflict(new
                    {
                        error = "The independent PaRuX Diffusers engine is not ready. Install it before starting a real Engine Race.",
                        engine = status
                    });

                await using var semanticJudge = new ManagedClipImageJudge();
                ManagedClipJudgeStatus judgeStatus = semanticJudge.GetStatus();
                if (!judgeStatus.Ready)
                    return Results.Conflict(new
                    {
                        error = "The pinned local semantic judge is not ready. Run the managed Diffusers install/repair endpoint before production Engine Race.",
                        engine = status,
                        judge = judgeStatus
                    });

                using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, runtime.GamingCancellationToken);
                await using MediaExecutionCoordinator.Lease mediaLease = await MediaExecutionCoordinator.Current
                    .AcquireAsync("engine-race:image", raceCts.Token).ConfigureAwait(false);
                if (runtime.GamingMode)
                    return Results.Conflict(new { error = "Gaming Mode became active while Engine Race was queued." });

                var comfyRace = new ManagedComfyImageRaceEngine(processManager, runtime);
                MediaEngineRaceResult race = await generation
                    .RunManagedImageRaceAsync(comfyRace, diffusers, semanticJudge, request.Job, raceCts.Token)
                    .ConfigureAwait(false);

                StripPayloads(race, includeArtifact ?? true);
                return Results.Ok(race);
            }
            catch (OperationCanceledException) when (runtime.GamingMode)
            {
                return Results.Conflict(new { error = "Media Engine Race was cancelled because Gaming Mode became active." });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
                or IOException or TimeoutException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireDesktopOwner();
    }

    internal static void StripPayloads(MediaEngineRaceResult race, bool includeWinnerArtifact)
    {
        ArgumentNullException.ThrowIfNull(race);
        foreach (MediaEngineRaceMeasurement measurement in race.Measurements)
            measurement.Asset = null;
        if (!includeWinnerArtifact)
            race.WinnerAsset = null;
    }
}
