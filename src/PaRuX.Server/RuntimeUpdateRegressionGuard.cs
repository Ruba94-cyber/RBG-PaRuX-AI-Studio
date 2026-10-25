using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed class RuntimeRegressionEvidenceStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public RuntimeRegressionEvidenceStore() : this(Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "server", "runtime-regression-evidence.json")) { }

    internal RuntimeRegressionEvidenceStore(string path) => _path = path;

    public IReadOnlyList<RuntimeRegressionEvidenceDto> List()
    {
        lock (_gate) return LoadUnsafe().OrderByDescending(item => item.MeasuredUtc).ToList();
    }

    public void Append(RuntimeRegressionEvidenceDto evidence)
    {
        lock (_gate)
        {
            List<RuntimeRegressionEvidenceDto> items = LoadUnsafe();
            items.Add(evidence);
            items = items.OrderByDescending(item => item.MeasuredUtc).Take(100).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private List<RuntimeRegressionEvidenceDto> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<List<RuntimeRegressionEvidenceDto>>(
                       File.ReadAllText(_path), JsonOptions) ?? new();
        }
        catch { return new(); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

/// <summary>
/// Automatic post-install performance gate for PARUX_MANAGED llama.cpp builds. Candidate bytes are
/// kept installed regardless of the result; only active-tag promotion is decided here. Production
/// uses the same isolated llama-server evidence and multi-objective policy as Build Race 2.0 so an
/// automatic install can never bypass TTFT/load/sustained/VRAM/power/thermal guardrails.
/// </summary>
public sealed class RuntimeUpdateRegressionGuard
{
    public const double MinimumImprovementPercent = 1.0;

    private readonly LocalAiProcessManager _process;
    private readonly IRuntimeBuildPerformanceProbe _probe;
    private readonly RuntimeRegressionEvidenceStore _store;

    public RuntimeUpdateRegressionGuard(LocalAiProcessManager process)
        : this(
            process,
            new LlamaServerRuntimeBuildPerformanceProbe(
                new LlamaServerPerformanceProbe(process, new GpuPerformanceTelemetryService())),
            new RuntimeRegressionEvidenceStore()) { }

    internal RuntimeUpdateRegressionGuard(
        LocalAiProcessManager process,
        ILlamaPerformanceProbe probe,
        RuntimeRegressionEvidenceStore store)
        : this(process, new LegacyLlamaBenchRuntimeBuildPerformanceProbe(probe), store) { }

    internal RuntimeUpdateRegressionGuard(
        LocalAiProcessManager process,
        IRuntimeBuildPerformanceProbe probe,
        RuntimeRegressionEvidenceStore store)
    {
        _process = process;
        _probe = probe;
        _store = store;
    }

    public IReadOnlyList<RuntimeRegressionEvidenceDto> ListEvidence() => _store.List();

    public async Task<RuntimeRegressionEvidenceDto> EvaluateAsync(
        string backend,
        string originalTag,
        string candidateTag,
        CancellationToken ct)
    {
        await using IAsyncDisposable performanceLease = await RuntimePerformanceCoordinator.Shared
            .AcquireAsync(ct).ConfigureAwait(false);

        var evidence = new RuntimeRegressionEvidenceDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Backend = backend,
            OriginalTag = originalTag,
            CandidateTag = candidateTag,
            MeasuredUtc = DateTime.UtcNow
        };

        if (string.Equals(originalTag, candidateTag, StringComparison.Ordinal))
        {
            LocalAiPaths.SetActiveRuntimeBuildTag(backend, candidateTag);
            return Save(evidence, "accepted", candidateTag, "The requested runtime is already the active build.");
        }

        bool originalExists = LocalAiPaths.ListInstalledRuntimeBuildTags(backend)
            .Contains(originalTag, StringComparer.Ordinal);
        if (!originalExists)
        {
            LocalAiPaths.SetActiveRuntimeBuildTag(backend, candidateTag);
            return Save(evidence, "first-install", candidateTag,
                "No previous installed managed runtime exists to compare; the first valid build was activated.");
        }

        bool hasCuda = await _process.HasCudaHeavyGpuAsync(ct).ConfigureAwait(false);
        string measuredBackend = hasCuda ? "text-cuda" : "text-vulkan";
        if (!string.Equals(backend, measuredBackend, StringComparison.Ordinal))
        {
            LocalAiPaths.SetActiveRuntimeBuildTag(backend, originalTag);
            return Save(evidence, "deferred", originalTag,
                "The installed backend is not the runtime this hardware would execute for the serving probe; candidate kept installed but not auto-promoted.");
        }

        string? modelPath = FindRepresentativeModel();
        if (modelPath == null)
        {
            LocalAiPaths.SetActiveRuntimeBuildTag(backend, originalTag);
            return Save(evidence, "deferred", originalTag,
                "No managed GGUF model is available for an apples-to-apples regression test; candidate kept installed and previous build remains active.");
        }
        evidence.ModelFileName = Path.GetFileName(modelPath);

        FileInfo model = new(modelPath);
        GpuInventoryStatus gpu = new GpuInventoryService().GetStatus();
        LlamaPerformanceProfileDto profile = RuntimePerformanceTuner.BuildCandidates(
                model.Length,
                gpu.Available ? gpu.LargestDedicatedVideoMemoryBytes : 0,
                "chat")
            .OrderBy(candidate => Math.Abs(candidate.BatchSize - 512))
            .ThenBy(candidate => candidate.CacheTypeK == "q8_0" ? 0 : 1)
            .First();

        try
        {
            ct.ThrowIfCancellationRequested();
            LocalAiPaths.SetActiveRuntimeBuildTag(backend, originalTag);
            RuntimeBuildPerformanceMeasurement original = await _probe.RunAsync(
                modelPath, "chat", profile, sustainedValidation: true, ct).ConfigureAwait(false);
            ApplyOriginal(evidence, original);

            ct.ThrowIfCancellationRequested();
            LocalAiPaths.SetActiveRuntimeBuildTag(backend, candidateTag);
            RuntimeBuildPerformanceMeasurement candidate = await _probe.RunAsync(
                modelPath, "chat", profile, sustainedValidation: true, ct).ConfigureAwait(false);
            ApplyCandidate(evidence, candidate);

            RuntimeCandidateEvidence baselineEvidence = RuntimeBuildPromotionEvidence.From(
                originalTag, modelPath, original);
            RuntimeCandidateEvidence candidateEvidence = RuntimeBuildPromotionEvidence.From(
                candidateTag, modelPath, candidate);
            RuntimePromotionDecision decision = RuntimeCandidatePromotionPolicy.Evaluate(
                baselineEvidence,
                candidateEvidence,
                new RuntimePromotionPolicyOptions(
                    MinimumCompositeImprovementPercent: MinimumImprovementPercent));
            evidence.BaselineCompositeScore = decision.BaselineScore;
            evidence.CandidateCompositeScore = decision.CandidateScore;
            evidence.ImprovementPercent = decision.ImprovementPercent;

            if (decision.Promote)
            {
                LocalAiPaths.SetActiveRuntimeBuildTag(backend, candidateTag);
                return Save(evidence, "accepted", candidateTag,
                    "Candidate cleared the full BIC runtime regression gate. " + decision.Reason);
            }

            LocalAiPaths.SetActiveRuntimeBuildTag(backend, originalTag);
            return Save(evidence, "rejected", originalTag,
                "Candidate stayed installed but was not promoted. " + decision.Reason);
        }
        catch (OperationCanceledException)
        {
            TryRestore(backend, originalTag);
            throw;
        }
        catch (Exception ex)
        {
            TryRestore(backend, originalTag);
            return Save(evidence, "deferred", originalTag,
                "Full serving validation could not complete (" + Trim(ex.Message)
                + "); previous build remains active and candidate stays installed.");
        }
    }

    private static void ApplyOriginal(RuntimeRegressionEvidenceDto evidence, RuntimeBuildPerformanceMeasurement value)
    {
        evidence.OriginalPromptTokensPerSecond = value.PromptTokensPerSecond;
        evidence.OriginalDecodeTokensPerSecond = value.DecodeTokensPerSecond;
        evidence.OriginalTimeToFirstTokenMilliseconds = value.TimeToFirstTokenMilliseconds;
        evidence.OriginalLoadMilliseconds = value.LoadMilliseconds;
        evidence.OriginalSustainedDecodeTokensPerSecond = value.SustainedDecodeTokensPerSecond;
        evidence.OriginalPeakGpuMemoryUsedMib = value.PeakGpuMemoryUsedMib;
        evidence.OriginalMaxGpuTemperatureC = value.MaxGpuTemperatureC;
        evidence.OriginalAverageGpuPowerWatts = value.AverageGpuPowerWatts;
    }

    private static void ApplyCandidate(RuntimeRegressionEvidenceDto evidence, RuntimeBuildPerformanceMeasurement value)
    {
        evidence.CandidatePromptTokensPerSecond = value.PromptTokensPerSecond;
        evidence.CandidateDecodeTokensPerSecond = value.DecodeTokensPerSecond;
        evidence.CandidateTimeToFirstTokenMilliseconds = value.TimeToFirstTokenMilliseconds;
        evidence.CandidateLoadMilliseconds = value.LoadMilliseconds;
        evidence.CandidateSustainedDecodeTokensPerSecond = value.SustainedDecodeTokensPerSecond;
        evidence.CandidatePeakGpuMemoryUsedMib = value.PeakGpuMemoryUsedMib;
        evidence.CandidateMaxGpuTemperatureC = value.MaxGpuTemperatureC;
        evidence.CandidateAverageGpuPowerWatts = value.AverageGpuPowerWatts;
    }

    /// <summary>Legacy helper retained for historical tests/callers. Production promotion is now
    /// RuntimeCandidatePromotionPolicy and does not use this one-number comparison.</summary>
    internal static bool ShouldPromote(double originalScore, double candidateScore, double thresholdPercent)
    {
        if (!double.IsFinite(candidateScore) || candidateScore <= 0) return false;
        if (!double.IsFinite(originalScore) || originalScore <= 0) return true;
        double improvement = 100.0 * (candidateScore - originalScore) / originalScore;
        return improvement >= Math.Max(0, thresholdPercent);
    }

    internal static string? FindRepresentativeModel()
    {
        foreach (string role in new[] { "standard", "coding", "fast", "reasoning", "reviewer", "planner", "uncensored" })
        {
            string? path = LocalAiPaths.GetRoleModelPathOrNull(role);
            if (IsGguf(path)) return Path.GetFullPath(path!);
        }

        foreach (string root in new[] { ModelDownloadService.ModelsDirectory, LocalAiPaths.ModelsPath })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                string? path = Directory.EnumerateFiles(root, "*.gguf", SearchOption.AllDirectories)
                    .FirstOrDefault(File.Exists);
                if (path != null) return Path.GetFullPath(path);
            }
            catch { }
        }
        return null;
    }

    private RuntimeRegressionEvidenceDto Save(
        RuntimeRegressionEvidenceDto evidence,
        string decision,
        string activeTag,
        string reason)
    {
        evidence.Decision = decision;
        evidence.ActiveTag = activeTag;
        evidence.Reason = reason;
        evidence.MeasuredUtc = DateTime.UtcNow;
        _store.Append(evidence);
        return evidence;
    }

    private static bool IsGguf(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(path)
        && string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase);

    private static void TryRestore(string backend, string tag)
    {
        try { LocalAiPaths.SetActiveRuntimeBuildTag(backend, tag); } catch { }
    }

    private static string Trim(string value)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= 240 ? value : value[..240] + "…";
    }
}
