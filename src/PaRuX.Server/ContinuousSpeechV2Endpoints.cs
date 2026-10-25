using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal interface IContinuousSpeechStatusProvider
{
    Task<SpeechEngineStatus> GetStatusAsync(CancellationToken cancellationToken);
}

internal interface IContinuousSpeechStreamWorker
{
    Task StreamAsync(
        SynthesizeRequest request,
        string requestId,
        Func<SpeechStreamFrame, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken);
}

public static class ContinuousSpeechV2Endpoints
{
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);

    public static void MapContinuousSpeechV2Endpoints(this WebApplication app)
    {
        app.MapPost("/api/v2/speech/synthesize/stream", async (
            SynthesizeRequest request,
            HttpContext context,
            LocalRuntimeService runtime,
            IContinuousSpeechStatusProvider speech,
            IContinuousSpeechStreamWorker worker,
            CancellationToken ct) =>
        {
            string requestId = Guid.NewGuid().ToString("N");
            int lastSequence = 0;
            try
            {
                _ = SpeechStreamingProtocol.NormalizeVoice(request);
                if (runtime.GamingMode)
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    await context.Response.WriteAsJsonAsync(
                        new { error = "Gaming Mode is active, so continuous speech is unavailable." }, ct);
                    return;
                }

                SpeechEngineStatus engine = await speech.GetStatusAsync(ct).ConfigureAwait(false);
                if (!engine.TtsInstalled)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(
                        new { error = "The PaRuX-managed TTS engine is not installed or healthy on this machine." }, ct);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/x-ndjson; charset=utf-8";
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["X-PaRuX-Audio-Format"] = SpeechStreamingProtocol.Format;
                context.Response.Headers["X-PaRuX-Audio-Sample-Rate"] = SpeechStreamingProtocol.SampleRate.ToString();
                context.Response.Headers["X-PaRuX-Request-Id"] = requestId;

                await WriteFrameAsync(context.Response, new SpeechStreamFrame
                {
                    Type = "start",
                    RequestId = requestId,
                    Sequence = 0,
                    SampleRate = SpeechStreamingProtocol.SampleRate,
                    Channels = SpeechStreamingProtocol.Channels,
                    Format = SpeechStreamingProtocol.Format
                }, ct).ConfigureAwait(false);

                await worker.StreamAsync(
                    request,
                    requestId,
                    async (frame, frameCt) =>
                    {
                        lastSequence = frame.Sequence;
                        await WriteFrameAsync(context.Response, frame, frameCt).ConfigureAwait(false);
                    },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || context.RequestAborted.IsCancellationRequested)
            {
                // HTTP cancellation is the v2 barge-in primitive. ContinuousTtsWorker kills the
                // resident generator so no late audio from this turn can leak into the next one.
            }
            catch (TextRuntimeStateUnavailableException ex) when (!context.Response.HasStarted)
            {
                await TextRuntimeUnavailableResponse.TryWriteAsync(context.Response, ex, CancellationToken.None);
            }
            catch (InvalidDataException ex) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message }, CancellationToken.None);
            }
            catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = ex is InvalidDataException
                        ? StatusCodes.Status400BadRequest
                        : ex is InvalidOperationException && runtime.GamingMode
                            ? StatusCodes.Status409Conflict
                            : StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = ex.Message }, CancellationToken.None);
                    return;
                }

                try
                {
                    await WriteFrameAsync(context.Response, new SpeechStreamFrame
                    {
                        Type = "error",
                        RequestId = requestId,
                        Sequence = lastSequence + 1,
                        SampleRate = SpeechStreamingProtocol.SampleRate,
                        Channels = SpeechStreamingProtocol.Channels,
                        Format = SpeechStreamingProtocol.Format,
                        Error = Trim(ex.Message)
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
        });
    }

    private static async Task WriteFrameAsync(
        HttpResponse response,
        SpeechStreamFrame frame,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(frame, StreamJson);
        await response.WriteAsync(json + "\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Trim(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= 800 ? text : text[..800] + "…";
    }
}
