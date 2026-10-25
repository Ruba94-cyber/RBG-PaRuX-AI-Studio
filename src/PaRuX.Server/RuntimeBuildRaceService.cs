using System.Collections.Concurrent;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal interface IRuntimeBuildSwitcher
{
    string GetActiveTag(string backend);
    IReadOnlyList<string> ListInstalledTags(string backend);
    void SetActiveTag(string backend, string tag);
}

internal sealed class LocalRuntimeBuildSwitcher : IRuntimeBuildSwitcher
{
    public string GetActiveTag(string backend) =>
        LocalAiPaths.GetActiveRuntimeBuildTag(backend) ?? LocalAiPaths.DefaultRuntimeBuildTag(backend);

    public IReadOnlyList<string> ListInstalledTags(string backend) =>
        LocalAiPaths.ListInstalledRuntimeBuildTags(backend);

    public void SetActiveTag(string backend, string tag) =>
        LocalAiPaths.SetActiveRuntimeBuildTag(backend, tag);
}

/// <summary>
/// Races already-installed PaRuX-managed llama.cpp builds using the real isolated serving path.
/// Automatic promotion is decided by RuntimeCandidatePromotionPolicy, so decode throughput cannot
/// hide TTFT/load/VRAM/power/thermal or sustained regressions. Cancellation, baseline failure and
/// any unhandled error restore the original active tag.
/// </summary>
public sealed class RuntimeBuildRaceService
{
    private sealed class Entry
    {
        public readonly object Gate = new();
        public RuntimeBuildRaceStatusDto Status = new();
        public CancellationTokenSource Cancellation = new();
    }

    private readonly LocalAiProcessManager _process;
    private readonly IRuntimeBuildPerformanceProbe _probe;
    private readonly IRuntimeBuildSwitcher _switcher;
    private readonly SemaphoreSlim _raceGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Entry> _runs = new();

    public RuntimeBuildRaceService(LocalAiProcessManager process)
        : this(
            process,
            new LlamaServerRuntimeBuildPerformanceProbe(
                new LlamaServerPerformanceProbe(process, new GpuPerformanceTelemetryService())),
            new LocalRuntimeBuildSwitcher()) { }

    /// <summary>Compatibility constructor for existing deterministic tests that provide the older
    /// llama-bench probe. Production never uses this path.</summary>
    internal RuntimeBuildRaceService(
        LocalAiProcessManager process,
        ILlamaPerformanceProbe probe,
        IRuntimeBuildSwitcher switcher)
        : this(process, new LegacyLlamaBenchRuntimeBuildPerformanceProbe(probe), switcher) { }

    internal RuntimeBuildRaceService(
        LocalAiProcessManager process,
        IRuntimeBuildPerformanceProbe probe,
        IRuntimeBuildSwitcher switcher)
    {
        _process = process;
        _probe = probe;
        _switcher = switcher;
    }

