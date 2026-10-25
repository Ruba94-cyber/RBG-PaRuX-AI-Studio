using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// PaRuX-managed local Training Lab. Python/uv/venv and every dependency live below PaRuXDataRoot;
/// no system-Python fallback exists. SFT, DPO, continued pretraining, GRPO, Vision SFT and SpeechT5
/// TTS SFT all execute through the same durable job surface and Gaming Mode cancellation boundary.
/// </summary>
public sealed class TrainingLabService
{
    internal sealed class RecentOutputBuffer
    {
        internal const int MaxLines = 40;
        private readonly object _gate = new();
        private readonly Queue<string> _lines = new();

        public void Record(string line)
        {
            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxLines) _lines.Dequeue();
            }
        }

        public string? Tail()
        {
            lock (_gate)
            {
                return _lines.Count == 0 ? null : string.Join('\n', _lines);
            }
        }
    }

    private sealed class Entry
    {
        public readonly object Gate = new();
        public readonly TrainingJobStatus Status;
        public readonly int MaxWallClockMinutes;
        public readonly TrainingJobRequest? Request;
        public readonly string? DatasetSha256;
        public readonly string? RuntimeFingerprint;
        public Process? Process;
        public readonly RecentOutputBuffer RecentOutput = new();

        public Entry(
            TrainingJobStatus status,
            int maxWallClockMinutes,
            TrainingJobRequest? request = null,
            string? datasetSha256 = null,
            string? runtimeFingerprint = null)
        {
            Status = status;
            MaxWallClockMinutes = maxWallClockMinutes;
            Request = request;
            DatasetSha256 = datasetSha256;
            RuntimeFingerprint = runtimeFingerprint;
        }
    }

    private static readonly Regex StepProgress = new(
        @"^\s*(?<current>\d+)%\|.*?\|\s*(?<step>\d+)/(?<total>\d+)", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, Entry> _jobs = new();
    private readonly LocalRuntimeService _runtime;
    private readonly AdapterManagerService _adapters;

    public TrainingLabService(LocalRuntimeService runtime, AdapterManagerService adapters)
    {
        _runtime = runtime;
        _adapters = adapters;
        RecoverPersistedJobs();
    }

    private static string RuntimeRoot => Path.Combine(PaRuXDataRoot.Path, "ai-runtimes", "training-lab");
    private static string VenvRoot => Path.Combine(RuntimeRoot, ".venv");
    private static string VenvPython => ManagedPythonRuntime.VenvPython(VenvRoot);
    private static string TrainScriptPath => Path.Combine(RuntimeRoot, "parux_train.py");

    /// <summary>
    /// One probe, one JSON line. Importing unsloth is slow and noisy - it prints its own banner and
    /// patches transformers on the way in - so the payload is fenced with a marker rather than
    /// assumed to be the whole of stdout, and a missing package is reported as a null field instead
    /// of failing the probe outright.
    /// </summary>
    private const string EngineProbeScript = """
import json, sys

def version_of(name):
    try:
        module = __import__(name)
        return getattr(module, "__version__", "unknown")
    except Exception:
        return None

torch_version = version_of("torch")
cuda_available = False
device = None
if torch_version is not None:
    try:
        import torch
        cuda_available = bool(torch.cuda.is_available())
        if cuda_available:
            device = torch.cuda.get_device_name(0)
    except Exception:
        cuda_available = False

payload = {
    "python": sys.version.split()[0],
    "torch": torch_version,
    "cudaAvailable": cuda_available,
    "device": device,
    "transformers": version_of("transformers"),
    "trl": version_of("trl"),
    "unsloth": version_of("unsloth"),
}
print("PARUX_ENGINE_PROBE " + json.dumps(payload))
""";

    internal static string ExtractProbeJson(string stdout)
    {
        const string marker = "PARUX_ENGINE_PROBE ";
        int index = stdout.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return string.Empty;
        string tail = stdout[(index + marker.Length)..].Trim();
        int newline = tail.IndexOf('\n');
        return (newline >= 0 ? tail[..newline] : tail).Trim();
    }

    private static string? ReadProbeString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static string Truncate(string value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }

    public async Task<TrainingEngineStatus> GetEngineStatusAsync(CancellationToken ct)
    {
        if (!File.Exists(VenvPython)) return new TrainingEngineStatus { Installed = false };

        // Provenance before capability. A venv can import the whole stack perfectly and still be one
        // PaRuX will not run: Training Lab admits only environments built on its own managed Python,
        // never the system interpreter. This machine carries a legacy venv from the product's former
        // name, built on user-installed Python 3.13 - the job gate refuses it, correctly, and this
        // status must say the same thing rather than advertising a runtime that cannot start a job.
        if (!ManagedPythonRuntime.IsManagedVenv(VenvRoot))
        {
            return new TrainingEngineStatus
            {
                Installed = false,
                Detail = "A Python environment exists at " + VenvRoot + " but it was not built on "
                    + "PaRuX's managed Python, so Training Lab will not run jobs on it. "
                    + "Install the managed training runtime to replace it."
            };
        }

        try
        {
            // The probe used to report only torch and CUDA, leaving Ready, PythonVersion,
            // UnslothVersion and Backend at their defaults - so an installed, CUDA-verified engine
            // answered "ready: false, unslothVersion: null, backend: unknown" and read, to anyone
            // looking, as an engine that could not train. It reports what it actually found now.
            (int exitCode, string stdout, string stderr) = await RunCapturedAsync(VenvPython,
                new[] { "-c", EngineProbeScript },
                ct, TimeSpan.FromSeconds(180));

            string payload = ExtractProbeJson(stdout);
            if (exitCode != 0 || payload.Length == 0)
            {
                return new TrainingEngineStatus
                {
                    Installed = true,
                    Detail = Truncate(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr, 600)
                };
            }

            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            string? torchVersion = ReadProbeString(root, "torch");
            bool cudaAvailable = root.TryGetProperty("cudaAvailable", out JsonElement cudaNode)
                && cudaNode.ValueKind == JsonValueKind.True;
            string? unslothVersion = ReadProbeString(root, "unsloth");
            bool trlPresent = root.TryGetProperty("trl", out JsonElement trlNode)
                && trlNode.ValueKind == JsonValueKind.String;

            // Ready means a training run would actually start: the pinned stack imports and an
            // accelerator is visible to torch. Without one, Unsloth itself refuses the run.
            bool ready = torchVersion != null && unslothVersion != null && trlPresent && cudaAvailable;

            return new TrainingEngineStatus
            {
                Installed = true,
                Ready = ready,
                PythonVersion = ReadProbeString(root, "python"),
                UnslothVersion = unslothVersion,
                TorchVersion = torchVersion,
                CudaVerified = cudaAvailable,
                Backend = cudaAvailable ? "cuda" : "cpu",
                Detail = ready
                    ? ReadProbeString(root, "device") ?? string.Empty
                    : "No accelerator is visible to torch in the managed training runtime; "
                      + "local fine-tuning needs one.",
                AvailableMethods = ready ? TrainingMethod.All.ToList() : new List<string>()
            };
        }
        catch (Exception exception)
        {
            return new TrainingEngineStatus
            {
                Installed = true,
                Detail = Truncate("Training engine probe failed: " + exception.Message, 600)
            };
        }
    }

    /// <summary>
    /// Removes the managed training runtime and reports what it reclaimed.
    ///
    /// Installing it is a multi-gigabyte decision - torch alone is over 4 GB of the roughly 5 GB
    /// environment - and there was no way to undo it from inside PaRuX: once a user had tried local
    /// fine-tuning once, the disk was spent for good. A local-first product that provisions large
    /// third-party stacks on demand has to be able to give the disk back.
    ///
    /// It refuses while a job is running rather than deleting the interpreter out from under it.
    /// </summary>
    /// <summary>Samples per optimizer step. Kept constant while the split between real batching and
    /// accumulation changes, so tuning for the GPU never alters training dynamics.</summary>
    private const int EffectiveBatchSize = 4;

    /// <summary>
    /// Splits the effective batch between the GPU and gradient accumulation.
    ///
    /// Every method used to run at per_device_train_batch_size=1 with 4 accumulation steps whatever
    /// the hardware, which processes the four samples one at a time. Measured here on an RTX 5080,
    /// same Unsloth kernels, same 30 optimizer steps, same four samples per step: 49.1 s at 1×4
    /// against 18.5 s at 4×1 - **2.65x faster for identical arithmetic**, at 0.93 GB instead of
    /// 0.56 GB of a 16 GB card. Accumulation exists to fit a batch that does not fit; using it when
    /// the batch does fit simply serialises work the GPU could have done at once.
    /// </summary>
    internal static (int MicroBatch, int GradientAccumulation) PlanBatchShape(
        int maxSequenceLength,
        long? availableVramBytesOverride = null)
    {
        long vram = availableVramBytesOverride ?? LargestGpuVideoMemoryBytes();
        if (vram <= 0) return (1, EffectiveBatchSize);

        // Both halves of the cost are measured, not modelled. On an RTX 5080 at 1,024 tokens the run
        // held 0.557 GB at one sequence and 0.928 GB at four: roughly 0.6 GB of base footprint that
        // exists whatever the batch, plus about 128 MB for each concurrent sequence. Only the second
        // part scales with sequence length. Staying inside a quarter of the card leaves room for a
        // resident chat model or a second job rather than evicting them to train.
        const double BaseFootprintBytes = 640d * 1024 * 1024;
        double perSequenceBytes = 128d * 1024 * 1024 * Math.Max(1, maxSequenceLength) / 1024d;
        double usableBytes = vram * 0.25 - BaseFootprintBytes;
        int affordable = usableBytes <= 0 ? 1 : (int)Math.Floor(usableBytes / perSequenceBytes);

        int microBatch = Math.Clamp(affordable, 1, EffectiveBatchSize);
        while (microBatch > 1 && EffectiveBatchSize % microBatch != 0) microBatch--;
        return (microBatch, EffectiveBatchSize / microBatch);
    }

    private static long LargestGpuVideoMemoryBytes()
    {
        try
        {
            GpuInventoryStatus inventory = new GpuInventoryService().GetStatus();
            return inventory.Adapters
                .Where(adapter => !adapter.IsSoftware)
                .Select(adapter => adapter.DedicatedVideoMemoryBytes)
                .DefaultIfEmpty(0)
                .Max();
        }
        catch { return 0; }
    }

    public Task<TrainingEngineRemovalResult> UninstallEngineAsync(CancellationToken ct)
    {
        string[] busy = _jobs.Values
            .Where(entry => entry.Status.Status is "running" or "queued")
            .Select(entry => entry.Status.Id)
            .ToArray();
        if (busy.Length > 0)
        {
            return Task.FromResult(new TrainingEngineRemovalResult
            {
                Removed = false,
                Detail = "A training job is still running (" + string.Join(", ", busy)
                    + "). Stop it before removing the runtime."
            });
        }

        if (!Directory.Exists(RuntimeRoot))
        {
            return Task.FromResult(new TrainingEngineRemovalResult
            {
                Removed = false,
                Detail = "The managed training runtime is not installed."
            });
        }

        long freedBytes = 0;
        try
        {
            freedBytes = new DirectoryInfo(RuntimeRoot)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch (Exception) { /* Size is a courtesy; a failure to measure must not block removal. */ }

        try
        {
            Directory.Delete(RuntimeRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new TrainingEngineRemovalResult
            {
                Removed = false,
                Detail = Truncate("Could not remove the training runtime: " + exception.Message, 600)
            });
        }

        return Task.FromResult(new TrainingEngineRemovalResult
        {
            Removed = true,
            FreedBytes = freedBytes,
            Detail = "Managed training runtime removed. Training can be installed again at any time; "
                + "datasets, adapters and job history are kept."
        });
    }

    public async Task<string> InstallEngineAsync(CancellationToken ct)
    {
        TrainingRuntimeLock.Validate(TrainingRuntimeLock.DefaultPath);
        Directory.CreateDirectory(RuntimeRoot);
        var log = new System.Text.StringBuilder();
        TorchHardwareProfile torchHardware = await TorchInstallHardwareDetector.DetectAsync(ct).ConfigureAwait(false);
        TorchInstallChannelPlan torchPlan = TorchInstallChannelPolicy.Select(torchHardware);
        if (!torchPlan.CanInstall) throw new InvalidOperationException(torchPlan.Reason);
        TrainingRuntimeLock.Validate(TrainingRuntimeLock.TorchLockPath(torchPlan.Channel));

        await ManagedPythonRuntime.EnsureVenvAsync(VenvRoot, ManagedPythonRuntime.DefaultPythonVersion, ct).ConfigureAwait(false);
        log.AppendLine(torchPlan.Reason);
        // torch/torchvision are hash-locked per hardware channel (no unpinned wheel install); their pure-Python
        // dependencies are part of the main lock below, which is why both installs use --no-deps.
        log.Append(await TrainingRuntimeLock.InstallAsync(
            TrainingRuntimeLock.TorchLockPath(torchPlan.Channel),
            args => ManagedPythonRuntime.PipInstallAsync(VenvRoot, args, ct, TimeSpan.FromMinutes(20)),
            TrainingRuntimeLock.TorchIndexUrl(torchPlan.Channel)).ConfigureAwait(false));
        log.Append(await TrainingRuntimeLock.InstallAsync(TrainingRuntimeLock.DefaultPath,
            args => ManagedPythonRuntime.PipInstallAsync(VenvRoot, args, ct, TimeSpan.FromMinutes(25))).ConfigureAwait(false));
        WriteTrainScript();
        return log.ToString();
    }

    public TrainingJobStatus CreateJob(TrainingJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TrainJsonlPath) || !File.Exists(request.TrainJsonlPath))
            throw new InvalidDataException("trainJsonlPath must point to a real existing JSONL file.");
        if (string.IsNullOrWhiteSpace(request.BaseModel)) throw new InvalidDataException("baseModel is required.");
        string method = NormalizeMethod(request.Method);
        ValidateDatasetForMethod(request.TrainJsonlPath, method, request.GrpoRewardMode);
        ValidateRequest(request, method);

        TrainingRuntimeStatus runtimeStatus = TrainingRuntimeJobGate.GetVerifiedStatus(RuntimeRoot, VenvRoot, method);
        TrainingJobRequest requestSnapshot = CloneRequest(request);
        string datasetSha256 = TrainingJobDurability.ComputeFileSha256(requestSnapshot.TrainJsonlPath);
        string runtimeFingerprint = TrainingJobDurability.ComputeRuntimeFingerprint(runtimeStatus);
        var status = new TrainingJobStatus
        {
            Id = Guid.NewGuid().ToString("N"),
            TotalSteps = Math.Max(1, requestSnapshot.MaxSteps),
            Method = method,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        int maxWallClockMinutes = ResolveWallClockMinutes(ExecutionProfileStore.Current.Get(requestSnapshot.BaseModel).Effective);
        var entry = new Entry(status, maxWallClockMinutes, requestSnapshot, datasetSha256, runtimeFingerprint);

        // Journal creation is part of admission. A job is not made visible or queued unless PaRuX
        // can first persist enough identity to recover it without guessing after a process crash.
        PersistEntry(entry);
        _jobs[status.Id] = entry;
        _ = Task.Run(() => RunAsync(entry, requestSnapshot));
        return Snapshot(entry);
    }

    internal static int ResolveWallClockMinutes(ModelExecutionProfile? profile) =>
        Math.Clamp(profile?.Budget?.MaxWallClockMinutes ?? 60, 1, 1_440);

    internal static string NormalizeMethod(string? rawMethod) => TrainingMethod.Normalize(rawMethod);

    internal static void ValidateDpoJsonlShape(string path) =>
        ValidateDatasetForMethod(path, TrainingMethod.Dpo, GrpoRewardMode.ExactReference);

    internal static void ValidateDatasetForMethod(string path, string method, string? rewardMode)
    {
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(path);
        if (shape.LinesRead == 0) throw new InvalidDataException(shape.Summary);
        bool supported = TrainingRunPlanner.Supports(shape, method);
        if (!supported)
            throw new InvalidDataException("Dataset is not compatible with " + method + ". " + shape.Summary);
        if (shape.MissingMediaFiles > 0 && method is TrainingMethod.VisionSft or TrainingMethod.TtsSft)
            throw new InvalidDataException(shape.MissingMediaFiles + " referenced media file(s) do not exist.");
        if (method == TrainingMethod.Grpo)
        {
            string reward = GrpoRewardMode.Normalize(rewardMode);
            if (reward is GrpoRewardMode.ExactReference or GrpoRewardMode.ContainsReference)
            {
                foreach (string raw in File.ReadLines(path).Take(TrainingDatasetShape.SampleLineLimit))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    using JsonDocument doc = JsonDocument.Parse(raw);
                    if (!HasNonEmptyStringProperty(doc.RootElement, "reference"))
                        throw new InvalidDataException("GRPO reward mode " + reward + " requires a non-empty reference field on every row.");
                }
            }
        }
    }

    private static void ValidateRequest(TrainingJobRequest request, string method)
    {
        if (request.LoraRank is < 1 or > 256) throw new InvalidDataException("loraRank must be between 1 and 256.");
        if (request.MaxSteps is < 1 or > 1_000_000) throw new InvalidDataException("maxSteps must be between 1 and 1,000,000.");
        if (request.LearningRate is <= 0 or > 0.01 || double.IsNaN(request.LearningRate) || double.IsInfinity(request.LearningRate))
            throw new InvalidDataException("learningRate must be finite, > 0 and <= 0.01.");
        if (request.MaxSequenceLength is < 128 or > 1_048_576)
            throw new InvalidDataException("maxSequenceLength must be between 128 and 1,048,576.");
        if (method == TrainingMethod.Dpo && request.DpoBeta is <= 0 or > 10)
            throw new InvalidDataException("dpoBeta must be > 0 and <= 10.");
        if (method == TrainingMethod.Grpo)
        {
            _ = GrpoRewardMode.Normalize(request.GrpoRewardMode);
            if (request.GrpoGenerations is < 2 or > 16) throw new InvalidDataException("grpoGenerations must be between 2 and 16.");
        }
        ValidateFieldName(request.TextField, "textField");
        ValidateFieldName(request.ImageField, "imageField");
        ValidateFieldName(request.AudioField, "audioField");
        if (method == TrainingMethod.TtsSft
            && !request.BaseModel.Contains("speecht5", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The current PaRuX TTS training adapter supports SpeechT5-family base models. Other TTS families are not silently routed through an incompatible trainer.");
    }

    private static void ValidateFieldName(string? value, string name)
    {
        string field = value ?? string.Empty;
        if (field.Length is < 1 or > 80 || field.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-')))
            throw new InvalidDataException(name + " must be a simple JSON property name (letters, digits, _ or -, max 80)." );
    }

    private static bool HasNonEmptyStringProperty(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    public IReadOnlyList<TrainingJobStatus> List() => _jobs.Values.Select(Snapshot).OrderByDescending(s => s.CreatedUtc).ToList();
    public TrainingJobStatus? Get(string id) => _jobs.TryGetValue(id, out Entry? entry) ? Snapshot(entry) : null;

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out Entry? entry)) return false;
        lock (entry.Gate)
        {
            if (IsTerminal(entry.Status.Status)) return false;
            entry.Status.Status = "cancelled";
            entry.Status.Stage = "Cancelled";
            entry.Status.UpdatedUtc = DateTime.UtcNow;
            try { entry.Process?.Kill(entireProcessTree: true); } catch { }
        }
        TryPersistEntry(entry);
        return true;
    }

    private void RecoverPersistedJobs()
    {
        IReadOnlyList<TrainingJobDurability.LoadResult> persisted;
        try { persisted = TrainingJobDurability.LoadAll(RuntimeRoot); }
        catch { return; }

        foreach (TrainingJobDurability.LoadResult loaded in persisted)
        {
            if (loaded.Value == null)
            {
                var broken = new TrainingJobStatus
                {
                    Id = loaded.Id,
                    Status = "failed",
                    Stage = "Recovery failed",
                    Error = "Training journal could not be loaded: " + loaded.Error,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
                _jobs.TryAdd(loaded.Id, new Entry(broken, 1));
                continue;
            }

            TrainingJobDurability.Journal journal = loaded.Value;
            journal.Status.History ??= new List<TrainingMetricSample>();
            var entry = new Entry(
                journal.Status,
                journal.MaxWallClockMinutes,
                journal.Request,
                journal.DatasetSha256,
                journal.RuntimeFingerprint);
            if (!_jobs.TryAdd(journal.Status.Id, entry) || IsTerminal(journal.Status.Status)) continue;

            lock (entry.Gate)
            {
                entry.Status.Status = "queued";
                entry.Status.Stage = "Recovery pending";
                entry.Status.Error = null;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            TryPersistEntry(entry);
            _ = Task.Run(() => RecoverAsync(entry));
        }
    }

    private async Task RecoverAsync(Entry entry)
    {
        if (entry.Request == null) return;
        while (_runtime.GamingMode)
        {
            lock (entry.Gate)
            {
                if (IsTerminal(entry.Status.Status)) return;
                entry.Status.Status = "queued";
                entry.Status.Stage = "Recovery deferred by Gaming Mode";
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            TryPersistEntry(entry);
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        lock (entry.Gate)
        {
            if (IsTerminal(entry.Status.Status)) return;
        }

        try
        {
            string outputDir = TrainingJobDurability.OutputDirectory(RuntimeRoot, entry.Status.Id);
            string adapterPath = Path.Combine(outputDir, "lora-adapter");
            string completionMarker = Path.Combine(outputDir, "parux-training-complete");
            if (File.Exists(completionMarker) && Directory.Exists(adapterPath))
            {
                lock (entry.Gate)
                {
                    entry.Status.Status = "completed";
                    entry.Status.Stage = "Completed before Core restart";
                    entry.Status.AdapterPath = adapterPath;
                    entry.Status.Error = null;
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
                TryPersistEntry(entry);
                try { _adapters.RegisterFromTrainingJob(Snapshot(entry), entry.Request); }
                catch (Exception ex)
                {
                    lock (entry.Gate)
                    {
                        entry.Status.Stage = "Completed before Core restart, but the adapter was not registered";
                        entry.Status.Error = "Adapter exists at " + entry.Status.AdapterPath + " but registration failed: " + ex.Message;
                        entry.Status.UpdatedUtc = DateTime.UtcNow;
                    }
                    TryPersistEntry(entry);
                }
                return;
            }

            VerifyRecoveryIdentity(entry);
            string? checkpoint = TrainingJobDurability.FindLatestCompleteCheckpoint(outputDir);
            if (checkpoint == null)
                throw new InvalidDataException(
                    "The interrupted training job has no complete Trainer checkpoint. PaRuX will not silently restart it from step zero.");

            lock (entry.Gate)
            {
                entry.Status.Status = "queued";
                entry.Status.Stage = "Resuming from " + Path.GetFileName(checkpoint);
                entry.Status.Error = null;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            TryPersistEntry(entry);
            await RunAsync(entry, entry.Request, checkpoint).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                if (entry.Status.Status != "cancelled")
                {
                    entry.Status.Status = "failed";
                    entry.Status.Stage = "Recovery failed";
                    entry.Status.Error = ex.Message;
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
            }
            TryPersistEntry(entry);
        }
    }

    private async Task RunAsync(Entry entry, TrainingJobRequest request, string? resumeCheckpoint = null)
    {
        WriteTrainScript();
        string configPath = Path.Combine(RuntimeRoot, "run-config-" + entry.Status.Id + ".json");
        string outputDir = TrainingJobDurability.OutputDirectory(RuntimeRoot, entry.Status.Id);
        Directory.CreateDirectory(outputDir);

        try
        {
            VerifyRecoveryIdentity(entry);
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                entry.Status.Status = "failed";
                entry.Status.Stage = resumeCheckpoint == null ? "Training identity changed before start" : "Recovery identity mismatch";
                entry.Status.Error = ex.Message;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            TryPersistEntry(entry);
            return;
        }

        try
        {
            ModelExecutionProfile executionProfile = ExecutionProfileStore.Current.Get(request.BaseModel).Effective;
            ExecutionResourceDecision admission = await ExecutionResourceGuard.CheckCurrentAsync(
                executionProfile,
                enforceCurrentVram: true,
                _runtime.GamingCancellationToken).ConfigureAwait(false);
            if (!admission.Allowed)
            {
                lock (entry.Gate)
                {
                    entry.Status.Status = "failed";
                    entry.Status.Stage = "Blocked by execution profile resource guard";
                    entry.Status.Error = admission.Message;
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
                TryPersistEntry(entry);
                return;
            }
        }
        catch (OperationCanceledException) when (_runtime.GamingCancellationToken.IsCancellationRequested)
        {
            lock (entry.Gate)
            {
                entry.Status.Status = "cancelled";
                entry.Status.Stage = "Cancelled by Gaming Mode";
                entry.Status.Error = null;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            TryPersistEntry(entry);
            return;
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                entry.Status.Status = "failed";
                entry.Status.Stage = "Execution Profile resource admission failed";
                entry.Status.Error = ex.Message;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            TryPersistEntry(entry);
            return;
        }

        (int microBatch, int gradientAccumulation) = PlanBatchShape(request.MaxSequenceLength);

        var config = new
        {
            train_jsonl = Path.GetFullPath(request.TrainJsonlPath),
            dataset_dir = Path.GetDirectoryName(Path.GetFullPath(request.TrainJsonlPath)),
            base_model = request.BaseModel.Trim(),
            batch_size = microBatch,
            gradient_accumulation_steps = gradientAccumulation,
            lora_rank = request.LoraRank,
            max_steps = request.MaxSteps,
            learning_rate = request.LearningRate,
            output_dir = outputDir,
            method = entry.Status.Method,
            dpo_beta = request.DpoBeta,
            max_seq_length = request.MaxSequenceLength,
            grpo_reward_mode = GrpoRewardMode.Normalize(request.GrpoRewardMode),
            grpo_generations = request.GrpoGenerations,
            text_field = request.TextField,
            image_field = request.ImageField,
            audio_field = request.AudioField,
            checkpoint_steps = Math.Clamp(Math.Min(request.MaxSteps, 50), 1, 50),
            resume_checkpoint = resumeCheckpoint
        };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config)).ConfigureAwait(false);
        lock (entry.Gate)
        {
            entry.Status.Status = "running";
            entry.Status.Stage = resumeCheckpoint == null
                ? "Starting " + entry.Status.Method
                : "Resuming " + entry.Status.Method + " from " + Path.GetFileName(resumeCheckpoint);
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
        TryPersistEntry(entry);

        var psi = new ProcessStartInfo
        {
            FileName = VenvPython,
            WorkingDirectory = RuntimeRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(TrainScriptPath);
        psi.ArgumentList.Add(configPath);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        lock (entry.Gate) entry.Process = process;
        using var budgetCts = new CancellationTokenSource(TimeSpan.FromMinutes(entry.MaxWallClockMinutes));
        using var executionLink = CancellationTokenSource.CreateLinkedTokenSource(_runtime.GamingCancellationToken, budgetCts.Token);
        executionLink.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });

        try
        {
            process.Start();
            Task stdoutTask = PumpAsync(process.StandardOutput, entry);
            Task stderrTask = PumpAsync(process.StandardError, entry);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            bool justCompleted = false;
            lock (entry.Gate)
            {
                if (entry.Status.Status == "cancelled") { }
                else if (_runtime.GamingCancellationToken.IsCancellationRequested)
                {
                    entry.Status.Status = "cancelled";
                    entry.Status.Stage = "Cancelled by Gaming Mode";
                    entry.Status.Error = null;
                }
                else if (budgetCts.IsCancellationRequested)
                {
                    entry.Status.Status = "cancelled";
                    entry.Status.Stage = "Stopped by execution profile budget";
                    entry.Status.Error = $"Training exceeded the active execution profile wall-clock budget of {entry.MaxWallClockMinutes} minute(s).";
                }
                else if (process.ExitCode == 0)
                {
                    string adapterPath = Path.Combine(outputDir, "lora-adapter");
                    string completionMarker = Path.Combine(outputDir, "parux-training-complete");
                    if (Directory.Exists(adapterPath) && File.Exists(completionMarker))
                    {
                        entry.Status.Status = "completed";
                        entry.Status.Stage = "Completed";
                        entry.Status.AdapterPath = adapterPath;
                        justCompleted = true;
                    }
                    else
                    {
                        entry.Status.Status = "failed";
                        entry.Status.Stage = "Failed";
                        entry.Status.Error = "Training reported success but did not produce both the adapter and durable completion marker.";
                    }
                }
                else
                {
                    entry.Status.Status = "failed";
                    entry.Status.Stage = "Failed";
                    string baseError = "Training process exited with code " + process.ExitCode + ".";
                    string? tail = entry.RecentOutput.Tail();
                    entry.Status.Error ??= tail == null ? baseError : baseError + "\nLast output:\n" + tail;
                }
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            TryPersistEntry(entry);

            if (justCompleted)
            {
                try { _adapters.RegisterFromTrainingJob(Snapshot(entry), request); }
                catch (Exception ex)
                {
                    lock (entry.Gate)
                    {
                        entry.Status.Stage = "Completed, but the adapter was not registered";
                        entry.Status.Error = "Adapter exists at " + entry.Status.AdapterPath + " but registration failed: " + ex.Message;
                        entry.Status.UpdatedUtc = DateTime.UtcNow;
                    }
                    TryPersistEntry(entry);
                }
            }
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                if (_runtime.GamingCancellationToken.IsCancellationRequested)
                {
                    entry.Status.Status = "cancelled";
                    entry.Status.Stage = "Cancelled by Gaming Mode";
                    entry.Status.Error = null;
                }
                else if (budgetCts.IsCancellationRequested)
                {
                    entry.Status.Status = "cancelled";
                    entry.Status.Stage = "Stopped by execution profile budget";
                    entry.Status.Error = $"Training exceeded the active execution profile wall-clock budget of {entry.MaxWallClockMinutes} minute(s).";
                }
                else if (entry.Status.Status != "cancelled")
                {
                    entry.Status.Status = "failed";
                    entry.Status.Stage = "Failed";
                    entry.Status.Error = ex.Message;
                }
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            TryPersistEntry(entry);
        }
        finally
        {
            lock (entry.Gate) entry.Process = null;
            try { File.Delete(configPath); } catch { }
        }
    }

    private async Task PumpAsync(StreamReader reader, Entry entry)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line)) entry.RecentOutput.Record(line);
            TrainingMetricsParser.Sample sample = TrainingMetricsParser.Parse(line);
            if (sample.HasAnything)
            {
                lock (entry.Gate)
                {
                    if (sample.Loss.HasValue) entry.Status.LastLoss = sample.Loss.Value;
                    if (sample.GradientNorm.HasValue) entry.Status.LastGradientNorm = sample.GradientNorm.Value;
                    if (sample.LearningRate.HasValue) entry.Status.LastLearningRate = sample.LearningRate.Value;
                    if (sample.Epoch.HasValue) entry.Status.LastEpoch = sample.Epoch.Value;
                    AppendHistory(entry.Status, sample);
                    entry.Status.Stage = "Training";
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
                continue;
            }
            Match stepMatch = StepProgress.Match(line);
            if (stepMatch.Success && int.TryParse(stepMatch.Groups["step"].Value, out int step))
            {
                lock (entry.Gate)
                {
                    entry.Status.CurrentStep = step;
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
            }
        }
    }

    internal const int MaximumHistorySamples = 500;

    private static void AppendHistory(TrainingJobStatus status, TrainingMetricsParser.Sample sample)
    {
        status.History.Add(new TrainingMetricSample
        {
            Step = status.CurrentStep,
            Loss = sample.Loss,
            GradientNorm = sample.GradientNorm,
            LearningRate = sample.LearningRate,
            Epoch = sample.Epoch
        });
        if (status.History.Count <= MaximumHistorySamples) return;
        var thinned = new List<TrainingMetricSample>(status.History.Count / 2 + 1);
        for (int index = 0; index < status.History.Count; index += 2) thinned.Add(status.History[index]);
        status.History = thinned;
    }

    internal static double? TryParseLoss(string logLine) => TrainingMetricsParser.Parse(logLine).Loss;

    private void VerifyRecoveryIdentity(Entry entry)
    {
        if (entry.Request == null || string.IsNullOrWhiteSpace(entry.DatasetSha256)
            || string.IsNullOrWhiteSpace(entry.RuntimeFingerprint))
            throw new InvalidDataException("Training job has no durable recovery identity.");
        if (!File.Exists(entry.Request.TrainJsonlPath))
            throw new InvalidDataException("Training dataset no longer exists at its recorded path.");

        string currentDataset = TrainingJobDurability.ComputeFileSha256(entry.Request.TrainJsonlPath);
        if (!string.Equals(currentDataset, entry.DatasetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Training dataset changed after the job was admitted; recovery is refused.");

        TrainingRuntimeStatus currentRuntime = TrainingRuntimeJobGate.GetVerifiedStatus(
            RuntimeRoot, VenvRoot, entry.Status.Method);
        string currentFingerprint = TrainingJobDurability.ComputeRuntimeFingerprint(currentRuntime);
        if (!string.Equals(currentFingerprint, entry.RuntimeFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Training runtime identity changed after the job was admitted; recovery is refused.");
    }

    private static TrainingJobRequest CloneRequest(TrainingJobRequest request) =>
        JsonSerializer.Deserialize<TrainingJobRequest>(JsonSerializer.Serialize(request))
        ?? throw new InvalidDataException("Training request could not be snapshotted for durable recovery.");

    private static bool IsTerminal(string? status) =>
        status is "completed" or "failed" or "cancelled";

    private static void PersistEntry(Entry entry)
    {
        if (entry.Request == null || string.IsNullOrWhiteSpace(entry.DatasetSha256)
            || string.IsNullOrWhiteSpace(entry.RuntimeFingerprint))
            throw new InvalidDataException("Training job has no durable recovery identity.");
        TrainingJobDurability.Save(RuntimeRoot, new TrainingJobDurability.Journal
        {
            Request = entry.Request,
            Status = Snapshot(entry),
            MaxWallClockMinutes = entry.MaxWallClockMinutes,
            DatasetSha256 = entry.DatasetSha256,
            RuntimeFingerprint = entry.RuntimeFingerprint
        });
    }

    private static void TryPersistEntry(Entry entry)
    {
        try { PersistEntry(entry); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            lock (entry.Gate)
            {
                string warning = "Training journal update failed: " + ex.Message;
                entry.Status.Error = string.IsNullOrWhiteSpace(entry.Status.Error)
                    ? warning
                    : entry.Status.Error + "\n" + warning;
            }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCapturedAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken ct, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) psi.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = psi };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static TrainingJobStatus Snapshot(Entry entry)
    {
        lock (entry.Gate)
        {
            TrainingJobStatus s = entry.Status;
            return new TrainingJobStatus
            {
                Id = s.Id, Status = s.Status, Stage = s.Stage,
                CurrentStep = s.CurrentStep, TotalSteps = s.TotalSteps, LastLoss = s.LastLoss,
                LastGradientNorm = s.LastGradientNorm, LastLearningRate = s.LastLearningRate,
                LastEpoch = s.LastEpoch, History = s.History.ToList(),
                Error = s.Error, AdapterPath = s.AdapterPath, Method = s.Method,
                CreatedUtc = s.CreatedUtc, UpdatedUtc = s.UpdatedUtc
            };
        }
    }

    private static void WriteTrainScript()
    {
        Directory.CreateDirectory(RuntimeRoot);
        File.WriteAllText(TrainScriptPath, TrainScriptSource, new System.Text.UTF8Encoding(false));
    }

    private const string TrainScriptSource = """
import json, os, re, sys
from pathlib import Path

with open(sys.argv[1], encoding="utf-8") as f:
    cfg = json.load(f)
method = cfg.get("method", "sft")
base_model = cfg["base_model"]
out_dir = cfg["output_dir"]
adapter_dir = os.path.join(out_dir, "lora-adapter")
resume_checkpoint = cfg.get("resume_checkpoint") or None
checkpoint_steps = max(1, min(int(cfg.get("checkpoint_steps", 50)), int(cfg["max_steps"])))
os.makedirs(out_dir, exist_ok=True)

def rows():
    with open(cfg["train_jsonl"], encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                yield json.loads(line)

def resolve_media(value):
    p = Path(value)
    if not p.is_absolute():
        p = Path(cfg["dataset_dir"]) / p
    return str(p.resolve())

if method == "tts-sft":
    import torch, librosa
    from datasets import Dataset
    from transformers import SpeechT5Processor, SpeechT5ForTextToSpeech, Seq2SeqTrainer, Seq2SeqTrainingArguments
    from peft import LoraConfig, get_peft_model

    processor = SpeechT5Processor.from_pretrained(base_model)
    model = SpeechT5ForTextToSpeech.from_pretrained(base_model)
    model = get_peft_model(model, LoraConfig(
        r=cfg["lora_rank"], lora_alpha=cfg["lora_rank"] * 2,
        target_modules=["q_proj", "v_proj"], lora_dropout=0.0, bias="none"))

    def normalize_tts_spectrogram(labels):
        labels = torch.as_tensor(labels)
        # SpeechT5 processors may retain the singleton batch of a single audio target.
        # Dataset rows must contain [frames, mel bins], not [1, frames, mel bins].
        if labels.ndim == 3 and labels.shape[0] == 1:
            labels = labels[0]
        if labels.ndim != 2 or labels.shape[0] == 0 or labels.shape[1] == 0:
            raise ValueError("TTS example must contain one nonempty rank-2 spectrogram")
        return labels.tolist()

    prepared = []
    for record in rows():
        audio, _ = librosa.load(resolve_media(record[cfg["audio_field"]]), sr=16000, mono=True)
        item = processor(text=record[cfg["text_field"]], audio_target=audio, sampling_rate=16000,
                         return_attention_mask=False)
        item["labels"] = normalize_tts_spectrogram(item["labels"])
        item["speaker_embeddings"] = [0.0] * 512
        prepared.append(item)
    dataset = Dataset.from_list(prepared)

    class TtsCollator:
        def __init__(self, processor, model):
            self.processor, self.model = processor, model
        def __call__(self, features):
            inputs = [{"input_ids": f["input_ids"]} for f in features]
            labels = [{"input_values": normalize_tts_spectrogram(f["labels"])} for f in features]
            batch = self.processor.pad(input_ids=inputs, labels=labels, return_tensors="pt")
            if batch["labels"].ndim != 3:
                raise ValueError("TTS batch spectrograms must have shape [batch, frames, mel bins]")
            if "decoder_attention_mask" in batch:
                if tuple(batch["decoder_attention_mask"].shape) != tuple(batch["labels"].shape[:2]):
                    raise ValueError("TTS decoder attention mask must align with spectrogram frames")
                batch["labels"] = batch["labels"].masked_fill(batch["decoder_attention_mask"].unsqueeze(-1).ne(1), -100)
                del batch["decoder_attention_mask"]
            factor = getattr(self.model.config, "reduction_factor", 1)
            if factor > 1:
                max_len = batch["labels"].shape[1]
                max_len -= max_len % factor
                batch["labels"] = batch["labels"][:, :max_len]
            batch["speaker_embeddings"] = torch.tensor([f["speaker_embeddings"] for f in features], dtype=torch.float32)
            return batch

    trainer = Seq2SeqTrainer(
        model=model, train_dataset=dataset, data_collator=TtsCollator(processor, model),
        args=Seq2SeqTrainingArguments(
            output_dir=out_dir, per_device_train_batch_size=cfg.get("batch_size", 1), gradient_accumulation_steps=cfg.get("gradient_accumulation_steps", 4),
            max_steps=cfg["max_steps"], learning_rate=cfg["learning_rate"], logging_steps=1,
            save_strategy="steps", save_steps=checkpoint_steps, save_total_limit=2,
            report_to=[], fp16=torch.cuda.is_available() and not torch.cuda.is_bf16_supported(),
            bf16=torch.cuda.is_available() and torch.cuda.is_bf16_supported(), remove_unused_columns=False))
    trainer.train(resume_from_checkpoint=resume_checkpoint)
    model.save_pretrained(adapter_dir)
    processor.save_pretrained(adapter_dir)
else:
    import torch
    from datasets import Dataset

    def sft_config(**kwargs):
        # TRL renamed SFTConfig.max_seq_length to max_length; the pinned Unsloth resolves whichever TRL
        # it requires, so accept both instead of failing every SFT/CPT/Vision launch on the newer name.
        import inspect
        from trl import SFTConfig
        accepted = inspect.signature(SFTConfig.__init__).parameters
        if "max_seq_length" in kwargs and "max_seq_length" not in accepted and "max_length" in accepted:
            kwargs["max_length"] = kwargs.pop("max_seq_length")
        return SFTConfig(**kwargs)

    if method == "vision-sft":
        from unsloth import FastVisionModel
        from unsloth.trainer import UnslothVisionDataCollator
        from PIL import Image
        model, tokenizer = FastVisionModel.from_pretrained(
            model_name=base_model, load_in_4bit=True, use_gradient_checkpointing="unsloth")
        model = FastVisionModel.get_peft_model(
            model, finetune_vision_layers=True, finetune_language_layers=True,
            finetune_attention_modules=True, finetune_mlp_modules=True,
            r=cfg["lora_rank"], lora_alpha=cfg["lora_rank"] * 2, lora_dropout=0, bias="none")
        converted = []
        for record in rows():
            image = Image.open(resolve_media(record[cfg["image_field"]])).convert("RGB")
            if "messages" in record:
                text_messages = record["messages"]
                user_text = "\n".join(m.get("content", "") for m in text_messages if m.get("role") == "user")
                assistant_text = "\n".join(m.get("content", "") for m in text_messages if m.get("role") == "assistant")
            else:
                user_text, assistant_text = record["prompt"], record["response"]
            converted.append({"messages": [
                {"role": "user", "content": [{"type": "image", "image": image}, {"type": "text", "text": user_text}]},
                {"role": "assistant", "content": [{"type": "text", "text": assistant_text}]}
            ]})
        dataset = Dataset.from_list(converted)
        from trl import SFTTrainer, SFTConfig
        trainer = SFTTrainer(
            model=model, tokenizer=tokenizer, train_dataset=dataset,
            data_collator=UnslothVisionDataCollator(model, tokenizer),
            args=sft_config(
                per_device_train_batch_size=cfg.get("batch_size", 1), gradient_accumulation_steps=cfg.get("gradient_accumulation_steps", 4),
                max_steps=cfg["max_steps"], learning_rate=cfg["learning_rate"], logging_steps=1,
                save_strategy="steps", save_steps=checkpoint_steps, save_total_limit=2,
                output_dir=out_dir, report_to=[], remove_unused_columns=False,
                dataset_text_field="", dataset_kwargs={"skip_prepare_dataset": True},
                max_seq_length=cfg["max_seq_length"]))
        trainer.train(resume_from_checkpoint=resume_checkpoint)
    else:
        from unsloth import FastLanguageModel
        model, tokenizer = FastLanguageModel.from_pretrained(
            model_name=base_model, max_seq_length=cfg["max_seq_length"], dtype=None, load_in_4bit=True)
        model = FastLanguageModel.get_peft_model(
            model, r=cfg["lora_rank"], target_modules=["q_proj", "k_proj", "v_proj", "o_proj"],
            lora_alpha=cfg["lora_rank"] * 2, lora_dropout=0, bias="none",
            use_gradient_checkpointing="unsloth", random_state=3407)

        if method == "dpo":
            data = [{"prompt": r["prompt"], "chosen": r["chosen"], "rejected": r["rejected"]} for r in rows()]
            dataset = Dataset.from_list(data)
            from trl import DPOTrainer, DPOConfig
            trainer = DPOTrainer(
                model=model, ref_model=None, train_dataset=dataset, processing_class=tokenizer,
                args=DPOConfig(
                    per_device_train_batch_size=cfg.get("batch_size", 1), gradient_accumulation_steps=cfg.get("gradient_accumulation_steps", 4),
                    max_steps=cfg["max_steps"], learning_rate=cfg["learning_rate"], logging_steps=1,
                    save_strategy="steps", save_steps=checkpoint_steps, save_total_limit=2,
                    output_dir=out_dir, report_to=[], beta=cfg.get("dpo_beta", .1),
                    fp16=not torch.cuda.is_bf16_supported(), bf16=torch.cuda.is_bf16_supported()))
            trainer.train(resume_from_checkpoint=resume_checkpoint)
        elif method == "grpo":
            reward_mode = cfg.get("grpo_reward_mode", "exact-reference")
            data = []
            for r in rows():
                item = {"prompt": r["prompt"]}
                if "reference" in r: item["reference"] = r["reference"]
                data.append(item)
            dataset = Dataset.from_list(data)

            def completion_text(c):
                if isinstance(c, str): return c
                if isinstance(c, list):
                    if c and isinstance(c[0], dict): return "".join(str(x.get("content", "")) for x in c)
                    return "".join(map(str, c))
                return str(c)

            def reward_func(prompts, completions, reference=None, **kwargs):
                refs = reference if reference is not None else [""] * len(completions)
                scores = []
                for completion, ref in zip(completions, refs):
                    text = completion_text(completion).strip()
                    expected = str(ref or "").strip()
                    if reward_mode == "exact-reference": score = 1.0 if text == expected else 0.0
                    elif reward_mode == "contains-reference": score = 1.0 if expected and expected in text else 0.0
                    elif reward_mode == "json-format":
                        try: json.loads(text); score = 1.0
                        except Exception: score = 0.0
                    else:
                        score = 1.0 if re.fullmatch(r"\s*<[^>]+>.*</[^>]+>\s*", text, re.S) else 0.0
                    scores.append(score)
                return scores

            from trl import GRPOTrainer, GRPOConfig
            trainer = GRPOTrainer(
                model=model, processing_class=tokenizer, reward_funcs=reward_func, train_dataset=dataset,
                args=GRPOConfig(
                    per_device_train_batch_size=cfg.get("batch_size", 1), gradient_accumulation_steps=cfg.get("gradient_accumulation_steps", 4),
                    num_generations=cfg.get("grpo_generations", 4), max_steps=cfg["max_steps"],
                    learning_rate=cfg["learning_rate"], logging_steps=1,
                    save_strategy="steps", save_steps=checkpoint_steps, save_total_limit=2,
                    output_dir=out_dir, report_to=[],
                    fp16=not torch.cuda.is_bf16_supported(), bf16=torch.cuda.is_bf16_supported()))
            trainer.train(resume_from_checkpoint=resume_checkpoint)
        else:
            if method == "continued-pretraining":
                data = [{"text": r[cfg["text_field"]]} for r in rows()]
            else:
                data = [{"text": tokenizer.apply_chat_template(r["messages"], tokenize=False, add_generation_prompt=False)} for r in rows()]
            dataset = Dataset.from_list(data)
            from trl import SFTTrainer, SFTConfig
            trainer = SFTTrainer(
                model=model, train_dataset=dataset, processing_class=tokenizer,
                args=sft_config(
                    dataset_text_field="text", max_seq_length=cfg["max_seq_length"], packing=method == "continued-pretraining",
                    per_device_train_batch_size=cfg.get("batch_size", 1), gradient_accumulation_steps=cfg.get("gradient_accumulation_steps", 4),
                    max_steps=cfg["max_steps"], learning_rate=cfg["learning_rate"], logging_steps=1,
                    save_strategy="steps", save_steps=checkpoint_steps, save_total_limit=2,
                    output_dir=out_dir, report_to=[], fp16=not torch.cuda.is_bf16_supported(),
                    bf16=torch.cuda.is_bf16_supported()))
            trainer.train(resume_from_checkpoint=resume_checkpoint)

    model.save_pretrained(adapter_dir)
    tokenizer.save_pretrained(adapter_dir)

marker_tmp = os.path.join(out_dir, ".parux-training-complete.tmp")
with open(marker_tmp, "w", encoding="utf-8") as marker:
    marker.write("completed\n")
os.replace(marker_tmp, os.path.join(out_dir, "parux-training-complete"))
print("PARUX_TRAINING_DONE", adapter_dir)
""";
}
