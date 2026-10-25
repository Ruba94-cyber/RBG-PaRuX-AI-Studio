using PaRuX.Server;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server.Tests;

/// <summary>Test double for ILocalCompletionClient. It records ordinary and schema-constrained
/// requests separately so tests can prove a caller really asked for structured output instead of
/// merely parsing JSON-shaped prose.</summary>
public sealed class FakeLocalCompletionClient : ILocalCompletionClient
{
    public sealed record Call(string SystemPrompt, string UserPrompt);
    public sealed record StructuredCall(string SystemPrompt, string UserPrompt, LocalStructuredOutputRequest Request);

    public List<Call> Calls { get; } = new();
    public List<StructuredCall> StructuredCalls { get; } = new();
    public bool HonorStructuredOutput { get; set; }

    private readonly Queue<CompletionResult> _responses = new();

    public void Enqueue(CompletionResult result) => _responses.Enqueue(result);
    public void EnqueueOk(string content) => Enqueue(CompletionResult.Ok(content));
    public void EnqueueFailure(string error) => Enqueue(CompletionResult.Failure(error));

    public Exception? ThrowOnNextCall { get; set; }

    public Task<CompletionResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(systemPrompt, userPrompt));
        return TakeNextAsync(structuredOutputApplied: false);
    }

    public Task<CompletionResult> CompleteStructuredAsync(
        string systemPrompt,
        string userPrompt,
        LocalStructuredOutputRequest structuredOutput,
        CancellationToken cancellationToken)
    {
        StructuredCalls.Add(new StructuredCall(systemPrompt, userPrompt, structuredOutput));
        if (!HonorStructuredOutput)
        {
            Calls.Add(new Call(systemPrompt, userPrompt));
            return TakeNextAsync(structuredOutputApplied: false);
        }
        return TakeNextAsync(structuredOutputApplied: true);
    }

    private Task<CompletionResult> TakeNextAsync(bool structuredOutputApplied)
    {
        if (ThrowOnNextCall is { } exception)
        {
            ThrowOnNextCall = null;
            throw exception;
        }

        CompletionResult result = _responses.Count > 0
            ? _responses.Dequeue()
            : CompletionResult.Failure("FakeLocalCompletionClient: no response queued");
        return Task.FromResult(result with { StructuredOutputApplied = structuredOutputApplied && result.Success });
    }
}
