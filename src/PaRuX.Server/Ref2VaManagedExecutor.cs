using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// PaRuX-owned executor for the pinned Kablex/ComfyUI-Ref2VA-VSA source revision audited by the
/// media catalog. PaRuX installs source only. Model weights are never bundled or silently fetched:
/// each weight keeps its upstream licence and the engine becomes callable only when the user has
/// deliberately placed every required asset in PaRuX's own ComfyUI model tree.
/// </summary>
public sealed class Ref2VaManagedExecutor
{
    public const string SourceOwner = "Kablex";
    public const string SourceRepository = "ComfyUI-Ref2VA-VSA";
    public const string SourceRevision = MediaEngineCatalogService.Ref2VaSourceRevision;
    public const long MinimumDedicatedVramBytes = 23L * 1024 * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    private readonly LocalRuntimeService _runtime;
    private readonly LocalAiProcessManager _process;

    public Ref2VaManagedExecutor(LocalRuntimeService runtime, LocalAiProcessManager process)
    {
        _runtime = runtime;
        _process = process;
    }

    private static string NodeRoot => Path.Combine(LocalAiPaths.ComfyApp, "custom_nodes", "PaRuX-Ref2VA-VSA");
    private static string RevisionMarker => Path.Combine(NodeRoot, ".parux-source-revision");
    private static string WorkflowPath => Path.Combine(NodeRoot, "workflows", "ref2va_vsa_4step_api.json");
    private static string ModelsRoot => Path.Combine(LocalAiPaths.ComfyApp, "models");

    internal static readonly (string Folder, string File)[] RequiredAssets =
    {
        ("diffusion_models", "minimax_h3_ref2va_pruned_int8_convrot.safetensors"),
        ("loras", "fasth3_vsa_gate.safetensors"),
        ("loras", "minimax_h3_ref2v_turbo_4step_v0.1_comfyui_bf16.safetensors"),
        ("text_encoders", "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors"),
        ("vae", "minimax_h3_video_vae_fp16.safetensors"),
        ("vae", "minimax_h3_audio_vae_fp32.safetensors")
    };

    public static Ref2VaManagedStatus GetStatus()
    {
        string[] missing = RequiredAssets
            .Where(asset => !File.Exists(Path.Combine(ModelsRoot, asset.Folder, asset.File)))
            .Select(asset => asset.Folder + "/" + asset.File)
            .ToArray();

        bool sourcePinned = File.Exists(WorkflowPath)
            && File.Exists(RevisionMarker)
            && string.Equals(File.ReadAllText(RevisionMarker).Trim(), SourceRevision, StringComparison.OrdinalIgnoreCase);
        bool comfyReady = File.Exists(LocalAiPaths.ComfyPython) && File.Exists(LocalAiPaths.ComfyMain);

        GpuInventoryStatus gpu = new GpuInventoryService().GetStatus();
        bool hardwareEligible = gpu.Available
            && gpu.Adapters.Any(a => !a.IsSoftware && a.VendorId == 0x10DE
                && a.DedicatedVideoMemoryBytes >= MinimumDedicatedVramBytes);

        bool callable = sourcePinned && comfyReady && missing.Length == 0 && hardwareEligible;
        string reason = callable
            ? "Pinned Ref2VA source, all required local assets, PaRuX-managed ComfyUI and >=23 GiB NVIDIA VRAM are present."
            : BuildReason(sourcePinned, comfyReady, hardwareEligible, missing);

        return new Ref2VaManagedStatus
        {
            EngineId = MediaEngineCatalogService.Ref2VaEngineId,
            SourceRevision = SourceRevision,
            SourceInstalled = sourcePinned,
            ComfyReady = comfyReady,
            HardwareEligible = hardwareEligible,
            LargestDedicatedVramBytes = gpu.LargestDedicatedVideoMemoryBytes,
            MissingAssets = missing,
            Callable = callable,
            Ready = callable,
            Reason = reason
        };
    }