    public RuntimeBuildRaceStatusDto Start(RuntimeBuildRaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string backend = NormalizeBackend(request.Backend);
        string modelPath = ValidateModel(request.ModelPath);
        string workload = RuntimePerformanceTuner.NormalizeWorkload(request.Workload);
        double minimumImprovement = Math.Clamp(request.MinimumImprovementPercent, 0, 50);
        string original = _switcher.GetActiveTag(backend);
        List<string> installed = _switcher.ListInstalledTags(backend)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(tag => string.Equals(tag, original, StringComparison.Ordinal))
            .ThenByDescending(tag => tag, StringComparer.Ordinal)
            .ToList();
        if (installed.Count < 2)
            throw new InvalidOperationException("Install at least two PaRuX-managed llama.cpp builds before running a runtime race.");
        if (!installed.Contains(original, StringComparer.Ordinal))
            throw new InvalidOperationException("The active llama.cpp build is not present in the managed build inventory; race is fail-closed.");

        var status = new RuntimeBuildRaceStatusDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Backend = backend,
            ModelPath = modelPath,
            OriginalTag = original,
            StartedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Candidates = installed.Select(tag => new RuntimeBuildRaceCandidateDto { Tag = tag }).ToList()
        };
        var entry = new Entry { Status = status };
        _runs[status.Id] = entry;
        _ = Task.Run(() => ExecuteAsync(
            entry, workload, minimumImprovement, request.SustainedValidation));
        return Clone(status);
    }

    public RuntimeBuildRaceStatusDto? Get(string id)
    {
        if (!_runs.TryGetValue(id, out Entry? entry)) return null;
        lock (entry.Gate) return Clone(entry.Status);
    }

    public bool Cancel(string id)
    {
        if (!_runs.TryGetValue(id, out Entry? entry)) return false;
        lock (entry.Gate)
        {
            if (entry.Status.Status is "completed" or "failed" or "cancelled") return false;
            entry.Cancellation.Cancel();
            return true;
        }
    }

    private async Task ExecuteAsync(
        Entry entry,
        string workload,
        double minimumImprovement,
        bool sustainedValidation)
    {
        bool acquired = false;
        string original = entry.Status.OriginalTag;
        GamingHardOffWatch? gaming = null;
        try
        {
            await using IAsyncDisposable performanceLease = await RuntimePerformanceCoordinator.Shared
                .AcquireAsync(entry.Cancellation.Token).ConfigureAwait(false);
            await _raceGate.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
            acquired = true;
            if (_process.GamingActive)
                throw new InvalidOperationException("Gaming Mode is active; runtime racing is blocked.");
            // A game launched mid-race stops the GPU probes; the original build is restored below.
            gaming = new GamingHardOffWatch(() => _process.GamingActive, entry.Cancellation.Token);
            if (_process.GetTextProcessId() != null)
                throw new InvalidOperationException("Unload the active text model before racing runtime builds.");
            if (_process.GetMediaProcessId() != null)
                throw new InvalidOperationException("Stop media generation before racing runtime builds.");

            Set(entry, "running", sustainedValidation
                ? "Benchmarking installed llama.cpp builds with sustained serving validation…"
                : "Benchmarking installed llama.cpp builds with real serving validation…");
            FileInfo model = new(entry.Status.ModelPath);
            GpuInventoryStatus gpu = new GpuInventoryService().GetStatus();
            LlamaPerformanceProfileDto profile = RuntimePerformanceTuner.BuildCandidates(
                    model.Length,
                    gpu.Available ? gpu.LargestDedicatedVideoMemoryBytes : 0,
                    workload)
                .OrderBy(candidate => Math.Abs(candidate.BatchSize - 1024))
                .ThenBy(candidate => candidate.CacheTypeK == "q8_0" ? 0 : 1)
                .First();

            var measurements = new Dictionary<string, RuntimeBuildPerformanceMeasurement>(StringComparer.Ordinal);
            foreach (RuntimeBuildRaceCandidateDto candidate in entry.Status.Candidates)
            {
                gaming.Token.ThrowIfCancellationRequested();
                lock (entry.Gate)
                {
                    candidate.Status = "running";
                    entry.Status.Stage = "Serving probe for runtime " + candidate.Tag + "…";
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
                try
                {
                    _switcher.SetActiveTag(entry.Status.Backend, candidate.Tag);
                    RuntimeBuildPerformanceMeasurement measurement = await _probe.RunAsync(
                        entry.Status.ModelPath,
                        workload,
                        profile,
                        sustainedValidation,
                        gaming.Token).ConfigureAwait(false);
                    measurements[candidate.Tag] = measurement;
                    ApplyMeasurement(candidate, measurement);
                    candidate.Status = "completed";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    candidate.Status = "failed";
                    candidate.Error = ex.Message;
                    candidate.EligibleForPromotion = false;
                    candidate.DecisionReason = "Serving probe failed; candidate cannot be promoted.";
                }
                finally
                {
                    lock (entry.Gate) entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
            }

            if (!measurements.TryGetValue(original, out RuntimeBuildPerformanceMeasurement? baselineMeasurement))
                throw new InvalidOperationException("The active baseline build did not complete the real serving probe; no automatic promotion is allowed.");

            RuntimeCandidateEvidence baselineEvidence = RuntimeBuildPromotionEvidence.From(
                original, entry.Status.ModelPath, baselineMeasurement);
            var policy = new RuntimePromotionPolicyOptions(
                MinimumCompositeImprovementPercent: minimumImprovement);
            RuntimePromotionDecision baselineScore = RuntimeCandidatePromotionPolicy.Evaluate(
                baselineEvidence, baselineEvidence, policy);

            RuntimeBuildRaceCandidateDto baselineDto = entry.Status.Candidates.First(candidate =>
                string.Equals(candidate.Tag, original, StringComparison.Ordinal));
            baselineDto.Score = baselineScore.BaselineScore;
            baselineDto.ImprovementPercent = 0;
            baselineDto.EligibleForPromotion = false;
            baselineDto.DecisionReason = "Current active baseline; challengers must clear all BIC guardrails.";

            RuntimeBuildRaceCandidateDto? winner = null;
            double winnerScore = double.NegativeInfinity;
            foreach (RuntimeBuildRaceCandidateDto candidate in entry.Status.Candidates)
            {
                if (string.Equals(candidate.Tag, original, StringComparison.Ordinal)) continue;
                if (!measurements.TryGetValue(candidate.Tag, out RuntimeBuildPerformanceMeasurement? measurement)) continue;

                RuntimeCandidateEvidence candidateEvidence = RuntimeBuildPromotionEvidence.From(
                    candidate.Tag, entry.Status.ModelPath, measurement);
                RuntimePromotionDecision decision = RuntimeCandidatePromotionPolicy.Evaluate(
                    baselineEvidence, candidateEvidence, policy);
                candidate.Score = decision.CandidateScore;
                candidate.ImprovementPercent = decision.ImprovementPercent;
                candidate.EligibleForPromotion = decision.Promote;
                candidate.DecisionReason = decision.Reason;

                if (decision.Promote && decision.CandidateScore > winnerScore)
                {
                    winner = candidate;
                    winnerScore = decision.CandidateScore;
                }
            }

            bool promote = winner != null;
            string finalTag = promote ? winner!.Tag : original;
            _switcher.SetActiveTag(entry.Status.Backend, finalTag);
            lock (entry.Gate)
            {
                entry.Status.WinnerTag = promote ? winner!.Tag : original;
                entry.Status.Switched = promote;
                entry.Status.Status = "completed";
                entry.Status.Stage = promote
                    ? "Promoted " + winner!.Tag + " after it cleared full BIC serving guardrails."
                    : "Kept " + original + "; no candidate cleared the full BIC promotion guardrails.";
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            TryRestore(entry.Status.Backend, original);
            Set(entry, "cancelled", gaming?.Triggered == true
                ? "Gaming Mode started; runtime race stopped and original build restored."
                : "Runtime race cancelled; original build restored.");
        }
        catch (Exception ex)
        {
            TryRestore(entry.Status.Backend, original);
            lock (entry.Gate)
            {
                entry.Status.Status = "failed";
                entry.Status.Stage = "Runtime race failed; original build restored.";
                entry.Status.Error = ex.Message;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            gaming?.Dispose();
            if (acquired) _raceGate.Release();
            entry.Cancellation.Dispose();
        }
    }

    private static void ApplyMeasurement(
        RuntimeBuildRaceCandidateDto candidate,
        RuntimeBuildPerformanceMeasurement measurement)
    {
        candidate.PromptTokensPerSecond = measurement.PromptTokensPerSecond;
        candidate.DecodeTokensPerSecond = measurement.DecodeTokensPerSecond;
        candidate.TimeToFirstTokenMilliseconds = measurement.TimeToFirstTokenMilliseconds;
        candidate.LoadMilliseconds = measurement.LoadMilliseconds;
        candidate.SustainedDecodeTokensPerSecond = measurement.SustainedDecodeTokensPerSecond;
        candidate.PeakGpuMemoryUsedMib = measurement.PeakGpuMemoryUsedMib;
        candidate.MaxGpuTemperatureC = measurement.MaxGpuTemperatureC;
        candidate.AverageGpuPowerWatts = measurement.AverageGpuPowerWatts;
    }

    private void TryRestore(string backend, string tag)
    {
        try { _switcher.SetActiveTag(backend, tag); } catch { }
    }

    /// <summary>Legacy throughput-only score retained for backward compatibility with historical
    /// evidence/tests. It is no longer used by production Build Race promotion.</summary>
    internal static double CombinedScore(double promptTps, double decodeTps)
    {
        double prompt = Math.Log(1.0 + Math.Max(0, promptTps));
        double decode = Math.Log(1.0 + Math.Max(0, decodeTps));
        return 0.30 * prompt + 0.70 * decode;
    }

    private static string NormalizeBackend(string? backend) => (backend ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "text-cuda" => "text-cuda",
        "text-vulkan" => "text-vulkan",
        _ => throw new InvalidDataException("Runtime race backend must be text-cuda or text-vulkan.")
    };

    private static string ValidateModel(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) throw new InvalidDataException("A GGUF model path is required.");
        string full = Path.GetFullPath(modelPath);
        if (!full.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidDataException("Runtime race requires an existing GGUF model.");
        if (!IsInside(full, LocalAiPaths.ModelsPath) && !IsInside(full, ModelDownloadService.ModelsDirectory))
            throw new InvalidDataException("The model must be inside a configured PaRuX model directory.");
        return full;
    }

    private static bool IsInside(string path, string directory)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void Set(Entry entry, string status, string stage)
    {
        lock (entry.Gate)
        {
            entry.Status.Status = status;
            entry.Status.Stage = stage;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
    }

    private static RuntimeBuildRaceStatusDto Clone(RuntimeBuildRaceStatusDto value) => new()
    {
        Id = value.Id,
        Status = value.Status,
        Stage = value.Stage,
        Backend = value.Backend,
        ModelPath = value.ModelPath,
        OriginalTag = value.OriginalTag,
        WinnerTag = value.WinnerTag,
        Switched = value.Switched,
        Candidates = value.Candidates.Select(candidate => new RuntimeBuildRaceCandidateDto
        {
            Tag = candidate.Tag,
            Status = candidate.Status,
            PromptTokensPerSecond = candidate.PromptTokensPerSecond,
            DecodeTokensPerSecond = candidate.DecodeTokensPerSecond,
            TimeToFirstTokenMilliseconds = candidate.TimeToFirstTokenMilliseconds,
            LoadMilliseconds = candidate.LoadMilliseconds,
            SustainedDecodeTokensPerSecond = candidate.SustainedDecodeTokensPerSecond,
            PeakGpuMemoryUsedMib = candidate.PeakGpuMemoryUsedMib,
            MaxGpuTemperatureC = candidate.MaxGpuTemperatureC,
            AverageGpuPowerWatts = candidate.AverageGpuPowerWatts,
            Score = candidate.Score,
            ImprovementPercent = candidate.ImprovementPercent,
            EligibleForPromotion = candidate.EligibleForPromotion,
            DecisionReason = candidate.DecisionReason,
            Error = candidate.Error
        }).ToList(),
        Error = value.Error,
        StartedUtc = value.StartedUtc,
        UpdatedUtc = value.UpdatedUtc
    };
}
