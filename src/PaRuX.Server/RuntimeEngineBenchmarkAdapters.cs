using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Production Engine Race adapter for the existing PaRuX-managed llama.cpp CUDA runtime. It reuses
/// the already validated isolated llama-server probe and the same launch optimizer used by normal
/// serving, so Engine Race does not invent a second llama tuning policy.
/// </summary>
public sealed class LlamaCppCudaEngineBenchmarkAdapter : IRuntimeEngineBenchmarkAdapter
{
    private readonly ILlamaServerPerformanceProbe _probe;
    private readonly Func<string, string, CancellationToken, Task<LlamaPerformanceProfileDto>> _profileResolver;

    public LlamaCppCudaEngineBenchmarkAdapter(
        LocalAiProcessManager runtime,
        ILlamaServerPerformanceProbe probe)
        : this(
            probe,
            async (modelPath, workload, ct) =>
            {
                if (!await runtime.HasHeavyGpuAsync(ct).ConfigureAwait(false))
                    throw new InvalidOperationException("llama.cpp CUDA Engine Race requires a CUDA-capable PaRuX workstation.");
                RuntimeLaunchOptimization launch = await RuntimeLaunchOptimizer.ResolveAsync(
                    modelPath,
                    workload,
                    hasCuda: true,
                    fallbackBatch: 512,
                    fallbackUbatch: 128,
                    fallbackCache: "q8_0",
                    ct).ConfigureAwait(false);
                return launch.Profile;
            })
    {
    }

    internal LlamaCppCudaEngineBenchmarkAdapter(
        ILlamaServerPerformanceProbe probe,
        Func<string, string, CancellationToken, Task<LlamaPerformanceProfileDto>> profileResolver)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
    }

    public string EngineId => LocalInferenceEngineRegistry.LlamaCppCudaId;

    public string RuntimeVersion => LocalAiPaths.GetActiveRuntimeBuildTag("text-cuda")
        ?? LocalAiPaths.DefaultRuntimeBuildTag("text-cuda");

    public bool CanBenchmark(ManagedModelVariantIdentity variant) =>
        variant != null
        && variant.Format == ManagedModelFormat.Gguf
        && !string.IsNullOrWhiteSpace(variant.ArtifactPath)
        && variant.ArtifactPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        && File.Exists(variant.ArtifactPath);

    public async Task<RuntimeEngineBenchmarkMeasurement> MeasureAsync(
        ManagedModelVariantIdentity variant,
        string workload,
        bool sustainedValidation,
        CancellationToken cancellationToken)
    {
        if (!CanBenchmark(variant))
            throw new InvalidDataException("llama.cpp CUDA Engine Race requires an existing managed GGUF variant.");

        LlamaPerformanceProfileDto profile = await _profileResolver(
            variant.ArtifactPath,
            workload,
            cancellationToken).ConfigureAwait(false);
        LlamaServerPerformanceMeasurement measured = await _probe.RunAsync(
            variant.ArtifactPath,
            workload,
            profile,
            sustainedValidation,
            cancellationToken).ConfigureAwait(false);

        return new RuntimeEngineBenchmarkMeasurement(
            measured.PromptTokensPerSecond,
            measured.DecodeTokensPerSecond,
            measured.TimeToFirstTokenMilliseconds,
            measured.SustainedDecodeTokensPerSecond ?? measured.DecodeTokensPerSecond,
            measured.PeakGpuMemoryUsedMib ?? 0,
            measured.MaxGpuTemperatureC ?? 0,
            ErrorRate: 0,
            LoadMilliseconds: measured.LoadMilliseconds,
            AverageGpuPowerWatts: measured.AverageGpuPowerWatts);
    }
}

internal sealed class ExLlamaV3BenchmarkHostRecord
{
    [JsonPropertyName("api_version")]
    public int ApiVersion { get; set; }

    [JsonPropertyName("prompt_tokens_per_second")]
    public double PromptTokensPerSecond { get; set; }

    [JsonPropertyName("decode_tokens_per_second")]
    public double DecodeTokensPerSecond { get; set; }

