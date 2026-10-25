using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Everything the live chat path needs from the machine to route a modality turn; faked in tests.</summary>
public interface IModalityChatBackend
{
    Task<ModalityRouteFacts> GetFactsAsync(string role, CancellationToken cancellationToken);
    Task<TranscribeResponse> TranscribeAsync(TranscribeRequest request, CancellationToken cancellationToken);
    Task<SynthesizeResponse> SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken);
    /// <summary>The exact-candidate pin that makes the normal chat path use the verified tool model.</summary>
    RouteRequest CreateToolModelPin(string modelId);
}

/// <summary>Outcome of the modality step: either the turn was fully answered here, or it continues on the text path.</summary>
public sealed record ModalityChatResult(bool Handled, ModalityRouteOutcome? Route);

/// <summary>
/// The live-path hook for audio, tool-use and computer-use turns. Runs before any text runtime is touched, so a speech
/// request never wakes (or waits on) a text model. Manual selections (ForcedModelRoute) and edit-and-continue turns are
/// never rerouted. Refusals are typed and honest; nothing is substituted.
/// </summary>
public static class ModalityChatGate
{
    public static async Task<ModalityChatResult> TryHandleAsync(
        ChatCompletionRequest request, IModalityChatBackend? backend, HttpResponse downstream, CancellationToken cancellationToken)
    {
        if (backend is null || request.ContinueFinalMessage || request.ForcedModelRoute != null)
            return new ModalityChatResult(false, null);

        ChatCompletionMessage? lastUser = request.Messages.LastOrDefault(m => m.Role == "user");
        if (lastUser is null) return new ModalityChatResult(false, null);
        ChatAudioContent? audio = lastUser.Audio?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Base64Data));
        string? role = ModalityRouter.Classify(lastUser.Content);
        // A bare "transcribe" word with nothing to transcribe is ordinary conversation (how do I transcribe...?), not a route.
        if (role == ModalityRoles.AudioTranscribe && audio is null) role = null;
        if (role is null && audio is not null) role = ModalityRoles.AudioTranscribe;
        if (role is null) return new ModalityChatResult(false, null);

        ModalityRouteFacts facts = await backend.GetFactsAsync(role, cancellationToken).ConfigureAwait(false);
        facts = facts with { ComputerUseConsentGranted = request.ComputerUseConsent };
        ModalityRouteOutcome route = ModalityRouter.Resolve(role, facts);
        if (!route.Success)
        {
            await WriteRefusalAsync(downstream, route, cancellationToken).ConfigureAwait(false);
            return new ModalityChatResult(true, route);
        }

        switch (role)
        {
            case ModalityRoles.ToolUse:
                // Validated and pinned: the normal path now serves the turn with the verified model only.
                request.ForcedModelRoute = backend.CreateToolModelPin(route.EngineId!);
                return new ModalityChatResult(false, route);

            case ModalityRoles.ComputerUse:
                await WriteStartAsync(downstream, cancellationToken).ConfigureAwait(false);
                await WriteEventAsync(downstream, RoutedEvent(route, request), cancellationToken).ConfigureAwait(false);
                await WriteEventAsync(downstream, new JsonObject
                {
                    ["parux_event"] = "computer_use_handoff",
                    ["consent"] = "explicit",
                    ["task"] = lastUser.Content
                }, cancellationToken).ConfigureAwait(false);
                await WriteContentAsync(downstream,
                    "Handing this to the Computer Use agent. It runs on your desktop under the approval you just gave.", cancellationToken).ConfigureAwait(false);
                return new ModalityChatResult(true, route);

            case ModalityRoles.AudioSpeak:
                return await SpeakAsync(request, backend, downstream, route, lastUser, cancellationToken).ConfigureAwait(false);

            default:
                return await TranscribeAsync(request, backend, downstream, route, lastUser, audio!, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ModalityChatResult> TranscribeAsync(
        ChatCompletionRequest request, IModalityChatBackend backend, HttpResponse downstream, ModalityRouteOutcome route,
        ChatCompletionMessage lastUser, ChatAudioContent audio, CancellationToken ct)
    {
        TranscribeResponse transcript;
        try
        {
            transcript = await backend.TranscribeAsync(new TranscribeRequest
            {
                AudioBase64 = audio.Base64Data, FileName = audio.FileName
            }, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex) { await WriteErrorAsync(downstream, 400, "invalid_audio", ex.Message, route, ct); return new(true, route); }
        catch (InvalidOperationException ex) when (ex is not TextRuntimeStateUnavailableException)
        {
            await WriteErrorAsync(downstream, 503, "speech_engine_failed", ex.Message, route, ct);
            return new(true, route);
        }

        bool explicitTranscription = ModalityRouter.Classify(lastUser.Content) == ModalityRoles.AudioTranscribe
            || string.IsNullOrWhiteSpace(lastUser.Content);
        if (explicitTranscription)
        {
            await WriteStartAsync(downstream, ct).ConfigureAwait(false);
            await WriteEventAsync(downstream, RoutedEvent(route, request), ct).ConfigureAwait(false);
            await WriteContentAsync(downstream, transcript.Text, ct).ConfigureAwait(false);
            return new ModalityChatResult(true, route);
        }

        // Audio understanding is transcript-first: the transcript joins the question and the text path answers it.
        lastUser.Content = lastUser.Content + "\n\n[Audio transcript]\n" + transcript.Text;
        lastUser.Audio = new List<ChatAudioContent>();
        return new ModalityChatResult(false, route);
    }

    private static async Task<ModalityChatResult> SpeakAsync(
        ChatCompletionRequest request, IModalityChatBackend backend, HttpResponse downstream, ModalityRouteOutcome route,
        ChatCompletionMessage lastUser, CancellationToken ct)
    {
        string text = request.Messages.LastOrDefault(m => m.Role == "assistant")?.Content is { Length: > 0 } previous
            ? previous : lastUser.Content;
        SynthesizeResponse audio;
        try { audio = await backend.SynthesizeAsync(new SynthesizeRequest { Text = text }, ct).ConfigureAwait(false); }
        catch (InvalidDataException ex) { await WriteErrorAsync(downstream, 400, "invalid_text", ex.Message, route, ct); return new(true, route); }
        catch (InvalidOperationException ex) when (ex is not TextRuntimeStateUnavailableException)
        {
            await WriteErrorAsync(downstream, 503, "speech_engine_failed", ex.Message, route, ct);
            return new(true, route);
        }
        await WriteStartAsync(downstream, ct).ConfigureAwait(false);
        await WriteEventAsync(downstream, RoutedEvent(route, request), ct).ConfigureAwait(false);
        await WriteEventAsync(downstream, new JsonObject
        {
            ["parux_event"] = "speech_audio",
            ["mediaType"] = audio.MediaType,
            ["fileName"] = audio.FileName,
            ["durationSeconds"] = audio.DurationSeconds,
            ["base64Data"] = audio.Base64Data
        }, ct).ConfigureAwait(false);
        await WriteContentAsync(downstream, "Spoken with the local text-to-speech engine.", ct).ConfigureAwait(false);
        return new ModalityChatResult(true, route);
    }

    /// <summary>The model_routed shape for a turn that never reached a text model; reason carries the router's explanation.</summary>
    internal static JsonObject RoutedEvent(ModalityRouteOutcome route, ChatCompletionRequest request) => new()
    {
        ["parux_event"] = "model_routed",
        ["mode"] = request.ForcedModelRoute == null ? "auto" : "manual",
        ["modality"] = route.Capability,
        ["engineClass"] = route.EngineClass,
        ["engineId"] = route.EngineId,
        ["selectedModel"] = route.EngineId,
        ["reason"] = route.Explanation,
        ["fallbackTrace"] = new JsonArray(route.FallbackTrace.Select(t => (JsonNode?)t).ToArray()),
        ["manualLock"] = false,
        ["provider"] = "parux-local",
        ["locality"] = "local"
    };

    /// <summary>Fields merged into the ordinary model_routed event when the text path continues after a modality step.</summary>
    internal static void Annotate(JsonObject routed, ModalityRouteOutcome route)
    {
        routed["modality"] = route.Capability;
        routed["modalityEngineClass"] = route.EngineClass;
        routed["modalityReason"] = route.Explanation;
    }

    private static int StatusFor(string? code) => code switch
    {
        "gaming_hard_off" => StatusCodes.Status409Conflict,
        "consent_required" => StatusCodes.Status403Forbidden,
        "no_eligible_engine" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status409Conflict
    };

    private static Task WriteRefusalAsync(HttpResponse downstream, ModalityRouteOutcome route, CancellationToken ct) =>
        WriteErrorAsync(downstream, StatusFor(route.RefusalCode), route.RefusalCode ?? "refused", route.Explanation, route, ct);

    private static async Task WriteErrorAsync(
        HttpResponse downstream, int status, string code, string message, ModalityRouteOutcome route, CancellationToken ct)
    {
        downstream.StatusCode = status;
        await downstream.WriteAsJsonAsync(new
        {
            error = new
            {
                code,
                message,
                capability = route.Capability,
                engineClass = route.EngineClass,
                fallbackTrace = route.FallbackTrace
            }
        }, ct).ConfigureAwait(false);
    }

    private static Task WriteStartAsync(HttpResponse downstream, CancellationToken ct)
    {
        downstream.StatusCode = StatusCodes.Status200OK;
        downstream.ContentType = "text/event-stream";
        downstream.Headers.CacheControl = "no-cache";
        return Task.CompletedTask;
    }

    private static Task WriteEventAsync(HttpResponse downstream, JsonObject payload, CancellationToken ct) =>
        downstream.WriteAsync("data: " + payload.ToJsonString() + "\n\n", ct);

    private static async Task WriteContentAsync(HttpResponse downstream, string text, CancellationToken ct)
    {
        await downstream.WriteAsync("data: " + JsonSerializer.Serialize(
            new { choices = new[] { new { delta = new { content = text } } } }) + "\n\n", ct).ConfigureAwait(false);
        await downstream.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
        await downstream.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
