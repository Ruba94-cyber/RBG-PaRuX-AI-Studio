using System.Text.Json;

namespace PaRuX.Server;

/// <summary>HTTP transport for the OpenAI-compatible /v1 surface. Model routing may perform a
/// physical text-runtime transition, so this layer deliberately does not acquire a shared reader
/// before LocalApiServerService.PrepareAsync has routed the requested role. Baseline llama.cpp
/// stream quiescence is independently observable through the explicitly-enabled /slots endpoint;
/// transition writers use that as the fail-closed drain boundary. Holding a reader before routing
/// via runtime.AcquireTextInferenceAsync("local-api:chat-completions", cancellationToken)
/// would deadlock a legitimate role swap (reader waits for itself to leave before writer can run).
/// </summary>
public static class LocalApiEndpointExtensions
{
    public static RouteHandlerBuilder MapPost(
        this WebApplication app,
        string pattern,
        Func<HttpRequest, LocalApiServerService, CancellationToken, Task<IResult>> legacyHandler)
    {
        if (!string.Equals(pattern, "/v1/chat/completions", StringComparison.Ordinal))
            return app.MapMethods(pattern, new[] { HttpMethods.Post }, legacyHandler);

        return MapChatCompletions(app, pattern);
    }

    public static void MapLocalApiEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/models", (LocalApiServerService localApi) => Results.Ok(localApi.ListModels()));
        MapChatCompletions(app, "/v1/chat/completions");
    }

    private static RouteHandlerBuilder MapChatCompletions(WebApplication app, string pattern) =>
        app.MapMethods(pattern, new[] { HttpMethods.Post }, async (
            HttpRequest request,
            HttpResponse response,
            LocalApiServerService localApi,
            LocalRuntimeService runtime,
            CancellationToken cancellationToken) =>
        {
            string? body = await CompatRequestBody.ReadAsync(request, (status, message) =>
                WriteJsonAsync(response, new LocalApiCompletionResult(
                    LocalApiOutcome.BadRequest, status,
                    JsonSerializer.Serialize(new { error = new { message, type = "invalid_request_error" } })),
                    cancellationToken), cancellationToken);
            if (body == null) return;

            if (!LocalApiServerService.RequestsStreaming(body))
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, runtime.GamingCancellationToken);
                LocalApiCompletionResult buffered = await localApi.CompleteAsync(body, linked.Token);
                await WriteJsonAsync(response, buffered, cancellationToken);
                return;
            }

            using var streamLinked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, runtime.GamingCancellationToken);

            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers["Cache-Control"] = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no";

            try
            {
                LocalApiCompletionResult streamed = await localApi.StreamAsync(body, response.Body, streamLinked.Token);
                if (streamed.Outcome != LocalApiOutcome.Ok && !response.HasStarted)
                {
                    ClearStreamingHeaders(response);
                    await WriteJsonAsync(response, streamed, cancellationToken);
                }
                else if (streamed.Outcome != LocalApiOutcome.Ok)
                {
                    // Bytes are already out, so the 200 stands; the failure is a terminal error frame.
                    await WriteStreamErrorFrameAsync(response, streamed.Body, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Client disconnected/stopped generation. There is no response left to repair.
            }
            catch (OperationCanceledException)
            {
                if (response.HasStarted)
                {
                    response.HttpContext.Abort();
                    return;
                }

                ClearStreamingHeaders(response);
                await WriteJsonAsync(response,
                    new LocalApiCompletionResult(
                        LocalApiOutcome.UpstreamFailure,
                        StatusCodes.Status409Conflict,
                        JsonSerializer.Serialize(new { error = new { message = "Gaming Mode activated; the AI request was stopped." } })),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                if (response.HasStarted)
                {
                    await WriteStreamErrorFrameAsync(response,
                        LocalApiCompletionResult.UpstreamFailure("The local model stream ended unexpectedly: " + exception.Message).Body,
                        cancellationToken);
                    return;
                }

                ClearStreamingHeaders(response);
                await WriteJsonAsync(response,
                    LocalApiCompletionResult.UpstreamFailure(
                        "The local model stream ended unexpectedly: " + exception.Message),
                    cancellationToken);
            }
        });

    private static async Task WriteJsonAsync(
        HttpResponse response, LocalApiCompletionResult result, CancellationToken cancellationToken)
    {
        response.StatusCode = result.StatusCode;
        response.ContentType = "application/json; charset=utf-8";
        await response.WriteAsync(result.Body, cancellationToken);
    }

    /// <summary>OpenAI-style terminal error chunk for a failure after the SSE stream started.</summary>
    private static async Task WriteStreamErrorFrameAsync(HttpResponse response, string upstreamBody, CancellationToken cancellationToken)
    {
        string message = upstreamBody;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(upstreamBody);
            if (doc.RootElement.TryGetProperty("error", out JsonElement error))
                message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement m)
                    ? m.GetString() ?? message
                    : error.ValueKind == JsonValueKind.String ? error.GetString() ?? message : message;
        }
        catch (Exception) { }
        try
        {
            string frame = JsonSerializer.Serialize(new { error = new { message, type = "api_error" } });
            await response.WriteAsync("data: " + frame + "\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested || response.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The client is gone; there is no one left to tell.
        }
    }

    private static void ClearStreamingHeaders(HttpResponse response)
    {
        response.Headers.Remove("Cache-Control");
        response.Headers.Remove("X-Accel-Buffering");
        response.ContentType = null;
    }
}
