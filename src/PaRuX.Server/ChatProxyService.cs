using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server;

public sealed class ChatProxyService
{
    internal const int MaxMcpToolCallsPerRound = 4;
    internal const int MaxMcpToolCallsPerTurn = 8;
    internal const int MaxMcpToolResultCharactersPerTurn = 16_000;
    private static readonly HttpClient Http = new(ManagedRuntimePeers.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int ReplyReserveTokens = 1024;
    private const int FallbackDecisionThreshold = 240;
    private const int MaxExactPromptReplans = 4;

    // Keep the refusal probe bounded without turning ordinary streaming replies into a blank
    // 600-ms prelude. Refusal markers appear in the first few dozen characters; 100 ms is enough
    // to observe them on a local provider while keeping the user-visible first frame inside the
    // transport overhead budget.
    internal static TimeSpan FallbackDecisionWindow { get; private set; } = TimeSpan.FromMilliseconds(100);

    internal static IDisposable OverrideFallbackDecisionWindowForTests(TimeSpan window)
    {
        TimeSpan previous = FallbackDecisionWindow;
        FallbackDecisionWindow = window;
        return new WindowOverride(() => FallbackDecisionWindow = previous);
    }

    private sealed class WindowOverride : IDisposable
    {
        private readonly Action _restore;
        public WindowOverride(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }

    private static readonly string SwitchedEventLine =
        "data: " + JsonSerializer.Serialize(new { parux_event = "switched_uncensored" }) + "\n\n";

    private readonly LocalRuntimeService _runtime;
    private readonly IModalityChatBackend? _modality;
    private readonly IRoleRouter _router;
    private readonly AiProfileService _profiles;
    private readonly ContextEngineService _context;
    private readonly SkillRouter _skills;
    private readonly PersonaService _personas;
    private readonly McpClientRegistry _mcp;
    private readonly LocalInferenceChatBridge _engineBridge;
    private readonly CloudFallbackService _cloudFallback;
    private readonly PromptCacheScopePolicy _promptCache = new();
    private readonly RuntimeTokenCounter _tokenCounter = new(Http);

    public ChatProxyService(
        LocalRuntimeService runtime, IRoleRouter router, AiProfileService profiles,
        ContextEngineService context, SkillRouter skills, PersonaService personas, McpClientRegistry mcp,
        ModelDownloadService models, LocalAiProcessManager process, CloudFallbackService cloudFallback,
        IModalityChatBackend? modality = null)
    {
        _modality = modality;
        _runtime = runtime;
        _cloudFallback = cloudFallback;
        _router = router;
        _profiles = profiles;
        _context = context;
        _skills = skills;
        _personas = personas;
        _mcp = mcp;
        _engineBridge = new LocalInferenceChatBridge(
            LocalInferenceEngineSharedServices.GetOrCreateCoordinator(models, process),
            runtime);
    }

    public async Task ProxyAsync(ChatCompletionRequest request, HttpResponse downstream, CancellationToken cancellationToken)
    {
        if (ChatSamplingOptions.Validate(request) is { } invalidSampling)
        {
            downstream.StatusCode = StatusCodes.Status400BadRequest;
            await downstream.WriteAsJsonAsync(new
            {
                error = new { message = invalidSampling, type = "invalid_request_error" }
            }, cancellationToken);
            return;
        }

        // Audio / tool-use / computer-use turns are routed before any text runtime is touched. A handled turn is already
        // answered (or refused with a typed error); tool use continues below pinned to a model with verified tool calling.
        ModalityChatResult modality = await ModalityChatGate.TryHandleAsync(request, _modality, downstream, cancellationToken);
        if (modality.Handled) return;

        bool hasVisualInput = ContainsVisualInput(request);
        string? forcedRouteIneligibility = GetForcedRouteIneligibility(
            request.ForcedModelRoute, _runtime.UsesExternalProvider, hasVisualInput);
        if (forcedRouteIneligibility != null)
        {
            downstream.StatusCode = StatusCodes.Status409Conflict;
            await downstream.WriteAsJsonAsync(new
            {
                error = new { message = forcedRouteIneligibility }
            }, cancellationToken);
            return;
        }

        if (!_runtime.ManagedRuntimeOwned)
        {
            // A cold PaRuX runtime has no listener yet, which is different from an external
            // listener occupying the port. Give the managed lifecycle one chance to start its
            // configured resident before deciding that the port belongs to somebody else. This
            // keeps cold chat/concurrent first-use functional while the ownership check below
            // remains the final prompt-egress boundary.
            try
            {
                await _runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (TextRuntimeStateUnavailableException unavailable)
            {
                ChatProxyFailureResponse.AddRetryHint(downstream);
                await TextRuntimeUnavailableResponse.TryWriteAsync(downstream, unavailable, cancellationToken);
                return;
            }
            catch (Exception exception)
            {
                await ChatProxyFailureResponse.WriteRuntimeUnavailableAsync(
                    downstream, exception.Message, cancellationToken);
                return;
            }

            if (!_runtime.ManagedRuntimeOwned)
            {
                downstream.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await downstream.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        message = "Something else is using PaRuX's runtime port (127.0.0.1:" + LocalAiPaths.TextPort + ") and PaRuX "
                            + "did not start it, so it will not send your message there. Close the other "
                            + "program using that port - LM Studio serves on 1234 by default - then load "
                            + "a model again in Runtime. To use a runtime you manage yourself on purpose, "
                            + "configure it as an external provider instead."
                    }
                }, cancellationToken);
                return;
            }
        }

        if (request.ContinueFinalMessage)
            ApplyContinuation(new JsonObject(), request, _runtime.UsesExternalProvider);

        ChatCompletionMessage? lastUser = request.Messages.LastOrDefault(m => m.Role == "user");
        SkillRouteResult routed = _skills.Route(lastUser?.Content ?? string.Empty, request.SkillId);
        if (routed.Skill != null) ApplySkillPromptShaping(request, routed.Skill, request.SkillParameters);

        LocalStructuredOutputRequest? structuredOutput = StructuredOutputRequestTransport.TryRead(request);
        bool structuredOutputRequested = structuredOutput != null;
        string? structuredOutputFallbackReason = null;

        if (structuredOutput != null && _runtime.UsesExternalProvider)
        {
            ExternalTextProviderService.StructuredOutputProbeEvidence evidence =
                await ExternalTextProviderService.ProbeStructuredOutputAsync(
                    _runtime.ProviderEndpoint, cancellationToken).ConfigureAwait(false);
            if (!evidence.Supported)
            {
                StructuredOutputRequestTransport.RemoveReserved(request.SkillParameters);
                structuredOutputFallbackReason = evidence.Detail;
                structuredOutput = null;
            }
        }

        if (request.ForcedModelRoute == null && !request.ContinueFinalMessage && structuredOutput == null
            && LocalClockAnswer.TryCreate(lastUser?.Content, DateTimeOffset.Now, TimeZoneInfo.Local, out string clockAnswer))
        {
            await WriteDirectAnswerAsync(clockAnswer, downstream, cancellationToken);
            return;
        }
        if (request.ForcedModelRoute == null && !request.ContinueFinalMessage
            && LocalArithmeticAnswer.TryCreate(lastUser?.Content, out string arithmeticAnswer))
        {
            await WriteDirectAnswerAsync(arithmeticAnswer, downstream, cancellationToken);
            return;
        }

        AiProfileStatus profile = await _profiles.GetStatusAsync(cancellationToken);
        ChatRoutingPreferenceStore.Preference routingPreference = ChatRoutingPreferenceStore.ResolveAndPersist(request.Model);
        RouteDecisionDto? modelRoute = null;
        bool scopedForce = false;

        if (!_runtime.UsesExternalProvider && !hasVisualInput)
        {
            string? profileRole = string.Equals(profile.TextProfile, "UNCENSORED", StringComparison.OrdinalIgnoreCase)
                ? "uncensored" : null;
            ScopedRoutingOverrideDto? scoped = null;
            if (routingPreference.Mode != RouteMode.Manual && profileRole is null)
            {
                // The scope key is exactly the router's existing prompt mapping; unknown/future
                // scoped state fails closed to the original AUTO request.
                string mapped = SmartRouterService.MapTaskTypeToRole(lastUser?.Content);
                if (LocalAiPaths.NormalizeTextRole(mapped) is { } role)
                {
                    try { scoped = CapabilityRoutingPreferenceStore.Get(role, routingPreference.AutoProfile); }
                    catch (InvalidDataException) { }
                }
            }
            RouteRequest routeRequest = BuildRouteRequest(
                routingPreference, profileRole, lastUser?.Content, scoped, request);
            scopedForce = request.ForcedModelRoute != null || scoped?.Mode == ScopedRouteMode.Force;

            modelRoute = await _router.ResolveAsync(routeRequest, cancellationToken);
            if (!modelRoute.Success)
                throw new InvalidOperationException("PaRuX model routing failed"
                    + (modelRoute.EffectiveOverrideMode is "force" or "prefer" && modelRoute.FailureReasonCode is { } routeFailureCode
                        ? " [" + routeFailureCode + "]" : string.Empty) + ": " + modelRoute.Message);
        }

        request.Model = ResolveProviderModel(modelRoute);

        bool manualModelLock = routingPreference.Mode == RouteMode.Manual || scopedForce;
        bool considerFallback = profile.AutoUncensoredFallback
            && profile.TextProfile == "STANDARD"
            && !profile.GamingMode
            && !manualModelLock;

        bool useLowLatencyPath = ShouldUseLowLatencyPath(request, routed, hasVisualInput);
        if (useLowLatencyPath) considerFallback = false;
        ApplyPersonaPrompt(request, _personas.GetActive());

        List<string> sources;
        JsonArray mcpTools;
        if (useLowLatencyPath)
        {
            sources = new List<string>();
            mcpTools = new JsonArray();
        }
        else
        {
            ChatContextResult context = await _context.BuildForChatAsync(
                lastUser?.Content ?? string.Empty, routed.Skill, request.ConversationId,
                request.IncludeConversationHistory, cancellationToken);
            ApplyContextPack(request, context.Pack);
            sources = DisplaySources(context.Pack);
            mcpTools = context.ToolSchemas;
        }

        // Snapshot the physical context while the baseline resident is still available. A direct
        // engine transition may legitimately stop llama.cpp before prompt fitting; querying /props
        // afterwards would turn a known context into zero and silently skip the safety gate.
        int contextWindowSnapshot = await ChatContextWindowSnapshot.CaptureAsync(
            Http, _runtime.ProviderEndpoint, modelRoute, cancellationToken).ConfigureAwait(false);

        LocalInferenceChatPreparation enginePreparation;
        try
        {
            enginePreparation = await _engineBridge.PrepareAsync(
                request,
                modelRoute?.ModelPath,
                manualModelLock,
                hasVisualInput,
                requiresToolCalling: mcpTools.Count > 0,
                requiresProviderFallback: considerFallback,
                hasRetrievalContext: sources.Count > 0,
                requiresStructuredOutput: structuredOutput != null,
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception localFailure)
        {
            if (!manualModelLock && await TryServeFromCloudFallbackAsync(request, downstream, localFailure, cancellationToken))
                return;
            throw;
        }

        ConversationWindowPlanner.WindowPlan? window = await FitConversationWindowAsync(
            request, mcpTools, structuredOutput, contextWindowSnapshot, enginePreparation, cancellationToken, modelRoute?.ModelPath)
            .ConfigureAwait(false);

        HashSet<string> advertisedToolNames = mcpTools
            .Select(tool => tool?["function"]?["name"]?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        StreamingMcpToolLoop? toolLoop = null;
        HttpResponseMessage? toolLoopFirstTurn = null;
        if (mcpTools.Count > 0)
        {
            toolLoop = await CreateMcpToolLoopAsync(
                request, mcpTools, advertisedToolNames, contextWindowSnapshot, downstream, cancellationToken)
                .ConfigureAwait(false);
            // Opened before the response starts so growth / transport failures keep their real status code.
            toolLoopFirstTurn = await toolLoop.OpenFirstAsync(cancellationToken).ConfigureAwait(false);
        }

        downstream.StatusCode = StatusCodes.Status200OK;
        downstream.ContentType = "text/event-stream";
        downstream.Headers.CacheControl = "no-cache";
        downstream.Headers.Append("X-Accel-Buffering", "no");

        if (structuredOutputRequested)
        {
            string structuredLine = "data: " + JsonSerializer.Serialize(new
            {
                parux_event = "structured_output",
                applied = structuredOutput != null,
                name = structuredOutput?.Name,
                provider = _runtime.UsesExternalProvider ? "user-managed" : "parux-managed-llama",
                fallbackReason = structuredOutputFallbackReason
            }) + "\n\n";
            await downstream.WriteAsync(structuredLine, cancellationToken);
            await downstream.Body.FlushAsync(cancellationToken);
        }

        if (modelRoute != null)
        {
            string routeLine = "data: " + BuildModelRoutedEvent(
                modelRoute, enginePreparation, manualModelLock, structuredOutput != null,
                request.Model, _runtime.UsesExternalProvider, _runtime.ProviderEndpoint, modality.Route).ToJsonString() + "\n\n";
            await downstream.WriteAsync(routeLine, cancellationToken);
            await downstream.Body.FlushAsync(cancellationToken);
        }

        if (routed.Skill != null)
        {
            string skillLine = "data: " + JsonSerializer.Serialize(new
            {
                parux_event = "skill_selected",
                skillId = routed.Skill.Id,
                displayName = routed.Skill.DisplayName,
                mode = routed.Mode,
                reason = routed.Reason
            }) + "\n\n";
            await downstream.WriteAsync(skillLine, cancellationToken);
            await downstream.Body.FlushAsync(cancellationToken);
        }

        if (sources.Count > 0)
        {
            string sourcesLine = "data: " + JsonSerializer.Serialize(new { parux_event = "rag_sources", files = sources }) + "\n\n";
            await downstream.WriteAsync(sourcesLine, cancellationToken);
            await downstream.Body.FlushAsync(cancellationToken);
        }

        if (window is { Compacted: true })
        {
            string compactedLine = "data: " + JsonSerializer.Serialize(new
            {
                parux_event = "context_compacted",
                keptMessages = window.KeptTurns,
                summarisedMessages = window.CompactedTurns,
                usedTokens = window.EstimatedTokens,
                availableTokens = window.AvailableTokens,
                detail = window.Explanation
            }) + "\n\n";
            await downstream.WriteAsync(compactedLine, cancellationToken);
            await downstream.Body.FlushAsync(cancellationToken);
        }

        if (toolLoop != null && toolLoopFirstTurn != null)
        {
            await toolLoop.RunAsync(toolLoopFirstTurn, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (enginePreparation.Selection != null)
        {
            LocalInferenceChatStreamResult engineResult = await _engineBridge.StreamAsync(
                enginePreparation, request, downstream, cancellationToken);
            if (engineResult.Completed || !engineResult.CanFallbackToBaseline) return;

            await _runtime.EnsureReadyAsync(cancellationToken);
            string fallbackLine = "data: " + JsonSerializer.Serialize(new
            {
                parux_event = "engine_fallback",
                from = enginePreparation.Selection.Engine.Descriptor.Id,
                to = "llama.cpp",
                reason = Trim(engineResult.Error ?? "Direct engine failed before first output.")
            }) + "\n\n";
            await downstream.WriteAsync(fallbackLine, cancellationToken);
            await downstream.Body.FlushAsync(cancellationToken);

            HttpResponseMessage baseline;
            try { baseline = await OpenUpstreamAsync(request, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception localFailure)
            {
                if (await TryServeFromCloudFallbackAsync(request, downstream, localFailure, cancellationToken))
                    return;
                if (await TryReportStartedStreamFailureAsync(downstream, localFailure, cancellationToken)) return;
                throw;
            }
            using (baseline)
            {
                await using Stream baselineStream = await baseline.Content.ReadAsStreamAsync(cancellationToken);
                using var baselineReader = new StreamReader(baselineStream, Encoding.UTF8);
                await PumpRawAsync(baselineReader, downstream, cancellationToken);
            }
            return;
        }

        HttpResponseMessage upstream;
        try { upstream = await OpenUpstreamAsync(request, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception localFailure)
        {
            if (await TryServeFromCloudFallbackAsync(request, downstream, localFailure, cancellationToken))
                return;
            if (await TryReportStartedStreamFailureAsync(downstream, localFailure, cancellationToken)) return;
            throw;
        }

        using (upstream)
        {
            await using Stream stream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            if (considerFallback) await StreamWithFallbackAsync(reader, request, downstream, cancellationToken);
            else await PumpRawAsync(reader, downstream, cancellationToken);
        }
    }

    private Task<ConversationWindowPlanner.WindowPlan?> FitConversationWindowAsync(
        ChatCompletionRequest request,
        JsonArray toolSchemas,
        LocalStructuredOutputRequest? structuredOutput,
        int contextTokens,
        LocalInferenceChatPreparation enginePreparation,
        CancellationToken cancellationToken,
        string? modelPath = null) =>
        FitConversationWindowCoreAsync(
            request, toolSchemas, structuredOutput, contextTokens,
            directEngine: enginePreparation.Selection != null,
            (texts, token) => _tokenCounter.PrepareWithBasisAsync(
                _runtime.ProviderEndpoint, request.Model ?? string.Empty, texts, token, modelPath),
            token => TryCountFinalRenderedPromptAsync(request, toolSchemas, enginePreparation, token),
            cancellationToken);

    /// <summary>
    /// Plans the conversation window and, near the window, re-plans against the runtime's exact
    /// rendered-prompt count (tokenizer, tools, template) until it fits or provably cannot, in which
    /// case the typed 413 is thrown. Dependencies are injected so the budget logic is testable with
    /// fake tokenizers.
    /// </summary>
    internal static async Task<ConversationWindowPlanner.WindowPlan?> FitConversationWindowCoreAsync(
        ChatCompletionRequest request,
        JsonArray toolSchemas,
        LocalStructuredOutputRequest? structuredOutput,
        int contextTokens,
        bool directEngine,
        Func<IEnumerable<string>, CancellationToken, Task<RuntimeTokenCounter.PreparedCounter>> prepareCounter,
        Func<CancellationToken, Task<int?>> countFinalRendered,
        CancellationToken cancellationToken)
    {
        if (request.Messages == null || request.Messages.Count == 0 || contextTokens <= 0) return null;

        List<ChatCompletionMessage> originalMessages = request.Messages.ToList();
        var turns = originalMessages
            .Select(message => new ConversationWindowPlanner.Turn(message.Role, message.Content))
            .ToList();
        IReadOnlyList<string> fixedPromptFragments = ChatPromptBudget.BuildFixedFragments(toolSchemas, structuredOutput);
        string[] pricingCorpus = turns.Select(turn => turn.Content)
            .Concat(fixedPromptFragments)
            .Where(text => !string.IsNullOrEmpty(text))
            .ToArray();

        int certainCeiling = turns.Sum(turn => turn.Content?.Length ?? 0)
            + (turns.Count * RuntimeTokenCounter.PerMessageTemplateOverheadTokens)
            + fixedPromptFragments.Sum(fragment => fragment.Length);

        Func<string, int>? count = null;
        RuntimeTokenCounter.CountBasis countBasis = RuntimeTokenCounter.CountBasis.Estimated;
        if (!directEngine
            && (fixedPromptFragments.Count > 0 || certainCeiling > contextTokens - ReplyReserveTokens))
        {
            RuntimeTokenCounter.PreparedCounter prepared = await prepareCounter(pricingCorpus, cancellationToken)
                .ConfigureAwait(false);
            count = prepared.Count;
            countBasis = prepared.Basis;
        }

        int fixedPromptTokens = fixedPromptFragments.Count == 0
            ? 0
            : ChatPromptBudget.CountFixedTokens(
                fixedPromptFragments,
                count ?? ConversationWindowPlanner.EstimateTokens);
        int effectiveContextTokens = ChatProxyFailureResponse.GetEffectiveMessageContextTokensOrThrowTypedFailure(
            contextTokens, fixedPromptTokens, ReplyReserveTokens);

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, effectiveContextTokens, ReplyReserveTokens, count);
        ApplyWindowPlan(request, originalMessages, plan);

        int maximumRenderedPromptTokens = Math.Max(1, contextTokens - ReplyReserveTokens);

        // The digest/virtual-memory text was synthesized after the corpus above was tokenized, so
        // the planner priced it with an observed characters-per-token ratio. Tokenize the plan's
        // actual messages (cached afterwards) so the budget uses real counts for what is sent.
        bool pricedOverBudget = false;
        if (count != null && plan.Compacted)
        {
            for (int pricing = 0; pricing <= 2; pricing++)
            {
                RuntimeTokenCounter.PreparedCounter repriced = await prepareCounter(
                        plan.Messages.Select(message => message.Content), cancellationToken)
                    .ConfigureAwait(false);
                count = repriced.Count;
                countBasis = repriced.Basis;
                int priced = plan.Messages.Sum(message => count(message.Content)) + fixedPromptTokens;
                pricedOverBudget = priced > maximumRenderedPromptTokens;
                // The pass after the last refit only measures; it never refits.
                if (!pricedOverBudget || pricing == 2) break;
                try
                {
                    effectiveContextTokens = ChatPromptBudget.TightenMessageContextTokens(
                        effectiveContextTokens, priced, maximumRenderedPromptTokens, ReplyReserveTokens);
                }
                catch (InvalidDataException) { break; } // pricedOverBudget stays true
                plan = ConversationWindowPlanner.Fit(
                    turns, effectiveContextTokens, ReplyReserveTokens, count);
                ApplyWindowPlan(request, originalMessages, plan);
            }
        }

        bool verifyExact = pricedOverBudget
            || fixedPromptFragments.Count > 0
            || plan.Compacted
            || certainCeiling >= Math.Max(1, maximumRenderedPromptTokens - 512);
        if (!verifyExact) return plan;

        bool estimatorPriced = count == null;
        for (int attempt = 0; attempt < MaxExactPromptReplans; attempt++)
        {
            int? exactRendered = await countFinalRendered(cancellationToken).ConfigureAwait(false);
            if (!exactRendered.HasValue)
            {
                return LabelUnverifiedPlan(request, originalMessages, turns, plan, effectiveContextTokens,
                    fixedPromptTokens, maximumRenderedPromptTokens, count, countBasis, pricedOverBudget);
            }
            if (exactRendered.Value <= maximumRenderedPromptTokens) return plan;

            effectiveContextTokens = ChatProxyFailureResponse.TightenExactContextOrThrowTypedFailure(
                effectiveContextTokens,
                exactRendered.Value,
                maximumRenderedPromptTokens,
                ReplyReserveTokens,
                plan,
                fixedPromptTokens,
                pricedOverBudget);
            // An estimator-priced plan (no tokenizer count: direct engine, or a prompt the char bound
            // proved small) can under-price the runtime's rendering by a large factor, in which case
            // shaving a few tokens off the budget never changes the plan. Calibrate the estimator
            // to the runtime's exact count so each re-plan actually converges.
            if (estimatorPriced)
            {
                int planned = Math.Max(1, plan.EstimatedTokens + fixedPromptTokens);
                double scale = (double)exactRendered.Value / planned;
                if (scale > 1.0)
                {
                    Func<string, int> baseCount = count ?? ConversationWindowPlanner.EstimateTokens;
                    count = text => (int)Math.Ceiling(baseCount(text) * scale);
                }
            }
            plan = ConversationWindowPlanner.Fit(
                turns, effectiveContextTokens, ReplyReserveTokens, count);
            ApplyWindowPlan(request, originalMessages, plan);
        }

        int? finalCount = await countFinalRendered(cancellationToken).ConfigureAwait(false);
        if (!finalCount.HasValue)
        {
            return LabelUnverifiedPlan(request, originalMessages, turns, plan, effectiveContextTokens,
                fixedPromptTokens, maximumRenderedPromptTokens, count, countBasis, pricedOverBudget);
        }
        PromptExceedsContextException? failure = ChatProxyFailureResponse.CreatePromptExceedsContextFailure(
            plan,
            fixedPromptTokens,
            maximumRenderedPromptTokens,
            exactRenderedPromptTokens: finalCount.Value,
            pricedOverBudget,
            tightened: false);
        if (failure is not null) throw failure;
        return plan;
    }

    /// <summary>
    /// No exact rendered-prompt count was available for a near-window prompt. Reserve a window-scaled
    /// margin against the estimate and say plainly that the size is not exact.
    /// </summary>
    private static ConversationWindowPlanner.WindowPlan LabelUnverifiedPlan(
        ChatCompletionRequest request,
        List<ChatCompletionMessage> originalMessages,
        List<ConversationWindowPlanner.Turn> turns,
        ConversationWindowPlanner.WindowPlan plan,
        int effectiveContextTokens,
        int fixedPromptTokens,
        int maximumRenderedPromptTokens,
        Func<string, int>? count,
        RuntimeTokenCounter.CountBasis basis,
        bool pricedOverBudget)
    {
        bool tightened = ChatPromptBudget.TryTightenForUnverifiedCount(
            effectiveContextTokens, plan.EstimatedTokens + fixedPromptTokens,
            maximumRenderedPromptTokens, ReplyReserveTokens, out int tighterContext);
        if (tightened)
        {
            plan = ConversationWindowPlanner.Fit(turns, tighterContext, ReplyReserveTokens, count);
            ApplyWindowPlan(request, originalMessages, plan);
        }

        // A label is not a licence to ship an over-budget prompt: without an exact count to
        // overrule it, tokenizer/estimate pricing that still exceeds the window is an error.
        PromptExceedsContextException? failure = ChatProxyFailureResponse.CreatePromptExceedsContextFailure(
            plan,
            fixedPromptTokens,
            maximumRenderedPromptTokens,
            exactRenderedPromptTokens: null,
            pricedOverBudget,
            tightened);
        if (failure is not null) throw failure;
        return plan with
        {
            Explanation = plan.Explanation + " " + ChatPromptBudget.DescribeUnverifiedCount(
                basis, ChatPromptBudget.UnverifiedSafetyMarginTokens(maximumRenderedPromptTokens), tightened)
        };
    }

    private async Task<int?> TryCountFinalRenderedPromptAsync(
        ChatCompletionRequest request,
        JsonArray toolSchemas,
        LocalInferenceChatPreparation preparation,
        CancellationToken cancellationToken)
    {
        if (preparation.Selection != null)
        {
            return await CountDirectRenderedPromptAsync(
                request, toolSchemas, preparation, cancellationToken).ConfigureAwait(false);
        }

        JsonObject payload = await PrepareProviderPayloadAsync(request, stream: false, cancellationToken).ConfigureAwait(false);
        if (toolSchemas.Count > 0) payload["tools"] = toolSchemas.DeepClone();
        return await _tokenCounter.TryCountRenderedChatPromptAsync(
            _runtime.ProviderEndpoint, payload, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<int?> CountDirectRenderedPromptAsync(
        ChatCompletionRequest request,
        JsonArray availableToolSchemas,
        LocalInferenceChatPreparation preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(availableToolSchemas);
        ArgumentNullException.ThrowIfNull(preparation);
        if (preparation.Selection is not { } selection
            || selection.Engine is not ILocalInferencePromptTokenCounter counter)
            return null;

        // Direct-engine generation receives LocalInferenceRequest, not MCP schemas. The MCP
        // provider loop separately counts its own provider payload including tools; still count
        // this exact final request when tools were available but unused or the tool loop failed.
        LocalInferenceRequest localRequest = LocalInferenceChatBridge.BuildEngineRequest(
            preparation, request, stream: false);
        try
        {
            int count = await counter.CountPromptTokensAsync(localRequest, cancellationToken).ConfigureAwait(false);
            return count > 0 ? count : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static void ApplyWindowPlan(
        ChatCompletionRequest request,
        IReadOnlyList<ChatCompletionMessage> originalMessages,
        ConversationWindowPlanner.WindowPlan plan)
    {
        var rebuilt = new List<ChatCompletionMessage>(plan.Messages.Count);
        var used = new bool[originalMessages.Count];
        foreach (ConversationWindowPlanner.Turn turn in plan.Messages)
        {
            int match = -1;
            for (int i = 0; i < originalMessages.Count; i++)
            {
                if (used[i]) continue;
                ChatCompletionMessage candidate = originalMessages[i];
                if (string.Equals(candidate.Role, turn.Role, StringComparison.Ordinal)
                    && string.Equals(candidate.Content, turn.Content, StringComparison.Ordinal))
                {
                    match = i;
                    break;
                }
            }

            if (match >= 0)
            {
                used[match] = true;
                rebuilt.Add(originalMessages[match]);
            }
            else
            {
                rebuilt.Add(new ChatCompletionMessage { Role = turn.Role, Content = turn.Content });
            }
        }
        request.Messages = rebuilt;
    }

    internal static RouteRequest BuildRouteRequest(ChatRoutingPreferenceStore.Preference preference,
        string? profileRole, string? lastUserPrompt, ScopedRoutingOverrideDto? scoped,
        ChatCompletionRequest? chatRequest = null)
    {
        RouteRequest? forcedNextChatRoute = chatRequest?.ForcedModelRoute;
        if (forcedNextChatRoute != null)
        {
            // An exact registered candidate and an exact managed-library model are distinct contracts.
            // Synthesizing both from one field routes a managed-only GGUF through the registered-candidate
            // gate and falsely rejects a perfectly valid local model on a clean install.
            return new RouteRequest
            {
                Role = forcedNextChatRoute.Role,
                Mode = RouteMode.Manual,
                ExactCandidateFileName = forcedNextChatRoute.ExactCandidateFileName,
                ExactManagedModelFileName = forcedNextChatRoute.ExactManagedModelFileName,
                ScopedCandidateKey = forcedNextChatRoute.ScopedCandidateKey,
                ScopedCandidateRevision = forcedNextChatRoute.ScopedCandidateRevision
            };
        }

        if (preference.Mode == RouteMode.Manual)
            return new RouteRequest
            {
                Role = profileRole ?? "standard", Mode = RouteMode.Manual,
                ExactManagedModelFileName = preference.ManualModelFileName
            };
        if (profileRole is null && scoped?.Mode == ScopedRouteMode.Force)
            return new RouteRequest
            {
                Role = scoped.RouteRole, Mode = RouteMode.Manual,
                ExactManagedModelFileName = scoped.ManagedFileName,
                ScopedCandidateKey = scoped.CandidateKey,
                ScopedCandidateRevision = scoped.Revision
            };
        if (profileRole is null && scoped?.Mode == ScopedRouteMode.Prefer)
            return new RouteRequest
            {
                Mode = RouteMode.Auto, AutoProfile = preference.AutoProfile, PromptText = lastUserPrompt,
                PreferredCandidateKey = scoped.CandidateKey,
                PreferredCandidateRevision = scoped.Revision,
                PreferredManagedFileName = scoped.ManagedFileName
            };
        // An absent or Automatic row retains the original Role=null AUTO pool behavior.
        return new RouteRequest
        {
            Role = profileRole, Mode = RouteMode.Auto,
            AutoProfile = preference.AutoProfile, PromptText = lastUserPrompt
        };
    }

    internal static string? GetForcedRouteIneligibility(
        RouteRequest? forcedModelRoute, bool usesExternalProvider, bool hasVisualInput)
    {
        if (forcedModelRoute == null) return null;
        if (usesExternalProvider)
            return "The forced model is a local model and cannot be used with the configured external provider.";
        if (hasVisualInput)
            return "The forced text model cannot be used for a visual chat turn.";
        return null;
    }

    internal static bool ContainsVisualInput(ChatCompletionRequest request) =>
        request.Messages.Any(message => message.Images?.Count > 0);

    internal static string ResolveProviderModel(RouteDecisionDto? route)
    {
        if (route?.Success == true && !string.IsNullOrWhiteSpace(route.ModelPath)
            && HotModelPoolCatalog.TryGetActiveAlias(route.ModelPath, out string alias))
            return alias;

        // A resident pool exposes only its hashed aliases, so "local-default" is not a model the
        // provider knows and the request comes back as HTTP 400 "model 'local-default' not found".
        // Any caller that did not resolve to a specific pool entry - the coding agent among them -
        // still has to address something that exists, so fall back to the pool's own default
        // rather than a logical id that is only valid for a single-model launch.
        if (HotModelPoolCatalog.TryGetDefaultAlias(out string poolDefault)) return poolDefault;

        return "local-default";
    }

    /// <summary>
    /// The model_routed control event: which model was selected and where it runs. Every field that existed before is
    /// unchanged; provider and locality are always present, endpointHost only for a remote destination, and fallbackReason
    /// only when this turn did not run on the route first requested. None of them can carry a credential or a file path.
    /// </summary>
    internal static JsonObject BuildModelRoutedEvent(
        RouteDecisionDto modelRoute,
        LocalInferenceChatPreparation enginePreparation,
        bool manualModelLock,
        bool structuredOutput,
        string? requestModel,
        bool usesExternalProvider,
        string providerEndpoint,
        ModalityRouteOutcome? modality = null)
    {
        LocalInferenceEngineSelection? selectedEngine = enginePreparation.Selection;
        ChatRouteDestination destination = ChatRouteDestination.Describe(usesExternalProvider, providerEndpoint);
        string? fallbackReason = modelRoute.PreferenceFallbackReason is { } preferenceFallbackReason
            ? "Preferred candidate was unavailable for " + preferenceFallbackReason + "; automatic routing selected "
                + (modelRoute.PreferenceFallbackModel ?? "no model") + "."
            : !string.IsNullOrWhiteSpace(enginePreparation.FallbackReason)
            ? Trim(enginePreparation.FallbackReason)
            : modelRoute.FellBackToStandard
                ? "The requested role was unavailable, so PaRuX used the Standard role."
                : null;

        JsonObject routed = JsonSerializer.SerializeToNode(new
        {
            parux_event = "model_routed",
            mode = modelRoute.Mode,
            autoProfile = modelRoute.AutoProfile,
            requiredIntelligence = modelRoute.RequiredIntelligenceScore,
            selectedModel = modelRoute.ModelPath == null ? null : Path.GetFileName(modelRoute.ModelPath),
            selectedIntelligence = modelRoute.SelectedIntelligenceScore,
            tokensPerSecond = modelRoute.SelectedBenchmarkTokensPerSecond,
            fellBackToStandard = modelRoute.FellBackToStandard,
            reasoningEffort = enginePreparation.Reasoning.Effort,
            reasoningBudget = enginePreparation.Reasoning.Budget,
            workload = enginePreparation.Workload,
            engineId = selectedEngine?.Engine.Descriptor.Id
                ?? (usesExternalProvider ? ChatRouteDestination.ExternalProvider : "llama.cpp"),
            engineVersion = selectedEngine?.Evidence.Request.CandidateEngineVersion,
            engineVariant = selectedEngine?.Variant.VariantId,
            engineReason = selectedEngine?.Reason,
            engineFallback = enginePreparation.FallbackReason,
            hotPool = selectedEngine == null && !string.Equals(requestModel, "local-default", StringComparison.Ordinal),
            manualLock = manualModelLock,
            structuredOutput,
            reason = modelRoute.SelectionReason,
            overrideMode = modelRoute.EffectiveOverrideMode,
            fallbackTrace = modelRoute.FallbackTrace,
            provider = destination.Provider,
            locality = destination.Locality
        })!.AsObject();

        if (destination.EndpointHost is { } endpointHost) routed["endpointHost"] = endpointHost;
        if (fallbackReason is not null) routed["fallbackReason"] = fallbackReason;
        if (modality is not null) ModalityChatGate.Annotate(routed, modality);
        return routed;
    }

    private static async Task WriteDirectAnswerAsync(string answer, HttpResponse downstream, CancellationToken cancellationToken)
    {
        downstream.StatusCode = StatusCodes.Status200OK;
        downstream.ContentType = "text/event-stream";
        downstream.Headers.CacheControl = "no-cache";
        string chunk = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = answer } } } }) + "\n\n";
        await downstream.WriteAsync(chunk, cancellationToken);
        await downstream.WriteAsync("data: [DONE]\n\n", cancellationToken);
        await downstream.Body.FlushAsync(cancellationToken);
    }

    internal static bool ShouldUseLowLatencyPath(
        ChatCompletionRequest request, SkillRouteResult routed, bool hasVisualInput) =>
        request.PreferLowLatency
        && !hasVisualInput
        && string.IsNullOrWhiteSpace(request.SkillId)
        && (routed.Skill == null || string.Equals(routed.Skill.Id, "general", StringComparison.OrdinalIgnoreCase));

    private async Task<StreamingMcpToolLoop> CreateMcpToolLoopAsync(
        ChatCompletionRequest request,
        JsonArray tools,
        IReadOnlyCollection<string> advertisedToolNames,
        int contextTokens,
        HttpResponse downstream,
        CancellationToken cancellationToken)
    {
        JsonObject payload = await PrepareProviderPayloadAsync(request, stream: true, cancellationToken).ConfigureAwait(false);
        return new StreamingMcpToolLoop(
            payload,
            tools,
            open: async (body, ct) =>
            {
                if (contextTokens > ReplyReserveTokens)
                {
                    int? exact = await _tokenCounter.TryCountRenderedChatPromptAsync(
                        _runtime.ProviderEndpoint, body, ct).ConfigureAwait(false);
                    int maximum = contextTokens - ReplyReserveTokens;
                    PromptExceedsContextException? growth = ChatProxyFailureResponse.CreateToolLoopGrowthFailure(exact, maximum);
                    if (growth is not null) throw growth;
                }
                using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, _runtime.ProviderEndpoint + "/v1/chat/completions")
                {
                    Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
                };
                ExternalTextProviderService.ApplyRemoteAuthorization(upstreamRequest, _runtime.ProviderEndpoint);
                return await Http.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            },
            invokeTool: (name, args, ct) => _mcp.CallToolAsync(name, args, ct, advertisedToolNames),
            writeFrame: async (frame, ct) =>
            {
                await downstream.WriteAsync(frame, ct).ConfigureAwait(false);
                await downstream.Body.FlushAsync(ct).ConfigureAwait(false);
            },
            MaxMcpToolCallsPerRound,
            MaxMcpToolCallsPerTurn,
            MaxMcpToolResultCharactersPerTurn);
    }

    private async Task StreamWithFallbackAsync(
        StreamReader reader, ChatCompletionRequest request, HttpResponse downstream, CancellationToken cancellationToken)
    {
        var pending = new List<string>();
        var plainText = new StringBuilder();
        long firstDeltaTimestamp = 0;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool done = false;
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                string data = line[5..].Trim();
                if (data == "[DONE]") { pending.Add(line); done = true; }
                else if (data.Length > 0)
                {
                    (string? fragment, bool isError, bool isReasoning) = TryExtractDelta(data);
                    if (isError)
                    {
                        pending.Add(line);
                        await FlushPendingAsync(pending, downstream, cancellationToken);
                        await PumpRawAsync(reader, downstream, cancellationToken);
                        return;
                    }
                    if (isReasoning)
                    {
                        await downstream.WriteAsync(line + "\n\n", cancellationToken);
                        await downstream.Body.FlushAsync(cancellationToken);
                        continue;
                    }
                    pending.Add(line);
                    if (!string.IsNullOrEmpty(fragment))
                    {
                        if (firstDeltaTimestamp == 0) firstDeltaTimestamp = Stopwatch.GetTimestamp();
                        plainText.Append(fragment);
                    }
                }
                else pending.Add(line);
            }
            else pending.Add(line);

            bool windowElapsed = firstDeltaTimestamp != 0
                && Stopwatch.GetElapsedTime(firstDeltaTimestamp) >= FallbackDecisionWindow;

            if (done || plainText.Length >= FallbackDecisionThreshold || windowElapsed)
            {
                if (RefusalHeuristic.IsLikelyRefusal(plainText.ToString())
                    && await TryEscalateAndRestartAsync(request, downstream, cancellationToken)) return;
                await FlushPendingAsync(pending, downstream, cancellationToken);
                await PumpRawAsync(reader, downstream, cancellationToken);
                return;
            }
        }

        if (plainText.Length > 0
            && RefusalHeuristic.IsLikelyRefusal(plainText.ToString())
            && await TryEscalateAndRestartAsync(request, downstream, cancellationToken)) return;
        await FlushPendingAsync(pending, downstream, cancellationToken);
    }

    private async Task<bool> TryEscalateAndRestartAsync(
        ChatCompletionRequest request, HttpResponse downstream, CancellationToken cancellationToken)
    {
        try { await _profiles.EscalateToUncensoredAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }

        request.Model = "local-default";
        await downstream.WriteAsync(SwitchedEventLine, cancellationToken);
        await downstream.Body.FlushAsync(cancellationToken);

        using HttpResponseMessage retryUpstream = await OpenUpstreamAsync(request, cancellationToken);
        await using Stream retryStream = await retryUpstream.Content.ReadAsStreamAsync(cancellationToken);
        using var retryReader = new StreamReader(retryStream, Encoding.UTF8);
        await PumpRawAsync(retryReader, downstream, cancellationToken);
        return true;
    }

    internal Task<string> CompleteOnceAsync(string prompt, CancellationToken cancellationToken) =>
        CompleteOnceAsync(prompt, preferLowLatency: false, cancellationToken: cancellationToken);

    internal async Task<string> CompleteOnceAsync(string prompt, bool preferLowLatency, CancellationToken cancellationToken)
    {
        var request = new ChatCompletionRequest
        {
            Model = "local-compare",
            PreferLowLatency = preferLowLatency,
            Messages = new List<ChatCompletionMessage> { new() { Role = "user", Content = prompt } }
        };
        using HttpResponseMessage upstream = await OpenUpstreamAsync(request, cancellationToken);
        await using Stream stream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line[5..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;
            (string? fragment, bool isError, bool isReasoning) = TryExtractDelta(data);
            if (isError) throw new InvalidOperationException("The model provider returned an error while generating.");
            if (!isReasoning && !string.IsNullOrEmpty(fragment)) text.Append(fragment);
        }
        return text.ToString();
    }

    internal static bool IsAttestationRefusal(Exception? failure)
    {
        for (int depth = 0; failure != null && depth < 16; depth++)
        {
            if (failure is PeerAttestationException) return true;
            if (failure is AggregateException aggregate && aggregate.InnerExceptions.Any(IsAttestationRefusal)) return true;
            failure = failure.InnerException;
        }
        return false;
    }

    private async Task<bool> TryServeFromCloudFallbackAsync(
        ChatCompletionRequest request,
        HttpResponse downstream,
        Exception localFailure,
        CancellationToken cancellationToken)
    {
        // A refused peer means something other than PaRuX's runtime holds the endpoint. That is an
        // integrity failure, not an availability one, so it must never be turned into a reason to
        // send the prompt anywhere else.
        if (IsAttestationRefusal(localFailure)) return false;
        if (_runtime.GamingMode || _runtime.UsesExternalProvider || request.ContinueFinalMessage) return false;
        if (!_cloudFallback.IsArmed(out _)) return false;

        string localReason = Trim(localFailure.Message);
        CloudFallbackStatus status = _cloudFallback.GetStatus();

        HttpResponseMessage response;
        try
        {
            response = await _cloudFallback.OpenStreamAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception fallbackFailure)
        {
            if (!downstream.HasStarted) return false;
            await WriteEventAsync(downstream, new
            {
                parux_event = "cloud_fallback_failed",
                localReason,
                detail = Trim(fallbackFailure.Message)
            }, cancellationToken);
            await WriteEventAsync(downstream, new
            {
                error = new
                {
                    message = "Local inference failed (" + localReason + ") and the cloud fallback also failed: "
                        + Trim(fallbackFailure.Message)
                }
            }, cancellationToken);
            await downstream.WriteAsync("data: [DONE]\n\n", cancellationToken);
            await downstream.Body.FlushAsync(cancellationToken);
            return true;
        }

        using (response)
        {
            if (!downstream.HasStarted)
            {
                downstream.StatusCode = StatusCodes.Status200OK;
                downstream.ContentType = "text/event-stream";
                downstream.Headers.CacheControl = "no-cache";
                downstream.Headers.Append("X-Accel-Buffering", "no");
            }

            await WriteEventAsync(downstream, new
            {
                parux_event = "cloud_fallback",
                provider = status.ProviderDisplayName,
                providerId = status.ProviderId,
                model = status.Model,
                reason = localReason,
                notice = "Local inference was unavailable for this turn, so it was answered by "
                    + status.ProviderDisplayName + " and left this device."
            }, cancellationToken);

            _cloudFallback.RecordUse(localReason);
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await PumpRawAsync(reader, downstream, cancellationToken);
        }
        return true;
    }

    private static async Task WriteEventAsync(HttpResponse downstream, object payload, CancellationToken cancellationToken)
    {
        await downstream.WriteAsync("data: " + JsonSerializer.Serialize(payload) + "\n\n", cancellationToken);
        await downstream.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Reports a provider failure that happened after the SSE response already began. Rethrowing at
    /// that point cannot change a status code that is already on the wire, so the caller saw a
    /// successful stream that simply stopped - which is how a runtime rejecting PaRuX's own sampling
    /// grammar ("failed to parse grammar", HTTP 400) reached the coding agent as an empty answer and
    /// was spent, nine times over, being blamed on the model. Returns false when nothing has been
    /// written yet, so that path can still fail with a real status code.
    /// </summary>
    private static async Task<bool> TryReportStartedStreamFailureAsync(
        HttpResponse downstream, Exception failure, CancellationToken cancellationToken)
    {
        if (!downstream.HasStarted) return false;
        try
        {
            await WriteEventAsync(
                downstream,
                new { error = new { message = Trim(failure.Message), type = "provider_error" } },
                cancellationToken);
            await downstream.WriteAsync("data: [DONE]\n\n", cancellationToken);
            await downstream.Body.FlushAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // The connection is already gone; the original failure still propagates to the caller.
            return false;
        }
    }

    private async Task<HttpResponseMessage> OpenUpstreamAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        request.Stream = true;
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, _runtime.ProviderEndpoint + "/v1/chat/completions");
        JsonObject streamPayload = await PrepareProviderPayloadAsync(request, stream: true, cancellationToken).ConfigureAwait(false);
        upstreamRequest.Content = new StringContent(
            streamPayload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
        ExternalTextProviderService.ApplyRemoteAuthorization(upstreamRequest, _runtime.ProviderEndpoint);
        HttpResponseMessage upstream = await Http.SendAsync(
            upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!upstream.IsSuccessStatusCode)
        {
            string detail = await upstream.Content.ReadAsStringAsync(cancellationToken);
            upstream.Dispose();
            throw new HttpRequestException($"The model provider returned HTTP {(int)upstream.StatusCode}: {Trim(detail)}");
        }
        return upstream;
    }

    /// <summary>
    /// PrepareProviderPayload plus the served template's reasoning-effort vocabulary: an effort the
    /// template does not accept is a hard HTTP 500 from llama.cpp, not a hint it ignores.
    /// </summary>
    private async Task<JsonObject> PrepareProviderPayloadAsync(ChatCompletionRequest request, bool stream, CancellationToken cancellationToken)
    {
        JsonObject payload = PrepareProviderPayload(request, stream);
        if (payload["chat_template_kwargs"] is JsonObject kwargs
            && kwargs["reasoning_effort"] is JsonValue effortValue
            && effortValue.TryGetValue(out string? effort)
            && !string.IsNullOrWhiteSpace(effort))
        {
            IReadOnlyCollection<string>? supported = await ReasoningEffortCompatibility
                .GetSupportedAsync(Http, _runtime.ProviderEndpoint, cancellationToken).ConfigureAwait(false);
            kwargs["reasoning_effort"] = ReasoningEffortCompatibility.Resolve(effort, supported);
        }
        return payload;
    }

    private JsonObject PrepareProviderPayload(ChatCompletionRequest request, bool stream)
    {
        JsonObject payload = BuildProviderPayload(request);
        payload["stream"] = stream;

        LocalStructuredOutputRequest? structuredOutput = StructuredOutputRequestTransport.TryRead(request);
        if (structuredOutput != null)
            payload["response_format"] = StructuredOutputJson.BuildOpenAiResponseFormat(structuredOutput);

        if (!_runtime.UsesExternalProvider)
        {
            PromptCacheDecision cache = _promptCache.Decide(request, _runtime.ProviderEndpoint);
            payload["cache_prompt"] = cache.Reuse;
            payload["n_cache_reuse"] = cache.ReuseChunkTokens;

            AdaptiveReasoningPolicy.Decision reasoning = AdaptiveReasoningPolicy.Decide(request);
            // A caller-capped reply that is too small to hold hidden thinking plus an answer must not
            // be starved into an empty "length" completion: mirror LocalApiServerService's guard.
            if (ChatSamplingOptions.EffectiveMaxTokens(request) is { } cap && cap < 512 && reasoning.Budget > 0)
                reasoning = new AdaptiveReasoningPolicy.Decision("minimal", 0, reasoning.Demand);
            payload["reasoning_format"] = "auto";
            payload["reasoning_control"] = true;
            payload["thinking_budget_tokens"] = reasoning.Budget;
            payload["chat_template_kwargs"] = new JsonObject
            {
                ["enable_thinking"] = reasoning.Budget > 0,
                ["reasoning_effort"] = reasoning.Effort
            };

            ApplyContinuation(payload, request, usesExternalProvider: false);
        }
        else if (request.ContinueFinalMessage)
        {
            ApplyContinuation(payload, request, usesExternalProvider: true);
        }
        return payload;
    }

    internal static void ApplyContinuation(JsonObject payload, ChatCompletionRequest request, bool usesExternalProvider)
    {
        if (!request.ContinueFinalMessage) return;

        if (usesExternalProvider)
            throw new InvalidOperationException(
                "Continuing an edited reply is only supported on PaRuX's managed local runtime, not an external provider.");

        if (request.Messages.Count == 0
            || !string.Equals(request.Messages[^1].Role, "assistant", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "ContinueFinalMessage requires the final message to be the edited assistant turn to resume from.");

        payload["continue_final_message"] = true;
        payload["add_generation_prompt"] = false;
    }

    private static async Task FlushPendingAsync(List<string> pending, HttpResponse downstream, CancellationToken cancellationToken)
    {
        foreach (string line in pending)
        {
            await downstream.WriteAsync(line + "\n", cancellationToken);
            if (line.Length == 0) await downstream.Body.FlushAsync(cancellationToken);
        }
        pending.Clear();
    }

    private static async Task PumpRawAsync(StreamReader reader, HttpResponse downstream, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await downstream.WriteAsync(line + "\n", cancellationToken);
            if (line.Length == 0) await downstream.Body.FlushAsync(cancellationToken);
        }
    }

    private static (string? fragment, bool isError, bool isReasoning) TryExtractDelta(string dataJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(dataJson);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("error", out _)) return (null, true, false);
            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return (null, false, false);
            JsonElement first = choices[0];
            if (first.TryGetProperty("delta", out JsonElement delta))
            {
                if (delta.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                    return (content.GetString(), false, false);
                if (delta.TryGetProperty("reasoning_content", out JsonElement reasoning) && reasoning.ValueKind == JsonValueKind.String)
                    return (reasoning.GetString(), false, true);
            }
            return (null, false, false);
        }
        catch (JsonException) { return (null, false, false); }
    }

