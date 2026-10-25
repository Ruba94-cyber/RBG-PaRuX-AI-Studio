using System.Diagnostics;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal sealed record GpuPerformanceTelemetrySnapshot(
    DateTime ObservedUtc,
    IReadOnlyList<GpuPerformanceTelemetryDto> Devices,
    IReadOnlyList<GpuAdapterMemorySample>? AdapterMemory = null);

internal interface IGpuPerformanceTelemetrySnapshotProvider
{
    Task<GpuPerformanceTelemetrySnapshot?> GetSnapshotAsync(CancellationToken ct);

    Task<GpuPerformanceTelemetrySnapshot?> GetLatestForResourceGuardAsync(CancellationToken ct);

    /// <summary>
    /// Drops any cached sample so the next read measures again. Called when something other than
    /// elapsed time changed the GPU's free memory (a game started or exited), so a decision made
    /// right after a Gaming transition never sees a pre-transition number.
    /// </summary>
    void Invalidate() { }
}

/// <summary>
/// Shares one short-lived device-wide telemetry sample across admission and the per-request resource
/// guard. nvidia-smi is a process launch, so it must not become a subprocess tax on every chat turn.
/// A timeout or unavailable sensor is cached briefly as an empty (unknown) sample rather than
/// fanning out retries. This is advisory telemetry, never a reservation.
/// </summary>
internal sealed class CachedGpuPerformanceTelemetrySnapshotProvider : IGpuPerformanceTelemetrySnapshotProvider
{
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaximumSampleAge = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan AdapterMemoryTimeout = TimeSpan.FromMilliseconds(750);

    private static readonly CachedGpuPerformanceTelemetrySnapshotProvider SharedInstance = new(
        ct => new GpuPerformanceTelemetryService().QueryAsync(ct),
        adapterMemorySampler: OperatingSystem.IsWindows()
            ? new DxgiGpuMemorySampler() : NoGpuAdapterMemorySampler.Instance);

    public static CachedGpuPerformanceTelemetrySnapshotProvider Shared => SharedInstance;

    /// <summary>Invalidates the process-wide sample (Gaming transitions).</summary>
    internal static void InvalidateShared() => SharedInstance.Invalidate();

    private long _generation;

