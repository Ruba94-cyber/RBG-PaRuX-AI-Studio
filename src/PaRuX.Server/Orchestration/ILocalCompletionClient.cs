namespace PaRuX.Server.Orchestration;

/// <summary>
/// Abstraction over "ask the local model one question, get one answer back, non-streaming" - the
/// seam AgentWorkerService (Phase 5) and VerifierService (Phase 7) depend on instead of talking
/// to llama-server directly, so both are unit-testable without a loaded local model on the
/// machine running the tests. The real implementation (LlamaServerCompletionClient) is a thin
/// non-streaming call to the exact same OpenAI-compatible endpoint ChatProxyService already uses
/// (the former buffered MCP tool loop was the precedent for "stream:false against
/// LocalRuntimeService.ProviderEndpoint" in this codebase).
/// </summary>
public interface ILocalCompletionClient
{
    Task<CompletionResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);

    /// <summary>
    /// Requests schema-constrained JSON when the active provider has truthful capability evidence.
    /// The default implementation intentionally falls back to the existing unconstrained completion
    /// contract so test doubles and unsupported engines retain bounded parser/self-repair behavior
    /// rather than failing or pretending they applied a schema.
    /// </summary>
    async Task<CompletionResult> CompleteStructuredAsync(
        string systemPrompt,
        string userPrompt,
        LocalStructuredOutputRequest structuredOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(structuredOutput);
        CompletionResult fallback = await CompleteAsync(systemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);
        return fallback with { StructuredOutputApplied = false };
    }
}

public sealed record CompletionResult(
    bool Success,
    string? Content,
    string? Error,
    bool StructuredOutputApplied = false)
{
    public static CompletionResult Ok(string content, bool structuredOutputApplied = false) =>
        new(true, content, null, structuredOutputApplied);

    public static CompletionResult Failure(string error, bool structuredOutputApplied = false) =>
        new(false, null, error, structuredOutputApplied);
}
