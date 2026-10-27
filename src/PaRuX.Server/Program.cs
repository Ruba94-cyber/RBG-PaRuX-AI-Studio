using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Automation;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.Registry;
using PaRuX.Server.ProjectManager;
using PaRuX.Server.Autopilot;
using PaRuX.Server.EvalLab;
using PaRuX.Server.AutoLab;
using PaRuX.Server.Workflow;
using PaRuX.Server.CapabilityProfiler;
using PaRuX.Intelligence;

StartupTrace.Mark("main-entered");
PaRuX.Server.BundledBaselineRuntimeBootstrap.Initialize();
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
PaRuX.Server.Gateway.GatewaySettings gatewaySettings = PaRuX.Server.Gateway.GatewaySettings.Load(ServerState.StateDirectory);
LocalApiServerService.VendorModelDefault = gatewaySettings.VendorModelDefault;
// The gateway port is bound only when free: another program owning it must not stop PaRuX Core.
int? gatewayPort = gatewaySettings.Enabled && PaRuX.Server.Gateway.GatewaySettings.IsPortFree(gatewaySettings.Port)
    ? gatewaySettings.Port : null;
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 64L * 1024 * 1024;
    options.ListenLocalhost(51123);
    if (gatewayPort is int port) options.ListenLocalhost(port);
    RemoteAccessService.RemoteAccessConfiguration remote = RemoteAccessService.LoadConfiguration();
    if (remote.Enabled && RemoteAccessService.IsPrivateAssignedAddress(remote.Address))
    {
        options.Listen(IPAddress.Parse(remote.Address), remote.Port,
            listen => listen.UseHttps(RemoteAccessService.GetOrCreateCertificate()));
    }
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<ServerState>();
builder.Services.AddSingleton<LocalAiProcessManager>();
builder.Services.AddSingleton<ISystemRamSnapshotProvider, SystemRamSnapshotProvider>();
builder.Services.AddSingleton<IGpuPerformanceTelemetrySnapshotProvider>(
    CachedGpuPerformanceTelemetrySnapshotProvider.Shared);
builder.Services.AddSingleton<RuntimeManagerService>(services => new RuntimeManagerService(
    services.GetRequiredService<LocalAiProcessManager>(),
    services.GetRequiredService<ISystemRamSnapshotProvider>(),
    services.GetRequiredService<IGpuPerformanceTelemetrySnapshotProvider>()));
builder.Services.AddSingleton<RuntimeInstallService>();
builder.Services.AddSingleton<MediaSetupService>();
builder.Services.AddSingleton<ComponentInventoryService>();
builder.Services.AddSingleton<SmartRouterService>(services => new SmartRouterService(
    services.GetRequiredService<RuntimeManagerService>(),
    services.GetRequiredService<ModelBenchmarkService>(),
    services.GetRequiredService<PaRuX.Server.CapabilityProfiler.CapabilityProfilerStore>(),
    services.GetRequiredService<CandidateEvidenceService>(),
    new RecommendationRouteConsult(ServerState.StateDirectory,
        proof => services.GetRequiredService<IRecommendationProofSource>().IsPolicyCurrent(proof),
        () => services.GetRequiredService<LocalAiProcessManager>().GamingActive,
        currentIdentity: (proof, cancellationToken) => CandidateRecommendationEvaluationIdentity.CaptureCurrentAsync(
            proof, services.GetRequiredService<ICandidateLaunchFenceSource>(), cancellationToken))));
builder.Services.AddSingleton<PaRuX.Server.Orchestration.IRoleRouter>(sp => sp.GetRequiredService<SmartRouterService>());
builder.Services.AddSingleton<ExternalTextProviderService>();
builder.Services.AddSingleton<CloudFallbackService>();
builder.Services.AddSingleton<PaRuX.Server.DesktopIntegration.DesktopIntegrationService>();
builder.Services.AddSingleton<PaRuX.Server.DesktopIntegration.DocumentWriteService>();
builder.Services.AddSingleton<CustomWorkflowService>();
builder.Services.AddSingleton<ComfyWorkflowRunner>();
builder.Services.AddSingleton<ComfyWorkflowImportService>();
builder.Services.AddSingleton<LocalRuntimeService>();
builder.Services.AddSingleton<ContextEngineService>();
builder.Services.AddSingleton<IModalityChatBackend, ModalityChatBackend>();
builder.Services.AddSingleton<ChatProxyService>();
builder.Services.AddSingleton<MediaGenerationService>();
// Media job intent and status survive a Core restart in the local state folder (see MediaJobService.RecoverAsync).
builder.Services.AddSingleton<MediaJobStore>(_ => new MediaJobStore(Path.Combine(LocalAiPaths.StateDir, "media-jobs")));
builder.Services.AddSingleton<MediaJobService>(services => new MediaJobService(
    services.GetRequiredService<MediaGenerationService>(),
    services.GetRequiredService<MediaJobStore>(),
    logger: services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MediaJobService>>()));
builder.Services.AddSingleton<RemoteAccessService>();
builder.Services.AddSingleton<WebAccessService>();
builder.Services.AddSingleton(InFlightRequestRegistry.Shared);
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddSingleton<TrainingAdvisorService>();
builder.Services.AddSingleton<AiProfileService>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<RerankerService>();
builder.Services.AddSingleton<MemoryStore>();
builder.Services.AddSingleton<RagStore>(sp =>
    new RagStore(sp.GetRequiredService<EmbeddingService>(), sp.GetRequiredService<RerankerService>()));
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<WebSearchService>();
builder.Services.AddSingleton<InternetSettingsService>();
builder.Services.AddSingleton<ResearchEngineService>();
builder.Services.AddSingleton<PresentationService>();
builder.Services.AddSingleton<SkillRegistryService>();
builder.Services.AddSingleton<SkillRouter>();
builder.Services.AddSingleton<PersonaService>();
builder.Services.AddSingleton<McpClientRegistry>();
builder.Services.AddSingleton<AvatarEngineService>();
builder.Services.AddSingleton<AvatarProviderService>();
builder.Services.AddSingleton<AdapterStore>();
builder.Services.AddSingleton<LoraGgufConversionService>();
builder.Services.AddSingleton<AdapterManagerService>();
builder.Services.AddSingleton<TrainingLabService>();
builder.Services.AddSingleton<SpeechService>();
builder.Services.AddSingleton<IContinuousSpeechStatusProvider>(services => services.GetRequiredService<SpeechService>());
builder.Services.AddSingleton<IContinuousSpeechStreamWorker>(services =>
    ContinuousTtsWorker.For(services.GetRequiredService<LocalRuntimeService>()));
builder.Services.AddSingleton<ModelBenchmarkStore>();
builder.Services.AddSingleton<IModelBenchmarkRunner, LlamaBenchRunner>();
builder.Services.AddSingleton<ModelBenchmarkService>();
builder.Services.AddModelArenaServices();
builder.Services.AddSingleton<IModelDestinationSpaceProvider, ModelDestinationSpaceProvider>();
builder.Services.AddSingleton<PaRuX.Server.Orchestration.Gaming.IGamingResumable>(services => services.GetRequiredService<ModelDownloadService>());
builder.Services.AddSingleton<ModelDownloadService>(services => new ModelDownloadService(
    completionHandler: new PostDownloadOptimizationHandler(
        services.GetRequiredService<ModelBenchmarkService>(), new PostDownloadOptimizationQueue()),
    destinationSpaceProvider: services.GetRequiredService<IModelDestinationSpaceProvider>(),
    stateDirectory: ServerState.StateDirectory,
    scanOrphanPartials: true));
builder.Services.AddSingleton<IModelDiscoveryProvider>(_ => ModelDiscoveryProviderSelector.Create(
    Environment.GetEnvironmentVariable(ModelDiscoveryProviderSelector.EndpointVariable),
    () => new HuggingFaceModelDiscoveryProvider()));
builder.Services.AddSingleton<LoraInstallJobs>(services => new LoraInstallJobs(services.GetRequiredService<ModelDownloadService>()));
builder.Services.AddSingleton(_ => new ModelDiscoveryCatalogStore());
builder.Services.AddSingleton(_ => new ModelDiscoveryPolicyStore());
builder.Services.AddSingleton(services => new ModelDiscoveryService(
    services.GetRequiredService<IModelDiscoveryProvider>(),
    services.GetRequiredService<ModelDiscoveryCatalogStore>(),
    () => DateTime.UtcNow,
    isGamingActive: () => services.GetRequiredService<LocalRuntimeService>().GamingMode));
builder.Services.AddSingleton(_ => new CandidateEvidenceStore());
builder.Services.AddSingleton(_ => new CandidateUtilityPolicyStore(ServerState.StateDirectory));
builder.Services.AddSingleton(services => new CandidateEvidenceService(
    services.GetRequiredService<ModelDiscoveryCatalogStore>(),
    services.GetRequiredService<CandidateEvidenceStore>(),
    ModelDownloadService.ModelsDirectory));
builder.Services.AddSingleton<ITextQualityRuntime, LlamaCandidateTextQualityRuntime>();
builder.Services.AddSingleton<ICandidateLaunchFenceSource>(services =>
    new ProcessCandidateLaunchFenceSource(services.GetRequiredService<LocalAiProcessManager>()));
builder.Services.AddSingleton(services => new CandidateTextQualityRunner(
    services.GetRequiredService<CandidateEvidenceService>(),
    services.GetRequiredService<ITextQualityRuntime>(),
    () => services.GetRequiredService<LocalAiProcessManager>().GamingActive));
builder.Services.AddSingleton<ITextPerformanceRuntime, LlamaCandidateTextPerformanceRuntime>();
builder.Services.AddSingleton(services => new CandidateUtilityPolicyCalibrationService(
    services.GetRequiredService<ITextQualityRuntime>(),
    services.GetRequiredService<ITextPerformanceRuntime>()));
// Recommendation transaction (BIC-16 S5). Registered but grants no authority: it only runs on an explicit,
// consented request with a positive proof, and reconciles a durable Pending once at startup.
builder.Services.AddSingleton(_ => new CandidateRecommendationStore());
builder.Services.AddSingleton<IActiveTextModelIdentitySource>(services => new ActiveTextModelIdentitySource(
    services.GetRequiredService<LocalAiProcessManager>(), services.GetRequiredService<RuntimeManagerService>()));
builder.Services.AddSingleton<IRecommendationRuntime>(services => new ManagedRecommendationRuntime(
    services.GetRequiredService<RuntimeManagerService>(), services.GetRequiredService<LocalAiProcessManager>(),
    services.GetRequiredService<CandidateEvidenceService>(),
    services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>()));
builder.Services.AddSingleton<IRecommendationProofSource>(services => new ProductionRecommendationProofSource(
    services.GetRequiredService<CandidateEvidenceService>(), services.GetRequiredService<CandidateUtilityPolicyStore>(),
    services.GetRequiredService<CandidateUtilityPolicyCalibrationService>(),
    services.GetRequiredService<IActiveTextModelIdentitySource>(),
    services.GetRequiredService<ICandidateLaunchFenceSource>()));
builder.Services.AddSingleton(services => new CandidateRecommendationService(
    services.GetRequiredService<CandidateRecommendationStore>(), services.GetRequiredService<IRecommendationRuntime>(),
    services.GetRequiredService<IRecommendationProofSource>(), services.GetRequiredService<ICandidateLaunchFenceSource>(),
    ModelDownloadService.ModelsDirectory));
builder.Services.AddHostedService(services => new RecommendationReconcileHostedService(
    services.GetRequiredService<CandidateRecommendationService>()));
builder.Services.AddSingleton(services => new CandidateTextPerformanceRunner(
    services.GetRequiredService<CandidateEvidenceService>(),
    services.GetRequiredService<ITextPerformanceRuntime>(),
    () => services.GetRequiredService<LocalAiProcessManager>().GamingActive));
builder.Services.AddSingleton<ICandidateTextQualityRunner>(services =>
    services.GetRequiredService<CandidateTextQualityRunner>());
builder.Services.AddSingleton<ICandidateTextPerformanceRunner>(services =>
    services.GetRequiredService<CandidateTextPerformanceRunner>());
builder.Services.AddSingleton(_ => new CandidateEvaluationJobStore());
builder.Services.AddSingleton(services => new CandidateEvaluationJobService(
    services.GetRequiredService<ModelDiscoveryCatalogStore>(),
    services.GetRequiredService<CandidateEvidenceService>(),
    services.GetRequiredService<ICandidateTextQualityRunner>(),
    services.GetRequiredService<ICandidateTextPerformanceRunner>(),
    services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping,
    summaryStore: services.GetRequiredService<CandidateEvaluationJobStore>(),
    isGamingActive: () => services.GetRequiredService<LocalRuntimeService>().GamingMode));
builder.Services.AddHostedService(services => services.GetRequiredService<CandidateEvaluationJobService>());
builder.Services.AddSingleton<ModelCompareService>();
builder.Services.AddSingleton<GroupChatService>();
builder.Services.AddSingleton<ThreedService>();
builder.Services.AddSingleton<MusicService>();
builder.Services.AddSingleton<TaskStore>();
builder.Services.AddSingleton<AgentOrchestrator>();
builder.Services.AddSingleton<ILocalCompletionClient, LlamaServerCompletionClient>();
builder.Services.AddSingleton<AgentWorkerService>();
builder.Services.AddSingleton<VerifierService>();
builder.Services.AddSingleton<CodingModelSelectionService>(services =>
{
    SmartRouterService router = services.GetRequiredService<SmartRouterService>();
    var gpuInventory = new GpuInventoryService();
    return new CodingModelSelectionService(
        services.GetRequiredService<TaskStore>(),
        ct => router.GetCandidateEvidenceAsync(CodingModelSelectionService.CodingRole, ct),
        () => RuntimePerformanceTuner.BuildGpuFingerprint(gpuInventory.GetStatus()),
        ServerState.StateDirectory);
});
builder.Services.AddSingleton<LocalApiServerService>(services => new LocalApiServerService(
    services.GetRequiredService<LocalRuntimeService>(),
    services.GetRequiredService<PaRuX.Server.Orchestration.IRoleRouter>(),
    httpClient: services.GetService<HttpClient>(),
    codingSelection: services.GetRequiredService<CodingModelSelectionService>()));
builder.Services.AddSingleton<LocalApiEmbeddingsService>();
builder.Services.AddSingleton<DeepResearchService>();
builder.Services.AddSingleton<DatasetDownloadService>();
builder.Services.AddSingleton<DatasetCurationService>();
builder.Services.AddSingleton<AutomationStore>();
builder.Services.AddSingleton<AutomationEngine>();
builder.Services.AddSingleton<ContextGraphStore>();
builder.Services.AddSingleton<RepoIndexService>();
builder.Services.AddSingleton<ArtifactStore>();
builder.Services.AddSingleton<MobileArtifactService>();
builder.Services.AddSingleton<UnifiedRegistryService>();
builder.Services.AddSingleton<ProjectManagerService>();
builder.Services.AddHostedService<ProjectProvisioningRecoveryHostedService>();
builder.Services.AddSingleton<AutopilotStore>();
builder.Services.AddSingleton<AutopilotService>();
builder.Services.AddSingleton<EvalStore>();
builder.Services.AddSingleton<EvalRunnerService>();
builder.Services.AddSingleton<AutoLabStore>();
builder.Services.AddSingleton<AutoLabService>();
builder.Services.AddSingleton<CapabilityProfilerStore>();
builder.Services.AddSingleton<CapabilityProfilerService>();
builder.Services.AddSingleton<ObservabilityService>();
builder.Services.AddSingleton<DiffService>();
builder.Services.AddSingleton<WorkflowStore>();
builder.Services.AddSingleton<WorkflowService>();
builder.Services.AddSingleton<CustomWorkflowService>();
builder.Services.AddSingleton<GamingProfileStore>();
builder.Services.AddSingleton(sp => new WorkloadOrchestrator(
    sp.GetRequiredService<LocalRuntimeService>(),
    sp.GetRequiredService<LocalAiProcessManager>(),
    sp.GetRequiredService<MediaJobService>(),
    sp.GetRequiredService<SpeechService>(),
    sp.GetRequiredService<GamingProfileStore>(),
    sp.GetService<ILogger<WorkloadOrchestrator>>(),
    cancelEvaluations: () => sp.GetRequiredService<CandidateEvaluationJobService>().CancelForGaming()));
builder.Services.AddSingleton<PaRuX.Server.Control.StopAllService>();
builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<GamingGuardService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkloadOrchestrator>());
    builder.Services.AddHostedService<AgentSchedulerHostedService>();
    builder.Services.AddHostedService(sp => new AutoMaintenanceHostedService(sp.GetRequiredService<AutoMaintenanceService>()));
    builder.Services.AddHostedService(sp => new IdleCalibrationHostedService(sp.GetRequiredService<IdleCalibrationService>()));
}
builder.Services.AddSingleton(sp => RuntimeSuggestionComposition.Create(sp));
builder.Services.AddSingleton(sp => AutoMaintenanceComposition.Create(sp));
builder.Services.AddSingleton(_ => new GenerationActivityTracker());
builder.Services.AddSingleton<RuntimeOptimizerLink>();
builder.Services.AddSingleton(sp => IdleCalibrationComposition.Create(sp));

StartupTrace.Mark("services-registered");
StartupTrace.WrapHostedServices(builder.Services);
WebApplication app = builder.Build();
StartupTrace.Mark("built");

// Before anything can ask for a model or a picture: put the runtime config files back if the
// LocalAI root does not have them. They are embedded in this binary, so a first install and a
// config folder somebody deleted both recover here rather than failing later as "llama-server
// exited unexpectedly" or "Missing workflow template". Existing files are never touched.
ConfigAssets.EnsureAll();
StartupTrace.Mark("config-assets-ensured");

ServerState state = app.Services.GetRequiredService<ServerState>();
// Download jobs are in-memory; construct the service now so preserved partials are visible before Model Hub is opened.
_ = app.Services.GetRequiredService<ModelDownloadService>();
RemoteAccessService remoteAccess = app.Services.GetRequiredService<RemoteAccessService>();
WebAccessService webAccess = app.Services.GetRequiredService<WebAccessService>();
ApiKeyService apiKeys = app.Services.GetRequiredService<ApiKeyService>();
InFlightRequestRegistry inFlight = app.Services.GetRequiredService<InFlightRequestRegistry>();
StartupTrace.Mark("core-singletons-resolved");

// Outermost: an unreadable text-runtime port table is a typed 503 on every route, never an empty 500.
app.UseMiddleware<TextRuntimeUnavailableMiddleware>();

// Tells the idle-time calibration when a prompt is running (and cancels it the moment one arrives). Observes only; no body is read.
app.Use(async (context, next) =>
{
    if (GenerationActivityTracker.IsGenerationRequest(context.Request.Method, context.Request.Path))
    {
        using IDisposable lease = context.RequestServices.GetRequiredService<GenerationActivityTracker>().Begin();
        await next();
        return;
    }
    await next();
});

// Universal Gateway port: model APIs only. Checked before every other rule so no control-plane,
// browser or Ollama route can ever be reached through it; authentication then proceeds as usual
// (an API key is accepted for /v1 exactly as on the Core port).
app.Use(async (context, next) =>
{
    if (gatewayPort is int port && context.Connection.LocalPort == port)
    {
        if (!PaRuX.Server.Gateway.GatewaySettings.IsGatewayPath(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = new { type = "not_found", message = "Only the model APIs are served on the PaRuX gateway port." } });
            return;
        }
        if (context.Request.Path == "/health")
        {
            await context.Response.WriteAsJsonAsync(new { status = "ok", service = "parux-gateway" });
            return;
        }
    }
    await next();
});

