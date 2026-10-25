using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server;

/// <summary>OpenAI-compatible surface for third-party local/LAN clients (VS Code, OpenCode,
/// custom scripts) that already speak the OpenAI chat-completions protocol.
/// Deliberately a thin authenticated forward to whichever
/// model is loaded, NOT a reimplementation of ChatProxyService's memory/RAG/skills/persona
/// pipeline - that pipeline is PaRuX's own chat contract, this is a narrower,
/// protocol-compatible surface for external tools.
///
/// Both buffered completions and real OpenAI-style SSE streaming are supported. Streaming is a
/// byte-as-it-arrives proxy after the upstream provider accepts the request: PaRuX only rewrites
/// the upstream chunk's model id back to the logical role requested by the client and otherwise
/// preserves SSE event lines, including [DONE]. It never buffers a whole completion and calls it
/// streaming.
///
/// 2026-08-27 (closes this file's own "resolving a requested logical model to a freshly-loaded
/// runtime is Phase 8, separate and larger" gap now that Phase 8 - the Smart Model Router - is
/// real): "model" in the request body genuinely selects which of the 8 text roles gets loaded,
/// routed through the same IRoleRouter seam AgentWorkerService already uses. An external
/// OpenAI-compatible backend (LocalRuntimeService.UsesExternalProvider) has no role/GGUF concept
/// at all - that path remains a plain reachability probe before proxying.</summary>
public sealed class LocalApiServerService
{
    private static readonly HttpClient SharedHttp = new(ManagedRuntimePeers.CreateHandler()) { Timeout = TimeSpan.FromMinutes(5) };

    private readonly LocalRuntimeService _runtime;
    private readonly IRoleRouter _router;
    private readonly HttpClient _http;
    private readonly Func<bool> _ownsRuntime;

    /// <summary>The optional HttpClient is a deterministic test seam. Normal DI construction does
    /// not register HttpClient here, so the shared long-lived client is used in production.</summary>
    public LocalApiServerService(
        LocalRuntimeService runtime,
        IRoleRouter router,
        HttpClient? httpClient = null,
        CodingModelSelectionService? codingSelection = null)
        : this(runtime, router, httpClient, () => runtime.ManagedRuntimeOwned, codingSelection)
    {
    }

    // Deterministic ownership seam for in-memory protocol tests. Production construction always
    // uses LocalRuntimeService's fresh, fail-closed OS listener ownership check.
    internal LocalApiServerService(LocalRuntimeService runtime, IRoleRouter router,
        HttpClient? httpClient, Func<bool> ownsRuntime, CodingModelSelectionService? codingSelection = null)
    {
        _ownsRuntime = ownsRuntime;
        _runtime = runtime;
        _router = router;
        _http = httpClient ?? SharedHttp;
        _codingSelection = codingSelection;
    }

    private readonly CodingModelSelectionService? _codingSelection;

