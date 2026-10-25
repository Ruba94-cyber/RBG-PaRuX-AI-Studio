using System.Net;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Runtime/hardware transport shim. Besides enriching GET /api/v1/runtime/status with DXGI GPU
/// inventory, this hook exposes PaRuX's desktop-only inference Performance Engine without
/// duplicating the large Program.cs bootstrap.
/// </summary>
public static class RuntimeHardwareEndpointExtensions
{
    public static RouteHandlerBuilder MapGet(
        this WebApplication app,
        string pattern,
        Func<RuntimeManagerService, CancellationToken, Task<IResult>> legacyHandler)
    {
        if (!string.Equals(pattern, "/api/v1/runtime/status", StringComparison.Ordinal))
            return app.MapMethods(pattern, new[] { HttpMethods.Get }, legacyHandler);

        LocalAiProcessManager process = app.Services.GetRequiredService<LocalAiProcessManager>();
        RuntimeInstallService runtimeInstall = app.Services.GetRequiredService<RuntimeInstallService>();
        ModelDownloadService modelDownloads = app.Services.GetRequiredService<ModelDownloadService>();
        EmbeddingService embeddings = app.Services.GetRequiredService<EmbeddingService>();
        RerankerService reranker = app.Services.GetRequiredService<RerankerService>();
        RagStore rag = app.Services.GetRequiredService<RagStore>();
        var gpuInventory = new GpuInventoryService();
        var telemetry = new GpuPerformanceTelemetryService();
        var profileStore = new RuntimePerformanceProfileStore();
        var policy = new RuntimePerformancePolicyStore();
        var regressionEvidence = new RuntimeRegressionEvidenceStore();
        var tuner = new RuntimePerformanceTuner(
            process,
            gpuInventory,
            new CoordinatedLlamaPerformanceProbe(new LlamaPerformanceProbe(process)),
            profileStore);
        var serverProbe = new LlamaServerPerformanceProbe(process, telemetry);
        var optimizer = new RuntimeOptimizationService(
            tuner,
            serverProbe,
            profileStore,
            policy,
            process,
            telemetry);
        // The manual button and the idle trigger share this one pipeline; both feed the same calibration ledger and benchmark history.
        if (app.Services.GetService<RuntimeOptimizerLink>() is { } optimizerLink
            && app.Services.GetService<IdleCalibrationService>() is { } idleCalibration)
        {
            optimizerLink.Optimizer = optimizer;
            LocalBenchmarkHistoryStore benchmarkHistory = app.Services.GetRequiredService<LocalBenchmarkHistoryStore>();
            optimizer.Completed = (run, completion) =>
                idleCalibration.OnOptimizationCompleted(run, completion, benchmarkHistory, IdleCalibrationComposition.HardwareDigest(app.Services));
        }
        var buildRace = new RuntimeBuildRaceService(process);
        var backendRace = new RuntimeBackendRaceService(process, serverProbe);
        var engineRegistry = new LocalInferenceEngineRegistry();
        var updateStore = new ManagedAiUpdateStore();
        var exLlamaPacks = new ExLlamaV3ManagedPackService();
        var engineRace = new ManagedRuntimeEngineRaceService(
            process, serverProbe, modelDownloads, engineRegistry, exLlamaPacks);
        var exLlamaJobs = new ExLlamaV3InstallJobService(exLlamaPacks, updateStore);
        var modelUpdateJobs = new ManagedModelUpdateInstallJobService(updateStore);
        var retrievalPromotion = new RetrievalProviderPromotionStore();
        var retrievalResolver = new ManagedRetrievalProviderResolver(
            embeddings, reranker, modelDownloads, process, retrievalPromotion);
        var retrievalProviderEval = new RetrievalEmbeddingProviderEvalOrchestrator(
            rag, embeddings, modelDownloads, process, retrievalPromotion);
        var retrievalRerankerEval = new RetrievalRerankerProviderEvalOrchestrator(
            rag, embeddings, reranker, modelDownloads, process, retrievalPromotion);
        rag.ConfigureManagedProviderResolver(retrievalResolver);

        app.Lifetime.ApplicationStopping.Register(exLlamaJobs.Dispose);
        app.Lifetime.ApplicationStopping.Register(modelUpdateJobs.Dispose);
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try { retrievalResolver.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        });
        MapPerformanceEndpoints(
            app, tuner, optimizer, buildRace, backendRace, engineRace, telemetry, profileStore, policy,
            regressionEvidence, modelDownloads, engineRegistry);
        MapBestInClassEndpoints(
            app, engineRegistry, runtimeInstall, modelDownloads, updateStore, exLlamaPacks, exLlamaJobs, modelUpdateJobs);
        MapRetrievalPerformanceEndpoints(
            app, rag, modelDownloads, retrievalPromotion, retrievalProviderEval, retrievalRerankerEval);

