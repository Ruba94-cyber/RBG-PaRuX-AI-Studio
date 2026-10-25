using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>
/// Compatibility surfaces for clients built against OpenAI Responses or Anthropic Messages.
/// Everything translates into PaRuX's authoritative LocalApiServerService; PaRuX never executes
/// caller-declared tools here. Tool schemas, tool calls/results and bounded multimodal content are
/// protocol data passed between the client and the already-selected local model.
///
/// Stateful Responses continuation is intentionally bounded/in-memory. A response id is a protocol
/// convenience, not a durable conversation secret: restart invalidates it and old entries are evicted.
/// </summary>
public static partial class LocalProtocolCompatibilityEndpoints
{
    private const int MaxResponseStates = 256;
    private const int MaxMessagesPerState = 128;
    private const int MaxTools = 64;
    private const int MaxContentBlocks = 64;
    private const int MaxToolNameCharacters = 128;
    private const int MaxToolArgumentsCharacters = 1_000_000;
    private const int MaxImageReferenceCharacters = 16_000_000;

    private static readonly ConcurrentDictionary<string, ResponseState> ResponseStates = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> ResponseOrder = new();
    private static readonly RuntimeTokenCounter TokenCounter = new(new HttpClient(ManagedRuntimePeers.CreateHandler()) { Timeout = TimeSpan.FromSeconds(30) });

    internal sealed record CompatMessage(string Role, string Content);
    private sealed record ResponseState(string Model, List<JsonObject> Messages, DateTime CreatedUtc);
    private sealed record ChatAssistantOutput(string Text, JsonNode? Content, JsonArray ToolCalls, JsonNode? Usage, string? FinishReason = null);

