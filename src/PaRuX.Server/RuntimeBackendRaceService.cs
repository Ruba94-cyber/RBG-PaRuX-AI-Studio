using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Provider-neutral, read-only performance comparison. PaRuX's own candidate runs through the
/// isolated managed llama-server probe; the external candidate is only contacted over its already
/// running loopback OpenAI-compatible API. No external process/config/model is mutated.
/// </summary>
public sealed class RuntimeBackendRaceService
{
    private readonly LocalAiProcessManager _process;
    private readonly ILlamaServerPerformanceProbe _managedProbe;

    public RuntimeBackendRaceService(
        LocalAiProcessManager process,
        ILlamaServerPerformanceProbe managedProbe)
    {
        _process = process;
        _managedProbe = managedProbe;
    }

    public async Task<RuntimeBackendRaceResult> RunAsync(
        RuntimeBackendRaceRequest request,
        CancellationToken ct)
    {
        string modelPath = ValidateModel(request.ManagedModelPath);
        string workload = RuntimePerformanceTuner.NormalizeWorkload(request.Workload);
        Uri endpoint = ValidateLoopbackEndpoint(request.ExternalEndpoint);
        string externalModel = (request.ExternalModel ?? string.Empty).Trim();
        if (externalModel.Length == 0)
            throw new InvalidDataException("An external OpenAI-compatible model id is required.");

        if (_process.GamingActive)
            throw new InvalidOperationException("Gaming Mode is active; Backend Race is blocked.");
        using var gaming = new GamingHardOffWatch(() => _process.GamingActive, ct);
        ct = gaming.Token;
        if (_process.GetMediaProcessId() != null)
            throw new InvalidOperationException("Stop media generation before Backend Race.");
        if (_process.GetTextProcessId() != null)
            throw new InvalidOperationException(
                "Stop the active text model before Backend Race so candidates do not compete with Chat for VRAM.");

        var result = new RuntimeBackendRaceResult
        {
            Comparable = request.SameWeightsConfirmed,
            MeasuredUtc = DateTime.UtcNow
        };

        bool hasCuda = await _process.HasCudaHeavyGpuAsync(ct).ConfigureAwait(false);
        RuntimeLaunchOptimization launch = await RuntimeLaunchOptimizer.ResolveAsync(
            modelPath,
            workload,
            hasCuda,
            fallbackBatch: hasCuda ? 512 : 256,
            fallbackUbatch: hasCuda ? 128 : 64,
            fallbackCache: "q8_0",
            ct).ConfigureAwait(false);
        LlamaPerformanceProfileDto managedProfile = RuntimePerformanceProfileCopy.Copy(launch.Profile);
        managedProfile.SpeculativeType = "none";
        managedProfile.SpeculativeDraftMax = 0;

        var managed = new RuntimeBackendRaceCandidateDto
        {
            Backend = "PaRuX llama.cpp",
            Endpoint = "isolated managed probe",
            Model = Path.GetFileName(modelPath),
            Status = "running"
        };
        result.Candidates.Add(managed);
        try
        {
            LlamaServerPerformanceMeasurement measurement = await _managedProbe.RunAsync(
                modelPath, workload, managedProfile, request.SustainedValidation, ct).ConfigureAwait(false);
            managed.Status = "completed";
            managed.PromptTokensPerSecond = measurement.PromptTokensPerSecond;
            managed.DecodeTokensPerSecond = measurement.DecodeTokensPerSecond;
            managed.TimeToFirstTokenMilliseconds = measurement.TimeToFirstTokenMilliseconds;
            managed.SustainedDecodeTokensPerSecond = measurement.SustainedDecodeTokensPerSecond;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            managed.Status = "failed";
            managed.Error = ex.Message;
        }

        var external = new RuntimeBackendRaceCandidateDto
        {
            Backend = "USER_MANAGED OpenAI-compatible",
            Endpoint = endpoint.GetLeftPart(UriPartial.Authority),
            Model = externalModel,
            Status = "running"
        };
        result.Candidates.Add(external);
        try
        {
            await VerifyModelExistsAsync(endpoint, externalModel, ct).ConfigureAwait(false);
            ExternalMeasurement measurement = await MeasureExternalAsync(
                endpoint, externalModel, workload, request.SustainedValidation, ct).ConfigureAwait(false);
            external.Status = "completed";
            external.PromptTokensPerSecond = measurement.PromptTokensPerSecond;
            external.DecodeTokensPerSecond = measurement.DecodeTokensPerSecond;
            external.TimeToFirstTokenMilliseconds = measurement.TimeToFirstTokenMilliseconds;
            external.SustainedDecodeTokensPerSecond = measurement.SustainedDecodeTokensPerSecond;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            external.Status = "failed";
            external.Error = ex.Message;
        }

        Score(result.Candidates);
        List<RuntimeBackendRaceCandidateDto> successful = result.Candidates
            .Where(candidate => candidate.Status == "completed" && candidate.Score.HasValue)
            .OrderByDescending(candidate => candidate.Score)
            .ToList();

        if (!request.SameWeightsConfirmed)
        {
            result.Winner = null;
            result.Explanation = successful.Count == 2
                ? "Measurements completed, but no winner is declared because identical model weights/quantization were not confirmed."
                : "Backend Race completed without two comparable successful candidates.";
        }
        else if (successful.Count < 2)
        {
            result.Winner = null;
            result.Explanation = "Identical weights were declared, but both backends did not complete successfully; no winner was promoted.";
        }
        else
        {
            result.Winner = successful[0].Backend;
            result.Explanation = "Same weights/quantization confirmed: winner selected from measured decode, TTFT, prompt and sustained performance. No backend configuration was changed.";
        }
        return result;
    }