app.Use(async (context, next) =>
{
    bool loopback = IsLoopback(context);
    bool pairingCompletion = context.Request.Path == "/api/v1/remote/pair/complete";
    if (loopback && context.Request.Path == "/api/v1/health")
    {
        await next();
        return;
    }

    // The browser door. It is checked before the desktop token because a request that came through
    // a reverse proxy on this machine arrives from 127.0.0.1 and is otherwise indistinguishable
    // from a local process - loopback is not evidence of anything once a proxy is in front, so the
    // session cookie is what identifies a browser, and its absence simply falls through to the
    // token check below exactly as before. The login page and the login call itself are the only
    // unauthenticated paths, and only while browser access is switched on.
    // Only loopback may use the sign-in door. A LAN request reaches the gates below, so it is refused on the LAN's own terms
    // (private address, HTTPS, Origin, device token) instead of getting a passphrase guess that skips all of them.
    if (WebAccessEndpoints.IsPublicPath(context.Request, loopback) && webAccess.Enabled)
    {
        await next();
        return;
    }

    if (loopback && webAccess.Enabled)
    {
        WebAccessService.Session? session = webAccess.TryResume(
            context.Request.Cookies[WebAccessService.SessionCookieName]);
        if (session != null)
        {
            // A cross-site form can make the browser send the cookie in some configurations, but it
            // cannot set a custom header, so every state-changing request must echo the session's
            // own CSRF token. Safe methods are exempt because they change nothing.
            bool safeMethod = HttpMethods.IsGet(context.Request.Method)
                || HttpMethods.IsHead(context.Request.Method)
                || HttpMethods.IsOptions(context.Request.Method);
            if (!safeMethod)
            {
                string presented = context.Request.Headers[WebAccessService.CsrfHeaderName].ToString();
                if (presented.Length != session.CsrfToken.Length
                    || !CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(session.CsrfToken)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid CSRF token." });
                    return;
                }
            }

            context.Items["PaRuXOwner"] = "web:" + session.Id;
            context.Items["PaRuXWebSession"] = session;
            await next();
            return;
        }
    }

    // Inspect a cookie before taking the anonymous inspection path. Otherwise a real browser
    // session appears unauthenticated and loses the Sign out control. DELETE is deliberately
    // excluded: it changes session state and must pass the cookie + CSRF check above.
    if (loopback && webAccess.Enabled && context.Request.Path == WebAccessEndpoints.SessionPath
        && HttpMethods.IsGet(context.Request.Method))
    {
        await next();
        return;
    }

    if (!loopback)
    {
        if (context.Connection.RemoteIpAddress is not { } remoteIp
            || !RemoteAccessService.IsPrivateAddress(remoteIp)
            || !context.Request.IsHttps || !remoteAccess.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "LAN access is disabled or not using HTTPS." });
            return;
        }
        if (context.Request.Headers.ContainsKey("Origin"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Browser-origin LAN requests are not allowed." });
            return;
        }
        if (pairingCompletion)
        {
            await next();
            return;
        }

        string deviceToken = context.Request.Headers[PaRuXHeaders.DeviceToken].ToString();
        if (!remoteAccess.TryValidateDeviceToken(deviceToken, out string deviceId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "A paired device token is required." });
            return;
        }
        context.Items["PaRuXOwner"] = "device:" + deviceId;
        await next();
        return;
    }

    if (pairingCompletion)
    {
        await next();
        return;
    }

    // An API key from another application. Checked before the desktop token because that token is
    // regenerated on every Core start and is a handle for the app that launched the process, not a
    // credential a third-party client could hold. Keys are confined to the provider surfaces -
    // /v1 for OpenAI clients, the Ollama paths for the rest - so pasting one into another program
    // buys inference, never the control plane that can change settings or read the filesystem.
    if (loopback)
    {
        string bearer = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        string candidate = bearer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? bearer[prefix.Length..].Trim()
            : context.Request.Headers["X-Api-Key"].ToString();

        if (candidate.StartsWith(ApiKeyService.KeyPrefix, StringComparison.Ordinal))
        {
            if (!apiKeys.TryValidate(candidate, out string keyId))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = new { message = "Invalid API key." } });
                return;
            }
            if (!ProviderSurface.Covers(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        message = "An API key may call the OpenAI (/v1) and Ollama provider endpoints only."
                    }
                });
                return;
            }
            // Admitted under the key's revocation scope, then the key is checked again. Revoking removes the key before
            // it cancels the scope: a revocation before this check is refused here, and one after it cancels this request
            // through the lease, which is linked to RequestAborted so every endpoint's cancellation token observes it.
            using InFlightRequestLease admitted = inFlight.Begin(
                InFlightRequestRegistry.ApiKeyScope(keyId), context.RequestAborted);
            if (!apiKeys.IsActive(keyId))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = new { message = "Invalid API key." } });
                return;
            }
            context.Items["PaRuXOwner"] = "apikey:" + keyId;
            CancellationToken connectionAborted = context.RequestAborted;
            context.RequestAborted = admitted.Token;
            try { await next(); }
            finally
            {
                // The lease is disposed when this block ends, so the connection's own token goes back first: nothing
                // that outlives the request may keep a reference to a disposed token.
                context.RequestAborted = connectionAborted;
            }
            return;
        }
    }

    string provided = context.Request.Headers[PaRuXHeaders.Token].ToString();
    // A browser cannot set a header on a navigation, so the web client's own page - and only that
    // one path, only on loopback, which is already all this branch handles - may carry the token in
    // the query string. The page moves it into sessionStorage and strips it from the URL on load,
    // and every request it makes afterwards uses the header like every other client.
    if (string.IsNullOrEmpty(provided) && context.Request.Path == WebClientEndpoint.Path)
        provided = context.Request.Query["token"].ToString();
    if (string.IsNullOrEmpty(provided))
    {
        string authHeader = context.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            provided = authHeader[bearerPrefix.Length..].Trim();
    }
    bool valid = provided.Length == state.Token.Length
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(state.Token));
    if (!valid)
    {
        // A person who typed the address of their own PaRuX gets the sign-in page, not a bare
        // {"error":"Unauthorized"} they cannot act on. Measured on the physical PC: opening /app in
        // a fresh browser produced exactly that JSON, with nothing to say the door was at
        // /app/login. Only browser navigations are redirected - a fetch() or a CLI still gets the
        // 401 it can handle - and only while browser access is on, so a PaRuX that never enabled
        // it answers identically to before.
        if (webAccess.Enabled && WebAccessEndpoints.IsBrowserNavigation(context.Request))
        {
            context.Response.Redirect(WebAccessEndpoints.LoginPath);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
        return;
    }

    context.Items["PaRuXOwner"] = "desktop";
    await next();
});

// Android/paired-device control-plane routes deliberately sit behind the exact same global auth
// middleware as every other API route. The module exposes durable Project/task operations and
// sanitized Artifact transfer only; it never exposes raw Windows filesystem/input primitives.
app.MapMobileControlEndpoints();
app.MapCandidateUtilityEndpoints();
app.MapModelArenaEndpoints();
app.MapCandidateRecommendationEndpoints();

app.MapGet("/api/v1/health", async (LocalRuntimeService runtime, CancellationToken ct) =>
{
    bool providerReady = false;
    using (var probe = CancellationTokenSource.CreateLinkedTokenSource(ct))
    {
        probe.CancelAfter(TimeSpan.FromMilliseconds(250));
        providerReady = await runtime.IsHealthyAsync(probe.Token);
    }

    return Results.Ok(new ServerHealth
    {
        ProviderReady = providerReady,
        GamingMode = runtime.GamingMode
    });
});

app.MapGet("/api/v1/system/status", async (LocalRuntimeService runtime, CancellationToken ct) =>
{
    bool providerReady = false;
    using (var probe = CancellationTokenSource.CreateLinkedTokenSource(ct))
    {
        probe.CancelAfter(TimeSpan.FromMilliseconds(250));
        providerReady = await runtime.IsHealthyAsync(probe.Token);
    }

    return Results.Ok(new ServerHealth
    {
        ProviderReady = providerReady,
        GamingMode = runtime.GamingMode
    });
});

app.MapGet("/api/v1/remote/status", (RemoteAccessService remote, HttpContext context) =>
    IsDesktopOwner(context) ? Results.Ok(remote.GetStatus()) : Results.StatusCode(StatusCodes.Status403Forbidden));

