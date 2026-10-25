using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Receives the outcome of every real text-runtime launch so a freshly adopted build can be put on probation and
/// rolled back automatically when its first launches fail. One static seam, set by the server composition.</summary>
internal static class ManagedRuntimeLaunchObserver
{
    internal static volatile Action<string, bool, string?>? Handler;

    internal static void Report(string runtimeDirectory, bool succeeded, string? message)
    {
        try { Handler?.Invoke(runtimeDirectory, succeeded, message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning("Managed runtime launch observer failed: " + ex.Message);
        }
    }
}

/// <summary>Production host for <see cref="ManagedRuntimeUpdater"/>: wraps the installer (stage-only), the real
/// llama-server probe pointed at the staged folder, the local benchmark history and the active-pointer store.</summary>
internal sealed class ManagedRuntimeUpdateHost : IManagedRuntimeHost
{
    private const long ProbeModelMaxBytes = 8L << 30;
    private readonly LocalAiProcessManager _process;
    private readonly RuntimeInstallService _installer;
    private readonly ComponentInventoryService _inventory;
    private readonly ILlamaServerPerformanceProbe _probe;
    private readonly LocalBenchmarkHistoryStore _history;
    private readonly GpuInventoryService _gpu = new();
    private readonly Dictionary<string, RuntimeBenchResult?> _benchCache = new(StringComparer.Ordinal);

    internal ManagedRuntimeUpdateHost(LocalAiProcessManager process, RuntimeInstallService installer,
        ComponentInventoryService inventory, ILlamaServerPerformanceProbe probe, LocalBenchmarkHistoryStore history)
    {
        _process = process;
        _installer = installer;
        _inventory = inventory;
        _probe = probe;
        _history = history;
    }

    public bool IsGamingActive() => _process.GamingActive;

    /// <summary>Conservative: a resident text server is "busy" unless its slots report idle. An unreadable answer counts as busy.</summary>
    public bool IsGenerationRunning()
    {
        if (_process.GetTextProcessId() is null) return false;
        try
        {
            // The same slot-state probe the media GPU takeover trusts: only a proven Idle (or absent) server counts as idle.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            TextInferenceQuiescence.SlotProbe probe = TextInferenceQuiescence.ProbeAsync(cts.Token).GetAwaiter().GetResult();
            return probe.State is not (TextInferenceQuiescence.SlotState.Idle or TextInferenceQuiescence.SlotState.TextServerAbsent);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return true;
        }
    }

    public async Task<CudaHardware> ReadCudaHardwareAsync(CancellationToken ct)
    {
        GpuAdapterStatus? nvidia = _gpu.GetStatus().Adapters
            .Where(a => a.VendorId == RuntimeBackendAdvisor.NvidiaVendor && !a.IsSoftware)
            .OrderByDescending(a => a.DedicatedVideoMemoryBytes).FirstOrDefault();
        if (nvidia is null) return new CudaHardware(null, null);
        List<GpuPerformanceTelemetryDto> smi = await new GpuPerformanceTelemetryService().QueryAsync(ct).ConfigureAwait(false);
        double? cc = smi.FirstOrDefault(d => d.ComputeCapability is not null
            && nvidia.Name.Contains(d.Name, StringComparison.OrdinalIgnoreCase))?.ComputeCapability;
        return new CudaHardware(cc, nvidia.DriverVersion);
    }

    public async Task<IReadOnlyList<RuntimeUpdateTarget>> GetTargetsAsync(CancellationToken ct)
    {
        ResolvedTextRuntime? resolved = InstalledRuntimeResolver.Resolve(
            await _inventory.DiscoverAsync(ct).ConfigureAwait(false),
            b => LocalAiPaths.GetActiveRuntimeBuildTag(b) ?? LocalAiPaths.DefaultRuntimeBuildTag(b));
        if (resolved is null) return Array.Empty<RuntimeUpdateTarget>();
        return new[] { new RuntimeUpdateTarget(resolved.Backend, resolved.Runtime.Ownership, GetActive(resolved.Backend)) };
    }

    public async Task<IReadOnlyList<RuntimeCandidate>> ListAvailableAsync(string backend, int cudaMajor, CancellationToken ct)
    {
        List<RuntimeBuildOption> options = await _installer.ListAvailableAsync(backend, cudaMajor, ct).ConfigureAwait(false);
        return options.Select(o => new RuntimeCandidate(new RuntimeBuildId(backend, o.Tag, o.CudaToolkit), o.AssetSizeBytes)).ToList();
    }

    public RuntimeBuildId? GetActive(string backend)
    {
        string tag = LocalAiPaths.GetActiveRuntimeBuildTag(backend) ?? LocalAiPaths.DefaultRuntimeBuildTag(backend);
        return new RuntimeBuildId(backend, tag, backend == "text-cuda" ? LocalAiPaths.ResolveCudaToolkit(tag) : null);
    }

