using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using PaRuX.Server;
using Microsoft.Extensions.Logging;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The default chat path advertises MCP tools, which used to force the whole tool loop to run
/// non-streaming and deliver the answer as one chunk. These tests drive the streaming loop against
/// a real loopback HTTP server that emits SSE deltas with delays, like llama-server does.
/// </summary>
public sealed class StreamingMcpToolLoopTests : IAsyncLifetime
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private sealed record Step(int DelayMs, string Line);

    private WebApplication _app = null!;
    private string _endpoint = string.Empty;
    private readonly List<JsonObject> _requests = new();
    private readonly List<Func<Step[]>> _turns = new();
    private readonly List<long> _upstreamFinishedAt = new();
    private TaskCompletionSource _upstreamAborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<HttpContext, Task>? _turnOverride;

    public async Task InitializeAsync()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:" + port);
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            JsonObject body = (JsonObject)JsonNode.Parse(await reader.ReadToEndAsync())!;
            int turn;
            lock (_requests) { _requests.Add(body); turn = _requests.Count - 1; }
            ctx.Response.ContentType = "text/event-stream";
            ctx.RequestAborted.Register(() => _upstreamAborted.TrySetResult());
            if (_turnOverride != null) { await _turnOverride(ctx); return; }
            foreach (Step step in _turns[turn]())
            {
                if (step.DelayMs > 0) await Task.Delay(step.DelayMs, ctx.RequestAborted);
                await ctx.Response.WriteAsync(step.Line + "\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
            lock (_upstreamFinishedAt) _upstreamFinishedAt.Add(Stopwatch.GetTimestamp());
        });
        await _app.StartAsync();
        _endpoint = "http://127.0.0.1:" + port;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static string Delta(string content) =>
        "data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = content } }) }.ToJsonString();

    private static string Reasoning(string text) =>
        "data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["reasoning_content"] = text } }) }.ToJsonString();

    private static string Finish(string reason) =>
        "data: " + new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject(), ["finish_reason"] = reason }),
            ["usage"] = new JsonObject { ["completion_tokens"] = 7 }
        }.ToJsonString();

    private static string ToolDelta(int index, string? id, string? name, string? args)
    {
        var fn = new JsonObject();
        if (name != null) fn["name"] = name;
        if (args != null) fn["arguments"] = args;
        var call = new JsonObject { ["index"] = index, ["function"] = fn };
        if (id != null) { call["id"] = id; call["type"] = "function"; }
        return "data: " + new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(call) } })
        }.ToJsonString();
    }

    private sealed record Frame(string Text, long At);

    private sealed class Harness
    {
        public List<Frame> Frames { get; } = new();
        public List<(string Name, string Args)> ToolInvocations { get; } = new();
        public StreamingMcpToolLoop Loop = null!;
    }

    private Harness NewHarness(
        Func<string, string, Task<string>>? tool = null, int maxPerRound = 4, int maxPerTurn = 8)
    {
        var h = new Harness();
        var payload = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }) };
        h.Loop = new StreamingMcpToolLoop(
            payload,
            new JsonArray(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "lookup" } }),
            open: (body, ct) =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/v1/chat/completions")
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                };
                return Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            },
            invokeTool: async (name, args, ct) =>
            {
                lock (h.ToolInvocations) h.ToolInvocations.Add((name, args));
                return tool != null ? await tool(name, args) : "result-for-" + name;
            },
            writeFrame: (frame, ct) =>
            {
                lock (h.Frames) h.Frames.Add(new Frame(frame, Stopwatch.GetTimestamp()));
                return Task.CompletedTask;
            },
            maxPerRound, maxPerTurn, 16_000);
        return h;
    }

    private static async Task RunAsync(Harness h, CancellationToken ct = default)
    {
        HttpResponseMessage? first = await h.Loop.OpenFirstAsync(ct);
        Assert.NotNull(first);
        await h.Loop.RunAsync(first!, ct);
    }

    private static IEnumerable<JsonNode> Payloads(Harness h) => h.Frames
        .Select(f => f.Text.Trim())
        .Where(t => t.StartsWith("data: {", StringComparison.Ordinal))
        .Select(t => JsonNode.Parse(t[6..])!);

    private static string Text(Harness h) => string.Concat(Payloads(h)
        .Select(p => p["choices"]?[0]?["delta"]?["content"]?.GetValue<string>()));

    [Fact]
    public async Task First_chunk_reaches_the_client_before_the_upstream_finishes()
    {
        _turns.Add(() => new[]
        {
            new Step(0, Delta("Hel")), new Step(400, Delta("lo ")), new Step(400, Delta("world")),
            new Step(0, Finish("stop")), new Step(0, "data: [DONE]")
        });
        Harness h = NewHarness();
        await RunAsync(h);

        Frame firstContent = h.Frames.First(f => f.Text.Contains("Hel"));
        long upstreamDone = _upstreamFinishedAt.Single();
        double leadMs = Stopwatch.GetElapsedTime(firstContent.At, upstreamDone).TotalMilliseconds;
        Assert.True(leadMs > 500, $"First delta must precede the end of the upstream turn by the stream duration; lead was {leadMs:F0} ms.");
        Assert.Equal("Hello world", Text(h));
        Assert.Equal("data: [DONE]\n\n", h.Frames[^1].Text);
        Assert.Single(h.Frames, f => f.Text.Contains("[DONE]"));
        Assert.Empty(h.ToolInvocations);
    }

    [Fact]
    public async Task Reasoning_usage_and_finish_reason_are_forwarded_when_there_are_no_tool_calls()
    {
        _turns.Add(() => new[] { new Step(0, Reasoning("thinking")), new Step(0, Delta("ok")), new Step(0, Finish("stop")), new Step(0, "data: [DONE]") });
        Harness h = NewHarness();
        await RunAsync(h);

        Assert.Contains(h.Frames, f => f.Text.Contains("\"reasoning_content\":\"thinking\""));
        Assert.Contains(h.Frames, f => f.Text.Contains("\"finish_reason\":\"stop\"") && f.Text.Contains("completion_tokens"));
    }

    [Fact]
    public async Task One_tool_round_streams_both_turns_and_feeds_the_result_back()
    {
        _turns.Add(() => new[]
        {
            new Step(0, Delta("Checking. ")),
            new Step(50, ToolDelta(0, "call_a", "lookup", "{\"q\":")),
            new Step(50, ToolDelta(0, null, null, "\"x\"}")),
            new Step(0, Finish("tool_calls")), new Step(0, "data: [DONE]")
        });
        _turns.Add(() => new[] { new Step(0, Delta("It is ")), new Step(100, Delta("42.")), new Step(0, Finish("stop")), new Step(0, "data: [DONE]") });
        Harness h = NewHarness();
        await RunAsync(h);

        Assert.Equal(("lookup", "{\"q\":\"x\"}"), Assert.Single(h.ToolInvocations));
        Assert.Equal("Checking. It is 42.", Text(h));
        Assert.DoesNotContain(h.Frames, f => f.Text.Contains("tool_calls\":[{\"index"));
        Assert.Single(h.Frames, f => f.Text.Contains("\"finish_reason\""));   // only the final turn's
        Assert.Contains(h.Frames, f => f.Text.Contains("\"finish_reason\":\"stop\""));
        Assert.Single(h.Frames, f => f.Text.Contains("[DONE]"));

        JsonArray secondMessages = _requests[1]["messages"]!.AsArray();
        JsonObject assistant = secondMessages[1]!.AsObject();
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        Assert.Equal("call_a", assistant["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("{\"q\":\"x\"}", assistant["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        JsonObject toolMessage = secondMessages[2]!.AsObject();
        Assert.Equal("tool", toolMessage["role"]!.GetValue<string>());
        Assert.Equal("call_a", toolMessage["tool_call_id"]!.GetValue<string>());
        Assert.Equal("result-for-lookup", toolMessage["content"]!.GetValue<string>());
        Assert.True(_requests[1]["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Two_tool_rounds_with_parallel_calls_execute_in_order_and_stream_the_final_answer()
    {
        _turns.Add(() => new[]
        {
            new Step(0, ToolDelta(0, "c0", "lookup", "{}")), new Step(0, ToolDelta(1, "c1", "lookup", "{\"n\":1}")),
            new Step(0, Finish("tool_calls")), new Step(0, "data: [DONE]")
        });
        _turns.Add(() => new[] { new Step(0, ToolDelta(0, "c2", "lookup", "{\"n\":2}")), new Step(0, Finish("tool_calls")), new Step(0, "data: [DONE]") });
        _turns.Add(() => new[] { new Step(0, Delta("done")), new Step(0, Finish("stop")), new Step(0, "data: [DONE]") });
        Harness h = NewHarness();
        await RunAsync(h);

        Assert.Equal(3, h.ToolInvocations.Count);
        Assert.Equal(3, _requests.Count);
        Assert.Equal("done", Text(h));
        JsonArray last = _requests[2]["messages"]!.AsArray();
        Assert.Equal(new[] { "user", "assistant", "tool", "tool", "assistant", "tool" },
            last.Select(m => m!["role"]!.GetValue<string>()).ToArray());
        Assert.Equal("c0", last[2]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("c1", last[3]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Calls_over_the_round_allowance_are_reported_skipped_not_executed()
    {
        _turns.Add(() => new[]
        {
            new Step(0, ToolDelta(0, "a", "lookup", "{}")), new Step(0, ToolDelta(1, "b", "lookup", "{}")),
            new Step(0, ToolDelta(2, "c", "lookup", "{}")), new Step(0, Finish("tool_calls")), new Step(0, "data: [DONE]")
        });
        _turns.Add(() => new[] { new Step(0, Delta("ok")), new Step(0, Finish("stop")), new Step(0, "data: [DONE]") });
        Harness h = NewHarness(maxPerRound: 2);
        await RunAsync(h);

        Assert.Equal(2, h.ToolInvocations.Count);
        JsonArray messages = _requests[1]["messages"]!.AsArray();
        Assert.Equal("c", messages[4]!["tool_call_id"]!.GetValue<string>());
        Assert.Contains("skipped", messages[4]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tool_rounds_are_bounded_and_the_last_turn_runs_without_tools()
    {
        for (int i = 0; i < StreamingMcpToolLoop.MaxToolRounds; i++)
        {
            int n = i;
            _turns.Add(() => new[] { new Step(0, ToolDelta(0, "id" + n, "lookup", "{}")), new Step(0, Finish("tool_calls")), new Step(0, "data: [DONE]") });
        }
        _turns.Add(() => new[] { new Step(0, Delta("final")), new Step(0, Finish("stop")), new Step(0, "data: [DONE]") });
        Harness h = NewHarness(maxPerRound: 4, maxPerTurn: 99);
        await RunAsync(h);

        Assert.Equal(StreamingMcpToolLoop.MaxToolRounds + 1, _requests.Count);
        Assert.NotNull(_requests[StreamingMcpToolLoop.MaxToolRounds - 1]["tools"]);
        Assert.Null(_requests[^1]["tools"]);
        Assert.Equal("final", Text(h));
    }

    [Fact]
    public async Task Cancelling_mid_stream_stops_the_upstream_request_and_skips_tool_execution()
    {
        _turns.Add(() => new[]
        {
            new Step(0, Delta("a")), new Step(0, ToolDelta(0, "x", "lookup", "{}")),
            new Step(5000, Delta("never")), new Step(0, Finish("tool_calls")), new Step(0, "data: [DONE]")
        });
        using var cts = new CancellationTokenSource();
        Harness h = NewHarness();
        Task run = RunAsync(h, cts.Token);
        while (!h.Frames.Any(f => f.Text.Contains("\"a\""))) await Task.Delay(10);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await _upstreamAborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(h.ToolInvocations);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task Cancelling_during_tool_execution_does_not_start_another_upstream_turn()
    {
        _turns.Add(() => new[] { new Step(0, ToolDelta(0, "x", "lookup", "{}")), new Step(0, Finish("tool_calls")), new Step(0, "data: [DONE]") });
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        Harness h = NewHarness(tool: async (_, _) => { started.SetResult(); await Task.Delay(Timeout.Infinite, cts.Token); return ""; });
        Task run = RunAsync(h, cts.Token);
        await started.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task Connection_dropped_mid_stream_surfaces_a_terminal_error_event()
    {
        _turnOverride = async ctx =>
        {
            await ctx.Response.WriteAsync(Delta("par") + "\n\n");
            await ctx.Response.Body.FlushAsync();
            await Task.Delay(100);
            ctx.Abort();
        };
        Harness h = NewHarness();
        await RunAsync(h);

        Assert.Equal("par", Text(h));
        Assert.Contains(h.Frames, f => f.Text.Contains("\"error\"") && f.Text.Contains("provider_error"));
        Assert.Equal("data: [DONE]\n\n", h.Frames[^1].Text);
    }

    [Fact]
    public async Task Stream_that_ends_without_a_finish_marker_is_an_error_not_a_silent_success()
    {
        _turns.Add(() => new[] { new Step(0, Delta("half")) });
        Harness h = NewHarness();
        await RunAsync(h);

        Assert.Contains(h.Frames, f => f.Text.Contains("closed the stream before the answer was complete"));
        Assert.Equal("data: [DONE]\n\n", h.Frames[^1].Text);
    }

    [Fact]
    public async Task Provider_error_event_is_forwarded_and_terminates_the_stream()
    {
        _turns.Add(() => new[] { new Step(0, Delta("x")), new Step(0, "data: {\"error\":{\"message\":\"boom\"}}"), new Step(0, Delta("after")) });
        Harness h = NewHarness();
        await RunAsync(h);

        Assert.Contains(h.Frames, f => f.Text.Contains("boom"));
        Assert.DoesNotContain(h.Frames, f => f.Text.Contains("after"));
        Assert.Equal("data: [DONE]\n\n", h.Frames[^1].Text);
    }

    [Fact]
    public async Task Provider_rejection_on_a_later_turn_is_a_terminal_error_event_with_the_status()
    {
        int call = 0;
        _turnOverride = async ctx =>
        {
            if (Interlocked.Increment(ref call) == 1)
            {
                await ctx.Response.WriteAsync(ToolDelta(0, "x", "lookup", "{}") + "\n\n" + Finish("tool_calls") + "\n\ndata: [DONE]\n\n");
                return;
            }
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsync("slot exploded");
        };
        Harness h = NewHarness();
        await RunAsync(h);

        Assert.Contains(h.Frames, f => f.Text.Contains("HTTP 500") && f.Text.Contains("slot exploded"));
        Assert.Equal("data: [DONE]\n\n", h.Frames[^1].Text);
    }

    [Fact]
    public async Task Rejected_first_turn_returns_null_so_the_caller_can_fall_back()
    {
        _turnOverride = ctx => { ctx.Response.StatusCode = 400; return Task.CompletedTask; };
        Harness h = NewHarness();
        Assert.Null(await h.Loop.OpenFirstAsync(CancellationToken.None));
    }
}
