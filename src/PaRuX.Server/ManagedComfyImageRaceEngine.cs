using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Race-only ComfyUI image path. Engine Race compares renderers, not unrelated prompt-assistant
/// startup. The timed candidate therefore starts ComfyUI only, from an explicitly empty PaRuX text/
/// media residency, and receives the exact same prompt as the independent Diffusers candidate.
/// Gaming Mode remains a hard-off even though this path deliberately bypasses StartMediaAsync.
/// </summary>
public sealed class ManagedComfyImageRaceEngine
{
    public const string EngineId = "comfyui";
    public const string EngineVersion = "managed-race-v9";
    public const int SamplerSteps = 8;

    private const string MediaEndpoint = "http://127.0.0.1:8188";
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly LocalAiProcessManager _process;
    private readonly LocalRuntimeService _runtime;

    public ManagedComfyImageRaceEngine(LocalAiProcessManager process, LocalRuntimeService runtime)
    {
        _process = process;
        _runtime = runtime;
    }

    public async Task EstablishColdBoundaryAsync(CancellationToken ct)
    {
        ThrowIfGaming();
        // The HTTP endpoint owns MediaExecutionCoordinator's exclusive writer lease before entering
        // the race. That writer blocks new text readers; this call lets every already-admitted
        // llama.cpp/ExLlama response finish before anything is stopped for the cold benchmark.
        await _runtime.PrepareForExclusiveMediaAsync(ct).ConfigureAwait(false);
        ThrowIfGaming();
        ReleaseGpuForPeer();
    }

    public async Task<MediaAsset> GeneratePreparedAsync(
        string prompt,
        MediaJobRequest request,
        long seed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidDataException("A race prompt is required.");
        ThrowIfGaming();

        int width = MediaGenerationService.NormalizeDimension(request.Width, 1024);
        int height = MediaGenerationService.NormalizeDimension(request.Height, 1024);
        bool completed = false;
        await EnsureRendererOnlyReadyAsync(ct).ConfigureAwait(false);
        try
        {
            string path = await SubmitAndWaitAsync(prompt.Trim(), seed, width, height, ct).ConfigureAwait(false);
            if (!File.Exists(path)) throw new FileNotFoundException("ComfyUI race output could not be found.", path);
            byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var asset = new MediaAsset
            {
                FileName = Path.GetFileName(path),
                MediaType = "image/png",
                Base64Data = Convert.ToBase64String(bytes),
                Seed = seed,
                Width = width,
                Height = height,
                Steps = SamplerSteps
            };
            completed = true;
            return asset;
        }
        finally
        {
            if (completed) ReleaseGpuForPeer();
            else ReleaseGpuBestEffort();
        }
    }

