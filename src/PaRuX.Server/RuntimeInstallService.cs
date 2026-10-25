using System.Collections.Concurrent;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Runtime Manager install/update/rollback lifecycle. llama.cpp text builds are kept side-by-side
/// by tag. A newly installed text build is not assumed faster because it is newer: when an older
/// managed baseline and a representative GGUF exist, RuntimeUpdateRegressionGuard measures both
/// and decides which tag remains active. ComfyUI keeps its separate versioned swap lifecycle.
/// </summary>
public sealed class RuntimeInstallService
{
    private const string LlamaOwner = "ggml-org";
    private const string LlamaRepo = "llama.cpp";
    private const string MediaBackend = "media-comfyui";
    private const int ReleasePageSize = 100;
    private const int MaximumReleasePages = 8;
    private const int MaximumListedBuilds = 15;

    private sealed class Entry
    {
        public readonly object Gate = new();
        public readonly RuntimeInstallStatus Status;
        public CancellationTokenSource Cancellation;
        /// <summary>Called after every stage change so the job file records it. Invoked outside the entry lock.</summary>
        public Action? Changed;
        /// <summary>Stage-only installs: the CUDA toolkit folder the build was staged under.</summary>
        public string? StagedToolkit;
        public Entry(RuntimeInstallStatus status, Func<CancellationTokenSource> cancellationTokenSourceFactory)
        {
            Status = status;
            Cancellation = cancellationTokenSourceFactory();
        }
    }

    private readonly LocalAiProcessManager _process;
    private readonly HttpClient _http;
    private readonly GpuInventoryService _gpu;
    private readonly ComfyRuntimeStore _comfyStore;
    private readonly RuntimeUpdateRegressionGuard _regressionGuard;
    private readonly Func<Uri, bool>? _isTrustedDownloadUri;
    private readonly Action<IReadOnlyList<RuntimeInstallStatus>>? _saveJobSnapshot;
    private readonly Func<CancellationTokenSource> _cancellationTokenSourceFactory;
    private readonly ConcurrentDictionary<string, Entry> _jobs = new();
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly object _persistGate = new();

    public RuntimeInstallService(LocalAiProcessManager process, HttpClient? httpClient = null)
        : this(process, httpClient, new GpuInventoryService(), new ComfyRuntimeStore(), null) { }

    internal RuntimeInstallService(
        LocalAiProcessManager process,
        HttpClient? httpClient,
        GpuInventoryService gpuInventory,
        ComfyRuntimeStore comfyStore,
        RuntimeUpdateRegressionGuard? regressionGuard = null,
        Func<Uri, bool>? isTrustedDownloadUri = null,
        Action<IReadOnlyList<RuntimeInstallStatus>>? saveJobSnapshot = null,
        Func<CancellationTokenSource>? cancellationTokenSourceFactory = null)
    {
        _process = process;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _gpu = gpuInventory;
        _comfyStore = comfyStore;
        _regressionGuard = regressionGuard ?? new RuntimeUpdateRegressionGuard(process);
        _isTrustedDownloadUri = isTrustedDownloadUri;
        _saveJobSnapshot = saveJobSnapshot;
        _cancellationTokenSourceFactory = cancellationTokenSourceFactory ?? (() => new CancellationTokenSource());
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PaRuX-RuntimeManager/1.0");
        LoadPersistedJobs();
    }

    public Task<List<RuntimeBuildOption>> ListAvailableAsync(string backend, CancellationToken ct) =>
        ListAvailableAsync(backend, null, ct);

