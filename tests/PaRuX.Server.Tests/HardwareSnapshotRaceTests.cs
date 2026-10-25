using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-04: deterministic delayed-provider races. Every provider here is a fake gated by an explicit signal, so the
/// "slow sampler finishes after a newer one" interleaving is forced rather than hoped for. No GPU, no OS sampling.
/// </summary>
public sealed class HardwareSnapshotRaceTests
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * Mib;

    private static GpuAdapterStatus Nvidia(string driver, long dedicated = 16 * Gib) => new()
    {
        Index = 0, Name = "NVIDIA GeForce RTX 5080", VendorId = GpuInventoryService.NvidiaVendorId, DeviceId = 0x2C02,
        DedicatedVideoMemoryBytes = dedicated, DriverVersion = driver
    };

    private static readonly HostEvaluationFacts Host = new("cpu-hash", 16, "10.0.26220");

    [Fact]
    public async Task Delayed_inventory_sample_keeps_its_own_snapshot_and_hw2_fingerprint_when_a_newer_one_finishes_first()
    {
        var enumerator = new GatedEnumerator();
        var service = new GpuInventoryService(enumerator, () => true);

        Task<GpuInventoryStatus> slow = Task.Run(service.GetStatus);          // call 1: old driver, blocked inside DXGI
        await enumerator.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        enumerator.Next = new[] { Nvidia("32.0.15.9000") };                    // driver update lands while call 1 is in flight
        GpuInventoryStatus fast = service.GetStatus();                        // call 2 completes first
        enumerator.Release.TrySetResult();
        GpuInventoryStatus old = await slow.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("32.0.15.9000", fast.Adapters.Single().DriverVersion);
        Assert.Equal("32.0.15.8000", old.Adapters.Single().DriverVersion);
        string oldHw = EvaluationHardwareFingerprint.Build(old, Host, "text-cuda").Fingerprint;
        string newHw = EvaluationHardwareFingerprint.Build(fast, Host, "text-cuda").Fingerprint;
        Assert.StartsWith(EvaluationHardwareFingerprint.CurrentPrefix, oldHw);
        Assert.NotEqual(oldHw, newHw);
        // The late result is not "repaired" by the newer one: re-hashing the same snapshot is stable.
        Assert.Equal(oldHw, EvaluationHardwareFingerprint.Build(old, Host, "text-cuda").Fingerprint);
    }

    [Fact]
    public void Hw2_fingerprint_is_independent_of_adapter_enumeration_order_and_changes_with_driver_or_vram()
    {
        GpuAdapterStatus amd = new()
        {
            Index = 1, Name = "AMD Radeon 780M", VendorId = 0x1002, DeviceId = 0x15BF,
            DedicatedVideoMemoryBytes = 512 * Mib, DriverVersion = "31.0.24033.1003"
        };
        string a = EvaluationHardwareFingerprint.Build(
            GpuInventoryService.BuildStatus(new[] { Nvidia("1.0"), amd }, "dxgi"), Host).Fingerprint;
        string b = EvaluationHardwareFingerprint.Build(
            GpuInventoryService.BuildStatus(new[] { amd, Nvidia("1.0") }, "dxgi"), Host).Fingerprint;
        string driver = EvaluationHardwareFingerprint.Build(
            GpuInventoryService.BuildStatus(new[] { amd, Nvidia("1.1") }, "dxgi"), Host).Fingerprint;
        string vram = EvaluationHardwareFingerprint.Build(
            GpuInventoryService.BuildStatus(new[] { amd, Nvidia("1.0", 8 * Gib) }, "dxgi"), Host).Fingerprint;

        Assert.Equal(a, b);
        Assert.NotEqual(a, driver);
        Assert.NotEqual(a, vram);
        // The CUDA-scoped identity ignores the AMD adapter, so an AMD driver change must not invalidate CUDA evidence.
        GpuAdapterStatus amdNewDriver = new()
        {
            Index = 1, Name = amd.Name, VendorId = amd.VendorId, DeviceId = amd.DeviceId,
            DedicatedVideoMemoryBytes = amd.DedicatedVideoMemoryBytes, DriverVersion = "31.0.99999.1"
        };
        Assert.Equal(
            EvaluationHardwareFingerprint.Build(GpuInventoryService.BuildStatus(new[] { Nvidia("1.0"), amd }, "dxgi"), Host, "text-cuda").Fingerprint,
            EvaluationHardwareFingerprint.Build(GpuInventoryService.BuildStatus(new[] { Nvidia("1.0"), amdNewDriver }, "dxgi"), Host, "text-cuda").Fingerprint);
    }

    [Fact]
    public async Task Concurrent_runtime_status_calls_each_report_the_ram_sample_their_own_call_took()
    {
        var ram = new SequencedRamProvider(
            new SystemRamSnapshot(32UL << 30, 20UL << 30), new SystemRamSnapshot(16UL << 30, 2UL << 30));
        var runtime = new RuntimeManagerService(new LocalAiProcessManager(), ram,
            new CachedGpuPerformanceTelemetrySnapshotProvider(_ => Task.FromResult(new List<GpuPerformanceTelemetryDto>())));
        using IDisposable vram = new LocalAiProcessManager().OverrideVramMibForTests(0);

        // Call 1 takes the first sample and is held inside the provider; call 2 takes the second and finishes first.
        Task<RuntimeManagerStatus> slow = Task.Run(() => runtime.GetStatusAsync(CancellationToken.None));
        await ram.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        RuntimeManagerStatus fast = await runtime.GetStatusAsync(CancellationToken.None);
        ram.Release.TrySetResult();
        RuntimeManagerStatus first = await slow.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(16L * Gib, fast.RamTotalBytes);
        Assert.Equal(32L * Gib, first.RamTotalBytes);
        Assert.Equal(2, ram.Calls);
    }

    [Fact]
    public async Task Delayed_gpu_sampler_never_lets_a_stale_free_VRAM_value_widen_the_budget()
    {
        RuntimeManagerStatus machine = Machine(64 * Gib);
        RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(
            machine, Inventory(Nvidia("1.0")), 64 * Gib);
        var gpu = new GatedGpuProvider();

        // Sample A is taken while the resident model holds VRAM (2 GiB free) but is delivered after a swap freed it.
        Task<ContextMemoryBudget.Budget> delayed = ContextMemoryBudget.ForCurrentResourcesAsync(
            machine, hardware, 24 * Gib, 64 * Gib, gpu, CancellationToken.None);
        await gpu.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gpu.Current = Snapshot(DateTime.UtcNow, freeMib: 14 * 1024);          // post-swap sample for the next caller
        ContextMemoryBudget.Budget after = await ContextMemoryBudget.ForCurrentResourcesAsync(
            machine, hardware, 24 * Gib, 64 * Gib, new FixedGpuProvider(gpu.Current), CancellationToken.None);
        gpu.Complete(Snapshot(DateTime.UtcNow, freeMib: 2 * 1024));
        ContextMemoryBudget.Budget before = await delayed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(14 * Gib, after.VramBytes);
        Assert.Equal(2 * Gib, before.VramBytes);        // the delayed caller keeps the sample it was given
        Assert.True(before.TotalBytes < after.TotalBytes);

        // A sample that arrives older than the freshness window is unknown, which fails closed to no GPU capacity.
        ContextMemoryBudget.Budget stale = await ContextMemoryBudget.ForCurrentResourcesAsync(machine, hardware, 24 * Gib,
            64 * Gib, new FixedGpuProvider(Snapshot(
                DateTime.UtcNow - CachedGpuPerformanceTelemetrySnapshotProvider.MaximumSampleAge - TimeSpan.FromSeconds(5),
                14 * 1024)), CancellationToken.None);
        Assert.Equal(0, stale.VramBytes);
        Assert.Equal(0, stale.OffloadableRamBytes);
        Assert.Equal("text-cuda", stale.Backend);
    }

    [Fact]
    public void Free_vram_pressure_shrinks_the_gpu_pool_and_never_exceeds_the_device_total()
    {
        RuntimeManagerStatus machine = Machine(64 * Gib);
        RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(
            machine, Inventory(Nvidia("1.0")), 64 * Gib);
        long[] observed = { 15 * Gib, 8 * Gib, 512 * Mib, 0, 20 * Gib };
        long[] vram = observed.Select(free => ContextMemoryBudget.ForCurrentResources(
            machine, hardware, 24 * Gib, 64 * Gib, free).VramBytes).ToArray();

        Assert.Equal(new[] { 15 * Gib, 8 * Gib, 512 * Mib, 0, 16 * Gib }, vram);
    }

    [Fact]
    public void Ram_pressure_and_swap_reclaim_bound_the_shared_memory_pool_without_double_counting()
    {
        // 32 GiB UMA machine with a 12 GiB DXGI shared aperture (780M-style).
        GpuAdapterStatus igpu = new()
        {
            Index = 0, Name = "AMD Radeon 780M", VendorId = 0x1002, DeviceId = 0x15BF,
            DedicatedVideoMemoryBytes = 512 * Mib, SharedSystemMemoryBytes = 12 * Gib
        };
        RuntimeManagerStatus machine = Machine(32 * Gib, vulkan: true);
        RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(machine, Inventory(igpu), 32 * Gib);

        // Plenty free: bounded by the aperture, and nothing is added as a second RAM pool.
        long calm = RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(new SystemRamSnapshot(32UL << 30, 28UL << 30), 90);
        ContextMemoryBudget.Budget calmBudget = ContextMemoryBudget.ForCurrentResources(machine, hardware, calm, 32 * Gib, 0);
        Assert.True(calmBudget.PrimaryPoolIsSharedSystemMemory);
        Assert.Equal(0, calmBudget.OffloadableRamBytes);
        Assert.True(calmBudget.VramBytes <= 12 * Gib);

        // Memory pressure: other apps hold most RAM; only 3 GiB free. The pool follows the live budget down.
        long pressured = RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(new SystemRamSnapshot(32UL << 30, 3UL << 30), 90);
        ContextMemoryBudget.Budget tight = ContextMemoryBudget.ForCurrentResources(machine, hardware, pressured, 32 * Gib, 0);
        Assert.True(tight.VramBytes <= 3 * Gib, $"pool {tight.VramBytes} must not exceed free RAM");
        Assert.True(tight.VramBytes < calmBudget.VramBytes);

        // A model swap stops a 10 GiB resident child first, so that working set becomes available to the load...
        long swap = RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(
            new SystemRamSnapshot(32UL << 30, 3UL << 30), 90, reclaimableBytes: 10L * Gib);
        // (the 90% policy ceiling is already spent by other applications, so pressure alone admits nothing).
        Assert.Equal(0, pressured);
        Assert.True(swap > pressured && swap <= 13L * Gib, $"swap budget {swap}");
        Assert.True(swap <= (long)(32L * Gib * 0.9) - (32L * Gib - 3L * Gib - 10L * Gib) + 1);
        // ...but never more than free + reclaimable, and a hostile "reclaimable" value cannot exceed total RAM.
        long absurd = RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(
            new SystemRamSnapshot(32UL << 30, 3UL << 30), 90, reclaimableBytes: long.MaxValue);
        Assert.True(absurd <= 32L * Gib);
        // Invalid / missing samples fail closed to zero rather than to the full machine.
        Assert.Equal(0, RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(null, 90));
        Assert.Equal(0, RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(new SystemRamSnapshot(8UL << 30, 9UL << 30), 90));
        Assert.Equal(0, RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(new SystemRamSnapshot(32UL << 30, 3UL << 30), double.NaN));
        Assert.Equal(0, ContextMemoryBudget.ForCurrentResources(machine, hardware, 0, 32 * Gib, 0).VramBytes);
    }

    private static RuntimeManagerStatus Machine(long ramBytes, bool vulkan = false) => new()
    {
        RamTotalBytes = ramBytes,
        Backends = new List<RuntimeBackendStatus>
        {
            new() { Backend = "text-cuda", Installed = !vulkan },
            new() { Backend = "text-vulkan", Installed = vulkan }
        }
    };

    private static GpuInventoryStatus Inventory(params GpuAdapterStatus[] adapters) =>
        GpuInventoryService.BuildStatus(adapters, "test-dxgi");

    private static GpuPerformanceTelemetrySnapshot Snapshot(DateTime observedUtc, int freeMib) => new(observedUtc,
        new List<GpuPerformanceTelemetryDto>
        {
            new()
            {
                Index = 0, Name = "NVIDIA GeForce RTX 5080", MemoryTotalMib = 16384, MemoryFreeMib = freeMib,
                MemoryUsedMib = 16384 - freeMib, SampledUtc = observedUtc
            }
        });

    private sealed class GatedEnumerator : IGpuAdapterEnumerator
    {
        private int _calls;
        internal readonly TaskCompletionSource FirstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyList<GpuAdapterStatus> Next = Array.Empty<GpuAdapterStatus>();

        public IReadOnlyList<GpuAdapterStatus> Enumerate()
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                IReadOnlyList<GpuAdapterStatus> old = new[] { Nvidia("32.0.15.8000") };
                FirstEntered.TrySetResult();
                Release.Task.Wait(TimeSpan.FromSeconds(10));
                return old;
            }
            return Next;
        }
    }

    private sealed class SequencedRamProvider(params SystemRamSnapshot[] samples) : ISystemRamSnapshotProvider
    {
        private int _calls;
        internal readonly TaskCompletionSource FirstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls => Volatile.Read(ref _calls);

        public SystemRamSnapshot? TryGetSnapshot()
        {
            int index = Interlocked.Increment(ref _calls) - 1;
            if (index == 0)
            {
                FirstEntered.TrySetResult();
                Release.Task.Wait(TimeSpan.FromSeconds(10));
            }
            return samples[Math.Min(index, samples.Length - 1)];
        }
    }

    private sealed class FixedGpuProvider(GpuPerformanceTelemetrySnapshot? snapshot) : IGpuPerformanceTelemetrySnapshotProvider
    {
        public Task<GpuPerformanceTelemetrySnapshot?> GetSnapshotAsync(CancellationToken ct) => Task.FromResult(snapshot);
        public Task<GpuPerformanceTelemetrySnapshot?> GetLatestForResourceGuardAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class GatedGpuProvider : IGpuPerformanceTelemetrySnapshotProvider
    {
        private readonly TaskCompletionSource<GpuPerformanceTelemetrySnapshot?> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal GpuPerformanceTelemetrySnapshot? Current;

        internal void Complete(GpuPerformanceTelemetrySnapshot snapshot) => _result.TrySetResult(snapshot);

        public Task<GpuPerformanceTelemetrySnapshot?> GetSnapshotAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            return _result.Task;
        }

        public Task<GpuPerformanceTelemetrySnapshot?> GetLatestForResourceGuardAsync(CancellationToken ct) => GetSnapshotAsync(ct);
    }
}