        return app.MapMethods(pattern, new[] { HttpMethods.Get }, async (
            RuntimeManagerService runtimeManager,
            CancellationToken cancellationToken) =>
        {
            RuntimeManagerStatus status = await runtimeManager.GetStatusAsync(cancellationToken);
            GpuInventoryStatus gpu = gpuInventory.GetStatus();
            RuntimeHardwareProfile accelerator = RuntimeHardwareProfileResolver.Resolve(status, gpu);

            RuntimeManagerHardwareStatus shaped = FromStatus(status);
            return Results.Ok(new RuntimeManagerHardwareStatus
            {
                Backends = shaped.Backends,
                CurrentManagedProviderEndpoint = shaped.CurrentManagedProviderEndpoint,
                GpuDetected = shaped.GpuDetected,
                VramTotalMib = shaped.VramTotalMib,
                VramFreeMib = shaped.VramFreeMib,
                RamTotalBytes = shaped.RamTotalBytes,
                GamingActive = shaped.GamingActive,
                GamingShutdownPending = shaped.GamingShutdownPending,
                LastGamingShutdownFailure = shaped.LastGamingShutdownFailure,
                ActiveRole = shaped.ActiveRole,
                ActiveProfile = shaped.ActiveProfile,
                ActiveModelPath = shaped.ActiveModelPath,
                ActiveVisionProjectorPath = shaped.ActiveVisionProjectorPath,
                ActiveHasVisionSupport = shaped.ActiveHasVisionSupport,
                LaunchPlanExplanation = shaped.LaunchPlanExplanation,
                TextLaunchTrace = shaped.TextLaunchTrace,
                GpuAdapters = gpu.Adapters,
                GpuDedicatedVideoMemoryTotalBytes = gpu.TotalDedicatedVideoMemoryBytes,
                GpuDedicatedSystemMemoryTotalBytes = gpu.TotalDedicatedSystemMemoryBytes,
                GpuSharedSystemMemoryTotalBytes = gpu.SharedSystemMemoryTotalBytes,
                GpuLargestDedicatedVideoMemoryBytes = gpu.LargestDedicatedVideoMemoryBytes,
                GpuInventoryAvailable = gpu.Available,
                GpuInventorySource = gpu.Source,
                GpuInventoryError = gpu.Error,
                AcceleratorProfile = new RuntimeAcceleratorProfile
                {
                    GpuPresent = accelerator.GpuPresent,
                    AcceleratorEligible = accelerator.AcceleratorEligible,
                    Backend = accelerator.Backend,
                    Vendor = accelerator.Vendor,
                    AdapterName = accelerator.AdapterName,
                    DedicatedVideoMemoryBytes = accelerator.DedicatedVideoMemoryBytes,
                    SharedSystemMemoryLimitBytes = accelerator.SharedSystemMemoryLimitBytes,
                    SystemRamBytes = accelerator.SystemRamBytes,
                    UsableSystemRamBytes = accelerator.UsableSystemRamBytes,
                    SharedMemoryPrimary = accelerator.SharedMemoryPrimary,
                    InventorySource = accelerator.InventorySource
                }
            });
        });
    }

    /// <summary>Copies every original <see cref="RuntimeManagerStatus"/> field to the root of the hardware
    /// shape. The endpoint promises that all original fields stay at the root; the managed provider endpoint,
    /// free VRAM, gaming-shutdown state and vision projector were once left out, so clients that need the
    /// endpoint (the coding agent's context-window probe) always saw it as unknown.</summary>
    public static RuntimeManagerHardwareStatus FromStatus(RuntimeManagerStatus status) => new()
    {
        Backends = status.Backends,
        CurrentManagedProviderEndpoint = status.CurrentManagedProviderEndpoint,
        GpuDetected = status.GpuDetected,
        VramTotalMib = status.VramTotalMib,
        VramFreeMib = status.VramFreeMib,
        RamTotalBytes = status.RamTotalBytes,
        GamingActive = status.GamingActive,
        GamingShutdownPending = status.GamingShutdownPending,
        LastGamingShutdownFailure = status.LastGamingShutdownFailure,
        ActiveRole = status.ActiveRole,
        ActiveProfile = status.ActiveProfile,
        ActiveModelPath = status.ActiveModelPath,
        ActiveVisionProjectorPath = status.ActiveVisionProjectorPath,
        ActiveHasVisionSupport = status.ActiveHasVisionSupport,
        LaunchPlanExplanation = status.LaunchPlanExplanation,
        TextLaunchTrace = status.TextLaunchTrace
    };

    private static void MapPerformanceEndpoints(
        WebApplication app,
        RuntimePerformanceTuner tuner,
        RuntimeOptimizationService optimizer,
        RuntimeBuildRaceService buildRace,
        RuntimeBackendRaceService backendRace,
        ManagedRuntimeEngineRaceService engineRace,
        GpuPerformanceTelemetryService telemetry,
        RuntimePerformanceProfileStore profiles,
        RuntimePerformancePolicyStore policy,
        RuntimeRegressionEvidenceStore regressionEvidence,
        ModelDownloadService models,
        LocalInferenceEngineRegistry engines)
    {
        app.MapPost("/api/v1/runtime/performance/tune", (
            RuntimePerformanceTuneRequest request, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try
            {
                RuntimePerformanceTuneRun run = tuner.Start(request);
                return Results.Accepted("/api/v1/runtime/performance/tune/" + run.Id, run);
            }
            catch (Exception ex) when (IsExpected(ex)) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v1/runtime/performance/tune/{id}", (string id, HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            RuntimePerformanceTuneRun? run = tuner.Get(id);
            return run == null ? Results.NotFound(new { error = "Tuning run not found." }) : Results.Ok(run);
        });

        app.MapPost("/api/v1/runtime/performance/tune/{id}/cancel", (string id, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return tuner.Cancel(id) ? Results.Accepted() : Results.NotFound(new { error = "Active tuning run not found." });
        });

        app.MapPost("/api/v1/runtime/performance/optimize", (
            RuntimeOptimizationRequest request, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try
            {
                RuntimeOptimizationRun run = optimizer.Start(request);
                return Results.Accepted("/api/v1/runtime/performance/optimize/" + run.Id, run);
            }
            catch (Exception ex) when (IsExpected(ex)) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v1/runtime/performance/optimize/{id}", (string id, HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            RuntimeOptimizationRun? run = optimizer.Get(id);
            return run == null ? Results.NotFound(new { error = "Optimization run not found." }) : Results.Ok(run);
        });

        app.MapPost("/api/v1/runtime/performance/optimize/{id}/cancel", (string id, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return optimizer.Cancel(id) ? Results.Accepted() : Results.NotFound(new { error = "Active optimization run not found." });
        });

        app.MapPost("/api/v1/runtime/performance/build-race", (
            RuntimeBuildRaceRequest request, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try
            {
                RuntimeBuildRaceStatusDto run = buildRace.Start(request);
                return Results.Accepted("/api/v1/runtime/performance/build-race/" + run.Id, run);
            }
            catch (Exception ex) when (IsExpected(ex)) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v1/runtime/performance/build-race/{id}", (string id, HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            RuntimeBuildRaceStatusDto? run = buildRace.Get(id);
            return run == null ? Results.NotFound(new { error = "Runtime race not found." }) : Results.Ok(run);
        });

        app.MapPost("/api/v1/runtime/performance/build-race/{id}/cancel", (string id, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return buildRace.Cancel(id) ? Results.Accepted() : Results.NotFound(new { error = "Active runtime race not found." });
        });

        app.MapPost("/api/v1/runtime/performance/backend-race", async (
            RuntimeBackendRaceRequest request,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try
            {
                await using IAsyncDisposable lane = await RuntimePerformanceCoordinator.Shared
                    .AcquireAsync(ct).ConfigureAwait(false);
                return Results.Ok(await backendRace.RunAsync(request, ct));
            }
            catch (Exception ex) when (IsExpected(ex)) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/v1/runtime/performance/engine-race", async (
            RuntimeEngineRaceRequest request,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try
            {
                // RuntimeEngineRaceService owns the process-wide performance lane internally.
                // Acquiring it here as well would deadlock the request.
                return Results.Ok(await engineRace.RunAsync(request, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (IsExpected(ex) || ex is ArgumentException or TimeoutException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/v1/runtime/performance/engine-races", (
            int? maximum,
            HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(engineRace.Evidence.List(Math.Clamp(maximum ?? 50, 1, 200)));
        });

        app.MapGet("/api/v1/runtime/performance/engine-race/variants", (HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(new MultiFormatModelCatalog().List(models.ListManagedModels(), engines));
        });

        app.MapGet("/api/v1/runtime/performance/profiles", (HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(profiles.List());
        });

        app.MapGet("/api/v1/runtime/performance/regressions", (HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(regressionEvidence.List());
        });

        app.MapGet("/api/v1/runtime/performance/status", async (
            HttpContext context,
            RuntimeManagerService runtimeManager,
            CancellationToken ct) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            RuntimeManagerStatus runtime = await runtimeManager.GetStatusAsync(ct);
            List<GpuPerformanceTelemetryDto> gpus = await telemetry.QueryAsync(ct);
            string backend = runtime.GpuDetected ? "text-cuda" : "text-vulkan";
            string tag = LocalAiPaths.GetActiveRuntimeBuildTag(backend) ?? LocalAiPaths.DefaultRuntimeBuildTag(backend);
            return Results.Ok(new RuntimePerformanceStatusDto
            {
                Mode = policy.GetMode(),
                Profiles = profiles.List().ToList(),
                Gpus = gpus,
                ActiveRuntimeTag = tag,
                ActiveModelPath = runtime.ActiveModelPath
            });
        });

        app.MapPost("/api/v1/runtime/performance/mode", (SetRuntimePerformanceModeRequest request, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try { return Results.Ok(new { mode = policy.SetMode(request.Mode) }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v1/runtime/performance/topology", async (HttpContext context, CancellationToken ct) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            string? topology = await telemetry.GetTopologyTextAsync(ct);
            return Results.Ok(new { available = topology != null, topology });
        });
    }

    private static void MapRetrievalPerformanceEndpoints(
        WebApplication app,
        RagStore rag,
        ModelDownloadService models,
        RetrievalProviderPromotionStore promotion,
        RetrievalEmbeddingProviderEvalOrchestrator providerEval,
        RetrievalRerankerProviderEvalOrchestrator rerankerEval)
    {
        app.MapGet("/api/v1/rag/performance/configured", (HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            List<ManagedModelInfo> installed = models.ListManagedModels().ToList();
            HashSet<string> embeddingRepos = Qwen3RetrievalCandidateCatalog.Embeddings
                .Select(candidate => candidate.RepoId).ToHashSet(StringComparer.Ordinal);
            HashSet<string> rerankerRepos = Qwen3RetrievalCandidateCatalog.Rerankers
                .Where(candidate => candidate.Format == ManagedModelFormat.Gguf)
                .Select(candidate => candidate.RepoId).ToHashSet(StringComparer.Ordinal);
            return Results.Ok(new
            {
                promotion = promotion.Get(),
                embeddingCandidates = installed.Where(model => model.RepoId != null && embeddingRepos.Contains(model.RepoId)),
                rerankerCandidates = installed.Where(model => model.RepoId != null && rerankerRepos.Contains(model.RepoId)),
                qwenEmbeddingCatalog = Qwen3RetrievalCandidateCatalog.Embeddings,
                qwenRerankerCatalog = Qwen3RetrievalCandidateCatalog.Rerankers
            });
        });

        app.MapGet("/api/v1/rag/performance/evaluation-documents", (
            int? maxChunks,
            HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            int limit = Math.Clamp(maxChunks ?? 25_000, 16, 50_000);
            RagEvaluationCorpus corpus = rag.GetEvaluationCorpus(limit);
            List<RagEvaluationDocumentDto> documents = corpus.Chunks
                .GroupBy(chunk => new { chunk.DocumentId, chunk.FilePath })
                .Select(group => new RagEvaluationDocumentDto
                {
                    DocumentId = group.Key.DocumentId,
                    FileName = Path.GetFileName(group.Key.FilePath),
                    ChunkCount = group.Count()
                })
                .OrderBy(document => document.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(document => document.DocumentId, StringComparer.Ordinal)
                .ToList();
            return Results.Ok(new
            {
                totalEnabledChunks = corpus.TotalEnabledChunks,
                returnedChunks = corpus.Chunks.Count,
                truncated = corpus.Truncated,
                documents
            });
        });

        app.MapPost("/api/v1/rag/performance/provider-race/embedding", async (
            RagEmbeddingProviderEvalRequest request,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try
            {
                await using IAsyncDisposable lane = await RuntimePerformanceCoordinator.Shared
                    .AcquireAsync(ct).ConfigureAwait(false);
                return Results.Ok(await providerEval.RunAsync(request, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (IsExpected(ex) || ex is ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/v1/rag/performance/provider-race/reranker", async (
            RagRerankerProviderEvalRequest request,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try
            {
                await using IAsyncDisposable lane = await RuntimePerformanceCoordinator.Shared
                    .AcquireAsync(ct).ConfigureAwait(false);
                return Results.Ok(await rerankerEval.RunAsync(request, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (IsExpected(ex) || ex is ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/v1/rag/performance/index-race", async (
            RagVectorRaceRequest request,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try
            {
                int topK = Math.Clamp(request.TopK, 1, 100);
                int maxQueries = Math.Clamp(request.MaxQueries, 8, 256);
                double recall = Math.Clamp(request.MinimumRecallAtK, .80, 1.0);
                double speedup = Math.Clamp(request.MinimumSpeedup, 1.0, 100.0);
                return Results.Ok(await rag.RaceVectorAccelerationAsync(
                    topK, maxQueries, recall, speedup, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (IsExpected(ex)) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/v1/rag/performance/providers/reset", (
            RetrievalProviderResetRequest request,
            HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            string kind = (request.Kind ?? string.Empty).Trim().ToLowerInvariant();
            if (kind is not ("embedding" or "reranker" or "all"))
                return Results.BadRequest(new { error = "kind must be embedding, reranker, or all." });
            if (kind is "embedding" or "all") promotion.ResetEmbedding();
            if (kind is "reranker" or "all") promotion.ResetReranker();
            rag.InvalidateManagedProviderState();
            return Results.Ok(promotion.Get());
        });
    }

    private static void MapBestInClassEndpoints(
        WebApplication app,
        LocalInferenceEngineRegistry engines,
        RuntimeInstallService runtimeInstall,
        ModelDownloadService modelDownloads,
        ManagedAiUpdateStore updates,
        ExLlamaV3ManagedPackService exLlamaPacks,
        ExLlamaV3InstallJobService exLlamaJobs,
        ManagedModelUpdateInstallJobService modelUpdateJobs)
    {
        app.MapGet("/api/v1/runtime/engines", (HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(engines.ListDescriptors());
        });

        app.MapGet("/api/v1/runtime/engines/compatible", (
            string format,
            string hardware,
            bool? integratedOnly,
            HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!Enum.TryParse(format, ignoreCase: true, out ManagedModelFormat parsed) || parsed == ManagedModelFormat.Unknown)
                return Results.BadRequest(new { error = "Unknown managed model format." });
            return Results.Ok(engines.CompatibleDescriptors(parsed, hardware, integratedOnly ?? false));
        });

        app.MapGet("/api/v1/runtime/engines/exllamav3/available", async (HttpContext context, CancellationToken ct) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(await exLlamaPacks.ListAvailableAsync(ct)); }
            catch (Exception ex) when (IsExpected(ex) || ex is HttpRequestException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/v1/runtime/engines/exllamav3/installed", (HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(new
            {
                active = exLlamaPacks.GetActiveTag(),
                installed = exLlamaPacks.ListInstalledTags()
            });
        });

        app.MapPost("/api/v1/runtime/engines/exllamav3/install", (
            ExLlamaV3InstallRequest request,
            HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try
            {
                ExLlamaV3InstallJobStatus job = exLlamaJobs.Start(request.Tag);
                return Results.Accepted("/api/v1/runtime/engines/exllamav3/install/" + job.Id, job);
            }
            catch (Exception ex) when (IsExpected(ex)) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v1/runtime/engines/exllamav3/install/{id}", (string id, HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            ExLlamaV3InstallJobStatus? job = exLlamaJobs.Get(id);
            return job == null ? Results.NotFound(new { error = "ExLlamaV3 install job not found." }) : Results.Ok(job);
        });

        app.MapPost("/api/v1/runtime/engines/exllamav3/install/{id}/cancel", (string id, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            return exLlamaJobs.Cancel(id)
                ? Results.Accepted()
                : Results.NotFound(new { error = "Active ExLlamaV3 install job not found." });
        });

        app.MapGet("/api/v1/updates", (bool? includeDismissed, HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(updates.List(includeDismissed ?? false));
        });

        app.MapPost("/api/v1/updates/check", async (HttpContext context, CancellationToken ct) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            ManagedAiUpdateDiscoveryService discovery = BuildRuntimeUpdateDiscovery(
                runtimeInstall, modelDownloads, updates, exLlamaPacks);
            return Results.Ok(await discovery.CheckAllAsync(ct));
        });

        app.MapPost("/api/v1/updates/models/install", (
            ManagedModelUpdateInstallRequest request,
            HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try
            {
                ManagedModelUpdateInstallJobStatus job = modelUpdateJobs.Start(request.Asset);
                return Results.Accepted("/api/v1/updates/models/install/" + job.Id, job);
            }
            catch (Exception ex) when (IsExpected(ex)) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v1/updates/models/install/{id}", (string id, HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            ManagedModelUpdateInstallJobStatus? job = modelUpdateJobs.Get(id);
            return job == null ? Results.NotFound(new { error = "Managed model update job not found." }) : Results.Ok(job);
        });

        app.MapPost("/api/v1/updates/models/install/{id}/cancel", (string id, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            return modelUpdateJobs.Cancel(id)
                ? Results.Accepted()
                : Results.NotFound(new { error = "Active managed model update job not found." });
        });

        app.MapPost("/api/v1/updates/acknowledge", (ManagedAiAssetIdentity asset, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try
            {
                updates.Acknowledge(asset);
                return Results.Ok();
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        app.MapPost("/api/v1/updates/dismiss", (ManagedAiAssetIdentity asset, HttpContext context) =>
        {
            if (!IsDesktopOwner(context)) return DesktopOnly();
            try
            {
                return Results.Ok(updates.Transition(asset, ManagedAiUpdateState.Dismissed, "Dismissed by user."));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });
    }

    private static ManagedAiUpdateDiscoveryService BuildRuntimeUpdateDiscovery(
        RuntimeInstallService runtimeInstall,
        ModelDownloadService modelDownloads,
        ManagedAiUpdateStore updates,
        ExLlamaV3ManagedPackService exLlamaPacks)
    {
        var sources = new List<IManagedAiUpdateSource>
        {
            RuntimeUpdateSource(runtimeInstall, "text-cuda", ManagedAiAssetKind.RuntimeBuild, "llama.cpp CUDA"),
            RuntimeUpdateSource(runtimeInstall, "text-vulkan", ManagedAiAssetKind.RuntimeBuild, "llama.cpp Vulkan"),
            RuntimeUpdateSource(runtimeInstall, "media-comfyui", ManagedAiAssetKind.MediaEngine, "ComfyUI"),
            ExLlamaUpdateSource(exLlamaPacks)
        };
        sources.AddRange(HuggingFaceManagedModelUpdateSources.Build(modelDownloads));
        return new ManagedAiUpdateDiscoveryService(updates, sources);
    }

    private static IManagedAiUpdateSource RuntimeUpdateSource(
        RuntimeInstallService runtimeInstall,
        string backend,
        ManagedAiAssetKind kind,
        string displayName)
    {
        var asset = new ManagedAiAssetIdentity(kind, backend, displayName);
        return new DelegateManagedAiUpdateSource(asset, async ct =>
        {
            List<RuntimeBuildOption> available = await runtimeInstall.ListAvailableAsync(backend, ct).ConfigureAwait(false);
            List<RuntimeBuildOption> installed = runtimeInstall.ListInstalled(backend);
            RuntimeBuildOption? active = installed.FirstOrDefault(option => option.IsActive);
            RuntimeBuildOption? latest = available
                .OrderByDescending(option => option.Recommended)
                .ThenByDescending(option => option.PublishedUtc ?? DateTime.MinValue)
                .FirstOrDefault();

            if (latest == null)
                return new ManagedAiUpdateCheckResult(asset, false, active?.Tag, null, null, null, null);

            bool updateAvailable = !latest.InstalledLocally;
            string sourceUri = backend == "media-comfyui"
                ? "https://github.com/comfyanonymous/ComfyUI/releases"
                : "https://github.com/ggml-org/llama.cpp/releases";
            string version = string.IsNullOrWhiteSpace(latest.Variant) ? latest.Tag : latest.Tag + "/" + latest.Variant;
            string? installedVersion = active == null
                ? null
                : (string.IsNullOrWhiteSpace(active.Variant) ? active.Tag : active.Tag + "/" + active.Variant);

            return new ManagedAiUpdateCheckResult(
                asset,
                updateAvailable,
                installedVersion,
                version,
                latest.Tag,
                sourceUri,
                null,
                Message: updateAvailable ? "A newer PaRuX-managed runtime build is available." : null);
        });
    }

    private static IManagedAiUpdateSource ExLlamaUpdateSource(ExLlamaV3ManagedPackService packs)
    {
        var asset = new ManagedAiAssetIdentity(
            ManagedAiAssetKind.InferenceEngine,
            LocalInferenceEngineRegistry.ExLlamaV3Id,
            "ExLlamaV3");
        return new DelegateManagedAiUpdateSource(asset, async ct =>
        {
            IReadOnlyList<ExLlamaV3PackOption> available = await packs.ListAvailableAsync(ct).ConfigureAwait(false);
            ExLlamaV3PackOption? latest = available
                .OrderByDescending(option => option.PublishedUtc ?? DateTime.MinValue)
                .FirstOrDefault();
            if (latest == null)
                return new ManagedAiUpdateCheckResult(asset, false, packs.GetActiveTag(), null, null, null, null);

            bool updateAvailable = !latest.Installed;
            return new ManagedAiUpdateCheckResult(
                asset,
                updateAvailable,
                packs.GetActiveTag(),
                latest.Tag,
                latest.Tag,
                $"https://github.com/{ExLlamaV3BuildCatalog.Owner}/{ExLlamaV3BuildCatalog.Repo}/releases/tag/{latest.Tag}",
                null,
                Message: updateAvailable
                    ? "A newer verified Windows ExLlamaV3 engine pack is available. Installation remains inactive until Engine Race promotion."
                    : null);
        });
    }

    private static IResult DesktopOnly() => Results.Json(
        new { error = "This runtime control is desktop-only." },
        statusCode: StatusCodes.Status403Forbidden);

    private static bool IsExpected(Exception ex) =>
        ex is InvalidDataException or FileNotFoundException or InvalidOperationException;

    private static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    // Runtime controls (anything that starts, cancels, installs or changes a runtime) are desktop-owner authority. Loopback
    // alone is not: a browser session through a same-machine reverse proxy arrives from 127.0.0.1 too.
    private static bool IsDesktopOwner(HttpContext context) =>
        IsLoopback(context) && context.Items["PaRuXOwner"] as string == "desktop";
}
