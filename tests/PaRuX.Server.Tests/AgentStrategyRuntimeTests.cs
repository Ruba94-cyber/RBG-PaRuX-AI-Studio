using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentStrategyRuntimeTests
{
    private static readonly LocalStructuredOutputRequest Schema = new(
        "test_action",
        "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\"}},\"required\":[\"action\"],\"additionalProperties\":false}",
        Strict: true);

    private sealed class ParsedAction
    {
        public string Action { get; init; } = string.Empty;
    }

    [Fact]
    public async Task Validate_repair_permits_exactly_one_second_structured_completion()
    {
        var client = new SequenceClient(
            CompletionResult.Ok("not-json", structuredOutputApplied: true),
            CompletionResult.Ok("{\"action\":\"answer\"}", structuredOutputApplied: true),
            CompletionResult.Ok("{\"action\":\"must-never-be-used\"}", structuredOutputApplied: true));
        AgentExecutionBudget budget = Budget("adaptive", "validate-repair");

        AgentStrategyRuntime.StructuredAttempt<ParsedAction> result =
            await AgentStrategyRuntime.CompleteStructuredAsync(
                client,
                "system",
                "user",
                Schema,
                Parse,
                budget,
                CancellationToken.None);

        Assert.True(result.Result.Success);
        Assert.True(result.RepairAttempted);
        Assert.NotNull(result.Value);
        Assert.Equal("answer", result.Value!.Action);
        Assert.Equal(2, client.StructuredCalls);
        Assert.Contains("only repair attempt", client.UserPrompts[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schema_first_does_not_spend_a_second_model_call_on_parse_failure()
    {
        var client = new SequenceClient(CompletionResult.Ok("not-json", structuredOutputApplied: true));
        AgentExecutionBudget budget = Budget("adaptive", "schema-first");

        AgentStrategyRuntime.StructuredAttempt<ParsedAction> result =
            await AgentStrategyRuntime.CompleteStructuredAsync(
                client, "system", "user", Schema, Parse, budget, CancellationToken.None);

        Assert.False(result.RepairAttempted);
        Assert.Null(result.Value);
        Assert.Equal(1, client.StructuredCalls);
    }

    [Fact]
    public async Task Transport_failure_is_never_relabelled_as_a_protocol_repair()
    {
        var client = new SequenceClient(CompletionResult.Failure("runtime unavailable"));
        AgentExecutionBudget budget = Budget("adaptive", "validate-repair");

        AgentStrategyRuntime.StructuredAttempt<ParsedAction> result =
            await AgentStrategyRuntime.CompleteStructuredAsync(
                client, "system", "user", Schema, Parse, budget, CancellationToken.None);

        Assert.False(result.Result.Success);
        Assert.False(result.RepairAttempted);
        Assert.Equal("runtime unavailable", result.Result.Error);
        Assert.Equal(1, client.StructuredCalls);
    }

    [Fact]
    public async Task Parallel_evidence_starts_independent_sources_before_waiting_for_either()
    {
        AgentExecutionBudget budget = Budget("parallel-evidence", "schema-first");
        int started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<Task<int>> Factory(int value) => async () =>
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return value;
        };

        Task<IReadOnlyList<int>> running = AgentStrategyRuntime.RunIndependentEvidenceAsync(
            budget,
            "inspect two independent evidence sources",
            "review",
            new Func<Task<int>>[] { Factory(1), Factory(2) });

        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref started));
        release.TrySetResult();

        IReadOnlyList<int> results = await running;
        Assert.Equal(new[] { 1, 2 }, results);
    }

    [Fact]
    public async Task Serial_strategy_does_not_invoke_parallel_factories_at_all()
    {
        AgentExecutionBudget budget = Budget("serial-evidence", "schema-first");
        int invoked = 0;
        Func<Task<int>> factory = () =>
        {
            Interlocked.Increment(ref invoked);
            return Task.FromResult(1);
        };

        IReadOnlyList<int> results = await AgentStrategyRuntime.RunIndependentEvidenceAsync(
            budget,
            "complex review",
            "review",
            new Func<Task<int>>[] { factory, factory });

        Assert.Empty(results);
        Assert.Equal(0, invoked);
    }

    private static ParsedAction? Parse(string content)
    {
        if (!content.Contains("\"action\"", StringComparison.Ordinal)) return null;
        int marker = content.IndexOf(":\"", StringComparison.Ordinal);
        if (marker < 0) return null;
        int end = content.IndexOf('"', marker + 2);
        if (end <= marker + 2) return null;
        return new ParsedAction { Action = content[(marker + 2)..end] };
    }

    private static AgentExecutionBudget Budget(string toolStrategy, string structuredStrategy) => new(
        ExecutionQualityProfile.Balanced,
        "test-model",
        30,
        20,
        12_000,
        48_000,
        32_768,
        0.5,
        50,
        toolStrategy,
        structuredStrategy,
        3,
        2);

    private sealed class SequenceClient : ILocalCompletionClient
    {
        private readonly Queue<CompletionResult> _results;

        public SequenceClient(params CompletionResult[] results) => _results = new Queue<CompletionResult>(results);

        public int StructuredCalls { get; private set; }
        public List<string> UserPrompts { get; } = new();

        public Task<CompletionResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken) =>
            Task.FromResult(_results.Count > 0 ? _results.Dequeue() : CompletionResult.Failure("no queued result"));

        public Task<CompletionResult> CompleteStructuredAsync(
            string systemPrompt,
            string userPrompt,
            LocalStructuredOutputRequest structuredOutput,
            CancellationToken cancellationToken)
        {
            StructuredCalls++;
            UserPrompts.Add(userPrompt);
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : CompletionResult.Failure("no queued result"));
        }
    }
}
