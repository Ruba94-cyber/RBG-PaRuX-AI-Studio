using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

public sealed record LlamaPerformanceProbeMeasurement(
    double PromptTokensPerSecond,
    double DecodeTokensPerSecond,
    string Backend,
    string HardwareKey,
    int ActualGpuLayers);

public interface ILlamaPerformanceProbe
{
    Task<LlamaPerformanceProbeMeasurement> RunAsync(
        string modelPath, LlamaPerformanceProfileDto profile, CancellationToken cancellationToken);
}

/// <summary>
/// Executes one bounded llama-bench profile. Unlike the legacy one-number model benchmark, this
/// probe measures BOTH pp (prompt processing) and tg (decode generation), passes the same
/// batch/ubatch/KV/FA choices that a tuned llama-server launch will use, and enables llama.cpp's
/// own fit planner with a real per-device safety margin. A profile that cannot fit simply fails;
/// the tuner records that candidate and continues instead of ever risking the interactive Core.
/// Long-context profiles carry an explicit benchmark prompt length, so a 180k competitive target
/// is no longer represented by a misleading 1k prefill while -fitc still validates the full launch
/// envelope up to PaRuX's 1M context ceiling.
/// </summary>
public sealed class LlamaPerformanceProbe : ILlamaPerformanceProbe
{
    private readonly LocalAiProcessManager _process;

    public LlamaPerformanceProbe(LocalAiProcessManager process) => _process = process;

