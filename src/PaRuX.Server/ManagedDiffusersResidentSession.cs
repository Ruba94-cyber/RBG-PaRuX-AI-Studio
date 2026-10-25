using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Scoped resident Diffusers worker used for explicit warm measurements and future managed media
/// sessions. Residency never outlives the caller's exclusive media lease: disposing the session
/// unloads the pipeline/process, and cancellation or Gaming Mode kills the worker rather than
/// leaving GPU memory resident behind the coordinator's back.
/// </summary>
public sealed class ManagedDiffusersResidentSession : IAsyncDisposable
{
    public const string WorkerVersion = "1";

    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly LocalRuntimeService _runtime;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private Task<string>? _stderrDrain;
    private CancellationTokenRegistration _gamingRegistration;
    private bool _ownsSessionGate;
    private bool _disposed;

    private static string Root => Path.Combine(PaRuXDataRoot.Path, "ai-runtimes", "media-diffusers-zimage");
    private static string VenvRoot => Path.Combine(Root, "venv");
    private static string ModelRoot => Path.Combine(Root, "model");
    private static string WorkerPath => Path.Combine(Root, "resident-worker.py");
    private static string OutputRoot => Path.Combine(PaRuXDataRoot.Path, "media", "diffusers-zimage");

    private ManagedDiffusersResidentSession(LocalRuntimeService runtime) => _runtime = runtime;

    public long LoadMilliseconds { get; private set; }
    public long LoadAllocatedBytes { get; private set; }
    public long LoadReservedBytes { get; private set; }
    public bool IsResident => !_disposed && _process is { HasExited: false } && _stdin != null && _stdout != null;

    public static async Task<ManagedDiffusersResidentSession> StartAsync(
        LocalRuntimeService runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.GamingMode)
            throw new InvalidOperationException("Gaming Mode is active, so a resident media worker cannot start.");
        if (MediaExecutionCoordinator.Current.Snapshot().ActiveOwner == null)
            throw new InvalidOperationException("A resident media worker requires an active exclusive media lease.");

        ManagedMediaEngineStatus installed = new ManagedDiffusersImageEngine(runtime).GetStatus();
        if (!installed.Ready)
            throw new InvalidOperationException(installed.Detail ?? "The managed Diffusers engine is not installed.");

