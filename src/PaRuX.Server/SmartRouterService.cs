using System.Collections.Concurrent;
using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server;

/// <summary>
/// Smart Model Router. Interactive AUTO expands explicit role candidates with the Managed Model
/// Library and evaluates capability, throughput and hardware fit separately. Custom AUTO tuning is
/// loaded only for the Custom profile; exact user overrides still bypass all automatic ranking.
/// </summary>
public sealed class SmartRouterService : IRoleRouter
{
    private readonly RuntimeManagerService _runtimeManager;
    private readonly ModelBenchmarkService _benchmarks;
    private readonly CapabilityProfiler.CapabilityProfilerStore _profilerStore;
    private readonly CandidateEvidenceService? _candidateEvidence;

    // Explicit media requests come first: "create an image of a code editor" is an image task, not a coding one.
    // Every keyword must start at a word boundary, and a short one (5 letters or fewer) must also end at one, so
    // "see" no longer matches "seem", "fast" no longer matches "breakfast" and "code" no longer matches "barcode".
    // A trailing * marks a stem ("analy*") that may continue into a longer word.
    private static readonly (string Role, string[] Keywords)[] TaskTypeKeywords =
    {
        ("image_edit", new[] { "edit_image", "image_edit", "edit this photo", "edit the image", "edit this image", "remove background", "remove the background", "inpaint", "outpaint",
            "modifica l'immagine", "modifica questa immagine", "modifica la foto", "rimuovi lo sfondo" }),
        ("image", new[] { "generate_image", "text_to_image", "generate an image", "generate a picture", "create an image", "create a picture", "draw me", "illustration",
            "genera un'immagine", "genera un immagine", "crea un'immagine", "crea un immagine", "disegna" }),
        ("video", new[] { "generate_video", "text_to_video", "generate a video", "create a video", "make a video", "animate",
            "genera un video", "crea un video", "anima " }),
        ("coding", new[] { "coding", "code", "codebase", "codice", "implement", "implementa", "refactor", "debug", "programming", "programma", "script", "function", "funzione" }),
        ("reviewer", new[] { "review", "reviewer", "critique", "audit", "revisione" }),
        ("planner", new[] { "plan", "plans", "planning", "planner", "roadmap", "decompose", "piano", "pianifica" }),
        ("reasoning", new[] { "reasoning", "reason", "research", "analy*", "analizz*", "ragiona*", "deep", "approfond*" }),
        ("vision", new[] { "vision", "image_understanding", "describe_image", "what you see", "in this image", "in the image", "describe this image", "describe the image", "what is in this image", "descrivi questa immagine" }),
        ("fast", new[] { "fast", "quick", "trivial", "lookup", "veloce", "rapido" }),
        ("uncensored", new[] { "uncensored", "nsfw" }),
    };

    internal static bool ContainsKeyword(string text, string keyword)
    {
        keyword = keyword.TrimEnd();
        bool stem = keyword.EndsWith('*');
        if (stem) keyword = keyword.TrimEnd('*');
        bool wholeWord = !stem && (keyword.Length <= 5 || keyword.Contains(' '));
        int from = 0;
        while (from <= text.Length - keyword.Length)
        {
            int at = text.IndexOf(keyword, from, StringComparison.Ordinal);
            if (at < 0) return false;
            bool startOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            int end = at + keyword.Length;
            bool endOk = !wholeWord || end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (startOk && endOk) return true;
            from = at + 1;
        }
        return false;
    }

    private const int MaxDecisionHistory = 50;
    private readonly ConcurrentQueue<RouteDecisionDto> _decisions = new();

    public SmartRouterService(
        RuntimeManagerService runtimeManager,
        ModelBenchmarkService benchmarks,
        CapabilityProfiler.CapabilityProfilerStore profilerStore)
    {
        _runtimeManager = runtimeManager;
        _benchmarks = benchmarks;
        _profilerStore = profilerStore;
    }

    internal SmartRouterService(RuntimeManagerService runtimeManager, ModelBenchmarkService benchmarks,
        CapabilityProfiler.CapabilityProfilerStore profilerStore, CandidateEvidenceService candidateEvidence,
        RecommendationRouteConsult? recommendation = null)
        : this(runtimeManager, benchmarks, profilerStore)
    {
        _candidateEvidence = candidateEvidence;
        _recommendation = recommendation;
    }

    private readonly RecommendationRouteConsult? _recommendation;

    /// <summary>Data root of the capability-mode store; null means the product data root (tests override it).</summary>
    internal string? CapabilityModeRoot { get; set; }

    internal static string? CapabilityForTextRole(string? role) => role switch
    {
        "standard" or "fast" or "uncensored" => ModelCapabilityIds.GeneralChat,
        "reasoning" => ModelCapabilityIds.Reasoning,
        "coding" => ModelCapabilityIds.Coding,
        "vision" => ModelCapabilityIds.Vision,
        _ => null
    };