    // SourceLabels describe every prompt ingredient, including conversation history, memory and
    // graph hints. The chat footer promises actual retrievable sources, so expose only documents
    // and web pages there. Keep the full pack for diagnostics and API users.
    internal static List<string> DisplaySources(ContextPackDto pack) => pack.Items
        .Where(item => item.Kind is "rag" or "web" or "repository_file")
        .Select(item => item.Kind == "web" ? item.Source : item.Title)
        .Where(label => !string.IsNullOrWhiteSpace(label))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    internal static void ApplyContextPack(ChatCompletionRequest request, ContextPackDto pack)
    {
        if (string.IsNullOrWhiteSpace(pack.PromptText)) return;
        ChatCompletionMessage? firstSystem = request.Messages.FirstOrDefault(m => m.Role == "system");
        if (firstSystem != null) firstSystem.Content += "\n\n" + pack.PromptText;
        else request.Messages.Insert(0, new ChatCompletionMessage { Role = "system", Content = pack.PromptText });
    }

    internal const string GrillMeStructuredOutputName = "parux_grill_me_turn";

    internal static void ApplySkillPromptShaping(ChatCompletionRequest request, SkillManifest skill, Dictionary<string, string> parameters)
    {
        if (skill.Id != "grill-me") return;
        string difficulty = parameters.GetValueOrDefault("difficulty", "Medium");
        string style = parameters.GetValueOrDefault("style", "Quiz");
        string questions = parameters.GetValueOrDefault("questions", "Auto");
        bool customQuestions = string.Equals(questions, "Custom", StringComparison.OrdinalIgnoreCase);

        string instruction = "You are running PaRuX's Grill Me skill. Quiz the user on the topic of their message. "
            + "Difficulty: " + difficulty + ". Style: " + style + ". "
            + (customQuestions
                ? "The user will supply their own questions; ask them to."
                : "Generate the questions yourself, one at a time.")
            + " Ask exactly one question, wait for the user's answer before giving feedback and asking the next one. "
            + "Never answer your own question. Match the requested difficulty honestly - Brutal should actually be hard.";

        if (!customQuestions)
        {
            instruction += " For every turn, respond with a single JSON object matching the required "
                + "schema: \"feedback\" (empty string for the very first question, since there is no "
                + "prior answer yet - otherwise honest feedback on the user's previous answer), "
                + "\"question\" (the next quiz question), \"options\" (2 to 6 short, mutually "
                + "exclusive candidate answers - exactly one correct unless the style calls for open "
                + "discussion), and \"allowOther\" (true only when a free-text answer is genuinely "
                + "more appropriate than the listed options, e.g. an open-ended Socratic or Interview "
                + "question).";

            StructuredOutputRequestTransport.Set(request, new LocalStructuredOutputRequest(
                GrillMeStructuredOutputName,
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["feedback"] = new JsonObject { ["type"] = "string" },
                        ["question"] = new JsonObject { ["type"] = "string" },
                        ["options"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["minItems"] = 2,
                            ["maxItems"] = 6
                        },
                        ["allowOther"] = new JsonObject { ["type"] = "boolean" }
                    },
                    ["required"] = new JsonArray("feedback", "question", "options", "allowOther"),
                    ["additionalProperties"] = false
                }.ToJsonString()));
        }

        ChatCompletionMessage? firstSystem = request.Messages.FirstOrDefault(m => m.Role == "system");
        if (firstSystem != null) firstSystem.Content = instruction + "\n\n" + firstSystem.Content;
        else request.Messages.Insert(0, new ChatCompletionMessage { Role = "system", Content = instruction });
    }

    internal static void ApplyPersonaPrompt(ChatCompletionRequest request, Persona persona)
    {
        if (string.IsNullOrWhiteSpace(persona.SystemPrompt)) return;
        ChatCompletionMessage? firstSystem = request.Messages.FirstOrDefault(m => m.Role == "system");
        if (firstSystem != null) firstSystem.Content = persona.SystemPrompt + "\n\n" + firstSystem.Content;
        else request.Messages.Insert(0, new ChatCompletionMessage { Role = "system", Content = persona.SystemPrompt });
    }

    internal static JsonObject BuildProviderPayload(ChatCompletionRequest request)
    {
        var messages = new JsonArray();
        foreach (ChatCompletionMessage message in request.Messages)
        {
            var item = new JsonObject { ["role"] = message.Role };
            if (message.Images.Count == 0) item["content"] = message.Content;
            else
            {
                var parts = new JsonArray();
                if (!string.IsNullOrWhiteSpace(message.Content))
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
                foreach (ChatImageContent image in message.Images)
                {
                    if (!image.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Only image attachments are accepted by this endpoint.");
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{image.Base64Data}" }
                    });
                }
                item["content"] = parts;
            }
            messages.Add(item);
        }
        var built = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = true
        };
        ChatSamplingOptions.Apply(built, request);
        return built;
    }

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 300 ? value : value[..300] + "…";
    }
}
