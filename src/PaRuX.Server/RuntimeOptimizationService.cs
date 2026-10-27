using System.Collections.Concurrent;
using System.Diagnostics;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Full "Optimize for this PC" pipeline: quick llama-bench search first, then real isolated
/// llama-server validation (TTFT, prompt/decode throughput, concurrency, sustained speed,
/// power/thermal data), empirical GPU-layout comparison, measured C1/C2/C4/C8 serving race,
/// KV-cache precision race, prompt-cache cold/warm evidence, and speculative decoding comparison.
/// Persisted launch policy changes only after a complete valid winner exists.
/// </summary>
internal sealed record RuntimeOptimizationCompletion(string ModelPath, string Workload, string Backend, string RuntimeTag, string HardwareKey);

public sealed class RuntimeOptimizationService
{
    private const long MiB = 1024L * 1024L;

    private sealed class Entry
    {
        public readonly object Gate = new();
        public RuntimeOptimizationRun Status = new();
        public RuntimeOptimizationRequest Request = new();
        public CancellationTokenSource Cancellation = new();
    }

    private readonly RuntimePerformanceTuner _quick;
    private readonly ILlamaServerPerformanceProbe _server;
    private readonly RuntimePerformanceProfileStore _profiles;
    private readonly RuntimePerformancePolicyStore _policy;
    private readonly LocalAiProcessManager _process;
    private readonly GpuPerformanceTelemetryService _telemetry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Entry> _runs = new();

    /// <summary>Raised after a run saved its winner, for BOTH the manual button and the idle trigger, so they surface identical results
    /// (benchmark history, calibration registry). A failing observer never fails the run.</summary>
    internal Action<RuntimeOptimizationRun, RuntimeOptimizationCompletion>? Completed { get; set; }

    public RuntimeOptimizationService(
        RuntimePerformanceTuner quick,
        ILlamaServerPerformanceProbe server,
        RuntimePerformanceProfileStore profiles,
        RuntimePerformancePolicyStore policy,
        LocalAiProcessManager process,
        GpuPerformanceTelemetryService telemetry)
    {
        _quick = quick;
        _server = server;
        _profiles = profiles;
        _policy = policy;
        _process = process;
        _telemetry = telemetry;
    }

