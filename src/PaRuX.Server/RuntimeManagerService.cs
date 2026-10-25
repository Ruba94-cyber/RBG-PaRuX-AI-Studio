using System.Diagnostics;
using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

public sealed record HardwareFitEstimate(
    bool Compatible,
    double NeededMib,
    double AvailableMib,
    double? KvCacheMib = null,
    long? AssumedContextLength = null);

/// <summary>
/// Authoritative role/model load boundary. A healthy already-resident text model is a lock-free
/// reuse path so concurrent chats keep continuous batching. A real model/runtime mutation is an
/// exclusive GPU writer: existing readers/direct llama slots drain, new readers are held back,
/// alternative residents are stopped, state is rechecked under the writer, and only then is the
/// physical runtime changed. This prevents routing from stopping a model under an admitted stream
/// without turning every ordinary routed request into a serialized operation.
/// </summary>
public sealed class RuntimeManagerService
{
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    private static readonly TimeSpan TextTransitionDrainTimeout = TimeSpan.FromMinutes(10);
    private readonly LocalAiProcessManager _process;
    private readonly ISystemRamSnapshotProvider _ramSnapshotProvider;
    private readonly IGpuPerformanceTelemetrySnapshotProvider _gpuTelemetryProvider;

    private const double VramOverheadFactor = 1.20;
    private const double RuntimeReserveMib = 768;
    private const long DefaultLaunchContextLength = 32_768;
    private const string LaunchCacheType = "q8_0";

    public RuntimeManagerService(LocalAiProcessManager process)
        : this(process, new SystemRamSnapshotProvider(), CachedGpuPerformanceTelemetrySnapshotProvider.Shared)
    {
    }

    internal RuntimeManagerService(LocalAiProcessManager process, ISystemRamSnapshotProvider ramSnapshotProvider)
        : this(process, ramSnapshotProvider, CachedGpuPerformanceTelemetrySnapshotProvider.Shared)
    {
    }

    internal RuntimeManagerService(
        LocalAiProcessManager process,
        ISystemRamSnapshotProvider ramSnapshotProvider,
        IGpuPerformanceTelemetrySnapshotProvider gpuTelemetryProvider,
        Func<GpuInventoryStatus?>? inventoryProvider = null)
    {
        _process = process;
        _ramSnapshotProvider = ramSnapshotProvider;
        _gpuTelemetryProvider = gpuTelemetryProvider;
        _inventoryProvider = inventoryProvider;
    }

    private readonly Func<GpuInventoryStatus?>? _inventoryProvider;

    /// <summary>
    /// Live free dedicated VRAM for the status DTO. Null (unknown) unless the host has a discrete, eligible
    /// adapter whose free memory was actually measured; AMD/Intel telemetry that cannot be trusted is unknown,
    /// not 0, so a consumer never mistakes "no data" for "card full" (or the reverse).
    /// </summary>
    internal async Task<int?> ResolveVramFreeMibAsync(
        RuntimeManagerStatus status, long systemRamTotalBytes, CancellationToken ct)
    {
        GpuInventoryStatus? inventory = _inventoryProvider?.Invoke();
        RuntimeHardwareProfile hardware = inventory == null
            ? RuntimeHardwareProfileResolver.Resolve(status, systemRamTotalBytes)
            : RuntimeHardwareProfileResolver.Resolve(status, inventory, systemRamTotalBytes);
        if (!hardware.AcceleratorEligible
            || hardware.UsesSharedSystemMemoryAsPrimary
            || hardware.DedicatedVideoMemoryBytes <= 0)
            return null; // no dedicated pool to measure

        GpuPerformanceTelemetrySnapshot? snapshot = await _gpuTelemetryProvider.GetSnapshotAsync(ct).ConfigureAwait(false);
        return ToFreeMib(RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(hardware, snapshot, DateTime.UtcNow));
    }

    internal static int? ToFreeMib(long? freeBytes) =>
        freeBytes is long bytes ? (int)Math.Min(int.MaxValue, bytes / (1024L * 1024)) : null;

    public async Task<RuntimeManagerStatus> GetStatusAsync(CancellationToken ct)
    {
        SystemRamSnapshot? ramSnapshot = TryGetSystemRamSnapshot();
        return await GetStatusAsync(ct, ramSnapshot).ConfigureAwait(false);
    }