    public IReadOnlyList<InstalledBuild> ListInstalled(string backend)
    {
        var result = new List<InstalledBuild>();
        if (!Directory.Exists(LocalAiPaths.RuntimeRoot)) return result;
        foreach (string dir in Directory.EnumerateDirectories(LocalAiPaths.RuntimeRoot))
        {
            if (!LocalAiPaths.TryParseRuntimeFolder(backend, Path.GetFileName(dir), out string? tag, out string? toolkit) || tag is null) continue;
            if (!File.Exists(Path.Combine(dir, LocalAiPaths.LlamaServerExecutable))) continue;
            result.Add(new InstalledBuild(new RuntimeBuildId(backend, tag, toolkit), IsParuxManagedFolder(dir)));
        }
        return result;
    }

    public async Task<RuntimeStageResult> StageAsync(RuntimeCandidate candidate, int cudaMajor, CancellationToken ct)
    {
        RuntimeBuildId id = candidate.Id;
        if (ListInstalled(id.Backend).Any(i => i.Id == id)) return new RuntimeStageResult(true, "already installed");
        try
        {
            string? toolkit = await _installer.StageTextBuildAsync(id.Backend, id.Tag, cudaMajor, ct).ConfigureAwait(false);
            if (id.Toolkit is not null && toolkit != id.Toolkit)
            {
                // The release published a different toolkit than the catalog listing promised: do not keep a mismatched build.
                Remove(new RuntimeBuildId(id.Backend, id.Tag, toolkit));
                return new RuntimeStageResult(false, $"staged toolkit {toolkit} differs from the expected {id.Toolkit}");
            }
            return ListInstalled(id.Backend).Any(i => i.Id == id)
                ? new RuntimeStageResult(true, "staged")
                : new RuntimeStageResult(false, "the build was not found after staging");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RuntimeStageResult(false, ex.Message);
        }
    }

    public async Task<RuntimeSmokeResult> SmokeAsync(RuntimeBuildId build, CancellationToken ct)
    {
        string exe = Path.Combine(DirectoryOf(build), LocalAiPaths.LlamaServerExecutable);
        if (!File.Exists(exe)) return new RuntimeSmokeResult(false, "failed", "llama-server is missing from the staged build");
        string? model = PickProbeModel();
        if (model is null) return await BinaryOnlyAsync(build, exe, ct).ConfigureAwait(false);
        RuntimeBenchResult? measured = await MeasureAsync(build, model, ct).ConfigureAwait(false);
        if (measured is null) return await BinaryOnlyAsync(build, exe, ct).ConfigureAwait(false);
        bool sane = measured.DecodeTokensPerSecond > 0 && measured.PrefillTokensPerSecond > 0;
        return sane
            ? new RuntimeSmokeResult(true, "served", $"served a prompt on {measured.ModelLabel}")
            : new RuntimeSmokeResult(false, "failed", "the staged build produced no tokens");
    }

    public async Task<RuntimeBenchResult?> BenchmarkAsync(RuntimeBuildId build, CancellationToken ct)
    {
        string? model = PickProbeModel();
        if (model is null) return null;
        return await MeasureAsync(build, model, ct).ConfigureAwait(false);
    }

    public void Activate(RuntimeBuildId build)
    {
        if (IsGenerationRunning()) throw new InvalidOperationException("A generation is running; the runtime build was not switched.");
        LocalAiPaths.SetActiveRuntimeBuildTag(build.Backend, build.Tag, build.Toolkit);
    }