    public async Task<Ref2VaManagedStatus> InstallSourceAsync(CancellationToken ct)
    {
        if (!File.Exists(LocalAiPaths.ComfyPython) || !File.Exists(LocalAiPaths.ComfyMain))
            throw new InvalidOperationException("PaRuX-managed ComfyUI must be installed before Ref2VA source can be installed.");

        string staging = Path.Combine(LocalAiPaths.Root, "runtime", "managed-sources",
            "ref2va-vsa-" + SourceRevision);
        if (Directory.Exists(staging) && !File.Exists(Path.Combine(staging, "nodes.py")))
            Directory.Delete(staging, recursive: true);
        await ManagedSourceArchive.EnsureGithubCommitAsync(
            SourceOwner, SourceRepository, SourceRevision, staging, ct).ConfigureAwait(false);

        RequireSourceFile(staging, "LICENSE");
        RequireSourceFile(staging, "nodes.py");
        RequireSourceFile(staging, Path.Combine("workflows", "ref2va_vsa_4step_api.json"));

        string temp = NodeRoot + ".install-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(NodeRoot)!);
        try
        {
            CopyDirectory(staging, temp);
            await File.WriteAllTextAsync(Path.Combine(temp, ".parux-source-revision"), SourceRevision, ct)
                .ConfigureAwait(false);

            if (Directory.Exists(NodeRoot)) Directory.Delete(NodeRoot, recursive: true);
            Directory.Move(temp, NodeRoot);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { }
        }

        // Validate only packages the pinned node declares/uses. Never mutate a USER_MANAGED Python.
        ProcessResult validation = await RunCapturedAsync(LocalAiPaths.ComfyPython,
            new[] { "-c", "import torch,safetensors; print(torch.__version__); print(safetensors.__version__)" },
            ct, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        if (validation.ExitCode != 0)
            throw new InvalidOperationException("PaRuX-managed ComfyUI cannot import Ref2VA prerequisites: " + Trim(validation.Error));

        return GetStatus();
    }

    public async Task<Ref2VaGenerationResult> GenerateAsync(Ref2VaGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Ref2VaManagedStatus status = GetStatus();
        if (!status.Callable) throw new InvalidOperationException(status.Reason);
        if (_runtime.GamingMode) throw new InvalidOperationException("Gaming Mode is active, so Ref2VA generation is unavailable.");
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new InvalidDataException("prompt is required.");
        if (string.IsNullOrWhiteSpace(request.ReferenceImageBase64)) throw new InvalidDataException("referenceImageBase64 is required.");

        byte[] source;
        try { source = Convert.FromBase64String(request.ReferenceImageBase64); }
        catch (FormatException ex) { throw new InvalidDataException("referenceImageBase64 is not valid base64.", ex); }
        if (source.Length is < 8 or > 32 * 1024 * 1024)
            throw new InvalidDataException("Reference image must be between 8 bytes and 32 MiB.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _runtime.GamingCancellationToken);
        linked.CancelAfter(DefaultTimeout);
        CancellationToken runCt = linked.Token;

        await using MediaExecutionCoordinator.Lease lease = await MediaExecutionCoordinator.Current
            .AcquireAsync("media:ref2va", runCt).ConfigureAwait(false);
        await _runtime.PrepareForExclusiveMediaAsync(runCt).ConfigureAwait(false);
        if (_runtime.GamingMode) throw new OperationCanceledException("Gaming Mode became active.", runCt);

        LocalAiProcessManager.RuntimeResult start = await _process.StartMediaAsync(runCt).ConfigureAwait(false);
        if (!start.Success) throw new InvalidOperationException("Could not start PaRuX-managed ComfyUI: " + start.Message);

        string inputName = "parux_ref2va_" + Guid.NewGuid().ToString("N") + ".png";
        string uploaded = await UploadImageAsync(source, inputName, runCt).ConfigureAwait(false);
        JsonObject workflow = JsonNode.Parse(await File.ReadAllTextAsync(WorkflowPath, runCt).ConfigureAwait(false)) as JsonObject
            ?? throw new InvalidDataException("Pinned Ref2VA workflow is not a JSON object.");

        int width = NormalizeDimension(request.Width, 1344);
        int height = NormalizeDimension(request.Height, 768);
        int fps = Math.Clamp(request.Fps <= 0 ? 24 : request.Fps, 8, 60);
        double duration = Math.Clamp(request.DurationSeconds <= 0 ? 5.16 : request.DurationSeconds, 1, 20);
        int frames = NormalizeFrameCount((int)Math.Ceiling(duration * fps));
        long seed = request.Seed <= 0 ? Random.Shared.NextInt64(1, long.MaxValue) : request.Seed;

        SetIn(workflow, "6", "prompt", request.Prompt.Trim());
        SetIn(workflow, "6", "width", width);
        SetIn(workflow, "6", "height", height);
        SetIn(workflow, "6", "length", frames);
        SetIn(workflow, "50", "image", uploaded);
        SetIn(workflow, "8", "noise_seed", seed);
        SetIn(workflow, "15", "fps", fps);
        SetIn(workflow, "16", "filename_prefix", "video/PaRuX_Ref2VA");

        string promptId = await QueuePromptAsync(workflow, runCt).ConfigureAwait(false);
        using CancellationTokenRegistration interrupt = runCt.Register(static state =>
        {
            _ = InterruptBestEffortAsync((string)state!);
        }, "http://127.0.0.1:8188");

        string output = await WaitForOutputAsync(promptId, runCt).ConfigureAwait(false);
        if (!File.Exists(output)) throw new FileNotFoundException("Ref2VA completed but its output file is missing.", output);
        byte[] bytes = await File.ReadAllBytesAsync(output, runCt).ConfigureAwait(false);
        return new Ref2VaGenerationResult
        {
            FileName = Path.GetFileName(output),
            MediaType = Path.GetExtension(output).Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4",
            Base64Data = Convert.ToBase64String(bytes),
            Seed = seed,
            Width = width,
            Height = height,
            Fps = fps,
            Frames = frames,
            DurationSeconds = frames / (double)fps,
            SourceRevision = SourceRevision
        };
    }

    internal static int NormalizeFrameCount(int requested)
    {
        // MiniMax H3 expects video lengths in the 4n shape used by the pinned workflow (124).
        int clamped = Math.Clamp(requested, 8, 480);
        return Math.Max(8, clamped - (clamped % 4));
    }

    internal static int NormalizeDimension(int value, int fallback)
    {
        int normalized = Math.Clamp(value <= 0 ? fallback : value, 256, 1920);
        return normalized - normalized % 8;
    }

    private static string BuildReason(bool sourcePinned, bool comfyReady, bool hardwareEligible, string[] missing)
    {
        var reasons = new List<string>();
        if (!sourcePinned) reasons.Add("pinned Ref2VA source is not installed");
        if (!comfyReady) reasons.Add("PaRuX-managed ComfyUI is incomplete");
        if (missing.Length > 0) reasons.Add("missing local assets: " + string.Join(", ", missing));
        if (!hardwareEligible) reasons.Add("no NVIDIA adapter with at least 23 GiB dedicated VRAM was detected");
        return string.Join("; ", reasons) + ".";
    }

    private static void RequireSourceFile(string root, string relative)
    {
        if (!File.Exists(Path.Combine(root, relative)))
            throw new InvalidDataException("Pinned Ref2VA source archive is missing " + relative + ".");
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static async Task<string> UploadImageAsync(byte[] bytes, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        using var payload = new ByteArrayContent(bytes);
        payload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(payload, "image", fileName);
        form.Add(new StringContent("true"), "overwrite");
        using HttpResponseMessage response = await Http.PostAsync("http://127.0.0.1:8188/upload/image", form, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? fileName : fileName;
    }

    private static async Task<string> QueuePromptAsync(JsonObject workflow, CancellationToken ct)
    {
        using HttpResponseMessage response = await Http.PostAsJsonAsync("http://127.0.0.1:8188/prompt", new { prompt = workflow }, ct)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("ComfyUI rejected Ref2VA workflow: " + Trim(body));
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("prompt_id").GetString()
            ?? throw new InvalidDataException("ComfyUI did not return a prompt_id.");
    }

    private static async Task<string> WaitForOutputAsync(string promptId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using HttpResponseMessage response = await Http.GetAsync("http://127.0.0.1:8188/history/" + Uri.EscapeDataString(promptId), ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (doc.RootElement.TryGetProperty(promptId, out JsonElement item))
                {
                    if (TryFindOutputArtifact(item, out string? filename, out string? subfolder))
                    {
                        string root = LocalAiPaths.ComfyOutputRoot;
                        string path = string.IsNullOrWhiteSpace(subfolder)
                            ? Path.Combine(root, filename!) : Path.Combine(root, subfolder!, filename!);
                        string full = Path.GetFullPath(path);
                        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("ComfyUI returned an output path outside PaRuX's managed output directory.");
                        return full;
                    }
                    if (item.TryGetProperty("status", out JsonElement status)
                        && status.TryGetProperty("status_str", out JsonElement statusString)
                        && string.Equals(statusString.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Ref2VA workflow failed inside ComfyUI.");
                }
            }
            await Task.Delay(PollDelay, ct).ConfigureAwait(false);
        }
    }

    private static bool TryFindOutputArtifact(JsonElement root, out string? filename, out string? subfolder)
    {
        filename = null; subfolder = null;
        if (!root.TryGetProperty("outputs", out JsonElement outputs) || outputs.ValueKind != JsonValueKind.Object) return false;
        foreach (JsonProperty node in outputs.EnumerateObject())
        {
            if (node.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (string key in new[] { "videos", "gifs", "images", "files" })
            {
                if (!node.Value.TryGetProperty(key, out JsonElement list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement entry in list.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("filename", out JsonElement name)) continue;
                    filename = name.GetString();
                    subfolder = entry.TryGetProperty("subfolder", out JsonElement folder) ? folder.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(filename)) return true;
                }
            }
        }
        return false;
    }

    private static void SetIn(JsonObject workflow, string nodeId, string field, object value)
    {
        JsonObject node = workflow[nodeId] as JsonObject
            ?? throw new InvalidDataException("Pinned Ref2VA workflow is missing node " + nodeId + ".");
        JsonObject inputs = node["inputs"] as JsonObject
            ?? throw new InvalidDataException("Pinned Ref2VA workflow node " + nodeId + " has no inputs.");
        inputs[field] = value switch
        {
            string text => JsonValue.Create(text),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            bool flag => JsonValue.Create(flag),
            _ => throw new InvalidDataException("Unsupported Ref2VA workflow override type: " + value.GetType().Name)
        };
    }

    private static async Task InterruptBestEffortAsync(string endpoint)
    {
        try { using HttpResponseMessage _ = await Http.PostAsJsonAsync(endpoint + "/interrupt", new { }); }
        catch { }
    }

    private static async Task<ProcessResult> RunCapturedAsync(string executable, IReadOnlyList<string> args,
        CancellationToken ct, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static string Trim(string value)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= 800 ? text : text[..800] + "…";
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

public sealed class Ref2VaManagedStatus
{
    public string EngineId { get; set; } = string.Empty;
    public string SourceRevision { get; set; } = string.Empty;
    public bool SourceInstalled { get; set; }
    public bool ComfyReady { get; set; }
    public bool HardwareEligible { get; set; }
    public long LargestDedicatedVramBytes { get; set; }
    public string[] MissingAssets { get; set; } = Array.Empty<string>();
    public bool Callable { get; set; }
    public bool Ready { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class Ref2VaGenerationRequest
{
    public string Prompt { get; set; } = string.Empty;
    public string ReferenceImageBase64 { get; set; } = string.Empty;
    public long Seed { get; set; }
    public int Width { get; set; } = 1344;
    public int Height { get; set; } = 768;
    public int Fps { get; set; } = 24;
    public double DurationSeconds { get; set; } = 5.16;
}

public sealed class Ref2VaGenerationResult
{
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = "video/mp4";
    public string Base64Data { get; set; } = string.Empty;
    public long Seed { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public int Frames { get; set; }
    public double DurationSeconds { get; set; }
    public string SourceRevision { get; set; } = string.Empty;
}
