using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using static PaRuX.Server.LocalProtocolCompatibilityEndpoints;

namespace PaRuX.Server.Tests;

/// <summary>
/// True streaming for the gateway: provider chunks are parsed once and re-encoded as Anthropic
/// Messages and OpenAI Responses events as they arrive.
/// </summary>
public sealed class GatewayStreamingTests
{
    private static LocalApiServerService.ChatStreamChunk Chunk(string json) =>
        LocalApiServerService.ParseStreamChunk(json) ?? throw new InvalidOperationException("chunk ignored: " + json);

    private static readonly string[] Provider =
    {
        """{"choices":[{"delta":{"role":"assistant","content":null,"reasoning_content":"thinking"}}]}""",
        """{"choices":[{"delta":{"content":"I'll "}}]}""",
        """{"choices":[{"delta":{"content":"write it."}}]}""",
        """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"Write","arguments":""}}]}}]}""",
        """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":\"a.txt\","}}]}}]}""",
        """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"content\":\"OK\"}"}}]}}]}""",
        """{"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"name":"Read","arguments":"{\"path\":\"b\"}"}}]}}]}""",
        """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
        """{"choices":[],"usage":{"prompt_tokens":120,"completion_tokens":30,"total_tokens":150}}"""
    };

    private sealed class ListSink : IStreamSink
    {
        public List<(string Name, JsonObject Payload)> Events { get; } = new();
        public Task EventAsync(string eventName, JsonObject payload)
        {
            Events.Add((eventName, (JsonObject)payload.DeepClone()));
            return Task.CompletedTask;
        }
    }

    /// <summary>Feeds the provider script through the accumulator the way the endpoint does.</summary>
    private static async Task<ChatStreamAccumulator> Drive(Func<string, Task> text, Func<CompletedToolCall, Task> tool, List<string>? order = null)
    {
        var turn = new ChatStreamAccumulator();
        foreach (string line in Provider)
        {
            LocalApiServerService.ChatStreamChunk? chunk = LocalApiServerService.ParseStreamChunk(line);
            if (chunk == null) continue;
            List<CompletedToolCall> completed = turn.Add(chunk, out string? delta);
            foreach (CompletedToolCall call in completed) { order?.Add("tool:" + call.Name); await tool(call); }
            if (delta != null) { order?.Add("text"); await text(delta); }
        }
        foreach (CompletedToolCall call in turn.Complete()) { order?.Add("tool:" + call.Name); await tool(call); }
        return turn;
    }

    [Fact]
    public void Provider_chunks_parse_text_tool_deltas_finish_and_usage_and_drop_reasoning()
    {
        Assert.Null(LocalApiServerService.ParseStreamChunk(Provider[0]));                                // reasoning only: not client output
        Assert.Equal("I'll ", Chunk(Provider[1]).Text);
        LocalApiServerService.ChatStreamToolDelta start = Assert.Single(Chunk(Provider[3]).Tools);
        Assert.Equal((0, "call_a", "Write", ""), (start.Index, start.Id, start.Name, start.Arguments));
        Assert.Equal("tool_calls", Chunk(Provider[7]).FinishReason);
        Assert.Equal(150, Chunk(Provider[8]).Usage!["total_tokens"]!.GetValue<int>());
        Assert.Null(LocalApiServerService.ParseStreamChunk("not json"));
    }

    [Fact]
    public async Task Tool_calls_are_emitted_once_complete_and_text_streams_before_them()
    {
        var calls = new List<CompletedToolCall>();
        var order = new List<string>();
        var text = new StringBuilder();
        ChatStreamAccumulator turn = await Drive(t => { text.Append(t); return Task.CompletedTask; }, c => { calls.Add(c); return Task.CompletedTask; }, order);

        Assert.Equal(new[] { "text", "text", "tool:Write", "tool:Read" }, order);                         // Write completes when Read starts
        Assert.Equal("I'll write it.", text.ToString());
        Assert.Equal("{\"path\":\"a.txt\",\"content\":\"OK\"}", calls[0].Arguments);
        Assert.Equal("call_a", calls[0].Id);
        Assert.StartsWith("call_", calls[1].Id);                                                          // provider omitted an id
        Assert.Equal("tool_calls", turn.FinishReason);
        JsonObject message = turn.ToChatMessage();
        Assert.Equal(2, message["tool_calls"]!.AsArray().Count);
        Assert.Equal("I'll write it.", message["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anthropic_stream_is_a_valid_message_with_text_then_tool_use_blocks()
    {
        var sink = new ListSink();
        var encoder = new AnthropicStreamEncoder(sink, "msg_1", "parux-auto");
        await encoder.StartAsync();
        ChatStreamAccumulator turn = await Drive(encoder.TextAsync, encoder.ToolAsync);
        await encoder.FinishAsync(turn.FinishReason, turn.Usage);

        string[] names = sink.Events.Select(e => e.Name).ToArray();
        Assert.Equal(new[]
        {
            "message_start",
            "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop",
            "content_block_start", "content_block_delta", "content_block_stop",
            "content_block_start", "content_block_delta", "content_block_stop",
            "message_delta", "message_stop"
        }, names);
        Assert.Equal("tool_use", sink.Events[5].Payload["content_block"]!["type"]!.GetValue<string>());
        Assert.Equal(1, sink.Events[5].Payload["index"]!.GetValue<int>());
        JsonNode input = JsonNode.Parse(sink.Events[6].Payload["delta"]!["partial_json"]!.GetValue<string>())!;
        Assert.Equal("OK", input["content"]!.GetValue<string>());
        JsonObject delta = sink.Events[^2].Payload;
        Assert.Equal("tool_use", delta["delta"]!["stop_reason"]!.GetValue<string>());
        Assert.Equal(30, delta["usage"]!["output_tokens"]!.GetValue<int>());
        Assert.Equal(120, delta["usage"]!["input_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Anthropic_stream_reports_max_tokens_and_an_empty_turn_like_a_buffered_answer()
    {
        var sink = new ListSink();
        var encoder = new AnthropicStreamEncoder(sink, "msg_2", "parux-auto");
        await encoder.StartAsync();
        await encoder.FinishAsync("length", null);
        Assert.Equal(new[] { "message_start", "content_block_start", "content_block_stop", "message_delta", "message_stop" },
            sink.Events.Select(e => e.Name).ToArray());
        Assert.Equal("max_tokens", sink.Events[3].Payload["delta"]!["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Responses_stream_items_match_the_completed_response()
    {
        var sink = new ListSink();
        var encoder = new ResponsesStreamEncoder(sink, "resp_1", "parux-code");
        await encoder.StartAsync();
        ChatStreamAccumulator turn = await Drive(encoder.TextAsync, encoder.ToolAsync);
        JsonObject completed = await encoder.FinishAsync(turn.Usage);

        Assert.Equal("response.created", sink.Events[0].Name);
        Assert.Equal("in_progress", sink.Events[0].Payload["response"]!["status"]!.GetValue<string>());
        Assert.Equal("response.completed", sink.Events[^1].Name);
        Assert.Equal(2, sink.Events.Count(e => e.Name == "response.output_text.delta"));
        JsonArray output = completed["output"]!.AsArray();
        Assert.Equal(new[] { "message", "function_call", "function_call" }, output.Select(i => i!["type"]!.GetValue<string>()).ToArray());
        Assert.Equal("I'll write it.", completed["output_text"]!.GetValue<string>());
        Assert.Equal(150, completed["usage"]!["total_tokens"]!.GetValue<int>());

        // Every item announced during the stream is the same item (same id, same index) in the final response.
        foreach ((string name, JsonObject payload) in sink.Events.Where(e => e.Name == "response.output_item.done"))
        {
            int index = payload["output_index"]!.GetValue<int>();
            Assert.Equal(payload["item"]!["id"]!.GetValue<string>(), output[index]!["id"]!.GetValue<string>());
        }
        Assert.Equal("call_a", output[1]!["call_id"]!.GetValue<string>());
        Assert.Equal("{\"path\":\"a.txt\",\"content\":\"OK\"}", output[1]!["arguments"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_tool_calls_are_neither_streamed_nor_saved_as_continuation_state()
    {
        var turn = new ChatStreamAccumulator();
        var completed = new List<CompletedToolCall>();
        completed.AddRange(turn.Add(Chunk("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_ok","function":{"name":"Read","arguments":"{}"}}]}}]}"""), out _));
        completed.AddRange(turn.Add(Chunk("""{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_bad","function":{"name":"not a valid name!","arguments":"{}"}}]}}]}"""), out _));
        completed.AddRange(turn.Complete());
        Assert.Equal(2, completed.Count);
        var sink = new ListSink();
        var encoder = new AnthropicStreamEncoder(sink, "msg_v", "parux-code");
        await encoder.StartAsync();
        foreach (CompletedToolCall call in completed) await encoder.ToolAsync(call);

        Assert.Single(sink.Events, e => e.Name == "content_block_start");                            // only the valid call is shown
        JsonArray saved = turn.ToChatMessage()["tool_calls"]!.AsArray();
        Assert.Equal("call_ok", Assert.Single(saved)!["id"]!.GetValue<string>());                   // and only it is remembered
    }

    [Fact]
    public async Task Truncated_turns_are_reported_as_truncated_streamed_and_buffered()
    {
        const string truncated = """{"choices":[{"message":{"role":"assistant","content":""},"finish_reason":"length"}],"usage":{"prompt_tokens":28,"completion_tokens":600}}""";
        Assert.Equal("max_tokens", TranslateChatAnswerToAnthropicForTest(truncated)["stop_reason"]!.GetValue<string>());
        JsonObject buffered = TranslateChatAnswerToResponsesForTest(truncated);
        Assert.Equal("incomplete", buffered["status"]!.GetValue<string>());
        Assert.Equal("max_output_tokens", buffered["incomplete_details"]!["reason"]!.GetValue<string>());

        var sink = new ListSink();
        var encoder = new ResponsesStreamEncoder(sink, "resp_t", "parux-fast");
        await encoder.StartAsync();
        await encoder.TextAsync("partial");
        JsonObject final = await encoder.FinishAsync(null, "length");
        Assert.Equal("response.incomplete", sink.Events[^1].Name);
        Assert.Equal("incomplete", final["status"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_requests_think_only_when_the_client_asks()
    {
        static JsonObject Normalize(string thinking) => NormalizeAnthropicRequest(
            "{\"model\":\"parux-auto\",\"max_tokens\":600" + thinking + ",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}").Request!;
        Assert.False(Normalize("")["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.False(Normalize(",\"thinking\":{\"type\":\"disabled\"}")["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Null(Normalize(",\"thinking\":{\"type\":\"adaptive\"}")["chat_template_kwargs"]);             // Claude Code
        Assert.Null(Normalize(",\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":4000}")["chat_template_kwargs"]);
    }

    [Fact]
    public async Task Idle_stream_sends_keep_alives_and_stops_after_dispose()
    {
        var written = new List<string>();
        var gate = new object();
        var channel = new SseChannel((text, _) => { lock (gate) written.Add(text); return Task.CompletedTask; },
            ": keepalive\n\n", TimeSpan.FromMilliseconds(100), CancellationToken.None);
        await Task.Delay(450);
        await channel.EventAsync("x", new JsonObject { ["a"] = 1 });
        await channel.DisposeAsync();
        int count;
        lock (gate) count = written.Count;
        await Task.Delay(250);
        lock (gate)
        {
            Assert.Contains(": keepalive\n\n", written);
            Assert.Contains("event: x\ndata: {\"a\":1}\n\n", written);
            Assert.Equal(count, written.Count);                                                           // nothing after dispose
        }
    }
}
