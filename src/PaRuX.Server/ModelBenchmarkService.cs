using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

public interface IModelDownloadCompletionHandler
{
    void OnGgufDownloaded(string modelPath, string label);
}

public interface IModelBenchmarkRunner
{
    Task<ModelBenchmarkMeasurement> RunAsync(string modelPath, CancellationToken cancellationToken);
}

public sealed record ModelBenchmarkMeasurement(
    double TokensPerSecond, int GeneratedTokens, int Repetitions, string Backend, string HardwareKey);

/// <summary>Runs the native llama.cpp benchmark executable and parses its machine-readable output.
/// No token count is inferred from text: a missing/invalid avg_ts makes the run fail honestly.</summary>
public sealed class LlamaBenchRunner : IModelBenchmarkRunner
{
    private readonly LocalAiProcessManager _process;
    private readonly Func<GpuInventoryStatus> _inventory;

    public LlamaBenchRunner(LocalAiProcessManager process, Func<GpuInventoryStatus>? inventory = null)
    {
        _process = process;
        _inventory = inventory ?? (() => new GpuInventoryService().GetStatus());
    }

    public async Task<ModelBenchmarkMeasurement> RunAsync(string modelPath, CancellationToken cancellationToken)
    {
        if (_process.GamingActive)
            throw new InvalidOperationException("Gaming Mode is active; model benchmarking is blocked.");
        // A game started while llama-bench is on the GPU cancels the run (killing the benchmark
        // process tree below) instead of letting it finish its repetitions.
        using var gaming = new GamingHardOffWatch(() => _process.GamingActive, cancellationToken);
        cancellationToken = gaming.Token;
        if (await _process.IsMediaHealthyAsync(cancellationToken))
            throw new InvalidOperationException("Media generation is active; stop it before benchmarking a text model.");

        bool restoreText = await _process.IsTextHealthyAsync(cancellationToken);
        bool hasGpu = await _process.HasHeavyGpuAsync(cancellationToken);
        // CPU-only means no usable adapter at all, not merely no NVIDIA card: a host with a Vulkan adapter keeps it.
        bool cpuOnly = RuntimeLaunchOptimizer.ResolveCpuOnly(hasGpu, _inventory);
        string runtime = hasGpu ? LocalAiPaths.RuntimeCudaPath : LocalAiPaths.RuntimeVulkanPath;
        string executable = Path.Combine(runtime, LocalAiPaths.LlamaBenchExecutable);
        if (!File.Exists(executable)) throw new FileNotFoundException("llama-bench was not found.", executable);

        if (!_process.StopText())
            throw new InvalidOperationException("The resident text runtime did not stop, so the benchmark was not started.");
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable, WorkingDirectory = runtime, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in LlamaBenchRunner.BuildArguments(modelPath, hasGpu, cpuOnly))
                start.ArgumentList.Add(argument);

            using var benchmark = new Process { StartInfo = start };
            benchmark.Start();
            Task<string> stdoutTask = benchmark.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = benchmark.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(gaming.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            try { await benchmark.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { if (!benchmark.HasExited) benchmark.Kill(entireProcessTree: true); } catch { }
                if (gaming.Triggered)
                    throw new InvalidOperationException("Gaming Mode started during the benchmark; llama-bench was stopped.");
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("llama-bench did not finish within 20 minutes.");
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            if (benchmark.ExitCode != 0)
                throw new InvalidOperationException("llama-bench failed: " + Trim(stderr));

            using JsonDocument document = JsonDocument.Parse(stdout);
            JsonElement measurement = document.RootElement.EnumerateArray()
                .FirstOrDefault(e => e.TryGetProperty("n_gen", out JsonElement n) && n.GetInt32() > 0);
            if (measurement.ValueKind != JsonValueKind.Object
                || !measurement.TryGetProperty("avg_ts", out JsonElement throughput)
                || !throughput.TryGetDouble(out double tokensPerSecond)
                || !double.IsFinite(tokensPerSecond) || tokensPerSecond <= 0)
                throw new InvalidDataException("llama-bench returned no valid generation throughput (avg_ts).");

            int generatedTokens = measurement.GetProperty("n_gen").GetInt32();
            string backend = measurement.TryGetProperty("backends", out JsonElement b) ? b.GetString() ?? "unknown" : "unknown";
            string hardware = measurement.TryGetProperty("gpu_info", out JsonElement g) ? g.GetString() ?? "unknown" : "unknown";
            return new ModelBenchmarkMeasurement(tokensPerSecond, generatedTokens, 3, backend, backend + " | " + hardware);
        }
        finally
        {
            if (restoreText)
            {
                try { await _process.RestoreTextProfileAsync(CancellationToken.None); } catch { }
            }
        }
    }

    /// <summary>
    /// The llama-bench arguments. A Vulkan device is pinned only when a GPU exists to use. On a host with no
    /// usable adapter, -dev would name a device that is not there, so the run offloads nothing (-ngl 0) and
    /// measures the CPU backend, which is the launch the text runtime uses on such a host.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(string modelPath, bool hasGpu, bool cpuOnly)
    {
        var arguments = new List<string>
        {
            "-m", modelPath, "-p", "0", "-n", "128", "-r", "3", "-ngl", cpuOnly ? "0" : "99",
            "-fa", "on", "--offline", "-o", "json"
        };
        if (!hasGpu && !cpuOnly) arguments.AddRange(["-dev", "Vulkan0"]);
        return arguments;
    }

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 500 ? value : value[..500] + "…";
    }
}