    public void RestoreTextProfileInBackground()
    {
        // The race endpoint still owns its exclusive media writer when this task is scheduled. The
        // centralized restore acquires a new exclusive transition after that writer is released,
        // then drains any intervening chat/model activity instead of switching the resident under it.
        if (_runtime.GamingMode) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _runtime.RestoreDefaultTextExclusivelyAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { }
        });
    }

    public void ReleaseGpuForPeer()
    {
        bool mediaStopped = _process.StopMedia();
        bool textStopped = _process.StopText();
        if (!mediaStopped || !textStopped)
            throw new InvalidOperationException(
                "Could not release the complete ComfyUI/text GPU residency before the peer race candidate.");
    }

    public void ReleaseGpuBestEffort()
    {
        try { _process.StopMedia(); } catch { }
        try { _process.StopText(); } catch { }
    }

    private async Task EnsureRendererOnlyReadyAsync(CancellationToken ct)
    {
        ThrowIfGaming();
        await _process.WaitForIdleAsync(ct).ConfigureAwait(false);
        ThrowIfGaming();
        if (!await _process.HasHeavyGpuAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("No sufficiently powerful GPU detected; ComfyUI Engine Race requires one.");

        bool textStopped = _process.StopText();
        bool mediaStopped = _process.StopMedia();
        if (!textStopped || !mediaStopped)
            throw new InvalidOperationException("Could not establish an empty GPU residency before cold ComfyUI race startup.");
        ThrowIfGaming();
        if (!File.Exists(LocalAiPaths.ComfyPython) || !File.Exists(LocalAiPaths.ComfyMain))
            throw new InvalidOperationException("ComfyUI is incomplete at " + LocalAiPaths.ComfyRoot);

        var info = new ProcessStartInfo
        {
            FileName = LocalAiPaths.ComfyPython,
            WorkingDirectory = LocalAiPaths.ComfyApp,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "-s", LocalAiPaths.ComfyMain, "--windows-standalone-build", "--listen", "127.0.0.1",
            "--port", "8188", "--disable-auto-launch", "--preview-method", "auto",
            "--enable-dynamic-vram", "--reserve-vram", "1.0"
        }) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Windows could not start the cold ComfyUI race renderer.");
        ChildProcessJob.Assign(process);
        Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.MediaPidFile)!);
        await File.WriteAllTextAsync(
            LocalAiPaths.MediaPidFile,
            process.Id.ToString(CultureInfo.InvariantCulture),
            new UTF8Encoding(false),
            ct).ConfigureAwait(false);
        if (!LocalAiProcessManager.RecordManagedProcess(LocalAiPaths.MediaPidFile, process))
            throw new InvalidOperationException("Windows did not report the cold ComfyUI race renderer's start time, so it was stopped rather than tracked unverified.");

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfGaming();
            if (process.HasExited)
            {
                int exitCode = process.ExitCode;
                _process.StopMedia();
                throw new InvalidOperationException(
                    "Cold ComfyUI race renderer exited during startup with code " + exitCode + ".");
            }

            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(2));
                using HttpResponseMessage response = await Http.GetAsync(
                    MediaEndpoint + "/system_stats", probeCts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (HttpRequestException) { }
            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        _process.StopMedia();
        throw new TimeoutException("Timed out waiting for the cold ComfyUI race renderer to become healthy.");
    }

    private void ThrowIfGaming()
    {
        if (_runtime.GamingMode)
            throw new InvalidOperationException("Gaming Mode is active, so media Engine Race is unavailable.");
    }

    private async Task<string> SubmitAndWaitAsync(
        string prompt,
        long seed,
        int width,
        int height,
        CancellationToken ct)
    {
        ConfigAssets.EnsureAll();
        string templatePath = LocalAiPaths.ImageWorkflowTemplate;
        if (!File.Exists(templatePath)) throw new InvalidOperationException("Missing workflow template: " + templatePath);
        JsonObject request = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(templatePath, ct).ConfigureAwait(false))!;
        request["client_id"] = "parux-race-" + Guid.NewGuid().ToString("N");

        SetIn(request, "4", "text", prompt);
        SetIn(request, "8", "seed", seed);
        SetIn(request, "8", "steps", SamplerSteps);
        SetIn(request, "6", "width", width);
        SetIn(request, "6", "height", height);
        SetIn(request, "10", "filename_prefix", "LocalAI_images/PaRuX_EngineRace");
        MediaCheckpointEffective effective = MediaCheckpointResolution.ResolveForJob("image");
        if (effective.IsOverridden) SetIn(request, MediaCheckpointResolution.UnetNode, "unet_name", effective.File);
        // Registry-resolved companion names apply here too, so the race graph matches the sequential media path.
        ComfyWorkflowRunner.ApplyResolvedCompanionChoices(request, "image");
        // Same live preflight as the sequential media path, so the race refuses a missing file or node with
        // the same typed reason before queueing instead of surfacing ComfyUI's raw node error text.
        await ComfyLivePreflight.EnsureSubmittableAsync(Http, new Uri(MediaEndpoint + "/"), request, ct,
            MediaCompanionRegistry.Shipped.MinimumComfyUiVersionFor(Path.GetFileName(templatePath)),
            _process.GetMediaProcessIdentityKey()).ConfigureAwait(false);

        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage queuedResponse = await Http.PostAsync(MediaEndpoint + "/prompt", content, ct).ConfigureAwait(false);
        string queuedBody = await queuedResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!queuedResponse.IsSuccessStatusCode)
        {
            ComfyLivePreflight.ThrowTypedRefusal(queuedBody);
            throw new InvalidOperationException("ComfyUI rejected the race workflow: " + Trim(queuedBody, 1000));
        }

        using JsonDocument queuedDocument = JsonDocument.Parse(queuedBody);
        JsonElement queued = queuedDocument.RootElement;
        if (!queued.TryGetProperty("prompt_id", out JsonElement idElement)
            || string.IsNullOrWhiteSpace(idElement.GetString()))
            throw new InvalidOperationException("ComfyUI did not return a prompt id for the race workflow.");
        if (queued.TryGetProperty("node_errors", out JsonElement nodeErrors)
            && nodeErrors.ValueKind == JsonValueKind.Object
            && nodeErrors.EnumerateObject().Any())
        {
            ComfyLivePreflight.ThrowTypedRefusal(queuedBody);
            throw new InvalidOperationException("ComfyUI race workflow has node errors: " + Trim(nodeErrors.ToString(), 1000));
        }

        string promptId = idElement.GetString()!;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400, ct).ConfigureAwait(false);
            using HttpResponseMessage historyResponse = await Http.GetAsync(
                MediaEndpoint + "/history/" + promptId, ct).ConfigureAwait(false);
            if (!historyResponse.IsSuccessStatusCode) continue;

            string historyBody = await historyResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument historyDocument = JsonDocument.Parse(historyBody);
            JsonElement history = historyDocument.RootElement;
            if (!history.TryGetProperty(promptId, out JsonElement entry)) continue;
            if (!entry.TryGetProperty("status", out JsonElement status)) continue;

            if (status.TryGetProperty("status_str", out JsonElement statusString)
                && string.Equals(statusString.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ComfyUI race generation failed: " + Trim(status.ToString(), 1000));

            if (!status.TryGetProperty("completed", out JsonElement completedState) || !completedState.GetBoolean()) continue;
            JsonElement outputs = entry.GetProperty("outputs");
            if (!outputs.TryGetProperty("10", out JsonElement saveOutput)
                || !saveOutput.TryGetProperty("images", out JsonElement images)
                || images.ValueKind != JsonValueKind.Array
                || images.GetArrayLength() == 0)
                throw new InvalidOperationException("ComfyUI race generation completed without an image output.");

            JsonElement asset = images[0];
            string? filename = asset.TryGetProperty("filename", out JsonElement fileElement)
                ? fileElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(filename))
                throw new InvalidOperationException("ComfyUI race output did not contain a filename.");
            string subfolder = asset.TryGetProperty("subfolder", out JsonElement subElement)
                ? subElement.GetString() ?? string.Empty : string.Empty;
            return Path.Combine(LocalAiPaths.ComfyOutputRoot, subfolder, filename);
        }

        throw new TimeoutException("Timed out waiting for ComfyUI Engine Race generation to complete.");
    }

    private static void SetIn(JsonObject request, string node, string field, object value)
    {
        JsonObject inputs = (JsonObject)request["prompt"]![node]!["inputs"]!;
        inputs[field] = value switch
        {
            string s => JsonValue.Create(s),
            long l => JsonValue.Create(l),
            int i => JsonValue.Create(i),
            double d => JsonValue.Create(d),
            _ => throw new ArgumentException("Unsupported race workflow value type: " + value.GetType())
        };
    }

    private static string Trim(string? value, int max)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
