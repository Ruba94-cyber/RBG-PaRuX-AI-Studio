using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DxgiAdapterMemoryBudgetTests
{
    private const long Gib = 1L << 30;
    private static DateTime Now => new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static RuntimeHardwareProfile Amd(long dedicated = 16 * Gib, string vendor = "amd",
        string name = "AMD Radeon RX 9070", bool eligible = true, bool shared = false) => new(
        GpuPresent: true, AcceleratorEligible: eligible, Backend: "text-vulkan", Vendor: vendor,
        AdapterName: name, DedicatedVideoMemoryBytes: dedicated, SharedSystemMemoryLimitBytes: 0,
        SystemRamBytes: 64 * Gib, SharedMemoryPrimary: shared, InventorySource: "dxgi");

    private static GpuAdapterMemorySample Sample(uint vendor = 0x1002, string name = "AMD Radeon RX 9070",
        long dedicated = 16 * Gib, long budget = 15 * Gib, long usage = 3 * Gib, DateTime? at = null,
        long? system = -2) =>
        new(vendor, 0x7550, name, dedicated, budget, usage, at ?? Now, 0x1234, system == -2 ? usage : system);

    private static GpuPerformanceTelemetrySnapshot Snap(params GpuAdapterMemorySample[] s) =>
        new(Now, Array.Empty<GpuPerformanceTelemetryDto>(), s);

    private static long Free(RuntimeHardwareProfile p, GpuPerformanceTelemetrySnapshot? s, DateTime? now = null) =>
        RuntimeGpuMemoryBudget.ResolveAvailableDedicatedGpuBytes(p, s, now ?? Now);

    [Fact]
    public void Amd_discrete_free_is_budget_minus_usage_capped_by_dedicated()
    {
        Assert.Equal(12 * Gib, Free(Amd(), Snap(Sample())));
        Assert.Equal(16 * Gib, Free(Amd(), Snap(Sample(budget: 20 * Gib, usage: 0)))); // budget > dedicated -> cap
    }

    [Fact]
    public void Other_process_vram_is_subtracted_via_system_wide_usage()
    {
        // This process uses 1 GiB, but a llama-server child + other apps push system usage to 9 GiB.
        Assert.Equal(6 * Gib, Free(Amd(), Snap(Sample(usage: 1 * Gib, system: 9 * Gib))));
        // Per-process usage is only a floor if the counter reads lower.
        Assert.Equal(12 * Gib, Free(Amd(), Snap(Sample(usage: 3 * Gib, system: 1 * Gib))));
    }

    [Fact]
    public void Missing_system_wide_usage_fails_closed_instead_of_using_per_process_usage()
    {
        Assert.Equal(0, Free(Amd(), Snap(Sample(usage: 0, system: null))));
        Assert.Equal(0, Free(Amd(), Snap(Sample(system: -1)))); // negative counter reading
    }

    [Fact]
    public void Pdh_instance_matching_requires_exactly_one_luid_instance()
    {
        long luid = (0x0L << 32) | 0xD2F1;
        var one = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["luid_0x00000000_0x0000D2F1_phys_0"] = 7 * Gib,
            ["luid_0x00000000_0x0000AAAA_phys_0"] = 1 * Gib
        };
        Assert.Equal(7 * Gib, PdhGpuAdapterMemory.TryMatchLuid(one, luid));
        Assert.Null(PdhGpuAdapterMemory.TryMatchLuid(one, 0xBEEF)); // absent
        one["luid_0x00000000_0x0000D2F1_phys_1"] = 2 * Gib;
        Assert.Null(PdhGpuAdapterMemory.TryMatchLuid(one, luid)); // multi-phys: ambiguous
        Assert.Equal(3 * Gib, PdhGpuAdapterMemory.TryMatchLuid(
            new Dictionary<string, long> { ["luid_0x00000001_0x00000002_phys_0"] = 3 * Gib }, (1L << 32) | 2));
    }

    [Fact]
    public void Intel_discrete_is_supported_by_vendor_id()
    {
        Assert.Equal(6 * Gib, Free(Amd(8 * Gib, "intel", "Intel Arc B580"),
            Snap(Sample(0x8086, "Intel Arc B580", 8 * Gib, 8 * Gib, 2 * Gib))));
    }

    [Fact]
    public void Over_budget_clamps_to_zero_not_negative()
    {
        Assert.Equal(0, Free(Amd(), Snap(Sample(budget: 4 * Gib, usage: 6 * Gib))));
    }

    [Fact]
    public void Unknown_stale_ambiguous_or_mismatched_samples_fail_closed()
    {
        Assert.Equal(0, Free(Amd(), null));
        Assert.Equal(0, Free(Amd(), new GpuPerformanceTelemetrySnapshot(Now, Array.Empty<GpuPerformanceTelemetryDto>())));
        Assert.Equal(0, Free(Amd(), Snap()));
        Assert.Equal(0, Free(Amd(), Snap(Sample(at: Now - TimeSpan.FromSeconds(5)))));
        Assert.Equal(0, Free(Amd(), Snap(Sample()), Now + TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Free(Amd(), Snap(Sample(), Sample()))); // two identical adapters: ambiguous
        Assert.Equal(0, Free(Amd(), Snap(Sample(vendor: 0x8086)))); // wrong vendor
        Assert.Equal(0, Free(Amd(), Snap(Sample(name: "Other GPU")))); // wrong name
        Assert.Equal(0, Free(Amd(), Snap(Sample(dedicated: 8 * Gib)))); // wrong total
        Assert.Equal(0, Free(Amd(), Snap(Sample(budget: 0))));
        Assert.Equal(0, Free(Amd(), Snap(Sample(usage: -1))));
    }

    [Fact]
    public void Ineligible_shared_primary_and_other_vendors_never_use_the_dxgi_budget()
    {
        Assert.Equal(0, Free(Amd(eligible: false), Snap(Sample())));
        Assert.Equal(0, Free(Amd(shared: true), Snap(Sample())));
        Assert.Equal(0, Free(Amd(vendor: "other"), Snap(Sample())));
        Assert.Equal(0, Free(Amd(name: " "), Snap(Sample())));
    }

    [Fact]
    public void Nvidia_path_is_unchanged_by_adapter_memory_samples()
    {
        RuntimeHardwareProfile rtx = Amd(16 * Gib, "nvidia", "NVIDIA GeForce RTX 5080");
        Assert.Equal(0, Free(rtx, Snap(Sample(0x10de, "NVIDIA GeForce RTX 5080")))); // only nvidia-smi is trusted
    }

    [Fact]
    public async Task Provider_samples_adapter_memory_and_survives_sampler_failure()
    {
        var ok = new CachedGpuPerformanceTelemetrySnapshotProvider(
            _ => Task.FromResult(new List<GpuPerformanceTelemetryDto>()), () => Now, new FakeSampler(Sample()));
        GpuPerformanceTelemetrySnapshot? snapshot = await ok.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(12 * Gib, Free(Amd(), snapshot));

        var broken = new CachedGpuPerformanceTelemetrySnapshotProvider(
            _ => Task.FromResult(new List<GpuPerformanceTelemetryDto>()), () => Now, new FakeSampler(null));
        snapshot = await broken.GetSnapshotAsync(CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(0, Free(Amd(), snapshot));
    }

    [Fact]
    public async Task Resource_guard_snapshot_with_stale_adapter_sample_is_rejected()
    {
        DateTime now = Now;
        var provider = new CachedGpuPerformanceTelemetrySnapshotProvider(
            _ => Task.FromResult(new List<GpuPerformanceTelemetryDto>()), () => now,
            new FakeSampler(Sample(at: Now)));
        await provider.GetSnapshotAsync(CancellationToken.None);
        Assert.NotNull(await provider.GetLatestForResourceGuardAsync(CancellationToken.None));
        now += TimeSpan.FromSeconds(3);
        Assert.Null(await provider.GetLatestForResourceGuardAsync(CancellationToken.None));
    }

    [Fact]
    public void Real_dxgi_sampler_never_throws_and_reports_sane_values_when_present()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (GpuAdapterMemorySample s in new DxgiGpuMemorySampler().Sample())
        {
            Assert.True(s.BudgetBytes >= 0);
            Assert.True(s.CurrentUsageBytes >= 0);
            Assert.True(s.VendorId != 0);
            Assert.True(s.SystemDedicatedUsageBytes is null or >= 0);
        }
    }

    [Fact]
    public async Task Hanging_sampler_neither_blocks_the_snapshot_nor_ages_nvidia_readings()
    {
        var hang = new HangingSampler();
        DateTime now = DateTime.UtcNow;
        var provider = new CachedGpuPerformanceTelemetrySnapshotProvider(
            _ => Task.FromResult(new List<GpuPerformanceTelemetryDto>
            {
                new() { Name = "NVIDIA GeForce RTX 5080", MemoryTotalMib = 16384, MemoryFreeMib = 8192, SampledUtc = DateTime.UtcNow }
            }), () => DateTime.UtcNow, hang, TimeSpan.FromMilliseconds(150));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        GpuPerformanceTelemetrySnapshot? snapshot = await provider.GetSnapshotAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        watch.Stop();
        hang.Release();

        Assert.NotNull(snapshot);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "snapshot must not wait for the hung sampler");
        Assert.Single(snapshot!.Devices);
        Assert.Equal(8192, snapshot.Devices[0].MemoryFreeMib);
        Assert.Empty(snapshot.AdapterMemory ?? Array.Empty<GpuAdapterMemorySample>());
        Assert.True(snapshot.ObservedUtc - snapshot.Devices[0].SampledUtc < CachedGpuPerformanceTelemetrySnapshotProvider.MaximumSampleAge);
    }

    [Fact]
    public async Task Persistent_sampler_stall_emits_one_sanitized_diagnostic()
    {
        var hang = new HangingSampler();
        DateTime t = DateTime.UtcNow;
        var messages = new List<string>();
        var provider = new CachedGpuPerformanceTelemetrySnapshotProvider(
            _ => Task.FromResult(new List<GpuPerformanceTelemetryDto>()), () => t, hang,
            TimeSpan.FromMilliseconds(50), messages.Add);
        for (int i = 0; i < 3; i++)
        {
            await provider.GetSnapshotAsync(CancellationToken.None);
            t += TimeSpan.FromSeconds(2);
        }
        hang.Release();
        string message = Assert.Single(messages);
        Assert.DoesNotContain("luid", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AMD/Intel", message);
    }

    [Fact]
    public void Pdh_match_distinguishes_absent_from_ambiguous()
    {
        var d = new Dictionary<string, long> { ["luid_0x00000000_0x00000001_phys_0"] = 1, ["luid_0x00000000_0x00000001_phys_1"] = 2 };
        Assert.Equal(PdhLuidMatch.Ambiguous, PdhGpuAdapterMemory.Match(d, 1, out _));
        Assert.Equal(PdhLuidMatch.Absent, PdhGpuAdapterMemory.Match(d, 2, out _));
        d.Remove("luid_0x00000000_0x00000001_phys_1");
        Assert.Equal(PdhLuidMatch.Found, PdhGpuAdapterMemory.Match(d, 1, out long v));
        Assert.Equal(1, v);
    }

    [Fact]
    public async Task Sampler_is_single_flight_while_a_previous_sample_is_still_running()
    {
        var hang = new HangingSampler();
        DateTime t = DateTime.UtcNow;
        var provider = new CachedGpuPerformanceTelemetrySnapshotProvider(
            _ => Task.FromResult(new List<GpuPerformanceTelemetryDto>()), () => t, hang, TimeSpan.FromMilliseconds(50));
        await provider.GetSnapshotAsync(CancellationToken.None);
        t += TimeSpan.FromSeconds(2);
        await provider.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(1, hang.Calls);
        hang.Release();
    }

    private sealed class HangingSampler : IGpuAdapterMemorySampler
    {
        private readonly ManualResetEventSlim _gate = new(false);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public void Release() => _gate.Set();
        public IReadOnlyList<GpuAdapterMemorySample> Sample()
        {
            Interlocked.Increment(ref _calls);
            _gate.Wait(TimeSpan.FromSeconds(30));
            return Array.Empty<GpuAdapterMemorySample>();
        }
    }

    private sealed class FakeSampler(GpuAdapterMemorySample? sample) : IGpuAdapterMemorySampler
    {
        public IReadOnlyList<GpuAdapterMemorySample> Sample() =>
            sample is null ? throw new InvalidOperationException("boom") : new[] { sample };
    }
}