    public static string MapTaskTypeToRole(string? taskType)
    {
        if (string.IsNullOrWhiteSpace(taskType)) return "standard";
        string text = taskType.ToLowerInvariant().Replace('\u2019', '\'');
        foreach ((string role, string[] keywords) in TaskTypeKeywords)
            if (keywords.Any(k => ContainsKeyword(text, k))) return role;
        return "standard";
    }

    public Task<List<CandidateEvidenceDto>> GetCandidateEvidenceAsync(string role, CancellationToken ct) =>
        GetCandidateEvidenceAsync(role, includeManagedLibrary: false, ct);

    private async Task<List<CandidateEvidenceDto>> GetCandidateEvidenceAsync(
        string role,
        bool includeManagedLibrary,
        CancellationToken ct)
    {
        string? normalizedRole = LocalAiPaths.NormalizeTextRole(role);
        if (normalizedRole == null) return new List<CandidateEvidenceDto>();

        List<string> paths = LocalAiPaths.ListCandidates(normalizedRole);
        if (includeManagedLibrary)
            paths = ManagedLibraryCandidateExpander.Expand(paths, ModelDownloadService.ModelsDirectory);

        string? currentAssignment = LocalAiPaths.GetRoleModelPathOrNull(normalizedRole);
        RuntimeManagerStatus status = await _runtimeManager.GetStatusAsync(ct);
        // The existing no-heavy-GPU fallback is an AUTO candidate, never a persisted role
        // replacement. Otherwise a fresh iGPU installation can only rank its 27B default,
        // load it into shared RAM, and then reject Chat for exceeding the execution budget.
        if (includeManagedLibrary && !status.GpuDetected)
        {
            string fallback = normalizedRole == "uncensored" ? LocalAiPaths.SmallUncensoredModel : LocalAiPaths.SmallModel;
            if (File.Exists(fallback) && !paths.Any(path => PathsEqual(path, fallback))) paths.Add(fallback);
        }
        Dictionary<string, double> benchmarkByPath = _benchmarks.ListModels()
            .Where(m => m.AverageTokensPerSecond.HasValue)
            .GroupBy(m => m.ModelPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().AverageTokensPerSecond!.Value, StringComparer.OrdinalIgnoreCase);

        var results = new List<CandidateEvidenceDto>();
        foreach (string path in paths)
        {
            CapabilityModelSummaryDto? summary = _profilerStore.GetModelSummary(path);
            string fileName = Path.GetFileName(path);
            ModelCapabilityScorer.Assessment capability = ModelCapabilityScorer.Assess(
                fileName, null, null, summary?.CategoryPassRates, normalizedRole);
            ModelIntelligenceEstimator.Estimate intelligence =
                ModelIntelligenceEstimator.EstimateFrom(
                    fileName, capability.RoleEmpiricalScore, capability.ModelCapabilities);

            long sizeBytes = 0;
            try { sizeBytes = new FileInfo(path).Length; } catch { }
            HardwareFitEstimate? fit = await _runtimeManager.EstimateHardwareFitAsync(path, ct);

            results.Add(new CandidateEvidenceDto
            {
                FilePath = path,
                FileName = fileName,
                ModelCategory = capability.ModelCategory,
                ModelCapabilities = intelligence.ModelCapabilities.ToList(),
                IsCurrentAssignment = currentAssignment != null && PathsEqual(currentAssignment, path),
                IsCurrentlyLoaded = status.ActiveModelPath != null && PathsEqual(status.ActiveModelPath, path)
                    && string.Equals(status.ActiveRole, normalizedRole, StringComparison.OrdinalIgnoreCase),
                BenchmarkTokensPerSecond = benchmarkByPath.TryGetValue(path, out double tps) ? tps : null,
                CapabilityScore = capability.OverallEmpiricalScore,
                RoleCapabilityScore = capability.RoleEmpiricalScore,
                IntelligenceScore = intelligence.Score,
                ParameterBillions = intelligence.ParameterBillions,
                IntelligenceEvidence = intelligence.Evidence,
                SizeBytes = sizeBytes,
                HardwareCompatible = fit?.Compatible
            });
        }
        return results;
    }

    private CandidateEvidenceDto GetExactManagedModelEvidence(string role, string path)
    {
        CapabilityModelSummaryDto? summary = _profilerStore.GetModelSummary(path);
        string fileName = Path.GetFileName(path);
        ModelCapabilityScorer.Assessment capability = ModelCapabilityScorer.Assess(
            fileName, null, null, summary?.CategoryPassRates, role);
        ModelIntelligenceEstimator.Estimate intelligence =
            ModelIntelligenceEstimator.EstimateFrom(
                fileName, capability.RoleEmpiricalScore, capability.ModelCapabilities);
        string? currentAssignment = LocalAiPaths.GetRoleModelPathOrNull(role);
        long sizeBytes = 0;
        try { sizeBytes = new FileInfo(path).Length; } catch { }

        // Preserve useful telemetry for the locked model without listing alternatives,
        // querying runtime status, or estimating hardware fit a second time before admission.
        return new CandidateEvidenceDto
        {
            FilePath = path,
            FileName = fileName,
            ModelCategory = capability.ModelCategory,
            ModelCapabilities = intelligence.ModelCapabilities.ToList(),
            IsCurrentAssignment = currentAssignment != null && PathsEqual(currentAssignment, path),
            BenchmarkTokensPerSecond = _benchmarks.GetAverageTokensPerSecond(path),
            CapabilityScore = capability.OverallEmpiricalScore,
            RoleCapabilityScore = capability.RoleEmpiricalScore,
            IntelligenceScore = intelligence.Score,
            ParameterBillions = intelligence.ParameterBillions,
            IntelligenceEvidence = intelligence.Evidence,
            SizeBytes = sizeBytes
        };
    }