    internal static void Score(IReadOnlyList<RuntimeBackendRaceCandidateDto> candidates)
    {
        List<RuntimeBackendRaceCandidateDto> good = candidates
            .Where(candidate => candidate.Status == "completed"
                && candidate.DecodeTokensPerSecond is > 0
                && candidate.TimeToFirstTokenMilliseconds is > 0)
            .ToList();
        if (good.Count == 0) return;

        double maxDecode = good.Max(candidate => candidate.DecodeTokensPerSecond!.Value);
        double minTtft = good.Min(candidate => candidate.TimeToFirstTokenMilliseconds!.Value);
        double maxPrompt = good.Where(candidate => candidate.PromptTokensPerSecond is > 0)
            .Select(candidate => candidate.PromptTokensPerSecond!.Value)
            .DefaultIfEmpty(0).Max();
        double maxSustained = good.Select(candidate =>
                candidate.SustainedDecodeTokensPerSecond ?? candidate.DecodeTokensPerSecond!.Value)
            .Max();

        foreach (RuntimeBackendRaceCandidateDto candidate in good)
        {
            double decode = candidate.DecodeTokensPerSecond!.Value / maxDecode;
            double ttft = minTtft / candidate.TimeToFirstTokenMilliseconds!.Value;
            double prompt = maxPrompt > 0 && candidate.PromptTokensPerSecond is > 0
                ? candidate.PromptTokensPerSecond.Value / maxPrompt
                : decode;
            double sustained = (candidate.SustainedDecodeTokensPerSecond ?? candidate.DecodeTokensPerSecond.Value)
                / maxSustained;
            candidate.Score = 0.55 * decode + 0.20 * ttft + 0.15 * prompt + 0.10 * sustained;
        }
    }

    private sealed record ExternalMeasurement(
        double? PromptTokensPerSecond,
        double DecodeTokensPerSecond,
        double TimeToFirstTokenMilliseconds,
        double? SustainedDecodeTokensPerSecond);

    private sealed record ExternalCompletion(double? PromptTps, double DecodeTps);
    private sealed record WorkloadPrompt(string System, string User);