    public async Task<LlamaPerformanceProbeMeasurement> RunAsync(
        string modelPath, LlamaPerformanceProfileDto profile, CancellationToken cancellationToken)
    {
        bool hasCuda = await _process.HasCudaHeavyGpuAsync(cancellationToken).ConfigureAwait(false);
        string runtime = hasCuda ? LocalAiPaths.RuntimeCudaPath : LocalAiPaths.RuntimeVulkanPath;
        string executable = Path.Combine(runtime, LocalAiPaths.LlamaBenchExecutable);
        if (!File.Exists(executable))
            throw new FileNotFoundException("llama-bench was not found in the active PaRuX runtime.", executable);

        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = runtime,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        int promptTokens = Math.Clamp(
            profile.BenchmarkPromptTokens <= 0 ? 1024 : profile.BenchmarkPromptTokens,
            256,
            Math.Max(256, profile.ContextSize - 256));
        int repetitions = promptTokens >= 32_768 ? 1 : 2;

        // -fitt/-fitc deliberately leave GPU-layer/KV fitting to the same native runtime instead
        // of pretending PaRuX can calculate architecture-specific layer memory exactly from file
        // size. Deep prompts use one repetition because repeating a 180k prefill adds benchmark
        // duration without improving candidate ordering evidence.
        foreach (string argument in new[]
        {
            "-m", modelPath,
            "-p", promptTokens.ToString(),
            "-n", "128",
            "-r", repetitions.ToString(),
            "-b", profile.BatchSize.ToString(),
            "-ub", profile.UBatchSize.ToString(),
            "-ctk", profile.CacheTypeK,
            "-ctv", profile.CacheTypeV,
            "-fa", profile.FlashAttention,
            "-sm", profile.SplitMode,
            "-mg", profile.MainGpu.ToString(),
            "-fitt", profile.FitTargetMib.ToString(),
            "-fitc", profile.ContextSize.ToString(),
            "--offline",
            "-o", "json"
        }) start.ArgumentList.Add(argument);

        if (!hasCuda)
        {
            start.ArgumentList.Add("-dev");
            start.ArgumentList.Add("Vulkan0");
        }

        using var process = new Process { StartInfo = start };
        process.Start();
        ChildProcessJob.Assign(process);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(promptTokens >= 131_072 ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("llama-bench performance candidate exceeded its bounded timeout.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("llama-bench candidate failed: " + Trim(stderr));

        using JsonDocument document = JsonDocument.Parse(stdout);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("llama-bench did not return its expected JSON array.");

        JsonElement prompt = default;
        JsonElement decode = default;
        foreach (JsonElement row in document.RootElement.EnumerateArray())
        {
            int nPrompt = row.TryGetProperty("n_prompt", out JsonElement p) && p.TryGetInt32(out int pv) ? pv : 0;
            int nGen = row.TryGetProperty("n_gen", out JsonElement g) && g.TryGetInt32(out int gv) ? gv : 0;
            if (nPrompt > 0 && nGen == 0) prompt = row;
            if (nGen > 0 && nPrompt == 0) decode = row;
        }

        double promptTps = ReadThroughput(prompt, "prompt processing");
        double decodeTps = ReadThroughput(decode, "token generation");
        string backend = ReadString(decode, "backends") ?? ReadString(prompt, "backends") ?? "unknown";
        string gpu = ReadString(decode, "gpu_info") ?? ReadString(prompt, "gpu_info") ?? "unknown";
        int gpuLayers = ReadInt(decode, "n_gpu_layers") ?? ReadInt(prompt, "n_gpu_layers") ?? -1;
        string runtimeTag = LocalAiPaths.GetActiveRuntimeBuildTag(hasCuda ? "text-cuda" : "text-vulkan")
            ?? LocalAiPaths.DefaultRuntimeBuildTag(hasCuda ? "text-cuda" : "text-vulkan");
        return new LlamaPerformanceProbeMeasurement(
            promptTps, decodeTps, backend, backend + " | " + gpu + " | runtime=" + runtimeTag, gpuLayers);
    }

    private static double ReadThroughput(JsonElement row, string label)
    {
        if (row.ValueKind != JsonValueKind.Object
            || !row.TryGetProperty("avg_ts", out JsonElement value)
            || !value.TryGetDouble(out double throughput)
            || !double.IsFinite(throughput) || throughput <= 0)
            throw new InvalidDataException("llama-bench returned no valid " + label + " throughput.");
        return throughput;
    }

    private static string? ReadString(JsonElement row, string property) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(property, out JsonElement value)
            ? value.GetString() : null;

    private static int? ReadInt(JsonElement row, string property) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(property, out JsonElement value)
            && value.TryGetInt32(out int result) ? result : null;

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 700 ? value : value[..700] + "…";
    }
}

/// <summary>
/// Small durable winner catalog. It is separate from ModelBenchmarkStore because model benchmark
/// history is evidence, while these records are launch policy. Writes are atomic and a winner is
/// accepted only if model size+mtime, runtime build and GPU fingerprint still match.
/// </summary>
public sealed class RuntimePerformanceProfileStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public RuntimePerformanceProfileStore() : this(Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "server", "runtime-performance-profiles.json")) { }

    internal RuntimePerformanceProfileStore(string path) => _path = path;

    public IReadOnlyList<RuntimePerformanceProfileSelection> List()
    {
        lock (_gate) return LoadUnsafe().Values.OrderByDescending(x => x.TunedUtc).ToList();
    }

    public void Save(RuntimePerformanceProfileSelection selection)
    {
        lock (_gate)
        {
            Dictionary<string, RuntimePerformanceProfileSelection> current = LoadUnsafe();
            current[selection.Key] = selection;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(current, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }

    public RuntimePerformanceProfileSelection? FindValid(
        string modelPath, string workload, string runtimeBuildTag, string gpuFingerprint)
    {
        FileInfo file = new(modelPath);
        if (!file.Exists) return null;
        string full = file.FullName;
        string normalizedWorkload = RuntimePerformanceTuner.NormalizeWorkload(workload);
        lock (_gate)
        {
            return LoadUnsafe().Values
                .Where(x => string.Equals(x.ModelPath, full, StringComparison.OrdinalIgnoreCase)
                    && x.ModelSizeBytes == file.Length
                    && x.ModelLastWriteUtcTicks == file.LastWriteTimeUtc.Ticks
                    && string.Equals(x.Workload, normalizedWorkload, StringComparison.Ordinal)
                    && string.Equals(x.RuntimeBuildTag, runtimeBuildTag, StringComparison.Ordinal)
                    && string.Equals(x.HardwareKey, gpuFingerprint, StringComparison.Ordinal))
                .OrderByDescending(x => x.TunedUtc)
                .FirstOrDefault();
        }
    }

    private Dictionary<string, RuntimePerformanceProfileSelection> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, RuntimePerformanceProfileSelection>>(
                       File.ReadAllText(_path), JsonOptions)
                   ?? new(StringComparer.Ordinal);
        }
        catch
        {
            // A corrupt advisory tuning file must never stop PaRuX. Built-in safe launch defaults
            // remain authoritative until the user runs tuning again.
            return new(StringComparer.Ordinal);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

/// <summary>
/// Bounded single-user llama.cpp autotuner. It deliberately does NOT modify NVIDIA/AMD driver
/// settings, clocks, power limits or user-managed runtimes. RTX 50-series detection only expands
/// the benchmark search space; generic candidates remain present and are still allowed to win.
/// </summary>
public sealed class RuntimePerformanceTuner
{
    internal const string MediaBusyMessage = "Media generation is active; stop it before tuning text inference.";
    internal const string GamingBusyMessage = "Gaming Mode is active; performance tuning is blocked.";

    /// <summary>True when a run was refused only because the machine is busy right now. That is a deferral to retry when idle, not a failure.</summary>
    internal static string? BusyWaitingText(string? error) =>
        string.Equals(error, MediaBusyMessage, StringComparison.Ordinal) ? "Waiting: media generation is active; it will retry when the PC is idle"
        : string.Equals(error, GamingBusyMessage, StringComparison.Ordinal) ? "Paused: Gaming Mode is on"
        : null;

    private readonly LocalAiProcessManager _process;
    private readonly GpuInventoryService _gpus;
    private readonly ILlamaPerformanceProbe _probe;
    private readonly RuntimePerformanceProfileStore _store;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly ConcurrentDictionary<string, RunEntry> _runs = new();

    private sealed class RunEntry
    {
        public readonly object Gate = new();
        public RuntimePerformanceTuneRun Status = new();
        public CancellationTokenSource Cancellation = new();
    }

    public RuntimePerformanceTuner(
        LocalAiProcessManager process,
        GpuInventoryService gpus,
        ILlamaPerformanceProbe probe,
        RuntimePerformanceProfileStore store)
    {
        _process = process;
        _gpus = gpus;
        _probe = probe;
        _store = store;
    }

    public RuntimePerformanceTuneRun Start(RuntimePerformanceTuneRequest request)
    {
        string path = ValidateModelPath(request.ModelPath);
        string workload = NormalizeWorkload(request.Workload);
        FileInfo model = new(path);
        GpuInventoryStatus hardware = _gpus.GetStatus();
        var run = new RuntimePerformanceTuneRun
        {
            Id = Guid.NewGuid().ToString("N"),
            ModelPath = path,
            Label = string.IsNullOrWhiteSpace(request.Label) ? Path.GetFileNameWithoutExtension(path) : request.Label.Trim(),
            Workload = workload,
            StartedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Candidates = BuildCandidates(path, model.Length, hardware, workload)
                .Select(p => new RuntimePerformanceCandidateResult { Profile = p }).ToList()
        };
        var entry = new RunEntry { Status = run };
        if (!_runs.TryAdd(run.Id, entry)) throw new InvalidOperationException("Could not allocate a tuning run id.");
        _ = Task.Run(() => ExecuteAsync(entry));
        return Clone(run);
    }

    public RuntimePerformanceTuneRun? Get(string id)
    {
        if (!_runs.TryGetValue(id, out RunEntry? entry)) return null;
        lock (entry.Gate) return Clone(entry.Status);
    }

    public bool Cancel(string id)
    {
        if (!_runs.TryGetValue(id, out RunEntry? entry)) return false;
        lock (entry.Gate)
        {
            if (entry.Status.Status is "completed" or "failed" or "cancelled") return false;
            entry.Cancellation.Cancel();
            return true;
        }
    }

    public IReadOnlyList<RuntimePerformanceProfileSelection> ListSelections() => _store.List();

    private async Task ExecuteAsync(RunEntry entry)
    {
        bool restoreText = false;
        bool acquiredQueue = false;
        GamingHardOffWatch? gaming = null;
        try
        {
            await _queue.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
            acquiredQueue = true;
            Set(entry, "running", "Preparing isolated performance benchmark…");

            if (_process.GamingActive)
                throw new InvalidOperationException(GamingBusyMessage);
            // A game launched mid-run must stop GPU benchmarking promptly, not after the matrix.
            gaming = new GamingHardOffWatch(() => _process.GamingActive, entry.Cancellation.Token);
            if (await _process.IsMediaHealthyAsync(gaming.Token).ConfigureAwait(false))
                throw new InvalidOperationException(MediaBusyMessage);

            restoreText = await _process.IsTextHealthyAsync(gaming.Token).ConfigureAwait(false);
            if (!_process.StopText())
                throw new InvalidOperationException("The resident text runtime did not stop, so tuning was not started.");

            for (int i = 0; i < entry.Status.Candidates.Count; i++)
            {
                // The watch cancels as soon as Gaming Mode starts, so the candidate on the GPU stops at once and no later
                // candidate starts. It covers the whole run, not only the gaps between candidates.
                gaming.Token.ThrowIfCancellationRequested();
                RuntimePerformanceCandidateResult candidate;
                lock (entry.Gate)
                {
                    candidate = entry.Status.Candidates[i];
                    candidate.Status = "running";
                    entry.Status.Stage = $"Benchmarking {candidate.Profile.DisplayName} ({i + 1}/{entry.Status.Candidates.Count}) · pp {candidate.Profile.BenchmarkPromptTokens:N0}…";
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }

                try
                {
                    LlamaPerformanceProbeMeasurement measurement = await _probe.RunAsync(
                        entry.Status.ModelPath, candidate.Profile, gaming.Token).ConfigureAwait(false);
                    lock (entry.Gate)
                    {
                        candidate.Status = "completed";
                        candidate.PromptTokensPerSecond = measurement.PromptTokensPerSecond;
                        candidate.DecodeTokensPerSecond = measurement.DecodeTokensPerSecond;
                        candidate.Backend = measurement.Backend;
                        candidate.HardwareKey = measurement.HardwareKey;
                        candidate.ActualGpuLayers = measurement.ActualGpuLayers;
                        entry.Status.UpdatedUtc = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lock (entry.Gate)
                    {
                        candidate.Status = "failed";
                        candidate.Error = ex.Message;
                        entry.Status.UpdatedUtc = DateTime.UtcNow;
                    }
                }
            }

            List<RuntimePerformanceCandidateResult> successful;
            lock (entry.Gate)
                successful = entry.Status.Candidates.Where(c => c.Status == "completed"
                    && c.PromptTokensPerSecond > 0 && c.DecodeTokensPerSecond > 0).ToList();
            if (successful.Count == 0)
                throw new InvalidOperationException("No performance candidate completed successfully; the existing safe runtime profile was left unchanged.");

            double maxPrompt = successful.Max(c => c.PromptTokensPerSecond!.Value);
            double maxDecode = successful.Max(c => c.DecodeTokensPerSecond!.Value);
            foreach (RuntimePerformanceCandidateResult candidate in successful)
                candidate.Score = 0.30 * candidate.PromptTokensPerSecond!.Value / maxPrompt
                    + 0.70 * candidate.DecodeTokensPerSecond!.Value / maxDecode;

            RuntimePerformanceCandidateResult winner = successful
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Profile.BatchSize)
                .First();

            FileInfo model = new(entry.Status.ModelPath);
            string backendKey = await _process.HasCudaHeavyGpuAsync(gaming.Token).ConfigureAwait(false)
                ? "text-cuda" : "text-vulkan";
            string runtimeTag = LocalAiPaths.GetActiveRuntimeBuildTag(backendKey)
                ?? LocalAiPaths.DefaultRuntimeBuildTag(backendKey);
            string gpuFingerprint = BuildGpuFingerprint(_gpus.GetStatus());
            string key = BuildSelectionKey(model, entry.Status.Workload, runtimeTag, gpuFingerprint);
            _store.Save(new RuntimePerformanceProfileSelection
            {
                Key = key,
                ModelPath = model.FullName,
                ModelSizeBytes = model.Length,
                ModelLastWriteUtcTicks = model.LastWriteTimeUtc.Ticks,
                Workload = entry.Status.Workload,
                RuntimeBuildTag = runtimeTag,
                Backend = winner.Backend ?? backendKey,
                HardwareKey = gpuFingerprint,
                Profile = CloneProfile(winner.Profile),
                PromptTokensPerSecond = winner.PromptTokensPerSecond!.Value,
                DecodeTokensPerSecond = winner.DecodeTokensPerSecond!.Value,
                TunedUtc = DateTime.UtcNow
            });

            lock (entry.Gate)
            {
                entry.Status.Winner = CloneProfile(winner.Profile);
                entry.Status.Backend = winner.Backend;
                entry.Status.HardwareKey = gpuFingerprint;
                entry.Status.Status = "completed";
                entry.Status.Stage = $"Optimized: {winner.Profile.DisplayName} · ctx {winner.Profile.ContextSize:N0} · pp {winner.Profile.BenchmarkPromptTokens:N0} · {winner.DecodeTokensPerSecond:0.0} decode tok/s · {winner.PromptTokensPerSecond:0} prompt tok/s";
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            Set(entry, "cancelled", gaming?.Triggered == true
                ? "Gaming Mode started; GPU tuning was stopped. Existing runtime tuning was not changed."
                : "Cancelled. Existing runtime tuning was not changed.");
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                entry.Status.Status = "failed";
                entry.Status.Stage = "Tuning failed; existing runtime tuning was not changed.";
                entry.Status.Error = ex.Message;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            try
            {
                if (restoreText) await _process.RestoreTextProfileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            gaming?.Dispose();
            if (acquiredQueue) _queue.Release();
            entry.Cancellation.Dispose();
        }
    }

    /// <summary>Legacy/test overload retains the original conservative matrix and therefore does
    /// not make assumptions from an unavailable model path or GPU product identity.</summary>
    internal static List<LlamaPerformanceProfileDto> BuildCandidates(long modelSizeBytes, long largestVramBytes, string workload)
    {
        workload = NormalizeWorkload(workload);
        int context = RecommendContext(modelSizeBytes, largestVramBytes, workload);
        return BuildCandidateMatrix(modelSizeBytes, largestVramBytes, workload, context, null);
    }

    /// <summary>Production matrix. Model geometry can expand long-context to the real safe capacity
    /// (up to 1M) and RTX 50 hardware adds wider Blackwell candidates without removing generic ones.</summary>
    internal static List<LlamaPerformanceProfileDto> BuildCandidates(
        string modelPath,
        long modelSizeBytes,
        GpuInventoryStatus hardware,
        string workload)
    {
        workload = NormalizeWorkload(workload);
        long largestVram = hardware?.Available == true ? hardware.LargestDedicatedVideoMemoryBytes : 0;
        int context = RecommendContext(modelSizeBytes, largestVram, workload, modelPath);
        return BuildCandidateMatrix(modelSizeBytes, largestVram, workload, context, Rtx50SeriesOptimization.Detect(hardware));
    }

    private static List<LlamaPerformanceProfileDto> BuildCandidateMatrix(
        long modelSizeBytes,
        long largestVramBytes,
        string workload,
        int context,
        Rtx50SeriesOptimization.Envelope? rtx50)
    {
        int promptTokens = LongContextPerformancePlan.SelectQuickProbeTokens(context, workload);
        var candidates = new List<LlamaPerformanceProfileDto>
        {
            Profile("balanced-q8", "Balanced Q8", 512, 128, "q8_0", context, promptTokens),
            Profile("prefill-q8", "Fast prefill Q8", 1024, 256, "q8_0", context, promptTokens),
            Profile("max-prefill-q8", "Maximum prefill Q8", 2048, 512, "q8_0", context, promptTokens)
        };

        if (largestVramBytes > 0)
        {
            long estimatedWeights = SaturatingAdd((long)Math.Ceiling(Math.Max(0, modelSizeBytes) * 1.16), 768L * 1024 * 1024);
            if (largestVramBytes - estimatedWeights >= 6L * 1024 * 1024 * 1024)
                candidates.Add(Profile("prefill-f16", "Fast prefill F16 KV", 1024, 256, "f16", context, promptTokens));
        }

        if (rtx50 != null)
        {
            LlamaPerformanceProfileDto blackwell = Profile(
                "blackwell-wide-q8",
                "RTX 50 measured wide-prefill candidate",
                rtx50.BenchmarkBatchSize,
                rtx50.BenchmarkUbatchSize,
                "q8_0",
                context,
                promptTokens);
            blackwell.MainGpu = rtx50.AdapterIndex;
            blackwell.FitTargetMib = rtx50.FitTargetMib;
            // Concurrency remains 1 in the quick llama-bench phase. C2/C4/C8 are promoted only by
            // the real server probe where aggregate throughput, TTFT, quality and thermals exist.
            blackwell.ParallelSlots = 1;
            candidates.Add(blackwell);
        }
        return candidates;
    }

    internal static int RecommendContext(long modelSizeBytes, long largestVramBytes, string workload)
        => RecommendContext(modelSizeBytes, largestVramBytes, workload, modelPath: null);

    /// <summary>
    /// The context the runtime is actually launched with. When model geometry is available,
    /// long-context is allowed to consume the capacity planner's real safe recommendation all the
    /// way to PaRuX's 1M ceiling. Chat/coding keep their smaller latency-oriented preferences.
    /// Without model geometry the legacy conservative brackets remain unchanged rather than
    /// guessing at KV bytes/token from file size.
    /// </summary>
    internal static int RecommendContext(long modelSizeBytes, long largestVramBytes, string workload, string? modelPath)
    {
        workload = NormalizeWorkload(workload);
        if (largestVramBytes <= 0) return workload == "chat" ? 8192 : 16384;

        long estimatedWeights = SaturatingAdd((long)Math.Ceiling(Math.Max(0, modelSizeBytes) * 1.16), 768L * 1024 * 1024);
        long headroom = Math.Max(0, largestVramBytes - estimatedWeights);
        long GiB = 1024L * 1024 * 1024;
        int preferred = workload switch
        {
            "chat" => headroom >= 6 * GiB ? 16384 : 8192,
            "coding" => headroom >= 8 * GiB ? 32768 : 16384,
            "long-context" => headroom >= 12 * GiB ? 65536 : 32768,
            _ => 8192
        };

        // The estimate above already clamped to the "not much room" bracket, but on a genuinely
        // borderline card - weights alone consuming effectively all VRAM (headroom rounds to zero)
        // - even that bracket's context still leaves too little space for its own KV cache, and the
        // untuned launch silently lets llama.cpp's --fit spill most layers to CPU to make room:
        // GPU utilization near 0%, prefill an order of magnitude slower than the same model actually
        // achieves once it fits. Measured proof, not a guess: on this exact 27B model and a 16GB-class
        // card with headroom rounding to zero, "Optimize for this PC" independently converged on
        // contextSize=8192 for both the chat and coding workloads (were: 16384 preferred for coding,
        // capacity-planner-dependent for chat) and that measured profile ran at 1666-2179 prompt
        // tok/s fully GPU-resident, versus ~75-150 tok/s CPU-bound at this bracket's original context.
        // Halving again here trades window size for actually landing on the GPU by default, with no
        // prior benchmark required - the trade a user in this position would obviously want.
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return preferred;

        // Apply this hardware-specific fallback only when the real model file is available. A
        // caller that supplies only an approximate byte count must retain the documented workload
        // classes; otherwise a conservative estimate can silently collapse coding/long-context to
        // chat size even though the actual GGUF geometry is unknown.
        GgufModelInfo model = GgufMetadataReader.Read(modelPath);
        // llama.cpp's --fit keeps --fit-target (1024 MiB) free on the device. The DXGI dedicated
        // memory used here already reports ~330 MiB less than the driver total on the reference
        // RTX 5080, which covers the idle desktop's own VRAM use (0-280 MiB measured).
        ContextCapacityPlanner.ContextCapacity capacity = ContextCapacityPlanner.Plan(
            model, Math.Max(0, largestVramBytes - FitTargetReserveBytes), Math.Max(0, modelSizeBytes));
        return ChooseContext(workload, preferred, headroom, capacity);
    }

    private const long FitTargetReserveBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// With real GGUF geometry the capacity planner (weights + per-token KV + runtime reserve) is
    /// authoritative; the coarse file-size estimate above (x1.16 + 768 MiB) only guards launches
    /// whose geometry is unknown. Measured 2026-09-29 on RTX 5080 16 GB: Qwen3.8-27B IQ4_XS ran
    /// fully on the GPU at 32,768 context (14.4 GB VRAM, prefill 1,514 tok/s, decode 47.5 tok/s -
    /// no loss against 8,192), yet the coarse estimate left "zero" headroom and pinned coding to
    /// 8,192, which the coding agent itself reports as too small for its protocol.
    /// </summary>
    internal static int ChooseContext(
        string workload, int coarsePreferred, long coarseHeadroomBytes, ContextCapacityPlanner.ContextCapacity capacity)
    {
        long GiB = 1024L * 1024 * 1024;
        if (!capacity.Known || capacity.Recommended <= 0)
            return coarseHeadroomBytes <= GiB / 4 && coarsePreferred > 8192 ? 8192 : coarsePreferred;

        if (workload == "long-context")
            return LongContextPerformancePlan.AutomaticLongContext(capacity.Recommended);
        int geometryPreferred = workload == "coding" ? Math.Max(coarsePreferred, 32768) : coarsePreferred;
        return (int)Math.Min(geometryPreferred, capacity.Recommended);
    }

    public static string NormalizeWorkload(string? workload) => (workload ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "chat" or "normal" => "chat",
        "code" or "coding" or "agent" => "coding",
        "long" or "long-context" or "long_context" or "rag" => "long-context",
        _ => throw new InvalidDataException("workload must be chat, coding, or long-context.")
    };

    public static string BuildGpuFingerprint(GpuInventoryStatus hardware) =>
        BuildGpuFingerprint(hardware, HostEvaluationFacts.Current());

    internal static string BuildGpuFingerprint(GpuInventoryStatus? hardware, HostEvaluationFacts hostFacts) =>
        EvaluationHardwareFingerprint.Build(hardware, hostFacts).Fingerprint;

    private static LlamaPerformanceProfileDto Profile(
        string id, string name, int batch, int ubatch, string cache, int context, int benchmarkPromptTokens = 1024) => new()
    {
        Id = id,
        DisplayName = name,
        BatchSize = batch,
        UBatchSize = ubatch,
        CacheTypeK = cache,
        CacheTypeV = cache,
        FlashAttention = "on",
        GpuLayers = -1,
        SplitMode = "layer",
        MainGpu = 0,
        ContextSize = context,
        FitTargetMib = 1024,
        BenchmarkPromptTokens = benchmarkPromptTokens
    };

    private static string ValidateModelPath(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) throw new InvalidDataException("A model path is required.");
        string full = Path.GetFullPath(modelPath);
        if (!full.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only GGUF text models can be performance-tuned.");
        if (!File.Exists(full)) throw new FileNotFoundException("The model file was not found.", full);
        if (!IsInside(full, LocalAiPaths.ModelsPath) && !IsInside(full, ModelDownloadService.ModelsDirectory))
            throw new InvalidDataException("The model must be inside a configured PaRuX model directory.");
        return full;
    }

    private static bool IsInside(string path, string directory)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSelectionKey(FileInfo model, string workload, string runtimeTag, string gpuFingerprint)
    {
        string identity = model.FullName.ToUpperInvariant() + "|" + model.Length + "|" + model.LastWriteTimeUtc.Ticks
            + "|" + workload + "|" + runtimeTag + "|" + gpuFingerprint;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static void Set(RunEntry entry, string status, string stage)
    {
        lock (entry.Gate)
        {
            entry.Status.Status = status;
            entry.Status.Stage = stage;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
    }

    private static RuntimePerformanceTuneRun Clone(RuntimePerformanceTuneRun value) => new()
    {
        Id = value.Id,
        Status = value.Status,
        Stage = value.Stage,
        ModelPath = value.ModelPath,
        Label = value.Label,
        Workload = value.Workload,
        Candidates = value.Candidates.Select(CloneCandidate).ToList(),
        Winner = value.Winner == null ? null : CloneProfile(value.Winner),
        Backend = value.Backend,
        HardwareKey = value.HardwareKey,
        Error = value.Error,
        StartedUtc = value.StartedUtc,
        UpdatedUtc = value.UpdatedUtc
    };

    private static RuntimePerformanceCandidateResult CloneCandidate(RuntimePerformanceCandidateResult value) => new()
    {
        Profile = CloneProfile(value.Profile),
        Status = value.Status,
        PromptTokensPerSecond = value.PromptTokensPerSecond,
        DecodeTokensPerSecond = value.DecodeTokensPerSecond,
        TimeToFirstTokenMilliseconds = value.TimeToFirstTokenMilliseconds,
        LoadMilliseconds = value.LoadMilliseconds,
        SustainedDecodeTokensPerSecond = value.SustainedDecodeTokensPerSecond,
        ConcurrentDecodeTokensPerSecond = value.ConcurrentDecodeTokensPerSecond,
        ConcurrentRequests = value.ConcurrentRequests,
        AverageGpuPowerWatts = value.AverageGpuPowerWatts,
        PeakGpuPowerWatts = value.PeakGpuPowerWatts,
        MaxGpuTemperatureC = value.MaxGpuTemperatureC,
        PeakGpuMemoryUsedMib = value.PeakGpuMemoryUsedMib,
        Score = value.Score,
        Backend = value.Backend,
        HardwareKey = value.HardwareKey,
        ActualGpuLayers = value.ActualGpuLayers,
        Error = value.Error
    };

    internal static LlamaPerformanceProfileDto CloneProfile(LlamaPerformanceProfileDto value) =>
        RuntimePerformanceProfileCopy.Copy(value);
}