        await SessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var session = new ManagedDiffusersResidentSession(runtime) { _ownsSessionGate = true };
        try
        {
            await session.StartWorkerAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ResidentRenderResult> GenerateAsync(MediaJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        if (_runtime.GamingMode)
            throw new InvalidOperationException("Gaming Mode is active, so media generation is unavailable.");
        if (!string.Equals((request.Type ?? string.Empty).Trim(), "image", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The resident Diffusers worker supports text-to-image jobs only.");
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new InvalidDataException("A media prompt is required.");

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!IsResident || _stdin == null || _stdout == null)
                throw new InvalidOperationException("The resident Diffusers worker is not available.");

            int width = MediaGenerationService.NormalizeDimension(request.Width, 1024);
            int height = MediaGenerationService.NormalizeDimension(request.Height, 1024);
            int steps = Math.Clamp(request.Steps <= 0 ? 9 : request.Steps, 4, 60);
            long seed = request.Seed <= 0 ? Random.Shared.NextInt64(1, long.MaxValue) : request.Seed;
            long maxArtifactBytes = Math.Max(1,
                ExecutionProfileStore.Current.Get(ManagedDiffusersImageEngine.ModelId).Effective.Budget.MaxArtifactBytes);
            string requestId = Guid.NewGuid().ToString("N");
            string outputPath = Path.Combine(OutputRoot,
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-warm-" + Guid.NewGuid().ToString("N")[..8] + ".png");
            Directory.CreateDirectory(OutputRoot);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _runtime.GamingCancellationToken);
            try
            {
                string command = JsonSerializer.Serialize(new
                {
                    apiVersion = WorkerVersion,
                    id = requestId,
                    op = "generate",
                    prompt = request.Prompt.Trim(),
                    width,
                    height,
                    steps,
                    seed,
                    output = outputPath
                });
                await _stdin.WriteLineAsync(command.AsMemory(), linked.Token).ConfigureAwait(false);
                await _stdin.FlushAsync(linked.Token).ConfigureAwait(false);

                string line = await ReadLineOrFailAsync(_stdout, linked.Token).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string eventId = root.TryGetProperty("id", out JsonElement idElement)
                    ? idElement.GetString() ?? string.Empty : string.Empty;
                if (!string.Equals(eventId, requestId, StringComparison.Ordinal))
                    throw new InvalidDataException("Resident Diffusers worker returned an event for the wrong request id.");

                string type = root.TryGetProperty("type", out JsonElement typeElement)
                    ? typeElement.GetString() ?? string.Empty : string.Empty;
                if (string.Equals(type, "error", StringComparison.Ordinal))
                {
                    string error = root.TryGetProperty("error", out JsonElement errorElement)
                        ? errorElement.GetString() ?? string.Empty : string.Empty;
                    throw new InvalidOperationException("Resident Diffusers generation failed: " + Trim(error, 1200));
                }
                if (!string.Equals(type, "done", StringComparison.Ordinal))
                    throw new InvalidDataException("Resident Diffusers worker returned unsupported event type '" + type + "'.");

                if (!File.Exists(outputPath))
                    throw new InvalidOperationException("Resident Diffusers worker completed without an output image.");
                long artifactBytes = new FileInfo(outputPath).Length;
                if (!MediaGenerationService.FitsArtifactBudget(artifactBytes, maxArtifactBytes))
                    throw new InvalidOperationException(
                        $"Generated resident Diffusers media is {artifactBytes:N0} bytes, exceeding the active execution profile artifact budget of {maxArtifactBytes:N0} bytes.");

                byte[] bytes = await File.ReadAllBytesAsync(outputPath, linked.Token).ConfigureAwait(false);
                (int actualWidth, int actualHeight) = TryReadPngSize(bytes) ?? (width, height);
                var asset = new MediaAsset
                {
                    FileName = Path.GetFileName(outputPath),
                    MediaType = "image/png",
                    Base64Data = Convert.ToBase64String(bytes),
                    Seed = seed,
                    Width = actualWidth,
                    Height = actualHeight,
                    Steps = steps
                };
                return new ResidentRenderResult(
                    asset,
                    GetInt64(root, "generationMilliseconds"),
                    GetInt64(root, "peakAllocatedBytes"),
                    GetInt64(root, "peakReservedBytes"));
            }
            catch (OperationCanceledException)
            {
                AbortWorker();
                throw;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                AbortWorker();
                throw new InvalidOperationException("Resident Diffusers protocol lost synchronization: " + ex.Message, ex);
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task StartWorkerAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(OutputRoot);
        await File.WriteAllTextAsync(WorkerPath, PythonWorker, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        string python = ManagedPythonRuntime.VenvPython(VenvRoot);
        if (!File.Exists(python)) throw new InvalidOperationException("The PaRuX Diffusers venv is unavailable.");
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
        info.ArgumentList.Add(WorkerPath);
        info.ArgumentList.Add(ModelRoot);
        ManagedPythonRuntime.ApplyEnvironment(info);
        info.Environment["HF_HOME"] = Path.Combine(Root, "hf-cache");
        info.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        info.Environment["DO_NOT_TRACK"] = "1";
        info.Environment["TOKENIZERS_PARALLELISM"] = "false";

        var process = new Process { StartInfo = info };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start the resident PaRuX Diffusers worker.");
        }
        ChildProcessJob.Assign(process);
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        _stderrDrain = process.StandardError.ReadToEndAsync(cancellationToken);
        _gamingRegistration = _runtime.GamingCancellationToken.Register(
            static state => ((ManagedDiffusersResidentSession)state!).AbortWorker(), this);

        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _runtime.GamingCancellationToken);
        readyCts.CancelAfter(ReadyTimeout);
        try
        {
            string line = await ReadLineOrFailAsync(_stdout, readyCts.Token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string type = root.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(type, "ready", StringComparison.Ordinal))
                throw new InvalidDataException("Resident Diffusers worker did not emit a ready event.");
            string revision = root.TryGetProperty("modelRevision", out JsonElement revisionElement)
                ? revisionElement.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(revision, ManagedDiffusersImageEngine.ModelRevision, StringComparison.Ordinal))
                throw new InvalidDataException("Resident Diffusers worker model revision does not match the pinned engine revision.");
            LoadMilliseconds = GetInt64(root, "loadMilliseconds");
            LoadAllocatedBytes = GetInt64(root, "allocatedBytes");
            LoadReservedBytes = GetInt64(root, "reservedBytes");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_runtime.GamingMode)
        {
            AbortWorker();
            throw new TimeoutException("The resident PaRuX Diffusers worker did not become ready within eight minutes.");
        }
        catch
        {
            AbortWorker();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _gamingRegistration.Dispose();

        Process? process = _process;
        StreamWriter? input = _stdin;
        _stdin = null;
        _stdout = null;
        if (process is { HasExited: false })
        {
            try
            {
                if (input != null)
                {
                    string shutdown = JsonSerializer.Serialize(new { apiVersion = WorkerVersion, op = "shutdown" });
                    await input.WriteLineAsync(shutdown).ConfigureAwait(false);
                    await input.FlushAsync().ConfigureAwait(false);
                }
                using var shutdownCts = new CancellationTokenSource(ShutdownTimeout);
                await process.WaitForExitAsync(shutdownCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
        try { process?.Dispose(); } catch { }
        _process = null;
        _stderrDrain = null;
        _requestGate.Dispose();
        if (_ownsSessionGate)
        {
            _ownsSessionGate = false;
            SessionGate.Release();
        }
    }

    private void AbortWorker()
    {
        Process? process = _process;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private async Task<string> ReadLineOrFailAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line != null) return line;
        string stderr = _stderrDrain is { IsCompletedSuccessfully: true } ? _stderrDrain.Result : string.Empty;
        throw new IOException("Resident Diffusers worker exited before completing the protocol event. " + Trim(stderr, 800));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ManagedDiffusersResidentSession));
    }

    private static long GetInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.TryGetInt64(out long value) ? value : 0;

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

    public sealed record ResidentRenderResult(
        MediaAsset Asset,
        long WorkerGenerationMilliseconds,
        long PeakAllocatedBytes,
        long PeakReservedBytes);

    internal const string PythonWorker = """
import json
import os
import sys
import time
import traceback

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["DO_NOT_TRACK"] = "1"
os.environ["TOKENIZERS_PARALLELISM"] = "false"


def emit(payload):
    print(json.dumps(payload, separators=(",", ":")), flush=True)


def main():
    if len(sys.argv) != 2:
        raise SystemExit("usage: resident-worker.py <model-dir>")

    import torch
    from diffusers import DiffusionPipeline

    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is not available to the resident PaRuX Diffusers worker")

    model_dir = sys.argv[1]
    load_started = time.perf_counter()
    pipe = DiffusionPipeline.from_pretrained(
        model_dir,
        dtype=torch.bfloat16,
        local_files_only=True,
        device_map="cuda",
    )
    torch.cuda.synchronize()
    emit({
        "type": "ready",
        "modelRevision": "f332072aa78be7aecdf3ee76d5c247082da564a6",
        "loadMilliseconds": round((time.perf_counter() - load_started) * 1000),
        "allocatedBytes": int(torch.cuda.memory_allocated()),
        "reservedBytes": int(torch.cuda.memory_reserved()),
    })

    for raw in sys.stdin:
        command = {}
        if not raw.strip():
            continue
        try:
            command = json.loads(raw)
            if str(command.get("apiVersion", "")) != "1":
                raise ValueError("unsupported worker apiVersion")
            op = command.get("op")
            if op == "shutdown":
                emit({"type": "stopped"})
                return
            if op != "generate":
                raise ValueError("unsupported worker operation")

            request_id = str(command.get("id", ""))
            if not request_id:
                raise ValueError("missing request id")
            torch.cuda.reset_peak_memory_stats()
            started = time.perf_counter()
            generator = torch.Generator(device="cuda").manual_seed(int(command["seed"]))
            result = pipe(
                str(command["prompt"]),
                width=int(command["width"]),
                height=int(command["height"]),
                num_inference_steps=int(command["steps"]),
                guidance_scale=0.0,
                max_sequence_length=1024,
                generator=generator,
            )
            result.images[0].save(str(command["output"]), format="PNG", optimize=False)
            torch.cuda.synchronize()
            emit({
                "id": request_id,
                "type": "done",
                "generationMilliseconds": round((time.perf_counter() - started) * 1000),
                "peakAllocatedBytes": int(torch.cuda.max_memory_allocated()),
                "peakReservedBytes": int(torch.cuda.max_memory_reserved()),
            })
        except Exception as exc:
            emit({
                "id": str(command.get("id", "")),
                "type": "error",
                "error": str(exc),
            })


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc(file=sys.stderr)
        raise
""";
}
