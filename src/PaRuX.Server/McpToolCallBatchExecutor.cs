using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>
/// Executes the tool calls emitted together by one model turn concurrently while preserving their
/// original order for the follow-up assistant request. OpenAI-style tool_calls in one assistant
/// message are independent siblings; executing them serially makes latency equal to the sum of
/// every remote MCP round trip. Ordering is restored after Task.WhenAll so concurrency never
/// changes the transcript seen by the model.
/// </summary>
internal static class McpToolCallBatchExecutor
{
    internal sealed record Result(string Id, string Content);

    public static async Task<IReadOnlyList<Result>> ExecuteAsync(
        JsonArray toolCalls,
        Func<string, string, CancellationToken, Task<string>> invoke,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);
        ArgumentNullException.ThrowIfNull(invoke);

        var tasks = new List<Task<Result>>(toolCalls.Count);
        foreach (JsonNode? callNode in toolCalls)
        {
            if (callNode is not JsonObject call) continue;
            string id = call["id"]?.GetValue<string>() ?? string.Empty;
            JsonObject? function = call["function"] as JsonObject;
            string name = function?["name"]?.GetValue<string>() ?? string.Empty;
            string arguments = function?["arguments"]?.GetValue<string>() ?? "{}";
            tasks.Add(InvokeOneAsync(id, name, arguments, invoke, cancellationToken));
        }

        if (tasks.Count == 0) return Array.Empty<Result>();
        // Task.WhenAll returns results in the same order as the input task array, not completion
        // order. That is exactly the transcript ordering required for tool_call_id correlation.
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<Result> InvokeOneAsync(
        string id,
        string name,
        string arguments,
        Func<string, string, CancellationToken, Task<string>> invoke,
        CancellationToken cancellationToken)
    {
        try
        {
            string value = await invoke(name, arguments, cancellationToken).ConfigureAwait(false);
            return new Result(id, value ?? string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(id, "Tool call failed: " + ex.Message);
        }
    }
}
