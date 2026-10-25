using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Independent semantic judge for image Engine Race. The judge reuses the already-managed
/// Diffusers Python environment but owns a separate, immutable CLIP snapshot. It runs on CPU so
/// quality measurement cannot perturb renderer VRAM, GPU clocks or render latency. Long prompts
/// are token-chunked so CLIP's short native text window cannot silently ignore trailing constraints.
/// A persistent worker is kept for one judge instance so both candidates use the exact same model.
/// </summary>
public sealed class ManagedClipImageJudge : IAsyncDisposable
{
    public const string JudgeId = "parux-clip-semantic";
    public const string JudgeVersion = "3";
    public const string ModelId = "openai/clip-vit-base-patch32";
    public const string ModelRevision = "b97b0100e55e367c057773c2a614676470b0d575";

    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private readonly SemaphoreSlim _workerGate = new(1, 1);
    private Process? _worker;
    private StreamWriter? _workerInput;
    private StreamReader? _workerOutput;
    private Task<string>? _workerErrors;

    private static string DiffusersRoot => Path.Combine(PaRuXDataRoot.Path, "ai-runtimes", "media-diffusers-zimage");
    private static string VenvRoot => Path.Combine(DiffusersRoot, "venv");
    private static string Root => Path.Combine(DiffusersRoot, "semantic-judge");
    private static string ModelRoot => Path.Combine(Root, "clip-vit-base-patch32");
    private static string RunnerPath => Path.Combine(Root, "clip_judge.py");
    private static string ReadyMarker => Path.Combine(Root, "ready.json");
    private static string ScratchRoot => Path.Combine(Root, "scratch");