app.MapPost("/api/v1/remote/config", (
    RemoteAccessConfigRequest request, RemoteAccessService remote, HttpContext context) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(remote.SaveConfiguration(request)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/v1/remote/pair/start", (RemoteAccessService remote, HttpContext context) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(remote.StartPairing()); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

app.MapPost("/api/v1/remote/pair/complete", (
    PairingCompleteRequest request, RemoteAccessService remote) =>
{
    try { return Results.Ok(remote.CompletePairing(request)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
});

app.MapDelete("/api/v1/remote/devices/{id}", (string id, RemoteAccessService remote, HttpContext context) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return remote.Revoke(id) ? Results.NoContent() : Results.NotFound(new { error = "Device not found" });
});

app.MapDelete("/api/v1/remote/devices", (RemoteAccessService remote, HttpContext context) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    int revoked = remote.RevokeAll();
    return Results.Ok(new { count = revoked });
});

app.MapPost("/api/v1/chat/completions", async (
    ChatCompletionRequest request,
    ChatProxyService proxy,
    HttpContext context,
    CancellationToken ct) =>
{
    // ChatSchedulingEndpointExtensions.MapPost already wraps this exact route: it combines the real
    // client-disconnect token with runtime.GamingCancellationToken before calling this handler, and
    // its own catch blocks correctly distinguish "client disconnected" from "Gaming Mode activated"
    // by testing the ORIGINAL, un-combined token. This handler used to redundantly recombine `ct`
    // (already Gaming-combined by the wrapper) with GamingCancellationToken again and then test
    // `ct.IsCancellationRequested` as if it meant "client disconnected" - but by the time this method
    // runs, that condition is also true whenever Gaming Mode is active, so a Gaming-triggered
    // cancellation matched the silent "client disconnected" branch here first and never reached the
    // wrapper's correct 409 handling: the request silently completed as an empty HTTP 200, a fake
    // success with no visible answer and no error. Let the exception propagate to the wrapper instead.
    await proxy.ProxyAsync(request, context.Response, ct);
});

app.MapPost("/api/v1/runtime/actions", async (
    RuntimeActionRequest request,
    LocalRuntimeService runtime,
    HttpContext context,
    CancellationToken ct) =>
{
    if (!IsDesktopOwner(context))
        return Results.Json(new RuntimeActionResponse { Success = false, Message = "Runtime controls are desktop-only." },
            statusCode: StatusCodes.Status403Forbidden);
    LocalRuntimeService.RuntimeResult result = await runtime.RunActionAsync(request.Action, ct);
    return Results.Json(new RuntimeActionResponse { Success = result.Success, Message = result.Message },
        statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
});

// Reports the data root, so where PaRuX keeps the user's data is something they can see rather
// than something they have to infer.
app.MapGet("/api/v1/system/data-root", () => Results.Ok(new DataRootStatus
{
    Path = PaRuX.Contracts.PaRuXDataRoot.Path
}));

// The LocalAI root is a desktop-owner setting (the POST below already requires that), so reading it is desktop-owner too.
app.MapGet("/api/v1/system/localai-root", (HttpContext context) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(new LocalAiRootStatus
    {
        Root = LocalAiPaths.Root,
        Configured = LocalAiPaths.IsExplicitlyConfigured,
        LooksConfigured = LocalAiPaths.LooksConfigured
    });
});

app.MapPost("/api/v1/system/localai-root", (LocalAiRootRequest request, HttpContext context) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        LocalAiPaths.SetRoot(request.Root);
        return Results.Ok(new LocalAiRootStatus
        {
            Root = LocalAiPaths.Root,
            Configured = LocalAiPaths.IsExplicitlyConfigured,
            LooksConfigured = LocalAiPaths.LooksConfigured
        });
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/v1/images/generations", async (
    ImageGenerationRequest request,
    MediaGenerationService generator,
    LocalRuntimeService runtime,
    CancellationToken ct) =>
{
    try
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.GamingCancellationToken);
        return Results.Ok(await generator.GenerateImageAsync(request, linked.Token));
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Conflict(new { error = "Gaming Mode activated; image generation was stopped." });
    }
    catch (TextRuntimeStateUnavailableException unavailable)
    {
        return TextRuntimeUnavailableResponse.Result(unavailable);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        return Results.Json(new { error = exception.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireDesktopOwner();

app.MapGet("/api/v1/conversations",(ConversationStore store) => Results.Ok(store.List()));

app.MapGet("/api/v1/conversations/{id}", (string id, ConversationStore store) =>
    store.Get(id) is { } doc ? Results.Ok(doc) : Results.NotFound(new { error = "Conversation not found" }));

app.MapPut("/api/v1/conversations/{id}", (string id, ConversationUpsertRequest request, ConversationStore store) =>
{
    try { return Results.Ok(store.Save(id, request)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Imports a ChatGPT, Claude or Gemini (Takeout) export; idempotent by stable id, reports imported/skipped counts.
ConversationImportRoute.Map(app).RequireDesktopOwner();

app.MapDelete("/api/v1/conversations/{id}", (string id, ConversationStore store) =>
    store.Delete(id) ? Results.NoContent() : Results.NotFound(new { error = "Conversation not found" }));

app.MapGet("/api/v1/ai/profile", async (AiProfileService profiles, CancellationToken ct) => Results.Ok(await profiles.GetStatusAsync(ct)));

app.MapPost("/api/v1/ai/profile", async (AiProfileRequest request, AiProfileService profiles, CancellationToken ct) =>
{
    try { return Results.Ok(await profiles.SetTextProfileAsync(request.TextProfile, ct)); }
    catch (TextRuntimeStateUnavailableException unavailable) { return TextRuntimeUnavailableResponse.Result(unavailable); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

app.MapPost("/api/v1/ai/profile/auto-fallback", async (AiAutoFallbackRequest request, AiProfileService profiles, CancellationToken ct) =>
{
    profiles.SetAutoFallbackEnabled(request.Enabled);
    return Results.Ok(await profiles.GetStatusAsync(ct));
}).RequireDesktopOwner();

// Backend discovery and status are desktop-owned too: both perform outbound probes, so a LAN device, a browser
// session or an API key must not be able to make PaRuX dial out on its behalf.
app.MapGet("/api/v1/ai/backends/detect", async (ExternalTextProviderService provider, HttpContext context, CancellationToken ct) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await provider.DetectAsync(ct));
});

app.MapGet("/api/v1/ai/backends/external", async (ExternalTextProviderService provider, HttpContext context, CancellationToken ct) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await provider.GetStatusAsync(ct));
});

// Mutating AI-backend routes are desktop-owned, like /api/v1/apikeys: a LAN device, a browser session or an
// API key must not repoint a provider, change the remote-egress opt-in or spend a stored credential.
app.MapPost("/api/v1/ai/backends/external", (SetExternalTextProviderRequest request, ExternalTextProviderService provider, HttpContext context) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { provider.SetProvider(request.ProviderName, request.Endpoint); return Results.NoContent(); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/v1/ai/backends/external", (ExternalTextProviderService provider, HttpContext context) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    provider.ClearProvider();
    return Results.NoContent();
});

// Remote (non-local) inference opt-in. Kept on its own route rather than folded into the provider
// endpoints above so that leaving the machine is always a separate, deliberate act: configuring an
// endpoint can never enable it as a side effect. Off by default; PaRuX depends on nothing here.
app.MapGet("/api/v1/ai/backends/remote-policy", (HttpContext context) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(new
    {
        allowRemoteProviders = ExternalTextProviderService.Policy.IsRemoteAllowed(),
        hasApiKey = ExternalTextProviderService.Policy.HasApiKey()
    });
});

app.MapPost("/api/v1/ai/backends/remote-policy", (SetRemoteProviderPolicyRequest request, HttpContext context) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        ExternalTextProviderService.Policy.SetRemoteAllowed(request.AllowRemoteProviders);
        // Only touch the credential when the caller actually supplied the field, so toggling the
        // policy does not silently wipe a key the user set earlier. Disabling always clears it -
        // that is handled inside SetRemoteAllowed, not here.
        if (request.ApiKey != null && request.AllowRemoteProviders)
            ExternalTextProviderService.Policy.SetApiKey(request.ApiKey);
        return Results.NoContent();
    }
    catch (PlatformNotSupportedException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Cloud fallback. Separate from both routes above on purpose: the remote-provider policy decides
// where a USER_MANAGED provider may live and then sends every turn there, whereas this decides
// nothing about routing at all - it only says what PaRuX may do on the turns its own local runtime
// could not serve. Off by default, and never consulted while local inference is working.
app.MapGet("/api/v1/ai/backends/cloud-fallback", (CloudFallbackService fallback, HttpContext context) =>
    IsDesktopOwner(context) ? Results.Ok(fallback.GetStatus()) : Results.StatusCode(StatusCodes.Status403Forbidden));

app.MapPost("/api/v1/ai/backends/cloud-fallback", (SetCloudFallbackRequest request, CloudFallbackService fallback, HttpContext context) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { fallback.Configure(request); return Results.Ok(fallback.GetStatus()); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (PlatformNotSupportedException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/v1/ai/backends/cloud-fallback", (CloudFallbackService fallback, HttpContext context) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    fallback.Disable();
    return Results.Ok(fallback.GetStatus());
});

// Verifies the credential against the provider's model list. Deliberately a GET of /models rather
// than a completion: checking the setup must not itself send any of the user's text off the device.
app.MapPost("/api/v1/ai/backends/cloud-fallback/test", async (CloudFallbackService fallback, HttpContext context, CancellationToken ct) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await fallback.TestAsync(ct));
});

app.MapPost("/api/v1/jobs", (MediaJobRequest request, MediaJobService jobs, LocalRuntimeService runtime, HttpContext context) =>
{
    if (runtime.GamingMode) return Results.Conflict(new { error = "AI paused by Gaming — Return to AI." });
    try { return Results.Accepted($"/api/v1/jobs", jobs.Create(request, RequestOwner(context))); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/v1/jobs", (MediaJobService jobs) => Results.Ok(jobs.List()));

app.MapGet("/api/v1/jobs/{id}", MediaJobStatusEndpoints.GetById);

app.MapPost("/api/v1/jobs/{id}/cancel", (string id, MediaJobService jobs) =>
    jobs.Cancel(id) ? Results.Accepted() : Results.NotFound(new { error = "Active job not found" }));

app.MapGet("/api/v1/media/{id}", (string id, MediaJobService jobs) =>
{
    MediaJobStatus? job = jobs.Get(id);
    if (job?.Asset is not { } asset || string.IsNullOrEmpty(asset.Base64Data))
        return Results.NotFound(new { error = "Media not found" });

    byte[] bytes;
    try { bytes = Convert.FromBase64String(asset.Base64Data); }
    catch (FormatException) { return Results.NotFound(new { error = "Media not found" }); }

    return Results.Bytes(bytes, asset.MediaType, asset.FileName, enableRangeProcessing: true);
});

app.MapGet("/api/v1/memory", (MemoryStore memory) => Results.Ok(memory.List()));

app.MapPost("/api/v1/memory", (MemoryAddRequest request, MemoryStore memory) =>
{
    if (string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "content is required." });
    return Results.Ok(memory.Add(request));
});

app.MapPut("/api/v1/memory/{id}", (string id, MemoryUpdateRequest request, MemoryStore memory) =>
    memory.Update(id, request.Content) ? Results.NoContent() : Results.NotFound(new { error = "Memory entry not found" })).RequireDesktopOwner();

app.MapPost("/api/v1/memory/{id}/enabled", (string id, MemoryEnabledRequest request, MemoryStore memory) =>
    memory.SetEnabled(id, request.Enabled) ? Results.NoContent() : Results.NotFound(new { error = "Memory entry not found" })).RequireDesktopOwner();

app.MapDelete("/api/v1/memory/{id}", (string id, MemoryStore memory) =>
    memory.Delete(id) ? Results.NoContent() : Results.NotFound(new { error = "Memory entry not found" }));

app.MapDelete("/api/v1/memory", (MemoryStore memory) => Results.Ok(new { deleted = memory.DeleteAll() })).RequireDesktopOwner();

app.MapPost("/api/v1/memory/import", (List<MemoryEntryDto> entries, MemoryStore memory) =>
    Results.Ok(new { imported = memory.Import(entries) })).RequireDesktopOwner();

app.MapGet("/api/v1/rag/sources", (RagStore rag) => Results.Ok(rag.ListSources()));

app.MapPost("/api/v1/rag/sources", (RagAddSourceRequest request, RagStore rag) =>
{
    if (string.IsNullOrWhiteSpace(request.FolderPath) || !Directory.Exists(request.FolderPath))
        return Results.BadRequest(new { error = "folderPath must be a folder that exists on this PC." });
    return Results.Ok(rag.AddSource(request.FolderPath));
}).RequireDesktopOwner();

app.MapPost("/api/v1/rag/sources/{id}/enabled", (string id, RagEnabledRequest request, RagStore rag) =>
{
    rag.SetSourceEnabled(id, request.Enabled);
    return Results.NoContent();
}).RequireDesktopOwner();

app.MapPost("/api/v1/rag/sources/{id}/reindex", async (string id, RagStore rag, HttpContext context, CancellationToken ct) =>
{
    RagSourceDto? source = rag.ListSources().Find(s => s.Id == id);
    if (source == null) return Results.NotFound(new { error = "RAG source not found" });
    try
    {
        (int documents, int chunks) = await rag.IndexSourceAsync(id, source.FolderPath, null, ct);
        return Results.Ok(new { documentCount = documents, chunkCount = chunks });
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapDelete("/api/v1/rag/sources/{id}", (string id, RagStore rag) =>
{
    rag.RemoveSource(id);
    return Results.NoContent();
});

app.MapGet("/api/v1/backup/export", async (BackupService backups, HttpContext context, CancellationToken ct) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        byte[] zip = await backups.ExportAsync(ct);
        BackupImportGuardMiddleware.EnsureRestorable(zip);
        string fileName = "parux-backup-" + DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss") + ".zip";
        return Results.File(zip, "application/zip", fileName);
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/v1/backup/import", async (HttpContext context, BackupService backups, CancellationToken ct) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (context.Request.ContentLength is null or 0)
        return Results.BadRequest(new { error = "A backup .zip file is required." });
    try
    {
        BackupImportResult result = await backups.ImportAsync(
            context.Request.Body, ct, ProductV2Endpoints.AllowIncompleteRollbackSnapshot(context));
        return Results.Ok(result);
    }
    catch (BackupService.IncompleteRollbackSnapshotException exception)
    {
        return ProductV2Endpoints.RefusalResult(exception);
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapDelete("/api/v1/backup/safety-snapshots", async (HttpContext context, BackupService backups, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!string.Equals(context.Items["PaRuXOwner"] as string, "desktop", StringComparison.Ordinal))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        return Results.Ok(new { removed = await backups.ClearPreRestoreSnapshotsAsync(ct) });
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

/// Multi-step research: plan, search, read real pages, then answer citing only sources actually
/// fetched. Returns 400 with the pipeline's own reason (Internet disabled, nothing readable) rather
/// than a synthesised answer, so a caller can never mistake a memory-only reply for research.
app.MapPost("/api/v1/research/deep", async (
    DeepResearchRequest request, DeepResearchService research, CancellationToken ct) =>
{
    DeepResearchReport report = await research.ResearchAsync(
        request.Question ?? string.Empty, request.MaxSubQuestions, ct);
    return report.Success ? Results.Ok(report) : Results.BadRequest(report);
}).RequireDesktopOwner();

app.MapGet("/api/v1/embeddings/engine", (EmbeddingService embeddings) => Results.Ok(embeddings.GetStatus()));

app.MapPost("/api/v1/embeddings/engine/install", async (EmbeddingService embeddings, CancellationToken ct) =>
{
    try
    {
        await embeddings.InstallEngineAsync(null, ct);
        return Results.Ok(embeddings.GetStatus());
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireDesktopOwner();

app.MapGet("/api/v1/reranker/engine", (RerankerService reranker) => Results.Ok(reranker.GetStatus()));

app.MapPost("/api/v1/reranker/engine/install", async (RerankerService reranker, CancellationToken ct) =>
{
    try
    {
        await reranker.InstallEngineAsync(null, ct);
        return Results.Ok(reranker.GetStatus());
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/reranker/rerank", (RerankRequest request, RerankerService reranker) =>
{
    if (string.IsNullOrWhiteSpace(request.Query) || request.Documents.Count == 0)
        return Results.BadRequest(new { error = "query and at least one document are required." });
    return Results.Ok(reranker.Rerank(request.Query, request.Documents, request.TopK));
}).RequireDesktopOwner();

app.MapGet("/api/v1/internet", (InternetSettingsService internet) =>
    Results.Ok(new InternetStatus { Enabled = internet.GetEnabled() }));

app.MapPost("/api/v1/internet", (InternetEnabledRequest request, InternetSettingsService internet) =>
{
    internet.SetEnabled(request.Enabled);
    return Results.Ok(new InternetStatus { Enabled = request.Enabled });
});

app.MapGet("/api/v1/personas", (PersonaService personas) => Results.Ok(personas.GetAll()));

app.MapPost("/api/v1/personas", (CreatePersonaRequest request, PersonaService personas) =>
{
    try { return Results.Ok(personas.Create(request.Name, request.SystemPrompt)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/personas/import-card", (ImportPersonaCardRequest request, PersonaService personas) =>
{
    try
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.DataBase64 ?? string.Empty); }
        catch (FormatException) { return Results.BadRequest(new { error = "The card data is not valid Base64." }); }
        (string name, string prompt) = CharacterCardImporter.ToPersona(bytes, request.FileName ?? string.Empty);
        return Results.Ok(personas.Create(name, prompt));
    }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapDelete("/api/v1/personas/{id}", (string id, PersonaService personas) =>
{
    try { personas.Delete(id); return Results.NoContent(); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/personas/active", (SetActivePersonaRequest request, PersonaService personas) =>
{
    try { personas.SetActive(request.PersonaId); return Results.Ok(personas.GetAll()); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

// The browser client. Loopback only - a headless PaRuX Core has no desktop app, and this is what
// makes it usable at all. LAN access stays on the paired-device path, which has a device token and
// certificate pinning; a browser has neither, and shipping a LAN web UI without them would trade
// the product's security model for convenience.
app.MapGet(WebClientEndpoint.Path, (HttpContext context, WebAccessService web) =>
{
    if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    // Reaching here means the middleware already accepted this request - either the desktop token,
    // or a browser session. A browser that has neither is sent to sign in rather than shown a bare
    // 401, which is what makes the reverse-proxy case usable at all.
    if (context.Items["PaRuXOwner"] is not string owner) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (web.Enabled && !owner.StartsWith("web:", StringComparison.Ordinal)
        && context.Request.Cookies.ContainsKey(WebAccessService.SessionCookieName))
        return Results.Redirect(WebAccessEndpoints.LoginPath);

    // The page is tested for having no external references; this makes that a property the browser
    // enforces rather than one a test merely observed. connect-src 'self' also means the client
    // cannot be turned into an exfiltration path for the chat it is displaying.
    WebAccessEndpoints.ApplyClientHeaders(context);
    return Results.Content(WebClientEndpoint.Html, "text/html; charset=utf-8");
});

app.MapWebAccessEndpoints();
app.MapOllamaCompatibilityEndpoints();

/// What to do next to make this PaRuX faster or more accurate, drawn from what it has measured on
/// this machine. The evidence is gathered here and the reasoning is a pure function of it, so the
/// report always names the number behind every recommendation.
app.MapGet("/api/v1/advisor/training", async (
    string? workload,
    RuntimeManagerService runtimeManager,
    EvalStore evals,
    ModelDownloadService models,
    TrainingAdvisorService advisor,
    CancellationToken ct) =>
{
    string normalized = RuntimePerformanceTuner.NormalizeWorkload(workload);
    RuntimePerformanceProfileSelection? measured = null;
    try
    {
        string? modelPath = (await runtimeManager.GetStatusAsync(ct)).ActiveModelPath;
        if (!string.IsNullOrWhiteSpace(modelPath))
        {
            // The same key the probe used when it stored the measurement, so a CUDA machine does
            // not read a Vulkan machine's numbers back.
            string backendKey = LocalAiPaths.GetActiveRuntimeBuildTag("text-cuda") != null
                ? "text-cuda" : "text-vulkan";
            string tag = LocalAiPaths.GetActiveRuntimeBuildTag(backendKey)
                ?? LocalAiPaths.DefaultRuntimeBuildTag(backendKey);
            string fingerprint = RuntimePerformanceTuner.BuildGpuFingerprint(new GpuInventoryService().GetStatus());
            // Constructed rather than injected, matching RuntimeLaunchOptimizer and
            // RuntimeHardwareEndpointExtensions: the store is a thin reader over one JSON file and
            // is not a DI service anywhere else in the server.
            measured = new RuntimePerformanceProfileStore().FindValid(modelPath, normalized, tag, fingerprint);
        }
    }
    catch
    {
        // No measurement is a real answer here - the advisor says "measure first" rather than
        // inventing numbers for a machine it has not benchmarked.
    }

    // Eval evidence is summed across completed runs: one small run says nothing, and the question
    // the advisor is answering is whether a weakness has shown up repeatedly.
    int passed = 0;
    int failed = 0;
    try
    {
        foreach (EvalDatasetDto dataset in evals.ListDatasets())
        {
            foreach (EvalRunDto run in evals.ListRuns(dataset.Id))
            {
                if (!string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)) continue;
                passed += run.PassCount;
                failed += run.FailCount;
            }
        }
    }
    catch { /* an unreadable eval history means no accuracy evidence, not a failed report */ }

    string? draftCandidate = null;
    try
    {
        if (measured != null)
        {
            draftCandidate = TrainingAdvisorService.FindDraftCandidate(
                Path.GetFileName(measured.ModelPath),
                measured.ModelSizeBytes,
                models.ListManagedModels().Select(model => (model.FileName, model.SizeBytes)));
        }
    }
    catch { /* an unreadable library means no candidate, not a failed report */ }

    var evidence = new TrainingAdvisorService.Evidence(
        measured?.DecodeTokensPerSecond,
        measured?.TimeToFirstTokenMilliseconds,
        measured?.PromptCacheImprovementPercent,
        measured?.PromptCacheColdTtftMilliseconds,
        measured?.PromptCacheWarmTtftMilliseconds,
        !string.IsNullOrWhiteSpace(measured?.Profile.SpeculativeDraftModelPath),
        passed,
        failed,
        draftCandidate);

    return Results.Ok(advisor.Build(normalized, evidence));
});

// API keys are minted and revoked from the desktop only: a browser session or another API key must
// never be able to issue itself a fresh credential, which would make revocation meaningless.
app.MapGet("/api/v1/apikeys", (HttpContext context, ApiKeyService keys) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(keys.List());
});

app.MapPost("/api/v1/apikeys", (ApiKeyCreateRequest request, HttpContext context, ApiKeyService keys) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(keys.Create(request?.Name)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapDelete("/api/v1/apikeys/{id}", (string id, HttpContext context, ApiKeyService keys) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return keys.Revoke(id) ? Results.NoContent() : Results.NotFound(new { error = "No such API key." });
});

// Universal Gateway: settings and live status for Settings > Integrations. Desktop only; the
// gateway port itself never serves these. Port changes apply on the next Core start.
app.MapGet("/api/v1/gateway/status", (HttpContext context) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    PaRuX.Server.Gateway.GatewaySettings saved = PaRuX.Server.Gateway.GatewaySettings.Load(ServerState.StateDirectory);
    LocalApiServerService.GatewayRoutingRecord? last = LocalApiServerService.LastRoutingDecision;
    return Results.Ok(new
    {
        listening = gatewayPort != null,
        port = gatewayPort ?? saved.Port,
        configuredPort = saved.Port,
        restartRequired = saved.Enabled != (gatewayPort != null) || (gatewayPort != null && gatewayPort != saved.Port),
        baseUrl = "http://127.0.0.1:" + (gatewayPort ?? saved.Port),
        enabled = saved.Enabled,
        vendorModelDefault = saved.VendorModelDefault,
        // Gateway request/response bodies are never written to logs.
        promptLogging = false,
        models = PaRuX.Server.Gateway.GatewayModelAliases.Virtual.Select(v => new { id = v.Id, role = v.Role, description = v.Description }),
        lastRouting = last == null ? null : new { requestedModel = last.RequestedModel, source = last.AliasSource, role = last.Role, modelFile = last.ModelFile == null ? null : Path.GetFileName(last.ModelFile), utc = last.Utc }
    });
});

app.MapPut("/api/v1/gateway/settings", (PaRuX.Server.Gateway.GatewaySettings request, HttpContext context) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request == null) return Results.BadRequest(new { error = "Missing gateway settings." });
    PaRuX.Server.Gateway.GatewaySettings normalized = request.Normalized();
    normalized.Save(ServerState.StateDirectory);
    // The vendor-model choice applies immediately; enable/port apply on the next Core start.
    LocalApiServerService.VendorModelDefault = normalized.VendorModelDefault;
    return Results.Ok(normalized);
});

// Settings owns the browser door. Loopback-and-token only, exactly like the LAN configuration
// routes: a browser session must never be able to change the passphrase that admitted it, or to
// switch the feature off and lock the desktop out of its own setting.
app.MapGet("/api/v1/web/access", (HttpContext context, WebAccessService web) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(new WebAccessStatus
    {
        Enabled = web.Enabled,
        PassphraseSet = web.PassphraseSet,
        ActiveSessions = web.ActiveSessionCount,
        LocalUrl = "http://127.0.0.1:51123" + WebClientEndpoint.Path
    });
});

app.MapPost("/api/v1/web/access", (
    WebAccessConfigRequest request, HttpContext context, WebAccessService web) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        if (request.Passphrase != null) web.SetPassphrase(request.Passphrase);
        if (request.Enabled.HasValue) web.SetEnabled(request.Enabled.Value);
        return Results.Ok(new WebAccessStatus
        {
            Enabled = web.Enabled,
            PassphraseSet = web.PassphraseSet,
            ActiveSessions = web.ActiveSessionCount,
            LocalUrl = "http://127.0.0.1:51123" + WebClientEndpoint.Path
        });
    }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapDelete("/api/v1/web/access/sessions", (HttpContext context, WebAccessService web) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    web.RevokeAll();
    return Results.NoContent();
});

// Apps already installed on this PC - notes, mail, calendar, bookmarks. Every source is off until
// connected, read-only, and scoped to the one path it was given. Detection offers; it never enables.
app.MapGet("/api/v1/desktop/integrations", (PaRuX.Server.DesktopIntegration.DesktopIntegrationService desktop) =>
    Results.Ok(desktop.GetStatuses()));

app.MapPost("/api/v1/desktop/integrations/{id}/connect", (
    string id, ConnectDesktopIntegrationRequest request,
    PaRuX.Server.DesktopIntegration.DesktopIntegrationService desktop, HttpContext context) =>
{
    // Desktop owner only: choosing which of your own files an assistant may read is a decision for the person sitting at
    // the machine, not for a paired phone or a browser session. Loopback alone admits a reverse proxy's requests too.
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(desktop.Connect(id, request?.Path)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/v1/desktop/integrations/{id}", (
    string id, PaRuX.Server.DesktopIntegration.DesktopIntegrationService desktop, HttpContext context) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    desktop.Disconnect(id);
    return Results.NoContent();
});

// A bounded preview, so someone can see exactly what PaRuX reads from a source before trusting it
// with a question. Returns excerpts and provenance, never whole files.
app.MapGet("/api/v1/desktop/integrations/{id}/preview", (
    string id, PaRuX.Server.DesktopIntegration.DesktopIntegrationService desktop, int? limit) =>
{
    try
    {
        int take = Math.Clamp(limit ?? 20, 1, 100);
        var items = desktop.Read(id, new PaRuX.Server.DesktopIntegration.DesktopReadLimits(MaxItems: take))
            .Take(take)
            .Select(item => new DesktopIntegrationItem
            {
                SourceId = item.SourceId,
                Kind = item.Kind,
                Id = item.Id,
                Title = item.Title,
                Subtitle = item.Subtitle,
                Timestamp = item.Timestamp,
                Excerpt = item.Body.Length <= 400 ? item.Body : item.Body[..400] + "…",
                Location = item.Location
            })
            .ToList();
        return Results.Ok(items);
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return Results.Problem("That source could not be read right now: " + ex.Message);
    }
});

// Where documents PaRuX generates are saved. Defaults inside PaRuX's own data directory rather than
// the user's Documents folder: writing there uninvited is the presumption this whole path avoids.
app.MapGet("/api/v1/desktop/documents/output", (
    PaRuX.Server.DesktopIntegration.DocumentWriteService documents) =>
{
    string folder = documents.OutputFolder;
    return Results.Ok(new DocumentOutputStatus
    {
        Folder = folder,
        IsDefault = folder.StartsWith(PaRuXDataRoot.Path, PaRuXPathSafety.PathComparison)
    });
});

app.MapPost("/api/v1/desktop/documents/output", (
    SetDocumentOutputRequest request,
    PaRuX.Server.DesktopIntegration.DocumentWriteService documents, HttpContext context) =>
{
    // Desktop owner only, like connecting a source: choosing where an assistant may create files is a decision for the
    // person at the machine.
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        documents.SetOutputFolder(request?.Folder);
        string folder = documents.OutputFolder;
        return Results.Ok(new DocumentOutputStatus
        {
            Folder = folder,
            IsDefault = folder.StartsWith(PaRuXDataRoot.Path, PaRuXPathSafety.PathComparison)
        });
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// The status carries each server's launch command, arguments and URL. That is this machine's own configuration, so only the
// desktop owner reads it; a browser session is refused the same way it is refused every other MCP route.
app.MapGet("/api/v1/mcp/servers", async (McpClientRegistry mcp, HttpContext context, CancellationToken ct) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await mcp.GetStatusesAsync(ct));
});

// Accepts both transports. The credential in the request is stored encrypted and is never read
// back out through this API - status reports only whether one exists.
app.MapPost("/api/v1/mcp/servers", (AddMcpServerRequest request, McpClientRegistry mcp, HttpContext context) =>
{
    // A stdio server is a command this machine runs, so only the desktop owner may add one: a browser session may not.
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(mcp.AddServer(request)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Narrows (or, with an empty list, restores) the exact tools one server may put in front of the
// model. Kept as its own route because approving a tool list is a separate decision from adding
// the server, and should be revisitable without re-entering a credential.
app.MapPost("/api/v1/mcp/servers/{id}/allowed-tools", async (
    string id, SetMcpAllowedToolsRequest request, McpClientRegistry mcp, HttpContext context, CancellationToken ct) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { await mcp.SetAllowedToolsAsync(id, request.AllowedTools, ct); return Results.NoContent(); }
    catch (FileNotFoundException) { return Results.NotFound(new { error = "No MCP server with that id." }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/v1/mcp/servers/{id}", async (string id, McpClientRegistry mcp, HttpContext context, CancellationToken ct) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    await mcp.RemoveServerAsync(id, ct);
    return Results.NoContent();
});

app.MapPost("/api/v1/mcp/servers/{id}/enabled", async (string id, SetMcpServerEnabledRequest request, McpClientRegistry mcp, HttpContext context, CancellationToken ct) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { await mcp.SetEnabledAsync(id, request.Enabled, ct); return Results.NoContent(); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/v1/orchestrator/tasks/{id}/mcp-tools", async (
    string id, TaskStore tasks, McpClientRegistry mcp, HttpContext context, CancellationToken ct) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    AgentTaskDto? task = tasks.Get(id);
    if (task == null) return Results.NotFound(new { error = "Task not found" });
    if (!AgentMcpToolAuthority.CanUse(task)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    IReadOnlyList<McpExternalToolSchema> externalTools = await mcp.GetCodingAgentToolSchemasAsync(ct);
    AgentTaskDto? currentTask = tasks.Get(id);
    if (!AgentMcpToolAuthority.CanUse(currentTask)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    IReadOnlyList<AgentMcpToolDefinition> candidates = externalTools.Select(tool =>
        new AgentMcpToolDefinition(tool.ServerId, tool.Name, tool.Description, tool.InputSchema)).ToList();
    return Results.Ok(AgentMcpToolAuthority.FilterAvailableTools(currentTask, candidates));
});

app.MapPost("/api/v1/orchestrator/tasks/{id}/mcp-tools/call", async (
    string id, AgentMcpToolCallRequest request, TaskStore tasks, McpClientRegistry mcp,
    HttpContext context, CancellationToken ct) =>
{
    if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    AgentTaskDto? task = tasks.Get(id);
    if (task == null) return Results.NotFound(new { error = "Task not found" });
    if (request.Arguments.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new { error = "MCP tool arguments must be a JSON object." });
    if (!AgentMcpToolAuthority.CanRequest(task, request.ServerId, request.ToolName))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    try
    {
        IReadOnlyList<McpExternalToolSchema> selectedTools =
            await mcp.GetCodingAgentToolSchemasAsync(ct, request.ServerId);
        IReadOnlyList<AgentMcpToolDefinition> candidates = selectedTools.Select(tool =>
            new AgentMcpToolDefinition(tool.ServerId, tool.Name, tool.Description, tool.InputSchema)).ToList();
        // Discovery can take seconds. Re-read task lifecycle and capability immediately before
        // the external side effect so cancellation/revocation during discovery closes the route.
        AgentTaskDto? currentTask = tasks.Get(id);
        if (!AgentMcpToolAuthority.CanCall(currentTask, request.ServerId, request.ToolName, candidates))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        string result = await mcp.CallExternalToolAsync(
            request.ServerId, request.ToolName, request.Arguments.GetRawText(), ct);
        return Results.Ok(new { result });
    }
    catch (FileNotFoundException) { return Results.NotFound(new { error = "No MCP server with that id." }); }
    catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch { return Results.Problem("The external MCP tool call failed.", statusCode: StatusCodes.Status502BadGateway); }
});

// A deliberately narrow documentation-search tool for the native desktop coding agent.
// A full research run would itself invoke models and consume more context; this endpoint
// only gathers bounded, untrusted source metadata. Desktop-owner loopback + Core Internet
// consent are both required. It cannot enable Internet or fetch an arbitrary LAN URL.
// Optional, read-only OpenCode interoperability. This observes a user-operated
// server; it cannot create/prompt/delete sessions, run code, or grant OpenCode
// filesystem permissions. The path owner gate is stricter than ordinary API auth.
app.MapGet("/api/v1/agent-backends/opencode/health", async (
    HttpContext context, CancellationToken ct) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    string? url = Environment.GetEnvironmentVariable("PARUX_OPENCODE_OBSERVE_URL");
    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? endpoint)
        || !OpenCodeLocalProbe.IsAllowedEndpoint(endpoint))
        return Results.Ok(new OpenCodeLocalProbe.Result(false, "not_configured", null));
    return Results.Ok(await OpenCodeLocalProbe.CheckAsync(endpoint, ct).ConfigureAwait(false));
});

app.MapGet("/api/v1/agent-backends/opencode/sessions/{sessionId}/observation", async (
    string sessionId, HttpContext context, CancellationToken ct) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!OpenCodeSessionObserver.IsAllowedSessionId(sessionId))
        return Results.BadRequest(new { error = "Invalid OpenCode session identifier." });
    return Results.Ok(await OpenCodeSessionObserver.ObserveIfConfiguredAsync(sessionId, ct)
        .ConfigureAwait(false));
});

// Native coding-agent preflight: ask the REAL selected managed coding model's
// tokenizer to count the same JSON-schema chat request before spending inference.
// Desktop owner only. This endpoint never logs the prompt, responds with counts
// only and never enables any shell/computer-use/network capability.
app.MapPost("/api/v1/coding/prompt-token-count", async (
    CodingRenderedPromptCountRequest request, LocalApiServerService localApi,
    HttpContext context, CancellationToken ct) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!CodingRenderedPromptCountContract.IsValid(request))
        return Results.BadRequest(new { error = "Invalid or oversized coding prompt." });
    try
    {
        string body = CodingRenderedPromptCountContract.BuildProviderPayload(request);
        int? tokens = await localApi.TryCountRenderedCodingPromptAsync(body, ct)
            .ConfigureAwait(false);
        return Results.Ok(new {
            inputTokens = tokens,
            exact = tokens.HasValue,
            source = tokens.HasValue ? "managed-llama-rendered-chat" : "unavailable"
        });
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception)
    {
        // Model-not-ready, tokenizer-not-supported and schema/provider failures
        // are not authentication failures or evidence that a prompt fits.
        return Results.Ok(new { inputTokens = (int?)null, exact = false,
            source = "unavailable" });
    }
});

app.MapPost("/api/v1/coding/web-search", async (
    CodingWebSearchRequest request, WebSearchService search,
    InternetSettingsService internet, LocalRuntimeService runtime,
    HttpContext context, CancellationToken ct) =>
{
    if (!IsLoopback(context)
        || !string.Equals(context.Items["PaRuXOwner"]?.ToString(), "desktop", StringComparison.Ordinal))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!internet.GetEnabled())
        return Results.Conflict(new { error = "PaRuX Internet is OFF; documentation search was not executed." });
    if (!CodingWebSearchPolicy.IsValidQuery(request.Query))
        return Results.BadRequest(new { error = "Search query is empty, too long or contains control characters." });

    try
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.GamingCancellationToken);
        var results = await search.SearchAsync(
            request.Query.Trim(), CodingWebSearchPolicy.ResultLimit(request.MaxResults), linked.Token)
            .ConfigureAwait(false);
        // If the user switched off Internet while this request was in flight,
        // do not inject subsequently returned web text into the coding task.
        if (!internet.GetEnabled())
            return Results.Conflict(new { error = "Internet was switched off during this search." });
        return Results.Ok(new
        {
            results = results
                .Where(item => CodingWebSearchPolicy.IsSafeSourceUrl(item.Url))
                .Take(CodingWebSearchPolicy.MaximumResultCount)
                .Select(item => new {
                    title = (item.Title ?? string.Empty).Length > 180 ? item.Title[..180] : item.Title,
                    url = item.Url,
                    snippet = (item.Snippet ?? string.Empty).Length > 500 ? item.Snippet[..500] : item.Snippet
                }).ToArray()
        });
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Conflict(new { error = "Gaming Mode activated; documentation search was stopped." });
    }
    catch (InternetAccessDisabledException)
    {
        // The switch went off while the search was running. Report it as the post-search check does, not as a
        // provider failure.
        return Results.Conflict(new { error = "Internet was switched off during this search." });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Avoid leaking provider HTML, internal paths or request token details.
        return Results.Json(new { error = "The configured search providers are unavailable." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/v1/research", async (
    ResearchRequest request, ResearchEngineService research, InternetSettingsService internet,
    LocalRuntimeService runtime, HttpContext context, CancellationToken ct) =>
{
    if (!internet.GetEnabled())
        return Results.Conflict(new { error = "Internet is disabled. Enable it in Settings to use Research." });
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "question is required." });
    try
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.GamingCancellationToken);
        return Results.Ok(await research.RunAsync(request.Question, request.Mode, linked.Token));
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Conflict(new { error = "Gaming Mode activated; research was stopped." });
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/v1/presentations", async (
    PresentationRequest request, PresentationService presentations, LocalRuntimeService runtime,
    HttpContext context, CancellationToken ct) =>
{
    try
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.GamingCancellationToken);
        (List<PresentationSlideDto> slides, byte[] pptxBytes) = await presentations.GenerateAsync(request.Topic, request.SlideCount, linked.Token);
        return Results.Ok(new PresentationResponse
        {
            FileName = "PaRuX_" + Guid.NewGuid().ToString("N")[..8] + ".pptx",
            Slides = slides,
            Base64Data = Convert.ToBase64String(pptxBytes)
        });
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Conflict(new { error = "Gaming Mode activated; presentation generation was stopped." });
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/v1/skills", (SkillRegistryService skills) => Results.Ok(skills.List()));

app.MapGet("/api/v1/skills/{id}", (string id, SkillRegistryService skills) =>
{
    SkillManifest? skill = skills.Get(id);
    return skill != null ? Results.Ok(skill) : Results.NotFound(new { error = "Skill not found" });
});

app.MapPost("/api/v1/skills", (SkillCreateRequest request, SkillRegistryService skills) =>
{
    if (string.IsNullOrWhiteSpace(request.Manifest.DisplayName))
        return Results.BadRequest(new { error = "displayName is required." });
    try
    {
        return Results.Ok(skills.CreateOrUpdateUserSkill(request.Manifest, isImport: false));
    }
    catch (InvalidDataException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/skills/import", (SkillManifest manifest, SkillRegistryService skills) =>
{
    if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        return Results.BadRequest(new { error = "displayName is required." });
    try
    {
        return Results.Ok(skills.CreateOrUpdateUserSkill(manifest, isImport: true));
    }
    catch (InvalidDataException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/skills/{id}/enabled", (string id, SkillEnabledRequest request, SkillRegistryService skills) =>
    skills.SetEnabled(id, request.Enabled) ? Results.NoContent() : Results.NotFound(new { error = "Skill not found" }));

app.MapPost("/api/v1/skills/{id}/permissions", (string id, SkillPermissions permissions, SkillRegistryService skills) =>
{
    if (skills.Get(id) == null) return Results.NotFound(new { error = "Skill not found" });
    try
    {
        return Results.Ok(skills.SetPermissions(id, permissions));
    }
    catch (InvalidDataException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).RequireDesktopOwner();

app.MapDelete("/api/v1/skills/{id}", (string id, SkillRegistryService skills) =>
    skills.DeleteUserSkill(id) ? Results.NoContent() : Results.NotFound(new { error = "Only user or imported skills can be deleted, and the id must exist." }));

app.MapPost("/api/v1/skills/route", (SkillRouteRequest request, SkillRouter router) =>
    Results.Ok(router.Route(request.Message, request.ForcedSkillId))).RequireDesktopOwner();

app.MapPost("/api/v1/orchestrator/tasks", (CreateAgentTaskRequest request, TaskStore tasks) =>
{
    try
    {
        return Results.Ok(tasks.Create(request));
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapGet("/api/v1/orchestrator/tasks", (TaskStore tasks) =>
    Results.Ok(new AgentTaskListResponse { Tasks = tasks.List() }));

app.MapGet("/api/v1/orchestrator/tasks/{id}", (string id, TaskStore tasks) =>
{
    AgentTaskDto? task = tasks.Get(id);
    return task != null ? Results.Ok(task) : Results.NotFound(new { error = "Task not found" });
});

app.MapPost("/api/v1/orchestrator/tasks/{id}/status", (string id, UpdateAgentTaskStatusRequest request, TaskStore tasks) =>
{
    try
    {
        if (tasks.UpdateStatus(id, request)) return Results.NoContent();
        return tasks.Get(id) is { } existing
            ? Results.Conflict(new { error = "Task is " + existing.Status + "; a cancelled task cannot change status." })
            : Results.NotFound(new { error = "Task not found" });
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/orchestrator/tasks/{id}/cancel", (string id, TaskStore tasks) =>
    tasks.UpdateStatus(id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled, Stage = "Cancelled" })
        ? Results.NoContent() : Results.NotFound(new { error = "Task not found" }));

app.MapGet("/api/v1/orchestrator/tasks/{id}/logs", (string id, TaskStore tasks) =>
    Results.Ok(new AgentTaskLogListResponse { Logs = tasks.GetLogs(id) }));

app.MapPost("/api/v1/orchestrator/tasks/{id}/logs", (string id, AppendAgentTaskLogRequest request, TaskStore tasks) =>
{
    if (tasks.Get(id) == null) return Results.NotFound(new { error = "Task not found" });
    tasks.AppendLog(id, request.Message);
    return Results.NoContent();
}).RequireDesktopOwner();

app.MapPost("/api/v1/orchestrator/tasks/{id}/worktree", (string id, SetAgentTaskWorktreeRequest request, TaskStore tasks) =>
    tasks.SetWorktreePath(id, request.WorktreePath) ? Results.NoContent() : Results.NotFound(new { error = "Task not found" })).RequireDesktopOwner();

app.MapPost("/api/v1/orchestrator/schedule", (AgentOrchestrator orchestrator) =>
    Results.Ok(new { dispatchedTaskIds = orchestrator.RunSchedulingPass() })).RequireDesktopOwner();

app.MapPost("/api/v1/automations", (CreateAutomationRequest request, AutomationStore automations) =>
{
    try { return Results.Ok(automations.Create(request)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/automations", (AutomationStore automations) =>
    Results.Ok(new AutomationListResponse { Automations = automations.List() }));

app.MapGet("/api/v1/automations/{id}", (string id, AutomationStore automations) =>
{
    AutomationDto? automation = automations.Get(id);
    return automation != null ? Results.Ok(automation) : Results.NotFound(new { error = "Automation not found" });
});

app.MapPost("/api/v1/automations/{id}/enabled", (string id, SetAutomationEnabledRequest request, AutomationStore automations) =>
    automations.SetEnabled(id, request.Enabled) ? Results.NoContent() : Results.NotFound(new { error = "Automation not found" }));

app.MapDelete("/api/v1/automations/{id}", (string id, AutomationStore automations) =>
    automations.Delete(id) ? Results.NoContent() : Results.NotFound(new { error = "Automation not found" }));

app.MapPost("/api/v1/automations/tick", async (PaRuX.Server.Automation.AutomationEngine engine, CancellationToken ct) =>
{
    await engine.RunTickAsync(ct);
    return Results.NoContent();
}).RequireDesktopOwner();

app.MapPost("/api/v1/context/entities", (CreateContextEntityRequest request, ContextGraphStore graph) =>
{
    try { return Results.Ok(graph.CreateEntity(request)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/context/entities", (string? type, ContextGraphStore graph) =>
    Results.Ok(new ContextEntityListResponse { Entities = graph.ListEntities(type) }));

app.MapGet("/api/v1/context/entities/{id}", (string id, ContextGraphStore graph) =>
{
    ContextEntityDto? entity = graph.GetEntity(id);
    return entity != null ? Results.Ok(entity) : Results.NotFound(new { error = "Entity not found" });
});

app.MapDelete("/api/v1/context/entities/{id}", (string id, ContextGraphStore graph) =>
    graph.DeleteEntity(id) ? Results.NoContent() : Results.NotFound(new { error = "Entity not found" })).RequireDesktopOwner();

app.MapGet("/api/v1/context/entities/{id}/neighborhood", (string id, ContextGraphStore graph) =>
{
    ContextNeighborhoodResponse? neighborhood = graph.GetNeighborhood(id);
    return neighborhood != null ? Results.Ok(neighborhood) : Results.NotFound(new { error = "Entity not found" });
});

app.MapPost("/api/v1/context/relations", (CreateContextRelationRequest request, ContextGraphStore graph) =>
{
    try { return Results.Ok(graph.CreateRelation(request)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapDelete("/api/v1/context/relations/{id}", (string id, ContextGraphStore graph) =>
    graph.DeleteRelation(id) ? Results.NoContent() : Results.NotFound(new { error = "Relation not found" })).RequireDesktopOwner();

app.MapPost("/api/v1/context/build", async (ContextBuildRequest request, ContextEngineService context, CancellationToken ct) =>
{
    try { return Results.Ok(await context.BuildAsync(request, ct)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (ConversationNotFoundException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/repo-index", (IndexRepositoryRequest request, RepoIndexService index) =>
{
    if (string.IsNullOrWhiteSpace(request.RepositoryPath) || !Directory.Exists(request.RepositoryPath))
        return Results.BadRequest(new { error = "repositoryPath does not exist." });
    return Results.Ok(index.IndexRepository(request.RepositoryPath));
}).RequireDesktopOwner();

app.MapPost("/api/v1/repo-index/search", (RepoContextSearchRequest request, RepoIndexService index) =>
{
    if (string.IsNullOrWhiteSpace(request.RepositoryPath))
        return Results.BadRequest(new { error = "repositoryPath is required." });
    return Results.Ok(index.SearchContext(request.RepositoryPath, request.GoalText, request.MaxFiles, request.MaxSymbols));
}).RequireDesktopOwner();

app.MapPost("/api/v1/artifacts", (CreateArtifactRequest request, ArtifactStore artifacts) =>
{
    try { return Results.Ok(artifacts.Create(request)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/artifacts", (string? type, ArtifactStore artifacts) =>
    Results.Ok(new ArtifactListResponse { Artifacts = artifacts.List(type) }));

app.MapGet("/api/v1/artifacts/{id}", (string id, ArtifactStore artifacts) =>
{
    ArtifactDto? artifact = artifacts.Get(id);
    return artifact != null ? Results.Ok(artifact) : Results.NotFound(new { error = "Artifact not found" });
});

app.MapPost("/api/v1/artifacts/{id}/status", (string id, SetArtifactStatusRequest request, ArtifactStore artifacts) =>
    artifacts.SetStatus(id, request.Status) ? Results.NoContent() : Results.NotFound(new { error = "Artifact not found" })).RequireDesktopOwner();

app.MapGet("/api/v1/artifacts/{id}/versions", (string id, ArtifactStore artifacts) =>
    Results.Ok(new ArtifactVersionListResponse { Versions = artifacts.ListVersions(id) }));

app.MapPost("/api/v1/artifacts/{id}/versions", (string id, AddArtifactVersionRequest request, ArtifactStore artifacts) =>
{
    try
    {
        ArtifactVersionDto? version = artifacts.AddVersion(id, request);
        return version != null ? Results.Ok(version) : Results.NotFound(new { error = "Artifact not found" });
    }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/artifacts/{id}/versions/{versionNumber:int}/restore", (string id, int versionNumber, ArtifactStore artifacts) =>
{
    ArtifactVersionDto? restored = artifacts.RestoreVersion(id, versionNumber);
    return restored != null ? Results.Ok(restored) : Results.NotFound(new { error = "Artifact or version not found" });
}).RequireDesktopOwner();

app.MapGet("/api/v1/registry/capabilities", async (UnifiedRegistryService registry, CancellationToken ct) =>
    Results.Ok(new UnifiedCapabilityListResponse { Capabilities = await registry.ListAsync(ct) }));

app.MapPost("/api/v1/projects", async (CreateProjectRequest request, ProjectManagerService pm, CancellationToken ct) =>
{
    try { return Results.Ok(await pm.CreateProjectAsync(request.Goal, ct)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

// The loopback-desktop creation path CreateDesktopProjectRequest documents: it carries the chosen
// coding permission profile, where the plain /api/v1/projects route above deliberately leaves
// mobile/LAN callers on the offline default. The desktop app and the CLI have always posted here,
// but the route was never mapped, so both got a bare 405 - the path matched the GET
// /api/v1/projects/{projectId} template and no method accepted it - and creating a Project was
// impossible from either client. Paired devices cannot reach this: their POST allowlist has
// "/api/v1/projects" exactly, plus "/{id}/tasks", neither of which matches this path.
app.MapPost("/api/v1/projects/desktop", async (CreateDesktopProjectRequest request, ProjectManagerService pm, CancellationToken ct) =>
{
    try { return Results.Ok(await pm.CreateProjectAsync(request.Goal, ct, request.CodingPermissionProfile, request.WorkspaceRoot)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/autopilot/runs", async (StartAutopilotRunRequest request, AutopilotService autopilot, CancellationToken ct) =>
{
    try { return Results.Ok(await autopilot.StartRunAsync(request.Goal, request.PermissionMode, ct)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapGet("/api/v1/autopilot/runs/{id}", (string id, AutopilotService autopilot) =>
{
    AutopilotRunDto? run = autopilot.GetRun(id);
    return run != null ? Results.Ok(run) : Results.NotFound(new { error = "Autopilot run not found" });
});

app.MapPost("/api/v1/autopilot/runs/{id}/approve", (string id, AutopilotService autopilot) =>
    autopilot.ApproveRun(id) ? Results.NoContent() : Results.BadRequest(new { error = "Run not found or not pending approval" }));

app.MapPost("/api/v1/eval/datasets", (CreateEvalDatasetRequest request, EvalStore evalStore) =>
{
    try { return Results.Ok(evalStore.CreateDataset(request)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/eval/datasets", (EvalStore evalStore) =>
    Results.Ok(new EvalDatasetListResponse { Datasets = evalStore.ListDatasets() }));

app.MapPost("/api/v1/eval/datasets/{id}/cases", (string id, CreateEvalCaseRequest request, EvalStore evalStore) =>
{
    try { return Results.Ok(evalStore.CreateCase(id, request)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/eval/datasets/{id}/cases", (string id, EvalStore evalStore) =>
    Results.Ok(new EvalCaseListResponse { Cases = evalStore.ListCases(id) }));

app.MapPost("/api/v1/eval/datasets/{id}/runs", async (string id, StartEvalRunRequest request,
    EvalRunnerService runner, SmartRouterService router, CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(request.Role))
    {
        RouteDecisionDto routed = await router.ResolveAsync(new RouteRequest { Role = request.Role }, ct);
        if (!routed.Success)
            return Results.BadRequest(new { error = "Could not route to role \"" + request.Role + "\": " + routed.Message });
    }
    EvalRunDto run = runner.StartRun(id);
    _ = runner.RunCasesAsync(run.Id, id, CancellationToken.None);
    return Results.Ok(run);
}).RequireDesktopOwner();

app.MapGet("/api/v1/eval/runs/{id}", (string id, EvalStore evalStore) =>
{
    EvalRunDto? run = evalStore.GetRun(id);
    return run != null ? Results.Ok(run) : Results.NotFound(new { error = "Eval run not found" });
});

app.MapGet("/api/v1/eval/runs/{id}/results", (string id, EvalStore evalStore) =>
    Results.Ok(new EvalCaseResultListResponse { Results = evalStore.ListCaseResults(id) }));

app.MapPost("/api/v1/autolab/experiments", async (CreateExperimentRequest request, AutoLabService autoLab, CancellationToken ct) =>
{
    try { return Results.Ok(await autoLab.CreateExperimentAsync(request, ct)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/autolab/experiments", (AutoLabService autoLab) => Results.Ok(autoLab.List()));

app.MapGet("/api/v1/autolab/experiments/{id}", (string id, AutoLabService autoLab) =>
{
    ExperimentDto? experiment = autoLab.Get(id);
    return experiment != null ? Results.Ok(experiment) : Results.NotFound(new { error = "Experiment not found" });
});

app.MapGet("/api/v1/autolab/compare", (string ids, AutoLabService autoLab) =>
    Results.Ok(autoLab.Compare(ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))));

app.MapPost("/api/v1/autolab/experiments/{id}/promote", (string id, PromoteExperimentRequest request, AutoLabService autoLab) =>
{
    try { return Results.Ok(autoLab.Promote(id, request.Role)); }
    catch (ManualSelectionGateBusyException exception) { return Results.Conflict(new { error = exception.Code }); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/autolab/sweep", async (CreateComparisonSweepRequest request, AutoLabService autoLab, CancellationToken ct) =>
{
    try { return Results.Ok(await autoLab.CreateComparisonSweepAsync(request, ct)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/observability/timeline", (int? limit, ObservabilityService observability) =>
    Results.Ok(new TimelineResponse { Entries = observability.GetTimeline(limit ?? 100) }));

app.MapPost("/api/v1/diff/text", (DiffRequest request, DiffService diff) =>
{
    try { return Results.Ok(diff.Compute(request.OldText, request.NewText)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/workflows", (CreateWorkflowRequest request, [FromServices] WorkflowService workflows) =>
{
    try { return Results.Ok(workflows.Create(request)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/workflows", ([FromServices] WorkflowService workflows) => Results.Ok(workflows.List()));

app.MapGet("/api/v1/workflows/{id}", (string id, [FromServices] WorkflowService workflows) =>
{
    WorkflowDto? workflow = workflows.Get(id);
    return workflow != null ? Results.Ok(workflow) : Results.NotFound(new { error = "Workflow not found" });
});

app.MapDelete("/api/v1/workflows/{id}", (string id, [FromServices] WorkflowService workflows) =>
    workflows.Delete(id) ? Results.NoContent() : Results.NotFound(new { error = "Workflow not found" })).RequireDesktopOwner();

app.MapPost("/api/v1/workflows/{id}/run", (string id, RunWorkflowRequest request, [FromServices] WorkflowService workflows) =>
{
    try { return Results.Ok(workflows.Run(id, request)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapGet("/api/v1/eval/runs/{id}/compare/{previousRunId}", (string id, string previousRunId, EvalStore evalStore) =>
    Results.Ok(evalStore.Compare(id, previousRunId)));

app.MapPost("/api/v1/system/stop-all", (PaRuX.Server.Control.StopAllService stopAll) => Results.Ok(stopAll.StopAll()));

app.MapPost("/api/v1/system/resume-automations", (PaRuX.Server.Control.StopAllService stopAll) =>
{
    stopAll.ResumeAutomations();
    return Results.NoContent();
});

app.MapGet("/api/v1/system/stop-all/status", (PaRuX.Server.Control.StopAllService stopAll) => Results.Ok(stopAll.GetStatus()));

app.MapGet("/api/v1/avatar/status", async (AvatarProviderService avatar, CancellationToken ct) =>
    Results.Ok(await avatar.GetStatusAsync(ct)));

app.MapGet("/api/v1/avatar/engine", async (AvatarEngineService engine, CancellationToken ct) =>
    Results.Ok(await engine.GetStatusAsync(ct)));

app.MapPost("/api/v1/avatar/engine/install", async (AvatarEngineService engine, CancellationToken ct) =>
{
    try
    {
        await engine.InstallEngineAsync(ct);
        return Results.Ok(await engine.GetStatusAsync(ct));
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/avatar/photo", async (AvatarPhotoRequest request, AvatarEngineService engine, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.PhotoBase64))
        return Results.BadRequest(new { error = "photoBase64 is required." });
    byte[] bytes;
    try { bytes = Convert.FromBase64String(request.PhotoBase64); }
    catch (FormatException) { return Results.BadRequest(new { error = "photoBase64 is not valid base64." }); }
    await engine.SetDefaultPhotoAsync(bytes, ct);
    return Results.Ok(await engine.GetStatusAsync(ct));
}).RequireDesktopOwner();

app.MapPost("/api/v1/avatar/generate", async (AvatarGenerateRequest request, AvatarProviderService avatar, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
        return Results.BadRequest(new { error = "text is required." });
    try
    {
        return Results.Ok(await avatar.GenerateAsync(request, ct));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/v1/training/engine", async (TrainingLabService training, CancellationToken ct) =>
    Results.Ok(await training.GetEngineStatusAsync(ct)));

// Removing the runtime is a desktop-owner action: it deletes several gigabytes of managed Python
// from the user's disk, and nothing paired over the LAN has any business doing that.
app.MapDelete("/api/v1/training/engine", async (
    TrainingLabService training,
    HttpContext context,
    CancellationToken ct) =>
{
    if (!IsDesktopOwner(context))
        return Results.Json(new { error = "Removing the training runtime is desktop-only." },
            statusCode: StatusCodes.Status403Forbidden);

    TrainingEngineRemovalResult result = await training.UninstallEngineAsync(ct);
    return result.Removed ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/v1/training/engine/install", async (TrainingLabService training, CancellationToken ct) =>
{
    try
    {
        string log = await training.InstallEngineAsync(ct);
        return Results.Ok(new { installed = true, log });
    }
    catch (TrainingRuntimeRefusalException exception)
    {
        return Results.Json(new { error = exception.Message, code = exception.Code }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireDesktopOwner();

// ---- Guided fine-tune: check before committing -------------------------------------------------
// A LoRA run loads a model, allocates VRAM and then trains for minutes or hours. Every way it can
// fail for a knowable reason - the dataset is the wrong shape for the method, the base model will
// not fit, the step count cannot see the data once - is decidable here in milliseconds. Returning
// blocking problems separately from warnings is the point: refusing on advice, or letting a certain
// failure through as advice, both turn a wizard into something users click past.

app.MapPost("/api/v1/training/plan", async (
    TrainingPlanRequest request, RuntimeManagerService runtimeManager, CancellationToken ct) =>
{
    if (request == null || string.IsNullOrWhiteSpace(request.TrainJsonlPath))
        return Results.BadRequest(new { error = "A training file is required." });
    if (!TrainingMethod.All.Contains(request.Method))
        return Results.BadRequest(new { error = "Unknown training method \"" + request.Method + "\"." });

    TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(request.TrainJsonlPath);

    RuntimeManagerStatus status = await runtimeManager.GetStatusAsync(ct);
    // A fine-tune runs where the accelerator is. With no capable GPU it runs on the CPU against
    // system RAM, and half the machine is what can honestly be promised to it.
    double availableGib = status.GpuDetected && status.VramTotalMib > 0
        ? status.VramTotalMib / 1024.0
        : status.RamTotalBytes / 1024.0 / 1024.0 / 1024.0 * 0.5;

    TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
        shape, request.Method, request.BaseModel,
        request.LoraRank, request.MaxSteps, request.LearningRate, availableGib);

    return Results.Ok(ToTrainingPlanResult(plan));
}).RequireDesktopOwner();

app.MapGet("/api/v1/training/jobs", (TrainingLabService training) => Results.Ok(training.List()));

app.MapGet("/api/v1/training/jobs/{id}", (string id, TrainingLabService training) =>
{
    TrainingJobStatus? job = training.Get(id);
    return job != null ? Results.Ok(job) : Results.NotFound(new { error = "Training job not found" });
});

app.MapPost("/api/v1/training/jobs", async (
    TrainingJobRequest request, TrainingLabService training,
    RuntimeManagerService runtimeManager, CancellationToken ct) =>
{
    // The same check the wizard runs, enforced here so it is not merely advisory. A dataset that is
    // the wrong shape for the method fails several minutes in, after a model has been loaded and
    // VRAM allocated, with a Python traceback; refusing it in milliseconds with the reason is
    // strictly better, and a caller that skipped the wizard should not get the worse outcome.
    if (request != null && !string.IsNullOrWhiteSpace(request.TrainJsonlPath))
    {
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(request.TrainJsonlPath);
        RuntimeManagerStatus hardware = await runtimeManager.GetStatusAsync(ct);
        double availableGib = hardware.GpuDetected && hardware.VramTotalMib > 0
            ? hardware.VramTotalMib / 1024.0
            : hardware.RamTotalBytes / 1024.0 / 1024.0 / 1024.0 * 0.5;

        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            shape, request.Method, request.BaseModel,
            request.LoraRank, request.MaxSteps, request.LearningRate, availableGib);

        if (!plan.CanStart)
        {
            // Warnings are deliberately not returned as a refusal: they describe a run that will
            // work but may disappoint, and that is the user's call to make.
            return Results.BadRequest(new
            {
                error = "This run cannot succeed as configured.",
                plan = ToTrainingPlanResult(plan)
            });
        }
    }

    try
    {
        return Results.Ok(training.CreateJob(request!));
    }
    catch (TrainingRuntimeRefusalException exception)
    {
        return Results.Json(new { error = exception.Message, code = exception.Code }, statusCode: StatusCodes.Status409Conflict);
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/training/jobs/{id}/cancel", (string id, TrainingLabService training) =>
    training.Cancel(id) ? Results.NoContent() : Results.NotFound(new { error = "Training job not found or already finished" })).RequireDesktopOwner();

app.MapGet("/api/v1/adapters", (AdapterManagerService adapters) => Results.Ok(adapters.List()));

app.MapGet("/api/v1/adapters/{id}", (string id, AdapterManagerService adapters) =>
{
    AdapterDto? adapter = adapters.Get(id);
    return adapter != null ? Results.Ok(adapter) : Results.NotFound(new { error = "Adapter not found" });
});

app.MapPost("/api/v1/adapters", (RegisterAdapterRequest request, AdapterManagerService adapters) =>
{
    try { return Results.Ok(adapters.Register(request)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/adapters/{id}/enabled", (string id, SetAdapterEnabledRequest request, AdapterManagerService adapters) =>
    adapters.SetEnabled(id, request.Enabled) ? Results.NoContent() : Results.NotFound(new { error = "Adapter not found" })).RequireDesktopOwner();

app.MapPost("/api/v1/adapters/{id}/rename", (string id, RenameAdapterRequest request, AdapterManagerService adapters) =>
{
    try { return adapters.Rename(id, request.Name) ? Results.NoContent() : Results.NotFound(new { error = "Adapter not found" }); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/adapters/{id}/export", (string id, ExportAdapterRequest request, AdapterManagerService adapters) =>
{
    try { return Results.Ok(new { destination = adapters.Export(id, request.DestinationDirectory) }); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict); }
}).RequireDesktopOwner();

app.MapDelete("/api/v1/adapters/{id}", (string id, bool? deleteFiles, AdapterManagerService adapters) =>
{
    try
    {
        bool removed = deleteFiles == true ? adapters.DeleteWithFiles(id) : adapters.Delete(id);
        return removed ? Results.NoContent() : Results.NotFound(new { error = "Adapter not found" });
    }
    catch (InvalidOperationException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/adapters/conversion/status", async (AdapterManagerService adapters, CancellationToken ct) =>
    Results.Ok(await adapters.GetConversionEngineStatusAsync(ct)));

app.MapPost("/api/v1/adapters/conversion/install", async (AdapterManagerService adapters, CancellationToken ct) =>
{
    try { return Results.Ok(new { log = await adapters.InstallConversionEngineAsync(ct) }); }
    catch (InvalidOperationException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/adapters/{id}/convert", async (string id, ConvertAdapterRequest request, AdapterManagerService adapters, CancellationToken ct) =>
{
    try { return Results.Ok(await adapters.ConvertToGgufAsync(id, request.Outtype, ct, request.BaseModelConfigDir)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/adapters/{id}/merge", async (string id, MergeAdapterRequest request, AdapterManagerService adapters, CancellationToken ct) =>
{
    try { return Results.Ok(new { outputDirectory = await adapters.MergeToBaseModelAsync(id, request.OutputDirectory, ct, request.BaseModelDir) }); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/speech/engine", async (SpeechService speech, CancellationToken ct) =>
    Results.Ok(await speech.GetStatusAsync(ct)));

app.MapPost("/api/v1/speech/engine/install", async (SpeechService speech, CancellationToken ct) =>
{
    try
    {
        string log = await speech.InstallEngineAsync(ct);
        return Results.Ok(new { installed = true, log });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/speech/transcribe", async (TranscribeRequest request, SpeechService speech, CancellationToken ct) =>
{
    try { return Results.Ok(await speech.TranscribeAsync(request, ct)); }
    catch (TextRuntimeStateUnavailableException unavailable) { return TextRuntimeUnavailableResponse.Result(unavailable); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
});

app.MapPost("/api/v1/speech/synthesize", async (SynthesizeRequest request, SpeechService speech, CancellationToken ct) =>
{
    try { return Results.Ok(await speech.SynthesizeAsync(request, ct)); }
    catch (TextRuntimeStateUnavailableException unavailable) { return TextRuntimeUnavailableResponse.Result(unavailable); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
});

app.MapGet("/api/v1/models/search", async (string query, int? limit, string? sort, ModelDownloadService models, CancellationToken ct) =>
{
    // An unknown sort key is refused with 400 rather than silently ignored.
    try { return Results.Ok(await models.SearchAsync(query, limit ?? 15, ct, sort)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

// Desktop control-plane routes. Paired-device allowlisting remains deny-by-default.
app.MapGet("/api/v1/models/discovery", (ModelDiscoveryService discovery) =>
{
    try { return Results.Ok(discovery.GetCatalog()); }
    catch (InvalidDataException)
    {
        return Results.Conflict(new { error = "The model discovery catalog needs repair before it can be used." });
    }
});

app.MapGet("/api/v1/models/discovery/policy", (ModelDiscoveryPolicyStore policies) =>
    Results.Ok(policies.Load())).RequireDesktopOwner();

app.MapPut("/api/v1/models/discovery/policy", (
    ModelDiscoveryPolicySettings settings, ModelDiscoveryPolicyStore policies) =>
{
    try
    {
        policies.Save(settings);
        return Results.Ok(policies.Load());
    }
    catch (InvalidDataException)
    {
        return Results.BadRequest(new { error = "invalid_model_discovery_policy" });
    }
}).RequireDesktopOwner().WithMetadata(new RequestSizeLimitAttribute(ModelDiscoveryPolicyStore.MaximumDocumentBytes));

app.MapPost("/api/v1/models/discovery/refresh", async (ModelDiscoveryService discovery, CancellationToken ct) =>
{
    try
    {
        ModelDiscoveryRefreshResult result = await discovery.RefreshAsync(ct);
        return result.Outcome == "failed"
            ? Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable)
            : result.Outcome == "cooldown"
                ? Results.Json(result, statusCode: StatusCodes.Status429TooManyRequests)
                : Results.Ok(result);
    }
    catch (InvalidDataException)
    {
        return Results.Conflict(new { error = "The model discovery catalog needs repair before it can be refreshed." });
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/models/discovery/refresh-explicit", async (
    HttpContext context, ModelDiscoveryService discovery, CancellationToken ct) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    const int maxBodyBytes = 1024;
    if (context.Request.ContentLength > maxBodyBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    byte[] body = new byte[maxBodyBytes + 1];
    int length = 0;
    while (length < body.Length)
    {
        int read = await context.Request.Body.ReadAsync(body.AsMemory(length), ct);
        if (read == 0) break;
        length += read;
    }
    if (length > maxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    ModelDiscoveryExplicitRefreshRequest? request;
    try
    {
        using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(
            body.AsMemory(0, length), new System.Text.Json.JsonDocumentOptions { MaxDepth = 4 });
        if (parsed.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
            || parsed.RootElement.EnumerateObject().Count() != 1
            || !parsed.RootElement.TryGetProperty("repositoryIds", out _))
            return Results.BadRequest(new { error = "invalid_repository_ids" });
        request = System.Text.Json.JsonSerializer.Deserialize<ModelDiscoveryExplicitRefreshRequest>(
            body.AsSpan(0, length), new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web)
            {
                MaxDepth = 4,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            });
    }
    catch (System.Text.Json.JsonException)
    { return Results.BadRequest(new { error = "invalid_repository_ids" }); }
    if (request is null || !ModelDiscoveryService.TryValidateExplicitRepositoryIds(
            request.RepositoryIds, out _))
        return Results.BadRequest(new { error = "invalid_repository_ids" });
    try
    {
        ModelDiscoveryRefreshResult result = await discovery.RefreshExplicitAsync(request, ct);
        return result.Outcome == "failed"
            ? Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable)
            : result.Outcome == "cooldown"
                ? Results.Json(result, statusCode: StatusCodes.Status429TooManyRequests)
                : result.Outcome == "refused"
                    ? Results.Conflict(result)
                : Results.Ok(result);
    }
    catch (InvalidDataException)
    { return Results.Conflict(new { error = "The model discovery catalog needs repair before it can be refreshed." }); }
});

app.MapPost("/api/v1/models/discovery/{key}/ignore", async (
    string key, ModelDiscoveryService discovery, CancellationToken ct) =>
{
    if (!ModelDiscoveryIdentity.IsSafeKey(key))
        return Results.BadRequest(new { error = "A 64-character lowercase Candidate key is required." });
    try
    {
        return await discovery.IgnoreAsync(key, ct)
            ? Results.Ok(new { key, disposition = "ignored" })
            : Results.NotFound(new { error = "Candidate not found." });
    }
    catch (InvalidDataException)
    {
        return Results.Conflict(new { error = "The model discovery catalog needs repair before it can be changed." });
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/models/discovery/{key}/notice/later", async (
    string key, ModelDiscoveryService discovery, CancellationToken ct) =>
{
    if (!ModelDiscoveryIdentity.IsSafeKey(key))
        return Results.BadRequest(new { error = "A 64-character lowercase Candidate key is required." });
    try
    {
        DateTime? deferredUntilUtc = await discovery.DeferNoticeAsync(key, ct);
        return deferredUntilUtc is { } deferred
            ? Results.Ok(new { key, noticePending = false, deferredUntilUtc = deferred })
            : Results.NotFound(new { error = "Active Candidate not found." });
    }
    catch (InvalidDataException)
    {
        return Results.Conflict(new { error = "The model discovery catalog needs repair before it can be changed." });
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/models/discovery/groups/choice", async (
    ModelDiscoveryGroupChoiceRequest request, ModelDiscoveryService discovery, CancellationToken ct) =>
{
    try
    {
        ModelDiscoveryGroupNoticeStore.Row? row = request is null ? null
            : await discovery.SetGroupChoiceAsync(request.GroupKey, request.Choice, ct);
        return row is null
            ? Results.BadRequest(new { error = "A known model group and a choice of evaluate, later or ignore are required." })
            : Results.Ok(new { groupKey = row.GroupKey, choice = row.Choice, laterUntilUtc = row.LaterUntilUtc });
    }
    catch (InvalidDataException)
    {
        return Results.Conflict(new { error = "The model discovery state needs repair before it can be changed." });
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/models/discovery/{key}/notice/handled", async (
    string key, ModelDiscoveryService discovery, CancellationToken ct) =>
{
    if (!ModelDiscoveryIdentity.IsSafeKey(key))
        return Results.BadRequest(new { error = "A 64-character lowercase Candidate key is required." });
    try
    {
        return await discovery.MarkNoticeHandledAsync(key, ct)
            ? Results.Ok(new { key, noticePending = false })
            : Results.NotFound(new { error = "Active Candidate not found." });
    }
    catch (InvalidDataException)
    {
        return Results.Conflict(new { error = "The model discovery catalog needs repair before it can be changed." });
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/models/discovery/{key}/downloads", async (
    string key, StartCandidateDownloadRequest request, ModelDiscoveryService discovery,
    ModelDownloadService models, LocalRuntimeService runtime, HttpContext context, CancellationToken ct) =>
{
    if (runtime.GamingMode)
        return Results.Conflict(new { error = "AI paused by Gaming — Return to AI." });
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.OtherFields is { Count: > 0 } || !ModelDiscoveryIdentity.IsSafeKey(key)
        || request.LicenseEvidence is null)
        return Results.BadRequest(new { error = "invalid_candidate_download_request" });

    ModelDiscoveryCatalog catalog;
    try { catalog = discovery.GetCatalog(); }
    catch (InvalidDataException)
    {
        return Results.Conflict(new { error = "discovery_catalog_invalid" });
    }

    ModelDiscoveryCandidate? candidate = catalog.Candidates.FirstOrDefault(item =>
        string.Equals(item.Key, key, StringComparison.Ordinal));
    if (candidate is null) return Results.NotFound(new { error = "candidate_not_found" });
    if (candidate.Lifecycle != "Candidate" || candidate.Disposition != "active")
        return Results.Conflict(new { error = "candidate_not_active" });
    if (catalog.LastSuccessUtc is not DateTime metadataObservedUtc
        || request.LicenseEvidence.MetadataObservedUtc != metadataObservedUtc)
        return Results.Conflict(new { error = "candidate_metadata_snapshot_changed" });

    try
    {
        return Results.Ok(await models.CreateCandidateDownloadAsync(
            candidate, request.LicenseEvidence, metadataObservedUtc, ct));
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { error = exception.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException)
    {
        return Results.Json(new { error = "candidate_metadata_unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).WithMetadata(new RequestSizeLimitAttribute(1024));

app.MapPost("/api/v1/models/discovery/{key}/evaluation-jobs", (
    string key, StartCandidateEvaluationJobRequest request, CandidateEvaluationJobService jobs,
    LocalRuntimeService runtime, HttpContext context) =>
{
    if (runtime.GamingMode)
        return Results.Conflict(new { error = "AI paused by Gaming — Return to AI." });
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.OtherFields is { Count: > 0 } || !ModelDiscoveryIdentity.IsSafeKey(key)
        || !ModelDownloadProvenanceManifestStore.IsSafeFileName(request.ManagedFileName)
        || (request.QualityProfileId is not null
            && !CandidateEvaluationProfileCatalog.TryResolveQualityProfileId(request.QualityProfileId, out _)))
        return Results.BadRequest(new { error = "invalid_candidate_or_filename" });
    try
    {
        CandidateEvaluationStartResult result = jobs.TryStart(key, request.ManagedFileName,
            context.RequestAborted, request.QualityProfileId);
        return result.Outcome switch
        {
            "Accepted" => Results.Accepted("/api/v1/models/evaluation-jobs/" + result.JobId,
                new CandidateEvaluationJobAccepted { JobId = result.JobId! }),
            "GamingActive" => Results.Conflict(new { error = "AI paused by Gaming — Return to AI." }),
            "Busy" => Busy(),
            "NotFound" => Results.NotFound(new { error = "candidate_not_found" }),
            "LicenseEvidenceMissing" => Results.Conflict(new { error = "candidate_license_evidence_missing" }),
            "LicenseMetadataChanged" => Results.Conflict(new { error = "candidate_license_metadata_changed" }),
            "LicenseReviewRequired" => Results.Conflict(new { error = "candidate_license_review_required" }),
            "ProvenanceUnavailable" => Results.Conflict(new { error = "candidate_provenance_unavailable" }),
            "Stopping" => Results.Json(new { error = "server_stopping" },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            "StorageUnavailable" => Results.Json(new { error = "evaluation_history_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.BadRequest(new { error = "invalid_candidate_or_filename" })
        };
    }
    catch (InvalidDataException)
    {
        return Results.Conflict(new { error = "discovery_catalog_invalid" });
    }

    IResult Busy()
    {
        context.Response.Headers.RetryAfter = "5";
        return Results.Conflict(new { error = "evaluation_busy" });
    }
}).WithMetadata(new RequestSizeLimitAttribute(1024));

app.MapGet("/api/v1/models/evaluation-jobs", (CandidateEvaluationJobService jobs, HttpContext context) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(jobs.List()); }
    catch (InvalidDataException)
    {
        return Results.Json(new { error = "evaluation_history_unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/v1/models/evaluation-jobs/{id}", (string id,
    CandidateEvaluationJobService jobs, HttpContext context) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        return Guid.TryParseExact(id, "N", out _)
            && jobs.Get(id) is { } status ? Results.Ok(status) : Results.NotFound();
    }
    catch (InvalidDataException)
    {
        return Results.Json(new { error = "evaluation_history_unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/v1/models/evaluation-jobs/{id}/cancel", (string id,
    CandidateEvaluationJobService jobs, HttpContext context) =>
{
    if (!IsLoopback(context) || context.Items["PaRuXOwner"] as string != "desktop")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        return Guid.TryParseExact(id, "N", out _)
            && jobs.Cancel(id) is { } status ? Results.Ok(status) : Results.NotFound();
    }
    catch (InvalidDataException)
    {
        return Results.Json(new { error = "evaluation_history_unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/v1/models/license", async (string repoId, ModelDownloadService models, CancellationToken ct) =>
    Results.Ok(await models.GetLicenseAsync(repoId, ct)));

app.MapGet("/api/v1/models/details", async (string repoId, ModelDownloadService models, CancellationToken ct) =>
    Results.Ok(await models.GetDetailsAsync(repoId, ct)));

app.MapGet("/api/v1/models/recommended", () => Results.Ok(RecommendedModelsService.List));

// The same curated list, ranked for the machine this is running on, with a sentence per model
// explaining the verdict. The first-run flow needs an answer to "which one?", and a search box is
// not an answer for somebody who has never downloaded a model.
app.MapGet("/api/v1/models/recommended/for-this-pc", async (
    RuntimeManagerService runtime, ModelDownloadService models, ComponentInventoryService components,
    CancellationToken ct) =>
{
    // Constructed here rather than injected: GpuInventoryService is not in the DI container - the
    // hardware endpoints new it up too - and a minimal-API parameter of an unregistered type is
    // inferred as a request body, which takes the whole route table down at startup.
    var gpuInventory = new GpuInventoryService();

    RuntimeManagerStatus? status = null;
    GpuInventoryStatus? gpu = null;
    IReadOnlyList<ManagedModelInfo>? localModels = null;

    // A machine that cannot report its hardware still gets suggestions, flagged as generic in the
    // summary - better than an error on the very first screen somebody sees.
    try
    {
        status = await runtime.GetStatusAsync(ct);
        gpu = gpuInventory.GetStatus();
        localModels = models.ListManagedModels();
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { }

    // Real ownership and per-backend build tag from the component inventory; unknown stays null (never assumed).
    InstalledRuntime? installedRuntime = null;
    try
    {
        installedRuntime = InstalledRuntimeResolver.Resolve(await components.DiscoverAsync(ct),
            b => LocalAiPaths.GetActiveRuntimeBuildTag(b) ?? LocalAiPaths.DefaultRuntimeBuildTag(b))?.Runtime;
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { }
    return Results.Ok(FirstModelAdvisor.Advise(status, gpu, localModels, installedRuntime));
});

app.MapPost("/api/v1/models/roles/auto-assign", async (
    RuntimeManagerService runtime, ModelDownloadService models, CancellationToken ct) =>
{
    var gpuInventory = new GpuInventoryService();

    RuntimeManagerStatus? status = null;
    GpuInventoryStatus? gpu = null;
    IReadOnlyList<ManagedModelInfo> localModels = Array.Empty<ManagedModelInfo>();

    try
    {
        status = await runtime.GetStatusAsync(ct);
        gpu = gpuInventory.GetStatus();
        localModels = models.ListManagedModels();
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { }

    RoleAutoAssignResponse result = ModelRoleAutoAssigner.AutoAssignRoles(localModels, status, gpu);
    return Results.Ok(result);
}).RequireDesktopOwner();

AutoMaintenanceEndpoints.Map(app);
MediaCatalogueEndpoints.Map(app);
MediaLaneEndpoints.Map(app);
RuntimeSuggestionEndpoints.Map(app);

app.MapGet("/api/v1/models/budget", async (ModelDownloadService models) => Results.Ok(await models.GetBudgetAsync()));

app.MapGet("/api/v1/models/downloads", (ModelDownloadService models) => Results.Ok(models.List()));

app.MapGet("/api/v1/models/library", (ModelDownloadService models, ModelBenchmarkService benchmarks) =>
{
    IReadOnlyList<ManagedModelInfo> library = models.ListManagedModels();
    Dictionary<string, ModelBenchmarkSummary> byPath = benchmarks.ListModels()
        .GroupBy(b => b.ModelPath, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    foreach (ManagedModelInfo model in library)
    {
        if (!byPath.TryGetValue(model.FilePath, out ModelBenchmarkSummary? summary)) continue;
        model.LastBenchmarkTokensPerSecond = summary.AverageTokensPerSecond;
        model.LastBenchmarkUtc = summary.LastRun?.CompletedUtc;
    }
    return Results.Ok(library);
});

app.MapDelete("/api/v1/models/library/{fileName}", (string fileName, ModelDownloadService models) =>
{
    try { return models.DeleteManagedModel(fileName) ? Results.NoContent() : Results.NotFound(new { error = "Managed model not found" }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/models/library/import", (ImportManagedModelRequest request, ModelDownloadService models) =>
{
    try { return Results.Ok(models.ImportManagedModel(request.SourcePath, request.FileName)); }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/models/library/{fileName}/alias", (string fileName, SetManagedModelAliasRequest request, ModelDownloadService models) =>
{
    try { return Results.Ok(models.SetAlias(fileName, request.Alias)); }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/models/downloads/{id}", (string id, ModelDownloadService models) =>
{
    ModelDownloadStatus? status = models.Get(id);
    return status != null ? Results.Ok(status) : Results.NotFound(new { error = "Download not found" });
});

app.MapPost("/api/v1/models/downloads", async (ModelDownloadRequest request, ModelDownloadService models, CancellationToken ct) =>
{
    try { return Results.Ok(await models.CreateDownloadAsync(request, ct)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict); }
}).RequireDesktopOwnerForDestructiveModelDownload();

// Split (multi-part) GGUF sets: the plan is read-only; the start refuses an incomplete set and never
// accepts a client-supplied URL, because the server re-plans the set from the repository itself.
app.MapGet("/api/v1/models/download-plan", async (string repoId, string? quantization, string? setName, string? directory, int? partCount, ModelDownloadService models, CancellationToken ct) =>
{
    try { return Results.Ok(await models.PlanModelDownloadAsync(repoId, quantization, ct, setName, directory, partCount)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapPost("/api/v1/models/download-plan/start", async (ModelDownloadPlanRequest request, ModelDownloadService models, CancellationToken ct) =>
{
    try { return Results.Ok(await models.StartModelDownloadPlanAsync(
        request.RepoId, request.Quantization, request.LicenseEvidence, ct,
        request.SetName, request.Revision, request.Directory, request.PartCount)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict); }
});

// Incomplete split sets are listed here, not in /api/v1/models/library, so the library keeps offering
// only models that can be loaded while the UI is still being taught to show the missing parts.
app.MapGet("/api/v1/models/library/split-sets", (ModelDownloadService models) =>
    Results.Ok(models.ListManagedLibrary().Where(m => m.SplitSet is { Complete: false }).ToList()));

// Models that Ollama and LM Studio already store. Discovery only reads. Import hard-links the file and
// copies only with explicit consent. Both are desktop-only: they expose and touch local file paths.
app.MapGet("/api/v1/models/library/external", () =>
    Results.Ok(ExternalModelStoreImporter.Discover(ExternalModelStoreImporter.DefaultRoots(), ModelDownloadService.ModelsDirectory))).RequireDesktopOwner();

// Per-store report: found counts, or an honest reason a store was not recognised. Read-only.
app.MapGet("/api/v1/models/library/external/stores", () =>
    Results.Ok(ExternalModelStoreImporter.Inspect(ExternalModelStoreImporter.DefaultRoots(), ModelDownloadService.ModelsDirectory))).RequireDesktopOwner();

ExternalModelImportRoute.Map(app, ExternalModelStoreImporter.DefaultRoots, () => ModelDownloadService.ModelsDirectory)
    .RequireDesktopOwner();

app.MapPost("/api/v1/models/downloads/{id}/cancel", (string id, ModelDownloadService models) =>
    models.Cancel(id) ? Results.NoContent() : Results.NotFound(new { error = "Download not found or already finished" }));

// Pause keeps the partial and resumable; it is distinct from cancel (which also ends the job).
app.MapPost("/api/v1/models/downloads/{id}/pause", (string id, ModelDownloadService models) =>
    models.Pause(id) ? Results.NoContent() : Results.NotFound(new { error = "Download not found or not queued, downloading or retrying" }));

// The Hugging Face token is write-only: no endpoint returns, echoes or logs it.
app.MapGet("/api/v1/models/download-credentials", (ModelDownloadService models) => Results.Ok(models.GetHubCredentialStatus()));

app.MapPut("/api/v1/models/download-credentials", (ModelDownloadCredentialUpdate update, ModelDownloadService models) =>
{
    try
    {
        models.SetHubToken(update.Token);
        return Results.NoContent();
    }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict); }
}).RequireDesktopOwner();

app.MapDelete("/api/v1/models/download-credentials", (ModelDownloadService models) =>
{
    try
    {
        return models.ClearHubToken()
            ? Results.NoContent()
            : Results.NotFound(new { error = "No Hugging Face access token is saved" });
    }
    catch (IOException)
    {
        return Results.Problem(
            title: "Unable to clear the Hugging Face token.",
            statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Problem(
            title: "Unable to clear the Hugging Face token.",
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireDesktopOwner();

// One shared limit for every model transfer; zero means unlimited. Changes apply to the next chunk at once.
app.MapGet("/api/v1/models/download-settings", (ModelDownloadService models) => Results.Ok(models.GetTransferSettings()));

app.MapPut("/api/v1/models/download-settings", (ModelDownloadTransferSettings settings, ModelDownloadService models) =>
{
    try
    {
        models.SetBandwidthLimitMegabytesPerSecond(settings.BandwidthLimitMegabytesPerSecond);
        return Results.NoContent();
    }
    catch (ArgumentOutOfRangeException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapDelete("/api/v1/models/downloads/{id}", (string id, ModelDownloadService models) =>
    models.Remove(id) ? Results.NoContent() : Results.NotFound(new { error = "Download not found, or it is not paused/failed (cancel it first)." })).RequireDesktopOwner();

app.MapGet("/api/v1/models/benchmarks/models", (ModelBenchmarkService benchmarks) =>
    Results.Ok(benchmarks.ListModels()));

app.MapGet("/api/v1/models/benchmarks/runs", (string? modelPath, ModelBenchmarkService benchmarks) =>
    Results.Ok(benchmarks.ListRuns(modelPath)));

app.MapGet("/api/v1/models/benchmarks/{id}", (string id, ModelBenchmarkService benchmarks) =>
    benchmarks.Get(id) is { } run ? Results.Ok(run) : Results.NotFound(new { error = "Benchmark run not found" }));

app.MapPost("/api/v1/models/benchmarks", (ModelBenchmarkRequest request, ModelBenchmarkService benchmarks) =>
{
    try { return Results.Accepted("/api/v1/models/benchmarks", benchmarks.Start(request)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (FileNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/models/benchmarks/{id}/cancel", (string id, ModelBenchmarkService benchmarks) =>
    benchmarks.Cancel(id) ? Results.Accepted() : Results.NotFound(new { error = "Active benchmark run not found" })).RequireDesktopOwner();

static ModelRoleStatus DescribeTextRole(string role, string? activeRole, RuntimeManagerStatus? runtime = null)
{
    string? modelPath = LocalAiPaths.GetRoleModelPathOrNull(role);
    string? roleOverride = LocalAiPaths.GetRoleOverride(role);
    string? overrideOrigin = roleOverride is null ? null : LocalAiPaths.GetRoleOverrideOrigin(role);
    bool overridden = roleOverride != null;
    bool isActive = activeRole != null && string.Equals(activeRole, role, StringComparison.OrdinalIgnoreCase);
    var status = new ModelRoleStatus
    {
        Role = role, Kind = "text", ModelPath = modelPath ?? string.Empty, IsOverridden = overridden,
        OverrideOrigin = overrideOrigin,
        OverrideOriginReason = overrideOrigin == RoleOverrideOrigin.UnknownLegacy
            ? RoleOverrideOrigin.UnknownLegacyReason
            : null,
        FileExists = modelPath != null && File.Exists(modelPath), HasVisionSupport = modelPath != null && !overridden,
        IsActive = isActive
    };

    // Only the active role can have observed runtime state, and only when a runtime status was
    // actually read (the POST handlers describe a role right after changing its assignment, with
    // no live process to observe). Everything below is what IS loaded, not what was configured.
    if (!isActive || runtime == null) return status;

    status.LoadedModelPath = runtime.ActiveModelPath;
    status.LoadedHasVisionSupport = runtime.ActiveHasVisionSupport;
    ActiveModelSubstitution.Result substitution = ActiveModelSubstitution.Evaluate(
        modelPath, runtime.ActiveModelPath, status.HasVisionSupport,
        runtime.ActiveHasVisionSupport, runtime.GpuDetected);
    status.IsSubstituted = substitution.IsSubstituted;
    status.Note = substitution.Note;
    return status;
}

static ModelRoleStatus DescribeMediaRole(string role, string checkpointType)
{
    // The checkpoint the next job actually loads (template default or override), computed by the same
    // resolver the graph builder uses. Previously this reported the alphabetically first diffusion file.
    MediaCheckpointEffective effective = MediaCheckpointResolution.Resolve(checkpointType);
    List<string> available = LocalAiPaths.ListDiffusionModelCheckpoints();
    string current = effective.File;
    return new ModelRoleStatus
    {
        Role = role, Kind = "media", ModelPath = current, IsOverridden = effective.IsOverridden,
        FileExists = !string.IsNullOrEmpty(current) && available.Contains(current, StringComparer.OrdinalIgnoreCase),
        HasVisionSupport = false, IsActive = false
    };
}

static ModelRoleStatus DescribeFixedEngineRole(string role, string note) => new()
{
    Role = role, Kind = "fixed-engine", ModelPath = string.Empty, IsOverridden = false,
    FileExists = false, HasVisionSupport = false, IsActive = false, Note = note
};

static ModelRoleStatus DescribeNotImplementedRole(string role) => new()
{
    Role = role, Kind = "not-implemented", ModelPath = string.Empty, IsOverridden = false,
    FileExists = false, HasVisionSupport = false, IsActive = false,
    Note = "No local engine implements this role yet."
};

// Answers "how long a conversation can this model hold on this machine, and can I make it longer?"
// - computed from the model's own declared attention geometry against the memory really available,
// never from a proportion of its file size. Reports Known=false rather than a guess for a file that
// does not declare enough, because a fabricated context recommendation is precisely the number that
// gets trusted and then causes the out-of-memory it was meant to prevent.
app.MapGet("/api/v1/models/context-capacity", async (
    string? path, string? role, RuntimeManagerService runtimeManager, CancellationToken ct) =>
{
    string? modelPath = path;
    if (string.IsNullOrWhiteSpace(modelPath) && !string.IsNullOrWhiteSpace(role))
    {
        string? normalized = LocalAiPaths.NormalizeTextRole(role);
        if (normalized == null)
            return Results.BadRequest(new { error = "Unknown role \"" + role + "\"." });
        modelPath = LocalAiPaths.GetRoleModelPathOrNull(normalized);
    }
    if (string.IsNullOrWhiteSpace(modelPath))
        return Results.BadRequest(new { error = "Pass either ?path= a model file or ?role= a text role that has one assigned." });
    if (!File.Exists(modelPath))
        return Results.NotFound(new { error = "No model file at " + modelPath + "." });

    RuntimeManagerStatus status = await runtimeManager.GetStatusAsync(ct);
    // Whatever the runtime can really place the model in: VRAM when a capable GPU is present,
    // otherwise system RAM, which is where a no-GPU machine actually runs.
    long availableBytes = status.GpuDetected && status.VramTotalMib > 0
        ? (long)status.VramTotalMib * 1024 * 1024
        : status.RamTotalBytes;
    long weightsBytes;
    try { weightsBytes = new FileInfo(modelPath).Length; }
    catch { return Results.NotFound(new { error = "Could not read " + modelPath + "." }); }

    GgufModelInfo model = GgufMetadataReader.Read(modelPath);
    ContextCapacityPlanner.ContextCapacity capacity =
        ContextCapacityPlanner.Plan(model, availableBytes, weightsBytes);

    return Results.Ok(new ModelContextCapacity
    {
        Known = capacity.Known,
        ModelPath = modelPath,
        DeclaredMaximum = capacity.DeclaredMaximum,
        MemoryLimited = capacity.MemoryLimited,
        Recommended = capacity.Recommended,
        KvCacheBytesAtRecommended = capacity.KvCacheBytesAtRecommended,
        AvailableContextLengths = ContextCapacityPlanner.AvailableSteps(capacity).ToList(),
        Explanation = capacity.Explanation
    });
});


// ---- Adjustable context ----------------------------------------------------------------------
// The capacity endpoint above answers "what fits". These answer "what can I have, and give me it".
// Everything a caller can select here is a configuration PaRuX can really launch: the options are
// produced by the same planner the launch reads back, so a setting offered by the UI cannot fail at
// load time, and one that is out of reach is refused with the best available named rather than
// rounded down to something the user did not ask for.

app.MapGet("/api/v1/models/context-options", async (
    string? path, string? role, RuntimeManagerService runtimeManager,
    ISystemRamSnapshotProvider ramSnapshotProvider,
    IGpuPerformanceTelemetrySnapshotProvider gpuTelemetryProvider, CancellationToken ct) =>
{
    (string? modelPath, IResult? failure) = ResolveContextModelPath(path, role);
    if (failure != null) return failure;

    SystemRamSnapshot? ramSnapshot = ramSnapshotProvider.TryGetSnapshot();
    RuntimeManagerStatus status = await runtimeManager.GetStatusAsync(ct, ramSnapshot);
    long currentRamBudgetBytes = runtimeManager.GetCurrentSystemRamBudgetBytes(modelPath!, ramSnapshot);
    RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(
        status, ramSnapshot?.TotalBytesAsLong ?? 0);
    ContextMemoryBudget.Budget budget = await ContextMemoryBudget.ForCurrentResourcesAsync(
        status, hardware, currentRamBudgetBytes, ramSnapshot?.TotalBytesAsLong ?? 0,
        gpuTelemetryProvider, ct);

    long weightsBytes;
    try { weightsBytes = new FileInfo(modelPath!).Length; }
    catch { return Results.NotFound(new { error = "Could not read " + modelPath + "." }); }

    GgufModelInfo model = GgufMetadataReader.Read(modelPath!);
    IReadOnlyList<ContextExpansionPlanner.ContextOption> options =
        ContextExpansionPlanner.Explore(model, budget.VramBytes, budget.OffloadableRamBytes, weightsBytes);

    ContextPreferenceStore.ContextPreference? stored = ContextPreferenceStore.Get(modelPath!);

    return Results.Ok(new ModelContextOptions
    {
        Known = options.Count > 0,
        ModelPath = modelPath!,
        DeclaredMaximum = model.ContextLength ?? 0,
        Options = options.Select(ToContextOptionDto).ToList(),
        Selected = stored == null ? null : new ModelContextOption
        {
            ContextLength = stored.ContextLength,
            CacheType = stored.CacheType,
            UsesSystemRam = stored.UsesSystemRam,
            RopeScale = stored.RopeScale,
            KvCacheBytes = KvCacheSizeEstimator.Estimate(
                model, stored.ContextLength, stored.CacheType, stored.CacheType) ?? 0,
            Levers = new List<string>(),
            Cost = "Currently selected."
        },
        Explanation = options.Count > 0
            ? "Each option is a configuration PaRuX can launch on this machine."
            : model.ContextLength is > 0
                // The geometry is there; the machine is what cannot hold it.
                ? "This model declares a context of " + (model.ContextLength ?? 0).ToString("N0")
                  + " tokens, but its weights leave no room for a conversation on this machine."
                : "This model's file does not declare the attention geometry needed to work out what it can hold."
    });
});

app.MapPut("/api/v1/models/context-preference", async (
    ModelContextPreferenceRequest request, RuntimeManagerService runtimeManager,
    ISystemRamSnapshotProvider ramSnapshotProvider,
    IGpuPerformanceTelemetrySnapshotProvider gpuTelemetryProvider, CancellationToken ct) =>
{
    if (request == null || string.IsNullOrWhiteSpace(request.ModelPath))
        return Results.BadRequest(new { error = "A model path is required." });
    if (request.ContextLength <= 0)
        return Results.BadRequest(new { error = "A context length in tokens is required." });
    if (!File.Exists(request.ModelPath))
        return Results.NotFound(new { error = "No model file at " + request.ModelPath + "." });

    SystemRamSnapshot? ramSnapshot = ramSnapshotProvider.TryGetSnapshot();
    RuntimeManagerStatus status = await runtimeManager.GetStatusAsync(ct, ramSnapshot);
    long currentRamBudgetBytes = runtimeManager.GetCurrentSystemRamBudgetBytes(request.ModelPath, ramSnapshot);
    RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(
        status, ramSnapshot?.TotalBytesAsLong ?? 0);
    ContextMemoryBudget.Budget budget = await ContextMemoryBudget.ForCurrentResourcesAsync(
        status, hardware, currentRamBudgetBytes, ramSnapshot?.TotalBytesAsLong ?? 0,
        gpuTelemetryProvider, ct);

    long weightsBytes;
    try { weightsBytes = new FileInfo(request.ModelPath).Length; }
    catch { return Results.NotFound(new { error = "Could not read " + request.ModelPath + "." }); }

    GgufModelInfo model = GgufMetadataReader.Read(request.ModelPath);
    ContextExpansionPlanner.ContextPlan plan = ContextExpansionPlanner.PlanToReach(
        model, request.ContextLength, budget.VramBytes, budget.OffloadableRamBytes, weightsBytes);

    if (!plan.Reachable || plan.Option == null)
    {
        return Results.Json(new ModelContextPreferenceResult
        {
            Applied = false,
            ModelPath = request.ModelPath,
            BestAchievable = plan.BestAchievable,
            Message = plan.Explanation,
            RequiresReload = false
        }, statusCode: StatusCodes.Status409Conflict);
    }

    // Going past the trained window is a real quality trade, so it is opt-in rather than something
    // that happens because a number was typed into a box.
    if (plan.Option.RopeScale > 1.0 && !request.AllowBeyondTrainedContext)
    {
        return Results.Json(new ModelContextPreferenceResult
        {
            Applied = false,
            ModelPath = request.ModelPath,
            Selected = ToContextOptionDto(plan.Option),
            BestAchievable = plan.BestAchievable,
            Message = plan.Option.Cost
                + " Set allowBeyondTrainedContext to accept that, or choose a length within "
                + (model.ContextLength ?? 0).ToString("N0") + " tokens.",
            RequiresReload = false
        }, statusCode: StatusCodes.Status409Conflict);
    }

    // An explicitly requested cache type wins over the cheapest one, as long as it still reaches the
    // requested length - someone who wants f16 fidelity at a shorter window is making a valid choice.
    string cacheType = plan.Option.CacheType;
    bool usesRam = plan.Option.UsesSystemRam;
    if (!string.IsNullOrWhiteSpace(request.CacheType))
    {
        if (!ContextExpansionPlanner.CacheTypes.Contains(request.CacheType, StringComparer.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Unsupported cache type \"" + request.CacheType + "\"." });
        cacheType = request.CacheType!;
    }
    if (request.UsesSystemRam.HasValue) usesRam = request.UsesSystemRam.Value;

    long reachable = ContextCapacityPlanner.Plan(
        model,
        usesRam ? budget.TotalBytes : budget.VramBytes,
        weightsBytes,
        cacheType).MemoryLimited;
    if (reachable < request.ContextLength)
    {
        return Results.Json(new ModelContextPreferenceResult
        {
            Applied = false,
            ModelPath = request.ModelPath,
            BestAchievable = plan.BestAchievable,
            Message = "With the cache settings requested this model reaches about "
                + reachable.ToString("N0") + " tokens, not " + request.ContextLength.ToString("N0") + ".",
            RequiresReload = false
        }, statusCode: StatusCodes.Status409Conflict);
    }

    ContextPreferenceStore.Set(request.ModelPath, new ContextPreferenceStore.ContextPreference(
        request.ContextLength, cacheType, usesRam, plan.Option.RopeScale));

    return Results.Ok(new ModelContextPreferenceResult
    {
        Applied = true,
        ModelPath = request.ModelPath,
        Selected = ToContextOptionDto(plan.Option with { CacheType = cacheType, UsesSystemRam = usesRam }),
        BestAchievable = plan.BestAchievable,
        Message = plan.Explanation,
        // Stated rather than implied: llama-server fixes its context at launch, so nothing changes
        // for a model that is already loaded until it is reloaded.
        RequiresReload = true
    });
}).RequireDesktopOwner();

app.MapDelete("/api/v1/models/context-preference", (string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "A model path is required." });
    ContextPreferenceStore.Clear(path);
    return Results.Ok(new ModelContextPreferenceResult
    {
        Applied = true,
        ModelPath = path,
        Message = "PaRuX will choose this model's context again.",
        RequiresReload = true
    });
}).RequireDesktopOwner();

app.MapGet("/api/v1/models/roles", async (RuntimeManagerService runtimeManager, CancellationToken ct) =>
{
    RuntimeManagerStatus status = await runtimeManager.GetStatusAsync(ct);
    var roles = new List<ModelRoleStatus>();
    foreach (string role in ModelRoleCatalog.TextRoles) roles.Add(DescribeTextRole(role, status.ActiveRole, status));
    roles.Add(DescribeMediaRole("image", "image"));
    roles.Add(DescribeMediaRole("image_edit", "edit"));
    roles.Add(DescribeMediaRole("video", "video"));
    roles.Add(DescribeFixedEngineRole("3d", "Fixed Depth-Anything V2 draft-mesh pipeline (ThreedService) - not yet a swappable model role."));
    roles.Add(DescribeFixedEngineRole("stt", "Fixed Parakeet-TDT speech-to-text pipeline (SpeechService) - not yet a swappable model role."));
    roles.Add(DescribeFixedEngineRole("tts", "Fixed Kokoro text-to-speech pipeline (SpeechService) - not yet a swappable model role."));
    roles.Add(DescribeFixedEngineRole("embedding", "Fixed ONNX MiniLM sentence-embedding engine (EmbeddingService) - not yet a swappable model role."));
    roles.Add(DescribeFixedEngineRole("reranker", "Fixed ONNX cross-encoder reranking engine (RerankerService) - not yet a swappable model role."));
    foreach (string role in ModelRoleCatalog.NotImplementedRoles) roles.Add(DescribeNotImplementedRole(role));
    return Results.Ok(roles);
});

app.MapGet("/api/v1/coding/model-selection", async (CodingModelSelectionService selection, CancellationToken ct) =>
    Results.Ok(await selection.GetDecisionAsync(ct, refresh: true)));

app.MapPut("/api/v1/coding/model-selection/mode", (SetCodingModelSelectionModeRequest request, CodingModelSelectionService selection) =>
{
    try
    {
        selection.SetMode(request.Mode);
        return Results.Ok(new { mode = selection.GetMode() });
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return Results.Problem("Could not save the coding model selection mode: " + ex.Message, statusCode: 500);
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/models/roles/{role}", (string role, SetModelRoleRequest request) =>
{
    try
    {
        LocalAiPaths.SetRoleOverride(role, request.ModelPath);
        string? normalized = LocalAiPaths.NormalizeTextRole(role);
        return Results.Ok(DescribeTextRole(normalized!, null));
    }
    catch (ManualSelectionGateBusyException ex) { return Results.Conflict(new { error = ex.Code }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/models/roles/{role}/adapter", (string role, SetRoleAdapterRequest request, AdapterStore adapters) =>
{
    AdapterDto? adapter = adapters.Get(request.AdapterId);
    if (adapter == null) return Results.BadRequest(new { error = "No adapter found with id \"" + request.AdapterId + "\"." });
    try
    {
        LocalAiPaths.SetRoleAdapter(role, adapter.GgufPath ?? adapter.AdapterPath);
        return Results.Ok(DescribeTextRole(LocalAiPaths.NormalizeTextRole(role)!, null));
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapDelete("/api/v1/models/roles/{role}/adapter", (string role) =>
{
    string? normalized = LocalAiPaths.NormalizeTextRole(role);
    if (normalized == null)
        return Results.BadRequest(new { error = "Unknown role \"" + role + "\". Known roles: " + string.Join(", ", LocalAiPaths.KnownTextRoles) + "." });
    LocalAiPaths.ClearRoleAdapter(normalized);
    return Results.Ok(DescribeTextRole(normalized, null));
}).RequireDesktopOwner();

app.MapPost("/api/v1/models/roles/{role}/reset", (string role) =>
{
    string? normalized = LocalAiPaths.NormalizeTextRole(role);
    if (normalized == null)
        return Results.BadRequest(new { error = "Unknown role \"" + role + "\". Known roles: " + string.Join(", ", LocalAiPaths.KnownTextRoles) + "." });
    try
    {
        LocalAiPaths.ClearRoleOverride(normalized);
        return Results.Ok(DescribeTextRole(normalized, null));
    }
    catch (ManualSelectionGateBusyException ex) { return Results.Conflict(new { error = ex.Code }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/runtime/status", async (RuntimeManagerService runtimeManager, CancellationToken ct) =>
    Results.Ok(await runtimeManager.GetStatusAsync(ct)));

app.MapGet("/api/v1/runtime/diagnostics", async (RuntimeManagerService runtimeManager, CancellationToken ct) =>
    Results.Ok(await runtimeManager.GetDiagnosticsAsync(ct)));

app.MapGet("/api/v1/runtime/components", async (ComponentInventoryService inventory, CancellationToken ct) =>
    Results.Ok(await inventory.DiscoverAsync(ct)));

app.MapPost("/api/v1/runtime/components/{id}/locate", async (string id, ComponentLocateRequest request,
    ComponentInventoryService inventory, CancellationToken ct) =>
{
    if (id == "comfyui")
    {
        string? mainPy = ComponentInventoryService.TryResolveComfyMainPy(request.Path);
        if (mainPy == null)
            return Results.BadRequest(new { error = "That path is not a ComfyUI installation - point to the ComfyUI folder (or main.py itself) that contains main.py." });
        try { LocalAiPaths.SetComponentLocation("comfyui", mainPy); }
        catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        return Results.Ok((await inventory.DiscoverAsync(ct)).First(c => c.Id == "comfyui"));
    }

    if (!ComponentInventoryService.LocatableComponents.Any(c => c.Id == id))
        return Results.BadRequest(new { error = "Unknown or non-locatable component id \"" + id + "\"." });
    try { LocalAiPaths.SetComponentLocation(id, request.Path); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    return Results.Ok((await inventory.DiscoverAsync(ct)).First(c => c.Id == id));
}).RequireDesktopOwner();

app.MapPost("/api/v1/runtime/components/{id}/remove-location", async (string id, ComponentInventoryService inventory, CancellationToken ct) =>
{
    LocalAiPaths.ClearComponentLocation(id);
    return Results.Ok((await inventory.DiscoverAsync(ct)).FirstOrDefault(c => c.Id == id));
}).RequireDesktopOwner();

app.MapPost("/api/v1/runtime/components/{id}/skip", async (string id, ComponentInventoryService inventory, CancellationToken ct) =>
{
    if (!ComponentInventoryService.AllComponentIds.Contains(id))
        return Results.BadRequest(new { error = "Unknown component id \"" + id + "\"." });
    LocalAiPaths.SetComponentSkipped(id);
    return Results.Ok((await inventory.DiscoverAsync(ct)).First(c => c.Id == id));
}).RequireDesktopOwner();

app.MapPost("/api/v1/runtime/components/{id}/unskip", async (string id, ComponentInventoryService inventory, CancellationToken ct) =>
{
    LocalAiPaths.ClearComponentSkipped(id);
    return Results.Ok((await inventory.DiscoverAsync(ct)).FirstOrDefault(c => c.Id == id));
}).RequireDesktopOwner();

app.MapPost("/api/v1/models/library/{fileName}/load", async (string fileName, LoadManagedModelRequest request,
    RuntimeManagerService runtimeManager, CancellationToken ct) =>
{
    RuntimeLoadResult result;
    try { result = await runtimeManager.LoadRoleAsync(fileName, request.Role, request.PersistRoleAssignment, ct); }
    catch (TextRuntimeStateUnavailableException unavailable)
    {
        // Typed refusal instead of an unhandled exception (an empty HTTP 500 with no body).
        return TextRuntimeUnavailableResponse.Result(unavailable);
    }
    if (!result.Success && result.Message == ManualSelectionGateBusyException.ErrorCode)
        return Results.Conflict(new { error = ManualSelectionGateBusyException.ErrorCode });
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/v1/models/library/unload", (RuntimeManagerService runtimeManager) =>
    Results.Ok(runtimeManager.Unload()));

app.MapGet("/api/v1/runtime/builds/available", async (string backend, RuntimeInstallService installer, CancellationToken ct) =>
{
    try { return Results.Ok(await installer.ListAvailableAsync(backend, ct)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (HttpRequestException ex)
    {
        return Results.Json(new { error = "Could not reach the llama.cpp release catalog: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/v1/runtime/builds/installed", (string backend, RuntimeInstallService installer) =>
{
    try { return Results.Ok(installer.ListInstalled(backend)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/v1/runtime/builds/install", (RuntimeInstallRequest request, RuntimeInstallService installer) =>
{
    try { return Results.Accepted("/api/v1/runtime/builds/install", installer.CreateInstall(request)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/runtime/builds/install", (RuntimeInstallService installer) => Results.Ok(installer.ListJobs()));

app.MapGet("/api/v1/runtime/builds/install/{id}", (string id, RuntimeInstallService installer) =>
{
    RuntimeInstallStatus? status = installer.GetJob(id);
    return status == null ? Results.NotFound(new { error = "Install job not found" }) : Results.Ok(status);
});

app.MapPost("/api/v1/runtime/builds/install/{id}/cancel", (string id, RuntimeInstallService installer) =>
    installer.CancelJob(id) ? Results.Ok(new { cancelled = true }) : Results.BadRequest(new { error = "Job not found or already finished" })).RequireDesktopOwner();

app.MapPost("/api/v1/runtime/builds/install/{id}/resume", (string id, RuntimeInstallService installer) =>
{
    try
    {
        RuntimeInstallStatus? resumed = installer.ResumeJob(id);
        return resumed == null
            ? Results.NotFound(new { error = "Install job not found" })
            : Results.Accepted("/api/v1/runtime/builds/install/" + id, resumed);
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (IOException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/runtime/builds/rollback", (RuntimeRollbackRequest request, RuntimeInstallService installer) =>
{
    try { installer.Rollback(request); return Results.Ok(new { backend = request.Backend, activeTag = request.Tag }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/runtime/builds/reset", (string backend, RuntimeInstallService installer) =>
{
    try { installer.ResetToDefault(backend); return Results.Ok(new { backend, activeTag = LocalAiPaths.DefaultRuntimeBuildTag(backend) }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/capabilities/runs", async (StartCapabilityProfileRequest request, CapabilityProfilerService profiler, CancellationToken ct) =>
    Results.Accepted("/api/v1/capabilities/runs", await profiler.StartRunAsync(request.Role, ct))).RequireDesktopOwner();

app.MapGet("/api/v1/capabilities/runs", (CapabilityProfilerService profiler) => Results.Ok(profiler.ListRuns()));

app.MapGet("/api/v1/capabilities/runs/{id}", (string id, CapabilityProfilerService profiler) =>
{
    CapabilityProfileRunDto? run = profiler.GetRun(id);
    if (run == null) return Results.NotFound(new { error = "Capability profile run not found" });
    return Results.Ok(new CapabilityProfileRunDetailDto { Run = run, Results = profiler.GetResults(id) });
});

app.MapGet("/api/v1/capabilities/summary", (string modelPath, CapabilityProfilerService profiler) =>
{
    CapabilityModelSummaryDto? summary = profiler.GetModelSummary(modelPath);
    return summary != null ? Results.Ok(summary) : Results.NotFound(new { error = "No completed capability profile run for that model path yet" });
});

app.MapPost("/api/v1/router/resolve", async (RouteRequest request, SmartRouterService router, CancellationToken ct) =>
    Results.Ok(await router.ResolveAsync(request, ct))).RequireDesktopOwner();

app.MapGet("/api/v1/router/decisions", (SmartRouterService router) => Results.Ok(router.ListDecisions()));

app.MapGet("/api/v1/router/candidates/{role}", async (string role, SmartRouterService router, CancellationToken ct) =>
{
    if (LocalAiPaths.NormalizeTextRole(role) == null)
        return Results.BadRequest(new { error = "Unknown or unsupported role for a candidate pool: " + role + "." });
    List<CandidateEvidenceDto> candidates = await router.GetCandidateEvidenceAsync(role, ct);
    return Results.Ok(new CandidatePoolResponse { Role = role, Candidates = candidates });
});

app.MapPost("/api/v1/router/candidates/{role}", (string role, AddCandidateRequest request) =>
{
    try
    {
        string path = ModelDownloadService.ResolveManagedModelPath(request.FileName);
        LocalAiPaths.AddCandidate(role, path);
        return Results.NoContent();
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (FileNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapDelete("/api/v1/router/candidates/{role}/{fileName}", (string role, string fileName) =>
{
    try
    {
        string path = ModelDownloadService.ResolveManagedModelPath(fileName);
        LocalAiPaths.RemoveCandidate(role, path);
        return Results.NoContent();
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (FileNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapGet("/v1/models", (LocalApiServerService localApi) => Results.Ok(localApi.ListModels()));

app.MapGet("/v1/models/{id}", (string id, LocalApiServerService localApi) =>
{
    LocalApiServerService.LocalApiModel? model = localApi.GetModel(id);
    return model == null
        ? Results.NotFound(new { error = new { message = "Unknown model id \"" + id + "\"." } })
        : Results.Ok(model);
});

app.MapPost("/v1/chat/completions", async (HttpRequest req, LocalApiServerService localApi, CancellationToken ct) =>
{
    string body;
    using (var reader = new StreamReader(req.Body)) body = await reader.ReadToEndAsync(ct);
    LocalApiCompletionResult result = await localApi.CompleteAsync(body, ct);
    return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
});

app.MapPost("/v1/completions", async (HttpRequest req, LocalApiServerService localApi, CancellationToken ct) =>
{
    string body;
    using (var reader = new StreamReader(req.Body)) body = await reader.ReadToEndAsync(ct);
    LocalApiCompletionResult result = await localApi.CompleteLegacyAsync(body, ct);
    return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
});

app.MapPost("/v1/embeddings", async (HttpRequest req, LocalApiEmbeddingsService embeddingsApi, CancellationToken ct) =>
{
    string body;
    using (var reader = new StreamReader(req.Body)) body = await reader.ReadToEndAsync(ct);
    LocalApiCompletionResult result = embeddingsApi.CreateEmbeddings(body);
    return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
});

app.MapGet("/api/v1/datasets/search", async (string query, int? limit, DatasetDownloadService datasets, CancellationToken ct) =>
    Results.Ok(await datasets.SearchAsync(query, limit ?? 15, ct)));

app.MapGet("/api/v1/datasets/files", async (string repoId, DatasetDownloadService datasets, CancellationToken ct) =>
    Results.Ok(await datasets.GetDetailsAsync(repoId, ct)));

app.MapGet("/api/v1/datasets/budget", async (DatasetDownloadService datasets) => Results.Ok(await datasets.GetBudgetAsync()));

app.MapGet("/api/v1/datasets/downloads", (DatasetDownloadService datasets) => Results.Ok(datasets.List()));

app.MapGet("/api/v1/datasets/downloads/{id}", (string id, DatasetDownloadService datasets) =>
{
    DatasetDownloadStatus? status = datasets.Get(id);
    return status != null ? Results.Ok(status) : Results.NotFound(new { error = "Download not found" });
});

app.MapPost("/api/v1/datasets/downloads", async (DatasetDownloadRequest request, DatasetDownloadService datasets, CancellationToken ct) =>
{
    try { return Results.Ok(await datasets.CreateDownloadAsync(request, ct)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/datasets/downloads/{id}/cancel", (string id, DatasetDownloadService datasets) =>
    datasets.Cancel(id) ? Results.NoContent() : Results.NotFound(new { error = "Download not found or already finished" })).RequireDesktopOwner();

app.MapPost("/api/v1/datasets/inspect", async (DatasetInspectRequest request, DatasetCurationService curation, CancellationToken ct) =>
    Results.Ok(await curation.InspectAsync(request.SourcePath, ct))).RequireDesktopOwner();

app.MapPost("/api/v1/datasets/split", async (DatasetSplitRequest request, DatasetCurationService curation, CancellationToken ct) =>
{
    try { return Results.Ok(await curation.SplitAsync(request, ct)); }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (FileNotFoundException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/datasets/import-eval", async (ImportDatasetAsEvalCasesRequest request, DatasetCurationService curation, EvalStore evalStore, CancellationToken ct) =>
{
    try
    {
        (List<CreateEvalCaseRequest> cases, int rowsSkipped) = await curation.MapToEvalCasesAsync(
            request.SourcePath, request.PromptColumn, request.ExpectedBehaviorColumn, request.SystemPromptColumn, ct);

        foreach (CreateEvalCaseRequest c in cases) evalStore.CreateCase(request.TargetEvalDatasetId, c);

        return Results.Ok(new ImportDatasetAsEvalCasesResult { CasesCreated = cases.Count, RowsSkipped = rowsSkipped });
    }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (FileNotFoundException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDesktopOwner();

// Template and default-checkpoint resolution now live in MediaCheckpointResolution (TemplatePathFor, Resolve),
// shared with the graph builder, so the status cannot read a different template than the job uses.

static MediaCheckpointStatus DescribeCheckpoint(string type)
{
    // One resolver answers "what will the next job load" for both the graph and this status, so the two
    // cannot disagree. It also states which graph input an override replaces, and the detected family.
    MediaCheckpointEffective effective = MediaCheckpointResolution.Resolve(type);
    return new MediaCheckpointStatus
    {
        Type = type,
        CurrentFile = effective.File,
        IsOverridden = effective.IsOverridden,
        AvailableFiles = LocalAiPaths.ListDiffusionModelCheckpoints(),
        Template = effective.TemplateFile,
        ReplacesInputs = effective.ReplacesInputs.ToList(),
        Family = effective.IsOverridden ? MediaCheckpointResolution.FamilyOfInstalledFile(effective.File) : null,
        ClearedOverrideFile = effective.ClearedOverrideFile,
        CompatibleFiles = MediaCheckpointResolution.CompatibleFiles(type)
    };
}

// The plan for the checkpoint a type's next job loads. Shared by the read-only endpoint and the fetch.
static MediaCompanionPlan BuildCompanionPlan(string type) => MediaCompanionPlanReader.Build(type);

app.MapGet("/api/v1/media/checkpoints", () => Results.Ok(new[]
{
    DescribeCheckpoint("image"), DescribeCheckpoint("video"), DescribeCheckpoint("edit")
}));

app.MapPost("/api/v1/media/checkpoints/{type}", (string type, SetMediaCheckpointRequest request) =>
{
    if (type != "image" && type != "video" && type != "edit")
        return Results.BadRequest(new { error = "Type must be \"image\", \"video\", or \"edit\"." });
    try
    {
        LocalAiPaths.SetMediaCheckpointOverride(type, request.FileName);
        return Results.Ok(DescribeCheckpoint(type));
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapPost("/api/v1/media/checkpoints/{type}/reset", (string type) =>
{
    if (type != "image" && type != "video" && type != "edit")
        return Results.BadRequest(new { error = "Type must be \"image\", \"video\", or \"edit\"." });
    LocalAiPaths.ClearMediaCheckpointOverride(type);
    return Results.Ok(DescribeCheckpoint(type));
}).RequireDesktopOwner();

app.MapGet("/api/v1/media/checkpoints/recommended", () => Results.Ok(RecommendedCheckpointsService.List));

// Companion plan for the checkpoint the next job of this type will load: what is installed, what is missing,
// and the plan hash a fetch must present. Read-only; nothing is downloaded here.
app.MapGet("/api/v1/media/companions", (string type) =>
{
    if (type != "image" && type != "video" && type != "edit")
        return Results.BadRequest(new { error = "Type must be \"image\", \"video\", or \"edit\"." });
    return Results.Ok(BuildCompanionPlan(type));
});

// Consent-gated fetch of only the missing companions. Refused unless the caller confirms the plan hash it reviewed.
app.MapPost("/api/v1/media/companions/fetch", async (MediaCompanionFetchRequest request, ModelDownloadService downloads, CancellationToken ct) =>
{
    if (request.Type != "image" && request.Type != "video" && request.Type != "edit")
        return Results.BadRequest(new { error = "Type must be \"image\", \"video\", or \"edit\"." });
    var fetcher = new MediaCompanionFetcher(downloads, Path.Combine(LocalAiPaths.ComfyApp, "models"));
    MediaCompanionFetchResult result = await fetcher.FetchAsync(BuildCompanionPlan(request.Type), request, ct);
    return result.Ok ? Results.Ok(result) : Results.Conflict(result);
}).RequireDesktopOwner();

// Guided first-run media setup: ComfyUI, the prompt-assistant model, a downloadable image checkpoint and its companions.
app.MapGet("/api/v1/media/setup", (string? type, MediaSetupService setup) => Results.Ok(setup.GetStatus(type)));
app.MapPost("/api/v1/media/setup/start", (string? type, MediaSetupService setup) => Results.Accepted("/api/v1/media/setup", setup.Start(type))).RequireDesktopOwner();
app.MapPost("/api/v1/media/setup/cancel", (string? type, MediaSetupService setup) => { setup.Cancel(type); return Results.Ok(setup.GetStatus(type)); }).RequireDesktopOwner();

app.MapPost("/api/v1/media/checkpoints/activate", (ActivateCheckpointRequest request) =>
{
    if (request.Type != "image" && request.Type != "video" && request.Type != "edit")
        return Results.BadRequest(new { error = "Type must be \"image\", \"video\", or \"edit\"." });
    if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Contains("..")
        || request.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        return Results.BadRequest(new { error = "Invalid file name." });

    string source = Path.Combine(ModelDownloadService.ModelsDirectory, request.FileName);
    if (!File.Exists(source))
        return Results.BadRequest(new { error = "No downloaded file named \"" + request.FileName + "\" was found - download it first." });

    MediaDependencyError? familyRefusal = null;
    try
    {
        // Family gate BEFORE the file is moved: a refused file stays where the download put it.
        MediaCheckpointResolution.EnsureFamilyAllowed(request.Type, source, out familyRefusal);
        Directory.CreateDirectory(LocalAiPaths.DiffusionModelsPath);
        string destination = Path.Combine(LocalAiPaths.DiffusionModelsPath, request.FileName);
        if (!File.Exists(destination))
        {
            try { File.Move(source, destination); }
            catch (IOException) { File.Copy(source, destination, overwrite: false); File.Delete(source); }
        }
        if (request.AssignOverride) LocalAiPaths.SetMediaCheckpointOverride(request.Type, request.FileName);
        return Results.Ok(DescribeCheckpoint(request.Type));
    }
    catch (InvalidDataException ex) when (familyRefusal != null)
    {
        return Results.Conflict(new
        {
            error = ex.Message + " Next step: " + familyRefusal.NextAction,
            errorDetail = familyRefusal
        });
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) { return Results.Json(new { error = "Couldn't activate: " + ex.Message }, statusCode: StatusCodes.Status500InternalServerError); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/media/workflows", ([FromServices] CustomWorkflowService workflows) => Results.Ok(workflows.List()));

app.MapPost("/api/v1/media/workflows", (ImportCustomWorkflowRequest request, [FromServices] CustomWorkflowService workflows) =>
{
    try { return Results.Ok(workflows.Import(request.Name, request.WorkflowJson)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapDelete("/api/v1/media/workflows/{id}", (string id, [FromServices] CustomWorkflowService workflows) =>
{
    workflows.Delete(id);
    return Results.NoContent();
}).RequireDesktopOwner();

ComfyWorkflowImportEndpoints.Map(app);

app.MapGet("/api/v1/compare/available-models", (ModelCompareService compare) => Results.Ok(compare.ListAvailableModels()));

app.MapPost("/api/v1/compare", (ModelCompareRequest request, ModelCompareService compare, LocalRuntimeService runtime) =>
{
    if (runtime.GamingMode) return Results.Conflict(new { error = "AI paused by Gaming — Return to AI." });
    try { return Results.Accepted("/api/v1/compare", compare.Create(request)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/compare/{id}", (string id, ModelCompareService compare) =>
    compare.Get(id) is { } status ? Results.Ok(status) : Results.NotFound(new { error = "Compare run not found" }));

app.MapPost("/api/v1/compare/{id}/cancel", (string id, ModelCompareService compare) =>
    compare.Cancel(id) ? Results.Accepted() : Results.NotFound(new { error = "Active compare run not found" })).RequireDesktopOwner();

app.MapGet("/api/v1/group-chat/available-models", (GroupChatService groupChat) => Results.Ok(groupChat.ListAvailableModels()));

app.MapPost("/api/v1/group-chat", (GroupChatRequest request, GroupChatService groupChat, LocalRuntimeService runtime) =>
{
    if (runtime.GamingMode) return Results.Conflict(new { error = "AI paused by Gaming — Return to AI." });
    try { return Results.Accepted("/api/v1/group-chat", groupChat.Create(request)); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireDesktopOwner();

app.MapGet("/api/v1/group-chat/{id}", (string id, GroupChatService groupChat) =>
    groupChat.Get(id) is { } status ? Results.Ok(status) : Results.NotFound(new { error = "Group chat run not found" }));

app.MapPost("/api/v1/group-chat/{id}/cancel", (string id, GroupChatService groupChat) =>
    groupChat.Cancel(id) ? Results.Accepted() : Results.NotFound(new { error = "Active group chat run not found" })).RequireDesktopOwner();

app.MapGet("/api/v1/threed/engine", async (ThreedService threed, CancellationToken ct) =>
    Results.Ok(await threed.GetStatusAsync(ct)));

app.MapPost("/api/v1/threed/engine/install", async (ThreedService threed, CancellationToken ct) =>
{
    try
    {
        string log = await threed.InstallEngineAsync(ct);
        return Results.Ok(new { installed = true, log });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireDesktopOwner();

app.MapPost("/api/v1/threed/generate", async (ThreedGenerateRequest request, ThreedService threed, LocalRuntimeService runtime, CancellationToken ct) =>
{
    try
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.GamingCancellationToken);
        return Results.Ok(await threed.GenerateAsync(request, linked.Token));
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Conflict(new { error = "Gaming Mode activated; 3D generation was stopped." });
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// Local music (ACE-Step 1.5 through the managed ComfyUI): returns the saved FLAC as a MediaAsset.
app.MapPost("/api/v1/music/generate", async (MusicGenerateRequest request, MusicService music, LocalRuntimeService runtime, CancellationToken ct) =>
{
    try
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.GamingCancellationToken);
        return Results.Ok(await music.GenerateAsync(request, linked.Token));
    }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Conflict(new { error = "Gaming Mode activated; music generation was stopped." });
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireDesktopOwner();

app.MapGamingEndpoints();
app.MapMcp("/mcp");

if (!app.Environment.IsEnvironment("Testing"))
{
    app.Lifetime.ApplicationStarted.Register(state.Publish);
    app.Lifetime.ApplicationStarted.Register(() => StartupTrace.Mark("application-started"));
    app.Lifetime.ApplicationStopped.Register(state.RemoveIfOwned);
}
// Media jobs that were in flight when Core last stopped are decided against ComfyUI in the background.
_ = Task.Run(async () =>
{
    try
    {
        await app.Services.GetRequiredService<MediaJobService>()
            .RecoverAsync(app.Services.GetRequiredService<ComfyWorkflowRunner>(), CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Media job recovery stopped early; jobs that were in flight may still show as running.");
    }
});
StartupTrace.Mark("routes-mapped");
await app.RunAsync();

// ---- Adjustable-context helpers ---------------------------------------------------------------

static (string? Path, IResult? Failure) ResolveContextModelPath(string? path, string? role)
{
    string? modelPath = path;
    if (string.IsNullOrWhiteSpace(modelPath) && !string.IsNullOrWhiteSpace(role))
    {
        string? normalized = LocalAiPaths.NormalizeTextRole(role);
        if (normalized == null)
            return (null, Results.BadRequest(new { error = "Unknown role \"" + role + "\"." }));
        modelPath = LocalAiPaths.GetRoleModelPathOrNull(normalized);
    }
    if (string.IsNullOrWhiteSpace(modelPath))
        return (null, Results.BadRequest(new { error = "Pass either ?path= a model file or ?role= a text role that has one assigned." }));
    if (!File.Exists(modelPath))
        return (null, Results.NotFound(new { error = "No model file at " + modelPath + "." }));
    return (modelPath, null);
}



static ModelContextOption ToContextOptionDto(ContextExpansionPlanner.ContextOption option) => new()
{
    ContextLength = option.ContextLength,
    CacheType = option.CacheType,
    UsesSystemRam = option.UsesSystemRam,
    RopeScale = option.RopeScale,
    KvCacheBytes = option.KvCacheBytes,
    Levers = option.Levers.Select(lever => lever.ToString()).ToList(),
    Cost = option.Cost
};

static TrainingPlanResult ToTrainingPlanResult(TrainingRunPlanner.RunPlan plan) => new()
{
    CanStart = plan.CanStart,
    DetectedMethod = plan.DetectedMethod,
    DatasetSupportsSupervised = plan.Dataset.SupportsSupervisedFineTuning,
    DatasetSupportsPreference = plan.Dataset.SupportsPreferenceTuning,
    DatasetExamples = plan.Dataset.LinesRead,
    DatasetUnparseableLines = plan.Dataset.UnparseableLines,
    DatasetSampleTruncated = plan.Dataset.Truncated,
    DatasetSummary = plan.Dataset.Summary,
    RecommendedLoraRank = plan.Recommended.LoraRank,
    RecommendedMaxSteps = plan.Recommended.MaxSteps,
    RecommendedLearningRate = plan.Recommended.LearningRate,
    RecommendationRationale = plan.Recommended.Rationale,
    EstimatedGib = plan.EstimatedGib,
    AvailableGib = plan.AvailableGib,
    Findings = plan.Findings.Select(finding => new TrainingPlanFinding
    {
        Severity = finding.Severity.ToString(),
        Field = finding.Field,
        Message = finding.Message
    }).ToList()
};

// A request with no recorded remote address is not evidence of a local caller, so it is never treated as loopback.
static bool IsLoopback(HttpContext context)
{
    IPAddress? address = context.Connection.RemoteIpAddress;
    return address is not null && IPAddress.IsLoopback(address);
}

// Desktop-owner routes. Loopback alone is not enough for these: a browser session that reaches PaRuX through a same-machine
// reverse proxy arrives from 127.0.0.1 too, so the owner the middleware stamped must also be the desktop itself.
static bool IsDesktopOwner(HttpContext context) =>
    DesktopOwnerRouteExtensions.IsDesktopOwner(context);

static string RequestOwner(HttpContext context) =>
    context.Items["PaRuXOwner"] as string ?? "desktop";

public partial class Program { }