    public bool Remove(RuntimeBuildId build)
    {
        try
        {
            if (GetActive(build.Backend) == build) return false;
            if (build.Tag == LocalAiPaths.DefaultRuntimeBuildTag(build.Backend)) return false;
            string dir = DirectoryOf(build);
            if (!Directory.Exists(dir) || !IsParuxManagedFolder(dir)) return false;
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public string DirectoryOf(RuntimeBuildId build) =>
        Path.Combine(LocalAiPaths.RuntimeRoot, LocalAiPaths.RuntimeFolderName(build.Backend, build.Tag, build.Toolkit));

    public void CleanupStaging()
    {
        _benchCache.Clear(); // called at the start of every pass, so a cached measurement never outlives one pass
        try
        {
            if (!Directory.Exists(LocalAiPaths.RuntimeRoot)) return;
            if (_installer.ListJobs().Any(j => j.Stage is "queued" or "downloading" or "verifying" or "extracting" or "swapping" or "performance-check"))
                return; // an interactive install owns its own staging
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            foreach (string entry in Directory.EnumerateFileSystemEntries(LocalAiPaths.RuntimeRoot, ".staging-*"))
            {
                try
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
                    if (Directory.GetLastWriteTimeUtc(entry) > cutoff) continue;
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true); else File.Delete(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------------------------------------------------

    private static bool IsParuxManagedFolder(string dir)
    {
        try
        {
            string sidecar = Path.Combine(dir, LocalAiPaths.RuntimeSidecarFileName);
            if (!File.Exists(sidecar)) return false;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            return doc.RootElement.TryGetProperty("ownership", out JsonElement o) && o.GetString() == "PARUX_MANAGED";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The user's last-used model when it is small enough for a quick probe, otherwise the smallest managed GGUF.</summary>
    private string? PickProbeModel()
    {
        string last = _process.GetActiveTextModelPath();
        if (IsSmallGguf(last)) return Path.GetFullPath(last);
        string? best = null;
        long bestSize = long.MaxValue;
        foreach (string root in new[] { ModelDownloadService.ModelsDirectory, LocalAiPaths.ModelsPath })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (string path in Directory.EnumerateFiles(root, "*.gguf", SearchOption.AllDirectories))
                {
                    long size = new FileInfo(path).Length;
                    if (size is > 0 && size <= ProbeModelMaxBytes && size < bestSize) { best = path; bestSize = size; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return best is null ? null : Path.GetFullPath(best);
    }

    private static bool IsSmallGguf(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path)
        && string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase)
        && new FileInfo(path).Length is > 0 and <= ProbeModelMaxBytes;

    private async Task<RuntimeBenchResult?> MeasureAsync(RuntimeBuildId build, string modelPath, CancellationToken ct)
    {
        string cacheKey = build.Backend + "|" + build.Key + "|" + modelPath;
        if (_benchCache.TryGetValue(cacheKey, out RuntimeBenchResult? cached)) return cached;
        if (_process.GamingActive) throw new RuntimeUpdateDeferredException("Gaming Mode is on");
        if (_process.GetTextProcessId() is not null)
            throw new RuntimeUpdateDeferredException("a text model is loaded; testing waits until it is unloaded so the test is fair and cannot run out of memory");
        bool hasCuda = await _process.HasCudaHeavyGpuAsync(ct).ConfigureAwait(false);
        if (!string.Equals(build.Backend, hasCuda ? "text-cuda" : "text-vulkan", StringComparison.Ordinal))
            return null; // this machine would not execute that backend for serving: nothing fair to measure
        var model = new FileInfo(modelPath);
        GpuInventoryStatus gpu = _gpu.GetStatus();
        LlamaPerformanceProfileDto profile = RuntimePerformanceTuner.BuildCandidates(
                model.Length, gpu.Available ? gpu.LargestDedicatedVideoMemoryBytes : 0, "chat")
            .OrderBy(c => Math.Abs(c.BatchSize - 512)).ThenBy(c => c.CacheTypeK == "q8_0" ? 0 : 1).First();

        await using IAsyncDisposable lease = await RuntimePerformanceCoordinator.Shared.AcquireAsync(ct).ConfigureAwait(false);
        LlamaServerPerformanceProbe.RuntimeDirectoryOverride.Value = DirectoryOf(build);
        try
        {
            LlamaServerPerformanceMeasurement m = await _probe.RunAsync(modelPath, "chat", profile, false, ct).ConfigureAwait(false);
            var result = new RuntimeBenchResult(m.PromptTokensPerSecond, m.DecodeTokensPerSecond, Path.GetFileNameWithoutExtension(modelPath));
            _benchCache[cacheKey] = result;
            RecordHistory(build, modelPath, m);
            return result;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Gaming Mode", StringComparison.Ordinal))
        {
            throw new RuntimeUpdateDeferredException(ex.Message);
        }
        finally { LlamaServerPerformanceProbe.RuntimeDirectoryOverride.Value = null; }
    }

    private void RecordHistory(RuntimeBuildId build, string modelPath, LlamaServerPerformanceMeasurement m)
    {
        try
        {
            string? modelId = LocalBenchmarkIdentity.ModelId(modelPath);
            if (modelId is null) return;
            string hw = LocalBenchmarkIdentity.HardwareDigest(_gpu.GetStatus(), 0, LocalBenchmarkIdentity.CpuLabel());
            _history.Append(new LocalBenchmarkEntry
            {
                HardwareDigest = hw, ModelId = modelId, ModelLabel = Path.GetFileNameWithoutExtension(modelPath),
                RuntimeBackend = build.Backend, RuntimeBuild = build.Label, LaunchPlan = "runtime-update-gate",
                Workload = "runtime-update:chat", TtftMs = m.TimeToFirstTokenMilliseconds,
                PrefillTokensPerSecond = m.PromptTokensPerSecond, DecodeTokensPerSecond = m.DecodeTokensPerSecond,
                LoadMs = m.LoadMilliseconds
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private static async Task<RuntimeSmokeResult> BinaryOnlyAsync(RuntimeBuildId build, string exe, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--version");
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("the process did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new RuntimeSmokeResult(false, "failed", "llama-server --version did not finish in 30 s");
            }
            return process.ExitCode == 0
                ? new RuntimeSmokeResult(true, "binary_only", "no small model was available, so only startup of the binary was verified")
                : new RuntimeSmokeResult(false, "failed", "llama-server --version exited with code " + process.ExitCode);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new RuntimeSmokeResult(false, "failed", "llama-server could not start: " + ex.Message);
        }
    }
}