    [JsonPropertyName("time_to_first_token_milliseconds")]
    public double TimeToFirstTokenMilliseconds { get; set; }

    [JsonPropertyName("sustained_decode_tokens_per_second")]
    public double SustainedDecodeTokensPerSecond { get; set; }

    [JsonPropertyName("peak_vram_mib")]
    public double PeakVramMib { get; set; }

    [JsonPropertyName("load_milliseconds")]
    public double LoadMilliseconds { get; set; }

    [JsonPropertyName("generated_tokens")]
    public int GeneratedTokens { get; set; }

    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }
}

/// <summary>
/// Direct PaRuX-owned ExLlamaV3 benchmark adapter. It invokes only the Python executable inside an
/// already installed PaRuX ExLlamaV3 pack and the benchmark host shipped with PaRuX Server. No
/// TabbyAPI, system Python, source build, network access or user environment is required.
/// </summary>
public sealed class ExLlamaV3EngineBenchmarkAdapter : IRuntimeEngineBenchmarkAdapter
{
    internal const int HostApiVersion = 1;
    private readonly string _packTag;
    private readonly string _pythonPath;
    private readonly string _scriptPath;
    private readonly GpuPerformanceTelemetryService _telemetry;

    public ExLlamaV3EngineBenchmarkAdapter(
        string packTag,
        string pythonPath,
        GpuPerformanceTelemetryService? telemetry = null,
        string? scriptPath = null)
    {
        if (string.IsNullOrWhiteSpace(packTag)) throw new ArgumentException("ExLlamaV3 pack tag is required.", nameof(packTag));
        if (string.IsNullOrWhiteSpace(pythonPath)) throw new ArgumentException("Managed ExLlamaV3 Python path is required.", nameof(pythonPath));
        _packTag = packTag.Trim();
        _pythonPath = Path.GetFullPath(pythonPath);
        _scriptPath = Path.GetFullPath(scriptPath ?? Path.Combine(
            AppContext.BaseDirectory,
            "exllamav3-benchmark",
            "parux_exllamav3_benchmark.py"));
        _telemetry = telemetry ?? new GpuPerformanceTelemetryService();
    }

    public string EngineId => LocalInferenceEngineRegistry.ExLlamaV3Id;
    public string RuntimeVersion => _packTag;

    public bool CanBenchmark(ManagedModelVariantIdentity variant)
    {
        if (variant == null || variant.Format != ManagedModelFormat.Exl3
            || string.IsNullOrWhiteSpace(variant.ArtifactPath)) return false;
        (bool valid, _) = MultiFormatModelCatalog.ValidateStructure(
            ManagedModelFormat.Exl3,
            Path.GetFullPath(variant.ArtifactPath));
        return valid;
    }