    /// <summary>
    /// The coding role follows Core's measured coding-model choice when it has one; otherwise, or
    /// when that exact model cannot be admitted right now, the role's assigned model is used. The
    /// model that actually served the request is attributed to the running coding task(s).
    /// </summary>
    private async Task<RouteDecisionDto> ResolveRoleAsync(string role, CancellationToken cancellationToken)
    {
        if (_codingSelection == null
            || !string.Equals(role, CodingModelSelectionService.CodingRole, StringComparison.OrdinalIgnoreCase))
            return await _router.ResolveAsync(new RouteRequest { Role = role }, cancellationToken);

        string? note = null;
        RouteDecisionDto? routing = null;
        CodingModelSelectionDto? decision = null;
        try
        {
            decision = await _codingSelection.GetDecisionAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            note = "measured selection unavailable (" + ex.Message + "); assigned coding model used";
        }

        if (decision?.EffectiveSource == CodingModelSelectionMode.Auto && decision.SelectedFileName != null
            && _codingSelection.IsExactLoadBackedOff(decision.SelectedFileName))
        {
            note = "measured choice " + decision.SelectedFileName + " failed to load recently; assigned coding model used";
        }
        else if (decision?.EffectiveSource == CodingModelSelectionMode.Auto && decision.SelectedFileName != null)
        {
            routing = await _router.ResolveAsync(new RouteRequest
            {
                Role = role,
                ExactCandidateFileName = decision.SelectedFileName
            }, cancellationToken);
            note = routing.Success
                ? "auto: " + decision.Reason
                : "measured choice " + decision.SelectedFileName + " could not be loaded (" + routing.Message + "); assigned coding model used";
            if (!routing.Success)
            {
                _codingSelection.RecordExactLoadFailure(decision.SelectedFileName);
                routing = null;
            }
        }
        else
        {
            note ??= decision == null ? "assigned coding model used" : "assigned: " + decision.Reason;
        }

        routing ??= await _router.ResolveAsync(new RouteRequest { Role = role }, cancellationToken);
        if (routing.Success && !string.IsNullOrWhiteSpace(routing.ModelPath))
        {
            try { _codingSelection.RecordCodingResolution(routing.ModelPath, note); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return routing;
    }

    /// <summary>"parux-main"/"parux-uncensored" are this endpoint's original two ids, kept
    /// byte-for-byte so no existing external client configuration breaks; every other text role
    /// gets the same "parux-{role}" convention.</summary>
    private static string ModelIdForRole(string role) => "parux-" + (role == "standard" ? "main" : role);

    /// <summary>OpenAI's model object shape. Typed rather than anonymous so the list route and the
    /// single-model retrieve route below cannot drift apart.</summary>
    public sealed record LocalApiModel(
        string id,
        string @object,
        long created,
        string owned_by,
        string? real_model)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public string? ModelPath { get; init; }
    }

    /// <summary>Model id used when a client sends a vendor id such as claude-* or gpt-* (Advanced setting).</summary>
    public static string VendorModelDefault { get; set; } = PaRuX.Server.Gateway.GatewayModelAliases.Auto;

    public sealed record GatewayRoutingRecord(string RequestedModel, string AliasSource, string Role, string? ModelFile, DateTime Utc);

    /// <summary>The last routing decision taken for an external request (model actually used), for management/diagnostics.</summary>
    public static GatewayRoutingRecord? LastRoutingDecision { get; private set; }

    private static List<LocalApiModel> BuildModelCatalog()
    {
        var virtualModels = PaRuX.Server.Gateway.GatewayModelAliases.Virtual.Select(v =>
        {
            string? modelPath = v.Role == null ? null : LocalAiPaths.GetRoleModelPathOrNull(v.Role);
            return new LocalApiModel(v.Id, "model", 0, "parux", modelPath != null ? Path.GetFileName(modelPath) : null)
            {
                ModelPath = modelPath
            };
        });
        var virtualIds = new HashSet<string>(PaRuX.Server.Gateway.GatewayModelAliases.Virtual.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
        var data = virtualModels.Concat(ModelRoleCatalog.TextRoles.Where(role => !virtualIds.Contains(ModelIdForRole(role))).Select(role =>
        {
            string? modelPath = LocalAiPaths.GetRoleModelPathOrNull(role);
            return new LocalApiModel(
                ModelIdForRole(role), "model", 0, "parux",
                modelPath != null ? Path.GetFileName(modelPath) : null)
            {
                ModelPath = modelPath
            };
        })).ToList();

        // The local embedding engine is served over the OpenAI-compatible /v1/embeddings route,
        // so it has to be discoverable here too - clients enumerate /v1/models to find out which
        // embedding model id to send.
        data.Add(new LocalApiModel(
            LocalApiEmbeddingsService.ModelId, "model", 0, "parux", "all-MiniLM-L6-v2"));
        return data;
    }

    public object ListModels() => new { @object = "list", data = BuildModelCatalog() };

    /// <summary>
    /// OpenAI's single-model retrieve route. Official OpenAI SDKs call it to validate a configured
    /// model id before sending real work, so a client pointed at PaRuX would otherwise get a 404
    /// for a model that /v1/models had just advertised. Returns null for an unknown id so the
    /// endpoint can answer a proper 404 instead of inventing a model entry.
    /// </summary>
    public LocalApiModel? GetModel(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : BuildModelCatalog().FirstOrDefault(model => string.Equals(model.id, id, StringComparison.Ordinal));

    /// <summary>Lets the HTTP endpoint choose the streaming path without duplicating validation.
    /// Invalid JSON intentionally returns false so CompleteAsync can produce the canonical 400.</summary>
    public static bool RequestsStreaming(string rawBody)
    {
        try
        {
            return JsonNode.Parse(rawBody) is JsonObject obj
                && obj["stream"] is JsonValue streamValue
                && streamValue.TryGetValue(out bool stream)
                && stream;
        }
        catch (JsonException)
        {
            return false;
        }
    }


    /// <summary>
    /// OpenAI's legacy <c>POST /v1/completions</c>. Implemented as a translation over the chat path
    /// rather than a second generation pipeline, so routing, provider readiness, error handling and
    /// model-id resolution stay in exactly one place and cannot drift.
    ///
    /// Streaming is deliberately refused here with an explicit message instead of being silently
    /// downgraded to a buffered response, which would look like a hung stream to the caller.
    /// </summary>
    public async Task<LocalApiCompletionResult> CompleteLegacyAsync(string rawBody, CancellationToken cancellationToken)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(rawBody); }
        catch (JsonException) { return LocalApiCompletionResult.BadRequest("Invalid JSON body."); }
        if (parsed is not JsonObject request)
            return LocalApiCompletionResult.BadRequest("Request body must be a JSON object.");

        if (request["stream"] is JsonValue streamValue && streamValue.TryGetValue(out bool wantsStream) && wantsStream)
        {
            return LocalApiCompletionResult.BadRequest(
                "Streaming is not supported on the legacy /v1/completions route; use /v1/chat/completions with \"stream\": true.");
        }

        if (request["prompt"] is not JsonValue promptValue
            || !promptValue.TryGetValue(out string? prompt)
            || string.IsNullOrEmpty(prompt))
        {
            return LocalApiCompletionResult.BadRequest(
                "\"prompt\" must be a non-empty string. Array and token-id prompts are not supported by this route.");
        }

        // Carry every other tuning field through untouched; only the prompt/messages shape differs
        // between the two protocols, so allow-listing fields here would silently drop options the
        // upstream provider already understands.
        var chatRequest = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> property in request)
        {
            if (property.Key is "prompt" or "stream") continue;
            chatRequest[property.Key] = property.Value?.DeepClone();
        }
        chatRequest["messages"] = new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = prompt });

        LocalApiCompletionResult chatResult =
            await CompleteAsync(chatRequest.ToJsonString(), cancellationToken);
        if (chatResult.Outcome != LocalApiOutcome.Ok) return chatResult;

        return TranslateChatToLegacy(chatResult.Body);
    }

    /// <summary>Rewrites a chat.completion payload into the text_completion shape. If the upstream
    /// body is not the expected shape it is surfaced as an upstream failure rather than returned as
    /// a malformed completion the caller would have to guess about.</summary>
    private static LocalApiCompletionResult TranslateChatToLegacy(string chatBody)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(chatBody); }
        catch (JsonException)
        {
            return LocalApiCompletionResult.UpstreamFailure("The local model returned an unparseable response.");
        }
        if (parsed is not JsonObject chat || chat["choices"] is not JsonArray chatChoices)
            return LocalApiCompletionResult.UpstreamFailure("The local model returned an unexpected completion shape.");

        var choices = new JsonArray();
        for (int index = 0; index < chatChoices.Count; index++)
        {
            JsonNode? choice = chatChoices[index];
            string text = choice?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
            choices.Add(new JsonObject
            {
                ["index"] = choice?["index"]?.DeepClone() ?? index,
                ["text"] = text,
                ["logprobs"] = null,
                ["finish_reason"] = choice?["finish_reason"]?.DeepClone()
            });
        }

        var legacy = new JsonObject
        {
            ["id"] = chat["id"]?.DeepClone() ?? "cmpl-" + Guid.NewGuid().ToString("N"),
            ["object"] = "text_completion",
            ["created"] = chat["created"]?.DeepClone() ?? 0,
            ["model"] = chat["model"]?.DeepClone(),
            ["choices"] = choices
        };
        if (chat["usage"] is JsonNode usage) legacy["usage"] = usage.DeepClone();
        return LocalApiCompletionResult.Ok(legacy.ToJsonString());
    }

    /// <summary>
    /// Reuse the exact gateway preparation used by a parux-coding structured Chat
    /// request: resolve/load the assigned coding model, normalize the provider model
    /// alias, and count the fully rendered chat template including response_format.
    /// No completion is generated. Unavailable external providers return null and
    /// the desktop agent retains its conservative character budget.
    /// </summary>
    public async Task<int?> TryCountRenderedCodingPromptAsync(
        string rawBody, CancellationToken cancellationToken)
    {
        if (_runtime.UsesExternalProvider) return null;
        (PreparedCompletion? prepared, LocalApiCompletionResult? error) =
            await PrepareAsync(rawBody, requireStreaming: false, cancellationToken)
                .ConfigureAwait(false);
        if (error != null || prepared == null) return null;
        var counter = new RuntimeTokenCounter(_http);
        return await counter.TryCountRenderedChatPromptAsync(
            _runtime.ProviderEndpoint, prepared.Body, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LocalApiCompletionResult> CompleteAsync(string rawBody, CancellationToken cancellationToken)
    {
        (PreparedCompletion? prepared, LocalApiCompletionResult? error) =
            await PrepareAsync(rawBody, requireStreaming: false, cancellationToken);
        if (error != null) return error;

        using HttpRequestMessage upstream = BuildUpstreamRequest(prepared!);
        HttpResponseMessage response;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            response = await _http.SendAsync(upstream, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            string resolvedModel = prepared?.Body?["model"]?.ToString() ?? "unknown";
            string requested = prepared?.RequestedModel ?? "unknown";
            return LocalApiCompletionResult.UpstreamFailure(
                $"Upstream request timed out after {stopwatch.ElapsedMilliseconds}ms for model \"{requested}\" (resolved: \"{resolvedModel}\").");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return LocalApiCompletionResult.UpstreamFailure("Could not reach the local model provider: " + ex.Message);
        }

        using (response)
        {
            string payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return LocalApiCompletionResult.UpstreamError((int)response.StatusCode, payload);

            JsonNode? result;
            try
            {
                result = JsonNode.Parse(payload);
            }
            catch (JsonException)
            {
                return LocalApiCompletionResult.UpstreamFailure("The local model returned an unparseable response.");
            }

            if (result is JsonObject resultObj) resultObj["model"] = prepared!.RequestedModel;
            return LocalApiCompletionResult.Ok(result!.ToJsonString());
        }
    }

    /// <summary>Opens the provider with ResponseHeadersRead and proxies SSE events directly to
    /// destination. Failures before a successful upstream response are returned as a normal JSON
    /// error result; once streaming starts, transport exceptions deliberately propagate because
    /// an HTTP status can no longer be honestly changed after bytes have reached the client.</summary>
    public async Task<LocalApiCompletionResult> StreamAsync(
        string rawBody, Stream destination, CancellationToken cancellationToken)
    {
        (PreparedCompletion? prepared, LocalApiCompletionResult? error) =
            await PrepareAsync(rawBody, requireStreaming: true, cancellationToken);
        if (error != null) return error;

        using HttpRequestMessage upstream = BuildUpstreamRequest(prepared!);
        upstream.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        HttpResponseMessage response;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            response = await _http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            string resolvedModel = prepared?.Body?["model"]?.ToString() ?? "unknown";
            string requested = prepared?.RequestedModel ?? "unknown";
            return LocalApiCompletionResult.UpstreamFailure(
                $"Upstream streaming request timed out after {stopwatch.ElapsedMilliseconds}ms for model \"{requested}\" (resolved: \"{resolvedModel}\").");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return LocalApiCompletionResult.UpstreamFailure("Could not reach the local model provider: " + ex.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string payload = await response.Content.ReadAsStringAsync(cancellationToken);
                return LocalApiCompletionResult.UpstreamError((int)response.StatusCode, payload);
            }

            try
            {
                await PumpSseAsync(response.Content, destination, prepared!.RequestedModel, cancellationToken);
                return LocalApiCompletionResult.Ok(string.Empty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return LocalApiCompletionResult.UpstreamFailure("The local model provider stream terminated unexpectedly: " + ex.Message);
            }
        }
    }

    public sealed record ChatStreamToolDelta(int Index, string? Id, string? Name, string? Arguments);

    /// <summary>One provider SSE chunk reduced to what protocol encoders need.</summary>
    public sealed record ChatStreamChunk(string? Text, IReadOnlyList<ChatStreamToolDelta> Tools, string? FinishReason, JsonNode? Usage);

    /// <summary>
    /// The single internal stream every gateway protocol is encoded from: the provider's OpenAI
    /// chat SSE, parsed into text/tool-call deltas as they arrive. <paramref name="onStarted"/>
    /// runs once the provider has accepted the request, so errors before that point are still
    /// returned as an ordinary result with an honest HTTP status.
    /// </summary>
    public async Task<LocalApiCompletionResult> StreamChunksAsync(
        string rawBody, Func<Task> onStarted, Func<ChatStreamChunk, Task> onChunk, CancellationToken cancellationToken)
    {
        string body = rawBody;
        try
        {
            if (JsonNode.Parse(rawBody) is JsonObject request)
            {
                request["stream"] = true;
                // Usage arrives in the final chunk; protocol encoders report it (Codex requires it).
                request["stream_options"] = new JsonObject { ["include_usage"] = true };
                body = request.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // PrepareAsync reports the malformed body.
        }

        (PreparedCompletion? prepared, LocalApiCompletionResult? error) =
            await PrepareAsync(body, requireStreaming: true, cancellationToken);
        if (error != null) return error;

        using HttpRequestMessage upstream = BuildUpstreamRequest(prepared!);
        upstream.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        HttpResponseMessage response;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            response = await _http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            string resolvedModel = prepared?.Body?["model"]?.ToString() ?? "unknown";
            string requested = prepared?.RequestedModel ?? "unknown";
            return LocalApiCompletionResult.UpstreamFailure(
                $"Upstream streaming request timed out after {stopwatch.ElapsedMilliseconds}ms for model \"{requested}\" (resolved: \"{resolvedModel}\").");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return LocalApiCompletionResult.UpstreamFailure("Could not reach the local model provider: " + ex.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string payload = await response.Content.ReadAsStringAsync(cancellationToken);
                return LocalApiCompletionResult.UpstreamError((int)response.StatusCode, payload);
            }

            await onStarted();
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(source, Encoding.UTF8);
            try
            {
                bool completed = false;
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string data = line[5..].Trim();
                    if (data.Length == 0) continue;
                    if (data == "[DONE]") { completed = true; break; }
                    ChatStreamChunk? chunk = ParseStreamChunk(data);
                    if (chunk == null) continue;
                    if (chunk.FinishReason != null) completed = true;
                    await onChunk(chunk);
                }
                // A connection that closes cleanly before any finish_reason or [DONE] is a truncated
                // turn, not a finished one; report it instead of encoding a normal completion.
                if (!completed) throw new IOException(IncompleteStreamMessage);
                return LocalApiCompletionResult.Ok(string.Empty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return LocalApiCompletionResult.UpstreamFailure("The local model provider stream terminated unexpectedly: " + ex.Message);
            }
        }
    }

    /// <summary>Parses one OpenAI chat.completion.chunk; reasoning deltas are not client output and are dropped.</summary>
    internal static ChatStreamChunk? ParseStreamChunk(string data)
    {
        JsonObject? chunk;
        try { chunk = JsonNode.Parse(data) as JsonObject; }
        catch (JsonException) { return null; }
        if (chunk == null) return null;

        JsonObject? choice = chunk["choices"] is JsonArray choices && choices.Count > 0 ? choices[0] as JsonObject : null;
        JsonObject? delta = choice?["delta"] as JsonObject;
        string? text = delta?["content"] is JsonValue content && content.TryGetValue(out string? value) ? value : null;
        var tools = new List<ChatStreamToolDelta>();
        if (delta?["tool_calls"] is JsonArray calls)
        {
            for (int position = 0; position < calls.Count; position++)
            {
                if (calls[position] is not JsonObject call) continue;
                int index = call["index"] is JsonValue indexValue && indexValue.TryGetValue(out int parsed) ? parsed : position;
                string? Read(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
                tools.Add(new ChatStreamToolDelta(index, Read(call["id"]), Read(call["function"]?["name"]), Read(call["function"]?["arguments"])));
            }
        }
        string? finish = choice?["finish_reason"] is JsonValue finishValue && finishValue.TryGetValue(out string? reason) ? reason : null;
        JsonNode? usage = chunk["usage"] is JsonObject usageObject ? usageObject.DeepClone() : null;
        if (text == null && tools.Count == 0 && finish == null && usage == null) return null;
        return new ChatStreamChunk(text, tools, finish, usage);
    }

    private async Task<(PreparedCompletion? Prepared, LocalApiCompletionResult? Error)> PrepareAsync(
        string rawBody, bool requireStreaming, CancellationToken cancellationToken)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(rawBody);
        }
        catch (JsonException)
        {
            return (null, LocalApiCompletionResult.BadRequest("Invalid JSON body."));
        }

        if (body is not JsonObject obj)
            return (null, LocalApiCompletionResult.BadRequest("Request body must be a JSON object."));
        if (obj["messages"] is not JsonArray messages || messages.Count == 0)
            return (null, LocalApiCompletionResult.BadRequest("\"messages\" must be a non-empty array."));

        bool streaming = false;
        if (obj.TryGetPropertyValue("stream", out JsonNode? streamNode) && streamNode != null)
        {
            if (streamNode is not JsonValue streamValue || !streamValue.TryGetValue(out streaming))
                return (null, LocalApiCompletionResult.BadRequest("\"stream\" must be a boolean."));
        }

        if (requireStreaming && !streaming)
            return (null, LocalApiCompletionResult.BadRequest("This streaming path requires \"stream\": true."));
        if (!requireStreaming && streaming)
            return (null, LocalApiCompletionResult.BadRequest(
                "Streaming requests must use the SSE streaming path; set \"stream\": false or omit it for a buffered completion."));

        string requestedModel = "parux-main";
        if (obj.TryGetPropertyValue("model", out JsonNode? modelNode) && modelNode != null)
        {
            if (modelNode is not JsonValue modelValue
                || !modelValue.TryGetValue(out string? modelId)
                || string.IsNullOrWhiteSpace(modelId))
                return (null, LocalApiCompletionResult.BadRequest("\"model\" must be a non-empty string."));
            requestedModel = modelId;
        }

        // Virtual ids (parux-auto/-code/-reasoning/-fast/-vision), parux-{role} ids and the vendor
        // ids agent clients send by default all resolve here, for every protocol adapter alike.
        PaRuX.Server.Gateway.GatewayModelAliases.Resolution? alias =
            PaRuX.Server.Gateway.GatewayModelAliases.Resolve(requestedModel, VendorModelDefault);
        if (alias == null)
        {
            string knownIds = string.Join(", ", PaRuX.Server.Gateway.GatewayModelAliases.Virtual.Select(v => v.Id)
                .Concat(ModelRoleCatalog.TextRoles.Select(ModelIdForRole)));
            return (null, LocalApiCompletionResult.BadRequest(
                "Unknown model \"" + requestedModel + "\". Known models: " + knownIds + "."));
        }
        RouteRequest? autoRoute = alias.Automatic ? PaRuX.Server.Gateway.GatewayModelAliases.AutoRouteRequest(obj) : null;
        string role = autoRoute?.Role ?? alias.Role ?? "standard";

        // llama-server exposes only the hashed per-entry aliases while a resident multi-model pool
        // is active, so the logical single-model id is valid only when no pool is serving.
        string providerModel = "local-default";
        if (_runtime.UsesExternalProvider)
        {
            try
            {
                await _runtime.EnsureReadyAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, LocalApiCompletionResult.UpstreamFailure("The local runtime is not ready: " + ex.Message));
            }
        }
        else
        {
            RouteDecisionDto routing;
            try
            {
                routing = autoRoute != null && autoRoute.Role == null
                    ? await _router.ResolveAsync(autoRoute, cancellationToken)
                    : await ResolveRoleAsync(role, cancellationToken);
                LastRoutingDecision = new GatewayRoutingRecord(requestedModel, alias.Source,
                    autoRoute?.Role ?? alias.Role ?? "auto", routing.Success ? Path.GetFileName(routing.ModelPath ?? string.Empty) : null,
                    DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, LocalApiCompletionResult.UpstreamFailure(
                    "Routing to model \"" + requestedModel + "\" failed: " + ex.Message));
            }
            if (!routing.Success)
                return (null, LocalApiCompletionResult.UpstreamFailure(
                    "Could not ensure model \"" + requestedModel + "\" was loaded: " + routing.Message));
            providerModel = ChatProxyService.ResolveProviderModel(routing);
        }

        // Routing may load a model, but a healthy response alone cannot establish who owns the
        // runtime port. Apply the same final prompt-egress boundary to every protocol adapter.
        if (!_ownsRuntime())
            return (null, LocalApiCompletionResult.UpstreamFailure(
                "PaRuX does not exclusively own the local runtime port; your prompt was not sent. "
                + "Close the other program using that port and load the model again."));

        obj["stream"] = requireStreaming;
        obj["model"] = providerModel;
        GuardAgainstThinkingStarvation(obj);
        return (new PreparedCompletion(obj, requestedModel), null);
    }

    /// <summary>
    /// A reasoning-capable local model can spend its entire per-request token budget on hidden
    /// "thinking" before emitting any visible answer, leaving an ordinary bounded client request
    /// (any OpenAI SDK caller that sets max_tokens, which is common and expected usage) with an
    /// empty completion and finish_reason "length" - a silent failure from the caller's point of
    /// view. This endpoint is deliberately a thin, untouched-fields passthrough (see the class
    /// doc comment), so this only adds a field the caller left unset rather than overriding one
    /// they specified: a caller who already supplied chat_template_kwargs, or who requested a
    /// budget generous enough to plausibly cover this server's own configured thinking length,
    /// keeps exactly the reasoning behavior it asked for. MinimumThinkingBudgetTokens mirrors the
    /// smallest --reasoning-budget PaRuX itself ever launches llama-server with (see
    /// LocalAiProcessManager); a caller asking for less than that cannot be safely budgeted for
    /// both thinking and a visible answer, so thinking is turned off for that one request instead
    /// of risking an empty response.
    /// </summary>
    private const int MinimumThinkingBudgetTokens = 512;

    internal static void GuardAgainstThinkingStarvation(JsonObject obj)
    {
        if (obj.ContainsKey("chat_template_kwargs")) return;
        // max_completion_tokens is OpenAI's current name for the same cap; llama-server honours
        // either, so a small value in either field starves hidden thinking identically.
        int? maxTokens = null;
        foreach (string key in new[] { "max_completion_tokens", "max_tokens" })
        {
            if (obj.TryGetPropertyValue(key, out JsonNode? node) && node is JsonValue value && value.TryGetValue(out int parsed))
            {
                maxTokens = parsed;
                break;
            }
        }
        if (maxTokens is not { } cap || cap <= 0 || cap >= MinimumThinkingBudgetTokens) return;

        obj["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
    }

    private HttpRequestMessage BuildUpstreamRequest(PreparedCompletion prepared)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, _runtime.ProviderEndpoint.TrimEnd('/') + "/v1/chat/completions")
        {
            Content = new StringContent(prepared.Body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        // No-op unless the endpoint is genuinely remote and the user stored a key.
        ExternalTextProviderService.ApplyRemoteAuthorization(request, _runtime.ProviderEndpoint);
        return request;
    }

    private static async Task PumpSseAsync(
        HttpContent content, Stream destination, string requestedModel, CancellationToken cancellationToken)
    {
        await using Stream source = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(source, Encoding.UTF8);
        bool completed = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!completed && IsTerminalSseLine(line)) completed = true;
            string rewritten = RewriteSseDataLine(line, requestedModel);
            byte[] bytes = Encoding.UTF8.GetBytes(rewritten + "\n");
            await destination.WriteAsync(bytes, cancellationToken);
            if (line.Length == 0) await destination.FlushAsync(cancellationToken);
        }

        await destination.FlushAsync(cancellationToken);
        if (!completed) throw new IOException(IncompleteStreamMessage);
    }

    private const string IncompleteStreamMessage = "the stream ended before the model finished its answer";

    /// <summary>True for the provider's [DONE] sentinel or a chunk that carries a finish_reason.</summary>
    private static bool IsTerminalSseLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return false;
        string data = line[5..].Trim();
        if (data == "[DONE]") return true;
        if (data.Length == 0) return false;
        try
        {
            return JsonNode.Parse(data) is JsonObject chunk
                && chunk["choices"] is JsonArray { Count: > 0 } choices
                && choices[0] is JsonObject choice
                && choice["finish_reason"] is JsonValue finish && finish.TryGetValue(out string? _);
        }
        catch (JsonException) { return false; }
    }

    private static string RewriteSseDataLine(string line, string requestedModel)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return line;

        string data = line[5..].Trim();
        if (data.Length == 0 || data == "[DONE]") return line;

        try
        {
            if (JsonNode.Parse(data) is JsonObject chunk && chunk.ContainsKey("model"))
            {
                chunk["model"] = requestedModel;
                return "data: " + chunk.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // A non-JSON SSE data line is still valid SSE; proxy it untouched.
        }

        return line;
    }

    private sealed record PreparedCompletion(JsonObject Body, string RequestedModel);
}

