using Microsoft.AspNetCore.Http;
using PaRuX.Intelligence;

namespace PaRuX.Server;

internal sealed class PromptExceedsContextException : Exception
{
    public const string ErrorCode = "prompt_exceeds_context";
    public const string RemediationText =
        "Shorten the prompt, split it into smaller requests, or use a model with a larger context window.";

    public PromptExceedsContextException(string message) : base(message) { }
}

internal static class ChatProxyFailureResponse
{
    private const string RetryAfterSeconds = "5";
    private const string RetryHint = "Retry after the local runtime is available.";

    internal static int GetEffectiveMessageContextTokensOrThrowTypedFailure(
        int physicalContextTokens,
        int fixedPromptTokens,
        int reservedForReplyTokens)
    {
        try
        {
            return ChatPromptBudget.EffectiveMessageContextTokens(
                physicalContextTokens,
                fixedPromptTokens,
                reservedForReplyTokens);
        }
        catch (InvalidDataException exception)
        {
            throw new PromptExceedsContextException(exception.Message);
        }
    }

    internal static int TightenExactContextOrThrowTypedFailure(
        int currentMessageContextTokens,
        int exactRenderedPromptTokens,
        int maximumRenderedPromptTokens,
        int reservedForReplyTokens,
        ConversationWindowPlanner.WindowPlan plan,
        int fixedPromptTokens,
        bool pricedOverBudget)
    {
        try
        {
            return ChatPromptBudget.TightenMessageContextTokens(
                currentMessageContextTokens,
                exactRenderedPromptTokens,
                maximumRenderedPromptTokens,
                reservedForReplyTokens);
        }
        catch (InvalidDataException)
        {
            PromptExceedsContextException? failure = CreatePromptExceedsContextFailure(
                plan,
                fixedPromptTokens,
                maximumRenderedPromptTokens,
                exactRenderedPromptTokens,
                pricedOverBudget,
                tightened: false);
            if (failure is not null) throw failure;
            throw;
        }
    }

    internal static PromptExceedsContextException? CreatePromptExceedsContextFailure(
        ConversationWindowPlanner.WindowPlan plan,
        int fixedPromptTokens,
        int maximumRenderedPromptTokens,
        int? exactRenderedPromptTokens,
        bool pricedOverBudget,
        bool tightened)
    {
        int estimatedPromptTokens = plan.EstimatedTokens + fixedPromptTokens;
        bool estimateExceeds = (pricedOverBudget && !tightened)
            || estimatedPromptTokens > maximumRenderedPromptTokens;
        bool exact = exactRenderedPromptTokens.HasValue;
        bool exceedsWindow = exact
            ? exactRenderedPromptTokens!.Value > maximumRenderedPromptTokens
            : estimateExceeds;
        if (!exceedsWindow) return null;

        int? measuredPromptTokens = exact
            ? exactRenderedPromptTokens
            : estimatedPromptTokens > maximumRenderedPromptTokens ? estimatedPromptTokens : null;

        string size = measuredPromptTokens.HasValue
            ? measuredPromptTokens.Value.ToString("N0") + " input tokens"
            : "more input tokens than the active window allows";
        string message = exact
            ? "The fully rendered prompt still requires " + size + " after bounded compaction, but this model can safely accept only "
                + maximumRenderedPromptTokens.ToString("N0") + " while preserving the reply reserve."
            : "The rendered prompt could not be verified by the runtime and its estimated size still exceeds the "
                + maximumRenderedPromptTokens.ToString("N0") + " input tokens this model can safely accept while preserving the reply reserve."
                + (measuredPromptTokens.HasValue ? " The remaining estimate is " + size + "." : string.Empty);

        return new PromptExceedsContextException(message);
    }

    /// <summary>
    /// The tool loop re-counts the provider payload after tool results are appended. Growth past the
    /// safe input budget is a deterministic over-budget prompt, so it uses the same typed 413 failure
    /// as the planner instead of a retryable 503.
    /// </summary>
    internal static PromptExceedsContextException? CreateToolLoopGrowthFailure(
        int? exactRenderedPromptTokens,
        int maximumRenderedPromptTokens)
    {
        if (!exactRenderedPromptTokens.HasValue || exactRenderedPromptTokens.Value <= maximumRenderedPromptTokens)
            return null;

        return new PromptExceedsContextException(
            "The MCP conversation grew to " + exactRenderedPromptTokens.Value.ToString("N0")
            + " rendered input tokens after tool results, exceeding the safe "
            + maximumRenderedPromptTokens.ToString("N0")
            + "-token input budget. PaRuX stopped before sending an overflowing prompt; reduce tool output or use a larger-context model.");
    }

    internal static async Task WriteExceptionAsync(
        HttpResponse response,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is PromptExceedsContextException promptTooLarge)
        {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = PromptExceedsContextException.ErrorCode,
                    message = promptTooLarge.Message,
                    remediation = PromptExceedsContextException.RemediationText
                }
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        AddRetryHint(response);
        await response.WriteAsJsonAsync(new
        {
            error = exception.Message,
            retry_hint = RetryHint
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WriteRuntimeUnavailableAsync(
        HttpResponse response,
        string message,
        CancellationToken cancellationToken)
    {
        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        AddRetryHint(response);
        await response.WriteAsJsonAsync(new
        {
            error = new { message }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static void AddRetryHint(HttpResponse response)
    {
        response.Headers["Retry-After"] = RetryAfterSeconds;
    }
}