/// <summary>Persistent, serial benchmark queue. Automatic post-download failures are recorded in
/// benchmark history and never retroactively turn a valid completed download into a failure.</summary>
public sealed class ModelBenchmarkService : IModelDownloadCompletionHandler
{
    private readonly ModelBenchmarkStore _store;
    private readonly IModelBenchmarkRunner _runner;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public ModelBenchmarkService(ModelBenchmarkStore store, IModelBenchmarkRunner runner)
    {
        _store = store;
        _runner = runner;
    }

    public ModelBenchmarkRun Start(ModelBenchmarkRequest request, bool automatic = false)
    {
        string path = ValidateModelPath(request.ModelPath);
        ModelBenchmarkRun run = _store.Create(path, request.Label, automatic);
        var cancellation = new CancellationTokenSource();
        _active[run.Id] = cancellation;
        _ = Task.Run(() => ExecuteAsync(run.Id, path, cancellation));
        return run;
    }

    public void OnGgufDownloaded(string modelPath, string label)
    {
        try { Start(new ModelBenchmarkRequest { ModelPath = modelPath, Label = label }, automatic: true); }
        catch { /* Download remains valid; the benchmark endpoint/history reports benchmark failures. */ }
    }

    public bool Cancel(string id)
    {
        if (!_active.TryGetValue(id, out CancellationTokenSource? cancellation)) return false;
        cancellation.Cancel();
        return true;
    }

    public ModelBenchmarkRun? Get(string id) => _store.Get(id);
    public IReadOnlyList<ModelBenchmarkRun> ListRuns(string? modelPath = null) => _store.List(modelPath);

    public IReadOnlyList<ModelBenchmarkSummary> ListModels()
    {
        var paths = new Dictionary<string, (string Label, long Size)>(StringComparer.OrdinalIgnoreCase);
        AddModels(paths, LocalAiPaths.ModelsPath);
        AddModels(paths, ModelDownloadService.ModelsDirectory);
        var results = new List<ModelBenchmarkSummary>();
        foreach ((string path, (string label, long size)) in paths)
        {
            (int count, double? average, ModelBenchmarkRun? last) = _store.GetSummary(path);
            ModelBenchmarkRun? latestCompleted = _store.List(path)
                .FirstOrDefault(r => r.Status == "completed" && r.TokensPerSecond.HasValue);
            results.Add(new ModelBenchmarkSummary
            {
                ModelPath = path, Label = label, Usage = DescribeUsage(path), SizeBytes = size, CompletedRunCount = count,
                AverageTokensPerSecond = average, Backend = latestCompleted?.Backend,
                HardwareKey = latestCompleted?.HardwareKey, LastRun = last
            });
        }
        return results.OrderBy(r => UsagePriority(r.Usage))
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal double? GetAverageTokensPerSecond(string modelPath) => _store.GetSummary(modelPath).Average;

    internal static string DescribeUsage(string path)
    {
        if (PathsMatch(path, LocalAiPaths.StandardModel)) return "standard";
        if (PathsMatch(path, LocalAiPaths.UncensoredModel)) return "uncensored";
        if (LocalAiPaths.IsFallbackOnlyModel(path)) return "fallback";
        return "installed";
    }

    private static int UsagePriority(string usage) => usage switch
    {
        "standard" => 0,
        "uncensored" => 1,
        "installed" => 2,
        "fallback" => 3,
        _ => 4
    };

    private static bool PathsMatch(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

    private async Task ExecuteAsync(string id, string path, CancellationTokenSource cancellation)
    {
        try
        {
            await _queue.WaitAsync(cancellation.Token);
            try
            {
                _store.MarkRunning(id, "Running native llama-bench (128 generated tokens × 3)…");
                ModelBenchmarkMeasurement result = await _runner.RunAsync(path, cancellation.Token);
                _store.Complete(id, result.TokensPerSecond, result.GeneratedTokens,
                    result.Repetitions, result.Backend, result.HardwareKey);
            }
            finally { _queue.Release(); }
        }
        catch (OperationCanceledException) { _store.Cancel(id); }
        catch (Exception ex) { _store.Fail(id, ex.Message); }
        finally
        {
            _active.TryRemove(id, out _);
            cancellation.Dispose();
        }
    }

    private static string ValidateModelPath(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) throw new InvalidDataException("A model path is required.");
        string full = Path.GetFullPath(modelPath);
        if (!full.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only GGUF text models can be benchmarked.");
        if (!File.Exists(full)) throw new FileNotFoundException("The model file was not found.", full);
        if (!IsInside(full, LocalAiPaths.ModelsPath) && !IsInside(full, ModelDownloadService.ModelsDirectory))
            throw new InvalidDataException("The model must be inside a configured PaRuX/LocalAI model directory.");
        return full;
    }

    private static bool IsInside(string path, string directory)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddModels(Dictionary<string, (string Label, long Size)> paths, string directory)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly))
            {
                string full = Path.GetFullPath(file);
                paths.TryAdd(full, (Path.GetFileNameWithoutExtension(full), new FileInfo(full).Length));
            }
        }
        catch { }
    }
}