    public RuntimeOptimizationRun Start(RuntimeOptimizationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModelPath))
            throw new InvalidDataException("A GGUF model path is required.");
        string model = Path.GetFullPath(request.ModelPath);
        if (!model.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || !File.Exists(model))
            throw new InvalidDataException("Optimize requires an existing GGUF model.");
        string workload = RuntimePerformanceTuner.NormalizeWorkload(request.Workload);

        var normalized = new RuntimeOptimizationRequest
        {
            ModelPath = model,
            Label = request.Label,
            Workload = workload,
            IncludeSpeculative = request.IncludeSpeculative,
            IncludeMultiGpu = request.IncludeMultiGpu,
            SustainedValidation = request.SustainedValidation,
            Trigger = string.Equals(request.Trigger, "idle", StringComparison.OrdinalIgnoreCase) ? "idle" : "manual"
        };
        var status = new RuntimeOptimizationRun
        {
            Id = Guid.NewGuid().ToString("N"),
            ModelPath = model,
            Workload = workload,
            PerformanceMode = _policy.GetMode(),
            Trigger = normalized.Trigger,
            StartedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        var entry = new Entry { Status = status, Request = normalized };
        _runs[status.Id] = entry;
        _ = Task.Run(() => ExecuteAsync(entry));
        return Clone(status);
    }

    public RuntimeOptimizationRun? Get(string id)
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
            if (!string.IsNullOrWhiteSpace(entry.Status.QuickTuneRunId)) _quick.Cancel(entry.Status.QuickTuneRunId!);
            return true;
        }
    }

    private async Task ExecuteAsync(Entry entry)
    {
        bool acquired = false;
        IAsyncDisposable? performanceLane = null;
        try
        {
            await _gate.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
            acquired = true;
            performanceLane = await RuntimePerformanceCoordinator.Shared.AcquireAsync(entry.Cancellation.Token).ConfigureAwait(false);
            Set(entry, "running", "Quick-searching batch, KV cache and context candidates…");

            RuntimePerformanceTuneRun quick = _quick.Start(new RuntimePerformanceTuneRequest
            {
                ModelPath = entry.Request.ModelPath,
                Label = entry.Request.Label,
                Workload = entry.Request.Workload,
                IncludeSpeculative = false,
                IncludeMultiGpu = entry.Request.IncludeMultiGpu,
                SustainedValidation = false
            });
            lock (entry.Gate) entry.Status.QuickTuneRunId = quick.Id;

            while (true)
            {
                entry.Cancellation.Token.ThrowIfCancellationRequested();
                RuntimePerformanceTuneRun? current = _quick.Get(quick.Id);
                if (current == null) throw new InvalidOperationException("Quick tuning run disappeared.");
                lock (entry.Gate)
                {
                    entry.Status.QuickTune = current;
                    entry.Status.Stage = current.Stage;
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
                if (current.Status is "completed" or "failed" or "cancelled") { quick = current; break; }
                await Task.Delay(500, entry.Cancellation.Token).ConfigureAwait(false);
            }

            if (quick.Status != "completed" || quick.Winner == null)
                throw new InvalidOperationException(quick.Error ?? "Quick tuning did not produce a valid winner.");

            LlamaPerformanceProfileDto quickProfile = RuntimePerformanceProfileCopy.Copy(quick.Winner);
            quickProfile.SpeculativeType = "none";
            quickProfile.SpeculativeDraftMax = 0;
            quickProfile.SpeculativeDraftModelPath = null;
            quickProfile.SpeculativeDraftDevice = null;

            List<GpuPerformanceTelemetryDto> gpus = await _telemetry.QueryAsync(entry.Cancellation.Token).ConfigureAwait(false);
            long modelBytes = new FileInfo(entry.Status.ModelPath).Length;
            long largestVramBytes = gpus.Count == 0 ? 0 : gpus.Max(gpu => (long)(gpu.MemoryTotalMib ?? 0) * MiB);
            if (quickProfile.ReasoningEffort == "default")
                (quickProfile.ReasoningEffort, quickProfile.ReasoningBudget) = RuntimeLaunchOptimizer.RecommendReasoning(entry.Status.Workload);
            quickProfile.ParallelSlots = Math.Max(1,
                RuntimeLaunchOptimizer.RecommendParallelSlots(modelBytes, largestVramBytes, entry.Status.Workload));
            quickProfile.ContinuousBatching = true;
            quickProfile.SlotPromptSimilarity = entry.Status.Workload == "coding" ? 0.20 : 0.10;

            List<LlamaPerformanceProfileDto> layouts = BuildTopologyCandidates(quickProfile, gpus, entry.Request.IncludeMultiGpu);
            Set(entry, "running", layouts.Count > 1
                ? "Racing single-GPU, layer and row multi-GPU layouts through isolated llama-server…"
                : "Validating the quick winner through an isolated llama-server…");

            foreach (LlamaPerformanceProfileDto layout in layouts)
            {
                RuntimeOptimizationServerResult measured = await MeasureAsync(entry, layout, entry.Request.SustainedValidation).ConfigureAwait(false);
                lock (entry.Gate) entry.Status.ServerCandidates.Add(measured);
            }

            List<RuntimeOptimizationServerResult> topologyResults;
            lock (entry.Gate)
                topologyResults = entry.Status.ServerCandidates
                    .Where(candidate => candidate.Status == "completed" && candidate.Profile.SpeculativeType == "none").ToList();
            if (topologyResults.Count == 0) throw new InvalidOperationException("No ordinary server layout completed successfully.");

            Score(topologyResults, entry.Status.PerformanceMode);
            RuntimeOptimizationServerResult singleBaseline = topologyResults.FirstOrDefault(candidate =>
                string.Equals(candidate.Profile.SplitMode, "none", StringComparison.OrdinalIgnoreCase)
                && !candidate.Profile.Device.Contains(',')) ?? topologyResults[0];
            RuntimeOptimizationServerResult topologyWinner = RuntimeOptimizationPolicy.ChooseOrdinaryTopologyWinner(topologyResults, singleBaseline);

            // Serving concurrency is its own dimension. The old pipeline inherited one heuristic
            // slot count from RuntimeLaunchOptimizer and never compared C1/C2/C4/C8. RTX 50-series
            // now gets C8 in the search space; other hardware keeps C1/C2/C4. Every row launches an
            // isolated server, so a failed/OOM C8 is evidence and cannot poison the active runtime.
            Set(entry, "running", Rtx50SeriesOptimization.Detect(gpus) != null
                ? "Racing RTX 50 serving concurrency C1/C2/C4/C8…"
                : "Racing serving concurrency C1/C2/C4…");
            var concurrencyRace = new List<RuntimeOptimizationServerResult> { topologyWinner };
            foreach (LlamaPerformanceProfileDto concurrency in RuntimeConcurrencyOptimization.BuildCandidates(topologyWinner.Profile, gpus))
            {
                RuntimeOptimizationServerResult measured = await MeasureAsync(entry, concurrency, entry.Request.SustainedValidation).ConfigureAwait(false);
                lock (entry.Gate) entry.Status.ServerCandidates.Add(measured);
                if (measured.Status == "completed") concurrencyRace.Add(measured);
            }
            Score(concurrencyRace, entry.Status.PerformanceMode);
            RuntimeOptimizationServerResult concurrencyWinner = RuntimeConcurrencyOptimization.ChooseWinner(
                concurrencyRace, topologyWinner, entry.Status.PerformanceMode);

            Set(entry, "running", "Racing KV cache precision on the winning topology/concurrency…");
            foreach (LlamaPerformanceProfileDto kv in BuildKvCandidates(concurrencyWinner.Profile, entry.Status.Workload))
            {
                RuntimeOptimizationServerResult measured = await MeasureAsync(entry, kv, entry.Request.SustainedValidation).ConfigureAwait(false);
                lock (entry.Gate) entry.Status.ServerCandidates.Add(measured);
            }

            List<RuntimeOptimizationServerResult> kvRace;
            lock (entry.Gate)
                kvRace = entry.Status.ServerCandidates
                    .Where(candidate => candidate.Status == "completed"
                        && candidate.Profile.SpeculativeType == "none"
                        && candidate.Profile.ParallelSlots == concurrencyWinner.Profile.ParallelSlots
                        && SameTopology(candidate.Profile, concurrencyWinner.Profile)).ToList();
            Score(kvRace, entry.Status.PerformanceMode);
            bool fullPrecisionFailed;
            lock (entry.Gate)
                fullPrecisionFailed = entry.Status.ServerCandidates.Any(candidate => candidate.Status == "failed"
                    && string.Equals(candidate.Profile.CacheTypeK, "f16", StringComparison.OrdinalIgnoreCase)
                    && candidate.Profile.SpeculativeType == "none");
            long vramBytes = largestVramBytes;
            bool needsQuantisedKv = KvCachePolicy.ContextNeedsQuantisedKv(
                SafeReadGguf(entry.Status.ModelPath),
                RuntimeLaunchOptimizer.TotalContextFor(concurrencyWinner.Profile.ContextSize, concurrencyWinner.Profile.ParallelSlots, concurrencyWinner.Profile.ContextIsPerRequest),
                modelBytes, vramBytes);
            KvChoice kvChoice = KvCachePolicy.Choose(kvRace, concurrencyWinner, entry.Status.Workload, needsQuantisedKv, fullPrecisionFailed);
            RuntimeOptimizationServerResult ordinaryWinner = kvChoice.Winner;
            lock (entry.Gate) entry.Status.KvDecision = kvChoice.Reason;

            // Hardware-specific dimensions (GPU layers, backend fit, flash attention, context size, threads). On a PC where the
            // model fits a NVIDIA GPU entirely this adds no server run at all.
            ordinaryWinner = await RaceHardwareAsync(entry, ordinaryWinner, gpus, modelBytes, largestVramBytes).ConfigureAwait(false);

            // Speculative decoding is a single-stream feature: MTP/n-gram drafting needs extra VRAM and does not batch like C4.
            // Racing it on the C4 serving profile measured it at ~11 tok/s on a 27B (VRAM exhausted) against ~120 tok/s with one
            // slot, so it is raced against its own one-slot plain baseline and only replaces the serving winner when it beats
            // that baseline by the stated margin with identical greedy output.
            RuntimeOptimizationServerResult speculativeBaseline = ordinaryWinner;
            if (entry.Request.IncludeSpeculative
                && (entry.Request.Workload is "coding" or "long-context" || SafeHasEmbeddedMtp(entry.Status.ModelPath)))
            {
                if (ordinaryWinner.Profile.ParallelSlots > 1)
                {
                    Set(entry, "running", "Measuring the single-stream baseline for speculative decoding…");
                    LlamaPerformanceProfileDto single = RuntimePerformanceProfileCopy.Copy(ordinaryWinner.Profile);
                    single.Id += "-spec-c1";
                    single.DisplayName += " · single stream";
                    single.ParallelSlots = 1;
                    RuntimeOptimizationServerResult singleResult = await MeasureAsync(entry, single, entry.Request.SustainedValidation).ConfigureAwait(false);
                    lock (entry.Gate) entry.Status.ServerCandidates.Add(singleResult);
                    if (singleResult.Status == "completed") speculativeBaseline = singleResult;
                }
                Set(entry, "running", "Comparing n-gram and compatible local speculative decoding…");
                foreach (LlamaPerformanceProfileDto speculative in BuildSpeculativeCandidates(
                    speculativeBaseline.Profile, entry.Status.ModelPath, entry.Status.Workload))
                {
                    RuntimeOptimizationServerResult specResult = await MeasureAsync(entry, speculative, entry.Request.SustainedValidation).ConfigureAwait(false);
                    lock (entry.Gate) entry.Status.ServerCandidates.Add(specResult);
                }
            }

            List<RuntimeOptimizationServerResult> successful;
            lock (entry.Gate) successful = entry.Status.ServerCandidates.Where(candidate => candidate.Status == "completed").ToList();
            Score(successful, entry.Status.PerformanceMode);
            RuntimeOptimizationServerResult speculativeWinner = ChooseWinner(successful, speculativeBaseline);
            RuntimeOptimizationServerResult winner = ReferenceEquals(speculativeWinner, speculativeBaseline) ? ordinaryWinner : speculativeWinner;

            string? gpuFingerprint = quick.HardwareKey;
            if (string.IsNullOrWhiteSpace(gpuFingerprint))
                throw new InvalidOperationException("Quick tuning completed without a hardware identity; its persisted launch profile cannot be resolved safely.");
            string backendKey = await _process.HasCudaHeavyGpuAsync(entry.Cancellation.Token).ConfigureAwait(false) ? "text-cuda" : "text-vulkan";
            string runtimeTag = LocalAiPaths.GetActiveRuntimeBuildTag(backendKey) ?? LocalAiPaths.DefaultRuntimeBuildTag(backendKey);
            RuntimePerformanceProfileSelection? selection = _profiles.FindValid(entry.Status.ModelPath, entry.Status.Workload, runtimeTag, gpuFingerprint);
            if (selection == null)
                throw new InvalidOperationException("Quick tuning completed but its exact persisted launch profile could not be resolved for the current model/runtime/hardware identity.");

            selection.Profile = RuntimePerformanceProfileCopy.Copy(winner.Profile);
            selection.PromptTokensPerSecond = winner.PromptTokensPerSecond ?? selection.PromptTokensPerSecond;
            selection.DecodeTokensPerSecond = winner.DecodeTokensPerSecond ?? selection.DecodeTokensPerSecond;
            selection.TimeToFirstTokenMilliseconds = winner.TimeToFirstTokenMilliseconds;
            selection.LoadMilliseconds = winner.LoadMilliseconds;
            selection.SustainedDecodeTokensPerSecond = winner.SustainedDecodeTokensPerSecond;
            selection.ConcurrentDecodeTokensPerSecond = winner.ConcurrentDecodeTokensPerSecond;
            selection.ConcurrentRequests = winner.ConcurrentRequests;
            selection.SustainedDecodeRegressionPercent = winner.SustainedDecodeRegressionPercent;
            selection.ThermalRegressionSuspected = winner.ThermalRegressionSuspected;
            selection.AverageGpuPowerWatts = winner.AverageGpuPowerWatts;
            selection.MaxGpuTemperatureC = winner.MaxGpuTemperatureC;
            selection.PeakGpuMemoryUsedMib = winner.PeakGpuMemoryUsedMib;
            selection.PromptCacheColdTtftMilliseconds = winner.PromptCacheColdTtftMilliseconds;
            selection.PromptCacheWarmTtftMilliseconds = winner.PromptCacheWarmTtftMilliseconds;
            selection.PromptCacheImprovementPercent = winner.PromptCacheImprovementPercent;
            selection.PromptCacheRecommended = winner.PromptCacheRecommended;
            selection.TunedUtc = DateTime.UtcNow;
            _profiles.Save(selection);
            // The tuned profile is saved either way. A probe runtime that survives is kept on record, so the next start refuses beside it.
            if (!_process.StopText()) Trace.TraceWarning("Performance tuning finished, but its probe runtime did not stop.");

            RuntimeOptimizationRun finishedSnapshot;
            lock (entry.Gate)
            {
                entry.Status.Winner = RuntimePerformanceProfileCopy.Copy(winner.Profile);
                entry.Status.SpeculativeEnabled = winner.Profile.SpeculativeType != "none";
                entry.Status.Status = "completed";
                string cache = winner.PromptCacheRecommended switch
                {
                    true => " · KV reuse +" + (winner.PromptCacheImprovementPercent ?? 0).ToString("0.0") + "% TTFT",
                    false => " · KV reuse not beneficial",
                    _ => " · KV reuse unmeasured"
                };
                string thermal = winner.ThermalRegressionSuspected
                    ? " · thermal regression suspected (" + (winner.SustainedDecodeRegressionPercent ?? 0).ToString("0.0") + "% sustained drop)" : string.Empty;
                string concurrency = winner.ConcurrentDecodeTokensPerSecond.HasValue
                    ? " · C" + winner.ConcurrentRequests + "=" + winner.ConcurrentDecodeTokensPerSecond.Value.ToString("0.0") + " aggregate tok/s" : string.Empty;
                string speculative = winner.DraftAcceptancePercent.HasValue
                    ? " · draft accepted " + winner.DraftAcceptancePercent.Value.ToString("0") + "%" : string.Empty;
                entry.Status.Stage = "Optimized for this PC: " + winner.Profile.DisplayName
                    + " · ctx " + winner.Profile.ContextSize.ToString("N0")
                    + " · " + winner.DecodeTokensPerSecond?.ToString("0.0") + " decode tok/s"
                    + " · TTFT " + winner.TimeToFirstTokenMilliseconds?.ToString("0") + " ms"
                    + concurrency + speculative + cache + thermal;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
                finishedSnapshot = Clone(entry.Status);
            }
            try { Completed?.Invoke(finishedSnapshot, new RuntimeOptimizationCompletion(entry.Status.ModelPath, entry.Status.Workload, backendKey, runtimeTag, gpuFingerprint)); }
            catch (Exception ex) { Trace.TraceWarning("Optimization completion observer failed: " + ex.Message); }
        }
        catch (OperationCanceledException)
        {
            Set(entry, "cancelled", "Optimization cancelled. The last completed safe profile remains active.");
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                entry.Status.Status = "failed";
                entry.Status.Stage = "Optimization failed. Existing launch policy was preserved.";
                entry.Status.Error = ex.Message;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            if (performanceLane != null) await performanceLane.DisposeAsync().ConfigureAwait(false);
            if (acquired) _gate.Release();
            entry.Cancellation.Dispose();
        }
    }

    /// <summary>Races the hardware-aware candidates against the current winner, in two bounded stages (offload/attention/context, then threads).</summary>
    private async Task<RuntimeOptimizationServerResult> RaceHardwareAsync(
        Entry entry, RuntimeOptimizationServerResult winner, IReadOnlyList<GpuPerformanceTelemetryDto> gpus, long modelBytes, long telemetryVramBytes)
    {
        HardwareSearchFacts facts = CollectHardwareFacts(entry.Status.ModelPath, gpus, modelBytes, telemetryVramBytes);
        RuntimeOptimizationServerResult current = winner;
        List<LlamaPerformanceProfileDto> stageA = RuntimeHardwareSearchSpace.BuildOffloadCandidates(current.Profile, facts, entry.Status.Workload);
        if (stageA.Count > 0)
        {
            Set(entry, "running", "Racing GPU offload, attention and context options for this hardware…");
            var measuredA = new List<RuntimeOptimizationServerResult>();
            foreach (LlamaPerformanceProfileDto candidate in stageA)
            {
                RuntimeOptimizationServerResult measured = await MeasureAsync(entry, candidate, entry.Request.SustainedValidation).ConfigureAwait(false);
                lock (entry.Gate) entry.Status.ServerCandidates.Add(measured);
                measuredA.Add(measured);
            }
            current = RuntimeHardwareSearchSpace.Choose(current, measuredA);
        }
        List<LlamaPerformanceProfileDto> stageB = RuntimeHardwareSearchSpace.BuildThreadCandidates(current.Profile, facts);
        if (stageB.Count > 0)
        {
            Set(entry, "running", "Racing CPU thread counts (the CPU limits speed for this model on this PC)…");
            var measuredB = new List<RuntimeOptimizationServerResult>();
            foreach (LlamaPerformanceProfileDto candidate in stageB)
            {
                RuntimeOptimizationServerResult measured = await MeasureAsync(entry, candidate, entry.Request.SustainedValidation).ConfigureAwait(false);
                lock (entry.Gate) entry.Status.ServerCandidates.Add(measured);
                measuredB.Add(measured);
            }
            current = RuntimeHardwareSearchSpace.Choose(current, measuredB);
        }
        return current;
    }

    private static HardwareSearchFacts CollectHardwareFacts(string modelPath, IReadOnlyList<GpuPerformanceTelemetryDto> gpus, long modelBytes, long telemetryVramBytes)
    {
        long vram = telemetryVramBytes;
        if (vram <= 0)
        {
            // No NVIDIA telemetry (Vulkan / AMD / Intel): the DXGI inventory still knows the dedicated memory.
            try { GpuInventoryStatus inv = new GpuInventoryService().GetStatus(); if (inv.Available) vram = inv.LargestDedicatedVideoMemoryBytes; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        long ram = 0;
        try { ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch (InvalidOperationException) { }
        int physical = 0;
        try { physical = CpuTopology.PhysicalCores(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return new HardwareSearchFacts(modelBytes, vram, ram, Environment.ProcessorCount, physical,
            SafeReadGguf(modelPath)?.BlockCount, gpus.Any(g => g.MemoryTotalMib is > 0));
    }

    internal static List<LlamaPerformanceProfileDto> BuildTopologyCandidates(
        LlamaPerformanceProfileDto baseProfile,
        IReadOnlyList<GpuPerformanceTelemetryDto> telemetry,
        bool includeMultiGpu)
    {
        var result = new List<LlamaPerformanceProfileDto>();
        List<GpuPerformanceTelemetryDto> cuda = telemetry
            .Where(gpu => gpu.MemoryTotalMib is > 0)
            .OrderBy(gpu => gpu.Index)
            .ToList();

        if (cuda.Count == 0)
        {
            result.Add(RuntimePerformanceProfileCopy.Copy(baseProfile));
            return result;
        }

        GpuPerformanceTelemetryDto strongest = cuda
            .OrderByDescending(gpu => gpu.MemoryTotalMib)
            .ThenByDescending(gpu => gpu.SmClockMhz)
            .First();
        LlamaPerformanceProfileDto single = RuntimePerformanceProfileCopy.Copy(baseProfile);
        single.Id = baseProfile.Id + "-single-cuda" + strongest.Index;
        single.DisplayName = baseProfile.DisplayName + " · CUDA" + strongest.Index + " single";
        single.SplitMode = "none";
        single.MainGpu = strongest.Index;
        single.Device = "CUDA" + strongest.Index;
        single.TensorSplit = string.Empty;
        single.EnableCudaP2P = false;
        result.Add(single);

        if (includeMultiGpu && cuda.Count > 1)
        {
            string devices = string.Join(',', cuda.Select(gpu => "CUDA" + gpu.Index));
            string split = string.Join(',', cuda.Select(gpu => Math.Max(1, gpu.MemoryTotalMib ?? 1)));

            LlamaPerformanceProfileDto layer = RuntimePerformanceProfileCopy.Copy(baseProfile);
            layer.Id = baseProfile.Id + "-multi-layer";
            layer.DisplayName = baseProfile.DisplayName + " · multi-GPU layer";
            layer.SplitMode = "layer";
            layer.MainGpu = strongest.Index;
            layer.Device = devices;
            layer.TensorSplit = split;
            layer.EnableCudaP2P = false;
            layer.ParallelSlots = 1;
            result.Add(layer);

            LlamaPerformanceProfileDto row = RuntimePerformanceProfileCopy.Copy(layer);
            row.Id = baseProfile.Id + "-multi-row";
            row.DisplayName = baseProfile.DisplayName + " · multi-GPU row";
            row.SplitMode = "row";
            row.EnableCudaP2P = false;
            result.Add(row);

            LlamaPerformanceProfileDto p2p = RuntimePerformanceProfileCopy.Copy(layer);
            p2p.Id = baseProfile.Id + "-multi-layer-p2p";
            p2p.DisplayName = baseProfile.DisplayName + " · multi-GPU layer + P2P";
            p2p.EnableCudaP2P = true;
            result.Add(p2p);
        }
        return result;
    }

    /// <summary>The precisions raced against the baseline: full precision (f16), q8_0 and q4_0, minus the one the baseline already uses.</summary>
    internal static List<LlamaPerformanceProfileDto> BuildKvCandidates(LlamaPerformanceProfileDto baseline, string workload)
    {
        var result = new List<LlamaPerformanceProfileDto>();
        foreach ((string type, string label) in new[] { ("f16", "F16"), ("q8_0", "Q8"), ("q4_0", "Q4") })
        {
            if (string.Equals(baseline.CacheTypeK, type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(baseline.CacheTypeV, type, StringComparison.OrdinalIgnoreCase)) continue;
            // Mixed K/V precision is not explored; only matched pairs are raced.
            LlamaPerformanceProfileDto candidate = RuntimePerformanceProfileCopy.Copy(baseline);
            candidate.Id += "-kv-" + type.Replace("_0", string.Empty);
            candidate.DisplayName += " · " + label + " KV";
            candidate.CacheTypeK = candidate.CacheTypeV = type;
            result.Add(candidate);
        }
        return result;
    }

    internal static List<LlamaPerformanceProfileDto> BuildSpeculativeCandidates(
        LlamaPerformanceProfileDto baseline, string targetModelPath, string workload, bool? hasEmbeddedMtpHead = null)
    {
        var result = new List<LlamaPerformanceProfileDto>();
        bool mtp = hasEmbeddedMtpHead ?? SafeHasEmbeddedMtp(targetModelPath);
        if (mtp)
        {
            // Self-contained multi-token prediction: the model's own nextn head drafts, the full model
            // verifies. No extra file, no download; enabled only if the measured race below says so.
            LlamaPerformanceProfileDto candidate = RuntimePerformanceProfileCopy.Copy(baseline);
            candidate.Id += "-draft-mtp";
            candidate.DisplayName += " + draft-mtp (embedded)";
            candidate.SpeculativeType = "draft-mtp";
            candidate.SpeculativeDraftMax = 3;
            candidate.SpeculativeDraftModelPath = null;
            result.Add(candidate);
            LlamaPerformanceProfileDto chain = RuntimePerformanceProfileCopy.Copy(baseline);
            chain.Id += "-draft-mtp-ngram-mod";
            chain.DisplayName += " + draft-mtp,ngram-mod (embedded)";
            chain.SpeculativeType = "draft-mtp,ngram-mod";
            chain.SpeculativeDraftMax = 3;
            chain.SpeculativeDraftModelPath = null;
            result.Add(chain);
        }
        int ngramMax = RuntimePerformanceTuner.NormalizeWorkload(workload) == "coding" ? 64 : 32;
        foreach (string type in new[] { "ngram-simple", "ngram-mod" })
        {
            LlamaPerformanceProfileDto candidate = RuntimePerformanceProfileCopy.Copy(baseline);
            candidate.Id += "-" + type;
            candidate.DisplayName += " + " + type;
            candidate.SpeculativeType = type;
            candidate.SpeculativeDraftMax = ngramMax;
            result.Add(candidate);
        }
        foreach (SpeculativeDraftDiscovery.Candidate draft in SpeculativeDraftDiscovery.Discover(targetModelPath))
        {
            LlamaPerformanceProfileDto candidate = RuntimePerformanceProfileCopy.Copy(baseline);
            candidate.Id += "-" + draft.Type;
            candidate.DisplayName += " + " + draft.Type;
            candidate.SpeculativeType = draft.Type;
            candidate.SpeculativeDraftMax = draft.DraftMax;
            candidate.SpeculativeDraftModelPath = draft.Path;
            result.Add(candidate);
        }
        return result;
    }

    private static PaRuX.Intelligence.GgufModelInfo? SafeReadGguf(string path)
    {
        try { return File.Exists(path) ? PaRuX.Intelligence.GgufMetadataReader.Read(path) : null; }
        catch { return null; }
    }

    private static bool SafeHasEmbeddedMtp(string path)
    {
        try { return File.Exists(path) && PaRuX.Intelligence.GgufMetadataReader.Read(path).HasEmbeddedMtpHead; }
        catch { return false; }
    }

    private async Task<RuntimeOptimizationServerResult> MeasureAsync(
        Entry entry, LlamaPerformanceProfileDto profile, bool sustained)
    {
        var result = new RuntimeOptimizationServerResult { Profile = RuntimePerformanceProfileCopy.Copy(profile), Status = "running" };
        try
        {
            LlamaServerPerformanceMeasurement measured = await _server.RunAsync(
                entry.Status.ModelPath, entry.Status.Workload, profile, sustained, entry.Cancellation.Token).ConfigureAwait(false);
            result.Status = "completed";
            result.PromptTokensPerSecond = measured.PromptTokensPerSecond;
            result.DecodeTokensPerSecond = measured.DecodeTokensPerSecond;
            result.TimeToFirstTokenMilliseconds = measured.TimeToFirstTokenMilliseconds;
            result.LoadMilliseconds = measured.LoadMilliseconds;
            result.SustainedDecodeTokensPerSecond = measured.SustainedDecodeTokensPerSecond;
            result.ConcurrentDecodeTokensPerSecond = measured.ConcurrentDecodeTokensPerSecond;
            result.ConcurrentRequests = measured.ConcurrentRequests;
            result.SustainedDecodeRegressionPercent = RuntimeOptimizationPolicy.ComputeSustainedRegressionPercent(
                measured.DecodeTokensPerSecond, measured.SustainedDecodeTokensPerSecond);
            result.AverageGpuPowerWatts = measured.AverageGpuPowerWatts;
            result.PeakGpuPowerWatts = measured.PeakGpuPowerWatts;
            result.MaxGpuTemperatureC = measured.MaxGpuTemperatureC;
            result.ThermalRegressionSuspected = RuntimeOptimizationPolicy.IsThermalRegressionSuspected(
                result.SustainedDecodeRegressionPercent, measured.MaxGpuTemperatureC);
            result.PeakGpuMemoryUsedMib = measured.PeakGpuMemoryUsedMib;
            result.DraftTokens = measured.DraftTokens;
            result.DraftTokensAccepted = measured.DraftTokensAccepted;
            result.OutputDigest = measured.OutputDigest;
            result.OutputSample = measured.OutputSample;
            result.DraftAcceptancePercent = measured.DraftTokens > 0 ? 100.0 * measured.DraftTokensAccepted / measured.DraftTokens : null;
            result.PromptCacheColdTtftMilliseconds = measured.PromptCacheColdTtftMilliseconds;
            result.PromptCacheWarmTtftMilliseconds = measured.PromptCacheWarmTtftMilliseconds;
            result.PromptCacheImprovementPercent = measured.PromptCacheImprovementPercent;
            result.PromptCacheRecommended = measured.PromptCacheRecommended;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { result.Status = "failed"; result.Error = ex.Message; }
        return result;
    }

    internal static void Score(IReadOnlyList<RuntimeOptimizationServerResult> candidates, string mode)
    {
        List<RuntimeOptimizationServerResult> good = candidates.Where(candidate =>
            candidate.Status == "completed" && candidate.DecodeTokensPerSecond > 0
            && candidate.PromptTokensPerSecond > 0 && candidate.TimeToFirstTokenMilliseconds > 0).ToList();
        if (good.Count == 0) return;
        double maxDecode = good.Max(candidate => candidate.DecodeTokensPerSecond!.Value);
        double maxPrompt = good.Max(candidate => candidate.PromptTokensPerSecond!.Value);
        double minTtft = good.Min(candidate => candidate.TimeToFirstTokenMilliseconds!.Value);
        double maxSustained = good.Max(candidate => candidate.SustainedDecodeTokensPerSecond ?? candidate.DecodeTokensPerSecond!.Value);
        double maxConcurrent = good.Max(candidate => candidate.ConcurrentDecodeTokensPerSecond ?? candidate.DecodeTokensPerSecond!.Value);
        double maxEfficiency = good.Max(Efficiency);
        mode = RuntimePerformancePolicyStore.NormalizeMode(mode);

        foreach (RuntimeOptimizationServerResult candidate in good)
        {
            double decode = candidate.DecodeTokensPerSecond!.Value / maxDecode;
            double prompt = candidate.PromptTokensPerSecond!.Value / maxPrompt;
            double ttft = minTtft / candidate.TimeToFirstTokenMilliseconds!.Value;
            double sustained = (candidate.SustainedDecodeTokensPerSecond ?? candidate.DecodeTokensPerSecond.Value) / maxSustained;
            double concurrent = (candidate.ConcurrentDecodeTokensPerSecond ?? candidate.DecodeTokensPerSecond.Value) / maxConcurrent;
            double efficiency = maxEfficiency > 0 ? Efficiency(candidate) / maxEfficiency : decode;
            candidate.Score = mode switch
            {
                "turbo" => 0.60 * decode + 0.12 * prompt + 0.08 * ttft + 0.05 * sustained + 0.15 * concurrent,
                "eco" => 0.35 * decode + 0.12 * prompt + 0.08 * ttft + 0.10 * sustained + 0.10 * concurrent + 0.25 * efficiency,
                _ => 0.48 * decode + 0.17 * prompt + 0.12 * ttft + 0.08 * sustained + 0.15 * concurrent
            };
            if (candidate.ThermalRegressionSuspected) candidate.Score *= RuntimeOptimizationPolicy.ThermalScorePenalty;
        }
    }

    /// <summary>A speculative mode is kept only when the local calibration measured at least this decode speed-up (5%).</summary>
    internal const double MinimumSpeculativeGain = 1.05;

    internal static RuntimeOptimizationServerResult ChooseWinner(
        List<RuntimeOptimizationServerResult> successful, RuntimeOptimizationServerResult baseline)
    {
        if (successful.Count == 0) throw new InvalidOperationException("No real server candidate completed successfully.");
        // Walk the speculative candidates best-score first and keep the first one that passes EVERY gate. A faster mode that
        // fails the output-identity gate (e.g. the MTP+ngram chain on prose) must not hide a slower mode that passes (plain MTP).
        foreach (RuntimeOptimizationServerResult candidate in successful
            .Where(candidate => !ReferenceEquals(candidate, baseline) && candidate.Profile.SpeculativeType != "none")
            .OrderByDescending(candidate => candidate.Score ?? 0))
        {
            if (PassesSpeculativeGates(candidate, baseline)) return candidate;
        }
        return baseline;
    }

    private static bool PassesSpeculativeGates(RuntimeOptimizationServerResult best, RuntimeOptimizationServerResult baseline)
    {
        if (best.ThermalRegressionSuspected && !baseline.ThermalRegressionSuspected) return false;
        if (best.DraftTokens > 0 && (best.DraftAcceptancePercent ?? 0) < 35.0) return false;
        // Lossless means lossless: a mode whose greedy calibration output differs from the baseline's is not enabled.
        if (baseline.OutputDigest != null && best.OutputDigest != null && baseline.OutputDigest != best.OutputDigest) return false;
        double baselineDecode = baseline.DecodeTokensPerSecond ?? 0;
        double bestDecode = best.DecodeTokensPerSecond ?? 0;
        double baselineTtft = baseline.TimeToFirstTokenMilliseconds ?? double.MaxValue;
        double bestTtft = best.TimeToFirstTokenMilliseconds ?? double.MaxValue;
        return bestDecode >= baselineDecode * MinimumSpeculativeGain && bestTtft <= baselineTtft * 1.15;
    }

    private static bool SameTopology(LlamaPerformanceProfileDto left, LlamaPerformanceProfileDto right) =>
        string.Equals(left.SplitMode, right.SplitMode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Device, right.Device, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.TensorSplit, right.TensorSplit, StringComparison.OrdinalIgnoreCase)
        && left.MainGpu == right.MainGpu
        && left.EnableCudaP2P == right.EnableCudaP2P;

    private static double Efficiency(RuntimeOptimizationServerResult candidate)
    {
        double watts = candidate.AverageGpuPowerWatts ?? 0;
        double tps = candidate.SustainedDecodeTokensPerSecond ?? candidate.DecodeTokensPerSecond ?? 0;
        return watts > 0 ? tps / watts : 0;
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

    private static RuntimeOptimizationRun Clone(RuntimeOptimizationRun value) => new()
    {
        Id = value.Id,
        Status = value.Status,
        Stage = value.Stage,
        ModelPath = value.ModelPath,
        Workload = value.Workload,
        PerformanceMode = value.PerformanceMode,
        QuickTuneRunId = value.QuickTuneRunId,
        QuickTune = value.QuickTune,
        ServerCandidates = value.ServerCandidates.Select(candidate => new RuntimeOptimizationServerResult
        {
            Profile = RuntimePerformanceProfileCopy.Copy(candidate.Profile),
            Status = candidate.Status,
            PromptTokensPerSecond = candidate.PromptTokensPerSecond,
            DecodeTokensPerSecond = candidate.DecodeTokensPerSecond,
            TimeToFirstTokenMilliseconds = candidate.TimeToFirstTokenMilliseconds,
            LoadMilliseconds = candidate.LoadMilliseconds,
            SustainedDecodeTokensPerSecond = candidate.SustainedDecodeTokensPerSecond,
            ConcurrentDecodeTokensPerSecond = candidate.ConcurrentDecodeTokensPerSecond,
            ConcurrentRequests = candidate.ConcurrentRequests,
            SustainedDecodeRegressionPercent = candidate.SustainedDecodeRegressionPercent,
            ThermalRegressionSuspected = candidate.ThermalRegressionSuspected,
            AverageGpuPowerWatts = candidate.AverageGpuPowerWatts,
            PeakGpuPowerWatts = candidate.PeakGpuPowerWatts,
            MaxGpuTemperatureC = candidate.MaxGpuTemperatureC,
            PeakGpuMemoryUsedMib = candidate.PeakGpuMemoryUsedMib,
            DraftTokens = candidate.DraftTokens,
            DraftTokensAccepted = candidate.DraftTokensAccepted,
            OutputDigest = candidate.OutputDigest,
            OutputSample = candidate.OutputSample,
            DraftAcceptancePercent = candidate.DraftAcceptancePercent,
            PromptCacheColdTtftMilliseconds = candidate.PromptCacheColdTtftMilliseconds,
            PromptCacheWarmTtftMilliseconds = candidate.PromptCacheWarmTtftMilliseconds,
            PromptCacheImprovementPercent = candidate.PromptCacheImprovementPercent,
            PromptCacheRecommended = candidate.PromptCacheRecommended,
            Score = candidate.Score,
            Error = candidate.Error
        }).ToList(),
        Winner = value.Winner == null ? null : RuntimePerformanceProfileCopy.Copy(value.Winner),
        SpeculativeEnabled = value.SpeculativeEnabled,
        KvDecision = value.KvDecision,
        Trigger = value.Trigger,
        Error = value.Error,
        StartedUtc = value.StartedUtc,
        UpdatedUtc = value.UpdatedUtc
    };
}