    public static void MapLocalProtocolCompatibilityEndpoints(this WebApplication app)
    {
        app.MapProductV2Endpoints();

        app.MapPost("/v1/responses", async (
            HttpRequest request, HttpResponse response, LocalApiServerService localApi, LocalRuntimeService runtime, CancellationToken ct) =>
        {
            string? body = await ReadBodyAsync(request, (status, message) => WriteProtocolErrorAsync(response, status, message, ct), ct).ConfigureAwait(false);
            if (body == null) return;
            (JsonObject? normalized, string? error, bool stream, string requestedModel, List<CompatMessage>? messages) =
                NormalizeResponsesRequest(body);
            if (normalized == null || messages == null)
            {
                await WriteProtocolErrorAsync(response, StatusCodes.Status400BadRequest,
                    error ?? "Invalid Responses request.", ct).ConfigureAwait(false);
                return;
            }

            // Gaming Mode stops gateway requests exactly like PaRuX's own chat requests.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.GamingCancellationToken);
            string id = "resp_" + Guid.NewGuid().ToString("N");
            JsonArray stateMessages = normalized["messages"]!.AsArray().DeepClone().AsArray();
            try
            {
                if (stream)
                {
                    (bool streamed, LocalApiCompletionResult? streamError, ChatStreamAccumulator turn, _) = await RunStreamAsync(
                        localApi, normalized, response, ": keepalive\n\n",
                        sink =>
                        {
                            var encoder = new ResponsesStreamEncoder(sink, id, requestedModel);
                            return (encoder.StartAsync, encoder.TextAsync, encoder.ToolAsync,
                                async t => (JsonObject?)await encoder.FinishAsync(t.Usage, t.FinishReason).ConfigureAwait(false),
                                encoder.FailAsync);
                        }, linked.Token).ConfigureAwait(false);
                    if (!streamed)
                    {
                        await WriteProtocolErrorAsync(response, streamError!.StatusCode,
                            ExtractErrorMessage(streamError.Body) ?? "PaRuX local completion failed.", ct).ConfigureAwait(false);
                        return;
                    }
                    // A failed stream ended in response.failed; its partial turn is not continuation state.
                    if (streamError == null)
                    {
                        stateMessages.Add(turn.ToChatMessage());
                        SaveResponseState(id, requestedModel, stateMessages);
                    }
                    // The Responses API ends at response.completed; a Chat-Completions style "[DONE]" line is
                    // not JSON, and Codex treated it as a broken stream and re-sent the turn (live smoke).
                    return;
                }

                LocalApiCompletionResult result = await localApi.CompleteAsync(normalized.ToJsonString(), linked.Token).ConfigureAwait(false);
                if (result.Outcome != LocalApiOutcome.Ok)
                {
                    await WriteProtocolErrorAsync(response, result.StatusCode,
                        ExtractErrorMessage(result.Body) ?? "PaRuX local completion failed.", ct).ConfigureAwait(false);
                    return;
                }

                ChatAssistantOutput output = ExtractChatAnswer(result.Body);
                stateMessages.Add(BuildChatAssistantMessage(output));
                SaveResponseState(id, requestedModel, stateMessages);
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = "application/json; charset=utf-8";
                await response.WriteAsync(BuildResponsesPayload(id, requestedModel, output).ToJsonString(), ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await HandleStreamFailureAsync(response, exception, ct,
                    (status, message) => WriteProtocolErrorAsync(response, status, message, ct)).ConfigureAwait(false);
            }
        });

        // Anthropic token counting (Claude Code uses it for context management). Counted with the
        // served model's own chat template and tools when the managed runtime is up; otherwise a
        // labelled estimate, never a silent guess.
        app.MapPost("/v1/messages/count_tokens", async (
            HttpRequest request, HttpResponse response, LocalRuntimeService runtime, CancellationToken ct) =>
        {
            string? body = await ReadBodyAsync(request, (status, message) => WriteAnthropicErrorAsync(response, status, message, ct), ct).ConfigureAwait(false);
            if (body == null) return;
            (JsonObject? normalized, string? error, _, _) = NormalizeAnthropicRequest(body);
            if (normalized == null)
            {
                await WriteAnthropicErrorAsync(response, StatusCodes.Status400BadRequest,
                    error ?? "Invalid count_tokens request.", ct).ConfigureAwait(false);
                return;
            }
            normalized["model"] = "local-default";
            await WriteInputTokenCountResponseAsync(
                response,
                normalized,
                (payload, token) => TokenCounter.TryCountRenderedChatPromptAsync(runtime.ProviderEndpoint, payload, token),
                ct,
                await TryReadMaxInputTokensAsync(runtime.ProviderEndpoint, ct).ConfigureAwait(false))
                .ConfigureAwait(false);
        });

        app.MapPost("/v1/messages", async (
            HttpRequest request, HttpResponse response, LocalApiServerService localApi, LocalRuntimeService runtime, CancellationToken ct) =>
        {
            string? body = await ReadBodyAsync(request, (status, message) => WriteAnthropicErrorAsync(response, status, message, ct), ct).ConfigureAwait(false);
            if (body == null) return;
            (JsonObject? normalized, string? error, bool stream, string requestedModel) = NormalizeAnthropicRequest(body);
            if (normalized == null)
            {
                await WriteAnthropicErrorAsync(response, StatusCodes.Status400BadRequest,
                    error ?? "Invalid Messages request.", ct).ConfigureAwait(false);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.GamingCancellationToken);
            string id = "msg_" + Guid.NewGuid().ToString("N");
            try
            {
                if (stream)
                {
                    (bool streamed, LocalApiCompletionResult? streamError, _, _) = await RunStreamAsync(
                        localApi, normalized, response, "event: ping\ndata: {\"type\":\"ping\"}\n\n",
                        sink =>
                        {
                            var encoder = new AnthropicStreamEncoder(sink, id, requestedModel);
                            return (encoder.StartAsync, encoder.TextAsync, encoder.ToolAsync,
                                async t => { await encoder.FinishAsync(t.FinishReason, t.Usage).ConfigureAwait(false); return null; },
                                encoder.FailAsync);
                        }, linked.Token).ConfigureAwait(false);
                    if (!streamed)
                        await WriteAnthropicErrorAsync(response, streamError!.StatusCode,
                            ExtractErrorMessage(streamError.Body) ?? "PaRuX local completion failed.", ct).ConfigureAwait(false);
                    return;
                }

                LocalApiCompletionResult result = await localApi.CompleteAsync(normalized.ToJsonString(), linked.Token).ConfigureAwait(false);
                if (result.Outcome != LocalApiOutcome.Ok)
                {
                    await WriteAnthropicErrorAsync(response, result.StatusCode,
                        ExtractErrorMessage(result.Body) ?? "PaRuX local completion failed.", ct).ConfigureAwait(false);
                    return;
                }

                ChatAssistantOutput output = ExtractChatAnswer(result.Body);
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = "application/json; charset=utf-8";
                await response.WriteAsync(BuildAnthropicPayload(id, requestedModel, output).ToJsonString(), ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await HandleStreamFailureAsync(response, exception, ct,
                    (status, message) => WriteAnthropicErrorAsync(response, status, message, ct)).ConfigureAwait(false);
            }
        });
    }

    internal static (JsonObject? Request, string? Error, bool Stream, string Model, List<CompatMessage>? Messages)
        NormalizeResponsesRequest(string body)
    {
        JsonObject? input;
        try { input = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return (null, "Invalid JSON body.", false, "parux-main", null); }
        if (input == null) return (null, "Request body must be an object.", false, "parux-main", null);

        if (!ValidateSupportedFields(input, "Responses request", out string? fieldError,
                "model", "stream", "previous_response_id", "instructions", "input", "tools",
                "tool_choice", "temperature", "top_p", "parallel_tool_calls", "max_output_tokens"))
            return (null, fieldError, false, ReadString(input["model"]) ?? "parux-main", null);
        if (input["model"] != null && ReadString(input["model"]) == null)
            return (null, "model must be a string.", false, "parux-main", null);
        if (input["stream"] != null && !TryReadBoolean(input["stream"], out _))
            return (null, "stream must be a boolean.", false, ReadString(input["model"]) ?? "parux-main", null);
        if (input["previous_response_id"] != null && ReadString(input["previous_response_id"]) == null)
            return (null, "previous_response_id must be a string.", false, ReadString(input["model"]) ?? "parux-main", null);
        if (!ValidateSamplingValue(input, "temperature", 0, 2, out fieldError)
            || !ValidateSamplingValue(input, "top_p", 0, 1, out fieldError))
            return (null, fieldError, ReadBool(input["stream"]), ReadString(input["model"]) ?? "parux-main", null);
        if (input["parallel_tool_calls"] != null && !TryReadBoolean(input["parallel_tool_calls"], out _))
            return (null, "parallel_tool_calls must be a boolean.", ReadBool(input["stream"]), ReadString(input["model"]) ?? "parux-main", null);
        if (input["max_output_tokens"] != null && !TryReadPositiveInteger(input["max_output_tokens"], out _))
            return (null, "max_output_tokens must be a positive integer.", ReadBool(input["stream"]), ReadString(input["model"]) ?? "parux-main", null);
        if (input["instructions"] != null && !ValidateTextContent(input["instructions"], "instructions", out fieldError))
            return (null, fieldError, false, ReadString(input["model"]) ?? "parux-main", null);

        string model = ReadString(input["model"]) ?? "parux-main";
        bool stream = ReadBool(input["stream"]);
        var chatMessages = new List<JsonObject>();
        var compatibilityMessages = new List<CompatMessage>();

        string? previousId = ReadString(input["previous_response_id"]);
        if (!string.IsNullOrWhiteSpace(previousId))
        {
            if (!ResponseStates.TryGetValue(previousId, out ResponseState? previous))
                return (null, "previous_response_id is unknown or was evicted/restarted.", stream, model, null);
            foreach (JsonObject message in previous.Messages.TakeLast(MaxMessagesPerState))
            {
                chatMessages.Add((JsonObject)message.DeepClone());
                AddCompatibilityProjection(compatibilityMessages, message);
            }
            if (input["model"] == null) model = previous.Model;
        }

        if (input["instructions"] is JsonNode instructions)
        {
            string? system = ExtractContentText(instructions);
            if (!string.IsNullOrWhiteSpace(system))
            {
                chatMessages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
                compatibilityMessages.Add(new CompatMessage("system", system));
            }
        }

        if (!AppendResponsesInput(chatMessages, compatibilityMessages, input["input"], out string? inputError))
            return (null, inputError, stream, model, null);
        if (chatMessages.Count == 0)
            return (null, "input must contain at least one supported message or tool result.", stream, model, null);
        if (chatMessages.Count > MaxMessagesPerState)
            chatMessages = TrimConversation(chatMessages, MaxMessagesPerState);
        if (compatibilityMessages.Count > MaxMessagesPerState)
            compatibilityMessages = compatibilityMessages.TakeLast(MaxMessagesPerState).ToList();

        var normalized = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["messages"] = new JsonArray(chatMessages.Select(message => message.DeepClone()).ToArray())
        };
        if (!TryNormalizeResponsesTools(input, normalized, out string? toolError))
            return (null, toolError, stream, model, null);
        CopyGenerationOption(input, normalized, "temperature");
        CopyGenerationOption(input, normalized, "top_p");
        CopyGenerationOption(input, normalized, "parallel_tool_calls");
        if (input["max_output_tokens"] is JsonNode max) normalized["max_tokens"] = max.DeepClone();
        return (normalized, null, stream, model, compatibilityMessages);
    }

    private static readonly HttpClient ContextProbeHttp =
        new(ManagedRuntimePeers.CreateHandler()) { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>Safe input budget (window minus the chat reply reserve) of the live runtime, or null when unknown.</summary>
    private static async Task<int?> TryReadMaxInputTokensAsync(string providerEndpoint, CancellationToken ct)
    {
        try
        {
            int window = await ActiveContextWindow.GetAsync(ContextProbeHttp, providerEndpoint, ct).ConfigureAwait(false);
            return window > ChatInputReserveTokens ? window - ChatInputReserveTokens : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    internal const int ChatInputReserveTokens = 1024;

    /// <summary>
    /// Character-estimate fallback used only when the runtime tokenizer is unreachable. Counts message
    /// bodies, a per-message template overhead and the tool block (which renders larger than its raw
    /// schema, so it is priced at twice its length like the chat budget). Never presented as exact.
    /// </summary>
    internal static int EstimateInputTokens(JsonObject normalized)
    {
        int messageCount = (normalized["messages"] as JsonArray)?.Count ?? 0;
        int messages = PaRuX.Intelligence.ConversationWindowPlanner.EstimateTokens(
            normalized["messages"]?.ToJsonString() ?? string.Empty);
        int tools = normalized["tools"] is JsonNode toolNode
            ? 2 * PaRuX.Intelligence.ConversationWindowPlanner.EstimateTokens(toolNode.ToJsonString())
            : 0;
        return messages + tools + (messageCount * RuntimeTokenCounter.PerMessageTemplateOverheadTokens);
    }

    internal static async Task WriteInputTokenCountResponseAsync(
        HttpResponse response,
        JsonObject normalized,
        Func<JsonObject, CancellationToken, Task<int?>> tryCountRenderedPromptAsync,
        CancellationToken cancellationToken,
        int? maxInputTokens = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(normalized);
        ArgumentNullException.ThrowIfNull(tryCountRenderedPromptAsync);

        int? tokenizerCount = await tryCountRenderedPromptAsync(normalized, cancellationToken).ConfigureAwait(false);
        int count = tokenizerCount ?? EstimateInputTokens(normalized);
        bool exact = tokenizerCount.HasValue;
        string basis = exact
            ? nameof(RuntimeTokenCounter.CountBasis.Tokenizer)
            : nameof(RuntimeTokenCounter.CountBasis.Estimated);

        response.Headers["X-PaRuX-Token-Count"] = exact ? "exact" : "estimated";
        if (maxInputTokens is int limit && count > limit)
        {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            response.ContentType = "application/json; charset=utf-8";
            await response.WriteAsync(new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject
                {
                    ["type"] = "invalid_request_error",
                    ["code"] = PromptExceedsContextException.ErrorCode,
                    ["message"] = (exact ? "The prompt needs " : "The prompt is estimated (not exact) at about ")
                        + count.ToString("N0") + " input tokens, above the " + limit.ToString("N0")
                        + " this model can accept. " + PromptExceedsContextException.RemediationText,
                    ["input_tokens"] = count,
                    ["input_tokens_basis"] = basis,
                    ["exact"] = exact
                }
            }.ToJsonString(), cancellationToken).ConfigureAwait(false);
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json; charset=utf-8";
        var result = new JsonObject
        {
            ["input_tokens"] = count,
            ["input_tokens_basis"] = basis,
            ["exact"] = exact
        };
        if (!exact)
            result["note"] = "Estimate: the runtime tokenizer was unavailable, so this count is approximate and not exact.";
        await response.WriteAsync(result.ToJsonString(), cancellationToken).ConfigureAwait(false);
    }

    internal static bool AnthropicRequestsThinking(JsonNode? thinking) =>
        thinking is JsonObject settings && ReadString(settings["type"]) is string type
        && !string.Equals(type, "disabled", StringComparison.OrdinalIgnoreCase);

    internal static (JsonObject? Request, string? Error, bool Stream, string Model)
        NormalizeAnthropicRequest(string body)
    {
        JsonObject? input;
        try { input = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return (null, "Invalid JSON body.", false, "parux-main"); }
        if (input == null) return (null, "Request body must be an object.", false, "parux-main");
        if (!ValidateSupportedFields(input, "Anthropic request", out string? fieldError,
                "model", "messages", "stream", "system", "tools", "tool_choice",
                "temperature", "top_p", "max_tokens", "stop_sequences"))
            return (null, fieldError, false, ReadString(input["model"]) ?? "parux-main");
        if (input["stream"] != null && !TryReadBoolean(input["stream"], out _))
            return (null, "stream must be a boolean.", false, ReadString(input["model"]) ?? "parux-main");
        if (!ValidateSamplingValue(input, "temperature", 0, 1, out fieldError)
            || !ValidateSamplingValue(input, "top_p", 0, 1, out fieldError))
            return (null, fieldError, ReadBool(input["stream"]), ReadString(input["model"]) ?? "parux-main");
        if (input["max_tokens"] != null && !TryReadPositiveInteger(input["max_tokens"], out _))
            return (null, "max_tokens must be a positive integer.", ReadBool(input["stream"]), ReadString(input["model"]) ?? "parux-main");
        if (input["stop_sequences"] != null && !AreStringArray(input["stop_sequences"]))
            return (null, "stop_sequences must be an array of strings.", ReadBool(input["stream"]), ReadString(input["model"]) ?? "parux-main");
        string? model = ReadString(input["model"]);
        if (string.IsNullOrWhiteSpace(model)) return (null, "model is required.", false, "parux-main");
        if (input["system"] != null && !ValidateTextContent(input["system"], "system", out fieldError))
            return (null, fieldError, false, model);
        if (input["messages"] is not JsonArray source || source.Count == 0)
            return (null, "messages must be a non-empty array.", false, model);

        bool stream = ReadBool(input["stream"]);
        var messages = new List<JsonObject>();
        string? system = ExtractContentText(input["system"]);
        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        foreach (JsonNode? node in source)
        {
            if (node is not JsonObject message)
                return (null, "each Anthropic message must be an object.", stream, model);
            if (!ValidateSupportedFields(message, "Anthropic message", out fieldError, "role", "content"))
                return (null, fieldError, stream, model);
            string? role = ReadString(message["role"]);
            if (role == "system")
            {
                // Claude Code's mid-conversation-system beta sends system turns inside messages.
                // Local chat templates accept a system prompt only first: fold leading ones into
                // it, and carry later ones as a user-visible reminder at their point in the turn.
                string? systemText = ExtractContentText(message["content"]);
                if (string.IsNullOrWhiteSpace(systemText)) continue;
                if (messages.All(existing => ReadString(existing["role"]) == "system"))
                {
                    if (messages.Count > 0)
                        messages[0]["content"] = (ReadString(messages[0]["content"]) ?? string.Empty) + "\n\n" + systemText;
                    else
                        messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemText });
                }
                else
                {
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = "<system-reminder>\n" + systemText + "\n</system-reminder>" });
                }
                continue;
            }
            if (role is not ("user" or "assistant"))
                return (null, "message role must be user, assistant or system.", stream, model);
            if (!AppendAnthropicMessage(messages, role, message["content"], out string? contentError))
                return (null, contentError, stream, model);
        }
        if (messages.Count == 0)
            return (null, "messages contained no supported text, image or tool content.", stream, model);
        if (messages.Count > MaxMessagesPerState)
            messages = TrimConversation(messages, MaxMessagesPerState);

        var normalized = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["messages"] = new JsonArray(messages.Select(message => message.DeepClone()).ToArray())
        };
        if (!TryNormalizeAnthropicTools(input, normalized, out string? toolError))
            return (null, toolError, stream, model);
        CopyGenerationOption(input, normalized, "temperature");
        CopyGenerationOption(input, normalized, "top_p");
        if (input["max_tokens"] is JsonNode max) normalized["max_tokens"] = max.DeepClone();
        if (input["stop_sequences"] is JsonNode stop) normalized["stop"] = stop.DeepClone();
        // Anthropic semantics: a model thinks only when the request asks for it. Without this, a
        // reasoning-capable local model could spend the caller's whole max_tokens on hidden
        // reasoning and return an empty answer (live: 600/600 tokens, no text). Claude Code sends
        // thinking (adaptive/enabled) and keeps it.
        if (!AnthropicRequestsThinking(input["thinking"]))
            normalized["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
        return (normalized, null, stream, model);
    }