    internal async Task<RuntimeManagerStatus> GetStatusAsync(CancellationToken ct, SystemRamSnapshot? ramSnapshot)
    {
        int vramMib = await _process.GetVramMibAsync(ct);
        int? textPid = _process.GetTextProcessId();
        int? mediaPid = _process.GetMediaProcessId();
        // The hermetic overhead benchmark hosts a managed-compatible listener in the current test
        // process instead of launching a second HTTP server. LocalRuntimeService already treats
        // that listener as owned; keep the status surface consistent so routing does not relaunch
        // the inert runtime on every request. Production Core never binds the managed text port,
        // so this branch is unreachable outside that in-process managed host shape.
        bool inProcessManaged = textPid == null
            && OperatingSystem.IsWindows()
            && ManagedListenerOwnership.IsOwnedByThisProcess(LocalAiPaths.TextPort);
        bool textResident = textPid != null || inProcessManaged;
        bool textHealthy = textResident && await _process.IsTextHealthyAsync(ct).ConfigureAwait(false);
        bool mediaHealthy = mediaPid != null && await _process.IsMediaHealthyAsync(ct).ConfigureAwait(false);

        var backends = new List<RuntimeBackendStatus>
        {
            DescribeLlamaBackend("text-cuda", LocalAiPaths.RuntimeCudaPath, textPid, textHealthy, inProcessManaged),
            DescribeLlamaBackend("text-vulkan", LocalAiPaths.RuntimeVulkanPath, textPid, textHealthy, inProcessManaged),
            new()
            {
                Backend = "media-comfyui",
                Installed = File.Exists(LocalAiPaths.ComfyPython) && File.Exists(LocalAiPaths.ComfyMain),
                Version = null,
                Running = mediaPid != null,
                Healthy = mediaHealthy,
                ProcessId = mediaPid
            }
        };

        string activeProfile = textResident ? _process.GetActiveProfileLabel() : string.Empty;
        string activeProjector = textResident ? _process.GetActiveVisionProjectorPath() : string.Empty;
        var result = new RuntimeManagerStatus
        {
            Backends = backends,
            CurrentManagedProviderEndpoint = LocalRuntimeService.CurrentManagedProviderEndpoint,
            GpuDetected = vramMib >= LocalAiProcessManager.HeavyGpuVramThresholdMib,
            VramTotalMib = vramMib,
            RamTotalBytes = ramSnapshot?.TotalBytesAsLong ?? 0,
            GamingActive = _process.GamingActive,
            GamingShutdownPending = _process.GamingShutdown.Pending,
            LastGamingShutdownFailure = _process.GamingShutdown.LastFailure,
            ActiveRole = textResident ? ReadActiveRoleRaw() : null,
            ActiveProfile = string.IsNullOrWhiteSpace(activeProfile) ? null : activeProfile,
            ActiveModelPath = textResident ? ReadActiveModelPathRaw() : null,
            ActiveVisionProjectorPath = string.IsNullOrWhiteSpace(activeProjector) ? null : activeProjector,
            ActiveHasVisionSupport = !string.IsNullOrWhiteSpace(activeProjector),
            LaunchPlanExplanation = _process.LastLaunchPlanExplanation,
            TextLaunchTrace = _process.LastTextLaunchTrace.ToList()
        };
        try { result.VramFreeMib = await ResolveVramFreeMibAsync(result, result.RamTotalBytes, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { result.VramFreeMib = null; } // a failed sample is unknown, never a guess
        return result;
    }

    public async Task<RuntimeDiagnosticsReport> GetDiagnosticsAsync(CancellationToken ct)
    {
        var issues = new List<RuntimeDiagnosticIssue>();
        RuntimeManagerStatus status = await GetStatusAsync(ct).ConfigureAwait(false);
        RuntimeBackendStatus? cuda = status.Backends.FirstOrDefault(b => b.Backend == "text-cuda");
        RuntimeBackendStatus? vulkan = status.Backends.FirstOrDefault(b => b.Backend == "text-vulkan");
        RuntimeBackendStatus? media = status.Backends.FirstOrDefault(b => b.Backend == "media-comfyui");

        if (cuda is { Installed: true } && !status.GpuDetected)
            issues.Add(new RuntimeDiagnosticIssue { Severity = "warning", Message = "The CUDA text runtime is installed, but no NVIDIA GPU with at least 12GB VRAM was detected. Text generation will fall back to the small Vulkan model instead of the flagship profile." });
        if (cuda is { Installed: false } && vulkan is { Installed: false })
            issues.Add(new RuntimeDiagnosticIssue { Severity = "warning", Message = "No llama-server text runtime (CUDA or Vulkan) is installed at the expected path under " + LocalAiPaths.Root + "\\runtime\\. Text generation cannot start until one is." });
        if (media is { Installed: false })
            issues.Add(new RuntimeDiagnosticIssue { Severity = "info", Message = "ComfyUI is not installed; image/video/edit generation is unavailable until it is." });

        foreach (string role in LocalAiPaths.KnownTextRoles)
        {
            string? overridePath = LocalAiPaths.GetRoleOverride(role);
            if (overridePath != null && !File.Exists(overridePath))
                issues.Add(new RuntimeDiagnosticIssue { Severity = "warning", Message = $"Role \"{role}\" is assigned to {overridePath}, but that file no longer exists on disk." });
        }
        if (LocalAiPaths.GetRoleOverride("standard") == null && !File.Exists(LocalAiPaths.StandardModel))
            issues.Add(new RuntimeDiagnosticIssue { Severity = "warning", Message = "The built-in Standard model file is missing: " + LocalAiPaths.StandardModel });
        if (LocalAiPaths.GetRoleOverride("uncensored") == null && !File.Exists(LocalAiPaths.UncensoredModel))
            issues.Add(new RuntimeDiagnosticIssue { Severity = "warning", Message = "The built-in Uncensored model file is missing: " + LocalAiPaths.UncensoredModel });
        if (status.GamingActive)
            issues.Add(new RuntimeDiagnosticIssue { Severity = "info", Message = "Gaming Mode is currently active; all local AI loads are blocked until it ends." });
        if (LaunchDescriptorIssue(status.ActiveModelPath is not null, LocalAiProcessManager.LastTextLaunchDescriptorRefusal) is { } descriptorIssue)
            issues.Add(descriptorIssue);

        return new RuntimeDiagnosticsReport { Issues = issues, CheckedUtc = DateTime.UtcNow };
    }

    /// <summary>
    /// A text launch whose argv/environment cannot be described exactly has no ld1 launch descriptor, so no
    /// evidence can be attributed to it and AUTO recommendation admission silently stays closed. This makes the
    /// bounded reason (never values) visible instead. CUDA/OMP/GPU launch-environment changes alter the
    /// descriptor and therefore invalidate previously collected evidence by design.
    /// </summary>
    internal static RuntimeDiagnosticIssue? LaunchDescriptorIssue(bool textResident, string? refusal) =>
        textResident && !string.IsNullOrEmpty(refusal)
            ? new RuntimeDiagnosticIssue
            {
                Severity = "info",
                Message = "The running text model has no exact launch descriptor. " + CandidateReasonText.Explain("launch_descriptor_" + refusal)
                    + " Automatic recommendation stays unavailable for it."
            }
            : null;

    public async Task<RuntimeLoadResult> LoadRoleAsync(
        string fileName, string role, bool persistRoleAssignment, CancellationToken ct)
    {
        string? normalizedRole = LocalAiPaths.NormalizeTextRole(role);
        if (normalizedRole == null)
            return new RuntimeLoadResult { Success = false, Role = role, Message = "Unknown or unsupported role for a Managed Model Library load. Known text roles: " + string.Join(", ", LocalAiPaths.KnownTextRoles) + " (\"main\" is accepted as an alias for \"standard\")." };

        string path;
        try { path = ModelDownloadService.ResolveManagedModelPath(fileName); }
        catch (FileNotFoundException ex) { return new RuntimeLoadResult { Success = false, Role = normalizedRole, Message = ex.Message }; }
        catch (InvalidDataException ex) { return new RuntimeLoadResult { Success = false, Role = normalizedRole, Message = ex.Message }; }

        if (!LooksLikeValidGguf(path))
            return new RuntimeLoadResult { Success = false, Role = normalizedRole, ModelPath = path, Message = "That file is corrupt or not a valid GGUF file (missing the \"GGUF\" magic header)." };
        // Cheap capacity preflight only. The final check after the writer lease is acquired uses
        // live free VRAM; a static total here avoids rejecting a possible swap solely because the
        // currently resident model still occupies part of that device.
        RuntimeLoadResult? incompatible = await CheckHardwareCompatibilityQuickAsync(path, normalizedRole, ct).ConfigureAwait(false);
        if (incompatible != null) return incompatible;

        await using MediaExecutionCoordinator.Lease transition = await MediaExecutionCoordinator.Current
            .AcquireAsync("text-transition:assign:" + normalizedRole, ct).ConfigureAwait(false);
        return await LoadTextRoleCoreAsync(
            normalizedRole, path, persistRoleAssignment,
            useRequestedModelPath: true, transition: transition, ct: ct).ConfigureAwait(false);
    }

    public async Task<RuntimeLoadResult> EnsureRoleLoadedAsync(string role, CancellationToken ct)
    {
        string trimmedRole = (role ?? string.Empty).Trim().ToLowerInvariant();
        if (ModelRoleCatalog.MediaRoles.Contains(trimmedRole))
            return await EnsureMediaRoleLoadedAsync(trimmedRole, ct).ConfigureAwait(false);

        string? normalizedRole = LocalAiPaths.NormalizeTextRole(trimmedRole);
        if (normalizedRole == null)
            return new RuntimeLoadResult { Success = false, Role = trimmedRole, Message = "Unknown role. Known text roles: " + string.Join(", ", LocalAiPaths.KnownTextRoles) + "; media roles: " + string.Join(", ", ModelRoleCatalog.MediaRoles) + "." };

        string? path = LocalAiPaths.GetRoleModelPathOrNull(normalizedRole);
        if (path == null || !File.Exists(path))
            return new RuntimeLoadResult { Success = false, Role = normalizedRole, Message = path == null ? "No model is assigned to role \"" + normalizedRole + "\" yet." : "The model assigned to role \"" + normalizedRole + "\" is missing on disk: " + path };

        if (await IsRoleActiveAsync(normalizedRole, ct).ConfigureAwait(false))
        {
            // A writer that queued just after the status sample gets priority. Waiting here and
            // rechecking prevents a same-model request from racing onto a resident that is already
            // scheduled to be replaced, while the no-writer common case returns immediately.
            await MediaExecutionCoordinator.Current.WaitForMediaAvailabilityAsync(ct).ConfigureAwait(false);
            if (await IsRoleActiveAsync(normalizedRole, ct).ConfigureAwait(false))
                return new RuntimeLoadResult { Success = true, Role = normalizedRole, ModelPath = path, Message = "Already loaded (reused, no swap)." };
        }

        await using MediaExecutionCoordinator.Lease transition = await MediaExecutionCoordinator.Current
            .AcquireAsync("text-transition:role:" + normalizedRole, ct).ConfigureAwait(false);
        await PrepareExclusiveTextTransitionAsync(ct).ConfigureAwait(false);
        if (await IsRoleActiveAsync(normalizedRole, ct).ConfigureAwait(false))
            return new RuntimeLoadResult { Success = true, Role = normalizedRole, ModelPath = path, Message = "Already loaded after transition wait (duplicate swap avoided)." };

        return await LoadTextRoleCoreAsync(
            normalizedRole, path, persistRoleAssignment: false, useRequestedModelPath: false,
            transition: transition, ct: ct).ConfigureAwait(false);
    }

    public async Task<RuntimeLoadResult> EnsureExactCandidateLoadedAsync(string role, string filePath, CancellationToken ct)
    {
        string? normalizedRole = LocalAiPaths.NormalizeTextRole(role);
        if (normalizedRole == null)
            return new RuntimeLoadResult { Success = false, Role = role, Message = "Unknown or unsupported role for a candidate load: " + role + "." };
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return new RuntimeLoadResult { Success = false, Role = normalizedRole, Message = "That candidate file no longer exists on disk: " + filePath };

        if (await IsExactCandidateActiveAsync(normalizedRole, filePath, ct).ConfigureAwait(false))
        {
            await MediaExecutionCoordinator.Current.WaitForMediaAvailabilityAsync(ct).ConfigureAwait(false);
            if (await IsExactCandidateActiveAsync(normalizedRole, filePath, ct).ConfigureAwait(false))
                return new RuntimeLoadResult { Success = true, Role = normalizedRole, ModelPath = filePath, Message = "Already loaded (reused, no swap)." };
        }

        await using MediaExecutionCoordinator.Lease transition = await MediaExecutionCoordinator.Current
            .AcquireAsync("text-transition:candidate:" + normalizedRole, ct).ConfigureAwait(false);
        await PrepareExclusiveTextTransitionAsync(ct).ConfigureAwait(false);
        if (await IsExactCandidateActiveAsync(normalizedRole, filePath, ct).ConfigureAwait(false))
            return new RuntimeLoadResult { Success = true, Role = normalizedRole, ModelPath = filePath, Message = "Already loaded after transition wait (duplicate swap avoided)." };

        return await StartArbitraryTextModelWithinLeaseAsync(
            transition,
            filePath,
            RoleDisplayLabel(normalizedRole),
            "Role: ",
            adapterPaths: null,
            role: normalizedRole,
            persistRoleAssignment: false,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Planning only: the versioned descriptor of the launch <see cref="EnsureFreshExactCandidateLoadedAsync"/>
    /// would perform now for this role and file (same label, prefix and planner). Takes no writer,
    /// starts nothing, and returns a refusal reason instead of an approximate descriptor.
    /// </summary>
    internal async Task<TextLaunchDescriptorResult> PlanExactCandidateLaunchAsync(string role, string filePath,
        CancellationToken ct)
    {
        string? normalizedRole = LocalAiPaths.NormalizeTextRole(role);
        if (normalizedRole is null) return new TextLaunchDescriptorResult(null, "role_unsupported");
        return await _process.PlanFreshExactTextLaunchAsync(filePath, RoleDisplayLabel(normalizedRole), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Fresh exact load for a user's scoped Force. The writer lock precedes every
    /// resident-slot check and the file lease spans hashing, process launch and health validation.</summary>
    internal async Task<RuntimeLoadResult> EnsureFreshExactCandidateLoadedAsync(
        string role, string filePath, string expectedSha256,
        Func<FileStream?> acquireManagedFileReadLease,
        Func<CancellationToken, Task<bool>> stillCurrent, CancellationToken ct)
    {
        string? normalizedRole = LocalAiPaths.NormalizeTextRole(role);
        string? normalizedHash = ModelDiscoveryIdentity.NormalizeSha256(expectedSha256);
        if (normalizedRole is null || normalizedHash is null)
            return new RuntimeLoadResult { Success = false, Role = role, Message = "Invalid exact Candidate identity." };
        expectedSha256 = normalizedHash;
        if (_process.GamingActive)
            return new RuntimeLoadResult { Success = false, Role = normalizedRole, Message = "Gaming hard-off is active; exact Candidate routing is blocked." };

        await using MediaExecutionCoordinator.Lease transition = await MediaExecutionCoordinator.Current
            .AcquireAsync("text-transition:scoped-force:" + normalizedRole, ct).ConfigureAwait(false);
        return await EnsureFreshExactCandidateLoadedHeldAsync(normalizedRole, filePath, expectedSha256,
            acquireManagedFileReadLease, stillCurrent, transition, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The same fresh exact load, for a caller that already holds the exclusive text-transition writer (a
    /// recommendation transition spans proof, Pending, load and commit under ONE writer, so it must not
    /// nest-acquire the non-reentrant media writer). Gaming is rechecked here before anything starts.
    /// </summary>
    internal async Task<RuntimeLoadResult> EnsureFreshExactCandidateLoadedHeldAsync(
        string role, string filePath, string expectedSha256,
        Func<FileStream?> acquireManagedFileReadLease,
        Func<CancellationToken, Task<bool>> stillCurrent, MediaExecutionCoordinator.Lease transition,
        CancellationToken ct)
    {
        string? normalizedRole = LocalAiPaths.NormalizeTextRole(role);
        string? normalizedHash = ModelDiscoveryIdentity.NormalizeSha256(expectedSha256);
        if (normalizedRole == null || normalizedHash is null)
            return new RuntimeLoadResult { Success = false, Role = role, Message = "Invalid exact Candidate identity." };
        expectedSha256 = normalizedHash;
        if (_process.GamingActive)
            return new RuntimeLoadResult { Success = false, Role = normalizedRole, Message = "Gaming hard-off is active; exact Candidate routing is blocked." };
        await PrepareExclusiveTextTransitionAsync(ct).ConfigureAwait(false);
        if (_process.GamingActive)
            return new RuntimeLoadResult { Success = false, Role = normalizedRole, Message = "Gaming hard-off is active; exact Candidate routing is blocked." };
        bool freshRuntimeStarted = false;
        try
        {
            bool residentAtExactPath = await IsExactCandidateActiveAsync(normalizedRole, filePath, ct).ConfigureAwait(false);
            if (residentAtExactPath && _process.IsActiveTextModelIdentity(
                    runtimeHealthy: true, filePath, _process.GetActiveTextModelPath(), expectedSha256))
            {
                using FileStream? residentFileLease = acquireManagedFileReadLease();
                if (residentFileLease is null)
                    return new RuntimeLoadResult { Success = false, Role = normalizedRole,
                        Message = "The managed Candidate file is missing or unsafe; no model was substituted." };
                bool residentCandidateCurrent = await stillCurrent(ct).ConfigureAwait(false);
                return residentCandidateCurrent
                    ? new RuntimeLoadResult { Success = true, Role = normalizedRole, ModelPath = filePath,
                        Message = "Exact Candidate reused, no swap, from a resident runtime with matching verified bytes." }
                    : new RuntimeLoadResult { Success = false, Role = normalizedRole,
                        Message = "Exact Candidate identity changed before reuse; no model was substituted." };
            }

            await using FileStream? lease = acquireManagedFileReadLease();
            if (lease is null)
                return new RuntimeLoadResult { Success = false, Role = normalizedRole,
                    Message = "The managed Candidate file is missing or unsafe; no model was substituted." };
            if (!await MatchesExpectedHashAsync(lease, expectedSha256, ct).ConfigureAwait(false)
                || !await stillCurrent(ct).ConfigureAwait(false))
                return new RuntimeLoadResult { Success = false, Role = normalizedRole, Message = "Candidate bytes changed before exact load." };
            // This is the final load gate after coordinated/direct readers are drained. Use live
            // free VRAM here; the total-capacity estimate is only a preflight/ranking hint.
            HardwareFitEstimate? fit = await EstimateHardwareFitForAdmissionAsync(filePath, ct).ConfigureAwait(false);
            if (fit?.Compatible != true)
                return CreateHardwareCompatibilityFailure(filePath, normalizedRole, fit,
                    fit?.Compatible != true && _process.GetTextProcessId() != null)!;
            if (!MediaExecutionCoordinator.Current.IsCurrentMediaWriter(transition, "text-"))
                return new RuntimeLoadResult { Success = false, Role = normalizedRole, Message = "The exclusive text-runtime lease was lost before exact Candidate launch." };

            LocalAiProcessManager.RuntimeResult launched = await _process.StartFreshExactTextModelAsync(
                filePath, RoleDisplayLabel(normalizedRole), ct).ConfigureAwait(false);
            if (!launched.Success)
                return new RuntimeLoadResult { Success = false, Role = normalizedRole, Message = launched.Message };
            freshRuntimeStarted = true;

            bool sameBytes = await MatchesExpectedHashAsync(lease, expectedSha256, ct).ConfigureAwait(false);
            bool sameRuntime = await IsExactCandidateActiveAsync(normalizedRole, filePath, ct).ConfigureAwait(false);
            bool sameCandidate = await stillCurrent(ct).ConfigureAwait(false);
            using FileStream? currentManagedPath = acquireManagedFileReadLease();
            if (!sameBytes || !sameRuntime || !sameCandidate || currentManagedPath is null)
            {
                if (!_process.StopText() || await _process.IsTextHealthyAsync(ct).ConfigureAwait(false))
                    return new RuntimeLoadResult { Success = false, Role = normalizedRole,
                        Message = "Exact Candidate verification failed and the runtime could not be confirmed stopped." };
                return new RuntimeLoadResult { Success = false, Role = normalizedRole,
                    Message = "Exact Candidate bytes or loaded runtime changed during fresh load." };
            }
            WriteActiveRole(normalizedRole);
            _process.RecordExactTextModelIdentity(filePath, expectedSha256);
            return new RuntimeLoadResult { Success = true, Role = normalizedRole, ModelPath = filePath,
                Message = "Fresh exact Candidate load completed." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (freshRuntimeStarted && !_process.StopText())
                Trace.TraceWarning("A fresh exact load failed, and its text runtime did not stop; the next start refuses beside it.");
            return new RuntimeLoadResult { Success = false, Role = normalizedRole,
                Message = "Exact Candidate file or runtime is unavailable." };
        }
        catch
        {
            if (freshRuntimeStarted && !_process.StopText())
                Trace.TraceWarning("A fresh exact load failed, and its text runtime did not stop; the next start refuses beside it.");
            throw;
        }
    }

    private static async Task<bool> MatchesExpectedHashAsync(FileStream lease, string expectedSha256, CancellationToken ct)
    {
        lease.Position = 0;
        byte[] hash = await SHA256.HashDataAsync(lease, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    public RuntimeLoadResult Unload()
    {
        string? previousRole = ReadActiveRoleRaw();
        // A runtime that survives still holds VRAM, so the role stays recorded and the caller is told it did not stop.
        if (!_process.StopText())
            return new RuntimeLoadResult { Success = false, Message = "The text runtime did not stop and is still running.", Role = previousRole ?? string.Empty, ModelPath = null };
        ClearActiveRole();
        return new RuntimeLoadResult { Success = true, Message = "Stopped.", Role = previousRole ?? string.Empty, ModelPath = null };
    }

    public async Task<HardwareFitEstimate?> EstimateHardwareFitAsync(string path, CancellationToken ct)
    {
        long sizeBytes;
        try { sizeBytes = new FileInfo(path).Length; }
        catch { return null; }

        // Fast estimate used by SmartRouter ranking and as the cheap LoadRole preflight before it
        // acquires the writer. It is never a final admission decision: do not synchronously spawn
        // telemetry per candidate or treat this total-capacity estimate as proof a launch fits.
        int vramMib = await _process.GetVramMibAsync(ct).ConfigureAwait(false);
        SystemRamSnapshot? ramSnapshot = TryGetSystemRamSnapshot();
        long availableRamBytes = GetCurrentSystemRamBudgetBytes(path, ramSnapshot);
        double availableMib = Math.Max(0, vramMib) + availableRamBytes / 1024d / 1024d;
        if (ramSnapshot?.IsValid != true && vramMib <= 0) return null;

        return BuildHardwareFitEstimate(path, sizeBytes, availableMib);
    }

    private async Task<HardwareFitEstimate?> EstimateHardwareFitForAdmissionAsync(string path, CancellationToken ct)
    {
        long sizeBytes;
        try { sizeBytes = new FileInfo(path).Length; }
        catch { return null; }

        SystemRamSnapshot? ramSnapshot = TryGetSystemRamSnapshot();
        GpuInventoryStatus inventory = _process.InventoryProbe();
        int nvidiaSmiMib = await _process.GetVramMibAsync(ct).ConfigureAwait(false);
        RuntimeHardwareProfile hardware = ResolveAdmissionHardwareProfile(
            inventory, nvidiaSmiMib, ramSnapshot?.TotalBytesAsLong ?? 0);
        return await EstimateHardwareFitForAdmissionAsync(path, hardware, ramSnapshot, ct).ConfigureAwait(false);
    }

    internal static RuntimeManagerStatus BuildAdmissionHardwareStatus(int vramMib, long ramTotalBytes)
    {
        return new RuntimeManagerStatus
        {
            VramTotalMib = Math.Max(0, vramMib),
            RamTotalBytes = Math.Max(0, ramTotalBytes),
            Backends = new List<RuntimeBackendStatus>
            {
                new()
                {
                    Backend = "text-cuda",
                    Installed = File.Exists(Path.Combine(LocalAiPaths.RuntimeCudaPath, LocalAiPaths.LlamaServerExecutable))
                },
                new()
                {
                    Backend = "text-vulkan",
                    Installed = File.Exists(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable))
                }
            }
        };
    }

    internal static RuntimeHardwareProfile ResolveAdmissionHardwareProfile(
        GpuInventoryStatus inventory, int nvidiaSmiMib, long systemRamBytes)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        TextLaunchTier tier = TextLaunchTierSelector.Select(inventory, nvidiaSmiMib);
        int tierVramMib = (int)Math.Clamp(tier.VramMib, 0, int.MaxValue);
        RuntimeManagerStatus status = BuildAdmissionHardwareStatus(tierVramMib, systemRamBytes);
        return RuntimeHardwareProfileResolver.Resolve(status, inventory, systemRamBytes, tier);
    }

    internal async Task<HardwareFitEstimate?> EstimateHardwareFitForAdmissionAsync(
        string path,
        RuntimeHardwareProfile hardware,
        SystemRamSnapshot? ramSnapshot,
        CancellationToken ct)
    {
        long sizeBytes;
        try { sizeBytes = new FileInfo(path).Length; }
        catch { return null; }

        GpuPerformanceTelemetrySnapshot? gpuSnapshot = null;
        if (hardware.AcceleratorEligible
            && !hardware.UsesSharedSystemMemoryAsPrimary
            && (string.Equals(hardware.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase)
                || string.Equals(hardware.Vendor, "nvidia-legacy", StringComparison.OrdinalIgnoreCase)))
        {
            gpuSnapshot = await _gpuTelemetryProvider.GetSnapshotAsync(ct).ConfigureAwait(false);
        }

        return EstimateHardwareFitFromSnapshot(path, sizeBytes, hardware, ramSnapshot, gpuSnapshot, DateTime.UtcNow);
    }

    internal HardwareFitEstimate? EstimateHardwareFitFromSnapshot(
        string path,
        RuntimeHardwareProfile hardware,
        SystemRamSnapshot? ramSnapshot,
        GpuPerformanceTelemetrySnapshot? gpuSnapshot,
        DateTime nowUtc)
    {
        long sizeBytes;
        try { sizeBytes = new FileInfo(path).Length; }
        catch { return null; }

        return EstimateHardwareFitFromSnapshot(path, sizeBytes, hardware, ramSnapshot, gpuSnapshot, nowUtc);
    }

    private HardwareFitEstimate? EstimateHardwareFitFromSnapshot(
        string path,
        long sizeBytes,
        RuntimeHardwareProfile hardware,
        SystemRamSnapshot? ramSnapshot,
        GpuPerformanceTelemetrySnapshot? gpuSnapshot,
        DateTime nowUtc)
    {

        long availableRamBytes = GetCurrentSystemRamBudgetBytes(path, ramSnapshot);
        long availableGpuBytes = RuntimeGpuMemoryBudget.ResolveAvailableDedicatedGpuBytes(hardware, gpuSnapshot, nowUtc);
        if (hardware.UsesSharedSystemMemoryAsPrimary)
        {
            availableRamBytes = Math.Min(availableRamBytes, hardware.SharedSystemMemoryLimitBytes);
            availableRamBytes = Math.Min(availableRamBytes, hardware.UsableSystemRamBytes);
        }
        double availableMib = (availableGpuBytes + availableRamBytes) / 1024d / 1024d;
        if (ramSnapshot?.IsValid != true && availableGpuBytes <= 0) return null;

        return BuildHardwareFitEstimate(path, sizeBytes, availableMib);
    }

    private static HardwareFitEstimate BuildHardwareFitEstimate(string path, long sizeBytes, double availableMib)
    {
        GgufModelInfo model = GgufMetadataReader.Read(path);
        long contextLength = ResolveLaunchContextLength(model);
        string cacheType = ResolveLaunchCacheType(path);
        long? kvBytes = contextLength > 0 ? KvCacheSizeEstimator.Estimate(model, contextLength, cacheType, cacheType) : null;
        double weightsMib = sizeBytes / 1024.0 / 1024.0;
        double? kvMib = kvBytes.HasValue ? kvBytes.Value / 1024.0 / 1024.0 : null;
        double neededMib = kvMib.HasValue ? weightsMib + kvMib.Value + RuntimeReserveMib : weightsMib * VramOverheadFactor;
        return new HardwareFitEstimate(neededMib <= availableMib, neededMib, availableMib, kvMib, kvMib.HasValue ? contextLength : null);
    }

    internal static double EstimateAdmissionMemoryMib(int dedicatedVramMib, long totalRamBytes,
        double? usedRamPercent, double budgetPercent, long reclaimableBytes = 0)
    {
        double totalMib = totalRamBytes / 1024d / 1024d;
        double headroom = usedRamPercent.HasValue
            ? totalMib * (budgetPercent - usedRamPercent.Value) / 100d + Math.Max(0, reclaimableBytes) / 1024d / 1024d
            : totalMib * budgetPercent / 100d;
        return Math.Max(0, dedicatedVramMib) + Math.Clamp(headroom, 0, totalMib * budgetPercent / 100d);
    }

    internal long GetCurrentSystemRamBudgetBytes(string modelPath, SystemRamSnapshot? snapshot)
    {
        double maxSystemRamPercent = ExecutionProfileStore.Current.Get(modelPath)
            .Effective.Budget.MaxSystemRamPercent;
        return RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(
            snapshot, maxSystemRamPercent, GetReclaimableTextWorkingSetBytes());
    }

    /// <summary>
    /// The only managed entry point for arbitrary model swaps. It verifies the caller's actual
    /// exclusive lease, drains coordinated readers, direct llama slots and alternative engines,
    /// then samples fit immediately before the process manager is asked to replace its resident.
    /// </summary>
    internal async Task<RuntimeLoadResult> StartArbitraryTextModelWithinLeaseAsync(
        MediaExecutionCoordinator.Lease? transition,
        string path,
        string label,
        string profilePrefix,
        IReadOnlyList<string>? adapterPaths,
        string? role,
        bool persistRoleAssignment,
        CancellationToken ct)
    {
        RuntimeLoadResult? incompatible = await PrepareAndCheckTextModelStartAsync(
            transition, path, role ?? label, ct).ConfigureAwait(false);
        if (incompatible != null) return incompatible;

        if (persistRoleAssignment && role != null)
        {
            // persistRoleAssignment is true only for an explicit "make this the model for the role from now on"
            // request, so it is a user choice (origin: user) and will pin automatic recommendation.
            try { LocalAiPaths.SetRoleOverride(role, path); }
            catch (ManualSelectionGateBusyException ex)
            {
                return new RuntimeLoadResult { Success = false, Role = role, ModelPath = path, Message = ex.Code };
            }
            catch (InvalidDataException ex)
            {
                return new RuntimeLoadResult { Success = false, Role = role, ModelPath = path, Message = ex.Message };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new RuntimeLoadResult { Success = false, Role = role, ModelPath = path,
                    Message = "The role assignment could not be saved (" + ex.GetType().Name + "); the model was not loaded and your previous assignment is unchanged." };
            }
        }

        LocalAiProcessManager.RuntimeResult result = await _process.StartArbitraryTextModelAsync(
            path, label, profilePrefix, adapterPaths, ct).ConfigureAwait(false);
        if (result.Success && role != null) WriteActiveRole(role);
        return new RuntimeLoadResult
        {
            Success = result.Success,
            Message = result.Message,
            Role = role ?? label,
            ModelPath = path
        };
    }

    private long GetReclaimableTextWorkingSetBytes()
    {
        try
        {
            if (_process.GetTextProcessId() is not int pid) return 0;
            using Process resident = Process.GetProcessById(pid);
            return Math.Max(0, resident.WorkingSet64);
        }
        catch { return 0; }
    }

    private async Task<RuntimeLoadResult> LoadTextRoleCoreAsync(
        string normalizedRole,
        string path,
        bool persistRoleAssignment,
        bool useRequestedModelPath,
        MediaExecutionCoordinator.Lease transition,
        CancellationToken ct)
    {
        string? adapterPath = LocalAiPaths.GetRoleAdapter(normalizedRole);
        if (adapterPath != null || normalizedRole is not ("standard" or "uncensored"))
        {
            return await StartArbitraryTextModelWithinLeaseAsync(
                transition,
                path,
                RoleDisplayLabel(normalizedRole),
                "Role: ",
                adapterPath == null ? null : new[] { adapterPath },
                normalizedRole,
                persistRoleAssignment,
                ct).ConfigureAwait(false);
        }

        // EnsureRoleLoaded follows the active profile's normal hardware fallback (the small model
        // on hosts without a heavy GPU). An explicit Managed Model Library load instead pins the
        // user's requested GGUF, regardless of whether that assignment is persisted.
        string launchPath = useRequestedModelPath || await _process.HasHeavyGpuAsync(ct).ConfigureAwait(false)
            ? path
            : LocalAiPaths.SmallModel;

        // Built-in Standard/Uncensored launch through the profile action, but use the same lease,
        // direct-slot drain and current-resource admission boundary as arbitrary model swaps.
        RuntimeLoadResult? incompatible = await PrepareAndCheckTextModelStartAsync(
            transition, launchPath, normalizedRole, ct).ConfigureAwait(false);
        if (incompatible != null) return incompatible;
        if (persistRoleAssignment)
        {
            try { LocalAiPaths.SetRoleOverride(normalizedRole, path); }
            catch (ManualSelectionGateBusyException ex)
            {
                return new RuntimeLoadResult { Success = false, Role = normalizedRole, ModelPath = path, Message = ex.Code };
            }
            catch (InvalidDataException ex)
            {
                return new RuntimeLoadResult { Success = false, Role = normalizedRole, ModelPath = path, Message = ex.Message };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new RuntimeLoadResult { Success = false, Role = normalizedRole, ModelPath = path,
                    Message = "The role assignment could not be saved (" + ex.GetType().Name + "); the model was not loaded and your previous assignment is unchanged." };
            }
        }
        LocalAiProcessManager.RuntimeResult result = await _process.RunActionAsync(
            normalizedRole == "standard" ? "Standard" : "Uncensored",
            ct, modelPathOverride: launchPath).ConfigureAwait(false);
        if (result.Success) WriteActiveRole(normalizedRole);
        return new RuntimeLoadResult { Success = result.Success, Message = result.Message, Role = normalizedRole, ModelPath = launchPath };
    }

    private async Task<RuntimeLoadResult?> PrepareAndCheckTextModelStartAsync(
        MediaExecutionCoordinator.Lease? transition,
        string path,
        string role,
        CancellationToken ct)
    {
        MediaExecutionCoordinator coordinator = MediaExecutionCoordinator.Current;
        if (!coordinator.IsCurrentMediaWriter(transition, "text-"))
        {
            return new RuntimeLoadResult
            {
                Success = false,
                Role = role,
                ModelPath = path,
                Message = "Model admission requires this caller's active exclusive text-runtime lease."
            };
        }

        await PrepareExclusiveTextTransitionAsync(ct).ConfigureAwait(false);
        if (!coordinator.IsCurrentMediaWriter(transition, "text-"))
        {
            return new RuntimeLoadResult
            {
                Success = false,
                Role = role,
                ModelPath = path,
                Message = "The exclusive text-runtime lease was lost before model admission."
            };
        }

        return await CheckHardwareCompatibilityAsync(path, role, ct).ConfigureAwait(false);
    }

    private async Task PrepareExclusiveTextTransitionAsync(CancellationToken ct)
    {
        MediaExecutionCoordinator.Status scheduling = MediaExecutionCoordinator.Current.Snapshot();
        int externalReaders = scheduling.ActiveTextReaders - MediaExecutionCoordinator.ContextOwnedReaders;
        if (scheduling.ActiveOwner == null || externalReaders != 0)
            throw new InvalidOperationException("Text model transition requires an exclusive GPU writer with all coordinated readers drained.");

        await _process.WaitForIdleAsync(ct).ConfigureAwait(false);
        LocalInferenceEngineCoordinator? alternative = LocalInferenceEngineSharedServices.TryGetCoordinator();
        if (alternative != null)
        {
            await alternative.WaitForAlternativeEnginesIdleAsync(ct).ConfigureAwait(false);
            await alternative.StopAlternativeEnginesAsync(ct).ConfigureAwait(false);
        }
        // Protect local/LAN clients that speak to llama-server directly and therefore do not own
        // a PaRuX reader lease. Unknown slot state is a hard failure, never permission to stop.
        await TextInferenceQuiescence.WaitForIdleAsync(TextTransitionDrainTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The active role label alone is not proof that the role's assigned model is resident: an
    /// exact candidate load (or a reassignment of the role) leaves the label in place while a
    /// different file serves. Reusing it would answer with the wrong model and report the assigned
    /// path. When the resident path is known it must equal the assignment.
    /// </summary>
    internal static bool ActiveRoleResidentMatchesAssignment(
        string? activeRole, string? activeModelPath, string normalizedRole, string? assignedPath, bool healthyTextBackend)
    {
        if (!healthyTextBackend || activeRole == null
            || !string.Equals(activeRole, normalizedRole, StringComparison.OrdinalIgnoreCase))
            return false;
        return activeModelPath == null || assignedPath == null || PathsEqualLocal(activeModelPath, assignedPath);
    }

    private async Task<bool> IsRoleActiveAsync(string normalizedRole, CancellationToken ct)
    {
        RuntimeManagerStatus status = await GetStatusAsync(ct).ConfigureAwait(false);
        string? assignedPath = LocalAiPaths.GetRoleModelPathOrNull(normalizedRole);
        bool healthyText = status.Backends.Any(b => b.Backend.StartsWith("text-", StringComparison.Ordinal) && b.Running && b.Healthy);
        if (ActiveRoleResidentMatchesAssignment(status.ActiveRole, status.ActiveModelPath, normalizedRole, assignedPath, healthyText))
            return true;

        if (assignedPath != null && status.ActiveModelPath != null
            && PathsEqualLocal(status.ActiveModelPath, assignedPath)
            && status.Backends.Any(b => b.Backend.StartsWith("text-", StringComparison.Ordinal) && b.Running && b.Healthy))
        {
            WriteActiveRole(normalizedRole);
            return true;
        }
        return false;
    }

    private async Task<bool> IsExactCandidateActiveAsync(string normalizedRole, string filePath, CancellationToken ct)
    {
        RuntimeManagerStatus status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (status.ActiveModelPath != null
            && PathsEqualLocal(status.ActiveModelPath, filePath)
            && status.Backends.Any(b => b.Backend.StartsWith("text-", StringComparison.Ordinal) && b.Running && b.Healthy))
        {
            if (status.ActiveRole == null || !string.Equals(status.ActiveRole, normalizedRole, StringComparison.OrdinalIgnoreCase))
            {
                WriteActiveRole(normalizedRole);
            }
            return true;
        }
        return false;
    }

    private async Task<RuntimeLoadResult> EnsureMediaRoleLoadedAsync(string role, CancellationToken ct)
    {
        if (!MediaRoleToCheckpointType.TryGetValue(role, out string? checkpointType))
            return new RuntimeLoadResult { Success = false, Role = role, Message = "Unknown media role." };
        // The checkpoint the next job will load (override or template default), not the first file alphabetically.
        string checkpointFile = MediaCheckpointResolution.Resolve(checkpointType).File;
        if (await _process.IsMediaHealthyAsync(ct).ConfigureAwait(false))
            return new RuntimeLoadResult { Success = true, Role = role, ModelPath = checkpointFile, Message = "Already loaded (reused, no swap)." };

        await using MediaExecutionCoordinator.Lease transition = await MediaExecutionCoordinator.Current
            .AcquireAsync("media-transition:role:" + role, ct).ConfigureAwait(false);
        await _process.WaitForIdleAsync(ct).ConfigureAwait(false);
        LocalInferenceEngineCoordinator? alternative = LocalInferenceEngineSharedServices.TryGetCoordinator();
        if (alternative != null)
        {
            await alternative.WaitForAlternativeEnginesIdleAsync(ct).ConfigureAwait(false);
            await alternative.StopAlternativeEnginesAsync(ct).ConfigureAwait(false);
        }
        await TextInferenceQuiescence.WaitForIdleAsync(TextTransitionDrainTimeout, ct).ConfigureAwait(false);
        if (await _process.IsMediaHealthyAsync(ct).ConfigureAwait(false))
            return new RuntimeLoadResult { Success = true, Role = role, ModelPath = checkpointFile, Message = "Already loaded after transition wait." };
        ClearActiveRole();
        LocalAiProcessManager.RuntimeResult result = await _process.StartMediaAsync(ct).ConfigureAwait(false);
        return new RuntimeLoadResult { Success = result.Success, Role = role, ModelPath = checkpointFile, Message = result.Message };
    }

    internal async Task<RuntimeLoadResult?> CheckHardwareCompatibilityAsync(string path, string role, CancellationToken ct)
    {
        HardwareFitEstimate? fit = await EstimateHardwareFitForAdmissionAsync(path, ct).ConfigureAwait(false);
        bool resident = _process.GetTextProcessId() != null;
        if (fit is { Compatible: false } && resident)
            fit = ApplyResidentCredit(fit, await ResolveAdmissionHardwareAsync(ct).ConfigureAwait(false),
                path, _process.GetActiveTextModelPath());
        return CreateHardwareCompatibilityFailure(path, role, fit, fit?.Compatible != true && resident);
    }

    private async Task<RuntimeHardwareProfile> ResolveAdmissionHardwareAsync(CancellationToken ct)
    {
        SystemRamSnapshot? ramSnapshot = TryGetSystemRamSnapshot();
        int vramMib = await _process.GetVramMibAsync(ct).ConfigureAwait(false);
        RuntimeManagerStatus hardwareStatus = BuildAdmissionHardwareStatus(vramMib, ramSnapshot?.TotalBytesAsLong ?? 0);
        return RuntimeHardwareProfileResolver.Resolve(hardwareStatus, ramSnapshot?.TotalBytesAsLong ?? 0);
    }

    /// <summary>
    /// Credits the resident footprint only when its weights sit in dedicated accelerator memory, the one case
    /// where the live free-VRAM reading leaves them out. Weights in system RAM (CPU-only hosts, or a
    /// shared-memory iGPU as primary) are already handed back to the RAM budget through its resident
    /// working-set term, so crediting their footprint as well counted the same bytes twice.
    /// </summary>
    internal static HardwareFitEstimate ApplyResidentCredit(
        HardwareFitEstimate fit, RuntimeHardwareProfile hardware, string requestedPath, string? residentPath) =>
        ResidentWeightsOccupyAccelerator(hardware) ? CreditResidentTextModel(fit, requestedPath, residentPath) : fit;

    internal static bool ResidentWeightsOccupyAccelerator(RuntimeHardwareProfile hardware) =>
        hardware.AcceleratorEligible && !hardware.UsesSharedSystemMemoryAsPrimary;

    /// <summary>
    /// Live free VRAM is measured while PaRuX's own resident text model is still loaded, yet every
    /// text launch path stops that llama-server before starting the next one. Counting its memory
    /// as unavailable refused every swap between two large models (live: a Workflow planner task
    /// failed "needs 15.2 GB, only 12.5 GB available" while PaRuX's own 27B held the rest). Credit
    /// the resident model's footprint, estimated exactly as its own admission was, back to the
    /// budget; other processes' allocations stay excluded.
    /// </summary>
    internal static HardwareFitEstimate CreditResidentTextModel(HardwareFitEstimate fit, string requestedPath, string? residentPath)
    {
        if (string.IsNullOrWhiteSpace(residentPath) || !File.Exists(residentPath)) return fit;
        try
        {
            if (string.Equals(Path.GetFullPath(residentPath), Path.GetFullPath(requestedPath), StringComparison.OrdinalIgnoreCase))
                return fit;
            HardwareFitEstimate residentFootprint = BuildHardwareFitEstimate(residentPath, new FileInfo(residentPath).Length, 0);
            double available = fit.AvailableMib + residentFootprint.NeededMib;
            return fit with { AvailableMib = available, Compatible = fit.NeededMib <= available };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return fit;
        }
    }

    internal async Task<RuntimeLoadResult?> CheckHardwareCompatibilityQuickAsync(string path, string role, CancellationToken ct)
    {
        HardwareFitEstimate? fit = await EstimateHardwareFitAsync(path, ct).ConfigureAwait(false);
        return CreateHardwareCompatibilityFailure(path, role, fit,
            fit?.Compatible != true && _process.GetTextProcessId() != null);
    }

    private static RuntimeLoadResult? CreateHardwareCompatibilityFailure(
        string path, string role, HardwareFitEstimate? fit, bool residentTextRuntime)
    {
        if (fit?.Compatible == true) return null;
        if (fit == null)
            return new RuntimeLoadResult
            {
                Success = false,
                Role = role,
                ModelPath = path,
                Message = "Current RAM/VRAM capacity could not be measured; model load was refused."
                    + (residentTextRuntime ? " Stop the active text model in Runtime Manager and retry if this is a model swap." : string.Empty)
            };
        string swapGuidance = residentTextRuntime
            ? " PaRuX keeps the running model loaded because it cannot safely distinguish its VRAM from allocations made by other GPU processes. Stop the active model in Runtime Manager and retry to check the freed capacity."
            : string.Empty;
        return new RuntimeLoadResult
        {
            Success = false,
            Role = role,
            ModelPath = path,
            Message = string.Format(Inv, "This model needs an estimated {0:F1} GB but this machine has only ", fit.NeededMib / 1024d)
                + string.Format(Inv, "{0:F1} GB of VRAM+RAM available - incompatible with this hardware.", fit.AvailableMib / 1024d)
                + (fit.KvCacheMib.HasValue
                    ? string.Format(Inv, " That includes {0:F1} GB of KV cache at {1:N0} tokens of context.", fit.KvCacheMib.Value / 1024d, fit.AssumedContextLength)
                    : string.Empty)
                + swapGuidance
        };
    }

    private static RuntimeBackendStatus DescribeLlamaBackend(
        string name, string runtimePath, int? textPid, bool textHealthy, bool inProcessManaged = false)
    {
        string exe = Path.Combine(runtimePath, LocalAiPaths.LlamaServerExecutable);
        bool running = inProcessManaged || textPid != null && ProcessRunsFrom(textPid.Value, runtimePath);
        return new RuntimeBackendStatus { Backend = name, Installed = File.Exists(exe), Version = TryGetFileVersion(exe), Running = running, Healthy = running && textHealthy, ProcessId = running ? textPid : null };
    }

    private static bool ProcessRunsFrom(int pid, string runtimePath)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            string? modulePath = process.MainModule?.FileName;
            return modulePath != null && Path.GetFullPath(modulePath).StartsWith(Path.GetFullPath(runtimePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string? TryGetFileVersion(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return null;
            string? version = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch { return null; }
    }

    private SystemRamSnapshot? TryGetSystemRamSnapshot()
    {
        try { return _ramSnapshotProvider.TryGetSnapshot(); }
        catch { return null; }
    }

    private static bool LooksLikeValidGguf(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Length < 4) return false;
            Span<byte> header = stackalloc byte[4];
            int read = stream.Read(header);
            return read == 4 && header[0] == 'G' && header[1] == 'G' && header[2] == 'U' && header[3] == 'F';
        }
        catch { return false; }
    }

    internal static string RoleDisplayLabel(string role) => role.Length == 0 ? role : char.ToUpperInvariant(role[0]) + role[1..];

    private static string ResolveLaunchCacheType(string modelPath)
    {
        ContextPreferenceStore.ContextPreference? preference = ContextPreferenceStore.Get(modelPath);
        return preference != null && !string.IsNullOrWhiteSpace(preference.CacheType) ? preference.CacheType : LaunchCacheType;
    }

    private static long ResolveLaunchContextLength(GgufModelInfo model)
    {
        ContextPreferenceStore.ContextPreference? preference = ContextPreferenceStore.Get(model.FilePath ?? string.Empty);
        if (preference != null && preference.ContextLength > 0) return preference.ContextLength;
        long declared = model.ContextLength ?? 0;
        return declared <= 0 ? 0 : Math.Min(declared, DefaultLaunchContextLength);
    }

    private static string? ReadActiveRoleRaw()
    {
        try { return File.Exists(LocalAiPaths.ActiveRoleFile) ? File.ReadAllText(LocalAiPaths.ActiveRoleFile).Trim() : null; }
        catch { return null; }
    }

    private static string? ReadActiveModelPathRaw()
    {
        try { return File.Exists(LocalAiPaths.ActiveModelPathFile) ? File.ReadAllText(LocalAiPaths.ActiveModelPathFile).Trim() : null; }
        catch { return null; }
    }

    private static void WriteActiveRole(string role)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.ActiveRoleFile)!);
        File.WriteAllText(LocalAiPaths.ActiveRoleFile, role);
    }

    private static void ClearActiveRole()
    {
        try { File.Delete(LocalAiPaths.ActiveRoleFile); } catch { }
        try { File.Delete(LocalAiPaths.ActiveModelPathFile); } catch { }
    }

    private static bool PathsEqualLocal(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static readonly Dictionary<string, string> MediaRoleToCheckpointType = new()
    {
        ["image"] = "image",
        ["image_edit"] = "edit",
        ["video"] = "video"
    };
}
