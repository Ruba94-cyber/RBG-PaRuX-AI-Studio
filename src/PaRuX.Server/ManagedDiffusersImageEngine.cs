using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// A second, genuinely independent text-to-image engine owned by PaRuX. It does not call ComfyUI:
/// inference runs directly through Hugging Face Diffusers in a dedicated PaRuX-managed Python/uv
/// environment. Installation is explicit because the pinned Z-Image-Turbo Diffusers snapshot is
/// large; once installed, generation is forced offline and can never fetch model/runtime files.
/// </summary>
public sealed class ManagedDiffusersImageEngine
{
    public const string EngineId = "parux-diffusers-zimage";
    public const string EngineVersion = "2";
    public const string DiffusersVersion = "0.40.0";
    public const string TransformersVersion = "5.16.1";
    public const string AccelerateVersion = "1.14.0";
    public const string TorchVersion = ExLlamaV3BuildCatalog.TorchVersion;
    public const string ModelId = "Tongyi-MAI/Z-Image-Turbo";
    // Immutable upstream revision. Do not silently change the model behind an installed engine.
    public const string ModelRevision = "f332072aa78be7aecdf3ee76d5c247082da564a6";

    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly SemaphoreSlim RenderGate = new(1, 1);
    private readonly LocalRuntimeService _runtime;

    private static string Root => Path.Combine(PaRuXDataRoot.Path, "ai-runtimes", "media-diffusers-zimage");
    private static string VenvRoot => Path.Combine(Root, "venv");
    private static string ModelRoot => Path.Combine(Root, "model");
    private static string RunnerPath => Path.Combine(Root, "runner.py");
    private static string ReadyMarker => Path.Combine(Root, "ready.json");
    private static string OutputRoot => Path.Combine(PaRuXDataRoot.Path, "media", "diffusers-zimage");

    public ManagedDiffusersImageEngine(LocalRuntimeService runtime) => _runtime = runtime;