    /// <summary>
    /// Bounds a long agent conversation without breaking it. A plain TakeLast dropped the leading
    /// system prompt (agent clients such as Claude Code exceed the cap in ordinary sessions) and could
    /// start the window on a tool result whose assistant tool call had been cut, which chat templates
    /// reject. Leading system messages are kept, and the tail starts at a user message.
    /// </summary>
    internal static List<JsonObject> TrimConversation(List<JsonObject> messages, int max)
    {
        if (messages.Count <= max) return messages;
        List<JsonObject> system = messages.TakeWhile(m => ReadString(m["role"]) == "system").ToList();
        int budget = Math.Max(1, max - system.Count);
        int start = messages.Count - budget;
        while (start < messages.Count && ReadString(messages[start]["role"]) != "user") start++;
        if (start >= messages.Count) start = messages.Count - budget;
        return system.Concat(messages.Skip(Math.Max(start, system.Count))).ToList();
    }

    private static bool AppendResponsesInput(
        List<JsonObject> chatMessages,
        List<CompatMessage> compatibilityMessages,
        JsonNode? input,
        out string? error)
    {
        error = null;
        if (input == null) { error = "input is required."; return false; }
        if (input is JsonValue)
        {
            string? text = ExtractContentText(input);
            if (string.IsNullOrWhiteSpace(text)) { error = "input text is empty."; return false; }
            chatMessages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
            compatibilityMessages.Add(new CompatMessage("user", text));
            return true;
        }
        if (input is not JsonArray array) { error = "input must be a string or message array."; return false; }
        if (array.Count > MaxMessagesPerState * 2) { error = "input contains too many items."; return false; }

        foreach (JsonNode? item in array)
        {
            if (item is JsonValue)
            {
                string? text = ExtractContentText(item);
                if (text == null) { error = "input array items must be strings or objects."; return false; }
                if (string.IsNullOrWhiteSpace(text)) continue;
                chatMessages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
                compatibilityMessages.Add(new CompatMessage("user", text));
                continue;
            }
            if (item is not JsonObject obj) { error = "input array items must be strings or objects."; return false; }
            if (obj["type"] != null && ReadString(obj["type"]) == null)
            { error = "input item type must be a string."; return false; }
            string type = ReadString(obj["type"]) ?? "message";

            if (type == "function_call")
            {
                if (!ValidateSupportedFields(obj, "function_call item", out error,
                        "type", "id", "call_id", "name", "arguments", "status")) return false;
                if (obj["status"] != null && ReadString(obj["status"]) != "completed")
                { error = "function_call status must be completed when supplied."; return false; }
                string? callId = ReadString(obj["call_id"]) ?? ReadString(obj["id"]);
                string? name = ReadString(obj["name"]);
                if (obj["arguments"] != null && ReadString(obj["arguments"]) == null)
                { error = "function_call arguments must be a JSON string."; return false; }
                string arguments = ReadString(obj["arguments"]) ?? "{}";
                if (!ValidateToolCall(callId, name, arguments, out error)) return false;
                chatMessages.Add(BuildAssistantToolCallMessage(callId!, name!, arguments));
                continue;
            }

            if (type == "function_call_output")
            {
                if (!ValidateSupportedFields(obj, "function_call_output item", out error,
                        "type", "id", "call_id", "output", "status")) return false;
                if (obj["status"] != null && ReadString(obj["status"]) != "completed")
                { error = "function_call_output status must be completed when supplied."; return false; }
                string? callId = ReadString(obj["call_id"]);
                if (string.IsNullOrWhiteSpace(callId)) { error = "function_call_output requires call_id."; return false; }
                if (obj["output"] == null) { error = "function_call_output requires output."; return false; }
                if (!TryNormalizeOpenAiContent(obj["output"], allowImages: true, out JsonNode? output, out error))
                    return false;
                chatMessages.Add(new JsonObject
                {
                    ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = output ?? ""
                });
                continue;
            }

            // Codex replays prior reasoning items (encrypted, provider-specific) in stateless
            // multi-turn requests; they carry no content a local model can use.
            if (type == "reasoning") continue;
            if (type != "message") { error = "Unsupported Responses input item type: " + type + "."; return false; }
            if (!ValidateSupportedFields(obj, "message input item", out error, "type", "role", "content", "text"))
                return false;
            if (obj["role"] != null && ReadString(obj["role"]) == null)
            { error = "message role must be a string."; return false; }
            if (obj["content"] != null && obj["text"] != null)
            { error = "message input cannot supply both content and text."; return false; }
            string role = ReadString(obj["role"]) ?? "user";
            // "developer" is OpenAI's current name for system instructions (Codex sends it).
            if (role == "developer") role = "system";
            if (role is not ("system" or "user" or "assistant"))
            { error = "Unsupported message role: " + role + "."; return false; }
            bool hasContent = obj.TryGetPropertyValue("content", out JsonNode? messageContent);
            bool hasText = obj.TryGetPropertyValue("text", out JsonNode? messageText);
            if (hasContent == hasText || (hasContent && messageContent == null) || (hasText && messageText == null))
            { error = "message input requires exactly one non-null content or text field."; return false; }
            bool allowImages = role == "user";
            if (!TryNormalizeOpenAiContent(messageContent ?? messageText, allowImages, out JsonNode? content, out error))
                return false;
            if (content == null) continue;
            if (role == "system" && chatMessages.Any(existing => ReadString(existing["role"]) != "system"))
            {
                // Local chat templates accept system instructions only at the start: a later
                // system/developer item is carried as a reminder at its point in the conversation.
                string reminder = ExtractContentText(content) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(reminder)) continue;
                role = "user";
                content = "<system-reminder>\n" + reminder + "\n</system-reminder>";
            }
            else if (role == "system" && chatMessages.Count > 0)
            {
                // Consecutive leading system/developer items merge into one system prompt.
                chatMessages[0]["content"] = (ExtractContentText(chatMessages[0]["content"]) ?? string.Empty) + "\n\n"
                    + (ExtractContentText(content) ?? string.Empty);
                continue;
            }
            JsonObject message = new() { ["role"] = role, ["content"] = content };
            chatMessages.Add(message);
            string? textProjection = ExtractContentText(content);
            if (!string.IsNullOrWhiteSpace(textProjection))
                compatibilityMessages.Add(new CompatMessage(role, textProjection));
        }

