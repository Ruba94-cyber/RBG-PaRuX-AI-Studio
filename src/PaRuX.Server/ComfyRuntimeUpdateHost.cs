using System.Globalization;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Release-tag ordering and PyTorch/CUDA facts for ComfyUI builds. Pure, so the updater policy is testable without a disk.</summary>
internal static class ComfyBuildPolicy
{
    internal const string Backend = "media-comfyui";
    internal static readonly IReadOnlySet<string> CudaBackends = new HashSet<string>(StringComparer.Ordinal) { Backend };
    private static readonly Regex Version = new(@"^v?(\d{1,4})\.(\d{1,4})\.(\d{1,4})$", RegexOptions.Compiled);
    private static readonly Regex TorchCuda = new(@"^\s*cuda\s*(?::[^=]+)?=\s*(?:'(?<v>[^']*)'|""(?<v>[^""]*)""|None)", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>"v0.37.0" / "0.37.0" -> 37000. Anything else (including "legacy") is unorderable, which keeps it out of automatic updates.</summary>
    internal static int? ParseBuild(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        Match m = Version.Match(tag.Trim());
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1_000_000
             + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 1_000
             + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>CUDA label of an official portable variant: "nvidia" ships the current CUDA 13 wheels, "nvidia-cu126" the CUDA 12.6 ones.</summary>
    internal static string? ToolkitForVariant(string? variant) => variant switch
    {
        "nvidia" => "13.0",
        "nvidia-cu126" => "12.6",
        _ => null
    };

    internal static string? VariantForToolkit(string? toolkit) =>
        RuntimeCudaPolicy.MajorOf(toolkit) switch { 13 => "nvidia", 12 => "nvidia-cu126", _ => null };

    /// <summary>CUDA version the folder's PyTorch was compiled for (read from torch/version.py), or null for a CPU-only/unreadable torch.
    /// A CPU-only wheel on an NVIDIA machine is the failure this updater repairs: ComfyUI then renders on the CPU.</summary>
    internal static string? ReadTorchCuda(string portableRoot)
    {
        try
        {
            string file = Path.Combine(portableRoot, "python_embeded", "Lib", "site-packages", "torch", "version.py");
            if (!File.Exists(file) || new FileInfo(file).Length > 256 * 1024) return null;
            Match m = TorchCuda.Match(File.ReadAllText(file));
            return m.Success && m.Groups["v"].Success && m.Groups["v"].Value.Length > 0 ? m.Groups["v"].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>PyTorch's own version string (for example "2.14.1+cu130") from torch/version.py without starting Python.</summary>
    internal static string? ReadTorchVersion(string portableRoot)
    {
        try
        {
            string file = Path.Combine(portableRoot, "python_embeded", "Lib", "site-packages", "torch", "version.py");
            if (!File.Exists(file) || new FileInfo(file).Length > 256 * 1024) return null;
            Match m = Regex.Match(File.ReadAllText(file), @"^\s*__version__\s*(?::[^=]+)?=\s*(?:'(?<v>[^']*)'|""(?<v>[^""]*)"")", RegexOptions.Multiline);
            return m.Success ? m.Groups["v"].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string? ReadComfyVersion(string portableRoot)
    {
        try
        {
            string file = Path.Combine(portableRoot, "ComfyUI", "comfyui_version.py");
            if (!File.Exists(file)) return null;
            foreach (string line in File.ReadLines(file))
                if (line.StartsWith("__version__", StringComparison.Ordinal)) return line.Split('"').ElementAtOrDefault(1) ?? line.Split('\'').ElementAtOrDefault(1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>The updater-facing identity of an installed folder. A pre-managed ("legacy") build is ordered by the version it really
    /// contains; a build with a CPU-only PyTorch gets a null toolkit so any CUDA build of the same version counts as an upgrade (repair).</summary>
    internal static RuntimeBuildId IdOf(ComfyBuildIdentity identity, string portableRoot)
    {
        string? actualCuda = ReadTorchCuda(portableRoot);
        string tag = identity.Tag;
        if (identity.Tag == "legacy" && ReadComfyVersion(portableRoot) is { } v) tag = "v" + v;
        string? toolkit = actualCuda is null ? null : ToolkitForVariant(identity.Variant) ?? actualCuda;
        return new RuntimeBuildId(Backend, tag, toolkit);
    }
}

/// <summary>Production host for the ComfyUI component of the managed-runtime updater: stages a hash-verified official portable
/// build beside the active one, smoke-tests and benchmarks it on a private port, switches atomically between jobs, keeps the
/// previous build for rollback. User-located (USER_MANAGED) ComfyUI is reported, never modified.</summary>
internal sealed class ComfyRuntimeUpdateHost : IManagedRuntimeHost
{
    private readonly LocalAiProcessManager _process;
    private readonly RuntimeInstallService _installer;
    private readonly ComfyRuntimeStore _store;
    private readonly LocalBenchmarkHistoryStore _history;
    private readonly Func<CancellationToken, Task<IAsyncDisposable?>>? _acquireGpu;
    private readonly GpuInventoryService _gpu = new();
    private readonly Dictionary<string, RuntimeBenchResult?> _benchCache = new(StringComparer.Ordinal);

    internal ComfyRuntimeUpdateHost(LocalAiProcessManager process, RuntimeInstallService installer, ComfyRuntimeStore store,
        LocalBenchmarkHistoryStore history, Func<CancellationToken, Task<IAsyncDisposable?>>? acquireGpu = null)
    {
        _process = process;
        _installer = installer;
        _store = store;
        _history = history;
        _acquireGpu = acquireGpu;
    }

    public bool IsGamingActive() => _process.GamingActive;

    public bool IsGenerationRunning() => MediaExecutionCoordinator.Current.Snapshot().ActiveOwner != null;

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

    public Task<IReadOnlyList<RuntimeUpdateTarget>> GetTargetsAsync(CancellationToken ct)
    {
        IReadOnlyList<RuntimeUpdateTarget> none = Array.Empty<RuntimeUpdateTarget>();
        if (!_gpu.GetStatus().Adapters.Any(a => a.VendorId == RuntimeBackendAdvisor.NvidiaVendor && !a.IsSoftware))
            return Task.FromResult(none); // the AMD/Intel portable variants are not auto-managed
        if (LocalAiPaths.GetComponentLocation("comfyui") != null)
            return Task.FromResult<IReadOnlyList<RuntimeUpdateTarget>>(
                new[] { new RuntimeUpdateTarget(ComfyBuildPolicy.Backend, ComponentOwnership.USER_MANAGED, null) });
        if (!ComfyRuntimeStore.IsPortableRoot(_store.ActiveRoot)) return Task.FromResult(none);
        return Task.FromResult<IReadOnlyList<RuntimeUpdateTarget>>(
            new[] { new RuntimeUpdateTarget(ComfyBuildPolicy.Backend, ComponentOwnership.PARUX_MANAGED, GetActive(ComfyBuildPolicy.Backend)) });
    }

    public async Task<IReadOnlyList<RuntimeCandidate>> ListAvailableAsync(string backend, int cudaMajor, CancellationToken ct)
    {
        List<RuntimeBuildOption> options = await _installer.ListAvailableAsync(ComfyBuildPolicy.Backend, ct).ConfigureAwait(false);
        return options
            .Where(o => ComfyBuildPolicy.ToolkitForVariant(o.Variant) is not null)
            .Select(o => new RuntimeCandidate(new RuntimeBuildId(backend, o.Tag, ComfyBuildPolicy.ToolkitForVariant(o.Variant)), o.AssetSizeBytes))
            .ToList();
    }

    public IReadOnlyList<InstalledBuild> ListInstalled(string backend) =>
        _store.ListInstalled().Select(i => new InstalledBuild(ComfyBuildPolicy.IdOf(i, _store.PathOf(i)), ParuxManaged: true)).ToList();

    public RuntimeBuildId? GetActive(string backend)
    {
        ComfyBuildIdentity? active = _store.GetActiveIdentity();
        return active is null ? null : ComfyBuildPolicy.IdOf(active, _store.ActiveRoot);
    }

    private ComfyBuildIdentity? Resolve(RuntimeBuildId build) =>
        _store.ListInstalled().FirstOrDefault(i => ComfyBuildPolicy.IdOf(i, _store.PathOf(i)) == build);

    public async Task<RuntimeStageResult> StageAsync(RuntimeCandidate candidate, int cudaMajor, CancellationToken ct)
    {
        RuntimeBuildId id = candidate.Id;
        if (Resolve(id) is not null) return new RuntimeStageResult(true, "already installed");
        string? variant = ComfyBuildPolicy.VariantForToolkit(id.Toolkit);
        if (variant is null) return new RuntimeStageResult(false, "no portable variant for CUDA " + id.Toolkit);
        try
        {
            await _installer.StageComfyBuildAsync(id.Tag, variant, ct).ConfigureAwait(false);
            var identity = new ComfyBuildIdentity(id.Tag, variant);
            string staged = _store.PathOf(identity);
            // The wheel guard: a staged build whose PyTorch cannot use the GPU, or needs a newer CUDA than the driver supports, is discarded.
            string? cuda = ComfyBuildPolicy.ReadTorchCuda(staged);
            if (cuda is null)
            {
                _store.RemoveStored(identity);
                return new RuntimeStageResult(false, "the staged build's PyTorch has no CUDA support");
            }
            if (RuntimeCudaPolicy.MajorOf(cuda) > cudaMajor)
            {
                _store.RemoveStored(identity);
                return new RuntimeStageResult(false, $"the staged build's PyTorch needs CUDA {cuda}, newer than this GPU/driver supports (CUDA {cudaMajor})");
            }
            return Resolve(id) is not null
                ? new RuntimeStageResult(true, "staged")
                : new RuntimeStageResult(false, "the build was not found after staging");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new RuntimeStageResult(false, ex.Message); }
    }

    private ComfyCalibrationHarness HarnessFor(RuntimeBuildId build, string dataRoot)
    {
        string root = DirectoryOf(build);
        string models = Path.Combine(_store.ActiveRoot, "ComfyUI", "models");
        string yaml = Path.Combine(dataRoot, "extra_model_paths.yaml");
        Directory.CreateDirectory(dataRoot);
        // Models are shared read-only from the ACTIVE tree (they live there, not in a staged build).
        File.WriteAllText(yaml, "parux_active:\n  base_path: " + Path.Combine(_store.ActiveRoot, "ComfyUI").Replace('\\', '/') + "/\n"
            + "  diffusion_models: models/diffusion_models/\n  text_encoders: models/text_encoders/\n  vae: models/vae/\n");
        return new ComfyCalibrationHarness(new ComfyHarnessOptions
        {
            PythonExe = Path.Combine(root, "python_embeded", "python.exe"),
            MainPy = Path.Combine(root, "ComfyUI", "main.py"),
            WorkingDirectory = Path.Combine(root, "ComfyUI"),
            DataRoot = dataRoot,
            ModelRoots = new[] { models },
            ExtraLaunchArguments = new[] { "--extra-model-paths-config", yaml },
            IsGamingActive = IsGamingActive,
            IsBusy = IsGenerationRunning,
            AcquireGpu = _acquireGpu
        });
    }

    private string DataRootFor(RuntimeBuildId build) =>
        Path.Combine(ServerState.StateDirectory, "comfy-update-test", build.Tag.Replace('/', '_') + "-" + (build.Toolkit ?? "cpu"));

    public async Task<RuntimeSmokeResult> SmokeAsync(RuntimeBuildId build, CancellationToken ct)
    {
        string root = DirectoryOf(build);
        if (!ComfyRuntimeStore.IsPortableRoot(root)) return new RuntimeSmokeResult(false, "failed", "the staged build is incomplete");
        if (ComfyBuildPolicy.ReadTorchCuda(root) is null)
            return new RuntimeSmokeResult(false, "failed", "the staged build's PyTorch has no CUDA support");
        if (IsGamingActive()) throw new RuntimeUpdateDeferredException("Gaming Mode is on");
        if (IsGenerationRunning()) throw new RuntimeUpdateDeferredException("a media job is running; testing waits so the test is fair");
        try
        {
            ComfyCalibrationHarness harness = HarnessFor(build, DataRootFor(build));
            ComfyEngineSnapshot snap = await harness.InspectAsync(ct).ConfigureAwait(false);
            if (snap.Footprint.DiffusionBytes <= 0)
                return new RuntimeSmokeResult(true, "binary_only", "no media model is installed, so only startup of ComfyUI and CUDA PyTorch was verified");
            IReadOnlyList<string> args = ComfyLaunchPlanner.Legacy(snap.Caps).Arguments;
            ComfyCandidateRun run = await harness.RunAsync(args, 0, ct).ConfigureAwait(false);
            long bytes = new FileInfo(run.Samples[0].ImagePath).Length;
            return bytes > 20_000
                ? new RuntimeSmokeResult(true, "served", $"rendered a {snap.ModelLabel} test image in {run.Samples[0].Seconds:0.0} s")
                : new RuntimeSmokeResult(false, "failed", "the test image is empty or blank");
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Gaming Mode", StringComparison.Ordinal))
        {
            throw new RuntimeUpdateDeferredException(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RuntimeSmokeResult(false, "failed", ex.Message);
        }
    }

    public async Task<RuntimeBenchResult?> BenchmarkAsync(RuntimeBuildId build, CancellationToken ct)
    {
        string key = build.Key;
        if (_benchCache.TryGetValue(key, out RuntimeBenchResult? cached)) return cached;
        if (IsGamingActive()) throw new RuntimeUpdateDeferredException("Gaming Mode is on");
        if (IsGenerationRunning()) throw new RuntimeUpdateDeferredException("a media job is running; testing waits so the test is fair");
        ComfyCalibrationHarness harness = HarnessFor(build, DataRootFor(build));
        ComfyEngineSnapshot snap = await harness.InspectAsync(ct).ConfigureAwait(false);
        if (snap.Footprint.DiffusionBytes <= 0 || !snap.Env.TorchHasCuda) return null;
        ComfyCandidateRun run = await harness.RunAsync(ComfyLaunchPlanner.Legacy(snap.Caps).Arguments, 2, ct).ConfigureAwait(false);
        double cold = run.Samples[0].Seconds;
        double warm = run.Samples.Skip(1).Select(s => s.Seconds).OrderBy(s => s).ElementAt((run.Samples.Count - 1) / 2);
        var result = new RuntimeBenchResult(1.0 / cold, 1.0 / warm, snap.ModelLabel, "img/s", "cold", "warm");
        _benchCache[key] = result;
        try
        {
            _history.Append(new LocalBenchmarkEntry
            {
                HardwareDigest = snap.HardwareDigest, ModelId = snap.ModelId, ModelLabel = snap.ModelLabel,
                RuntimeBackend = "media-comfyui", RuntimeBuild = "comfyui " + snap.ComfyVersion + " / torch " + snap.TorchVersion,
                LaunchPlan = "runtime-update-gate", Workload = "runtime-update:" + ComfyMediaOptimizer.WorkloadId,
                LoadMs = cold * 1000.0, WarmMs = warm * 1000.0, PeakMemoryBytes = run.PeakVramBytes
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        return result;
    }

    public void Activate(RuntimeBuildId build)
    {
        if (IsGenerationRunning()) throw new InvalidOperationException("A media job is running; ComfyUI was not switched.");
        ComfyBuildIdentity identity = Resolve(build) ?? throw new InvalidOperationException("That ComfyUI build is not installed.");
        if (_process.GetMediaProcessId() != null && !_process.StopMedia())
            throw new InvalidOperationException("ComfyUI did not stop, so it was not switched.");
        if (!_store.TryActivateInstalled(identity)) throw new InvalidOperationException("That ComfyUI build could not be activated.");
    }

    public bool Remove(RuntimeBuildId build)
    {
        ComfyBuildIdentity? identity = Resolve(build);
        // The pre-managed original is never deleted automatically: it may be the only copy and other tools may share it.
        return identity is not null && identity.Tag != "legacy" && _store.RemoveStored(identity);
    }

    public string DirectoryOf(RuntimeBuildId build) =>
        Resolve(build) is { } identity ? _store.PathOf(identity) : Path.Combine(LocalAiPaths.RuntimeRoot, "comfyui-builds", "missing-" + build.Tag);

    public void CleanupStaging()
    {
        _benchCache.Clear();
        try
        {
            if (!Directory.Exists(LocalAiPaths.RuntimeRoot)) return;
            if (_installer.ListJobs().Any(j => j.Stage is "queued" or "downloading" or "verifying" or "extracting" or "swapping")) return;
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            foreach (string entry in Directory.EnumerateFileSystemEntries(LocalAiPaths.RuntimeRoot, ".comfy-staging-*"))
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
}