    private static async Task<ExternalMeasurement> MeasureExternalAsync(
        Uri endpoint,
        string model,
        string workload,
        bool sustained,
        CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // The managed probe reports llama.cpp's native decode timing after its isolated process has
        // loaded the model. OpenAI-compatible providers commonly omit native timings, so dividing
        // tokens by the first request's wall clock would charge only the external candidate for a
        // cold model load. Warm it once with an unscored, distinct prompt before either candidate's
        // serving metrics are compared. This does not alter external configuration or lifecycle.
        await WarmExternalAsync(http, endpoint, model, workload, ct).ConfigureAwait(false);
        WorkloadPrompt prompt = BuildPrompt(workload, 0);
        ExternalCompletion primary = await CompleteAsync(http, endpoint, model, prompt, ct).ConfigureAwait(false);
        double ttft = await MeasureTtftAsync(http, endpoint, model, BuildPrompt(workload, 1), ct).ConfigureAwait(false);

        double? sustainedDecode = null;
        if (sustained)
        {
            var values = new List<double> { primary.DecodeTps };
            Stopwatch duration = Stopwatch.StartNew();
            for (int i = 2; i < 7 && duration.Elapsed < TimeSpan.FromSeconds(75); i++)
            {
                ExternalCompletion sample = await CompleteAsync(
                    http, endpoint, model, BuildPrompt(workload, i), ct).ConfigureAwait(false);
                values.Add(sample.DecodeTps);
            }
            sustainedDecode = values.Average();
        }
        return new ExternalMeasurement(primary.PromptTps, primary.DecodeTps, ttft, sustainedDecode);
    }

    internal static async Task WarmExternalAsync(
        HttpClient http,
        Uri endpoint,
        string model,
        string workload,
        CancellationToken ct)
    {
        _ = await CompleteAsync(
            http, endpoint, model, BuildPrompt(workload, -1), ct).ConfigureAwait(false);
    }

