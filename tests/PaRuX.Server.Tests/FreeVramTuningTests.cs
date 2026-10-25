using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-04: one live free-VRAM fact feeds the status DTO, context admission and launch planning;
/// a measurement that cannot be established is unknown (null) and every consumer treats it
/// conservatively; and a Gaming transition drops the cached sample so the first launch after a
/// game exits sees the freed GPU. All samplers are fakes.
/// </summary>
public sealed class FreeVramTuningTests
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * Mib;
    private const string RadeonName = "AMD Radeon RX 7600";
    private const string RtxName = "NVIDIA GeForce RTX 5080";

    private static RuntimeHardwareProfile AmdDiscrete(long dedicated = 8 * Gib) => new(
        GpuPresent: true, AcceleratorEligible: true, Backend: "text-vulkan", Vendor: "amd", AdapterName: RadeonName,
        DedicatedVideoMemoryBytes: dedicated, SharedSystemMemoryLimitBytes: 0, SystemRamBytes: 32 * Gib,
        SharedMemoryPrimary: false, InventorySource: "dxgi");

    private static RuntimeHardwareProfile Rtx() => new(
        GpuPresent: true, AcceleratorEligible: true, Backend: "text-cuda", Vendor: "nvidia", AdapterName: RtxName,
        DedicatedVideoMemoryBytes: 16 * Gib, SharedSystemMemoryLimitBytes: 0, SystemRamBytes: 64 * Gib,
        SharedMemoryPrimary: false, InventorySource: "dxgi");

    private static GpuAdapterMemorySample AmdSample(DateTime now, long? systemUsage, long budget = 8 * Gib, long processUsage = 0, string name = RadeonName) =>
        new(0x1002, 0x7480, name, 8 * Gib, budget, processUsage, now, Luid: 1, SystemDedicatedUsageBytes: systemUsage);

    private static GpuPerformanceTelemetryDto RtxDevice(int freeMib, DateTime now, string name = RtxName) => new()
    {
        Index = 0, Name = name, MemoryTotalMib = 16384, MemoryUsedMib = 16384 - freeMib, MemoryFreeMib = freeMib, SampledUtc = now
    };

    private static GpuPerformanceTelemetrySnapshot Snapshot(DateTime now,
        IEnumerable<GpuPerformanceTelemetryDto>? devices = null, IEnumerable<GpuAdapterMemorySample>? adapters = null) =>
        new(now, (devices ?? Array.Empty<GpuPerformanceTelemetryDto>()).ToArray(),
            (adapters ?? Array.Empty<GpuAdapterMemorySample>()).ToArray());

    // ---- unknown is null, a measured zero is zero --------------------------------------------

    [Fact]
    public void Nvidia_measured_values_are_known_and_everything_else_is_unknown()
    {
        DateTime now = DateTime.UtcNow;
        Assert.Equal(6144L * Mib, RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(Rtx(), Snapshot(now, new[] { RtxDevice(6144, now) }), now));
        Assert.Equal(0L, RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(Rtx(), Snapshot(now, new[] { RtxDevice(0, now) }), now)); // full card: measured zero
        Assert.Null(RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(Rtx(), null, now));
        Assert.Null(RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(Rtx(), Snapshot(now), now));                       // nvidia-smi gave no devices
        Assert.Null(RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(Rtx(),
            Snapshot(now, new[] { RtxDevice(6144, now), RtxDevice(2048, now) }), now));                                             // ambiguous
        Assert.Null(RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(Rtx(),
            Snapshot(now.AddSeconds(-30), new[] { RtxDevice(6144, now.AddSeconds(-30)) }), now));                                    // stale
    }

    [Fact]
    public void Amd_and_Intel_discrete_without_a_trusted_system_wide_counter_are_unknown_not_zero()
    {
        DateTime now = DateTime.UtcNow;
        // Trusted: budget 8 GiB, system-wide dedicated usage 3 GiB -> 5 GiB free.
        Assert.Equal(5 * Gib, RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(
            AmdDiscrete(), Snapshot(now, adapters: new[] { AmdSample(now, 3 * Gib) }), now));
        // Per-process usage alone cannot prove headroom.
        Assert.Null(RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(
            AmdDiscrete(), Snapshot(now, adapters: new[] { AmdSample(now, systemUsage: null, processUsage: Gib) }), now));
        Assert.Null(RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(AmdDiscrete(), Snapshot(now), now));                 // sampler returned nothing
        Assert.Null(RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(AmdDiscrete(), null, now));
        Assert.Null(RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(AmdDiscrete(),
            Snapshot(now, adapters: new[] { AmdSample(now, 3 * Gib), AmdSample(now, 1 * Gib) }), now));                             // two matches
        // A genuinely full adapter is a measured zero.
        Assert.Equal(0L, RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(
            AmdDiscrete(), Snapshot(now, adapters: new[] { AmdSample(now, 8 * Gib) }), now));
        // The admission wrapper still fails closed for unknown.
        Assert.Equal(0L, RuntimeGpuMemoryBudget.ResolveAvailableDedicatedGpuBytes(AmdDiscrete(), Snapshot(now), now));
    }

    // ---- consumers: launch planner ------------------------------------------------------------

    [Fact]
    public void Launch_planner_uses_measured_free_vram_and_plans_unknown_conservatively()
    {
        RuntimeHardwareProfile amd = AmdDiscrete();
        long known = LaunchHardware.From(amd, 3 * Gib, 16 * Gib, 8, 16).GpuMemoryBytes;
        long full = LaunchHardware.From(amd, 0, 16 * Gib, 8, 16).GpuMemoryBytes;
        long unknown = LaunchHardware.From(amd, null, 16 * Gib, 8, 16).GpuMemoryBytes;
        long inflated = LaunchHardware.From(amd, 40 * Gib, 16 * Gib, 8, 16).GpuMemoryBytes;

        Assert.Equal(3 * Gib, known);
        Assert.Equal(0, full);                                    // a full card is not "unknown"
        Assert.Equal((long)(8 * Gib * LaunchHardware.UnknownFreeVramPlanningFraction), unknown);
        Assert.True(unknown < 8 * Gib, "unknown free VRAM must never be planned as the whole dedicated capacity");
        Assert.Equal(8 * Gib, inflated);                          // never above the adapter
    }

    // ---- consumers: context admission ---------------------------------------------------------

    [Fact]
    public async Task Context_admission_credits_a_measured_AMD_discrete_headroom_and_hides_capacity_when_unknown()
    {
        DateTime now = DateTime.UtcNow;
        RuntimeManagerStatus machine = Machine(32 * Gib);
        RuntimeHardwareProfile hardware = AmdDiscrete();

        ContextMemoryBudget.Budget measured = await ContextMemoryBudget.ForCurrentResourcesAsync(
            machine, hardware, 16 * Gib, 32 * Gib,
            Provider(() => Snapshot(now, adapters: new[] { AmdSample(DateTime.UtcNow, 3 * Gib) })), CancellationToken.None);
        ContextMemoryBudget.Budget unknown = await ContextMemoryBudget.ForCurrentResourcesAsync(
            machine, hardware, 16 * Gib, 32 * Gib, Provider(() => Snapshot(now)), CancellationToken.None);

        Assert.Equal(5 * Gib, measured.VramBytes);
        Assert.Equal(0, unknown.VramBytes);
        Assert.Equal(0, unknown.OffloadableRamBytes);
    }

    // ---- the status DTO ------------------------------------------------------------------------

    [Fact]
    public async Task Status_reports_measured_free_vram_and_null_when_it_is_unknown_or_not_applicable()
    {
        RuntimeManagerStatus machine = Machine(32 * Gib);
        GpuInventoryStatus amdInventory = Inventory(Adapter(RadeonName, 0x1002, 8 * Gib, 16 * Gib));
        GpuInventoryStatus rtxInventory = Inventory(Adapter(RtxName, 0x10DE, 16 * Gib, 32 * Gib));
        GpuInventoryStatus umaInventory = Inventory(Adapter("AMD Radeon 780M", 0x1002, 512 * Mib, 16 * Gib));

        Assert.Equal(5 * 1024, await FreeMib(machine, amdInventory, adapters: () => new[] { AmdSample(DateTime.UtcNow, 3 * Gib) }));
        Assert.Null(await FreeMib(machine, amdInventory));                                                // sampler produced nothing
        Assert.Null(await FreeMib(machine, amdInventory, adapters: () => new[] { AmdSample(DateTime.UtcNow, null) }));
        Assert.Equal(6144, await FreeMib(machine, rtxInventory, devices: () => new[] { RtxDevice(6144, DateTime.UtcNow) }));
        Assert.Equal(0, await FreeMib(machine, rtxInventory, devices: () => new[] { RtxDevice(0, DateTime.UtcNow) }));
        Assert.Null(await FreeMib(machine, rtxInventory));                                                // nvidia-smi timed out
        Assert.Null(await FreeMib(machine, umaInventory));                                                // shared memory: no dedicated pool
        Assert.Null(await FreeMib(Machine(32 * Gib, vulkanInstalled: false, cudaInstalled: false), amdInventory,
            adapters: () => new[] { AmdSample(DateTime.UtcNow, 3 * Gib) }));                              // no backend: not an accelerator
    }

    // ---- Gaming transitions --------------------------------------------------------------------

    [Fact]
    public async Task A_freed_GPU_after_a_game_exits_is_re_read_not_served_from_the_pre_transition_cache()
    {
        int freeMib = 1024;                    // the game holds the card
        int queries = 0;
        var provider = new CachedGpuPerformanceTelemetrySnapshotProvider(_ =>
        {
            Interlocked.Increment(ref queries);
            return Task.FromResult(new List<GpuPerformanceTelemetryDto> { RtxDevice(freeMib, DateTime.UtcNow) });
        });
        RuntimeManagerStatus machine = Machine(64 * Gib);
        GpuInventoryStatus inventory = Inventory(Adapter(RtxName, 0x10DE, 16 * Gib, 32 * Gib));
        var service = new RuntimeManagerService(new LocalAiProcessManager(0), new SystemRamSnapshotProvider(), provider, () => inventory);

        Assert.Equal(1024, await service.ResolveVramFreeMibAsync(machine, 64 * Gib, CancellationToken.None));
        freeMib = 15000;                       // the game exited; within the 1 s cache window nothing re-reads on its own
        Assert.Equal(1024, await service.ResolveVramFreeMibAsync(machine, 64 * Gib, CancellationToken.None));
        Assert.Equal(1, queries);

        var invalidations = new Counter();
        var runtime = NewRuntime(invalidations, provider);
        runtime.SetGamingState(active: false);  // the Gaming-off transition

        Assert.Equal(1, invalidations.Value);
        Assert.Equal(15000, await service.ResolveVramFreeMibAsync(machine, 64 * Gib, CancellationToken.None));
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task A_sample_started_before_the_transition_never_repopulates_the_cache_afterwards()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int freeMib = 1024;
        int calls = 0;
        var provider = new CachedGpuPerformanceTelemetrySnapshotProvider(async ct =>
        {
            int sampled = freeMib;
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult();
                await release.Task.WaitAsync(ct);   // a slow nvidia-smi that began while the game was running
            }
            return new List<GpuPerformanceTelemetryDto> { RtxDevice(sampled, DateTime.UtcNow) };
        });

        Task<GpuPerformanceTelemetrySnapshot?> stale = provider.GetSnapshotAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Invalidate();                       // game exits mid-sample
        freeMib = 15000;
        release.SetResult();
        Assert.Equal(1024, (await stale)!.Devices[0].MemoryFreeMib);   // the in-flight caller keeps its own answer

        GpuPerformanceTelemetrySnapshot? next = await provider.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(15000, next!.Devices[0].MemoryFreeMib);           // but nobody else is handed it
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Both_Gaming_directions_and_the_guard_transition_invalidate_the_GPU_sample()
    {
        var invalidations = new Counter();
        var runtime = NewRuntime(invalidations, provider: null);
        runtime.SetGamingState(active: true);
        runtime.SetGamingState(active: false);
        Assert.Equal(2, invalidations.Value);

        bool gaming = true;
        var guard = new GamingGuardService(runtime, new MediaJobService(null!), new SpeechService(),
            () => gaming, _ => Task.FromResult(true));
        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);   // game started
        gaming = false;
        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);   // game exited
        Assert.Equal(4, invalidations.Value);
        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);   // no transition: no extra work
        Assert.Equal(4, invalidations.Value);
    }

    // ---- harness -------------------------------------------------------------------------------

    private static RuntimeManagerStatus Machine(long ramBytes, bool cudaInstalled = true, bool vulkanInstalled = true) => new()
    {
        RamTotalBytes = ramBytes,
        Backends = new List<RuntimeBackendStatus>
        {
            new() { Backend = "text-cuda", Installed = cudaInstalled },
            new() { Backend = "text-vulkan", Installed = vulkanInstalled }
        }
    };

    private static GpuInventoryStatus Inventory(params GpuAdapterStatus[] adapters) =>
        new() { Available = true, Source = "test-dxgi", Adapters = adapters.ToList() };

    private static GpuAdapterStatus Adapter(string name, uint vendorId, long dedicated, long shared) => new()
    {
        Name = name, VendorId = vendorId, DedicatedVideoMemoryBytes = dedicated, SharedSystemMemoryBytes = shared
    };

    private sealed class FakeAdapterSampler(Func<IReadOnlyList<GpuAdapterMemorySample>> sample) : IGpuAdapterMemorySampler
    {
        public IReadOnlyList<GpuAdapterMemorySample> Sample() => sample();
    }

    /// <summary>A real caching provider over fake samplers; <paramref name="snapshot"/> supplies both sensors.</summary>
    private static IGpuPerformanceTelemetrySnapshotProvider Provider(Func<GpuPerformanceTelemetrySnapshot> snapshot) =>
        new CachedGpuPerformanceTelemetrySnapshotProvider(
            _ => Task.FromResult(snapshot().Devices.ToList()),
            adapterMemorySampler: new FakeAdapterSampler(() => snapshot().AdapterMemory ?? Array.Empty<GpuAdapterMemorySample>()));

    private static async Task<int?> FreeMib(
        RuntimeManagerStatus machine, GpuInventoryStatus inventory,
        Func<GpuPerformanceTelemetryDto[]>? devices = null, Func<GpuAdapterMemorySample[]>? adapters = null)
    {
        var provider = new CachedGpuPerformanceTelemetrySnapshotProvider(
            _ => Task.FromResult((devices?.Invoke() ?? Array.Empty<GpuPerformanceTelemetryDto>()).ToList()),
            adapterMemorySampler: new FakeAdapterSampler(() => adapters?.Invoke() ?? Array.Empty<GpuAdapterMemorySample>()));
        var service = new RuntimeManagerService(new LocalAiProcessManager(0), new SystemRamSnapshotProvider(), provider, () => inventory);
        return await service.ResolveVramFreeMibAsync(machine, machine.RamTotalBytes, CancellationToken.None);
    }

    private sealed class Counter { public int Value; }

    private static LocalRuntimeService NewRuntime(Counter invalidations, IGpuPerformanceTelemetrySnapshotProvider? provider)
    {
        var runtime = new LocalRuntimeService(new FakeHostEnvironment(), new LocalAiProcessManager(0), new ExternalTextProviderService());
        runtime.InvalidateGpuSamples = () => { Interlocked.Increment(ref invalidations.Value); provider?.Invalidate(); };
        return runtime;
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PaRuX.Server.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
