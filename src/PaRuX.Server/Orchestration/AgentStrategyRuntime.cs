using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Small execution-policy helpers shared by the specialist worker. They deliberately know nothing
/// about repositories, web research or task mutation: AgentWorkerService remains the sole tool
/// executor and this class only makes Execution Profile strategy fields behavioral and testable.
/// </summary>
internal static class AgentStrategyRuntime
{
    internal sealed record StructuredAttempt<T>(
        CompletionResult Result,
        T? Value,
        bool RepairAttempted)
        where T : class;

    /// <summary>
    /// Performs the normal strict schema completion and, only for validate-repair, permits exactly
    /// one bounded second completion when the transport succeeded but the returned payload still
    /// failed the caller's parser. There is no recursive retry and transport failures are never
    /// disguised as parse failures.
    /// </summary>
    public static async Task<StructuredAttempt<T>> CompleteStructuredAsync<T>(
        ILocalCompletionClient client,
        string systemPrompt,
        string userPrompt,
        LocalStructuredOutputRequest schema,
        Func<string, T?> parser,
        AgentExecutionBudget budget,
        CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(budget);

        CompletionResult first = await client.CompleteStructuredAsync(
            systemPrompt, userPrompt, schema, ct).ConfigureAwait(false);
        if (!first.Success || string.IsNullOrWhiteSpace(first.Content))
            return new StructuredAttempt<T>(first, null, false);

        T? parsed = parser(first.Content);
        if (parsed != null || !budget.AllowsStructuredProtocolRepair)
            return new StructuredAttempt<T>(first, parsed, false);

        string invalid = Bound(first.Content, Math.Min(4_000, budget.ToolObservationCharacters));
        string repairPrompt = userPrompt
            + "\n\nSTRUCTURED OUTPUT VALIDATION FAILED. Repair only the protocol object below. "
            + "Preserve the intended action, emit exactly one object valid for the supplied schema, "
            + "and do not add prose or markdown. This is the only repair attempt for this tool step.\n"
            + invalid;

        CompletionResult repaired = await client.CompleteStructuredAsync(
            systemPrompt, repairPrompt, schema, ct).ConfigureAwait(false);
        if (!repaired.Success || string.IsNullOrWhiteSpace(repaired.Content))
            return new StructuredAttempt<T>(repaired, null, true);

        return new StructuredAttempt<T>(repaired, parser(repaired.Content), true);
    }

    /// <summary>
    /// Starts all independent evidence factories before awaiting any one of them. Callers are
    /// responsible for proving each factory is read-only/independent and for bounding its result.
    /// Serial/adaptive decisions that do not qualify return an empty result without invoking a
    /// factory, so "parallel-evidence" never becomes a misleading label for serial work.
    /// </summary>
    public static async Task<IReadOnlyList<T>> RunIndependentEvidenceAsync<T>(
        AgentExecutionBudget budget,
        string? goal,
        string? role,
        IReadOnlyList<Func<Task<T>>> factories)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(factories);
        if (!budget.ShouldParallelizeEvidence(goal, role, factories.Count))
            return Array.Empty<T>();

        Task<T>[] tasks = factories.Select(factory => factory()).ToArray();
        if (tasks.Length == 0) return Array.Empty<T>();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static string Bound(string value, int maximum)
    {
        string text = value?.Trim() ?? string.Empty;
        if (maximum <= 0) return string.Empty;
        return text.Length <= maximum ? text : text[..maximum] + "\n… (invalid structured output truncated)";
    }
}
