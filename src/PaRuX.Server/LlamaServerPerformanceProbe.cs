using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed record LlamaServerPerformanceMeasurement(
    double PromptTokensPerSecond,
    double DecodeTokensPerSecond,
    double TimeToFirstTokenMilliseconds,
    double LoadMilliseconds,
    double? SustainedDecodeTokensPerSecond,
    double? ConcurrentDecodeTokensPerSecond,
    int? ConcurrentRequests,
    double? AverageGpuPowerWatts,
    double? PeakGpuPowerWatts,
    double? MaxGpuTemperatureC,
    long? PeakGpuMemoryUsedMib,
    int DraftTokens,
    int DraftTokensAccepted,
    double? PromptCacheColdTtftMilliseconds,
    double? PromptCacheWarmTtftMilliseconds,
    double? PromptCacheImprovementPercent,
    bool? PromptCacheRecommended,
    string Backend,
    string HardwareKey,
    string? OutputDigest = null,
    string? OutputSample = null);

public interface ILlamaServerPerformanceProbe
{
    Task<LlamaServerPerformanceMeasurement> RunAsync(
        string modelPath,
        string workload,
        LlamaPerformanceProfileDto profile,
        bool sustainedValidation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Launches an isolated llama-server on an ephemeral loopback port and measures the real serving
/// path. This is deliberately separate from the interactive slot, so speculative decoding,
/// topology, batching, concurrency or prompt-cache experiments cannot poison a user's active
/// conversation. The process is attached to PaRuX Server's Windows Job Object and killed in finally.
/// </summary>
public sealed class LlamaServerPerformanceProbe : ILlamaServerPerformanceProbe
{
    internal const int PromptCacheReuseTokens = 256;
    internal const double MinimumPromptCacheImprovementPercent = 5.0;
    internal const double MinimumPromptCacheSavedMilliseconds = 15.0;
    internal const int MaximumConcurrentProbeRequests = 8;

    /// <summary>Scoped override of the runtime folder the probe launches (flows with the async call). Lets the managed
    /// runtime updater measure a STAGED build without ever repointing the active runtime. Null = the active runtime.</summary>
    internal static readonly AsyncLocal<string?> RuntimeDirectoryOverride = new();

    private readonly LocalAiProcessManager _runtime;
    private readonly GpuPerformanceTelemetryService _telemetry;

    public LlamaServerPerformanceProbe(
        LocalAiProcessManager runtime,
        GpuPerformanceTelemetryService? telemetry = null)
    {
        _runtime = runtime;
        _telemetry = telemetry ?? new GpuPerformanceTelemetryService();
    }

    /// <summary>
    /// Gaming Mode is a hard-off. A probe never starts during a game, and one already running is cancelled the
    /// moment the game marker appears, so its own cleanup kills the llama-server it started. Every caller (race,
    /// tuner, endpoint, update guard) goes through here, so the rule holds for all of them.
    /// </summary>
    public async Task<LlamaServerPerformanceMeasurement> RunAsync(
        string modelPath,
        string workload,
        LlamaPerformanceProfileDto profile,
        bool sustainedValidation,
        CancellationToken cancellationToken)
    {
        if (_runtime.GamingActive)
            throw new InvalidOperationException("Gaming Mode is active; performance probes are blocked.");
        using GamingHardOffWatch gaming = new(() => _runtime.GamingActive, cancellationToken);
        try
        {
            return await RunMeasurementAsync(modelPath, workload, profile, sustainedValidation, gaming.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (gaming.Triggered)
        {
            throw new InvalidOperationException("Gaming Mode started during the performance probe; the probe was stopped.");
        }
    }

    private async Task<LlamaServerPerformanceMeasurement> RunMeasurementAsync(
        string modelPath,
        string workload,
        LlamaPerformanceProfileDto profile,
        bool sustainedValidation,
        CancellationToken cancellationToken)
    {
        bool hasCuda = await _runtime.HasCudaHeavyGpuAsync(cancellationToken).ConfigureAwait(false);
        string backendKey = hasCuda ? "text-cuda" : "text-vulkan";
        string runtimePath = RuntimeDirectoryOverride.Value
            ?? (hasCuda ? LocalAiPaths.RuntimeCudaPath : LocalAiPaths.RuntimeVulkanPath);
        string executable = Path.Combine(runtimePath, LocalAiPaths.LlamaServerExecutable);
        if (!File.Exists(executable))
            throw new FileNotFoundException("llama-server was not found in the active PaRuX runtime.", executable);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("GGUF model not found.", modelPath);

        int port = ReserveLoopbackPort();
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = runtimePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[]
        {
            "--model", modelPath,
            "--alias", "parux-performance-probe",
            "--host", "127.0.0.1",
            "--port", port.ToString(),
            "--parallel", "1",
            "--jinja",
            "--reasoning", "auto",
            "--sleep-idle-seconds", "-1"
        }) start.ArgumentList.Add(arg);
        // Profile arguments are appended last deliberately: a measured ParallelSlots/reasoning/
        // load-mode winner supersedes conservative probe defaults without a second launch path.
        foreach (string arg in RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, hasCuda))
            start.ArgumentList.Add(arg);
        RuntimeLaunchOptimizer.ApplyEnvironment(start, profile);

        using var process = new Process { StartInfo = start };
        using var sampleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var samples = new List<GpuPerformanceTelemetryDto>();
        Task telemetryTask = SampleTelemetryAsync(samples, sampleCts.Token);
        Stopwatch load = Stopwatch.StartNew();
        try
        {
            process.Start();
            ChildProcessJob.Assign(process);
            Task<string> stdoutDrain = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrDrain = process.StandardError.ReadToEndAsync(cancellationToken);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            string baseUrl = "http://127.0.0.1:" + port;
            await WaitUntilHealthyAsync(http, baseUrl, process, stderrDrain, cancellationToken).ConfigureAwait(false);
            load.Stop();

            workload = RuntimePerformanceTuner.NormalizeWorkload(workload);
            WorkloadPrompt prompt = BuildPrompt(workload, 0);
            CompletionMetrics primary = await RunCompletionAsync(http, baseUrl, prompt, cancellationToken).ConfigureAwait(false);
            double ttft = await MeasureTtftAsync(
                http, baseUrl, BuildPrompt(workload, 1), cancellationToken).ConfigureAwait(false);

            // Prompt throughput is measured on its own sized prompt rather than on the workload's.
            // llama.cpp's prompt_per_second divides prompt tokens by the whole prefill, fixed
            // start-up cost included, so on a short prompt it reports latency wearing throughput's
            // units. Measured on the physical PC against this very model: the chat benchmark's
            // 32-token prompt reported 42.7 tok/s, while 610 tokens gave 202, 2,410 gave 281 and
            // 7,210 gave 284 - the same machine, understated 6.6x, in PaRuX's own Runtime Manager
            // and in any comparison drawn from it. The curve is flat by ~2,000 tokens, which is why
            // the probe is sized there and not larger: past that point it costs seconds and tells
            // us nothing new.
            double promptThroughput = primary.PromptTokensPerSecond;
            try
            {
                CompletionMetrics throughput = await RunCompletionAsync(
                    http, baseUrl, BuildThroughputPrompt(), cancellationToken, maxTokens: 8).ConfigureAwait(false);
                if (throughput.PromptTokensPerSecond > 0) promptThroughput = throughput.PromptTokensPerSecond;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // A model whose context cannot hold the sized prompt keeps the workload figure,
                // which is honest for that model rather than a hole in the report.
            }

            PromptCacheRaceMetrics? cacheRace = null;
            try
            {
                cacheRace = await MeasurePromptCacheRaceAsync(http, baseUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Cache support is advisory. A runtime/profile that cannot provide a controlled
                // race remains "no evidence" rather than failing an otherwise valid candidate.
            }

            double? concurrentDecode = null;
            int? concurrentRequests = null;
            if (profile.ParallelSlots > 1)
            {
                try
                {
                    ConcurrentMetrics concurrent = await MeasureConcurrentAsync(
                        http,
                        baseUrl,
                        workload,
                        Math.Clamp(profile.ParallelSlots, 2, MaximumConcurrentProbeRequests),
                        cancellationToken).ConfigureAwait(false);
                    concurrentDecode = concurrent.AggregateDecodeTokensPerSecond;
                    concurrentRequests = concurrent.Requests;
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // A profile can still be useful for single-request latency even when a
                    // concurrency sample is unavailable. Missing evidence never gets fabricated.
                }
            }

            double? sustained = null;
            if (sustainedValidation)
            {
                var values = new List<double> { primary.DecodeTokensPerSecond };
                Stopwatch sustainedClock = Stopwatch.StartNew();
                for (int i = 2; i < 8 && sustainedClock.Elapsed < TimeSpan.FromSeconds(75); i++)
                {
                    CompletionMetrics measurement = await RunCompletionAsync(
                        http, baseUrl, BuildPrompt(workload, i), cancellationToken).ConfigureAwait(false);
                    values.Add(measurement.DecodeTokensPerSecond);
                }
                sustained = values.Count == 0 ? null : values.Average();
            }

            sampleCts.Cancel();
            try { await telemetryTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

            List<double> powers;
            List<double> temperatures;
            List<int> memory;
            lock (samples)
            {
                powers = samples.Where(sample => sample.PowerWatts.HasValue)
                    .Select(sample => sample.PowerWatts!.Value).ToList();
                temperatures = samples.Where(sample => sample.TemperatureC.HasValue)
                    .Select(sample => sample.TemperatureC!.Value).ToList();
                memory = samples.Where(sample => sample.MemoryUsedMib.HasValue)
                    .Select(sample => sample.MemoryUsedMib!.Value).ToList();
            }
            string runtimeTag = LocalAiPaths.GetActiveRuntimeBuildTag(backendKey)
                ?? LocalAiPaths.DefaultRuntimeBuildTag(backendKey);
            string hardware = RuntimePerformanceTuner.BuildGpuFingerprint(new GpuInventoryService().GetStatus());

            _ = stdoutDrain;
            return new LlamaServerPerformanceMeasurement(
                promptThroughput,
                primary.DecodeTokensPerSecond,
                ttft,
                load.Elapsed.TotalMilliseconds,
                sustained,
                concurrentDecode,
                concurrentRequests,
                powers.Count == 0 ? null : powers.Average(),
                powers.Count == 0 ? null : powers.Max(),
                temperatures.Count == 0 ? null : temperatures.Max(),
                memory.Count == 0 ? null : memory.Max(),
                primary.DraftTokens,
                primary.DraftTokensAccepted,
                cacheRace?.ColdTtftMilliseconds,
                cacheRace?.WarmTtftMilliseconds,
                cacheRace?.ImprovementPercent,
                cacheRace?.Recommended,
                backendKey,
                hardware + " | runtime=" + runtimeTag,
                primary.OutputDigest,
                primary.OutputSample);
        }
        finally
        {
            sampleCts.Cancel();
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

    private sealed record CompletionMetrics(
        double PromptTokensPerSecond,
        double DecodeTokensPerSecond,
        int PredictedTokens,
        int DraftTokens,
        int DraftTokensAccepted,
        string? OutputDigest = null,
        string? OutputSample = null);

    /// <summary>SHA-256 prefix of the answer parts; null when there is no text. Used to prove a speculative mode left the greedy output unchanged.</summary>
    internal static string? DigestOutput(params string?[] parts)
    {
        string joined = string.Join('', parts.Select(p => p ?? string.Empty));
        if (parts.All(string.IsNullOrEmpty)) return null;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16];
    }

    private sealed record ConcurrentMetrics(double AggregateDecodeTokensPerSecond, int Requests);
    private sealed record WorkloadPrompt(string System, string User);
    private sealed record PromptCacheRaceMetrics(
        double ColdTtftMilliseconds,
        double WarmTtftMilliseconds,
        double ImprovementPercent,
        bool Recommended);

    internal static double ComputePromptCacheImprovementPercent(double coldTtftMilliseconds, double warmTtftMilliseconds)
    {
        if (!double.IsFinite(coldTtftMilliseconds) || !double.IsFinite(warmTtftMilliseconds)
            || coldTtftMilliseconds <= 0 || warmTtftMilliseconds <= 0)
            return 0;
        return 100.0 * (coldTtftMilliseconds - warmTtftMilliseconds) / coldTtftMilliseconds;
    }

    internal static bool IsPromptCacheRecommended(double coldTtftMilliseconds, double warmTtftMilliseconds)
    {
        double improvement = ComputePromptCacheImprovementPercent(coldTtftMilliseconds, warmTtftMilliseconds);
        double saved = coldTtftMilliseconds - warmTtftMilliseconds;
        return improvement >= MinimumPromptCacheImprovementPercent
            && saved >= MinimumPromptCacheSavedMilliseconds;
    }

    private static async Task<PromptCacheRaceMetrics> MeasurePromptCacheRaceAsync(
        HttpClient http,
        string baseUrl,
        CancellationToken ct)
    {
        (WorkloadPrompt seed, WorkloadPrompt target) = BuildPromptCacheRacePrompts();
        double cold = await MeasureTtftAsync(
            http, baseUrl, target, ct, cachePrompt: false, reuseTokens: 0).ConfigureAwait(false);
        _ = await MeasureTtftAsync(
            http, baseUrl, seed, ct, cachePrompt: true, reuseTokens: 0).ConfigureAwait(false);
        double warm = await MeasureTtftAsync(
            http, baseUrl, target, ct, cachePrompt: true, reuseTokens: PromptCacheReuseTokens).ConfigureAwait(false);

        double improvement = ComputePromptCacheImprovementPercent(cold, warm);
        return new PromptCacheRaceMetrics(cold, warm, improvement, IsPromptCacheRecommended(cold, warm));
    }

    private static (WorkloadPrompt Seed, WorkloadPrompt Target) BuildPromptCacheRacePrompts()
    {
        string stablePrefix = string.Join(' ', Enumerable.Repeat(
            "PaRuX prompt cache benchmark uses a deterministic local prefix containing project context, durable conversation facts, tool policy, safety boundaries, model-routing evidence and repeated technical background so a bounded reusable prefix is materially longer than two hundred fifty six tokens.",
            24));
        return (
            new WorkloadPrompt(stablePrefix,
                "Cache seed turn. Reply with one short acknowledgement and no external knowledge."),
            new WorkloadPrompt(stablePrefix,
                "Cache target turn. Summarize the stable prefix in exactly three short bullets."));
    }

    private static async Task<ConcurrentMetrics> MeasureConcurrentAsync(
        HttpClient http,
        string baseUrl,
        string workload,
        int requests,
        CancellationToken ct)
    {
        var tasks = new Task<CompletionMetrics>[requests];
        Stopwatch wall = Stopwatch.StartNew();
        for (int i = 0; i < requests; i++)
            tasks[i] = RunCompletionAsync(http, baseUrl, BuildPrompt(workload, 100 + i), ct);
        CompletionMetrics[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        wall.Stop();
        int totalTokens = results.Sum(result => Math.Max(0, result.PredictedTokens));
        if (totalTokens <= 0 || wall.Elapsed.TotalSeconds <= 0)
            throw new InvalidDataException("Concurrent probe returned no decoded-token count.");
        double aggregate = totalTokens / wall.Elapsed.TotalSeconds;
        if (!double.IsFinite(aggregate) || aggregate <= 0)
            throw new InvalidDataException("Concurrent probe returned invalid aggregate throughput.");
        return new ConcurrentMetrics(aggregate, requests);
    }

    private static async Task<CompletionMetrics> RunCompletionAsync(
        HttpClient http,
        string baseUrl,
        WorkloadPrompt prompt,
        CancellationToken ct,
        // The workload probe decodes 192 tokens so decode throughput is measured over a real run;
        // the throughput probe only needs the prefill, so it decodes almost nothing.
        int maxTokens = 192)
    {
        var payload = new
        {
            model = "parux-performance-probe",
            messages = new object[]
            {
                new { role = "system", content = prompt.System },
                new { role = "user", content = prompt.User }
            },
            max_tokens = maxTokens,
            temperature = 0,
            stream = false,
            cache_prompt = false
        };
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            baseUrl + "/v1/chat/completions", payload, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("llama-server probe request failed: " + Trim(body));
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("timings", out JsonElement timings))
            throw new InvalidDataException("llama-server response contained no timings object.");
        double promptTps = ReadDouble(timings, "prompt_per_second");
        double decodeTps = ReadDouble(timings, "predicted_per_second");
        int predicted = ReadInt(timings, "predicted_n");
        int draft = ReadInt(timings, "draft_n");
        int accepted = ReadInt(timings, "draft_n_accepted");
        if (promptTps <= 0 || decodeTps <= 0)
            throw new InvalidDataException("llama-server returned invalid throughput timings.");
        string? content = null, reasoning = null;
        try
        {
            JsonElement message = document.RootElement.GetProperty("choices")[0].GetProperty("message");
            if (message.TryGetProperty("content", out JsonElement c) && c.ValueKind == JsonValueKind.String) content = c.GetString();
            if (message.TryGetProperty("reasoning_content", out JsonElement r) && r.ValueKind == JsonValueKind.String) reasoning = r.GetString();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { }
        return new CompletionMetrics(promptTps, decodeTps, predicted, draft, accepted, DigestOutput(reasoning, content),
            KvQualityGuard.Sample(reasoning, content));
    }

    private static async Task<double> MeasureTtftAsync(
        HttpClient http,
        string baseUrl,
        WorkloadPrompt prompt,
        CancellationToken ct,
        bool cachePrompt = false,
        int reuseTokens = 0)
    {
        var payload = new
        {
            model = "parux-performance-probe",
            messages = new object[]
            {
                new { role = "system", content = prompt.System },
                new { role = "user", content = prompt.User }
            },
            max_tokens = 48,
            temperature = 0,
            stream = true,
            cache_prompt = cachePrompt,
            n_cache_reuse = Math.Max(0, reuseTokens)
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        Stopwatch clock = Stopwatch.StartNew();
        using HttpResponseMessage response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string json = line[5..].Trim();
            if (json.Length == 0 || json == "[DONE]") continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (HasNonEmptyDelta(doc.RootElement)) return clock.Elapsed.TotalMilliseconds;
            }
            catch (JsonException) { }
        }
        throw new InvalidDataException("Streaming probe completed without a generated token.");
    }

    private static bool HasNonEmptyDelta(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array)
            return false;
        foreach (JsonElement choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out JsonElement delta)) continue;
            if (delta.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(content.GetString())) return true;
            if (delta.TryGetProperty("reasoning_content", out JsonElement reasoning)
                && reasoning.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(reasoning.GetString())) return true;
        }
        return false;
    }

    private async Task SampleTelemetryAsync(List<GpuPerformanceTelemetryDto> samples, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                List<GpuPerformanceTelemetryDto> current = await _telemetry.QueryAsync(ct).ConfigureAwait(false);
                lock (samples) samples.AddRange(current);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
    }

    private static async Task WaitUntilHealthyAsync(
        HttpClient http,
        string baseUrl,
        Process process,
        Task<string> stderr,
        CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(6);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException("llama-server probe exited during load: " + Trim(await stderr.ConfigureAwait(false)));
            try
            {
                using HttpResponseMessage response = await http.GetAsync(baseUrl + "/health", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("Isolated llama-server did not become healthy within six minutes.");
    }

    /// <summary>
    /// About 2,000 tokens - the point where measured prompt throughput stops rising on this class
    /// of hardware, so it reports the machine's real prefill rate instead of its start-up cost.
    /// Deliberately dull, repetitive prose: a benchmark prompt should not also be measuring how
    /// unusual its own vocabulary is.
    /// </summary>
    private static WorkloadPrompt BuildThroughputPrompt() => new(
        "You are a deterministic local inference benchmark. Answer with one word.",
        string.Join(' ', Enumerable.Repeat(
            "PaRuX keeps models, projects, artifacts, routing evidence and runtime measurements local to the workstation.", 120))
        + " Reply with the single word OK.");

    private static WorkloadPrompt BuildPrompt(string workload, int variant)
    {
        workload = RuntimePerformanceTuner.NormalizeWorkload(workload);
        return workload switch
        {
            "coding" => new WorkloadPrompt(
                "You are a deterministic code-edit benchmark. Return only the rewritten code block.",
                "Refactor this repeated C# loop without changing behavior. Preserve names and make the repeated pattern easy to continue. Variant " + variant + ".\n"
                + "for (int i=0;i<64;i++){ total += values[i]; log.Add(values[i]); }\n"
                + "for (int i=0;i<64;i++){ total += values[i]; log.Add(values[i]); }\n"
                + "for (int i=0;i<64;i++){ total += values[i]; log.Add(values[i]); }"),
            "long-context" => new WorkloadPrompt(
                "You are a deterministic local inference benchmark. Summarize precisely and do not use external knowledge.",
                string.Join(' ', Enumerable.Repeat(
                    "PaRuX keeps models, projects, artifacts, routing evidence and runtime measurements local to the workstation.", 180))
                + " Variant " + variant + ". Summarize the architecture in five bullets."),
            _ => new WorkloadPrompt(
                "You are a deterministic local chat benchmark. Answer concisely.",
                "Explain in six short points how local AI keeps data on a user's own computer. Variant " + variant + ".")
        };
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static double ReadDouble(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.TryGetDouble(out double result)
            && double.IsFinite(result) ? result : 0;

    private static int ReadInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 900 ? value : value[..900] + "…";
    }
}