    public async Task<RuntimeEngineBenchmarkMeasurement> MeasureAsync(
        ManagedModelVariantIdentity variant,
        string workload,
        bool sustainedValidation,
        CancellationToken cancellationToken)
    {
        if (!CanBenchmark(variant))
            throw new InvalidDataException("ExLlamaV3 Engine Race requires a validated EXL3 model directory.");
        if (!File.Exists(_pythonPath))
            throw new FileNotFoundException("The selected PaRuX-managed ExLlamaV3 Python runtime is missing.", _pythonPath);
        if (!File.Exists(_scriptPath))
            throw new FileNotFoundException("PaRuX ExLlamaV3 benchmark host is missing from the Server distribution.", _scriptPath);

        workload = RuntimePerformanceTuner.NormalizeWorkload(workload);
        var start = new ProcessStartInfo
        {
            FileName = _pythonPath,
            WorkingDirectory = Path.GetDirectoryName(_scriptPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(_scriptPath);
        start.ArgumentList.Add(Path.GetFullPath(variant.ArtifactPath));
        start.ArgumentList.Add("--parux-workload");
        start.ArgumentList.Add(workload);
        if (sustainedValidation) start.ArgumentList.Add("--parux-sustained");
        ManagedPythonRuntime.ApplyEnvironment(start);

        using var process = new Process { StartInfo = start };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromMinutes(20));
        var samples = new List<GpuPerformanceTelemetryDto>();
        Task telemetryTask = SampleTelemetryAsync(samples, linked.Token);
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not start the PaRuX ExLlamaV3 benchmark host.");
            ChildProcessJob.Assign(process);
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("PaRuX ExLlamaV3 benchmark failed: " + Trim(stderr.Length > 0 ? stderr : stdout));

            ExLlamaV3BenchmarkHostRecord host = ParseHostRecord(stdout);
            linked.Cancel();
            try { await telemetryTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

            List<double> power;
            List<double> temperature;
            List<int> memory;
            lock (samples)
            {
                power = samples.Where(sample => sample.PowerWatts.HasValue)
                    .Select(sample => sample.PowerWatts!.Value).ToList();
                temperature = samples.Where(sample => sample.TemperatureC.HasValue)
                    .Select(sample => sample.TemperatureC!.Value).ToList();
                memory = samples.Where(sample => sample.MemoryUsedMib.HasValue)
                    .Select(sample => sample.MemoryUsedMib!.Value).ToList();
            }
            double peakVram = Math.Max(host.PeakVramMib, memory.Count == 0 ? 0 : memory.Max());

            return new RuntimeEngineBenchmarkMeasurement(
                host.PromptTokensPerSecond,
                host.DecodeTokensPerSecond,
                host.TimeToFirstTokenMilliseconds,
                host.SustainedDecodeTokensPerSecond,
                peakVram,
                temperature.Count == 0 ? 0 : temperature.Max(),
                ErrorRate: 0,
                LoadMilliseconds: host.LoadMilliseconds,
                AverageGpuPowerWatts: power.Count == 0 ? null : power.Average());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("PaRuX ExLlamaV3 benchmark exceeded the 20 minute safety timeout.");
        }
        finally
        {
            linked.Cancel();
            try { await telemetryTask.ConfigureAwait(false); } catch { }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch { }
        }
    }

    internal static ExLlamaV3BenchmarkHostRecord ParseHostRecord(string stdout)
    {
        string? json = (stdout ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{') && line.EndsWith('}'));
        if (json == null)
            throw new InvalidDataException("PaRuX ExLlamaV3 benchmark returned no machine-readable result.");

        ExLlamaV3BenchmarkHostRecord result;
        try
        {
            result = JsonSerializer.Deserialize<ExLlamaV3BenchmarkHostRecord>(json)
                ?? throw new InvalidDataException("PaRuX ExLlamaV3 benchmark result was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("PaRuX ExLlamaV3 benchmark returned invalid JSON.", ex);
        }

        if (result.ApiVersion != HostApiVersion)
            throw new InvalidDataException($"Unsupported PaRuX ExLlamaV3 benchmark protocol version {result.ApiVersion}.");
        ValidatePositive(result.PromptTokensPerSecond, "prompt throughput");
        ValidatePositive(result.DecodeTokensPerSecond, "decode throughput");
        ValidatePositive(result.TimeToFirstTokenMilliseconds, "TTFT");
        ValidatePositive(result.SustainedDecodeTokensPerSecond, "sustained decode throughput");
        ValidatePositive(result.LoadMilliseconds, "load time");
        if (!double.IsFinite(result.PeakVramMib) || result.PeakVramMib < 0)
            throw new InvalidDataException("PaRuX ExLlamaV3 benchmark returned invalid peak VRAM.");
        if (result.GeneratedTokens <= 0 || result.PromptTokens <= 0)
            throw new InvalidDataException("PaRuX ExLlamaV3 benchmark returned invalid token counts.");
        return result;
    }

    private async Task SampleTelemetryAsync(List<GpuPerformanceTelemetryDto> samples, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                List<GpuPerformanceTelemetryDto> current = await _telemetry.QueryAsync(ct).ConfigureAwait(false);
                lock (samples) samples.AddRange(current);
            }
            catch (OperationCanceledException) { break; }
            catch { }
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static void ValidatePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new InvalidDataException("PaRuX ExLlamaV3 benchmark returned invalid " + name + ".");
    }

    private static string Trim(string value)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= 2000 ? value : value[^2000..];
    }
}
