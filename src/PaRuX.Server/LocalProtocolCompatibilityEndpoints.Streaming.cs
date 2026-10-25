using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>
/// True streaming for the Anthropic Messages and OpenAI Responses adapters. Both are encoded from
/// the one internal stream (LocalApiServerService.StreamChunksAsync): text reaches the client token
/// by token as the local model produces it. Each tool call is emitted as soon as its arguments are
/// complete (the next call starts or the model finishes), so it can still be validated the same
/// way as a buffered answer before any client acts on it. While nothing is produced (prompt
/// prefill of a long agent context) the channel sends protocol keep-alives so clients do not
/// treat a busy local model as a dead connection.
/// </summary>
public static partial class LocalProtocolCompatibilityEndpoints
{
    internal static readonly TimeSpan StreamKeepAliveInterval = TimeSpan.FromSeconds(10);

    /// <summary>Destination for encoded SSE events (the HTTP response, or a list in tests).</summary>
    internal interface IStreamSink
    {
        Task EventAsync(string eventName, JsonObject payload);
    }

    /// <summary>Serialises writes to the HTTP response and sends a keep-alive when the stream is idle.</summary>
    internal sealed class SseChannel : IStreamSink, IAsyncDisposable
    {
        private readonly Func<string, CancellationToken, Task> _write;
        private readonly string _keepAlive;
        private readonly TimeSpan _interval;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _ct;
        private readonly Task _loop;
        private DateTime _lastWriteUtc = DateTime.UtcNow;

        public SseChannel(Func<string, CancellationToken, Task> write, string keepAlive, TimeSpan interval, CancellationToken ct)
        {
            _write = write;
            _keepAlive = keepAlive;
            _interval = interval;
            _ct = ct;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _loop = KeepAliveLoopAsync();
        }

        public static SseChannel ForResponse(HttpResponse response, string keepAlive, CancellationToken ct) =>
            new(async (text, token) =>
            {
                await response.WriteAsync(text, token).ConfigureAwait(false);
                await response.Body.FlushAsync(token).ConfigureAwait(false);
            }, keepAlive, StreamKeepAliveInterval, ct);

        public Task EventAsync(string eventName, JsonObject payload) =>
            WriteAsync("event: " + eventName + "\ndata: " + payload.ToJsonString() + "\n\n");