    /// <inheritdoc />
    public void Invalidate()
    {
        // The generation also stops a refresh that started before the transition from re-publishing
        // its (pre-transition) sample after this call.
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _cached, null);
    }

    private readonly Func<CancellationToken, Task<List<GpuPerformanceTelemetryDto>>> _query;
    private readonly Func<DateTime> _utcNow;
    private readonly IGpuAdapterMemorySampler _adapterMemorySampler;
    private readonly TimeSpan _adapterMemoryTimeout;
    private readonly Action<string> _diagnostic;
    private int _adapterFailureReported;
    private Task<IReadOnlyList<GpuAdapterMemorySample>>? _adapterSampleInFlight;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private GpuPerformanceTelemetrySnapshot? _cached;
    private int _backgroundRefreshPending;

    internal CachedGpuPerformanceTelemetrySnapshotProvider(
        Func<CancellationToken, Task<List<GpuPerformanceTelemetryDto>>> query,
        Func<DateTime>? utcNow = null,
        IGpuAdapterMemorySampler? adapterMemorySampler = null,
        TimeSpan? adapterMemoryTimeout = null,
        Action<string>? diagnostic = null)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _adapterMemorySampler = adapterMemorySampler ?? NoGpuAdapterMemorySampler.Instance;
        _adapterMemoryTimeout = adapterMemoryTimeout ?? AdapterMemoryTimeout;
        _diagnostic = diagnostic ?? (message => Console.Error.WriteLine(message));
    }

    public async Task<GpuPerformanceTelemetrySnapshot?> GetSnapshotAsync(CancellationToken ct)
    {
        GpuPerformanceTelemetrySnapshot? cached = Volatile.Read(ref _cached);
        if (IsCachedSampleFresh(cached, _utcNow())) return Clone(cached!);

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _cached);
            DateTime now = _utcNow();
            if (IsCachedSampleFresh(cached, now)) return Clone(cached!);
            long generation = Interlocked.Read(ref _generation);

            // DXGI/PDH sampling runs in parallel with nvidia-smi and is bounded separately, so a slow
            // or hung sampler can neither age the NVIDIA sample nor block this snapshot.
            Task<IReadOnlyList<GpuAdapterMemorySample>> adapterTask = StartAdapterSampling();
            List<GpuPerformanceTelemetryDto> devices;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QueryTimeout);
            try
            {
                devices = await _query(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                devices = new List<GpuPerformanceTelemetryDto>();
            }

            IReadOnlyList<GpuAdapterMemorySample> adapterMemory = await AwaitAdapterSamplingAsync(adapterTask, ct)
                .ConfigureAwait(false);

            var snapshot = new GpuPerformanceTelemetrySnapshot(
                ObservedUtc: _utcNow(),
                Devices: devices.Select(Clone).ToArray(),
                AdapterMemory: adapterMemory);
            // A sample taken across an Invalidate() belongs to the previous GPU state: serve it to this
            // caller but do not let it become the cache the next decision reads.
            if (Interlocked.Read(ref _generation) == generation) Volatile.Write(ref _cached, snapshot);
            return Clone(snapshot);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private Task<IReadOnlyList<GpuAdapterMemorySample>> StartAdapterSampling()
    {
        // Single flight: while a previous (possibly hung) sample still runs, do not stack threads.
        Task<IReadOnlyList<GpuAdapterMemorySample>>? running = Volatile.Read(ref _adapterSampleInFlight);
        if (running is { IsCompleted: false })
            // A previous sample is still hung: this is the persistent-stall case, make it visible.
            ReportAdapterSamplingFailureOnce();
        if (running is { IsCompleted: false })
            return Task.FromResult<IReadOnlyList<GpuAdapterMemorySample>>(Array.Empty<GpuAdapterMemorySample>());

        Task<IReadOnlyList<GpuAdapterMemorySample>> task = Task.Run<IReadOnlyList<GpuAdapterMemorySample>>(
            () => _adapterMemorySampler.Sample().ToArray());
        Volatile.Write(ref _adapterSampleInFlight, task);
        return task;
    }

    private void ReportAdapterSamplingFailureOnce()
    {
        if (Interlocked.CompareExchange(ref _adapterFailureReported, 1, 0) != 0) return;
        try
        {
            _diagnostic("PaRuX GPU memory telemetry: adapter memory sampling (DXGI/PDH) timed out or failed; "
                + "AMD/Intel discrete free VRAM is treated as unknown (0) until it recovers. Reported once per process.");
        }
        catch { }
    }

    private async Task<IReadOnlyList<GpuAdapterMemorySample>> AwaitAdapterSamplingAsync(
        Task<IReadOnlyList<GpuAdapterMemorySample>> task, CancellationToken ct)
    {
        try
        {
            await task.WaitAsync(_adapterMemoryTimeout, ct).ConfigureAwait(false);
            return task.Result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // observe
            ReportAdapterSamplingFailureOnce();
            return Array.Empty<GpuAdapterMemorySample>(); // timeout/failure = unknown (fail closed)
        }
    }

    /// <summary>
    /// Non-blocking telemetry for the chat hot path. It returns a sample at most two seconds old,
    /// and refreshes older/missing data in the background; a slow driver query never delays TTFT.
    /// </summary>
    public Task<GpuPerformanceTelemetrySnapshot?> GetLatestForResourceGuardAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<GpuPerformanceTelemetrySnapshot?>(ct);
        GpuPerformanceTelemetrySnapshot? cached = Volatile.Read(ref _cached);
        if (cached == null || !IsSampleWithinMaximumAge(cached, _utcNow()))
        {
            ScheduleBackgroundRefresh();
            return Task.FromResult<GpuPerformanceTelemetrySnapshot?>(null);
        }

        if (!IsCachedSampleFresh(cached, _utcNow())) ScheduleBackgroundRefresh();
        return Task.FromResult<GpuPerformanceTelemetrySnapshot?>(Clone(cached));
    }

    private void ScheduleBackgroundRefresh()
    {
        if (Interlocked.CompareExchange(ref _backgroundRefreshPending, 1, 0) != 0) return;
        _ = Task.Run(RefreshForResourceGuardAsync);
    }

    private async Task RefreshForResourceGuardAsync()
    {
        try { await GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { }
        finally { Volatile.Write(ref _backgroundRefreshPending, 0); }
    }

    private bool IsCachedSampleFresh(GpuPerformanceTelemetrySnapshot? snapshot, DateTime now)
    {
        if (snapshot == null) return false;
        TimeSpan age = now - snapshot.ObservedUtc;
        return age >= TimeSpan.Zero && age < CacheDuration;
    }

    private bool IsSampleWithinMaximumAge(GpuPerformanceTelemetrySnapshot snapshot, DateTime now)
    {
        TimeSpan age = now - snapshot.ObservedUtc;
        if (age < TimeSpan.Zero || age > MaximumSampleAge) return false;
        return snapshot.Devices.All(device =>
        {
            TimeSpan sampleAge = now - device.SampledUtc;
            return sampleAge >= TimeSpan.Zero && sampleAge <= MaximumSampleAge;
        }) && (snapshot.AdapterMemory ?? Array.Empty<GpuAdapterMemorySample>()).All(sample =>
        {
            TimeSpan sampleAge = now - sample.SampledUtc;
            return sampleAge >= TimeSpan.Zero && sampleAge <= MaximumSampleAge;
        });
    }

    private static GpuPerformanceTelemetrySnapshot Clone(GpuPerformanceTelemetrySnapshot snapshot) =>
        new(snapshot.ObservedUtc, snapshot.Devices.Select(Clone).ToArray(), snapshot.AdapterMemory);

    private static GpuPerformanceTelemetryDto Clone(GpuPerformanceTelemetryDto device) => new()
    {
        Index = device.Index,
        Name = device.Name,
        MemoryTotalMib = device.MemoryTotalMib,
        MemoryUsedMib = device.MemoryUsedMib,
        MemoryFreeMib = device.MemoryFreeMib,
        UtilizationPercent = device.UtilizationPercent,
        TemperatureC = device.TemperatureC,
        PowerWatts = device.PowerWatts,
        SmClockMhz = device.SmClockMhz,
        ComputeCapability = device.ComputeCapability,
        SampledUtc = device.SampledUtc
    };
}

