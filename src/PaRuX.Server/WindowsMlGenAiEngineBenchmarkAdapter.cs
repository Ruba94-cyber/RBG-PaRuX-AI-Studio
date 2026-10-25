using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Engine Race adapter for the same PaRuX.WinMlWorker resident process used by interactive serving.
/// No synthetic ONNX microbenchmark is accepted: the measured path loads the exact GenAI model,
/// exact Windows ML EP and token loop that AUTO would later run.
/// </summary>
public sealed class WindowsMlGenAiEngineBenchmarkAdapter : IRuntimeEngineBenchmarkAdapter
{
    private const int LongContextMinimumPromptTokens = 4096;
    private readonly string _providerName;
    private readonly string? _adapterPin;
    private readonly string _runtimeIdentity;
    private readonly string _workerPath;
    private readonly GpuPerformanceTelemetryService _telemetry;

    public WindowsMlGenAiEngineBenchmarkAdapter(
        string providerName,
        string runtimeIdentity,
        string workerPath,
        GpuPerformanceTelemetryService? telemetry = null)
    {
        _providerName = WindowsMlGenAiProtocol.ValidateProviderName(providerName);
        _runtimeIdentity = runtimeIdentity?.Trim() ?? throw new ArgumentNullException(nameof(runtimeIdentity));
        if (!WindowsMlGenAiProtocol.TryParseRuntimeIdentity(_runtimeIdentity, out string identityProvider, out _, out _, out _adapterPin)
            || !string.Equals(identityProvider, _providerName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Windows ML benchmark runtime identity does not match its provider.");
        _workerPath = Path.GetFullPath(workerPath ?? throw new ArgumentNullException(nameof(workerPath)));
        _telemetry = telemetry ?? new GpuPerformanceTelemetryService();
    }

    public string EngineId => LocalInferenceEngineRegistry.WindowsMlGenAiId;
    public string RuntimeVersion => _runtimeIdentity;

    public bool CanBenchmark(ManagedModelVariantIdentity variant) =>
        variant != null
        && variant.Format == ManagedModelFormat.Onnx
        && !string.IsNullOrWhiteSpace(variant.ArtifactPath)
        && WindowsMlGenAiProtocol.HasServingLayout(variant.ArtifactPath, out _);

    public async Task<RuntimeEngineBenchmarkMeasurement> MeasureAsync(
        ManagedModelVariantIdentity variant,
        string workload,
        bool sustainedValidation,
        CancellationToken cancellationToken)
    {
        if (!CanBenchmark(variant))
            throw new InvalidDataException("Windows ML GenAI Engine Race requires a complete ORT GenAI ONNX model directory.");
        if (!File.Exists(_workerPath))
            throw new FileNotFoundException("PaRuX Windows ML worker is missing.", _workerPath);

        workload = RuntimePerformanceTuner.NormalizeWorkload(workload);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromMinutes(20));
        var samples = new List<GpuPerformanceTelemetryDto>();
        Task telemetryTask = SampleTelemetryAsync(samples, linked.Token);
        var start = new ProcessStartInfo
        {
            FileName = _workerPath,
            WorkingDirectory = Path.GetDirectoryName(_workerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add(Path.GetFullPath(variant.ArtifactPath));
        start.ArgumentList.Add(_providerName);
        if (_adapterPin != null) start.ArgumentList.Add(_adapterPin);

        using var process = new Process { StartInfo = start };
        try
        {
            Stopwatch load = Stopwatch.StartNew();
            if (!process.Start()) throw new InvalidOperationException("Could not start the PaRuX Windows ML benchmark worker.");
            ChildProcessJob.Assign(process);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            string? readyLine = await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false);
            load.Stop();
            if (readyLine == null) throw new InvalidOperationException("Windows ML benchmark worker exited during model load.");
            WindowsMlGenAiWorkerEvent ready = WindowsMlGenAiProtocol.ParseWorkerEvent(readyLine);
            if (ready.Type == "error") throw new InvalidOperationException(ready.Error ?? "Windows ML benchmark worker failed during model load.");
            string executionDevice = WindowsMlGenAiProtocol.ValidateReadyHandshake(
                ready, _providerName, requireUnambiguousDevice: true, requireThinkingControl: true,
                expectedAdapterPin: _adapterPin);

            BenchmarkPass primary = await RunPassAsync(
                process,
                BenchmarkPrompt(workload, sustained: false),
                maxNewTokens: 192,
                linked.Token).ConfigureAwait(false);
            if (workload == "long-context" && primary.PromptTokens < LongContextMinimumPromptTokens)
                throw new InvalidDataException(
                    "Windows ML long-context race did not actually exercise a long prompt: "
                    + primary.PromptTokens + " tokens observed; at least " + LongContextMinimumPromptTokens + " required.");

            BenchmarkPass sustained = sustainedValidation
                ? await RunSustainedValidationAsync(process, workload, linked.Token).ConfigureAwait(false)
                : primary;

            linked.Cancel();
            try { await telemetryTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            TelemetrySummary telemetry = SummarizeTelemetry(samples);

            return new RuntimeEngineBenchmarkMeasurement(
                PromptTokensPerSecond: primary.PromptTokensPerSecond,
                DecodeTokensPerSecond: primary.DecodeTokensPerSecond,
                TimeToFirstTokenMilliseconds: primary.TimeToFirstTokenMilliseconds,
                SustainedDecodeTokensPerSecond: sustained.DecodeTokensPerSecond,
                PeakVramMib: telemetry.PeakVramMib ?? 0,
                MaxGpuTemperatureC: telemetry.MaxTemperatureC ?? 0,
                ErrorRate: 0,
                LoadMilliseconds: Math.Max(0.001, load.Elapsed.TotalMilliseconds),
                AverageGpuPowerWatts: telemetry.AveragePowerWatts,
                ExecutionDeviceIdentity: executionDevice,
                ExecutionDeviceEvidence: ready.DeviceEvidence,
                MissingTelemetry: telemetry.MissingFields);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("PaRuX Windows ML GenAI benchmark exceeded the 20 minute safety timeout.");
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

    private static async Task<BenchmarkPass> RunSustainedValidationAsync(
        Process process,
        string workload,
        CancellationToken cancellationToken)
    {
        TimeSpan required = SustainedValidationDuration();
        Stopwatch duration = Stopwatch.StartNew();
        var passes = new List<BenchmarkPass>();
        do
        {
            BenchmarkPass pass = await RunPassAsync(
                process,
                BenchmarkPrompt(workload, sustained: true),
                maxNewTokens: 512,
                cancellationToken).ConfigureAwait(false);
            if (workload == "long-context" && pass.PromptTokens < LongContextMinimumPromptTokens)
                throw new InvalidDataException("Sustained long-context validation fell below the required real prompt length.");
            passes.Add(pass);
        }
        while (duration.Elapsed < required);

        // Weight by actual decoded token count/time rather than averaging per-request tok/s.
        int tokens = passes.Sum(pass => pass.DecodeTokenCount);
        double milliseconds = passes.Sum(pass => pass.DecodeMilliseconds);
        double decodeTps = tokens / Math.Max(0.001, milliseconds / 1000d);
        if (!double.IsFinite(decodeTps) || decodeTps <= 0)
            throw new InvalidDataException("Windows ML sustained validation produced invalid decode evidence.");
        return passes[^1] with { DecodeTokensPerSecond = decodeTps };
    }

    internal static TimeSpan SustainedValidationDuration()
    {
        string? raw = Environment.GetEnvironmentVariable("PARUX_ENGINE_RACE_SUSTAINED_SECONDS");
        if (int.TryParse(raw, out int seconds))
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 900));
        return TimeSpan.FromSeconds(60);
    }

    private static async Task<BenchmarkPass> RunPassAsync(
        Process process,
        string prompt,
        int maxNewTokens,
        CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString("N");
        string command = JsonSerializer.Serialize(new
        {
            apiVersion = WindowsMlGenAiProtocol.WorkerApiVersion,
            id,
            op = "generate",
            messages = new[]
            {
                new { role = "system", content = "You are a precise local benchmark assistant. Follow the user request directly." },
                new { role = "user", content = prompt }
            },
            maxNewTokens,
            temperature = 0.0,
            workload = "benchmark",
            enableThinking = false
        });

        Stopwatch wall = Stopwatch.StartNew();
        await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        double? firstTokenMilliseconds = null;
        int promptTokens = 0;
        int generatedTokens = 0;
        double firstTokenAt = 0;

        while (true)
        {
            string? line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null) throw new InvalidOperationException("Windows ML benchmark worker exited during generation.");
            WindowsMlGenAiWorkerEvent evt = WindowsMlGenAiProtocol.ParseWorkerEvent(line);
            if (!string.IsNullOrEmpty(evt.Id) && !string.Equals(evt.Id, id, StringComparison.Ordinal))
                throw new InvalidDataException("Windows ML benchmark worker returned an event for another request.");
            promptTokens = Math.Max(promptTokens, evt.PromptTokens);
            generatedTokens = Math.Max(generatedTokens, evt.GeneratedTokens);
            if (evt.Type == "error") throw new InvalidOperationException(evt.Error ?? "Windows ML benchmark generation failed.");
            if (evt.Type == "delta" && !firstTokenMilliseconds.HasValue)
            {
                firstTokenMilliseconds = Math.Max(0.001, wall.Elapsed.TotalMilliseconds);
                firstTokenAt = wall.Elapsed.TotalMilliseconds;
            }
            if (evt.Type == "done") break;
        }
        wall.Stop();
        if (!firstTokenMilliseconds.HasValue || promptTokens <= 0 || generatedTokens <= 0)
            throw new InvalidDataException("Windows ML benchmark did not return valid token/timing evidence.");

        // TTFT includes prompt prefill plus the first decoded token. This is intentionally exposed as
        // a conservative client-derived prompt-throughput estimate, never as a kernel prefill timer.
        double promptTps = promptTokens / (firstTokenMilliseconds.Value / 1000d);
        double decodeMilliseconds = Math.Max(0.001, wall.Elapsed.TotalMilliseconds - firstTokenAt);
        int postFirstTokens = Math.Max(1, generatedTokens - 1);
        double decodeTps = postFirstTokens / (decodeMilliseconds / 1000d);
        if (!double.IsFinite(promptTps) || promptTps <= 0 || !double.IsFinite(decodeTps) || decodeTps <= 0)
            throw new InvalidDataException("Windows ML benchmark produced invalid throughput evidence.");
        return new BenchmarkPass(
            promptTps,
            decodeTps,
            firstTokenMilliseconds.Value,
            promptTokens,
            postFirstTokens,
            decodeMilliseconds);
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

    private static TelemetrySummary SummarizeTelemetry(List<GpuPerformanceTelemetryDto> samples)
    {
        lock (samples)
        {
            List<int> memory = samples.Where(sample => sample.MemoryUsedMib.HasValue).Select(sample => sample.MemoryUsedMib!.Value).ToList();
            List<double> temperature = samples.Where(sample => sample.TemperatureC.HasValue).Select(sample => sample.TemperatureC!.Value).ToList();
            List<double> power = samples.Where(sample => sample.PowerWatts.HasValue).Select(sample => sample.PowerWatts!.Value).ToList();
            var missing = new List<string>();
            if (memory.Count == 0) missing.Add("peakVramMib");
            if (temperature.Count == 0) missing.Add("maxGpuTemperatureC");
            if (power.Count == 0) missing.Add("averageGpuPowerWatts");
            return new TelemetrySummary(
                memory.Count == 0 ? null : memory.Max(),
                temperature.Count == 0 ? null : temperature.Max(),
                power.Count == 0 ? null : power.Average(),
                missing);
        }
    }

    private static string BenchmarkPrompt(string workload, bool sustained) => workload switch
    {
        "coding" => sustained
            ? "Design a robust bounded concurrent work queue in C#. Explain cancellation, ordering, backpressure, failure isolation, and provide a compact implementation with tests."
            : "Write a C# method that validates and atomically saves a JSON settings file, with concise error handling.",
        "long-context" => BuildLongContextPrompt(sustained),
        _ => sustained
            ? "Explain the tradeoffs among latency, throughput, memory use, cancellation safety, and output quality for a local AI runtime."
            : "Explain in a few precise paragraphs why measured evidence should gate runtime optimization."
    };

    internal static string BuildLongContextPrompt(bool sustained)
    {
        var builder = new StringBuilder(96_000);
        builder.AppendLine("The following numbered records are the benchmark context. Read all of them; do not treat this as a hypothetical long-context question.");
        for (int i = 0; i < 500; i++)
        {
            builder.Append("Record ").Append(i.ToString("D4"))
                .Append(": component C").Append(i % 37)
                .Append(" has invariant I").Append((i * 13) % 97)
                .Append(", recovery code R").Append((i * 29) % 113)
                .Append(", owner O").Append(i % 19)
                .Append(", and verification marker V").Append((i * 31) % 127)
                .AppendLine(". Preserve the exact record number and marker relationships.");
        }
        builder.AppendLine(sustained
            ? "Summarize the architecture and explicitly report the component/invariant/recovery/marker values from records 0017, 0211 and 0487. Then give five cross-record reliability risks."
            : "Report the component/invariant/recovery/marker values from records 0017, 0211 and 0487, then explain how you verified them from the supplied context.");
        return builder.ToString();
    }

    private sealed record BenchmarkPass(
        double PromptTokensPerSecond,
        double DecodeTokensPerSecond,
        double TimeToFirstTokenMilliseconds,
        int PromptTokens,
        int DecodeTokenCount,
        double DecodeMilliseconds);

    private sealed record TelemetrySummary(
        double? PeakVramMib,
        double? MaxTemperatureC,
        double? AveragePowerWatts,
        IReadOnlyList<string> MissingFields);
}