    public ManagedMediaEngineStatus GetStatus()
    {
        bool runtimeInstalled = ManagedPythonRuntime.IsManagedVenv(VenvRoot)
            && File.Exists(ManagedPythonRuntime.VenvPython(VenvRoot))
            && File.Exists(RunnerPath);
        bool modelInstalled = File.Exists(Path.Combine(ModelRoot, "model_index.json"))
            && Directory.Exists(Path.Combine(ModelRoot, "scheduler"))
            && Directory.Exists(Path.Combine(ModelRoot, "transformer"))
            && Directory.Exists(Path.Combine(ModelRoot, "text_encoder"))
            && Directory.Exists(Path.Combine(ModelRoot, "tokenizer"))
            && Directory.Exists(Path.Combine(ModelRoot, "vae"));
        bool markerValid = false;
        string? detail = null;
        try
        {
            if (File.Exists(ReadyMarker))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ReadyMarker));
                JsonElement root = document.RootElement;
                markerValid = string.Equals(root.GetProperty("engineVersion").GetString(), EngineVersion, StringComparison.Ordinal)
                    && string.Equals(root.GetProperty("modelRevision").GetString(), ModelRevision, StringComparison.Ordinal)
                    && string.Equals(root.GetProperty("diffusersVersion").GetString(), DiffusersVersion, StringComparison.Ordinal)
                    && string.Equals(root.GetProperty("transformersVersion").GetString(), TransformersVersion, StringComparison.Ordinal)
                    && string.Equals(root.GetProperty("accelerateVersion").GetString(), AccelerateVersion, StringComparison.Ordinal)
                    && string.Equals(root.GetProperty("torchVersion").GetString(), TorchVersion, StringComparison.Ordinal);
                if (!markerValid) detail = "Runtime/model files exist but the pinned engine manifest changed; reinstall is required.";
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            detail = "Managed engine state could not be verified: " + Trim(ex.Message, 240);
        }
        bool ready = runtimeInstalled && modelInstalled && markerValid;
        return new ManagedMediaEngineStatus
        {
            EngineId = EngineId,
            EngineVersion = EngineVersion,
            RuntimeInstalled = runtimeInstalled,
            ModelInstalled = modelInstalled,
            Ready = ready,
            ModelId = ModelId,
            ModelRevision = ModelRevision,
            Detail = ready
                ? "Ready. Generation is pinned, local_files_only/offline, uses Z-Image-Turbo guidance_scale=0 and 1024-token prompt capacity."
                : detail ?? "Install the managed engine before racing it."
        };
    }

    public async Task<ManagedMediaEngineInstallResult> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (_runtime.GamingMode)
            throw new InvalidOperationException("Gaming Mode is active; installing a GPU media runtime is disabled.");

        await InstallGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ManagedMediaEngineStatus current = GetStatus();
            if (current.Ready)
                return new ManagedMediaEngineInstallResult { Status = current, Validation = "Already installed and pinned." };

            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(ModelRoot);
            Directory.CreateDirectory(OutputRoot);
            progress?.Report("Preparing PaRuX-managed Python...");
            await ManagedPythonRuntime.EnsureVenvAsync(
                VenvRoot, ManagedPythonRuntime.DefaultPythonVersion, ct).ConfigureAwait(false);

            progress?.Report("Installing pinned CUDA PyTorch " + TorchVersion + "...");
            await ManagedPythonRuntime.PipInstallAsync(
                VenvRoot,
                new[] { "torch==" + TorchVersion, "--index-url", ExLlamaV3BuildCatalog.TorchIndexUrl },
                ct, TimeSpan.FromMinutes(40)).ConfigureAwait(false);

            progress?.Report("Installing pinned Diffusers engine packages...");
            await ManagedPythonRuntime.PipInstallAsync(
                VenvRoot,
                new[]
                {
                    "diffusers==" + DiffusersVersion,
                    "transformers==" + TransformersVersion,
                    "accelerate==" + AccelerateVersion,
                    "huggingface-hub>=0.36,<2",
                    "safetensors>=0.5,<1",
                    "pillow>=11,<13",
                    "sentencepiece>=0.2,<1",
                    "protobuf>=5,<7"
                },
                ct, TimeSpan.FromMinutes(30)).ConfigureAwait(false);

            await File.WriteAllTextAsync(RunnerPath, PythonRunner, new UTF8Encoding(false), ct).ConfigureAwait(false);

            progress?.Report("Validating CUDA + Diffusers imports...");
            string validation = await RunPythonAsync(
                new[] { RunnerPath, "validate" },
                ct, TimeSpan.FromMinutes(3), forceOffline: false).ConfigureAwait(false);

            progress?.Report("Downloading pinned local Z-Image-Turbo snapshot (one-time, large download)...");
            string downloadScript =
                "from huggingface_hub import snapshot_download; " +
                "snapshot_download(repo_id=" + PyQuote(ModelId) + ", revision=" + PyQuote(ModelRevision) +
                ", local_dir=" + PyQuote(ModelRoot) +
                ", allow_patterns=['model_index.json','scheduler/*','text_encoder/*','tokenizer/*','transformer/*','vae/*'], max_workers=4); " +
                "print('snapshot-ready')";
            await RunPythonAsync(new[] { "-c", downloadScript }, ct, TimeSpan.FromHours(6), forceOffline: false)
                .ConfigureAwait(false);

            if (!File.Exists(Path.Combine(ModelRoot, "model_index.json"))
                || !Directory.Exists(Path.Combine(ModelRoot, "scheduler"))
                || !Directory.Exists(Path.Combine(ModelRoot, "tokenizer")))
                throw new InvalidDataException("The managed Diffusers model download completed without the complete pipeline files.");

            string readyJson = JsonSerializer.Serialize(new
            {
                engineId = EngineId,
                engineVersion = EngineVersion,
                modelId = ModelId,
                modelRevision = ModelRevision,
                diffusersVersion = DiffusersVersion,
                transformersVersion = TransformersVersion,
                accelerateVersion = AccelerateVersion,
                torchVersion = TorchVersion,
                installedUtc = DateTime.UtcNow
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            string tempMarker = ReadyMarker + ".tmp";
            await File.WriteAllTextAsync(tempMarker, readyJson, new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(tempMarker, ReadyMarker, overwrite: true);

            ManagedMediaEngineStatus status = GetStatus();
            if (!status.Ready) throw new InvalidOperationException(status.Detail ?? "Managed Diffusers engine did not become ready.");
            progress?.Report("PaRuX Diffusers engine installed. Engine Race now has an independent second renderer.");
            return new ManagedMediaEngineInstallResult { Status = status, Validation = validation };
        }
        finally { InstallGate.Release(); }
    }

    public async Task<MediaAsset> GenerateAsync(
        MediaJobRequest request,
        IProgress<(string Stage, int Percent, int? Eta)>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_runtime.GamingMode)
            throw new InvalidOperationException("Gaming Mode is active, so media generation is unavailable.");
        if (!string.Equals((request.Type ?? string.Empty).Trim(), "image", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The independent Diffusers engine currently supports text-to-image jobs only.");
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new InvalidDataException("A media prompt is required.");
        ManagedMediaEngineStatus status = GetStatus();
        if (!status.Ready) throw new InvalidOperationException(status.Detail ?? "Managed Diffusers engine is not installed.");

        int width = MediaGenerationService.NormalizeDimension(request.Width, 1024);
        int height = MediaGenerationService.NormalizeDimension(request.Height, 1024);
        int steps = Math.Clamp(request.Steps <= 0 ? 9 : request.Steps, 4, 60);
        long seed = request.Seed <= 0 ? Random.Shared.NextInt64(1, long.MaxValue) : request.Seed;
        ModelExecutionProfile profile = ExecutionProfileStore.Current.Get(ModelId).Effective;
        int timeoutMinutes = Math.Clamp(profile.Budget.MaxWallClockMinutes, 2, 120);
        long maxArtifactBytes = Math.Max(1, profile.Budget.MaxArtifactBytes);

        ExecutionResourceDecision resourceAdmission = await ExecutionResourceGuard.CheckCurrentAsync(
            profile,
            // This engine starts from a cold process and is normally entered only after the peer
            // renderer released VRAM. Temperature and system RAM remain strict; current VRAM can
            // legitimately still include a transient driver allocation during the handoff.
            enforceCurrentVram: false,
            ct).ConfigureAwait(false);
        if (!resourceAdmission.Allowed) throw new InvalidOperationException(resourceAdmission.Message);

        await RenderGate.WaitAsync(ct).ConfigureAwait(false);
        string requestPath = Path.Combine(Root, "request-" + Guid.NewGuid().ToString("N") + ".json");
        string outputPath = Path.Combine(OutputRoot,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        try
        {
            Directory.CreateDirectory(OutputRoot);
            string json = JsonSerializer.Serialize(new
            {
                model_dir = ModelRoot,
                prompt = request.Prompt.Trim(),
                width,
                height,
                steps,
                seed,
                output = outputPath
            });
            await File.WriteAllTextAsync(requestPath, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
            progress?.Report(("Diffusers · loading local Z-Image", 10, null));
            await RunPythonAsync(
                new[] { RunnerPath, "generate", requestPath },
                ct, TimeSpan.FromMinutes(timeoutMinutes), forceOffline: true).ConfigureAwait(false);
            progress?.Report(("Diffusers · finalizing", 95, 2));

            if (!File.Exists(outputPath))
                throw new InvalidOperationException("The managed Diffusers process completed without an output image.");
            long artifactBytes = new FileInfo(outputPath).Length;
            if (!MediaGenerationService.FitsArtifactBudget(artifactBytes, maxArtifactBytes))
                throw new InvalidOperationException(
                    $"Generated Diffusers media is {artifactBytes:N0} bytes, exceeding the active execution profile artifact budget of {maxArtifactBytes:N0} bytes.");

            byte[] bytes = await File.ReadAllBytesAsync(outputPath, ct).ConfigureAwait(false);
            (int actualWidth, int actualHeight) = TryReadPngSize(bytes) ?? (width, height);
            return new MediaAsset
            {
                FileName = Path.GetFileName(outputPath),
                MediaType = "image/png",
                Base64Data = Convert.ToBase64String(bytes),
                Seed = seed,
                Width = actualWidth,
                Height = actualHeight,
                Steps = steps
            };
        }
        finally
        {
            try { File.Delete(requestPath); } catch { }
            RenderGate.Release();
        }
    }

    private static async Task<string> RunPythonAsync(
        IReadOnlyList<string> arguments, CancellationToken ct, TimeSpan timeout, bool forceOffline)
    {
        string python = ManagedPythonRuntime.VenvPython(VenvRoot);
        if (!File.Exists(python)) throw new InvalidOperationException("The PaRuX Diffusers venv is unavailable.");
        var info = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        ManagedPythonRuntime.ApplyEnvironment(info);
        info.Environment["HF_HOME"] = Path.Combine(Root, "hf-cache");
        info.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        info.Environment["DO_NOT_TRACK"] = "1";
        info.Environment["TOKENIZERS_PARALLELISM"] = "false";
        if (forceOffline)
        {
            info.Environment["HF_HUB_OFFLINE"] = "1";
            info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        }

        using var process = new Process { StartInfo = info };
        process.Start();
        ChildProcessJob.Assign(process);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("The PaRuX-managed Diffusers operation timed out.");
        }
        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("PaRuX Diffusers engine failed: " + Trim(error.Length > 0 ? error : output, 1200));
        return Trim(output, 2000);
    }

    private static string PyQuote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static (int Width, int Height)? TryReadPngSize(byte[] bytes)
    {
        if (bytes.Length < 24) return null;
        ReadOnlySpan<byte> signature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (!bytes.AsSpan(0, 8).SequenceEqual(signature)) return null;
        int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static string Trim(string? value, int max)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private const string PythonRunner = """
import json
import os
import sys


def validate():
    import torch
    import diffusers
    import transformers
    import accelerate
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is not available to PaRuX-managed PyTorch")
    print("torch=" + torch.__version__ + ";cuda=" + str(torch.version.cuda)
          + ";diffusers=" + diffusers.__version__
          + ";transformers=" + transformers.__version__
          + ";accelerate=" + accelerate.__version__)


def generate(request_path):
    # Generation is deliberately offline. If a dependency/model file is missing, fail instead of
    # making an undeclared network request and turning an Engine Race into a network benchmark.
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    import torch
    from diffusers import DiffusionPipeline

    with open(request_path, "r", encoding="utf-8") as stream:
        cfg = json.load(stream)
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA became unavailable before Z-Image generation")

    # Official Z-Image-Turbo semantics: guidance_scale=0 and num_inference_steps=9 correspond to
    # the intended turbo path (eight DiT forwards). The larger sequence budget avoids silently
    # truncating detailed prompts produced by PaRuX's shared media prompt preparation.
    generator = torch.Generator(device="cuda").manual_seed(int(cfg["seed"]))
    pipe = DiffusionPipeline.from_pretrained(
        cfg["model_dir"],
        dtype=torch.bfloat16,
        local_files_only=True,
        device_map="cuda",
    )
    result = pipe(
        cfg["prompt"],
        width=int(cfg["width"]),
        height=int(cfg["height"]),
        num_inference_steps=int(cfg["steps"]),
        guidance_scale=0.0,
        max_sequence_length=1024,
        generator=generator,
    )
    image = result.images[0]
    image.save(cfg["output"], format="PNG", optimize=False)
    print(cfg["output"])


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "validate":
        validate()
    elif command == "generate" and len(sys.argv) == 3:
        generate(sys.argv[2])
    else:
        raise SystemExit("usage: runner.py validate | runner.py generate <request.json>")
""";
}