    /// <summary>cudaMajorOverride lets the managed auto-updater (which owns the driver/compute-capability decision)
    /// pick the CUDA major instead of the installer's own hardware read.</summary>
    internal async Task<List<RuntimeBuildOption>> ListAvailableAsync(string backend, int? cudaMajorOverride, CancellationToken ct)
    {
        ValidateBackend(backend);
        if (backend == MediaBackend) return await ListComfyAvailableAsync(ct);

        string activeTag = LocalAiPaths.GetActiveRuntimeBuildTag(backend) ?? LocalAiPaths.DefaultRuntimeBuildTag(backend);
        List<string> installed = LocalAiPaths.ListInstalledRuntimeBuildTags(backend);
        var options = new List<RuntimeBuildOption>();
        int cudaMajor = backend == "text-cuda" ? cudaMajorOverride ?? await PreferredCudaMajorAsync(ct) : 12;
        for (int page = 1; page <= MaximumReleasePages && options.Count < MaximumListedBuilds; page++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{LlamaOwner}/{LlamaRepo}/releases?per_page={ReleasePageSize}&page={page}");
            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            // ggml-org marks every llama.cpp build as a prerelease; skipping them left the catalog empty.
            List<RuntimeReleaseInfo> releases = RuntimeBuildCatalog.ParseReleases(json, includePrereleases: true);
            int responseCount;
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                responseCount = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement.GetArrayLength()
                    : 0;
            }

            foreach (RuntimeReleaseInfo release in releases)
            {
                RuntimeReleaseAsset? asset = RuntimeBuildCatalog.SelectAsset(
                    backend, release.Assets, RuntimeBuildCatalog.CurrentPlatform(), cudaMajor);
                if (asset == null) continue;
                try
                {
                    RuntimeArtifactSecurityPolicy.ValidateAsset(asset, LlamaOwner, LlamaRepo);
                    ValidateReleaseAssetTag(asset, release.Tag);
                }
                catch (InvalidDataException) { continue; }
                RuntimeReleaseAsset? cudaDependencies = backend == "text-cuda"
                    ? RuntimeBuildCatalog.SelectCudaDependencyAsset(asset, release.Assets, RuntimeBuildCatalog.CurrentPlatform())
                    : null;
                if (backend == "text-cuda"
                    && cudaDependencies == null
                    && RuntimeBuildCatalog.HasCudaDependencyAssetForPlatform(release.Assets, RuntimeBuildCatalog.CurrentPlatform()))
                    continue;
                if (cudaDependencies != null)
                {
                    try
                    {
                        RuntimeArtifactSecurityPolicy.ValidateAsset(cudaDependencies, LlamaOwner, LlamaRepo);
                        ValidateReleaseAssetTag(cudaDependencies, release.Tag);
                    }
                    catch (InvalidDataException) { continue; }
                }
                options.Add(new RuntimeBuildOption
                {
                    Backend = backend,
                    Tag = release.Tag,
                    PublishedUtc = release.PublishedUtc,
                    AssetSizeBytes = asset.SizeBytes + (cudaDependencies?.SizeBytes ?? 0),
                    InstalledLocally = installed.Contains(release.Tag),
                    IsActive = string.Equals(activeTag, release.Tag, StringComparison.Ordinal),
                    CudaToolkit = backend == "text-cuda" ? LocalAiPaths.ParseCudaToolkit(asset.Name) : null
                });
                if (options.Count >= MaximumListedBuilds) break;
            }

            if (responseCount < ReleasePageSize) break;
        }
        return options;
    }

    /// <summary>CUDA 13 for a Blackwell NVIDIA GPU on a driver that supports it, otherwise the broadly compatible
    /// CUDA 12 build. Any failure to read the hardware keeps 12.</summary>
    private async Task<int> PreferredCudaMajorAsync(CancellationToken ct)
    {
        try
        {
            GpuAdapterStatus? nvidia = _gpu.GetStatus().Adapters
                .Where(a => a.VendorId == RuntimeBackendAdvisor.NvidiaVendor && !a.IsSoftware)
                .OrderByDescending(a => a.DedicatedVideoMemoryBytes).FirstOrDefault();
            if (nvidia == null) return 12;
            List<GpuPerformanceTelemetryDto> smi = await new GpuPerformanceTelemetryService().QueryAsync(ct);
            double? cc = smi.FirstOrDefault(d => d.ComputeCapability is not null
                && nvidia.Name.Contains(d.Name, StringComparison.OrdinalIgnoreCase))?.ComputeCapability;
            return RuntimeBuildCatalog.PreferredCudaMajor(cc, nvidia.DriverVersion);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return 12; }
    }

    private async Task<List<RuntimeBuildOption>> ListComfyAvailableAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{ComfyBuildCatalog.Owner}/{ComfyBuildCatalog.Repo}/releases?per_page=15");
        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        List<RuntimeReleaseInfo> releases = RuntimeBuildCatalog.ParseReleases(json);
        GpuInventoryStatus gpu = _gpu.GetStatus();
        IReadOnlyList<ComfyBuildIdentity> installed = _comfyStore.ListInstalled();
        ComfyBuildIdentity? active = _comfyStore.GetActiveIdentity();

        var options = new List<RuntimeBuildOption>();
        foreach (RuntimeReleaseInfo release in releases)
        {
            foreach (ComfyBuildCatalog.Selection selection in ComfyBuildCatalog.ListSelections(release.Assets, gpu))
            {
                try { RuntimeArtifactSecurityPolicy.ValidateAsset(selection.Asset, ComfyBuildCatalog.Owner, ComfyBuildCatalog.Repo); }
                catch (InvalidDataException) { continue; }
                var identity = new ComfyBuildIdentity(release.Tag, selection.Variant);
                options.Add(new RuntimeBuildOption
                {
                    Backend = MediaBackend,
                    Tag = release.Tag,
                    Variant = selection.Variant,
                    PublishedUtc = release.PublishedUtc,
                    AssetSizeBytes = selection.Asset.SizeBytes,
                    InstalledLocally = installed.Contains(identity),
                    IsActive = active == identity,
                    Recommended = selection.Recommended
                });
            }
        }
        return options;
    }

    public List<RuntimeBuildOption> ListInstalled(string backend)
    {
        ValidateBackend(backend);
        if (backend == MediaBackend)
        {
            ComfyBuildIdentity? active = _comfyStore.GetActiveIdentity();
            return _comfyStore.ListInstalled()
                .Select(identity => new RuntimeBuildOption
                {
                    Backend = MediaBackend,
                    Tag = identity.Tag,
                    Variant = identity.Variant,
                    InstalledLocally = true,
                    IsActive = active == identity
                })
                .ToList();
        }

        string activeTag = LocalAiPaths.GetActiveRuntimeBuildTag(backend) ?? LocalAiPaths.DefaultRuntimeBuildTag(backend);
        return LocalAiPaths.ListInstalledRuntimeBuildTags(backend)
            .Select(tag => new RuntimeBuildOption
            {
                Backend = backend,
                Tag = tag,
                InstalledLocally = true,
                IsActive = string.Equals(tag, activeTag, StringComparison.Ordinal)
            })
            .ToList();
    }

    public void Rollback(RuntimeRollbackRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Backend) || string.IsNullOrWhiteSpace(request.Tag))
            throw new InvalidDataException("backend and tag are required.");
        ValidateBackend(request.Backend);
        ValidateReleaseTag(request.Tag);
        EnsureBackendIdle(request.Backend);

        if (request.Backend == MediaBackend)
        {
            ComfyBuildIdentity target = _comfyStore.ResolveInstalled(request.Tag, request.Variant);
            _comfyStore.Rollback(target);
            MarkComfyManagedBestEffort();
            return;
        }

        RejectTextVariant(request.Variant);
        LocalAiPaths.SetActiveRuntimeBuildTag(request.Backend, request.Tag,
            request.Backend == "text-cuda" ? request.CudaToolkit : null);
    }

    public void ResetToDefault(string backend)
    {
        ValidateBackend(backend);
        EnsureBackendIdle(backend);
        if (backend == MediaBackend)
            throw new InvalidDataException(
                "media-comfyui has no fixed upstream default tag. Use rollback with tag \"legacy\" to restore the pre-managed portable build when it is available.");
        LocalAiPaths.ClearActiveRuntimeBuildTag(backend);
    }

    private void EnsureBackendIdle(string backend)
    {
        if (backend == MediaBackend)
        {
            if (_process.GetMediaProcessId() != null)
                throw new InvalidOperationException(
                    "ComfyUI is currently running. Stop media generation first before changing the active ComfyUI build.");
            return;
        }

        if (_process.GetTextProcessId() != null)
            throw new InvalidOperationException(
                "A text model is currently loaded. Unload it first (POST /api/v1/models/library/unload) before changing the active "
                + backend + " runtime build.");
    }

    public RuntimeInstallStatus CreateInstall(RuntimeInstallRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Backend) || string.IsNullOrWhiteSpace(request.Tag))
            throw new InvalidDataException("backend and tag are required.");
        ValidateBackend(request.Backend);
        ValidateReleaseTag(request.Tag);
        EnsureBackendIdle(request.Backend);

        string? normalizedVariant = null;
        if (request.Backend == MediaBackend && !string.IsNullOrWhiteSpace(request.Variant))
            normalizedVariant = ComfyBuildCatalog.NormalizeVariant(request.Variant)
                ?? throw new InvalidDataException("Unknown ComfyUI portable variant \"" + request.Variant + "\".");
        else if (request.Backend != MediaBackend)
            RejectTextVariant(request.Variant);

        var normalizedRequest = new RuntimeInstallRequest
        {
            Backend = request.Backend,
            Tag = request.Tag,
            Variant = normalizedVariant
        };
        var status = new RuntimeInstallStatus
        {
            Id = Guid.NewGuid().ToString("N"),
            Backend = normalizedRequest.Backend,
            Tag = normalizedRequest.Tag,
            Variant = normalizedRequest.Variant,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        var entry = new Entry(status, _cancellationTokenSourceFactory) { Changed = PersistJobs };

        // A new managed-runtime transfer must be recoverable before it is allowed to start.
        // Serialize admission with snapshot persistence so concurrent starts cannot create
        // an unrecorded transfer or discard another job's durable state.
        lock (_persistGate)
        {
            _jobs[status.Id] = entry;
            bool persisted = false;
            try { persisted = TryPersistJobs(); }
            finally
            {
                if (!persisted)
                {
                    _jobs.TryRemove(status.Id, out _);
                    entry.Cancellation.Dispose();
                }
            }
            if (!persisted)
                throw new IOException("The runtime install could not start because its job state could not be saved.");
        }

        _ = Task.Run(() => RunInstallAsync(entry, normalizedRequest));
        return Snapshot(entry);
    }

    /// <summary>
    /// Writes every known job to the job file. New job admission fails closed if its first
    /// snapshot cannot be persisted. Later status updates remain best-effort and observable;
    /// a transient write error does not destroy a running install.
    /// </summary>
    private void PersistJobs() => _ = TryPersistJobs();

    private bool TryPersistJobs()
    {
        lock (_persistGate)
        {
            // Taken under the gate: a snapshot read before it can be written after a newer one and leave a stale stage on disk.
            IReadOnlyList<RuntimeInstallStatus> snapshot = _jobs.Values.Select(Snapshot).ToList();
            try
            {
                if (_saveJobSnapshot is null)
                    RuntimeInstallJobStore.Save(LocalAiPaths.RuntimeInstallJobsFile, snapshot);
                else
                    _saveJobSnapshot(snapshot);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Runtime install job state could not be saved: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>Restores the jobs an earlier Core saved. A job that had not reached a final stage is marked interrupted, and the
    /// corrected state is saved again so the next start reads the same thing.</summary>
    private void LoadPersistedJobs()
    {
        bool changed = false;
        foreach (RuntimeInstallStatus job in RuntimeInstallJobStore.Load(LocalAiPaths.RuntimeInstallJobsFile))
        {
            changed |= RuntimeInstallJobStore.MarkInterruptedIfUnfinished(job);
            _jobs[job.Id] = new Entry(job, _cancellationTokenSourceFactory) { Changed = PersistJobs };
        }
        if (changed) PersistJobs();
    }

    public IReadOnlyList<RuntimeInstallStatus> ListJobs() =>
        _jobs.Values.Select(Snapshot).OrderByDescending(status => status.CreatedUtc).ToList();

    public RuntimeInstallStatus? GetJob(string id) =>
        _jobs.TryGetValue(id, out Entry? entry) ? Snapshot(entry) : null;

    public bool CancelJob(string id)
    {
        if (!_jobs.TryGetValue(id, out Entry? entry)) return false;
        lock (entry.Gate)
        {
            // An interrupted job has no running install behind it, so there is nothing for a cancel to stop.
            if (entry.Status.Stage is "completed" or "failed" or "cancelled" or RuntimeInstallJobStore.InterruptedStage) return false;
        }
        entry.Cancellation.Cancel();
        return true;
    }

    /// <summary>Restarts an interrupted job through the regular install pipeline. Keeping its ID preserves the archive's
    /// .partial path, so the download can continue from the verified byte-range boundary.</summary>
    public RuntimeInstallStatus? ResumeJob(string id)
    {
        if (!_jobs.TryGetValue(id, out Entry? entry)) return null;

        RuntimeInstallRequest request;
        RuntimeInstallStatus previousStatus;
        CancellationTokenSource replacedCancellation;
        lock (entry.Gate)
        {
            if (entry.Status.Stage != RuntimeInstallJobStore.InterruptedStage)
                throw new InvalidOperationException("Only an interrupted install job can be resumed.");

            string backend = entry.Status.Backend;
            string tag = entry.Status.Tag;
            string? variant = entry.Status.Variant;
            ValidateBackend(backend);
            ValidateReleaseTag(tag);
            if (backend == MediaBackend && !string.IsNullOrWhiteSpace(variant))
                variant = ComfyBuildCatalog.NormalizeVariant(variant)
                    ?? throw new InvalidDataException("Unknown ComfyUI portable variant \"" + variant + "\".");
            else if (backend != MediaBackend)
                RejectTextVariant(variant);
            EnsureBackendIdle(backend);

            previousStatus = Snapshot(entry);
            request = new RuntimeInstallRequest { Backend = backend, Tag = tag, Variant = variant };
            replacedCancellation = entry.Cancellation;
            entry.Cancellation = _cancellationTokenSourceFactory();
            entry.Status.Variant = variant;
            entry.Status.Stage = "queued";
            entry.Status.BytesDownloaded = 0;
            entry.Status.TotalBytes = 0;
            entry.Status.Error = null;
            entry.Status.ActiveTag = null;
            entry.Status.PerformanceDecision = null;
            entry.Status.PerformanceMessage = null;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
        replacedCancellation.Dispose();

        // Restore the interrupted status for any save failure, not only the I/O ones TryPersistJobs
        // reports as false: a status left at "queued" would never be scheduled and never reach disk.
        bool persisted = false;
        try
        {
            persisted = TryPersistJobs();
        }
        finally
        {
            if (!persisted)
            {
                lock (entry.Gate)
                {
                    RestoreStatus(entry.Status, previousStatus);
                }
            }
        }
        if (!persisted)
            throw new IOException("The interrupted runtime install could not resume because its queued state could not be saved.");
        RuntimeInstallStatus resumed = Snapshot(entry);
        _ = Task.Run(() => RunInstallAsync(entry, request));
        return resumed;
    }

    private async Task RunInstallAsync(Entry entry, RuntimeInstallRequest request)
    {
        bool entered = false;
        try
        {
            await _installGate.WaitAsync(entry.Cancellation.Token);
            entered = true;
            EnsureBackendIdle(request.Backend);
            if (request.Backend == MediaBackend)
                await RunComfyInstallAsync(entry, request);
            else
                await RunTextInstallAsync(entry, request);
        }
        catch (OperationCanceledException) { Cancel(entry); }
        catch (Exception ex) { Fail(entry, ex); }
        finally
        {
            if (entered) _installGate.Release();
        }
    }

    /// <summary>
    /// Downloads, hash-verifies and extracts a llama.cpp build into its own runtime folder WITHOUT touching the
    /// active pointer, the CUDA toolkit hint or any running process, and without the regression guard (the managed
    /// auto-updater runs its own smoke/benchmark gate). Returns the toolkit folder it was staged under (CUDA only).
    /// Throws on failure; a failed stage never leaves a half-installed runtime folder behind.
    /// </summary>
    internal async Task<string?> StageTextBuildAsync(string backend, string tag, int? cudaMajor, CancellationToken ct)
    {
        ValidateBackend(backend);
        if (backend == MediaBackend) throw new InvalidDataException("Only llama.cpp text runtimes can be staged.");
        ValidateReleaseTag(tag);
        var status = new RuntimeInstallStatus
        {
            Id = Guid.NewGuid().ToString("N"), Backend = backend, Tag = tag,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        var entry = new Entry(status, () => CancellationTokenSource.CreateLinkedTokenSource(ct));
        try
        {
            await _installGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RunTextInstallAsync(entry, new RuntimeInstallRequest { Backend = backend, Tag = tag },
                    stageOnly: true, cudaMajorOverride: cudaMajor).ConfigureAwait(false);
            }
            finally { _installGate.Release(); }
        }
        finally { entry.Cancellation.Dispose(); }
        ct.ThrowIfCancellationRequested();
        if (entry.Status.Stage != "completed")
            throw new InvalidOperationException(entry.Status.Error ?? "The runtime build could not be staged.");
        return entry.StagedToolkit;
    }

    /// <summary>Stage-only counterpart of the interactive ComfyUI install, for the automatic updater: download, GitHub SHA-256 check,
    /// safe extraction, then the build is placed in the store and NOT activated.</summary>
    internal async Task StageComfyBuildAsync(string tag, string variant, CancellationToken ct)
    {
        ValidateReleaseTag(tag);
        var status = new RuntimeInstallStatus
        {
            Id = Guid.NewGuid().ToString("N"), Backend = MediaBackend, Tag = tag, Variant = variant,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        var entry = new Entry(status, () => CancellationTokenSource.CreateLinkedTokenSource(ct));
        try
        {
            await _installGate.WaitAsync(ct).ConfigureAwait(false);
            try { await RunComfyInstallAsync(entry, new RuntimeInstallRequest { Backend = MediaBackend, Tag = tag, Variant = variant }, stageOnly: true).ConfigureAwait(false); }
            finally { _installGate.Release(); }
        }
        finally { entry.Cancellation.Dispose(); }
        ct.ThrowIfCancellationRequested();
        if (entry.Status.Stage != "completed")
            throw new InvalidOperationException(entry.Status.Error ?? "The ComfyUI build could not be staged.");
    }

    private async Task RunTextInstallAsync(Entry entry, RuntimeInstallRequest request,
        bool stageOnly = false, int? cudaMajorOverride = null)
    {
        string originalTag = LocalAiPaths.GetActiveRuntimeBuildTag(request.Backend)
            ?? LocalAiPaths.DefaultRuntimeBuildTag(request.Backend);
        bool isCuda = request.Backend == "text-cuda";
        string? originalToolkit = isCuda ? LocalAiPaths.ResolveCudaToolkit(originalTag) : null;

        if (!stageOnly && LocalAiPaths.ListInstalledRuntimeBuildTags(request.Backend).Contains(request.Tag))
        {
            try
            {
                EnsureBackendIdle(request.Backend);
                SetStage(entry, "performance-check");
                RuntimeRegressionEvidenceDto evidence = await _regressionGuard.EvaluateAsync(
                    request.Backend, originalTag, request.Tag, entry.Cancellation.Token);
                ApplyPerformanceDecision(entry, evidence);
                Complete(entry);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Fail(entry, ex); }
            return;
        }

        string stagingDir = Path.Combine(LocalAiPaths.RuntimeRoot, ".staging-" + entry.Status.Id);
        string zipPath = stagingDir + ".zip";
        string cudaDependenciesZipPath = stagingDir + ".cudart.zip";
        try
        {
            SetStage(entry, "downloading");
            RuntimeReleaseInfo release = await GetReleaseByTagAsync(
                LlamaOwner, LlamaRepo, request.Tag, entry.Cancellation.Token);
            RuntimeReleaseAsset? asset = RuntimeBuildCatalog.SelectAsset(
                request.Backend, release.Assets, RuntimeBuildCatalog.CurrentPlatform(),
                request.Backend == "text-cuda" ? cudaMajorOverride ?? await PreferredCudaMajorAsync(entry.Cancellation.Token) : 12);
            if (asset == null)
                throw new InvalidOperationException(
                    "No " + RuntimeBuildCatalog.Describe(RuntimeBuildCatalog.CurrentPlatform())
                    + " build asset found for tag \"" + request.Tag + "\" and backend \""
                    + request.Backend + "\". That release published: "
                    + (release.Assets.Count == 0
                        ? "nothing"
                        : string.Join(", ", release.Assets.Select(a => a.Name)))
                    + ".");
            RuntimeArtifactSecurityPolicy.ValidateAsset(asset, LlamaOwner, LlamaRepo, _isTrustedDownloadUri);
            ValidateReleaseAssetTag(asset, release.Tag);
            RuntimeReleaseAsset? cudaDependencies = request.Backend == "text-cuda"
                ? RuntimeBuildCatalog.SelectCudaDependencyAsset(asset, release.Assets, RuntimeBuildCatalog.CurrentPlatform())
                : null;
            if (request.Backend == "text-cuda"
                && cudaDependencies == null
                && RuntimeBuildCatalog.HasCudaDependencyAssetForPlatform(release.Assets, RuntimeBuildCatalog.CurrentPlatform()))
                throw new InvalidDataException(
                    "The release advertises CUDA dependency bundles for this platform, but none matches the selected server CUDA version.");
            if (cudaDependencies != null)
            {
                RuntimeArtifactSecurityPolicy.ValidateAsset(cudaDependencies, LlamaOwner, LlamaRepo, _isTrustedDownloadUri);
                ValidateReleaseAssetTag(cudaDependencies, release.Tag);
            }

            Directory.CreateDirectory(LocalAiPaths.RuntimeRoot);
            long totalDownloadBytes = checked(asset.SizeBytes + (cudaDependencies?.SizeBytes ?? 0));
            await DownloadAssetAsync(asset, zipPath, entry, entry.Cancellation.Token, totalBytes: totalDownloadBytes);
            VerifyAdvertisedDigest(zipPath, asset, entry);
            if (cudaDependencies != null)
            {
                SetStage(entry, "downloading");
                await DownloadAssetAsync(
                    cudaDependencies, cudaDependenciesZipPath, entry, entry.Cancellation.Token,
                    bytesOffset: asset.SizeBytes, totalBytes: totalDownloadBytes);
                VerifyAdvertisedDigest(cudaDependenciesZipPath, cudaDependencies, entry);
            }

            SetStage(entry, "extracting");
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
            await SafeArchiveExtractor.ExtractAsync(zipPath, stagingDir, entry.Cancellation.Token);

            string exePath = FindLlamaServerExe(stagingDir)
                ?? throw new InvalidOperationException(
                    "The downloaded archive for tag \"" + request.Tag + "\" does not contain "
                    + LocalAiPaths.LlamaServerExecutable + ".");
            string exeDir = Path.GetDirectoryName(exePath)!;
            if (cudaDependencies != null)
                await SafeArchiveExtractor.ExtractAsync(cudaDependenciesZipPath, exeDir, entry.Cancellation.Token);
            MakeExecutable(exeDir);

            if (!stageOnly) EnsureBackendIdle(request.Backend);
            SetStage(entry, "swapping");
            string? installedToolkit = isCuda
                ? LocalAiPaths.ParseCudaToolkit(asset.Name) ?? LocalAiPaths.DefaultCudaToolkit
                : null;
            string finalDir = Path.Combine(
                LocalAiPaths.RuntimeRoot,
                LocalAiPaths.RuntimeFolderName(request.Backend, request.Tag, installedToolkit));
            if (stageOnly && File.Exists(Path.Combine(finalDir, LocalAiPaths.LlamaServerExecutable)))
            {
                // Already installed: never replace a build that may be the active one.
                entry.StagedToolkit = installedToolkit;
                Complete(entry);
                return;
            }
            // The sidecar is written BEFORE the single directory rename, so a crash can never leave a folder that
            // contains llama-server but no ownership record.
            WriteRuntimeSidecar(exeDir, request, asset.Name, installedToolkit);
            if (Directory.Exists(finalDir)) Directory.Delete(finalDir, recursive: true);
            Directory.Move(exeDir, finalDir);
            entry.StagedToolkit = installedToolkit;
            if (stageOnly) { Complete(entry); return; }
            // Point the toolkit selector at the new folder so the regression guard (which activates by tag) lands on it.
            if (installedToolkit != null) LocalAiPaths.SetCudaToolkitHint(installedToolkit);

            // Do not blindly activate the downloaded tag. The guard either compares it against
            // the prior known-good build, activates it as a genuine first install, or keeps the
            // previous tag active with an explicit deferred/rejected reason.
            SetStage(entry, "performance-check");
            RuntimeRegressionEvidenceDto evidence = await _regressionGuard.EvaluateAsync(
                request.Backend, originalTag, request.Tag, entry.Cancellation.Token);
            ApplyPerformanceDecision(entry, evidence);
            Complete(entry);
        }
        catch (OperationCanceledException)
        {
            if (!stageOnly) { try { LocalAiPaths.SetActiveRuntimeBuildTag(request.Backend, originalTag, originalToolkit); } catch { } }
            Cancel(entry);
        }
        catch (Exception ex)
        {
            if (!stageOnly) { try { LocalAiPaths.SetActiveRuntimeBuildTag(request.Backend, originalTag, originalToolkit); } catch { } }
            Fail(entry, ex);
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteFile(cudaDependenciesZipPath);
            TryDeleteDirectory(stagingDir);
        }
    }

    private static void WriteRuntimeSidecar(string folder, RuntimeInstallRequest request, string assetName, string? cudaToolkit)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(folder, LocalAiPaths.RuntimeSidecarFileName),
                JsonSerializer.Serialize(new { backend = request.Backend, tag = request.Tag, asset = assetName, cudaToolkit, ownership = "PARUX_MANAGED" }));
        }
        catch { /* metadata only; the folder name already carries tag and toolkit */ }
    }

    private async Task RunComfyInstallAsync(Entry entry, RuntimeInstallRequest request, bool stageOnly = false)
    {
        string? requestedVariant = request.Variant;
        GpuInventoryStatus gpu = _gpu.GetStatus();
        string? preselected = requestedVariant ?? ComfyBuildCatalog.InferRecommendedVariant(gpu);
        if (stageOnly && preselected != null && _comfyStore.ListInstalled().Contains(new ComfyBuildIdentity(request.Tag, preselected)))
        {
            lock (entry.Gate) entry.Status.Variant = preselected;
            Complete(entry);
            return;
        }
        if (preselected != null && !stageOnly)
        {
            var alreadyInstalled = new ComfyBuildIdentity(request.Tag, preselected);
            try
            {
                EnsureBackendIdle(MediaBackend);
                if (_comfyStore.TryActivateInstalled(alreadyInstalled))
                {
                    lock (entry.Gate) entry.Status.Variant = preselected;
                    MarkComfyManagedBestEffort();
                    Complete(entry);
                    return;
                }
            }
            catch (Exception ex)
            {
                Fail(entry, ex);
                return;
            }
        }

        string parent = Path.GetDirectoryName(_comfyStore.ActiveRoot)
            ?? throw new InvalidOperationException("ComfyUI active runtime path has no parent directory.");
        string stagingDir = Path.Combine(parent, ".comfy-staging-" + entry.Status.Id);
        string archivePath = stagingDir + ".7z";

        try
        {
            SetStage(entry, "downloading");
            RuntimeReleaseInfo release = await GetReleaseByTagAsync(
                ComfyBuildCatalog.Owner, ComfyBuildCatalog.Repo, request.Tag, entry.Cancellation.Token);
            ComfyBuildCatalog.Selection selection = ComfyBuildCatalog.SelectForInstall(
                release.Assets, gpu, requestedVariant);
            RuntimeArtifactSecurityPolicy.ValidateAsset(selection.Asset, ComfyBuildCatalog.Owner, ComfyBuildCatalog.Repo);
            var identity = new ComfyBuildIdentity(request.Tag, selection.Variant);
            lock (entry.Gate) entry.Status.Variant = selection.Variant;

            if (!stageOnly)
            {
                EnsureBackendIdle(MediaBackend);
                if (_comfyStore.TryActivateInstalled(identity))
                {
                    MarkComfyManagedBestEffort();
                    Complete(entry);
                    return;
                }
            }

            Directory.CreateDirectory(parent);
            await DownloadAssetAsync(selection.Asset, archivePath, entry, entry.Cancellation.Token);
            VerifyAdvertisedDigest(archivePath, selection.Asset, entry);

            SetStage(entry, "extracting");
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
            await SafeArchiveExtractor.ExtractAsync(archivePath, stagingDir, entry.Cancellation.Token);
            string portableRoot = FindComfyPortableRoot(stagingDir)
                ?? throw new InvalidDataException(
                    "The downloaded ComfyUI portable archive does not contain ComfyUI/main.py and python_embeded/python.exe.");

            if (stageOnly)
            {
                // The automatic updater tests the staged build before anything is switched: the active runtime stays untouched.
                SetStage(entry, "staging");
                _comfyStore.StageExtracted(portableRoot, identity);
                Complete(entry);
                return;
            }
            EnsureBackendIdle(MediaBackend);
            SetStage(entry, "swapping");
            _comfyStore.ActivateExtracted(portableRoot, identity);
            MarkComfyManagedBestEffort();
            Complete(entry);
        }
        catch (OperationCanceledException) { Cancel(entry); }
        catch (Exception ex) { Fail(entry, ex); }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingDir);
        }
    }

    private async Task<RuntimeReleaseInfo> GetReleaseByTagAsync(
        string owner, string repo, string tag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(tag)}");
        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        RuntimeReleaseInfo release = RuntimeBuildCatalog.ParseReleases("[" + json + "]", includePrereleases: true).FirstOrDefault()
            ?? throw new InvalidDataException("Upstream release response did not contain a usable release tag.");
        RuntimeArtifactSecurityPolicy.ValidateReleaseIdentity(release, tag);
        return release;
    }

    private static void ValidateReleaseAssetTag(RuntimeReleaseAsset asset, string tag)
    {
        Uri uri = new(asset.DownloadUrl, UriKind.Absolute);
        string expectedPrefix = $"/{LlamaOwner}/{LlamaRepo}/releases/download/{Uri.EscapeDataString(tag)}/";
        if (!uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime release asset URL does not match the requested release tag.");
    }

    private async Task DownloadAssetAsync(
        RuntimeReleaseAsset asset,
        string targetPath,
        Entry entry,
        CancellationToken ct,
        long bytesOffset = 0,
        long? totalBytes = null)
    {
        // The bytes stream into a partial file beside the target, so an interrupted download keeps what it already has. The target only
        // appears once every byte is present, and the digest is checked on it before anything is extracted, resumed or not.
        string partialPath = targetPath + RuntimeDownloadResume.PartialSuffix;
        long partialBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        RuntimeDownloadResume.Plan plan = RuntimeDownloadResume.PlanFor(partialBytes, asset.SizeBytes);
        if (!plan.Resumes) TryDeleteFile(partialPath);

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        if (plan.Resumes) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(plan.RangeFrom, null);
        using HttpResponseMessage download = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        download.EnsureSuccessStatusCode();
        // A server that ignores the range answers with the whole file, so the partial file is started again instead of appended to.
        long startAt = download.StatusCode == System.Net.HttpStatusCode.PartialContent ? plan.RangeFrom : 0;
        RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(download, asset, startAt, _isTrustedDownloadUri);
        if (startAt == 0) TryDeleteFile(partialPath);
        lock (entry.Gate)
        {
            entry.Status.TotalBytes = totalBytes ?? asset.SizeBytes;
            entry.Status.BytesDownloaded = bytesOffset + startAt;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }

        await using Stream source = await download.Content.ReadAsStreamAsync(ct);
        long downloaded = startAt;
        await using (var target = new FileStream(
            partialPath,
            startAt > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                RuntimeArtifactSecurityPolicy.ThrowIfDownloadWouldExceedBounds(downloaded, read, asset);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                lock (entry.Gate)
                {
                    entry.Status.BytesDownloaded = checked(bytesOffset + downloaded);
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
            }
            await target.FlushAsync(ct);
        }
        // A short or long stream leaves the partial file in place: the next attempt continues it, or starts again if it is no longer a prefix.
        RuntimeArtifactSecurityPolicy.ValidateDownloadedByteCount(downloaded, asset);
        File.Move(partialPath, targetPath, overwrite: true);
    }

    private static void VerifyAdvertisedDigest(string archivePath, RuntimeReleaseAsset asset, Entry entry)
    {
        SetStage(entry, "verifying");
        RuntimeAssetIntegrity.VerificationResult verification = RuntimeAssetIntegrity.VerifySha256(archivePath, asset.Digest);
        if (verification != RuntimeAssetIntegrity.VerificationResult.Verified)
        {
            // A file that fails its digest is never resumed from: the next attempt downloads the archive again from the start.
            TryDeleteFile(archivePath);
            throw new InvalidDataException(
                "The downloaded runtime archive failed its required GitHub SHA-256 integrity check; it was not extracted or activated.");
        }
    }

    internal static string? FindComfyPortableRoot(string root)
    {
        if (ComfyRuntimeStore.IsPortableRoot(root)) return root;
        if (!Directory.Exists(root)) return null;
        foreach (string main in Directory.EnumerateFiles(root, "main.py", SearchOption.AllDirectories))
        {
            string? app = Path.GetDirectoryName(main);
            if (app == null || !string.Equals(Path.GetFileName(app), "ComfyUI", StringComparison.OrdinalIgnoreCase)) continue;
            string? portable = Path.GetDirectoryName(app);
            if (portable != null && ComfyRuntimeStore.IsPortableRoot(portable)) return portable;
        }
        return null;
    }

    private static string? FindLlamaServerExe(string root) =>
        Directory.EnumerateFiles(root, LocalAiPaths.LlamaServerExecutable, SearchOption.AllDirectories).FirstOrDefault();

    private static void ValidateBackend(string backend)
    {
        if (backend == MediaBackend) return;
        LocalAiPaths.RuntimeFolderSuffix(backend);
    }

    private static void ValidateReleaseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 160 || tag.Any(char.IsControl)
            || tag is "." or ".."
            || tag.IndexOfAny(new[] { '/', '\\', '<', '>', ':', '"', '|', '?', '*' }) >= 0)
            throw new InvalidDataException(
                "release tag is missing or contains characters that are unsafe for a local runtime folder.");
    }

    private static void RejectTextVariant(string? variant)
    {
        if (!string.IsNullOrWhiteSpace(variant))
            throw new InvalidDataException("variant is only supported for media-comfyui installs.");
    }

    private static void MarkComfyManagedBestEffort()
    {
        try
        {
            LocalAiPaths.ClearComponentLocation("comfyui");
            LocalAiPaths.ClearComponentSkipped("comfyui");
        }
        catch { }
    }

    private static void ApplyPerformanceDecision(Entry entry, RuntimeRegressionEvidenceDto evidence)
    {
        lock (entry.Gate)
        {
            entry.Status.ActiveTag = evidence.ActiveTag;
            entry.Status.PerformanceDecision = evidence.Decision;
            entry.Status.PerformanceMessage = evidence.Reason;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
    }

    private static void SetStage(Entry entry, string stage)
    {
        lock (entry.Gate)
        {
            entry.Status.Stage = stage;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
        entry.Changed?.Invoke();
    }

    private static void Complete(Entry entry) => SetStage(entry, "completed");
    private static void Cancel(Entry entry) => SetStage(entry, "cancelled");

    private static void Fail(Entry entry, Exception ex)
    {
        lock (entry.Gate)
        {
            entry.Status.Stage = "failed";
            entry.Status.Error = ex.Message;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
        entry.Changed?.Invoke();
    }

    /// <summary>
    /// Restores the executable bit on a freshly extracted runtime, on the platforms that have one.
    ///
    /// SafeArchiveExtractor writes every entry through a plain FileStream - deliberately, because
    /// that is what keeps path construction under PaRuX's control - and a plain FileStream has no
    /// notion of a Unix mode. On Windows that costs nothing. Off Windows it is the difference
    /// between an installed runtime and a directory of files that cannot be launched, and the error
    /// it produces ("Permission denied" from a binary that is visibly present) is one of the more
    /// confusing ones to debug.
    ///
    /// Only the binaries PaRuX itself launches are marked, not every extracted file: an archive is
    /// not a reason to make someone's data files executable.
    /// </summary>
    private static void MakeExecutable(string runtimeDirectory)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (string name in new[] { LocalAiPaths.LlamaServerExecutable, LocalAiPaths.LlamaBenchExecutable })
        {
            string path = Path.Combine(runtimeDirectory, name);
            if (!File.Exists(path)) continue;
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception ex)
            {
                // Worth saying out loud rather than swallowing: the install continues, but the
                // runtime will refuse to start and the reason would otherwise be invisible.
                Console.Error.WriteLine("Could not mark " + path + " executable: " + ex.Message);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static RuntimeInstallStatus Snapshot(Entry entry)
    {
        lock (entry.Gate)
        {
            return new RuntimeInstallStatus
            {
                Id = entry.Status.Id,
                Backend = entry.Status.Backend,
                Tag = entry.Status.Tag,
                Variant = entry.Status.Variant,
                Stage = entry.Status.Stage,
                BytesDownloaded = entry.Status.BytesDownloaded,
                TotalBytes = entry.Status.TotalBytes,
                Error = entry.Status.Error,
                ActiveTag = entry.Status.ActiveTag,
                PerformanceDecision = entry.Status.PerformanceDecision,
                PerformanceMessage = entry.Status.PerformanceMessage,
                CreatedUtc = entry.Status.CreatedUtc,
                UpdatedUtc = entry.Status.UpdatedUtc
            };
        }
    }

    private static void RestoreStatus(RuntimeInstallStatus target, RuntimeInstallStatus snapshot)
    {
        target.Id = snapshot.Id;
        target.Backend = snapshot.Backend;
        target.Tag = snapshot.Tag;
        target.Variant = snapshot.Variant;
        target.Stage = snapshot.Stage;
        target.BytesDownloaded = snapshot.BytesDownloaded;
        target.TotalBytes = snapshot.TotalBytes;
        target.Error = snapshot.Error;
        target.ActiveTag = snapshot.ActiveTag;
        target.PerformanceDecision = snapshot.PerformanceDecision;
        target.PerformanceMessage = snapshot.PerformanceMessage;
        target.CreatedUtc = snapshot.CreatedUtc;
        target.UpdatedUtc = snapshot.UpdatedUtc;
    }

}