        private async Task WriteAsync(string text)
        {
            await _lock.WaitAsync(_ct).ConfigureAwait(false);
            try
            {
                await _write(text, _ct).ConfigureAwait(false);
                _lastWriteUtc = DateTime.UtcNow;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task KeepAliveLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, _interval.TotalMilliseconds / 4)), _stop.Token).ConfigureAwait(false);
                    if (DateTime.UtcNow - _lastWriteUtc >= _interval) await WriteAsync(_keepAlive).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Stream finished, client gone or request aborted: nothing left to keep alive. A
                // keep-alive failure must never mask the request's real outcome in DisposeAsync.
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _loop.ConfigureAwait(false);
            _stop.Dispose();
        }
    }

    internal sealed record CompletedToolCall(string Id, string Name, string Arguments);

    /// <summary>Rebuilds the assistant turn from stream deltas and decides when a tool call is complete.</summary>
    internal sealed class ChatStreamAccumulator
    {
        private sealed class ToolBuffer
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public readonly StringBuilder Arguments = new();
            public bool Emitted;
        }

        private readonly SortedDictionary<int, ToolBuffer> _tools = new();
        private readonly StringBuilder _text = new();
        private int? _openTool;

        public string? FinishReason { get; private set; }
        public JsonNode? Usage { get; private set; }
        public string Text => _text.ToString();

        /// <summary>Returns tool calls that became complete with this chunk; <paramref name="text"/> is its visible text delta.</summary>
        public List<CompletedToolCall> Add(LocalApiServerService.ChatStreamChunk chunk, out string? text)
        {
            var completed = new List<CompletedToolCall>();
            foreach (LocalApiServerService.ChatStreamToolDelta delta in chunk.Tools)
            {
                if (_openTool is int open && open != delta.Index) completed.AddRange(Flush(open));
                if (!_tools.TryGetValue(delta.Index, out ToolBuffer? buffer))
                {
                    buffer = new ToolBuffer();
                    _tools[delta.Index] = buffer;
                }
                if (!string.IsNullOrEmpty(delta.Id)) buffer.Id = delta.Id;
                if (!string.IsNullOrEmpty(delta.Name)) buffer.Name += delta.Name;
                if (delta.Arguments != null) buffer.Arguments.Append(delta.Arguments);
                _openTool = delta.Index;
            }
            text = string.IsNullOrEmpty(chunk.Text) ? null : chunk.Text;
            if (text != null) _text.Append(text);
            if (chunk.FinishReason != null) FinishReason = chunk.FinishReason;
            if (chunk.Usage != null) Usage = chunk.Usage;
            return completed;
        }

        /// <summary>The model finished: every tool call not yet emitted is complete.</summary>
        public List<CompletedToolCall> Complete()
        {
            var completed = new List<CompletedToolCall>();
            foreach (int index in _tools.Keys.ToList()) completed.AddRange(Flush(index));
            _openTool = null;
            return completed;
        }

        private IEnumerable<CompletedToolCall> Flush(int index)
        {
            if (!_tools.TryGetValue(index, out ToolBuffer? buffer) || buffer.Emitted) yield break;
            buffer.Emitted = true;
            if (buffer.Id.Length == 0) buffer.Id = "call_" + Guid.NewGuid().ToString("N");
            string arguments = buffer.Arguments.Length == 0 ? "{}" : buffer.Arguments.ToString();
            yield return new CompletedToolCall(buffer.Id, buffer.Name, arguments);
        }

        /// <summary>The whole assistant turn in chat form (Responses continuation state).</summary>
        public JsonObject ToChatMessage()
        {
            var message = new JsonObject { ["role"] = "assistant", ["content"] = Text };
            var calls = new JsonArray();
            foreach (ToolBuffer tool in _tools.Values.Where(t => t.Emitted))
            {
                string arguments = tool.Arguments.Length == 0 ? "{}" : tool.Arguments.ToString();
                // Only calls the client was actually shown belong in the continuation state; a call
                // the encoders dropped as invalid could never receive a matching tool result.
                if (ValidateToolCall(tool.Id, tool.Name, arguments, out _))
                    calls.Add(BuildChatToolCall(tool.Id, tool.Name, arguments));
            }
            if (calls.Count > 0) message["tool_calls"] = calls;
            return message;
        }
    }

    /// <summary>Anthropic Messages SSE: message_start, content blocks, message_delta, message_stop.</summary>
    internal sealed class AnthropicStreamEncoder
    {
        private readonly IStreamSink _sink;
        private readonly string _id;
        private readonly string _model;
        private int _nextBlock;
        private int? _openText;
        private int _tools;

        public AnthropicStreamEncoder(IStreamSink sink, string id, string model)
        {
            _sink = sink;
            _id = id;
            _model = model;
        }

        public Task StartAsync() => _sink.EventAsync("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = _id, ["type"] = "message", ["role"] = "assistant", ["model"] = _model,
                ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 }
            }
        });

        public async Task TextAsync(string text)
        {
            if (_openText == null)
            {
                _openText = _nextBlock++;
                await _sink.EventAsync("content_block_start", new JsonObject
                {
                    ["type"] = "content_block_start", ["index"] = _openText,
                    ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" }
                }).ConfigureAwait(false);
            }
            if (text.Length == 0) return;
            await _sink.EventAsync("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = _openText,
                ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text }
            }).ConfigureAwait(false);
        }

        public async Task ToolAsync(CompletedToolCall call)
        {
            if (!ValidateToolCall(call.Id, call.Name, call.Arguments, out _)) return;
            await CloseTextAsync().ConfigureAwait(false);
            JsonNode input;
            try { input = JsonNode.Parse(call.Arguments) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { input = new JsonObject(); }
            int index = _nextBlock++;
            _tools++;
            await _sink.EventAsync("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = index,
                ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = new JsonObject() }
            }).ConfigureAwait(false);
            await _sink.EventAsync("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = index,
                ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = input.ToJsonString() }
            }).ConfigureAwait(false);
            await _sink.EventAsync("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index }).ConfigureAwait(false);
        }

        /// <summary>Terminal error event for a provider failure after bytes were written (the HTTP status can no longer change).</summary>
        public Task FailAsync(string message) => _sink.EventAsync("error", new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = message }
        });

        public async Task FinishAsync(string? finishReason, JsonNode? usage)
        {
            // Same shape as a buffered answer: an empty turn is one empty text block.
            if (_nextBlock == 0) await TextAsync(string.Empty).ConfigureAwait(false);
            await CloseTextAsync().ConfigureAwait(false);
            string stopReason = _tools > 0 ? "tool_use" : finishReason == "length" ? "max_tokens" : "end_turn";
            await _sink.EventAsync("message_delta", new JsonObject
            {
                ["type"] = "message_delta",
                ["delta"] = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null },
                ["usage"] = BuildAnthropicUsage(usage)
            }).ConfigureAwait(false);
            await _sink.EventAsync("message_stop", new JsonObject { ["type"] = "message_stop" }).ConfigureAwait(false);
        }

        private async Task CloseTextAsync()
        {
            if (_openText is not int index) return;
            _openText = null;
            await _sink.EventAsync("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index }).ConfigureAwait(false);
        }
    }

    /// <summary>OpenAI Responses SSE: response.created … output items … response.completed.</summary>
    internal sealed class ResponsesStreamEncoder
    {
        private readonly IStreamSink _sink;
        private readonly string _id;
        private readonly string _model;
        private readonly long _createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private readonly JsonArray _items = new();
        private string? _messageId;
        private readonly StringBuilder _messageText = new();
        private readonly StringBuilder _allText = new();

        public ResponsesStreamEncoder(IStreamSink sink, string id, string model)
        {
            _sink = sink;
            _id = id;
            _model = model;
        }

        private JsonObject Envelope(string status, JsonNode? usage) => new()
        {
            ["id"] = _id, ["object"] = "response", ["created_at"] = _createdAt, ["status"] = status,
            ["model"] = _model, ["output"] = _items.DeepClone(), ["output_text"] = _allText.ToString(),
            ["error"] = null, ["usage"] = usage
        };

        public async Task StartAsync()
        {
            await _sink.EventAsync("response.created", new JsonObject { ["type"] = "response.created", ["response"] = Envelope("in_progress", null) }).ConfigureAwait(false);
            await _sink.EventAsync("response.in_progress", new JsonObject { ["type"] = "response.in_progress", ["response"] = Envelope("in_progress", null) }).ConfigureAwait(false);
        }

        public async Task TextAsync(string text)
        {
            if (_messageId == null)
            {
                _messageId = "msg_" + Guid.NewGuid().ToString("N");
                await _sink.EventAsync("response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added", ["output_index"] = _items.Count,
                    ["item"] = new JsonObject
                    {
                        ["id"] = _messageId, ["type"] = "message", ["status"] = "in_progress",
                        ["role"] = "assistant", ["content"] = new JsonArray()
                    }
                }).ConfigureAwait(false);
                await _sink.EventAsync("response.content_part.added", new JsonObject
                {
                    ["type"] = "response.content_part.added", ["item_id"] = _messageId, ["output_index"] = _items.Count,
                    ["content_index"] = 0, ["part"] = OutputText(string.Empty)
                }).ConfigureAwait(false);
            }
            if (text.Length == 0) return;
            _messageText.Append(text);
            _allText.Append(text);
            await _sink.EventAsync("response.output_text.delta", new JsonObject
            {
                ["type"] = "response.output_text.delta", ["item_id"] = _messageId, ["output_index"] = _items.Count,
                ["content_index"] = 0, ["delta"] = text
            }).ConfigureAwait(false);
        }

        public async Task ToolAsync(CompletedToolCall call)
        {
            if (!ValidateToolCall(call.Id, call.Name, call.Arguments, out _)) return;
            await CloseMessageAsync().ConfigureAwait(false);
            string itemId = "fc_" + Guid.NewGuid().ToString("N");
            int index = _items.Count;
            JsonObject item = new()
            {
                ["id"] = itemId, ["type"] = "function_call", ["status"] = "in_progress",
                ["call_id"] = call.Id, ["name"] = call.Name, ["arguments"] = string.Empty
            };
            await _sink.EventAsync("response.output_item.added", new JsonObject
            {
                ["type"] = "response.output_item.added", ["output_index"] = index, ["item"] = item.DeepClone()
            }).ConfigureAwait(false);
            await _sink.EventAsync("response.function_call_arguments.delta", new JsonObject
            {
                ["type"] = "response.function_call_arguments.delta", ["item_id"] = itemId, ["output_index"] = index, ["delta"] = call.Arguments
            }).ConfigureAwait(false);
            await _sink.EventAsync("response.function_call_arguments.done", new JsonObject
            {
                ["type"] = "response.function_call_arguments.done", ["item_id"] = itemId, ["output_index"] = index, ["arguments"] = call.Arguments
            }).ConfigureAwait(false);
            item["status"] = "completed";
            item["arguments"] = call.Arguments;
            _items.Add(item.DeepClone());
            await _sink.EventAsync("response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done", ["output_index"] = index, ["item"] = item
            }).ConfigureAwait(false);
        }

        /// <summary>Terminal response.failed for a provider failure after bytes were written; the turn is not completed.</summary>
        public async Task FailAsync(string message)
        {
            JsonObject failed = Envelope("failed", null);
            failed["error"] = new JsonObject { ["code"] = "server_error", ["message"] = message };
            await _sink.EventAsync("response.failed", new JsonObject { ["type"] = "response.failed", ["response"] = failed }).ConfigureAwait(false);
        }

        /// <summary>Closes the stream with response.completed and returns the final response object.</summary>
        public async Task<JsonObject> FinishAsync(JsonNode? chatUsage, string? finishReason = null)
        {
            // A buffered answer with neither text nor tool calls is one empty message; mirror it.
            if (_items.Count == 0 && _messageId == null) await TextAsync(string.Empty).ConfigureAwait(false);
            await CloseMessageAsync().ConfigureAwait(false);
            // A turn cut off by the token limit ends as response.incomplete, as the Responses API does,
            // instead of claiming a complete answer.
            bool truncated = finishReason == "length";
            JsonObject completed = Envelope(truncated ? "incomplete" : "completed", BuildResponsesUsage(chatUsage));
            if (truncated) completed["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" };
            string terminal = truncated ? "response.incomplete" : "response.completed";
            await _sink.EventAsync(terminal, new JsonObject { ["type"] = terminal, ["response"] = completed.DeepClone() }).ConfigureAwait(false);
            return completed;
        }

        private async Task CloseMessageAsync()
        {
            if (_messageId == null) return;
            string id = _messageId;
            _messageId = null;
            int index = _items.Count;
            string text = _messageText.ToString();
            _messageText.Clear();
            await _sink.EventAsync("response.output_text.done", new JsonObject
            {
                ["type"] = "response.output_text.done", ["item_id"] = id, ["output_index"] = index, ["content_index"] = 0, ["text"] = text
            }).ConfigureAwait(false);
            await _sink.EventAsync("response.content_part.done", new JsonObject
            {
                ["type"] = "response.content_part.done", ["item_id"] = id, ["output_index"] = index, ["content_index"] = 0, ["part"] = OutputText(text)
            }).ConfigureAwait(false);
            JsonObject item = new()
            {
                ["id"] = id, ["type"] = "message", ["status"] = "completed", ["role"] = "assistant",
                ["content"] = text.Length == 0 ? new JsonArray() : new JsonArray(OutputText(text))
            };
            _items.Add(item.DeepClone());
            await _sink.EventAsync("response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done", ["output_index"] = index, ["item"] = item
            }).ConfigureAwait(false);
        }

        private static JsonObject OutputText(string text) => new()
        {
            ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray()
        };
    }

    /// <summary>
    /// Drives one streamed gateway request: provider chunks → accumulator → protocol encoder.
    /// Returns false (nothing written yet) when the provider refused the request, so the caller
    /// can still answer with an honest error status.
    /// </summary>
    private static async Task<(bool Streamed, LocalApiCompletionResult? Error, ChatStreamAccumulator Turn, JsonObject? Final)> RunStreamAsync(
        LocalApiServerService localApi, JsonObject normalized, HttpResponse response, string keepAlive,
        Func<IStreamSink, (Func<Task> Start, Func<string, Task> Text, Func<CompletedToolCall, Task> Tool, Func<ChatStreamAccumulator, Task<JsonObject?>> Finish, Func<string, Task> Fail)> encoder,
        CancellationToken ct)
    {
        var turn = new ChatStreamAccumulator();
        SseChannel? channel = null;
        (Func<Task> Start, Func<string, Task> Text, Func<CompletedToolCall, Task> Tool, Func<ChatStreamAccumulator, Task<JsonObject?>> Finish, Func<string, Task> Fail) steps = default;
        normalized["stream"] = true;
        try
        {
            LocalApiCompletionResult result = await localApi.StreamChunksAsync(normalized.ToJsonString(),
                async () =>
                {
                    response.StatusCode = StatusCodes.Status200OK;
                    response.ContentType = "text/event-stream; charset=utf-8";
                    response.Headers.CacheControl = "no-cache";
                    response.Headers["X-Accel-Buffering"] = "no";
                    channel = SseChannel.ForResponse(response, keepAlive, ct);
                    steps = encoder(channel);
                    await steps.Start().ConfigureAwait(false);
                },
                async chunk =>
                {
                    List<CompletedToolCall> completed = turn.Add(chunk, out string? text);
                    foreach (CompletedToolCall call in completed) await steps.Tool(call).ConfigureAwait(false);
                    if (text != null) await steps.Text(text).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
            if (result.Outcome != LocalApiOutcome.Ok)
            {
                if (channel == null) return (false, result, turn, null);
                // Bytes are already on the wire: end the stream with the protocol's terminal error event,
                // never a clean-looking completion and never a JSON body appended to an SSE stream.
                await steps.Fail(ExtractErrorMessage(result.Body) ?? "PaRuX local completion failed.").ConfigureAwait(false);
                return (true, result, turn, null);
            }
            foreach (CompletedToolCall call in turn.Complete()) await steps.Tool(call).ConfigureAwait(false);
            JsonObject? final = await steps.Finish(turn).ConfigureAwait(false);
            return (true, null, turn, final);
        }
        finally
        {
            if (channel != null) await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Maps a failure during a gateway request to the right client-visible outcome.</summary>
    private static async Task HandleStreamFailureAsync(HttpResponse response, Exception exception, CancellationToken clientToken,
        Func<int, string, Task> writeError)
    {
        if (clientToken.IsCancellationRequested) return; // the client left; nothing to repair
        if (response.HasStarted)
        {
            // An HTTP status cannot change mid-stream; aborting tells the client the turn is incomplete.
            response.HttpContext.Abort();
            return;
        }
        if (exception is OperationCanceledException)
            await writeError(StatusCodes.Status409Conflict, "Gaming Mode activated; the AI request was stopped.").ConfigureAwait(false);
        else
            await writeError(StatusCodes.Status502BadGateway, "The local model stream ended unexpectedly: " + exception.Message).ConfigureAwait(false);
    }
}
