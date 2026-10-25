using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Adds the GPU reader lease to PaRuX's interactive chat transport without coupling scheduling to
/// ChatProxyService itself. That distinction matters: ComfyWorkflowRunner deliberately calls the
/// proxy's internal one-shot helper while it already owns the exclusive media writer in order to
/// refine prompts with the tiny media-assistant LLM. Wrapping ChatProxy globally would deadlock that
/// valid media-internal path. Wrapping only the HTTP chat entry point protects every user chat turn
/// for its complete SSE lifetime while leaving the media-internal helper inside writer authority.
///
/// Visual delegation deliberately runs before the reader lease. A text-only main resident may need
/// to be replaced temporarily by a vision helper and then restored; trying to perform that exclusive
/// transition while this endpoint already owns a shared reader would self-deadlock. After delegation
/// the request contains bounded textual observations instead of pixels, so the normal main-model
/// route can acquire the reader and proceed unchanged.
/// </summary>
public static class ChatSchedulingEndpointExtensions
{
    /// <summary>
    /// This overload is more specific than ASP.NET's generic MapPost overload for the exact legacy
    /// Program.cs handler signature, just like LocalApiEndpointExtensions does for /v1. It lets the
    /// existing endpoint body/error semantics remain unchanged while centralizing the scheduling
    /// boundary here. Non-chat routes with the same delegate shape are forwarded untouched.
    /// </summary>
    public static RouteHandlerBuilder MapPost(
        this WebApplication app,
        string pattern,
        Func<ChatCompletionRequest, ChatProxyService, HttpContext, CancellationToken, Task> legacyHandler)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(legacyHandler);

        if (!string.Equals(pattern, "/api/v1/chat/completions", StringComparison.Ordinal))
            return app.MapMethods(pattern, new[] { HttpMethods.Post }, legacyHandler);

        return app.MapMethods(pattern, new[] { HttpMethods.Post }, async (
            ChatCompletionRequest request,
            ChatProxyService proxy,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            using (MediaExecutionCoordinator.EnterContextScope())
            {
                LocalRuntimeService runtime = context.RequestServices.GetRequiredService<LocalRuntimeService>();
                string? forcedRouteIneligibility = ChatProxyService.GetForcedRouteIneligibility(
                    request.ForcedModelRoute,
                    runtime.UsesExternalProvider,
                    ChatProxyService.ContainsVisualInput(request));
                if (forcedRouteIneligibility != null)
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = new { message = forcedRouteIneligibility }
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                RuntimeManagerService runtimeManager = context.RequestServices.GetRequiredService<RuntimeManagerService>();
                var vision = new VisionSubagentService(runtimeManager, runtime);

                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, runtime.GamingCancellationToken);

                    // Must precede AcquireTextInferenceAsync: DelegateIfNeededAsync can own an
                    // exclusive text transition while it swaps to the helper and restores main.
                    await vision.DelegateIfNeededAsync(request, linked.Token).ConfigureAwait(false);

                    await using MediaExecutionCoordinator.Lease textLease = await runtime
                        .AcquireTextInferenceAsync("chat:interactive", linked.Token).ConfigureAwait(false);
                    await legacyHandler(request, proxy, context, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Client disconnected. Match Program.cs's existing silent cancellation path.
                }
                catch (OperationCanceledException)
                {
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = StatusCodes.Status409Conflict;
                        await context.Response.WriteAsJsonAsync(
                            new { error = "Gaming Mode activated; the AI request was stopped." },
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (ManualSelectionGateBusyException exception)
                {
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = StatusCodes.Status409Conflict;
                        await context.Response.WriteAsJsonAsync(
                            new { error = exception.Code }, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    if (!context.Response.HasStarted)
                    {
                        await ChatProxyFailureResponse.WriteExceptionAsync(
                            context.Response, exception, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        });
    }
}
