using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Primitives;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Compatibility adapter for the OpenAI Videos surface over PaRuX's existing durable local media
/// jobs. This is a protocol adapter only: it never calls OpenAI/Sora and never labels PaRuX output
/// as a proprietary remote model. The currently supported production path is text-to-video; the
/// optional OpenAI input_reference is rejected rather than silently ignored until PaRuX has a
/// verified image-to-video workflow behind the same media boundary.
/// </summary>
public static class OpenAiVideoCompatibilityEndpoints
{
    internal const string LocalModelId = "parux-video";
    private const int MaxCompatibilityStates = 128;
    private const int MaxPromptCharacters = 32_000;
    private static readonly ConcurrentDictionary<string, VideoCompatibilityState> States =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> StateOrder = new();

    internal sealed record NormalizedVideoCreate(
        string Prompt,
        int Width,
        int Height,
        int Seconds,
        string Size,
        string Model,
        byte[]? StartImage = null);

    private sealed record VideoCompatibilityState(
        string InternalJobId,
        string Prompt,
        string Size,
        int Seconds,
        string Model,
        DateTime CreatedUtc);

    public static void MapOpenAiVideoCompatibilityEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/videos", async (HttpRequest request, MediaJobService jobs, CancellationToken ct) =>
        {
            (NormalizedVideoCreate? normalized, string? error, int statusCode) =
                await ParseCreateRequestAsync(request, ct).ConfigureAwait(false);
            if (normalized == null)
                return Results.Json(OpenAiError(error ?? "Invalid video request."), statusCode: statusCode);

            MediaJobStatus job;
            try
            {
                job = jobs.Create(new MediaJobRequest
                {
                    Type = "video",
                    Prompt = normalized.Prompt,
                    Width = normalized.Width,
                    Height = normalized.Height,
                    DurationSeconds = normalized.Seconds,
                    Fps = 24,
                    Steps = 30,
                    // input_reference is the first frame of clip one, the same field the native video panel uses.
                    SourceImageBase64 = normalized.StartImage == null ? string.Empty : Convert.ToBase64String(normalized.StartImage)
                }, owner: "openai-videos");
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(OpenAiError(ex.Message));
            }

            SaveState(new VideoCompatibilityState(
                job.Id,
                normalized.Prompt,
                normalized.Size,
                normalized.Seconds,
                normalized.Model,
                job.CreatedUtc));
            return Results.Json(BuildVideoPayload(job, RequireState(job.Id)), statusCode: StatusCodes.Status200OK);
        });

        app.MapGet("/v1/videos/{videoId}", (string videoId, MediaJobService jobs) =>
        {
            if (!TryResolve(videoId, jobs, out MediaJobStatus? job, out VideoCompatibilityState? state))
                return Results.NotFound(OpenAiError("Video not found."));
            return Results.Ok(BuildVideoPayload(job!, state!));
        });

        app.MapGet("/v1/videos/{videoId}/content", (string videoId, MediaJobService jobs) =>
        {
            if (!TryResolve(videoId, jobs, out MediaJobStatus? job, out _))
                return Results.NotFound(OpenAiError("Video not found."));
            if (!string.Equals(job!.Status, "completed", StringComparison.Ordinal))
                return Results.Conflict(OpenAiError("Video generation is not completed yet."));
            MediaAsset? asset = job.Asset;
            if (asset == null || string.IsNullOrWhiteSpace(asset.Base64Data)
                || !asset.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(OpenAiError("Completed video content is unavailable."));

            try
            {
                byte[] bytes = Convert.FromBase64String(asset.Base64Data);
                return Results.File(bytes, asset.MediaType);
            }
            catch (FormatException)
            {
                return Results.Json(
                    OpenAiError("Stored video content is invalid."),
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/v1/videos", (HttpRequest request, MediaJobService jobs) =>
        {
            string order = request.Query["order"].ToString();
            bool ascending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(order)
                && !ascending
                && !string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(OpenAiError("order must be asc or desc."));

            int limit = 20;
            if (request.Query.TryGetValue("limit", out StringValues rawLimit)
                && (!int.TryParse(rawLimit.ToString(), out limit) || limit is < 0 or > 100))
                return Results.BadRequest(OpenAiError("limit must be an integer from 0 to 100."));

            IEnumerable<VideoCompatibilityState> ordered = ascending
                ? States.Values.OrderBy(state => state.CreatedUtc)
                : States.Values.OrderByDescending(state => state.CreatedUtc);
            List<VideoCompatibilityState> states = ordered.ToList();

            string after = request.Query["after"].ToString();
            if (!string.IsNullOrWhiteSpace(after))
            {
                string? afterInternal = ParseExternalId(after);
                int index = afterInternal == null
                    ? -1
                    : states.FindIndex(state => string.Equals(state.InternalJobId, afterInternal, StringComparison.Ordinal));
                if (index < 0) return Results.BadRequest(OpenAiError("after references an unknown video id."));
                states = states.Skip(index + 1).ToList();
            }

            var data = new JsonArray();
            foreach (VideoCompatibilityState state in states.Take(limit))
            {
                MediaJobStatus? job = jobs.Get(state.InternalJobId);
                if (job == null) continue; // Native retention pruned it; never return a ghost resource.
                data.Add(BuildVideoPayload(job, state));
            }
            return Results.Ok(new JsonObject { ["object"] = "list", ["data"] = data });
        });
    }

    internal static (NormalizedVideoCreate? Request, string? Error) NormalizeCreateFields(
        string? prompt,
        string? model,
        string? seconds,
        string? size,
        byte[]? inputReference = null)
    {
        prompt = (prompt ?? string.Empty).Trim();
        if (prompt.Length is < 1 or > MaxPromptCharacters)
            return (null, "prompt must contain 1-32000 characters.");

        model = string.IsNullOrWhiteSpace(model) ? LocalModelId : model.Trim();
        if (!string.Equals(model, LocalModelId, StringComparison.OrdinalIgnoreCase))
            return (null,
                "This endpoint is local-only and supports model \"" + LocalModelId
                + "\". PaRuX will not impersonate the proprietary model \"" + model + "\".");

        seconds = string.IsNullOrWhiteSpace(seconds) ? "4" : seconds.Trim();
        if (!int.TryParse(seconds, out int duration) || duration is not (4 or 8 or 12))
            return (null, "seconds must be one of 4, 8, or 12.");

        size = string.IsNullOrWhiteSpace(size) ? "720x1280" : size.Trim().ToLowerInvariant();
        (int Width, int Height)? dimensions = size switch
        {
            "720x1280" => (720, 1280),
            "1280x720" => (1280, 720),
            "1024x1792" => (1024, 1792),
            "1792x1024" => (1792, 1024),
            _ => null
        };
        if (!dimensions.HasValue)
            return (null, "size must be one of 720x1280, 1280x720, 1024x1792, or 1792x1024.");

        // The reference becomes the first frame, so it must match the aspect ratio of the requested size.
        if (inputReference != null)
        {
            try
            {
                MediaStartImageValidator.ValidateForVideo(inputReference, dimensions.Value.Width, dimensions.Value.Height);
            }
            catch (InvalidDataException ex)
            {
                return (null, ex.Message);
            }
        }

        return (new NormalizedVideoCreate(
            prompt,
            dimensions.Value.Width,
            dimensions.Value.Height,
            duration,
            size,
            LocalModelId,
            inputReference), null);
    }

    internal static JsonObject BuildVideoPayloadForTests(
        MediaJobStatus job,
        string prompt,
        string size,
        int seconds) =>
        BuildVideoPayload(job, new VideoCompatibilityState(
            job.Id, prompt, size, seconds, LocalModelId, job.CreatedUtc));

    // input_reference arrives as a data: URL or raw base64 string in JSON (a multipart file is handled inline).
    private static (byte[]? Bytes, string? Error) TryDecodeReference(string text)
    {
        try
        {
            return (MediaStartImageValidator.DecodePayload(text), null);
        }
        catch (InvalidDataException ex)
        {
            return (null, ex.Message);
        }
    }

    private static async Task<(NormalizedVideoCreate? Request, string? Error, int StatusCode)> ParseCreateRequestAsync(
        HttpRequest request,
        CancellationToken ct)
    {
        string? prompt;
        string? model;
        string? seconds;
        string? size;
        byte[]? inputReference = null;

        if (request.HasFormContentType)
        {
            IFormCollection form;
            try { form = await request.ReadFormAsync(ct).ConfigureAwait(false); }
            catch (InvalidDataException ex)
            {
                return (null, ex.Message, StatusCodes.Status400BadRequest);
            }
            prompt = form["prompt"].ToString();
            model = form["model"].ToString();
            seconds = form["seconds"].ToString();
            size = form["size"].ToString();
            IFormFile? reference = form.Files.GetFile("input_reference");
            if (reference != null)
            {
                if (reference.Length > MediaStartImageValidator.MaximumBytes)
                    return (null, "input_reference is larger than the 25 MB limit.", StatusCodes.Status400BadRequest);
                using var buffer = new MemoryStream();
                await reference.CopyToAsync(buffer, ct).ConfigureAwait(false);
                inputReference = buffer.ToArray();
            }
            else if (!StringValues.IsNullOrEmpty(form["input_reference"]))
            {
                (inputReference, string? referenceError) = TryDecodeReference(form["input_reference"].ToString());
                if (referenceError != null) return (null, referenceError, StatusCodes.Status400BadRequest);
            }
        }
        else if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            JsonObject? body;
            try
            {
                body = await JsonNode.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false) as JsonObject;
            }
            catch (JsonException)
            {
                return (null, "Invalid JSON body.", StatusCodes.Status400BadRequest);
            }
            if (body == null) return (null, "Request body must be an object.", StatusCodes.Status400BadRequest);
            prompt = ReadString(body["prompt"]);
            model = ReadString(body["model"]);
            seconds = ReadString(body["seconds"]);
            size = ReadString(body["size"]);
            if (body["input_reference"] != null)
            {
                string? referenceText = ReadString(body["input_reference"]);
                if (referenceText == null)
                    return (null, "input_reference must be an image file (multipart) or a base64 or data: URL string (JSON).",
                        StatusCodes.Status400BadRequest);
                (inputReference, string? referenceError) = TryDecodeReference(referenceText);
                if (referenceError != null) return (null, referenceError, StatusCodes.Status400BadRequest);
            }
        }
        else
        {
            return (null,
                "Content-Type must be multipart/form-data (OpenAI-compatible) or application/json.",
                StatusCodes.Status415UnsupportedMediaType);
        }

        (NormalizedVideoCreate? normalized, string? error) =
            NormalizeCreateFields(prompt, model, seconds, size, inputReference);
        return normalized == null
            ? (null, error, StatusCodes.Status400BadRequest)
            : (normalized, null, StatusCodes.Status200OK);
    }

    private static JsonObject BuildVideoPayload(MediaJobStatus job, VideoCompatibilityState state)
    {
        string status = job.Status switch
        {
            "queued" => "queued",
            "running" or "cancelling" => "in_progress",
            "completed" => "completed",
            _ => "failed"
        };
        JsonNode? error = null;
        if (status == "failed")
        {
            error = new JsonObject
            {
                ["code"] = string.Equals(job.Status, "cancelled", StringComparison.Ordinal)
                    ? "cancelled" : "generation_failed",
                ["message"] = job.Error ?? (string.Equals(job.Status, "cancelled", StringComparison.Ordinal)
                    ? "Video generation was cancelled." : "Video generation failed."),
                ["misalignment"] = null
            };
        }

        return new JsonObject
        {
            ["id"] = ExternalId(job.Id),
            ["object"] = "video",
            ["model"] = state.Model,
            ["status"] = status,
            ["progress"] = Math.Clamp(job.ProgressPercent, 0, 100),
            ["created_at"] = ToUnixSeconds(job.CreatedUtc),
            ["completed_at"] = status == "completed" ? ToUnixSeconds(job.UpdatedUtc) : null,
            ["expires_at"] = null,
            ["error"] = error,
            ["prompt"] = state.Prompt,
            ["remixed_from_video_id"] = null,
            ["seconds"] = state.Seconds.ToString(),
            ["size"] = state.Size,
            ["quality"] = "standard"
        };
    }

    private static void SaveState(VideoCompatibilityState state)
    {
        States[state.InternalJobId] = state;
        StateOrder.Enqueue(state.InternalJobId);
        while (States.Count > MaxCompatibilityStates && StateOrder.TryDequeue(out string? old))
            States.TryRemove(old, out _);
    }

    private static VideoCompatibilityState RequireState(string internalId) =>
        States.TryGetValue(internalId, out VideoCompatibilityState? state)
            ? state
            : throw new InvalidOperationException("Video compatibility state disappeared during creation.");

    private static bool TryResolve(
        string externalId,
        MediaJobService jobs,
        out MediaJobStatus? job,
        out VideoCompatibilityState? state)
    {
        job = null;
        state = null;
        string? internalId = ParseExternalId(externalId);
        if (internalId == null || !States.TryGetValue(internalId, out state)) return false;
        job = jobs.Get(internalId);
        return job != null && string.Equals(job.Type, "video", StringComparison.Ordinal);
    }

    private static string ExternalId(string internalId) => "video_" + internalId;

    private static string? ParseExternalId(string? externalId)
    {
        externalId = (externalId ?? string.Empty).Trim();
        if (!externalId.StartsWith("video_", StringComparison.Ordinal) || externalId.Length <= 6) return null;
        return externalId[6..];
    }

    private static long ToUnixSeconds(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeSeconds();
    }

    private static JsonObject OpenAiError(string message) => new()
    {
        ["error"] = new JsonObject
        {
            ["message"] = message,
            ["type"] = "invalid_request_error"
        }
    };

    private static string? ReadString(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        try { return value.GetValue<string>(); } catch { return null; }
    }
}