    public ManagedClipJudgeStatus GetStatus()
    {
        bool runtimeReady = ManagedPythonRuntime.IsManagedVenv(VenvRoot)
            && File.Exists(ManagedPythonRuntime.VenvPython(VenvRoot));
        bool modelReady = File.Exists(Path.Combine(ModelRoot, "config.json"))
            && File.Exists(Path.Combine(ModelRoot, "model.safetensors"))
            && File.Exists(Path.Combine(ModelRoot, "preprocessor_config.json"))
            && File.Exists(Path.Combine(ModelRoot, "tokenizer_config.json"))
            && File.Exists(RunnerPath);
        bool markerReady = false;
        string? detail = null;
        try
        {
            if (File.Exists(ReadyMarker))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ReadyMarker));
                JsonElement root = document.RootElement;
                markerReady = string.Equals(root.GetProperty("judgeVersion").GetString(), JudgeVersion, StringComparison.Ordinal)
                    && string.Equals(root.GetProperty("modelRevision").GetString(), ModelRevision, StringComparison.Ordinal);
                if (!markerReady) detail = "Semantic judge files exist but the pinned judge manifest changed; reinstall/repair is required.";
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            detail = "Semantic judge state could not be verified: " + Trim(ex.Message, 240);
        }

        bool ready = runtimeReady && modelReady && markerReady;
        return new ManagedClipJudgeStatus
        {
            JudgeId = JudgeId,
            JudgeVersion = JudgeVersion,
            Ready = ready,
            ModelId = ModelId,
            ModelRevision = ModelRevision,
            Detail = ready
                ? "Ready. Prompt/image similarity runs locally on CPU with a pinned offline CLIP snapshot; long prompts are scored across bounded token chunks."
                : detail ?? (runtimeReady
                    ? "Install the semantic judge model before production Engine Race."
                    : "Install the managed Diffusers engine first; the semantic judge reuses its managed Python runtime.")
        };
    }

    public async Task<string> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        await InstallGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ManagedClipJudgeStatus current = GetStatus();
            if (current.Ready) return "Already installed and pinned.";
            if (!ManagedPythonRuntime.IsManagedVenv(VenvRoot) || !File.Exists(ManagedPythonRuntime.VenvPython(VenvRoot)))
                throw new InvalidOperationException("Install the PaRuX-managed Diffusers engine before installing its semantic race judge.");

            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(ModelRoot);
            Directory.CreateDirectory(ScratchRoot);
            await File.WriteAllTextAsync(RunnerPath, PythonRunner, new UTF8Encoding(false), ct).ConfigureAwait(false);

            progress?.Report("Downloading pinned CLIP semantic-judge snapshot...");
            string downloadScript =
                "from huggingface_hub import snapshot_download; " +
                "snapshot_download(repo_id=" + PyQuote(ModelId) + ", revision=" + PyQuote(ModelRevision) +
                ", local_dir=" + PyQuote(ModelRoot) +
                ", allow_patterns=['config.json','merges.txt','preprocessor_config.json','model.safetensors','special_tokens_map.json','tokenizer.json','tokenizer_config.json','vocab.json'], max_workers=4); " +
                "print('clip-snapshot-ready')";
            string download = await RunPythonOnceAsync(
                new[] { "-c", downloadScript }, ct, TimeSpan.FromMinutes(30), forceOffline: false).ConfigureAwait(false);

            if (!File.Exists(Path.Combine(ModelRoot, "model.safetensors"))
                || !File.Exists(Path.Combine(ModelRoot, "tokenizer_config.json")))
                throw new InvalidDataException("The semantic judge snapshot completed without the complete pinned CLIP files.");

            progress?.Report("Validating offline CLIP semantic judge...");
            string validation = await RunPythonOnceAsync(
                new[] { RunnerPath, "validate", ModelRoot }, ct, TimeSpan.FromMinutes(4), forceOffline: true).ConfigureAwait(false);

            string readyJson = JsonSerializer.Serialize(new
            {
                judgeId = JudgeId,
                judgeVersion = JudgeVersion,
                modelId = ModelId,
                modelRevision = ModelRevision,
                installedUtc = DateTime.UtcNow
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            string tempMarker = ReadyMarker + ".tmp";
            await File.WriteAllTextAsync(tempMarker, readyJson, new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(tempMarker, ReadyMarker, overwrite: true);

            if (!GetStatus().Ready)
                throw new InvalidOperationException("The managed CLIP semantic judge did not become ready after installation.");
            return Trim(download + "; " + validation, 2_000);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    /// <summary>
    /// Returns normalized cosine similarity in [0,1]. The worker is CPU-only and persistent for
    /// this instance. Prompt text longer than CLIP's native window is scored across deterministic
    /// token chunks; the aggregate keeps mean adherence dominant while penalizing the weakest chunk.
    /// </summary>
    public async Task<double> ScoreAsync(string prompt, MediaAsset asset, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidDataException("Semantic image judging requires the original prompt.");
        if (!GetStatus().Ready) throw new InvalidOperationException("The managed CLIP semantic judge is not ready.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(asset.Base64Data); }
        catch (FormatException) { throw new InvalidDataException("Semantic judge received invalid Base64 image data."); }
        if (bytes.Length == 0) throw new InvalidDataException("Semantic judge received an empty image.");

        Directory.CreateDirectory(ScratchRoot);
        string id = Guid.NewGuid().ToString("N");
        string imagePath = Path.Combine(ScratchRoot, id + ".png");
        await File.WriteAllBytesAsync(imagePath, bytes, ct).ConfigureAwait(false);

        await _workerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                await EnsureWorkerAsync(timeoutCts.Token).ConfigureAwait(false);
                string request = JsonSerializer.Serialize(new { id, prompt = prompt.Trim(), image = imagePath });
                await _workerInput!.WriteLineAsync(request.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                await _workerInput.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
                string? response = await _workerOutput!.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response))
                    throw new InvalidOperationException("CLIP semantic judge worker exited without a response.");

                using JsonDocument document = JsonDocument.Parse(response);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("error", out JsonElement error))
                    throw new InvalidOperationException("CLIP semantic judge failed: " + Trim(error.GetString(), 500));
                if (!root.TryGetProperty("score", out JsonElement scoreElement)
                    || !scoreElement.TryGetDouble(out double score)
                    || !double.IsFinite(score))
                    throw new InvalidDataException("CLIP semantic judge returned an invalid score.");
                return Math.Clamp(score, 0, 1);
            }
            catch (OperationCanceledException)
            {
                ResetWorker();
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("The CLIP semantic judge timed out.");
            }
            catch
            {
                ResetWorker();
                throw;
            }
        }
        finally
        {
            _workerGate.Release();
            try { File.Delete(imagePath); } catch { }
        }
    }

    private async Task EnsureWorkerAsync(CancellationToken ct)
    {
        if (_worker is { HasExited: false } && _workerInput != null && _workerOutput != null) return;
        ResetWorker();

        ProcessStartInfo info = CreatePythonInfo(forceOffline: true);
        info.ArgumentList.Add(RunnerPath);
        info.ArgumentList.Add("serve");
        info.ArgumentList.Add(ModelRoot);
        var process = new Process { StartInfo = info };
        process.Start();
        ChildProcessJob.Assign(process);
        _worker = process;
        _workerInput = process.StandardInput;
        _workerOutput = process.StandardOutput;
        _workerErrors = process.StandardError.ReadToEndAsync();

        string? readyLine = await _workerOutput.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(readyLine))
        {
            string errors = await ReadErrorsBestEffortAsync().ConfigureAwait(false);
            ResetWorker();
            throw new InvalidOperationException("CLIP semantic judge worker failed to start: " + Trim(errors, 800));
        }
        using JsonDocument document = JsonDocument.Parse(readyLine);
        if (!document.RootElement.TryGetProperty("ready", out JsonElement ready) || !ready.GetBoolean())
        {
            ResetWorker();
            throw new InvalidOperationException("CLIP semantic judge worker did not report ready state.");
        }
    }

    private static async Task<string> RunPythonOnceAsync(
        IReadOnlyList<string> arguments, CancellationToken ct, TimeSpan timeout, bool forceOffline)
    {
        ProcessStartInfo info = CreatePythonInfo(forceOffline);
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
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
            throw new TimeoutException("The managed CLIP judge operation timed out.");
        }
        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Managed CLIP judge failed: " + Trim(error.Length > 0 ? error : output, 1_200));
        return Trim(output, 2_000);
    }

    private static ProcessStartInfo CreatePythonInfo(bool forceOffline)
    {
        string python = ManagedPythonRuntime.VenvPython(VenvRoot);
        if (!File.Exists(python)) throw new InvalidOperationException("The PaRuX Diffusers managed Python environment is unavailable.");
        var info = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ManagedPythonRuntime.ApplyEnvironment(info);
        info.Environment["HF_HOME"] = Path.Combine(DiffusersRoot, "hf-cache");
        info.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        info.Environment["DO_NOT_TRACK"] = "1";
        info.Environment["TOKENIZERS_PARALLELISM"] = "false";
        if (forceOffline)
        {
            info.Environment["HF_HUB_OFFLINE"] = "1";
            info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        }
        return info;
    }

    private async Task<string> ReadErrorsBestEffortAsync()
    {
        if (_workerErrors == null) return string.Empty;
        try
        {
            Task completed = await Task.WhenAny(_workerErrors, Task.Delay(250)).ConfigureAwait(false);
            return completed == _workerErrors ? await _workerErrors.ConfigureAwait(false) : string.Empty;
        }
        catch { return string.Empty; }
    }

    private void ResetWorker()
    {
        Process? process = _worker;
        StreamWriter? input = _workerInput;
        StreamReader? output = _workerOutput;
        _worker = null;
        _workerInput = null;
        _workerOutput = null;
        _workerErrors = null;
        try { input?.Dispose(); } catch { }
        try { output?.Dispose(); } catch { }
        if (process == null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
        finally { process.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _workerGate.WaitAsync().ConfigureAwait(false);
        try { ResetWorker(); }
        finally
        {
            _workerGate.Release();
            _workerGate.Dispose();
        }
    }

    private static string PyQuote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static string Trim(string? value, int max)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private const string PythonRunner = """
import json
import os
import sys


def load(model_dir):
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    import torch
    from transformers import CLIPModel, CLIPProcessor
    torch.set_num_threads(max(1, min(8, os.cpu_count() or 1)))
    model = CLIPModel.from_pretrained(
        model_dir,
        local_files_only=True,
        use_safetensors=True,
    ).to("cpu")
    model.eval()
    processor = CLIPProcessor.from_pretrained(model_dir, local_files_only=True)
    return torch, model, processor


def validate(model_dir):
    torch, model, processor = load(model_dir)
    print("clip-ready;torch=" + torch.__version__ + ";model=" + model.config.model_type)


def split_prompt(processor, prompt, max_text_length):
    tokenizer = processor.tokenizer
    token_ids = tokenizer.encode(prompt, add_special_tokens=False)
    chunk_size = max(1, max_text_length - 2)
    if not token_ids:
        return [prompt]
    chunks = []
    for start in range(0, len(token_ids), chunk_size):
        text = tokenizer.decode(token_ids[start:start + chunk_size], skip_special_tokens=True).strip()
        if text:
            chunks.append(text)
    return chunks or [prompt]


def serve(model_dir):
    torch, model, processor = load(model_dir)
    from PIL import Image
    import torch.nn.functional as F
    max_text_length = int(model.config.text_config.max_position_embeddings)
    print(json.dumps({"ready": True, "max_text_length": max_text_length, "chunked": True}), flush=True)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        request_id = ""
        try:
            cfg = json.loads(line)
            request_id = str(cfg.get("id", ""))
            prompt = str(cfg["prompt"])
            chunks = split_prompt(processor, prompt, max_text_length)
            with Image.open(cfg["image"]) as opened:
                image = opened.convert("RGB")
                inputs = processor(
                    text=chunks,
                    images=[image] * len(chunks),
                    return_tensors="pt",
                    padding=True,
                    truncation=True,
                    max_length=max_text_length,
                )
            with torch.inference_mode():
                output = model(**inputs)
                image_features = F.normalize(output.image_embeds.float(), dim=-1)
                text_features = F.normalize(output.text_embeds.float(), dim=-1)
                cosines = (image_features * text_features).sum(dim=-1)
                mean_cosine = float(cosines.mean().item())
                weakest_cosine = float(cosines.min().item())
                aggregate_cosine = 0.75 * mean_cosine + 0.25 * weakest_cosine
            score = max(0.0, min(1.0, (aggregate_cosine + 1.0) / 2.0))
            print(json.dumps({
                "id": request_id,
                "score": score,
                "cosine": aggregate_cosine,
                "mean_cosine": mean_cosine,
                "weakest_cosine": weakest_cosine,
                "chunks": len(chunks),
            }), flush=True)
        except Exception as exc:
            print(json.dumps({"id": request_id, "error": str(exc)[:800]}), flush=True)


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    model_dir = sys.argv[2] if len(sys.argv) > 2 else ""
    if command == "validate" and model_dir:
        validate(model_dir)
    elif command == "serve" and model_dir:
        serve(model_dir)
    else:
        raise SystemExit("usage: clip_judge.py validate|serve <model_dir>")
""";
}

public sealed class ManagedClipJudgeStatus
{
    public string JudgeId { get; set; } = string.Empty;
    public string JudgeVersion { get; set; } = string.Empty;
    public bool Ready { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string ModelRevision { get; set; } = string.Empty;
    public string? Detail { get; set; }
}
