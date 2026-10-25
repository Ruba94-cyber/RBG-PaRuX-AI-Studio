using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class McpToolCallBatchExecutorTests
{
    [Fact]
    public async Task Sibling_calls_run_concurrently_and_results_keep_model_order()
    {
        var calls = new JsonArray(
            Call("call_slow", "slow"),
            Call("call_fast", "fast"));
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int peak = 0;

        IReadOnlyList<McpToolCallBatchExecutor.Result> results = await McpToolCallBatchExecutor.ExecuteAsync(
            calls,
            async (name, _, ct) =>
            {
                int now = Interlocked.Increment(ref active);
                int observed;
                do
                {
                    observed = Volatile.Read(ref peak);
                    if (observed >= now) break;
                }
                while (Interlocked.CompareExchange(ref peak, now, observed) != observed);
                if (now >= 2) started.TrySetResult(true);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
                await Task.Delay(name == "slow" ? 80 : 10, ct);
                Interlocked.Decrement(ref active);
                return name + "-result";
            },
            CancellationToken.None);

        Assert.True(peak >= 2, "The sibling MCP calls never overlapped.");
        Assert.Equal(new[] { "call_slow", "call_fast" }, results.Select(result => result.Id));
        Assert.Equal(new[] { "slow-result", "fast-result" }, results.Select(result => result.Content));
    }

    [Fact]
    public async Task One_tool_failure_becomes_its_result_without_cancelling_siblings()
    {
        var calls = new JsonArray(Call("bad", "bad"), Call("good", "good"));

        IReadOnlyList<McpToolCallBatchExecutor.Result> results = await McpToolCallBatchExecutor.ExecuteAsync(
            calls,
            (name, _, _) => name == "bad"
                ? Task.FromException<string>(new InvalidOperationException("boom"))
                : Task.FromResult("ok"),
            CancellationToken.None);

        Assert.Equal("Tool call failed: boom", results[0].Content);
        Assert.Equal("ok", results[1].Content);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_into_a_tool_result()
    {
        var calls = new JsonArray(Call("wait", "wait"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => McpToolCallBatchExecutor.ExecuteAsync(
            calls,
            async (_, _, ct) => { await Task.Delay(100, ct); return "never"; },
            cts.Token));
    }

    private static JsonObject Call(string id, string name) => new()
    {
        ["id"] = id,
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["arguments"] = "{}"
        }
    };
}
