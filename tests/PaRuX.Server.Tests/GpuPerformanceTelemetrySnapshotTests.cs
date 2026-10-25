using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GpuPerformanceTelemetrySnapshotTests
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * Mib;

    private static DateTime Now => new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private static GpuPerformanceTelemetryDto Nvidia(
        string name = "NVIDIA GeForce RTX 5080",
        int totalMib = 16384,
        int freeMib = 8192,
        DateTime? sampledUtc = null,
        int index = 0) => new()
    {
        Index = index,
        Name = name,
        MemoryTotalMib = totalMib,
        MemoryUsedMib = totalMib - freeMib,
        MemoryFreeMib = freeMib,
        SampledUtc = sampledUtc ?? Now
    };

    private static RuntimeHardwareProfile RtxProfile(long totalBytes = 16 * Gib) => new(
        GpuPresent: true,
        AcceleratorEligible: true,
        Backend: "text-cuda",
        Vendor: "nvidia",
        AdapterName: "NVIDIA GeForce RTX 5080",
        DedicatedVideoMemoryBytes: totalBytes,
        SharedSystemMemoryLimitBytes: 0,
        SystemRamBytes: 64 * Gib,
        SharedMemoryPrimary: false,
        InventorySource: "dxgi");

    [Fact]
    public void Telemetry_parser_reads_device_free_memory_in_both_driver_shapes()
    {
        List<GpuPerformanceTelemetryDto> rich = GpuPerformanceTelemetryService.ParseRows(
            "0, NVIDIA GeForce RTX 5080, 16384, 4096, 12288, 23, 48, 255, 2400, 12.0",
            hasComputeCapability: true, Now);
        List<GpuPerformanceTelemetryDto> common = GpuPerformanceTelemetryService.ParseRows(
            "0, NVIDIA GeForce RTX 5080, 16384, 4096, 12288, 23, 48, 255, 2400",
            hasComputeCapability: false, Now);

        Assert.Single(rich);
        Assert.Equal(12288, rich[0].MemoryFreeMib);
        Assert.Equal(12.0, rich[0].ComputeCapability);
        Assert.Single(common);
        Assert.Equal(12288, common[0].MemoryFreeMib);
        Assert.Null(common[0].ComputeCapability);
    }

    [Fact]
    public async Task Snapshot_refresh_is_single_flight_cached_and_expires_quickly()
    {
        DateTime now = Now;
        int queries = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new CachedGpuPerformanceTelemetrySnapshotProvider(async ct =>
        {
            Interlocked.Increment(ref queries);
            started.TrySetResult();
            await finishQuery.Task.WaitAsync(ct);
            return new List<GpuPerformanceTelemetryDto> { Nvidia(sampledUtc: now) };
        }, () => now);

        Task<GpuPerformanceTelemetrySnapshot?>[] concurrent = Enumerable.Range(0, 12)
            .Select(_ => provider.GetSnapshotAsync(CancellationToken.None)).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        finishQuery.SetResult();
        GpuPerformanceTelemetrySnapshot?[] snapshots = await Task.WhenAll(concurrent);

        Assert.Equal(1, queries);
        Assert.All(snapshots, sample => Assert.NotNull(sample));
        Assert.Equal(1, (await provider.GetSnapshotAsync(CancellationToken.None))!.Devices.Count);
        Assert.Equal(1, queries);

        now += CachedGpuPerformanceTelemetrySnapshotProvider.CacheDuration + TimeSpan.FromMilliseconds(1);
        await provider.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task Resource_guard_gets_no_wait_snapshot_while_a_refresh_runs_in_background()
    {
        int queries = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new CachedGpuPerformanceTelemetrySnapshotProvider(async ct =>
        {
            Interlocked.Increment(ref queries);
            started.TrySetResult();
            await finishQuery.Task.WaitAsync(ct);
            return new List<GpuPerformanceTelemetryDto> { Nvidia() };
        }, () => Now);

        Task<GpuPerformanceTelemetrySnapshot?> immediate = provider.GetLatestForResourceGuardAsync(CancellationToken.None);
        Assert.True(immediate.IsCompleted);
        Assert.Null(await immediate);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        finishQuery.SetResult();

        GpuPerformanceTelemetrySnapshot? refreshed = await provider.GetSnapshotAsync(CancellationToken.None);
        Assert.NotNull(refreshed);
        Assert.Equal(1, queries);
    }

    [Fact]
    public async Task Candidate_discovery_obeys_cancellation_and_can_retry_after_an_interrupted_probe()
    {
        int probes = 0;
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverFinish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new CachedCommandCandidateResolver(new[] { "nvidia-smi" }, async (_, ct) =>
        {
            if (Interlocked.Increment(ref probes) == 1)
            {
                probeStarted.TrySetResult();
                return await neverFinish.Task.WaitAsync(ct);
            }
            return "index\n";
        }, TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();

        Task<string?> pending = resolver.ResolveAsync(cancellation.Token);
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("nvidia-smi", await resolver.ResolveAsync(CancellationToken.None));
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task Missing_candidate_is_negatively_cached_briefly_then_retried()
    {
        DateTime now = Now;
        bool commandAvailable = false;
        int probes = 0;
        var resolver = new CachedCommandCandidateResolver(new[] { "nvidia-smi", "fallback" },
            (candidate, _) =>
            {
                Interlocked.Increment(ref probes);
                return Task.FromResult(commandAvailable && candidate == "nvidia-smi" ? "index\n" : null);
            }, TimeSpan.FromSeconds(30), () => now);

        Assert.Null(await resolver.ResolveAsync(CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(CancellationToken.None));
        Assert.Equal(2, probes);

        commandAvailable = true;
        now += TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(1);
        Assert.Equal("nvidia-smi", await resolver.ResolveAsync(CancellationToken.None));
        Assert.Equal(3, probes);
    }

    [Fact]
    public void Admission_matches_only_one_exact_selected_NVIDIA_adapter_and_never_uses_indices()
    {
        var sample = new GpuPerformanceTelemetrySnapshot(Now, new[]
        {
            Nvidia(freeMib: 4096, index: 7),
            Nvidia(name: "NVIDIA GeForce RTX 4090", totalMib: 24576, freeMib: 20000, index: 0)
        });

        long available = RuntimeGpuMemoryBudget.ResolveAvailableDedicatedGpuBytes(RtxProfile(), sample, Now);

        Assert.Equal(4 * Gib, available);
    }

    [Fact]
    public void Ambiguous_stale_invalid_or_wrong_device_samples_fail_closed()
    {
        Assert.Equal(0, RuntimeGpuMemoryBudget.ResolveAvailableDedicatedGpuBytes(
            RtxProfile(), new GpuPerformanceTelemetrySnapshot(Now, new[]
            {
                Nvidia(freeMib: 4096, index: 0), Nvidia(freeMib: 2048, index: 1)
            }), Now));

        Assert.Equal(0, RuntimeGpuMemoryBudget.ResolveAvailableDedicatedGpuBytes(
            RtxProfile(), new GpuPerformanceTelemetrySnapshot(Now - TimeSpan.FromSeconds(4),
                new[] { Nvidia(sampledUtc: Now - TimeSpan.FromSeconds(4)) }), Now));

        Assert.Equal(0, RuntimeGpuMemoryBudget.ResolveAvailableDedicatedGpuBytes(
            RtxProfile(), new GpuPerformanceTelemetrySnapshot(Now, new[] { Nvidia(freeMib: 17000) }), Now));

        RuntimeHardwareProfile amdDiscrete = RtxProfile() with { Vendor = "amd", AdapterName = "AMD Radeon" };
        Assert.Equal(0, RuntimeGpuMemoryBudget.ResolveAvailableDedicatedGpuBytes(
            amdDiscrete, new GpuPerformanceTelemetrySnapshot(Now, new[] { Nvidia() }), Now));
    }

    [Fact]
    public void NVIDIA_memory_total_mismatch_does_not_borrow_another_adapters_free_memory()
    {
        RuntimeHardwareProfile largerAdapter = RtxProfile(totalBytes: 24 * Gib);
        var sample = new GpuPerformanceTelemetrySnapshot(Now, new[] { Nvidia(totalMib: 16384, freeMib: 12000) });

        Assert.Equal(0, RuntimeGpuMemoryBudget.ResolveAvailableDedicatedGpuBytes(largerAdapter, sample, Now));
    }
}