    private static bool PathsEqual(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    public async Task<RouteDecisionDto> ResolveAsync(RouteRequest request, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string inferredMode = !string.IsNullOrWhiteSpace(request.Role) ? RouteMode.Manual : RouteMode.Auto;
        string? explicitMode = request.Mode?.Trim().ToLowerInvariant();
        string mode = explicitMode != null && RouteMode.All.Contains(explicitMode) ? explicitMode : inferredMode;
        string taskSignal = !string.IsNullOrWhiteSpace(request.TaskType) ? request.TaskType! : request.PromptText ?? string.Empty;
        string requestedRole = !string.IsNullOrWhiteSpace(request.Role)
            ? request.Role!.Trim()
            : MapTaskTypeToRole(taskSignal);
        string? normalizedTextRole = LocalAiPaths.NormalizeTextRole(requestedRole);
        bool scopedPrefer = !string.IsNullOrWhiteSpace(request.PreferredCandidateKey)
            || !string.IsNullOrWhiteSpace(request.PreferredCandidateRevision)
            || !string.IsNullOrWhiteSpace(request.PreferredManagedFileName);
        bool exactRegisteredForced = !string.IsNullOrWhiteSpace(request.ExactCandidateFileName);
        bool exactManagedForced = !scopedPrefer && !string.IsNullOrWhiteSpace(request.ExactManagedModelFileName);
        bool scopedForce = !scopedPrefer && (!string.IsNullOrWhiteSpace(request.ScopedCandidateKey)
            || !string.IsNullOrWhiteSpace(request.ScopedCandidateRevision));
        bool exactForced = exactRegisteredForced || exactManagedForced || scopedForce;
        string autoProfile = AutoIntelligenceProfile.Normalize(request.AutoProfile);
        AutoRoutingTuningDto? autoTuning = mode == RouteMode.Auto
            && autoProfile == AutoIntelligenceProfile.Custom
                ? AutoRoutingTuningStore.Get()
                : null;
        double? requiredIntelligence = mode == RouteMode.Auto
            ? CandidatePoolRanker.EstimateTaskIntelligenceDemand(request.PromptText ?? request.TaskType, requestedRole)
            : null;

        bool managedLibraryExpanded = mode == RouteMode.Auto
            && string.IsNullOrWhiteSpace(request.Role)
            && !exactForced;

        // An exact managed-model lock cannot select from alternatives. Avoid collecting
        // per-candidate hardware and capability evidence on this latency-sensitive path;
        // EnsureExactCandidateLoadedAsync still applies the strict live admission checks.
        List<CandidateEvidenceDto> candidates = normalizedTextRole != null && !scopedForce
            && (!exactManagedForced || exactRegisteredForced)
            ? await GetCandidateEvidenceAsync(normalizedTextRole, managedLibraryExpanded, ct)
            : new List<CandidateEvidenceDto>();
        int candidatesConsidered = candidates.Count;
        CandidateEvidenceDto? selectedEvidence = null;
        RuntimeLoadResult result;
        string? recommendationSkip = null;
        bool recommendedApplied = false;
        bool scopedPreferApplied = false;
        string? scopedPreferenceFallbackReason = null;

        async Task<RuntimeLoadResult> ResolveAutomaticallyAsync()
        {
            // Evidence-qualified recommendation: explicit AUTO only, Standard x Balanced only, after every lock,
            // Force and Prefer attempt. Any doubt falls through to the ordinary ranker, and the reason is reported.
            RuntimeLoadResult? recommended = null;
            if (_recommendation is not null && mode == RouteMode.Auto && string.IsNullOrWhiteSpace(request.Role)
                && normalizedTextRole == "standard" && autoProfile == AutoIntelligenceProfile.Balanced)
            {
                (RecommendedRoute? route, string? skip) = await _recommendation.ConsultAsync(ct).ConfigureAwait(false);
                recommendationSkip = skip;
                // The consult is repeated once the exclusive writer is held; if the winner changed while we
                // waited (promote/rollback in between), the stale one is never loaded and we re-consult once.
                for (int attempt = 0; route is not null && attempt < 2; attempt++)
                {
                    RecommendedRoute current = route;
                    bool changed = false;
                    async Task<bool> SameAsync(CancellationToken token)
                    {
                        (RecommendedRoute? now, _) = await _recommendation.ConsultAsync(token).ConfigureAwait(false);
                        bool same = now is not null && now.CandidateKey == current.CandidateKey
                            && now.Revision == current.Revision && now.ManagedFileName == current.ManagedFileName;
                        if (!same) changed = true;
                        return same;
                    }
                    RuntimeLoadResult attemptResult = await LoadScopedForceAsync(new RouteRequest
                    {
                        Role = "standard", Mode = RouteMode.Manual, ExactManagedModelFileName = current.ManagedFileName,
                        ScopedCandidateKey = current.CandidateKey, ScopedCandidateRevision = current.Revision
                    }, normalizedTextRole, ct, SameAsync).ConfigureAwait(false);
                    if (attemptResult.Success) { recommended = attemptResult; recommendedApplied = true; break; }
                    if (changed && attempt == 0)
                    {
                        (route, recommendationSkip) = await _recommendation.ConsultAsync(ct).ConfigureAwait(false);
                        continue;
                    }
                    recommendationSkip = changed ? "recommendation_changed_during_load" : "recommended_winner_not_admitted";
                    break;
                }
            }

            return recommended ?? await SelectAndLoadAsync(
                requestedRole, normalizedTextRole, requestedRole, candidates, mode,
                request.PromptText ?? request.TaskType, autoProfile, autoTuning, ct,
                evidence => selectedEvidence = evidence);
        }

        // Per-capability Automatic / Prefer / Force (stored). Explicit locks, scoped overrides and manual roles win.
        CapabilityModeOverride? capOverride = null;
        CapabilityRouteOutcome? capGate = null;
        string? capabilityId = CapabilityForTextRole(normalizedTextRole);
        var capTrace = new List<string>();
        if (capabilityId is not null && mode == RouteMode.Auto && string.IsNullOrWhiteSpace(request.Role)
            && !exactForced && !scopedPrefer)
        {
            try { capOverride = CapabilityRoutingPreferenceStore.GetCapabilityMode(capabilityId, CapabilityModeRoot); }
            catch (InvalidDataException)
            {
                // Fail closed: a corrupt override store is never silently treated as Automatic.
                var broken = new RouteDecisionDto
                {
                    Mode = mode, RequestedRole = null, TaskType = request.TaskType, ResolvedRole = normalizedTextRole!,
                    FellBackToStandard = false, Success = false, Swapped = false,
                    Message = "The capability routing overrides need repair before routing can continue; no model was loaded.",
                    DurationMs = stopwatch.ElapsedMilliseconds, DecidedUtc = DateTime.UtcNow,
                    FailureReasonCode = "override_store_corrupt", EffectiveOverrideMode = ScopedRouteMode.Force,
                    SelectionReason = "capability_override_store_corrupt"
                };
                broken.FallbackTrace.Add("force_refused:override_store_corrupt:no_substitution");
                _decisions.Enqueue(broken);
                return broken;
            }
            if (capOverride.Mode != ScopedRouteMode.Automatic)
            {
                List<CandidateEvidenceDto> pool = new(candidates);
                if (capOverride.ModelId is { } wanted
                    && pool.All(c => !string.Equals(c.FileName, wanted, StringComparison.OrdinalIgnoreCase))
                    && IsSafeManagedFileName(wanted))
                {
                    try
                    {
                        string managed = ModelDownloadService.ResolveManagedModelPath(wanted);
                        if (File.Exists(managed)) pool.Add(GetExactManagedModelEvidence(normalizedTextRole!, managed));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException) { }
                }
                bool gaming = (await _runtimeManager.GetStatusAsync(ct).ConfigureAwait(false)).GamingActive;
                string profile = normalizedTextRole == "uncensored"
                    ? ModelContentProfileIds.Uncensored : ModelContentProfileIds.Standard;
                var facts = pool.Select(c => new CapabilityRouteCandidate(c.FileName, new[] { capabilityId },
                    profile, c.HardwareCompatible != false, c.IntelligenceScore ?? 0)).ToList();
                capGate = CapabilityRouteResolver.Resolve(new CapabilityRouteContext(capabilityId, profile,
                    gaming, LocalOnly: true, CloudConsent: false,
                    new InstalledRuntime(null, ComponentOwnership.SYSTEM_DETECTED)), capOverride, facts);
                capTrace.AddRange(capGate.FallbackTrace);
                selectedEvidence = pool.FirstOrDefault(c => c.FileName == capGate.ChosenModelId);
            }
        }

        if (capGate is { Success: false })
        {
            result = Failure(normalizedTextRole!, capGate.Explanation);
        }
        else if (capGate is { Success: true })
        {
            string chosenPath = selectedEvidence?.FilePath ?? ModelDownloadService.ResolveManagedModelPath(capGate.ChosenModelId!);
            try { result = await _runtimeManager.EnsureExactCandidateLoadedAsync(normalizedTextRole!, chosenPath, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = Failure(normalizedTextRole!, "The model could not be loaded safely (" + ex.GetType().Name + ").");
            }
            if (!result.Success && capOverride!.Mode == ScopedRouteMode.Force)
            {
                capTrace.Add("force_refused:load_failed:no_substitution");
                result = Failure(normalizedTextRole!, "Forced model \"" + capOverride.ModelId
                    + "\" could not be loaded safely; no model was substituted. " + result.Message);
            }
            else if (!result.Success)
            {
                capTrace.Add("prefer_unavailable:" + (ClassifyCapabilityPreferenceReason(result.Message) ?? "reliability"));
                selectedEvidence = null;
                result = await ResolveAutomaticallyAsync().ConfigureAwait(false);
            }
        }
        else if (scopedForce)
        {
            result = await LoadScopedForceAsync(request, normalizedTextRole, ct).ConfigureAwait(false);
        }

        else if (scopedPrefer)
        {
            RuntimeLoadResult preferred = await LoadScopedForceAsync(new RouteRequest
            {
                Role = normalizedTextRole,
                Mode = RouteMode.Manual,
                ExactManagedModelFileName = request.PreferredManagedFileName,
                ScopedCandidateKey = request.PreferredCandidateKey,
                ScopedCandidateRevision = request.PreferredCandidateRevision
            }, normalizedTextRole, ct).ConfigureAwait(false);
            if (preferred.Success)
            {
                result = preferred;
                scopedPreferApplied = true;
            }
            else if (ClassifyCapabilityPreferenceReason(preferred.Message) is { } fallbackReason)
            {
                scopedPreferenceFallbackReason = fallbackReason;
                result = await ResolveAutomaticallyAsync().ConfigureAwait(false);
            }
            else
            {
                result = preferred;
            }
        }
        else if (exactRegisteredForced)
        {
            string requestedFile = request.ExactCandidateFileName!.Trim();
            bool safeSimpleName = IsSafeManagedFileName(requestedFile);
            selectedEvidence = safeSimpleName
                ? candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.FileName, requestedFile, StringComparison.OrdinalIgnoreCase))
                : null;

            if (normalizedTextRole == null)
                result = Failure(requestedRole, "Exact candidate override is supported only for text-model roles.");
            else if (selectedEvidence == null)
                result = Failure(normalizedTextRole,
                    "The forced model \"" + requestedFile + "\" is not a registered candidate for role \""
                    + normalizedTextRole + "\". PaRuX did not substitute another model.");
            else
                result = await _runtimeManager.EnsureExactCandidateLoadedAsync(
                    normalizedTextRole, selectedEvidence.FilePath, ct);
        }
        else if (exactManagedForced)
        {
            string requestedFile = request.ExactManagedModelFileName!.Trim();
            if (normalizedTextRole == null)
                result = Failure(requestedRole, "Exact managed-model override is supported only for text-model roles.");
            else if (!IsSafeManagedFileName(requestedFile))
                result = Failure(normalizedTextRole,
                    "The manual model lock is not a safe managed GGUF file name. PaRuX did not substitute another model.");
            else
            {
                try
                {
                    string managedPath = ModelDownloadService.ResolveManagedModelPath(requestedFile);
                    selectedEvidence = GetExactManagedModelEvidence(normalizedTextRole, managedPath);
                    result = await _runtimeManager.EnsureExactCandidateLoadedAsync(normalizedTextRole, managedPath, ct);
                }
                catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
                {
                    result = Failure(normalizedTextRole,
                        "The manually locked model \"" + requestedFile + "\" is unavailable: " + ex.Message
                        + " PaRuX did not substitute another model.");
                }
            }
        }
        else
        {
            result = await ResolveAutomaticallyAsync().ConfigureAwait(false);
        }

        bool fellBack = false;
        bool wasTextRoleRequest = normalizedTextRole != null;
        if (ShouldFallBackToStandard(exactForced || capOverride?.Mode == ScopedRouteMode.Force, scopedPrefer,
                scopedPreferenceFallbackReason is not null, result.Success, wasTextRoleRequest, result.Role))
        {
            fellBack = true;
            List<CandidateEvidenceDto> standardCandidates =
                await GetCandidateEvidenceAsync("standard", managedLibraryExpanded, ct);
            candidatesConsidered += standardCandidates.Count;
            result = await SelectAndLoadAsync(
                "standard", "standard", requestedRole, standardCandidates, mode,
                request.PromptText ?? request.TaskType, autoProfile, autoTuning, ct,
                evidence => selectedEvidence = evidence);
        }

        if (selectedEvidence == null && result.ModelPath != null)
        {
            selectedEvidence = candidates.FirstOrDefault(candidate => PathsEqual(candidate.FilePath, result.ModelPath));
            if (selectedEvidence == null && fellBack)
            {
                List<CandidateEvidenceDto> standardCandidates =
                    await GetCandidateEvidenceAsync("standard", managedLibraryExpanded, ct);
                selectedEvidence = standardCandidates.FirstOrDefault(candidate => PathsEqual(candidate.FilePath, result.ModelPath));
            }
        }

        stopwatch.Stop();
        string selectionReason = scopedForce ? "explicit_user_scoped_force_fresh_exact_candidate"
            : scopedPreferApplied ? "explicit_user_scoped_prefer_fresh_exact_candidate"
            : recommendedApplied ? "evidence_qualified_recommendation_fresh_exact_candidate"
            : ExplainSelection(
            mode, requestedRole, exactRegisteredForced, exactManagedForced,
            candidatesConsidered, selectedEvidence, autoProfile, requiredIntelligence,
            managedLibraryExpanded, autoTuning)
            + (recommendationSkip is null ? string.Empty : " (recommendation not used: " + recommendationSkip + ")");
        string decisionMessage = fellBack
            ? "Requested role \"" + requestedRole + "\" unavailable; fell back to standard. " + result.Message
            : result.Message;
        var decision = new RouteDecisionDto
        {
            Mode = mode,
            RequestedRole = !string.IsNullOrWhiteSpace(request.Role) ? requestedRole : null,
            TaskType = string.IsNullOrWhiteSpace(request.Role) ? request.TaskType : null,
            ResolvedRole = result.Role,
            FellBackToStandard = fellBack,
            Success = result.Success,
            Swapped = WasRuntimeSwapped(result.Success, result.Message),
            ModelPath = result.ModelPath,
            Message = decisionMessage,
            DurationMs = stopwatch.ElapsedMilliseconds,
            DecidedUtc = DateTime.UtcNow,
            CandidatesConsidered = candidatesConsidered,
            ExactCandidateForced = exactRegisteredForced,
            ExactManagedModelForced = exactManagedForced,
            AutoProfile = mode == RouteMode.Auto ? autoProfile : null,
            RequiredIntelligenceScore = requiredIntelligence,
            SelectedIntelligenceScore = selectedEvidence?.IntelligenceScore,
            SelectedBenchmarkTokensPerSecond = selectedEvidence?.BenchmarkTokensPerSecond,
            SelectionReason = selectionReason,
            FailureReasonCode = !result.Success && scopedForce
                ? ClassifyCapabilityPreferenceReason(result.Message) ?? "safety"
                : null
        };
        if (scopedPreferenceFallbackReason is { } reason)
            ApplyCapabilityPreferenceFallback(decision, reason, result.ModelPath ?? selectedEvidence?.FilePath);
        decision.EffectiveOverrideMode = scopedForce || exactManagedForced || exactRegisteredForced ? ScopedRouteMode.Force
            : scopedPrefer ? ScopedRouteMode.Prefer
            : capOverride?.Mode ?? ScopedRouteMode.Automatic;
        if (capOverride is { Mode: not ScopedRouteMode.Automatic })
        {
            decision.FallbackTrace.AddRange(capTrace);
            if (!decision.Success) decision.FailureReasonCode = capGate is { Success: false } ? capGate.RefusalCode : "load_failed";
            if (capGate is not null && capOverride.Mode == ScopedRouteMode.Prefer && (capTrace.Count > 0 || !capGate.Success))
                decision.Message = capGate.Explanation + " " + decision.Message;
            decision.SelectionReason = capGate is { Success: false } ? "capability_override_refused_" + capGate.RefusalCode
                : capOverride.Mode == ScopedRouteMode.Force
                    ? (decision.Success ? "capability_force_applied" : "capability_force_load_failed")
                    : "capability_prefer_" + (capTrace.Count > 0 ? "fallback" : "applied");
        }
        if (decision.PreferenceFallbackReason is { } fallbackCode)
            decision.FallbackTrace.Add("prefer_unavailable:" + fallbackCode + "->" + (decision.PreferenceFallbackModel ?? "none"));
        if (decision.FellBackToStandard) decision.FallbackTrace.Add("role_fallback:standard");
        if (!decision.Success && decision.EffectiveOverrideMode == ScopedRouteMode.Force)
            decision.FallbackTrace.Add("force_refused:" + (decision.FailureReasonCode ?? "unavailable") + ":no_substitution");

        _decisions.Enqueue(decision);
        while (_decisions.Count > MaxDecisionHistory) _decisions.TryDequeue(out _);
        return decision;
    }

