using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// The persisted media content lane (Standard default, Uncensored, Automatic), stored next to the media checkpoint overrides in the
/// PaRuX data root. A missing, unreadable or unrecognised value reads as Standard, so a damaged file can never widen the lane.
/// </summary>
internal static class MediaContentLaneStore
{
    private const string Key = "lane";
    private static string FilePath => Path.Combine(PaRuXDataRoot.Path, "media-content-lane.json");

    internal static string Get()
    {
        try
        {
            return LocalAiPaths.LoadOverrides(FilePath).TryGetValue(Key, out string? v)
                ? MediaContentLanes.Normalize(v) ?? MediaContentLanes.Standard
                : MediaContentLanes.Standard;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return MediaContentLanes.Standard; }
    }

    /// <summary>Saves a lane. Throws InvalidDataException for anything other than Standard, Uncensored or Automatic.</summary>
    internal static string Set(string? lane)
    {
        string normalized = MediaContentLanes.Normalize(lane)
            ?? throw new InvalidDataException("Lane must be Standard, Uncensored or Automatic.");
        LocalAiPaths.MutateOverrides(FilePath, working => { working[Key] = normalized; return true; });
        return normalized;
    }
}

/// <summary>The prompt of the media request being processed, flowing with the async call chain so the router can read it where the
/// checkpoint is resolved (the graph builders have no prompt parameter at that point). Null outside a request.</summary>
internal static class MediaRoutingContext
{
    private static readonly AsyncLocal<string?> Prompt = new();

    internal static string? CurrentPrompt => Prompt.Value;

    internal static IDisposable Begin(string? prompt)
    {
        string? previous = Prompt.Value;
        Prompt.Value = prompt;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        internal Scope(string? previous) => _previous = previous;
        public void Dispose() => Prompt.Value = _previous;
    }
}

internal static class MediaLaneEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/media/lane", () => Results.Ok(new MediaLaneStatus { Lane = MediaContentLaneStore.Get() }));
        app.MapPost("/api/v1/media/lane", (SetMediaLaneRequest request) =>
        {
            try { return Results.Ok(new MediaLaneStatus { Lane = MediaContentLaneStore.Set(request.Lane) }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();
    }
}