/// <summary>Conservative bridge from live NVIDIA device-wide memory to the selected DXGI adapter.</summary>
internal static class RuntimeGpuMemoryBudget
{
    private const long Mib = 1024L * 1024;
    private const long MemoryTotalToleranceBytes = 512L * Mib;

    /// <summary>Free dedicated bytes for admission; an unknown value counts as 0 (fail closed).</summary>
    internal static long ResolveAvailableDedicatedGpuBytes(
        RuntimeHardwareProfile hardware,
        GpuPerformanceTelemetrySnapshot? snapshot,
        DateTime nowUtc) =>
        TryResolveAvailableDedicatedGpuBytes(hardware, snapshot, nowUtc) ?? 0;

    /// <summary>
    /// Free dedicated bytes on the selected discrete adapter, or null when it cannot be established
    /// (no or stale sample, ambiguous or unmatched adapter, AMD/Intel without trusted system-wide
    /// usage). Null is "unknown", never "empty": a measured 0 is returned as 0. An adapter with no
    /// dedicated pool to measure (CPU-only, shared-memory GPU) reports 0 here and is not a discrete case.
    /// </summary>
    internal static long? TryResolveAvailableDedicatedGpuBytes(
        RuntimeHardwareProfile hardware,
        GpuPerformanceTelemetrySnapshot? snapshot,
        DateTime nowUtc)
    {
        if (!hardware.AcceleratorEligible
            || hardware.UsesSharedSystemMemoryAsPrimary
            || hardware.DedicatedVideoMemoryBytes <= 0)
            return 0;
        if (snapshot == null) return null;

        TimeSpan snapshotAge = nowUtc - snapshot.ObservedUtc;
        if (snapshotAge < TimeSpan.Zero || snapshotAge > CachedGpuPerformanceTelemetrySnapshotProvider.MaximumSampleAge)
            return null;

        IEnumerable<GpuPerformanceTelemetryDto> candidates;
        if (string.Equals(hardware.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(hardware.AdapterName))
        {
            string selectedName = NormalizeName(hardware.AdapterName);
            candidates = snapshot.Devices.Where(device =>
                string.Equals(NormalizeName(device.Name), selectedName, StringComparison.OrdinalIgnoreCase));
        }
        else if (string.Equals(hardware.Vendor, "nvidia-legacy", StringComparison.OrdinalIgnoreCase))
        {
            candidates = snapshot.Devices.Where(device => HasCompatibleMemoryTotal(
                hardware.DedicatedVideoMemoryBytes, device.MemoryTotalMib));
        }
        else
        {
            // AMD/Intel discrete: the dedicated total is not free capacity. Use the OS video-memory
            // budget (DXGI QueryVideoMemoryInfo), matched to exactly one adapter; anything else is
            // unknown (0). Shared UMA capacity is handled via RAM, not here.
            return ResolveDxgiBudgetFreeBytes(hardware, snapshot, nowUtc);
        }

        GpuPerformanceTelemetryDto[] matches = candidates.ToArray();
        if (matches.Length != 1) return null; // No DXGI-index/nvidia-smi-index assumption; duplicates are ambiguous.

        GpuPerformanceTelemetryDto selected = matches[0];
        TimeSpan deviceSampleAge = nowUtc - selected.SampledUtc;
        if (deviceSampleAge < TimeSpan.Zero
            || deviceSampleAge > CachedGpuPerformanceTelemetrySnapshotProvider.MaximumSampleAge
            || !HasCompatibleMemoryTotal(hardware.DedicatedVideoMemoryBytes, selected.MemoryTotalMib)
            || selected.MemoryFreeMib is not int freeMib
            || selected.MemoryTotalMib is not int totalMib
            || freeMib < 0
            || totalMib <= 0
            || freeMib > totalMib)
            return null;

        long freeBytes = (long)freeMib * Mib;
        return Math.Min(hardware.DedicatedVideoMemoryBytes, freeBytes);
    }

    /// <summary>
    /// Headroom on the selected adapter: min(Budget, dedicated) - max(systemUsage, processUsage),
    /// where systemUsage is the system-wide PDH "GPU Adapter Memory" dedicated usage for the same
    /// LUID (includes llama-server children and every other app). DXGI CurrentUsage alone is
    /// per-process and is only used as a floor. Zero (fail closed) when the system-wide value is
    /// unavailable or ambiguous, the vendor is not AMD/Intel, the sample is missing/stale, matches
    /// are zero or duplicate, or the budget is non-positive. Advisory, not a reservation.
    /// </summary>
    private static long? ResolveDxgiBudgetFreeBytes(
        RuntimeHardwareProfile hardware, GpuPerformanceTelemetrySnapshot snapshot, DateTime nowUtc)
    {
        uint vendorId = hardware.Vendor.ToLowerInvariant() switch { "amd" => 0x1002u, "intel" => 0x8086u, _ => 0u };
        if (vendorId == 0 || string.IsNullOrWhiteSpace(hardware.AdapterName) || snapshot.AdapterMemory is null)
            return null;

        string selectedName = NormalizeName(hardware.AdapterName);
        GpuAdapterMemorySample[] matches = snapshot.AdapterMemory.Where(sample =>
            sample.VendorId == vendorId
            && sample.DedicatedBytes == hardware.DedicatedVideoMemoryBytes
            && string.Equals(NormalizeName(sample.Name), selectedName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return null; // ambiguous or absent: never guess between adapters

        GpuAdapterMemorySample selected = matches[0];
        TimeSpan age = nowUtc - selected.SampledUtc;
        if (age < TimeSpan.Zero || age > CachedGpuPerformanceTelemetrySnapshotProvider.MaximumSampleAge
            || selected.BudgetBytes <= 0 || selected.CurrentUsageBytes < 0 || selected.DedicatedBytes <= 0
            || selected.SystemDedicatedUsageBytes is not long systemUsage || systemUsage < 0)
            return null; // no trusted system-wide usage: never report per-process-only headroom

        long used = Math.Max(systemUsage, selected.CurrentUsageBytes);
        long headroom = Math.Min(selected.BudgetBytes, selected.DedicatedBytes) - used;
        return Math.Clamp(headroom, 0, hardware.DedicatedVideoMemoryBytes);
    }

    private static bool HasCompatibleMemoryTotal(long adapterBytes, int? telemetryTotalMib)
    {
        if (adapterBytes <= 0 || telemetryTotalMib is not > 0) return false;
        long telemetryBytes = (long)telemetryTotalMib.Value * Mib;
        long difference = Math.Abs(adapterBytes - telemetryBytes);
        return difference <= Math.Max(MemoryTotalToleranceBytes, adapterBytes / 50); // max(512 MiB, 2%)
    }

    private static string NormalizeName(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