    internal static bool WasRuntimeSwapped(bool success, string message) =>
        success && !message.Contains("reused, no swap", StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldFallBackToStandard(bool exactForced, bool scopedPrefer,
        bool preferenceFallbackAllowed, bool resultSuccess, bool wasTextRoleRequest, string? resolvedRole) =>
        !exactForced && (!scopedPrefer || preferenceFallbackAllowed) && !resultSuccess && wasTextRoleRequest
        && !string.Equals(resolvedRole, "standard", StringComparison.OrdinalIgnoreCase);

    internal static string? ClassifyCapabilityPreferenceReason(string message)
    {
        if (message.Contains("Gaming hard-off", StringComparison.OrdinalIgnoreCase)) return null;
        if (message.Contains("hardware budget", StringComparison.OrdinalIgnoreCase)
            || message.Contains("memory and Execution Profile budget", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not loadable on current hardware", StringComparison.OrdinalIgnoreCase))
            return "resource";
        if (message.Contains("identity is invalid", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no longer active or exact", StringComparison.OrdinalIgnoreCase))
            return "compatibility";
        if (message.Contains("could not be safely loaded", StringComparison.OrdinalIgnoreCase))
            return "reliability";
        return null;
    }

    internal static void ApplyCapabilityPreferenceFallback(RouteDecisionDto automaticDecision,
        string reason, string? fallbackModelPath)
    {
        ArgumentNullException.ThrowIfNull(automaticDecision);
        if (reason is not ("compatibility" or "resource" or "reliability"))
            throw new ArgumentOutOfRangeException(nameof(reason), "Unsupported capability preference fallback reason.");

        string? fallbackModel = Path.GetFileName(automaticDecision.ModelPath ?? fallbackModelPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fallbackModel)) fallbackModel = null;
        automaticDecision.PreferenceFallbackReason = reason;
        automaticDecision.PreferenceFallbackModel = fallbackModel;
        automaticDecision.SelectionReason = "scoped_preference_fallback_" + reason
            + (fallbackModel is null ? string.Empty : "_to_" + fallbackModel);
        automaticDecision.Message = "Preferred candidate was unavailable for " + reason + "; automatic fallback selected "
            + (fallbackModel ?? "no model") + ". " + automaticDecision.Message;
    }

    private async Task<RuntimeLoadResult> LoadScopedForceAsync(RouteRequest request,
        string? normalizedRole, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? stillWanted = null)
    {
        if (normalizedRole is null || _candidateEvidence is null
            || !ModelDiscoveryIdentity.IsSafeKey(request.ScopedCandidateKey)
            || string.IsNullOrWhiteSpace(request.ScopedCandidateRevision)
            || !IsSafeManagedFileName(request.ExactManagedModelFileName ?? string.Empty))
            return Failure(normalizedRole ?? request.Role ?? string.Empty, "The scoped Force identity is invalid; no model was substituted.");

        string file = request.ExactManagedModelFileName!;
        CandidateEvidenceIdentity? initial = _candidateEvidence.TryCaptureProvenanceSnapshot(
            request.ScopedCandidateKey!, file);
        if (initial is null || initial.Revision != request.ScopedCandidateRevision)
            return Failure(normalizedRole, "The scoped Force Candidate is no longer active or exact; no model was substituted.");

        string path = Path.Combine(_candidateEvidence.ModelsDirectory, file);
        try
        {
            RuntimeLoadResult loaded = await _runtimeManager.EnsureFreshExactCandidateLoadedAsync(
                normalizedRole, path, initial.LocalSha256,
                () => _candidateEvidence.TryOpenManagedFileReadLease(file, initial.SizeBytes),
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    // Evaluated after the writer is held: a recommendation that changed while we waited is not loaded.
                    bool wanted = stillWanted is null || await stillWanted(token).ConfigureAwait(false);
                    return wanted
                        && _candidateEvidence.IsProvenanceSnapshotCurrent(initial)
                        && _candidateEvidence.IsManagedFilePathCurrent(file, initial.SizeBytes);
                }, ct).ConfigureAwait(false);
            return loaded.Success ? loaded : Failure(normalizedRole,
                "The scoped Force Candidate could not be safely loaded because "
                + ExplainScopedForceFailure(loaded.Message) + ". No model was substituted.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return Failure(normalizedRole, "The scoped Force Candidate could not be safely loaded; no model was substituted.");
        }
    }

    private static string ExplainScopedForceFailure(string message)
    {
        if (message.Contains("Gaming hard-off", StringComparison.OrdinalIgnoreCase)) return "Gaming hard-off is active";
        if (message.Contains("Candidate bytes changed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Candidate identity changed", StringComparison.OrdinalIgnoreCase))
            return "the Candidate identity or bytes changed after selection";
        if (message.Contains("not loadable on current hardware", StringComparison.OrdinalIgnoreCase))
            return "the Candidate does not fit the current hardware budget";
        if (message.Contains("runtime could not be stopped", StringComparison.OrdinalIgnoreCase)
            || message.Contains("could not be confirmed stopped", StringComparison.OrdinalIgnoreCase))
            return "the resident runtime could not be stopped and verified safely";
        if (message.Contains("verification failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("changed during fresh load", StringComparison.OrdinalIgnoreCase))
            return "post-load checks did not confirm the exact resident bytes";
        if (message.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            return "the local model file or runtime is unavailable";
        return "the exact local runtime load failed";
    }

    private async Task<RuntimeLoadResult> SelectAndLoadAsync(
        string roleToLoad,
        string? normalizedTextRole,
        string rankingRole,
        IReadOnlyList<CandidateEvidenceDto> candidates,
        string mode,
        string? promptText,
        string autoProfile,
        AutoRoutingTuningDto? autoTuning,
        CancellationToken ct,
        Action<CandidateEvidenceDto?> capture)
    {
        bool automaticMode = mode is RouteMode.Auto or RouteMode.Smart or RouteMode.Quality or RouteMode.Performance or RouteMode.LowVram;
        if (automaticMode && normalizedTextRole != null && candidates.Count > 0)
        {
            if (candidates.All(candidate => candidate.HardwareCompatible == false))
                return Failure(normalizedTextRole, "No candidate fits the current memory and Execution Profile budget. Free memory or choose a smaller model.");
            IReadOnlyList<CandidateEvidenceDto> pool = mode == RouteMode.Auto
                ? AutoTrivialTurnPolicy.Narrow(candidates, promptText, rankingRole, autoProfile)
                : candidates;
            CandidateEvidenceDto? selected = CandidatePoolRanker.Rank(
                pool, mode, rankingRole, promptText, autoProfile, autoTuning);
            capture(selected);
            if (selected != null)
                return await _runtimeManager.EnsureExactCandidateLoadedAsync(normalizedTextRole, selected.FilePath, ct);
        }

        capture(null);
        return await _runtimeManager.EnsureRoleLoadedAsync(roleToLoad, ct);
    }

    private static bool IsSafeManagedFileName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
        && !value.Contains('/') && !value.Contains('\\')
        && value.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    private static RuntimeLoadResult Failure(string role, string message) => new()
    {
        Success = false,
        Role = role,
        Message = message
    };

    private static string ExplainSelection(
        string mode,
        string? role,
        bool exactRegisteredForced,
        bool exactManagedForced,
        int candidateCount,
        CandidateEvidenceDto? selected,
        string autoProfile,
        double? requiredIntelligence,
        bool managedLibraryExpanded,
        AutoRoutingTuningDto? autoTuning)
    {
        if (exactManagedForced)
            return selected == null
                ? "Persistent manual model lock requested; automatic substitution was disabled."
                : "Persistent manual model lock bypassed role ranking and automatic intelligence routing.";
        if (exactRegisteredForced)
            return selected == null
                ? "Exact user override requested; no substitution was allowed."
                : "Exact user override bypassed automatic model selection.";
        if (candidateCount <= 1)
            return candidateCount == 0
                ? "No candidate pool exists; using the configured role assignment."
                : "Only one candidate exists for this role; no ranking was necessary.";
        if (selected == null) return "No ranked candidate produced a loadable selection.";

        string quality = selected.IntelligenceScore.HasValue
            ? "role capability estimate " + selected.IntelligenceScore.Value.ToString("0.000")
                + " (" + selected.IntelligenceEvidence + ")"
            : "capability unmeasured";
        string speed = selected.BenchmarkTokensPerSecond.HasValue
            ? selected.BenchmarkTokensPerSecond.Value.ToString("0.0") + " tok/s"
            : "speed unmeasured";
        string scope = managedLibraryExpanded
            ? " across the registered role pool plus the Managed Model Library"
            : string.Empty;
        string custom = autoProfile == AutoIntelligenceProfile.Custom && autoTuning != null
            ? "; custom floor " + autoTuning.MinimumCapabilityRatio.ToString("0.00")
                + " + complexity×" + autoTuning.ComplexityFloorBoost.ToString("0.00")
                + ", base capability/performance " + autoTuning.BaseCapabilityWeight.ToString("0.00")
                + "/" + autoTuning.BasePerformanceWeight.ToString("0.00")
            : string.Empty;
        return mode switch
        {
            RouteMode.Auto => "AUTO/" + autoProfile + " estimated task intelligence demand "
                + (requiredIntelligence ?? 0).ToString("0.00") + " for " + (role ?? "standard")
                + scope + ", applied a capability floor first, then balanced " + quality + ", " + speed
                + ", hardware fit and swap cost" + custom + "."
                + (requiredIntelligence is <= AutoTrivialTurnPolicy.TrivialDemandCeiling && autoProfile is not (AutoIntelligenceProfile.MaxIntelligence or AutoIntelligenceProfile.Custom)
                    ? " Trivial turn: preferred the already-loaded or fast-loading adequate model over swapping to a larger one."
                    : string.Empty),
            RouteMode.Quality => "QUALITY selected by " + quality + "; speed is only a tie-break (" + speed + ").",
            RouteMode.Performance => "PERFORMANCE selected by measured throughput (" + speed + "); capability remains separate (" + quality + ").",
            RouteMode.LowVram => "LOW_VRAM selected the smallest hardware-compatible candidate; " + quality + ".",
            RouteMode.Smart => "SMART used role-aware weights for " + (role ?? "standard") + ": " + quality + ", " + speed + ", hardware fit and swap cost.",
            _ => "Using the configured manual role assignment."
        };
    }

    public List<RouteDecisionDto> ListDecisions() => _decisions.Reverse().ToList();
}