        if (chatMessages.Count == 0) { error = "input contained no supported content."; return false; }
        return true;
    }

    private static bool AppendAnthropicMessage(
        List<JsonObject> output,
        string role,
        JsonNode? content,
        out string? error)
    {
        error = null;
        if (content is JsonValue)
        {
            string? text = ExtractContentText(content);
            if (text == null) { error = "message content must be a string or array."; return false; }
            if (text.Length > 0)
                output.Add(new JsonObject { ["role"] = role, ["content"] = text });
            return true;
        }
        if (content is not JsonArray blocks)
        {
            error = "message content must be a string or array.";
            return false;
        }
        if (blocks.Count > MaxContentBlocks) { error = "message content contains too many blocks."; return false; }

        if (role == "assistant")
        {
            var chatContent = new JsonArray();
            var toolCalls = new JsonArray();
            foreach (JsonNode? node in blocks)
            {
                if (node is not JsonObject block) { error = "Anthropic content blocks must be objects."; return false; }
                string? typeValue = ReadString(block["type"]);
                if (typeValue == null) { error = "Anthropic content block type is required and must be a string."; return false; }
                string type = typeValue;
                if (type == "text")
                {
                    if (!ValidateSupportedFields(block, "Anthropic text block", out error, "type", "text")) return false;
                    string? text = ReadString(block["text"]);
                    if (!block.ContainsKey("text") || text == null) { error = "Anthropic text block requires a string text field."; return false; }
                    if (!string.IsNullOrEmpty(text))
                        chatContent.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                }
                else if (type == "tool_use")
                {
                    if (!ValidateSupportedFields(block, "Anthropic tool_use block", out error, "type", "id", "name", "input")) return false;
                    if (block["input"] != null && block["input"] is not JsonObject)
                    { error = "Anthropic tool_use input must be an object."; return false; }
                    string? id = ReadString(block["id"]);
                    string? name = ReadString(block["name"]);
                    string arguments = (block["input"] ?? new JsonObject()).ToJsonString();
                    if (!ValidateToolCall(id, name, arguments, out error)) return false;
                    toolCalls.Add(BuildChatToolCall(id!, name!, arguments));
                }
                else { error = "Unsupported Anthropic assistant content block type: " + type + "."; return false; }
            }

            if (chatContent.Count == 0 && toolCalls.Count == 0) return true;
            var message = new JsonObject { ["role"] = "assistant" };
            message["content"] = chatContent.Count == 0 ? null : chatContent;
            if (toolCalls.Count > 0) message["tool_calls"] = toolCalls;
            output.Add(message);
            return true;
        }

        var pendingUserContent = new JsonArray();
        void FlushUserContent()
        {
            if (pendingUserContent.Count == 0) return;
            output.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = pendingUserContent.Count == 1
                    && ReadString(pendingUserContent[0]?["type"]) == "text"
                    ? pendingUserContent[0]?["text"]?.DeepClone()
                    : pendingUserContent.DeepClone()
            });
            pendingUserContent = new JsonArray();
        }

        foreach (JsonNode? node in blocks)
        {
            if (node is not JsonObject block) { error = "Anthropic content blocks must be objects."; return false; }
            string? typeValue = ReadString(block["type"]);
            if (typeValue == null) { error = "Anthropic content block type is required and must be a string."; return false; }
            string type = typeValue;
            if (type == "text")
            {
                if (!ValidateSupportedFields(block, "Anthropic text block", out error, "type", "text")) return false;
                string? text = ReadString(block["text"]);
                if (!block.ContainsKey("text") || text == null) { error = "Anthropic text block requires a string text field."; return false; }
                if (!string.IsNullOrEmpty(text))
                    pendingUserContent.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                continue;
            }
            if (type == "image")
            {
                if (!ValidateSupportedFields(block, "Anthropic image block", out error, "type", "source")) return false;
                if (!TryNormalizeAnthropicImage(block, out JsonObject? image, out error)) return false;
                if (image != null) pendingUserContent.Add(image);
                continue;
            }
            if (type == "tool_result")
            {
                if (!ValidateSupportedFields(block, "Anthropic tool_result block", out error,
                        "type", "tool_use_id", "content", "is_error")) return false;
                if (block["is_error"] != null && !TryReadBoolean(block["is_error"], out _))
                { error = "tool_result is_error must be a boolean."; return false; }
                if (block["is_error"] != null && ReadBool(block["is_error"]))
                { error = "Error-marked tool_result content is not supported by the local chat compatibility endpoint."; return false; }
                FlushUserContent();
                string? callId = ReadString(block["tool_use_id"]);
                if (string.IsNullOrWhiteSpace(callId)) { error = "tool_result requires tool_use_id."; return false; }
                if (!block.ContainsKey("content") || block["content"] == null)
                { error = "tool_result requires a non-null content field."; return false; }
                if (!TryNormalizeAnthropicToolResultContent(block["content"], out JsonNode? toolContent, out error))
                    return false;
                output.Add(new JsonObject
                {
                    ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = toolContent ?? ""
                });
                continue;
            }
            error = "Unsupported Anthropic user content block type: " + type + ".";
            return false;
        }
        FlushUserContent();
        return true;
    }

    private static bool TryNormalizeResponsesTools(JsonObject input, JsonObject normalized, out string? error)
    {
        error = null;
        if (input["tools"] != null && input["tools"] is not JsonArray)
        { error = "tools must be an array."; return false; }
        if (input["tools"] is JsonArray tools)
        {
            if (tools.Count > MaxTools) { error = "tools exceeds the maximum of " + MaxTools + "."; return false; }
            var chatTools = new JsonArray();
            foreach (JsonNode? node in tools)
            {
                if (node is not JsonObject tool)
                {
                    error = "Each Responses tool must be an object.";
                    return false;
                }
                // Hosted tools (web_search, file_search, code_interpreter) and grouped tool namespaces
                // run on the vendor's servers; a local model cannot provide them. They are left out
                // rather than advertised, and the client keeps working with its function tools.
                if (ReadString(tool["type"]) != "function") continue;
                if (!ValidateSupportedFields(tool, "Responses tool", out error,
                        "type", "name", "description", "parameters", "strict")) return false;
                string? name = ReadString(tool["name"]);
                if (!ValidateToolName(name, out error)) return false;
                if (tool["description"] != null && ReadString(tool["description"]) == null)
                { error = "Responses tool description must be a string."; return false; }
                if (tool["parameters"] != null && tool["parameters"] is not JsonObject)
                { error = "Responses tool parameters must be a JSON Schema object."; return false; }
                if (tool["strict"] != null && !TryReadBoolean(tool["strict"], out _))
                { error = "Responses tool strict must be a boolean."; return false; }
                var function = new JsonObject
                {
                    ["name"] = name,
                    ["parameters"] = tool["parameters"]?.DeepClone() ?? new JsonObject()
                };
                string? description = ReadString(tool["description"]);
                if (!string.IsNullOrWhiteSpace(description)) function["description"] = description;
                if (tool["strict"] is JsonNode strict) function["strict"] = strict.DeepClone();
                chatTools.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }
            normalized["tools"] = chatTools;
        }

        if (input["tool_choice"] != null && input["tool_choice"] is not (JsonObject or JsonValue))
        { error = "tool_choice must be a string or named function object."; return false; }
        if (input["tool_choice"] is JsonNode toolChoice)
        {
            if (!TryNormalizeResponsesToolChoice(toolChoice, out JsonNode? choice, out error)) return false;
            if (choice != null) normalized["tool_choice"] = choice;
        }
        return true;
    }

    private static bool TryNormalizeAnthropicTools(JsonObject input, JsonObject normalized, out string? error)
    {
        error = null;
        if (input["tools"] != null && input["tools"] is not JsonArray)
        { error = "tools must be an array."; return false; }
        if (input["tools"] is JsonArray tools)
        {
            if (tools.Count > MaxTools) { error = "tools exceeds the maximum of " + MaxTools + "."; return false; }
            var chatTools = new JsonArray();
            foreach (JsonNode? node in tools)
            {
                if (node is not JsonObject tool) { error = "Each tool must be an object."; return false; }
                if (!ValidateSupportedFields(tool, "Anthropic tool", out error, "name", "input_schema", "description"))
                    return false;
                string? name = ReadString(tool["name"]);
                if (!ValidateToolName(name, out error)) return false;
                if (tool["input_schema"] != null && tool["input_schema"] is not JsonObject)
                { error = "Anthropic tool input_schema must be a JSON Schema object."; return false; }
                if (tool["description"] != null && ReadString(tool["description"]) == null)
                { error = "Anthropic tool description must be a string."; return false; }
                var function = new JsonObject
                {
                    ["name"] = name,
                    ["parameters"] = tool["input_schema"]?.DeepClone() ?? new JsonObject()
                };
                string? description = ReadString(tool["description"]);
                if (!string.IsNullOrWhiteSpace(description)) function["description"] = description;
                chatTools.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }
            normalized["tools"] = chatTools;
        }

        if (input["tool_choice"] != null && input["tool_choice"] is not JsonObject)
        { error = "Anthropic tool_choice must be an object."; return false; }
        if (input["tool_choice"] is JsonObject toolChoice)
        {
            if (!ValidateSupportedFields(toolChoice, "Anthropic tool_choice", out error,
                    "type", "name", "disable_parallel_tool_use")) return false;
            if (toolChoice["type"] != null && ReadString(toolChoice["type"]) == null)
            { error = "Anthropic tool_choice type must be a string."; return false; }
            if (toolChoice["name"] != null && ReadString(toolChoice["name"]) == null)
            { error = "Anthropic tool_choice name must be a string."; return false; }
            string type = ReadString(toolChoice["type"]) ?? "auto";
            if (type != "tool" && toolChoice["name"] != null)
            { error = "Anthropic tool_choice name is valid only when type is tool."; return false; }
            switch (type)
            {
                case "auto":
                    normalized["tool_choice"] = "auto";
                    break;
                case "any":
                    normalized["tool_choice"] = "required";
                    break;
                case "none":
                    normalized["tool_choice"] = "none";
                    break;
                case "tool":
                    string? name = ReadString(toolChoice["name"]);
                    if (!ValidateToolName(name, out error)) return false;
                    normalized["tool_choice"] = new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = name }
                    };
                    break;
                default:
                    error = "Unsupported Anthropic tool_choice type: " + type + ".";
                    return false;
            }
            if (toolChoice["disable_parallel_tool_use"] != null)
            {
                if (!TryReadBoolean(toolChoice["disable_parallel_tool_use"], out bool disableParallel))
                { error = "tool_choice disable_parallel_tool_use must be a boolean."; return false; }
                normalized["parallel_tool_calls"] = !disableParallel;
            }
        }
        return true;
    }

    private static bool TryNormalizeResponsesToolChoice(
        JsonNode node, out JsonNode? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (node is JsonValue value)
        {
            string? choice = null;
            try { choice = value.GetValue<string>(); } catch { }
            if (choice is "auto" or "required" or "none")
            {
                normalized = choice;
                return true;
            }
            error = "tool_choice must be auto, required, none, or a named function.";
            return false;
        }
        if (node is JsonObject obj && ReadString(obj["type"]) == "function")
        {
            if (!ValidateSupportedFields(obj, "Responses tool_choice", out error, "type", "name")) return false;
            string? name = ReadString(obj["name"]);
            if (!ValidateToolName(name, out error)) return false;
            normalized = new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name }
            };
            return true;
        }
        error = "Unsupported Responses tool_choice shape.";
        return false;
    }

    private static bool TryNormalizeOpenAiContent(
        JsonNode? source,
        bool allowImages,
        out JsonNode? normalized,
        out string? error)
    {
        normalized = null;
        error = null;
        if (source == null) return true;
        if (source is JsonValue)
        {
            string? text = ExtractContentText(source);
            if (text == null)
            {
                error = "content must be a text string or an array of content blocks.";
                return false;
            }
            if (text != null) normalized = text;
            return true;
        }
        if (source is JsonObject obj)
        {
            source = new JsonArray(obj.DeepClone());
        }
        if (source is not JsonArray blocks)
        {
            error = "content must be text or an array of content blocks.";
            return false;
        }
        if (blocks.Count > MaxContentBlocks) { error = "content contains too many blocks."; return false; }

        var result = new JsonArray();
        foreach (JsonNode? node in blocks)
        {
            if (node is JsonValue textValue)
            {
                string? text = ExtractContentText(textValue);
                if (text == null) { error = "content block values must be strings or objects."; return false; }
                if (!string.IsNullOrEmpty(text))
                    result.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                continue;
            }
            if (node is not JsonObject block) { error = "content blocks must be strings or objects."; return false; }
            if (block["type"] != null && ReadString(block["type"]) == null)
            { error = "content block type must be a string."; return false; }
            string type = ReadString(block["type"]) ?? "text";
            if (type is "text" or "input_text" or "output_text")
            {
                if (!ValidateSupportedFields(block, "text content block", out error, "type", "text", "content"))
                    return false;
                if (block.ContainsKey("text") && block.ContainsKey("content"))
                { error = "text content block cannot supply both text and content."; return false; }
                if (block["text"] != null && ReadString(block["text"]) == null)
                { error = "text content block text must be a string."; return false; }
                if (block["text"] == null && block["content"] == null)
                { error = "text content block requires text or content."; return false; }
                if (block["content"] != null && !ValidateTextContent(block["content"], "content block content", out error))
                    return false;
                string? text = ReadString(block["text"]) ?? ExtractContentText(block["content"]);
                if (!string.IsNullOrEmpty(text))
                    result.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                continue;
            }
            if (type is "input_image" or "image_url")
            {
                if (!ValidateSupportedFields(block, "image content block", out error, "type", "image_url", "url", "detail"))
                    return false;
                if (block["image_url"] is JsonObject imageWrapper
                    && !ValidateSupportedFields(imageWrapper, "image_url", out error, "url", "detail")) return false;
                if (block["image_url"] != null && block["url"] != null)
                { error = "image content block cannot supply both image_url and url."; return false; }
                if (block["detail"] != null && block["image_url"] is JsonObject wrapperWithDetail
                    && wrapperWithDetail["detail"] != null)
                { error = "image detail cannot be supplied both on the block and inside image_url."; return false; }
                JsonNode? detailNode = block["detail"] ?? (block["image_url"] as JsonObject)?["detail"];
                if (detailNode != null && ReadString(detailNode) == null)
                { error = "image detail must be a string."; return false; }
                if (!allowImages) { error = "Images are only supported in user message content."; return false; }
                string? url = ReadImageReference(block);
                if (!ValidateImageReference(url, out error)) return false;
                string? detail = ReadString(detailNode);
                if (detail != null && detail is not ("auto" or "low" or "high"))
                { error = "image detail must be auto, low, or high."; return false; }
                var imageReference = new JsonObject { ["url"] = url };
                if (detail != null) imageReference["detail"] = detail;
                var imageContent = new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = imageReference
                };
                result.Add(imageContent);
                continue;
            }
            error = "Unsupported content block type: " + type + ".";
            return false;
        }

        if (result.Count == 0) return true;
        normalized = result.Count == 1 && ReadString(result[0]?["type"]) == "text"
            ? result[0]?["text"]?.DeepClone()
            : result;
        return true;
    }

    private static bool TryNormalizeAnthropicToolResultContent(
        JsonNode? source, out JsonNode? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (source == null) { normalized = ""; return true; }
        if (source is JsonValue)
        {
            string? text = ExtractContentText(source);
            if (text == null) { error = "tool_result content must be text or an array."; return false; }
            normalized = text;
            return true;
        }
        if (source is not JsonArray blocks)
        {
            error = "tool_result content must be text or an array.";
            return false;
        }
        if (blocks.Count > MaxContentBlocks) { error = "tool_result contains too many blocks."; return false; }

        var result = new JsonArray();
        foreach (JsonNode? node in blocks)
        {
            if (node is not JsonObject block) { error = "tool_result content blocks must be objects."; return false; }
            string? typeValue = ReadString(block["type"]);
            if (typeValue == null) { error = "tool_result content block type is required and must be a string."; return false; }
            string type = typeValue;
            if (type == "text")
            {
                if (!ValidateSupportedFields(block, "Anthropic tool-result text block", out error, "type", "text"))
                    return false;
                string? text = ReadString(block["text"]);
                if (!block.ContainsKey("text") || text == null) { error = "tool-result text block requires a string text field."; return false; }
                if (!string.IsNullOrEmpty(text))
                    result.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            }
            else if (type == "image")
            {
                if (!ValidateSupportedFields(block, "Anthropic tool-result image block", out error, "type", "source"))
                    return false;
                if (!TryNormalizeAnthropicImage(block, out JsonObject? image, out error)) return false;
                if (image != null) result.Add(image);
            }
            else { error = "Unsupported Anthropic tool-result content block type: " + type + "."; return false; }
        }
        normalized = result.Count == 1 && ReadString(result[0]?["type"]) == "text"
            ? result[0]?["text"]?.DeepClone()
            : result;
        return true;
    }

    private static bool TryNormalizeAnthropicImage(
        JsonObject block, out JsonObject? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (block["source"] is not JsonObject source)
        {
            error = "Anthropic image block requires source.";
            return false;
        }
        string sourceType = ReadString(source["type"]) ?? "";
        string? reference;
        if (sourceType == "base64")
        {
            if (!ValidateSupportedFields(source, "Anthropic base64 image source", out error, "type", "media_type", "data"))
                return false;
            string? mediaType = ReadString(source["media_type"]);
            string? data = ReadString(source["data"]);
            if (!IsSupportedImageMediaType(mediaType) || string.IsNullOrWhiteSpace(data))
            {
                error = "Only base64 PNG, JPEG, WEBP or GIF images are supported.";
                return false;
            }
            reference = "data:" + mediaType + ";base64," + data;
        }
        else if (sourceType == "url")
        {
            if (!ValidateSupportedFields(source, "Anthropic URL image source", out error, "type", "url"))
                return false;
            reference = ReadString(source["url"]);
        }
        else
        {
            error = "Unsupported Anthropic image source type: " + sourceType + ".";
            return false;
        }

        if (!ValidateImageReference(reference, out error)) return false;
        normalized = new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = reference }
        };
        return true;
    }

    private static string? ReadImageReference(JsonObject block)
    {
        if (block["image_url"] is JsonValue direct) return ReadString(direct);
        if (block["image_url"] is JsonObject wrapper) return ReadString(wrapper["url"]);
        return ReadString(block["url"]);
    }

    private static bool ValidateImageReference(string? reference, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(reference))
        {
            error = "Image content requires a non-empty image URL or data URI.";
            return false;
        }
        if (reference.Length > MaxImageReferenceCharacters)
        {
            error = "Image reference exceeds the bounded compatibility limit.";
            return false;
        }
        if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int separator = reference.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (separator <= "data:".Length)
            {
                error = "Image data URI must use base64 encoding.";
                return false;
            }
            string mediaType = reference["data:".Length..separator];
            if (!IsSupportedImageMediaType(mediaType))
            {
                error = "Only PNG, JPEG, WEBP or GIF data images are supported.";
                return false;
            }
            return true;
        }
        if (!Uri.TryCreate(reference, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Image references may use only data:, http: or https:; local file paths are never accepted.";
            return false;
        }
        return true;
    }

    private static bool IsSupportedImageMediaType(string? mediaType) =>
        mediaType is "image/png" or "image/jpeg" or "image/webp" or "image/gif";

    private static bool ValidateToolName(string? name, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxToolNameCharacters)
        {
            error = "Tool name is required and must be at most " + MaxToolNameCharacters + " characters.";
            return false;
        }
        if (name.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')))
        {
            error = "Tool names may contain only letters, digits, '_', '-' and '.'.";
            return false;
        }
        return true;
    }

    private static bool ValidateToolCall(
        string? id, string? name, string arguments, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256)
        {
            error = "Tool call id is required and must be at most 256 characters.";
            return false;
        }
        if (!ValidateToolName(name, out error)) return false;
        if (arguments.Length > MaxToolArgumentsCharacters)
        {
            error = "Tool arguments exceed the bounded compatibility limit.";
            return false;
        }
        return true;
    }

    private static JsonObject BuildChatToolCall(string id, string name, string arguments) => new()
    {
        ["id"] = id,
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments }
    };

    private static JsonObject BuildAssistantToolCallMessage(string id, string name, string arguments) => new()
    {
        ["role"] = "assistant",
        ["content"] = null,
        ["tool_calls"] = new JsonArray(BuildChatToolCall(id, name, arguments))
    };

    private static void AddCompatibilityProjection(List<CompatMessage> destination, JsonObject message)
    {
        string? role = ReadString(message["role"]);
        string? text = ExtractContentText(message["content"]);
        if (!string.IsNullOrWhiteSpace(role) && !string.IsNullOrWhiteSpace(text))
            destination.Add(new CompatMessage(role, text));
    }

    private static string? ExtractContentText(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue value)
        {
            try { return value.GetValue<string>(); } catch { return null; }
        }
        if (node is JsonObject obj)
            return ExtractContentText(obj["text"] ?? obj["content"]);
        if (node is JsonArray array)
        {
            var parts = new List<string>();
            foreach (JsonNode? part in array)
            {
                if (part is JsonObject block)
                {
                    string? type = ReadString(block["type"]);
                    if (type != null && type is not ("text" or "input_text" or "output_text")) continue;
                }
                string? text = ExtractContentText(part);
                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
            }
            return parts.Count == 0 ? null : string.Join("\n", parts);
        }
        return null;
    }

    private static ChatAssistantOutput ExtractChatAnswer(string body)
    {
        try
        {
            JsonNode? parsed = JsonNode.Parse(body);
            JsonObject? message = parsed?["choices"]?[0]?["message"] as JsonObject;
            JsonNode? content = message?["content"]?.DeepClone();
            string text = ExtractContentText(content) ?? "";
            JsonArray toolCalls = message?["tool_calls"] is JsonArray calls
                ? (JsonArray)calls.DeepClone()
                : new JsonArray();
            return new ChatAssistantOutput(text, content, toolCalls, parsed?["usage"]?.DeepClone(),
                ReadString(parsed?["choices"]?[0]?["finish_reason"]));
        }
        catch { return new ChatAssistantOutput("", null, new JsonArray(), null); }
    }

    private static JsonObject BuildChatAssistantMessage(ChatAssistantOutput output)
    {
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = output.Content?.DeepClone() ?? output.Text
        };
        // Same filter as the client-visible payloads: state must not claim calls the client never saw.
        var visible = new JsonArray();
        foreach (JsonNode? node in output.ToolCalls)
        {
            if (node is not JsonObject call) continue;
            if (ValidateToolCall(ReadString(call["id"]), ReadString(call["function"]?["name"]),
                    ReadString(call["function"]?["arguments"]) ?? "{}", out _))
                visible.Add(call.DeepClone());
        }
        if (visible.Count > 0) message["tool_calls"] = visible;
        return message;
    }

    private static JsonObject BuildResponsesPayload(string id, string model, ChatAssistantOutput answer)
    {
        var output = new JsonArray();
        if (!string.IsNullOrEmpty(answer.Text) || answer.ToolCalls.Count == 0)
        {
            output.Add(new JsonObject
            {
                ["id"] = "msg_" + Guid.NewGuid().ToString("N"),
                ["type"] = "message",
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = string.IsNullOrEmpty(answer.Text)
                    ? new JsonArray()
                    : new JsonArray(new JsonObject
                    {
                        ["type"] = "output_text", ["text"] = answer.Text, ["annotations"] = new JsonArray()
                    })
            });
        }

        foreach (JsonNode? node in answer.ToolCalls)
        {
            if (node is not JsonObject call) continue;
            string? callId = ReadString(call["id"]);
            string? name = ReadString(call["function"]?["name"]);
            string arguments = ReadString(call["function"]?["arguments"]) ?? "{}";
            if (!ValidateToolCall(callId, name, arguments, out _)) continue;
            output.Add(new JsonObject
            {
                ["id"] = "fc_" + Guid.NewGuid().ToString("N"),
                ["type"] = "function_call",
                ["status"] = "completed",
                ["call_id"] = callId,
                ["name"] = name,
                ["arguments"] = arguments
            });
        }

        var response = new JsonObject
        {
            ["id"] = id,
            ["object"] = "response",
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["status"] = answer.FinishReason == "length" ? "incomplete" : "completed",
            ["model"] = model,
            ["output"] = output,
            ["output_text"] = answer.Text,
            ["error"] = null
        };
        // Responses usage uses input/output/total_tokens (with details objects); copying the chat
        // shape (prompt_tokens/completion_tokens) made Codex fail to parse response.completed and
        // retry every turn (live smoke).
        response["usage"] = BuildResponsesUsage(answer.Usage);
        if (answer.FinishReason == "length")
            response["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" };
        return response;
    }

    internal static JsonObject BuildResponsesUsage(JsonNode? chatUsage)
    {
        int Read(string name) => chatUsage is JsonObject usage && usage[name] is JsonValue value && value.TryGetValue(out int number) ? number : 0;
        int input = Read("prompt_tokens");
        int output = Read("completion_tokens");
        int cached = chatUsage is JsonObject u && u["prompt_tokens_details"] is JsonObject details
            && details["cached_tokens"] is JsonValue cachedValue && cachedValue.TryGetValue(out int c) ? c : 0;
        return new JsonObject
        {
            ["input_tokens"] = input,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = cached },
            ["output_tokens"] = output,
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
            ["total_tokens"] = Read("total_tokens") is int total && total > 0 ? total : input + output
        };
    }

    private static JsonObject BuildAnthropicPayload(string id, string model, ChatAssistantOutput answer)
    {
        var content = new JsonArray();
        if (!string.IsNullOrEmpty(answer.Text))
            content.Add(new JsonObject { ["type"] = "text", ["text"] = answer.Text });

        foreach (JsonNode? node in answer.ToolCalls)
        {
            if (node is not JsonObject call) continue;
            string? callId = ReadString(call["id"]);
            string? name = ReadString(call["function"]?["name"]);
            string arguments = ReadString(call["function"]?["arguments"]) ?? "{}";
            if (!ValidateToolCall(callId, name, arguments, out _)) continue;
            JsonNode input;
            try { input = JsonNode.Parse(arguments) as JsonObject ?? new JsonObject(); }
            catch { input = new JsonObject(); }
            content.Add(new JsonObject
            {
                ["type"] = "tool_use", ["id"] = callId, ["name"] = name, ["input"] = input
            });
        }
        if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = "" });

        return new JsonObject
        {
            ["id"] = id,
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = content,
            ["stop_reason"] = answer.ToolCalls.Count > 0 ? "tool_use" : answer.FinishReason == "length" ? "max_tokens" : "end_turn",
            ["stop_sequence"] = null,
            ["usage"] = BuildAnthropicUsage(answer.Usage)
        };
    }

    internal static JsonObject TranslateChatAnswerToResponsesForTest(string body)
    {
        ChatAssistantOutput answer = ExtractChatAnswer(body);
        return BuildResponsesPayload("resp_test", "parux-main", answer);
    }

    internal static JsonObject TranslateChatAnswerToAnthropicForTest(string body)
    {
        ChatAssistantOutput answer = ExtractChatAnswer(body);
        return BuildAnthropicPayload("msg_test", "parux-main", answer);
    }

    private static JsonObject BuildAnthropicUsage(JsonNode? usage, bool includeInput = true, bool includeOutput = true)
    {
        int input = ReadInt(usage?["prompt_tokens"]);
        int output = ReadInt(usage?["completion_tokens"]);
        var result = new JsonObject();
        if (includeInput) result["input_tokens"] = input;
        if (includeOutput) result["output_tokens"] = output;
        return result;
    }

    private static void SaveResponseState(string id, string model, JsonArray messages)
    {
        var snapshot = new List<JsonObject>();
        foreach (JsonNode? node in messages.TakeLast(MaxMessagesPerState))
            if (node is JsonObject message) snapshot.Add((JsonObject)message.DeepClone());
        ResponseStates[id] = new ResponseState(model, snapshot, DateTime.UtcNow);
        ResponseOrder.Enqueue(id);
        while (ResponseStates.Count > MaxResponseStates && ResponseOrder.TryDequeue(out string? old))
            ResponseStates.TryRemove(old, out _);
    }

    /// <summary>
    /// Fields real agent clients send on every request that carry no generation semantics PaRuX
    /// must honour (prompt-cache hints, telemetry/metadata, server-side storage, streaming usage
    /// options, provider betas, reasoning display). Claude Code sends cache_control, metadata,
    /// thinking and top_k; Codex sends store, include, prompt_cache_key, reasoning and text.
    /// Rejecting them made the adapters unusable by the very clients they exist for.
    /// </summary>
    private static readonly HashSet<string> IgnoredClientFields = new(StringComparer.Ordinal)
    {
        "cache_control", "metadata", "store", "include", "prompt_cache_key", "service_tier", "user",
        "safety_identifier", "stream_options", "top_k", "thinking", "reasoning", "text", "truncation",
        "context_management", "betas", "citations", "annotations", "logprobs", "top_logprobs", "status",
        "id", "encrypted_content", "summary", "strict", "client_metadata", "background", "container",
        "mcp_servers", "prompt", "conversation", "output_config", "container_upload"
    };

    private static bool ValidateSupportedFields(JsonObject value, string scope, out string? error, params string[] supportedFields)
    {
        var supported = new HashSet<string>(supportedFields, StringComparer.Ordinal);
        foreach ((string name, JsonNode? child) in value)
        {
            if (child != null && !supported.Contains(name) && !IgnoredClientFields.Contains(name))
            {
                error = scope + " field '" + name + "' is not supported.";
                return false;
            }
        }
        error = null;
        return true;
    }

    private static bool ValidateTextContent(JsonNode? node, string scope, out string? error)
    {
        error = null;
        if (node is JsonValue)
        {
            if (ReadString(node) != null) return true;
            error = scope + " must contain text strings only.";
            return false;
        }
        if (node is JsonArray array)
        {
            foreach (JsonNode? part in array)
            {
                if (!ValidateTextContent(part, scope, out error)) return false;
            }
            return true;
        }
        if (node is JsonObject block)
        {
            if (!ValidateSupportedFields(block, scope + " text block", out error, "type", "text", "content"))
                return false;
            if (block["type"] != null)
            {
                string? type = ReadString(block["type"]);
                if (type is not ("text" or "input_text" or "output_text"))
                { error = scope + " contains an unsupported text block type."; return false; }
            }
            if (block["text"] != null && block["content"] != null)
            { error = scope + " text block cannot supply both text and content."; return false; }
            JsonNode? payload = block["text"] ?? block["content"];
            if (payload == null) { error = scope + " text blocks require text."; return false; }
            return ValidateTextContent(payload, scope, out error);
        }
        error = scope + " must be a string or text-block array.";
        return false;
    }

    private static bool ValidateSamplingValue(JsonObject source, string name, double minimum, double maximum, out string? error)
    {
        error = null;
        if (source[name] == null) return true;
        if (!TryReadNumber(source[name], out double value) || !double.IsFinite(value) || value < minimum || value > maximum)
        {
            error = name + " must be a number between " + minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " and " + maximum.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
            return false;
        }
        return true;
    }

    private static bool TryReadNumber(JsonNode? node, out double result)
    {
        result = 0;
        return node is JsonValue value && value.TryGetValue(out result);
    }

    private static bool TryReadBoolean(JsonNode? node, out bool result)
    {
        result = false;
        return node is JsonValue value && value.TryGetValue(out result);
    }

    private static bool TryReadPositiveInteger(JsonNode? node, out int result)
    {
        result = 0;
        return node is JsonValue value && value.TryGetValue(out result) && result > 0;
    }

    private static bool AreStringArray(JsonNode? node)
    {
        if (node is not JsonArray values) return false;
        return values.All(value => value != null && ReadString(value) != null);
    }

    private static void CopyGenerationOption(JsonObject source, JsonObject destination, string name)
    {
        if (source[name] is JsonNode value) destination[name] = value.DeepClone();
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        try { return value.GetValue<string>(); } catch { return null; }
    }

    private static bool ReadBool(JsonNode? node)
    {
        if (node is not JsonValue value) return false;
        try { return value.GetValue<bool>(); } catch { return false; }
    }

    private static int ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        try { return value.GetValue<int>(); } catch { return 0; }
    }

    private static string? ExtractErrorMessage(string body)
    {
        try
        {
            JsonNode? parsed = JsonNode.Parse(body);
            return ReadString(parsed?["error"]?["message"]) ?? ReadString(parsed?["error"]);
        }
        catch { return string.IsNullOrWhiteSpace(body) ? null : body; }
    }

    private static Task<string?> ReadBodyAsync(HttpRequest request, Func<int, string, Task> writeTooLarge, CancellationToken ct) =>
        CompatRequestBody.ReadAsync(request, writeTooLarge, ct);

    private static async Task WriteProtocolErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        await response.WriteAsync(new JsonObject
        {
            ["error"] = new JsonObject { ["message"] = message, ["type"] = "invalid_request_error" }
        }.ToJsonString(), ct).ConfigureAwait(false);
    }

    private static async Task WriteAnthropicErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        await response.WriteAsync(new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = "invalid_request_error", ["message"] = message }
        }.ToJsonString(), ct).ConfigureAwait(false);
    }

    private static async Task WriteSseEventAsync(HttpResponse response, string eventName, JsonObject payload, CancellationToken ct)
    {
        await response.WriteAsync("event: " + eventName + "\n", ct).ConfigureAwait(false);
        await response.WriteAsync("data: " + payload.ToJsonString() + "\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