/// <summary>
/// OpenAI-compatible <c>POST /v1/embeddings</c>. PaRuX already ships and uses a local embedding
/// engine (MiniLM, 384-dim) for Memory and RAG retrieval, but before this it was reachable only
/// through PaRuX's own API - so third-party tooling that speaks the OpenAI embeddings protocol
/// (retrieval libraries, IDE extensions, other agent runtimes) could use a competing local runtime
/// as its embedding provider but not PaRuX. This closes that interop gap without introducing any
/// cloud dependency: it is a thin protocol adapter over the exact same local engine.
///
/// It never fabricates vectors. If the engine is not installed or cannot load, the caller gets an
/// explicit 503 telling it how to install the engine, rather than zeros that would silently poison
/// a caller's vector store.
/// </summary>
public sealed class LocalApiEmbeddingsService
{
    /// <summary>Matches ListModels' "parux-{role}" convention so a client can discover and use
    /// this id the same way it discovers chat models.</summary>
    public const string ModelId = "parux-embedding";

    private readonly EmbeddingService _embeddings;

    public LocalApiEmbeddingsService(EmbeddingService embeddings) => _embeddings = embeddings;

    public LocalApiCompletionResult CreateEmbeddings(string rawBody)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(rawBody); }
        catch (JsonException) { return LocalApiCompletionResult.BadRequest("Request body is not valid JSON."); }
        if (parsed is not JsonObject request)
            return LocalApiCompletionResult.BadRequest("Request body must be a JSON object.");

        if (!TryReadInputs(request["input"], out List<string> inputs, out string? inputError))
            return LocalApiCompletionResult.BadRequest(inputError!);

        // "float" is the default; base64 is deliberately rejected rather than silently ignored,
        // which would hand the caller float arrays it is not expecting to parse.
        string? encodingFormat = request["encoding_format"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(encodingFormat)
            && !string.Equals(encodingFormat, "float", StringComparison.OrdinalIgnoreCase))
        {
            return LocalApiCompletionResult.BadRequest(
                "Only encoding_format \"float\" is supported by the local PaRuX embedding engine.");
        }

        if (!_embeddings.IsInstalled)
        {
            return new LocalApiCompletionResult(
                LocalApiOutcome.UpstreamFailure,
                503,
                JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        message = "The local PaRuX embedding engine is not installed. "
                            + "Install it with POST /api/v1/embeddings/engine/install, then retry."
                    }
                }));
        }

        var data = new JsonArray();
        int totalTokens = 0;
        for (int index = 0; index < inputs.Count; index++)
        {
            float[]? vector = _embeddings.TryEmbed(inputs[index]);
            if (vector == null)
            {
                return new LocalApiCompletionResult(
                    LocalApiOutcome.UpstreamFailure,
                    503,
                    JsonSerializer.Serialize(new
                    {
                        error = new
                        {
                            message = "The local PaRuX embedding engine failed to embed input at index "
                                + index + "."
                        }
                    }));
            }

            var embedding = new JsonArray();
            foreach (float value in vector) embedding.Add(value);
            data.Add(new JsonObject
            {
                ["object"] = "embedding",
                ["index"] = index,
                ["embedding"] = embedding
            });
            totalTokens += EstimateTokens(inputs[index]);
        }

        var response = new JsonObject
        {
            ["object"] = "list",
            ["data"] = data,
            ["model"] = ModelId,
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = totalTokens,
                ["total_tokens"] = totalTokens
            }
        };
        return LocalApiCompletionResult.Ok(response.ToJsonString());
    }

    /// <summary>OpenAI accepts a single string or an array of strings. Token-id array inputs are
    /// rejected explicitly: this engine embeds text, and silently mis-reading token ids as text
    /// would return confidently wrong vectors.</summary>
    private static bool TryReadInputs(JsonNode? input, out List<string> inputs, out string? error)
    {
        inputs = new List<string>();
        error = null;

        switch (input)
        {
            case null:
                error = "\"input\" is required.";
                return false;
            case JsonValue value when value.TryGetValue(out string? single):
                if (string.IsNullOrEmpty(single)) { error = "\"input\" may not be empty."; return false; }
                inputs.Add(single);
                return true;
            case JsonArray array:
                if (array.Count == 0) { error = "\"input\" may not be an empty array."; return false; }
                foreach (JsonNode? item in array)
                {
                    if (item is JsonValue itemValue && itemValue.TryGetValue(out string? text) && text.Length > 0)
                    {
                        inputs.Add(text);
                        continue;
                    }
                    error = "\"input\" array must contain only non-empty strings; "
                        + "token-id array input is not supported by the local embedding engine.";
                    return false;
                }
                return true;
            default:
                error = "\"input\" must be a string or an array of strings.";
                return false;
        }
    }

    /// <summary>Reported usage is an explicit approximation of this engine's tokenizer, not a
    /// billing figure; it exists because OpenAI-compatible clients read the field.</summary>
    private static int EstimateTokens(string text) => Math.Max(1, (text.Length + 3) / 4);
}

public enum LocalApiOutcome { Ok, BadRequest, UpstreamFailure }

public sealed record LocalApiCompletionResult(LocalApiOutcome Outcome, int StatusCode, string Body)
{
    public static LocalApiCompletionResult Ok(string json) => new(LocalApiOutcome.Ok, 200, json);

    public static LocalApiCompletionResult BadRequest(string message) =>
        new(LocalApiOutcome.BadRequest, 400, JsonSerializer.Serialize(new { error = new { message } }));

    public static LocalApiCompletionResult UpstreamFailure(string message) =>
        new(LocalApiOutcome.UpstreamFailure, 502, JsonSerializer.Serialize(new { error = new { message } }));

    public static LocalApiCompletionResult UpstreamError(int statusCode, string body) =>
        new(LocalApiOutcome.UpstreamFailure, statusCode, body);
}
