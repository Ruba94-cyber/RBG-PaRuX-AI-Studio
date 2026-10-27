using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Local music generation: ACE-Step 1.5 turbo in the PaRuX-managed ComfyUI. It takes the exclusive GPU lease like every other media job,
/// starts the managed media runtime, submits <see cref="MusicWorkflow"/>, waits for the saved FLAC and returns it as a <see cref="MediaAsset"/>.
/// </summary>
public sealed class MusicService
{
    private const string MediaEndpoint = "http://127.0.0.1:8188";
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(15);

    private readonly LocalRuntimeService _runtime;
    private readonly ComfyWorkflowRunner _comfy;
    private readonly ChatProxyService? _chat;

    public MusicService(LocalRuntimeService runtime, ComfyWorkflowRunner comfy, ChatProxyService? chat = null)
    {
        _runtime = runtime;
        _comfy = comfy;
        _chat = chat;
    }

    internal static string CheckpointPath => Path.Combine(LocalAiPaths.ComfyApp, "models", "checkpoints", MusicWorkflow.CheckpointFileName);

    public async Task<MediaAsset> GenerateAsync(MusicGenerateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        long seed = request.Seed >= 0 ? request.Seed : Random.Shared.NextInt64(0, int.MaxValue);
        // Validates the request (empty prompt, bounds) before any GPU work or process start.
        JsonObject workflow = MusicWorkflow.Build(request, seed, MusicWorkflow.CheckpointFileName);
        if (!File.Exists(CheckpointPath))
            throw new InvalidOperationException("Music is not set up yet: the ACE-Step model is missing. Use \"Set up music\" in Create > Music.");
        if (_runtime.GamingMode) throw new InvalidOperationException("Gaming Mode is active, so music generation is unavailable.");

        await using MediaExecutionCoordinator.Lease lease = await MediaExecutionCoordinator.Current
            .AcquireAsync("music-generate", ct).ConfigureAwait(false);
        try
        {
            await _runtime.PrepareForExclusiveMediaAsync(ct).ConfigureAwait(false);
            await _runtime.PrepareMediaRuntimeForAdmissionAsync(ct).ConfigureAwait(false);
            if (request.WriteLyrics && string.IsNullOrWhiteSpace(request.Lyrics) && _chat is not null && MusicLyrics.WantsLyrics(request.Prompt))
            {
                string lyrics = await WriteLyricsAsync(request, ct).ConfigureAwait(false);
                if (lyrics.Length > 0)
                    workflow = MusicWorkflow.Build(new MusicGenerateRequest
                    {
                        Prompt = request.Prompt, Lyrics = lyrics, DurationSeconds = request.DurationSeconds, Bpm = request.Bpm,
                        Language = request.Language, KeyScale = request.KeyScale
                    }, seed, MusicWorkflow.CheckpointFileName);
            }
            string promptId = await QueueAsync(workflow, ct).ConfigureAwait(false);
            string path;
            try { path = await WaitForOutputAsync(promptId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                await InterruptAsync().ConfigureAwait(false);
                throw;
            }
            byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return new MediaAsset
            {
                FileName = Path.GetFileName(path),
                MediaType = "audio/flac",
                Base64Data = Convert.ToBase64String(bytes),
                Seed = seed,
                DurationSeconds = MusicWorkflow.NormalizeDuration(request.DurationSeconds),
                Steps = MusicWorkflow.Steps
            };
        }
        finally
        {
            // Same debounce as the other media jobs: a burst of songs keeps ComfyUI warm, then the chat model comes back.
            _comfy.ScheduleTextRestoreAfterFailedGeneration();
        }
    }

    /// <summary>Lyrics from the media assistant (already running for this job). Never blocks the song: any failure means an instrumental.</summary>
    private async Task<string> WriteLyricsAsync(MusicGenerateRequest request, CancellationToken ct)
    {
        try
        {
            string raw = await _chat!.CompleteOnceAsync(MusicLyrics.BuildPrompt(request.Prompt, request.Language), preferLowLatency: true, cancellationToken: ct)
                .ConfigureAwait(false);
            return MusicLyrics.Clean(raw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return string.Empty; }
    }

    private static async Task<string> QueueAsync(JsonObject workflow, CancellationToken ct)
    {
        using var content = new StringContent(workflow.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.PostAsync(MediaEndpoint + "/prompt", content, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("ComfyUI rejected the music workflow: " + Trim(body, 800));
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("node_errors", out JsonElement nodeErrors) && nodeErrors.ValueKind == JsonValueKind.Object && nodeErrors.EnumerateObject().Any())
            throw new InvalidOperationException("ComfyUI reported node errors for the music workflow: " + Trim(nodeErrors.ToString(), 800));
        string? id = root.TryGetProperty("prompt_id", out JsonElement idElement) ? idElement.GetString() : null;
        return string.IsNullOrWhiteSpace(id) ? throw new InvalidOperationException("ComfyUI did not return a prompt id for the music workflow.") : id;
    }

    private static async Task<string> WaitForOutputAsync(string promptId, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(700, ct).ConfigureAwait(false);
            using HttpResponseMessage response = await Http.GetAsync(MediaEndpoint + "/history/" + promptId, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;
            using JsonDocument history = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!history.RootElement.TryGetProperty(promptId, out JsonElement entry)) continue;
            if (entry.TryGetProperty("status", out JsonElement status)
                && status.TryGetProperty("status_str", out JsonElement statusString)
                && string.Equals(statusString.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ComfyUI failed to compose the music: " + Trim(status.ToString(), 800));
            string? path = FirstAudioOutput(entry);
            if (path != null) return path;
        }
        throw new TimeoutException("Music generation did not finish within " + Deadline.TotalMinutes + " minutes.");
    }

    /// <summary>Resolves the first saved audio file of a ComfyUI history entry to a path inside ComfyUI's output folder, or null while none exists.</summary>
    internal static string? FirstAudioOutput(JsonElement historyEntry)
    {
        if (!historyEntry.TryGetProperty("outputs", out JsonElement outputs) || outputs.ValueKind != JsonValueKind.Object) return null;
        string outputRoot = Path.GetFullPath(LocalAiPaths.ComfyOutputRoot);
        foreach (JsonProperty node in outputs.EnumerateObject())
        {
            if (!node.Value.TryGetProperty("audio", out JsonElement audio) || audio.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement file in audio.EnumerateArray())
            {
                string? name = file.TryGetProperty("filename", out JsonElement n) ? n.GetString() : null;
                string sub = file.TryGetProperty("subfolder", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty;
                string type = file.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "output" : "output";
                if (string.IsNullOrWhiteSpace(name) || type != "output") continue;
                string full = Path.GetFullPath(Path.Combine(outputRoot, sub, name));
                // Never follow a path ComfyUI reports outside its own output folder.
                if (!full.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    private static async Task InterruptAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var empty = new StringContent("{}", Encoding.UTF8, "application/json");
            await Http.PostAsync(MediaEndpoint + "/interrupt", empty, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { /* best effort */ }
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