    private static async Task<ExternalCompletion> CompleteAsync(
        HttpClient http,
        Uri endpoint,
        string model,
        WorkloadPrompt prompt,
        CancellationToken ct)
    {
        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = prompt.System },
                new { role = "user", content = prompt.User }
            },
            max_tokens = 192,
            temperature = 0,
            reasoning_effort = "none",
            stream = false
        };
        Stopwatch clock = Stopwatch.StartNew();
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            Combine(endpoint, "/v1/chat/completions"), payload, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        clock.Stop();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("External backend returned HTTP " + (int)response.StatusCode + ": " + Trim(body));

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        double? promptTps = null;
        double? decodeTps = null;
        if (root.TryGetProperty("timings", out JsonElement timings))
        {
            promptTps = ReadPositiveDouble(timings, "prompt_per_second");
            decodeTps = ReadPositiveDouble(timings, "predicted_per_second");
        }

        int completionTokens = 0;
        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            completionTokens = ReadInt(usage, "completion_tokens");
        }
        if (!(decodeTps is > 0))
        {
            if (completionTokens <= 0 || clock.Elapsed.TotalSeconds <= 0)
                throw new InvalidDataException("External backend returned neither native timings nor usable completion token counts.");
            decodeTps = completionTokens / clock.Elapsed.TotalSeconds;
        }
        // End-to-end wall time includes decode and cannot be presented as prompt throughput.
        // Providers without a native prefill metric therefore remain honestly unknown here.
        return new ExternalCompletion(promptTps, decodeTps.Value);
    }

    private static async Task<double> MeasureTtftAsync(
        HttpClient http,
        Uri endpoint,
        string model,
        WorkloadPrompt prompt,
        CancellationToken ct)
    {
        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = prompt.System },
                new { role = "user", content = prompt.User }
            },
            max_tokens = 48,
            temperature = 0,
            reasoning_effort = "none",
            stream = true
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Combine(endpoint, "/v1/chat/completions"))
        {
            Content = JsonContent.Create(payload)
        };
        Stopwatch clock = Stopwatch.StartNew();
        using HttpResponseMessage response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("External streaming probe returned HTTP " + (int)response.StatusCode + ".");

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
                if (HasGeneratedDelta(doc.RootElement)) return clock.Elapsed.TotalMilliseconds;
            }
            catch (JsonException) { }
        }
        throw new InvalidDataException("External backend stream produced no content token.");
    }

    private static async Task VerifyModelExistsAsync(Uri endpoint, string model, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        using HttpResponseMessage response = await http.GetAsync(Combine(endpoint, "/v1/models"), ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("External backend /v1/models probe failed.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array) return; // compatible providers may expose a nonstandard list shape
            bool found = data.EnumerateArray().Any(item =>
                item.TryGetProperty("id", out JsonElement id)
                && string.Equals(id.GetString(), model, StringComparison.Ordinal));
            if (!found)
                throw new InvalidDataException("The requested external model id was not returned by /v1/models.");
        }
        catch (JsonException)
        {
            // Reachable OpenAI-compatible providers with nonstandard model-list JSON are allowed;
            // the actual completion request remains the authoritative validation.
        }
    }

    internal static Uri ValidateLoopbackEndpoint(string value)
    {
        if (!Uri.TryCreate((value ?? string.Empty).TrimEnd('/'), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException("Backend Race requires an http(s) endpoint.");
        if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            && (!IPAddress.TryParse(uri.Host, out IPAddress? ip) || !IPAddress.IsLoopback(ip)))
            throw new InvalidDataException("Backend Race only probes loopback USER_MANAGED backends on this workstation.");
        return uri;
    }

    private static string ValidateModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("A managed GGUF path is required.");
        string full = Path.GetFullPath(path);
        if (!full.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidDataException("Backend Race requires an existing GGUF model.");
        if (!IsInside(full, LocalAiPaths.ModelsPath) && !IsInside(full, ModelDownloadService.ModelsDirectory))
            throw new InvalidDataException("The managed model must be inside a configured PaRuX model directory.");
        return full;
    }

    private static bool IsInside(string path, string directory)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkloadPrompt BuildPrompt(string workload, int variant)
    {
        workload = RuntimePerformanceTuner.NormalizeWorkload(workload);
        return workload switch
        {
            "coding" => new WorkloadPrompt(
                "You are a deterministic code benchmark. Return only the requested result.",
                "Refactor this repeated C# loop while preserving behavior. Variant " + variant + ".\n"
                + "for(int i=0;i<64;i++){ total += values[i]; log.Add(values[i]); }\n"
                + "for(int i=0;i<64;i++){ total += values[i]; log.Add(values[i]); }"),
            "long-context" => new WorkloadPrompt(
                "Summarize only the supplied local text.",
                string.Join(' ', Enumerable.Repeat(
                    "PaRuX keeps models projects artifacts routing and runtime evidence on the local workstation.", 160))
                + " Variant " + variant + ". Summarize in five bullets."),
            _ => new WorkloadPrompt(
                "Answer concisely and deterministically.",
                "Explain in six short points why local AI can keep private data on-device. Variant " + variant + ".")
        };
    }

    internal static bool HasGeneratedDelta(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array)
            return false;
        foreach (JsonElement choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out JsonElement delta)) continue;
            foreach (string property in new[] { "content", "reasoning_content", "reasoning" })
                if (delta.TryGetProperty(property, out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(value.GetString())) return true;
        }
        return false;
    }

    private static string Combine(Uri endpoint, string path) => endpoint.GetLeftPart(UriPartial.Authority) + path;

    private static double? ReadPositiveDouble(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.TryGetDouble(out double result)
            && double.IsFinite(result) && result > 0 ? result : null;

    private static int ReadInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 500 ? value : value[..500] + "…";
    }
}
