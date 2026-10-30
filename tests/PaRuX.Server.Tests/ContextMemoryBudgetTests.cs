using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Regression coverage for BIC-04. These tests intentionally keep the legacy NVIDIA-heavy flag
/// separate from real DXGI hardware: a GPU can be physically present and Vulkan-eligible even when
/// RuntimeManagerStatus.GpuDetected is false.
/// </summary>
public sealed class ContextMemoryBudgetTests
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * 1024 * 1024;

    private static RuntimeManagerStatus Machine(
        long ramBytes,
        bool cudaInstalled = false,
        bool vulkanInstalled = false,
        bool legacyHeavyGpu = false,
        int legacyVramMib = 0) => new()
    {
        GpuDetected = legacyHeavyGpu,
        VramTotalMib = legacyVramMib,
        RamTotalBytes = ramBytes,
        Backends = new List<RuntimeBackendStatus>
        {
            new() { Backend = "text-cuda", Installed = cudaInstalled },
            new() { Backend = "text-vulkan", Installed = vulkanInstalled }
        }
    };

    private static GpuInventoryStatus Inventory(params GpuAdapterStatus[] adapters) => new()
    {
        Available = true,
        Source = "test-dxgi",
        Adapters = adapters.ToList()
    };

    private static GpuAdapterStatus Adapter(
        string name,
        uint vendorId,
        long dedicatedBytes,
        long sharedBytes,
        bool software = false) => new()
    {
        Name = name,
        VendorId = vendorId,
        DedicatedVideoMemoryBytes = dedicatedBytes,
        SharedSystemMemoryBytes = sharedBytes,
        IsSoftware = software
    };

    [Theory]
    [InlineData(4, 2)]
    [InlineData(8, 4)]
    [InlineData(12, 6)]
    [InlineData(16, 8)]
    [InlineData(32, 24)]
    [InlineData(64, 48)]
    public void Hardware_profile_preserves_nonzero_low_ram_budget_without_changing_larger_hosts(
        long totalGiB, long usableGiB)
    {
        Assert.Equal(usableGiB * Gib,
            RuntimeHardwareProfileResolver.ConservativeUsableSystemRamBytes(totalGiB * Gib));
    }

    [Fact]
    public void Eight_gib_integrated_gpu_uses_one_bounded_four_gib_shared_ram_pool()
    {
        RuntimeManagerStatus machine = Machine(8 * Gib, vulkanInstalled: true);
        GpuInventoryStatus inventory = Inventory(Adapter("Intel integrated", 0x8086, 128 * Mib, 8 * Gib));
        RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(machine, inventory);
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, hardware);

        Assert.True(hardware.UsesSharedSystemMemoryAsPrimary);
        Assert.Equal(4 * Gib, hardware.UsableSystemRamBytes);
        Assert.Equal(4 * Gib, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
    }

    [Fact]
    public void Cpu_only_uses_system_RAM_as_one_pool()
    {
        RuntimeManagerStatus machine = Machine(26 * Gib, vulkanInstalled: true);
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, Inventory());

        Assert.Equal(26 * Gib, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.Equal(26 * Gib, budget.TotalBytes);
        Assert.Equal("cpu", budget.Backend);
        Assert.False(budget.PrimaryPoolIsSharedSystemMemory);
    }

    [Fact]
    public void Radeon_780M_style_shared_memory_is_Vulkan_capacity_without_double_counting_RAM()
    {
        RuntimeManagerStatus machine = Machine(32 * Gib, vulkanInstalled: true, legacyHeavyGpu: false);
        GpuInventoryStatus gpu = Inventory(Adapter(
            "AMD Radeon 780M", 0x1002, 512 * Mib, 15 * Gib));

        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(machine, gpu);
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile);

        Assert.True(profile.GpuPresent);
        Assert.True(profile.AcceleratorEligible);
        Assert.Equal("amd", profile.Vendor);
        Assert.Equal("text-vulkan", profile.Backend);
        Assert.True(profile.UsesSharedSystemMemoryAsPrimary);
        Assert.Equal(0, profile.DedicatedVideoMemoryBytes); // tiny UMA reservation is not a second pool
        Assert.Equal(15 * Gib, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.Equal(15 * Gib, budget.TotalBytes);
        Assert.True(budget.PrimaryPoolIsSharedSystemMemory);
        Assert.Equal(24 * Gib, profile.UsableSystemRamBytes);
    }

    [Fact]
    public void Radeon_780M_shared_memory_is_capped_to_conservative_RAM_on_a_16_GiB_host()
    {
        RuntimeManagerStatus machine = Machine(16 * Gib, vulkanInstalled: true, legacyHeavyGpu: false);
        GpuInventoryStatus gpu = Inventory(Adapter(
            "AMD Radeon 780M", 0x1002, 512 * Mib, 15 * Gib));

        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(machine, gpu);
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile);

        Assert.Equal(8 * Gib, profile.UsableSystemRamBytes);
        Assert.Equal(8 * Gib, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.Equal(8 * Gib, budget.TotalBytes);
        Assert.True(budget.PrimaryPoolIsSharedSystemMemory);
    }

    [Fact]
    public void Current_RAM_pressure_caps_shared_pool_without_adding_a_second_RAM_pool()
    {
        RuntimeManagerStatus machine = Machine(32 * Gib, vulkanInstalled: true);
        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(machine, Inventory(Adapter(
            "AMD Radeon 780M", 0x1002, 512 * Mib, 15 * Gib)));

        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile, 6 * Gib);

        Assert.Equal(6 * Gib, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.Equal(6 * Gib, budget.TotalBytes);
        Assert.True(budget.PrimaryPoolIsSharedSystemMemory);
    }

    [Fact]
    public void Current_RAM_pressure_caps_CPU_and_discrete_offload_pools_only()
    {
        RuntimeManagerStatus cpu = Machine(26 * Gib, vulkanInstalled: true);
        ContextMemoryBudget.Budget cpuBudget = ContextMemoryBudget.For(cpu, Inventory(), 4 * Gib);
        Assert.Equal(4 * Gib, cpuBudget.TotalBytes);

        RuntimeManagerStatus discrete = Machine(64 * Gib, cudaInstalled: true, vulkanInstalled: true);
        ContextMemoryBudget.Budget discreteBudget = ContextMemoryBudget.For(discrete, Inventory(Adapter(
            "NVIDIA 24GB", 0x10DE, 24 * Gib, 32 * Gib)), 10 * Gib);
        Assert.Equal(24 * Gib, discreteBudget.VramBytes); // Static capacity overload remains for estimate-only callers.
        Assert.Equal(10 * Gib, discreteBudget.OffloadableRamBytes);
        Assert.Equal(34 * Gib, discreteBudget.TotalBytes);
    }

    [Fact]
    public void Context_budget_uses_the_same_OS_RAM_total_as_the_live_snapshot_when_status_total_is_unknown()
    {
        RuntimeManagerStatus status = Machine(0, vulkanInstalled: true);

        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(
            status, Inventory(), 4 * Gib, 32 * Gib);

        Assert.Equal(4 * Gib, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.Equal("cpu", budget.Backend);
    }

    [Fact]
    public async Task Context_GET_and_PUT_budgets_use_one_cached_live_NVIDIA_sample()
    {
        DateTime sampledUtc = DateTime.UtcNow;
        int queries = 0;
        var telemetryProvider = new CachedGpuPerformanceTelemetrySnapshotProvider(_ =>
        {
            Interlocked.Increment(ref queries);
            return Task.FromResult(new List<GpuPerformanceTelemetryDto>
            {
                new()
                {
                    Index = 0,
                    Name = "NVIDIA GeForce RTX 5080",
                    MemoryTotalMib = 16384,
                    MemoryUsedMib = 12288,
                    MemoryFreeMib = 4096,
                    SampledUtc = sampledUtc
                }
            });
        });
        RuntimeManagerStatus machine = Machine(64 * Gib, cudaInstalled: true, vulkanInstalled: true);
        GpuInventoryStatus inventory = Inventory(
            Adapter("AMD Radeon 780M", 0x1002, 512 * Mib, 12 * Gib),
            Adapter("NVIDIA GeForce RTX 5080", 0x10DE, 16 * Gib, 32 * Gib));
        RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(machine, inventory, 64 * Gib);

        ContextMemoryBudget.Budget getOptionsBudget = await ContextMemoryBudget.ForCurrentResourcesAsync(
            machine, hardware, 6 * Gib, 64 * Gib, telemetryProvider, CancellationToken.None);
        ContextMemoryBudget.Budget putPreferenceBudget = await ContextMemoryBudget.ForCurrentResourcesAsync(
            machine, hardware, 6 * Gib, 64 * Gib, telemetryProvider, CancellationToken.None);

        Assert.Equal("nvidia", hardware.Vendor);
        Assert.Equal(4 * Gib, getOptionsBudget.VramBytes);
        Assert.Equal(6 * Gib, getOptionsBudget.OffloadableRamBytes);
        Assert.Equal(getOptionsBudget, putPreferenceBudget);
        Assert.Equal(1, queries);
    }

    [Fact]
    public async Task Missing_or_zero_dedicated_GPU_sample_hides_context_capacity_instead_of_offering_RAM_only()
    {
        var telemetryProvider = new CachedGpuPerformanceTelemetrySnapshotProvider(_ =>
            Task.FromResult(new List<GpuPerformanceTelemetryDto>()));
        RuntimeManagerStatus machine = Machine(64 * Gib, cudaInstalled: true, vulkanInstalled: true);
        GpuInventoryStatus inventory = Inventory(
            Adapter("NVIDIA GeForce RTX 5080", 0x10DE, 16 * Gib, 32 * Gib));
        RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(machine, inventory, 64 * Gib);

        ContextMemoryBudget.Budget budget = await ContextMemoryBudget.ForCurrentResourcesAsync(
            machine, hardware, 12 * Gib, 64 * Gib, telemetryProvider, CancellationToken.None);

        Assert.Equal("nvidia", hardware.Vendor);
        Assert.Equal(0, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.Equal("text-cuda", budget.Backend);
        Assert.Equal(0, budget.TotalBytes);
    }

    [Fact]
    public async Task UMA_current_budget_counts_live_RAM_once_and_does_not_query_discrete_GPU_telemetry()
    {
        int queries = 0;
        var telemetryProvider = new CachedGpuPerformanceTelemetrySnapshotProvider(_ =>
        {
            Interlocked.Increment(ref queries);
            return Task.FromResult(new List<GpuPerformanceTelemetryDto>());
        });
        RuntimeManagerStatus machine = Machine(64 * Gib, vulkanInstalled: true);
        GpuInventoryStatus inventory = Inventory(Adapter("AMD Radeon 780M", 0x1002, 512 * Mib, 12 * Gib));
        RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(machine, inventory, 64 * Gib);

        ContextMemoryBudget.Budget budget = await ContextMemoryBudget.ForCurrentResourcesAsync(
            machine, hardware, 6 * Gib, 64 * Gib, telemetryProvider, CancellationToken.None);

        Assert.True(hardware.UsesSharedSystemMemoryAsPrimary);
        Assert.Equal(6 * Gib, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.True(budget.PrimaryPoolIsSharedSystemMemory);
        Assert.Equal(0, queries);
    }

    [Fact]
    public void Shared_memory_budget_fails_closed_when_total_system_RAM_is_unknown()
    {
        RuntimeManagerStatus machine = Machine(0, vulkanInstalled: true, legacyHeavyGpu: false);
        GpuInventoryStatus gpu = Inventory(Adapter(
            "AMD Radeon 780M", 0x1002, 512 * Mib, 15 * Gib));

        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(machine, gpu);
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile);

        Assert.True(profile.AcceleratorEligible);
        Assert.Equal(0, profile.UsableSystemRamBytes);
        Assert.Equal(0, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.Equal(0, budget.TotalBytes);
    }

    [Fact]
    public void Legacy_physical_VRAM_is_not_an_eligible_accelerator_without_an_installed_text_backend()
    {
        RuntimeManagerStatus machine = Machine(16 * Gib, legacyHeavyGpu: false, legacyVramMib: 8 * 1024);
        var unavailableInventory = new GpuInventoryStatus { Available = false, Source = "dxgi-error" };

        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(machine, unavailableInventory);
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile);

        Assert.True(profile.GpuPresent);
        Assert.False(profile.AcceleratorEligible);
        Assert.Equal("cpu", profile.Backend);
        Assert.Equal(8 * Gib, profile.DedicatedVideoMemoryBytes);
        Assert.Equal(8 * Gib, RuntimeHardwareProfileResolver.ConservativeUsableSystemRamBytes(16 * Gib));
        Assert.Equal(16 * Gib, budget.TotalBytes);
    }

    [Fact]
    public void Physical_inventory_without_a_compatible_text_backend_keeps_identity_but_uses_cpu_budget()
    {
        RuntimeManagerStatus machine = Machine(24 * Gib, cudaInstalled: false, vulkanInstalled: false);
        GpuInventoryStatus gpu = Inventory(Adapter("AMD discrete", 0x1002, 8 * Gib, 12 * Gib));

        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(machine, gpu);
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile);

        Assert.True(profile.GpuPresent);
        Assert.False(profile.AcceleratorEligible);
        Assert.Equal("AMD discrete", profile.AdapterName);
        Assert.Equal(8 * Gib, profile.DedicatedVideoMemoryBytes);
        Assert.Equal("cpu", budget.Backend);
        Assert.Equal(24 * Gib, budget.TotalBytes);
    }

    [Fact]
    public void Intel_integrated_GPU_is_not_misclassified_as_CPU_only_when_Vulkan_is_installed()
    {
        RuntimeManagerStatus machine = Machine(16 * Gib, vulkanInstalled: true);
        GpuInventoryStatus gpu = Inventory(Adapter(
            "Intel Arc integrated graphics", 0x8086, 128 * Mib, 8 * Gib));

        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(machine, gpu);
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile);

        Assert.True(profile.AcceleratorEligible);
        Assert.Equal("intel", profile.Vendor);
        Assert.Equal("text-vulkan", budget.Backend);
        Assert.Equal(8 * Gib, budget.TotalBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
    }

    [Fact]
    public void Nvidia_below_old_12GB_threshold_is_still_real_CUDA_capacity()
    {
        RuntimeManagerStatus machine = Machine(
            32 * Gib, cudaInstalled: true, vulkanInstalled: true,
            legacyHeavyGpu: false, legacyVramMib: 8 * 1024);
        GpuInventoryStatus gpu = Inventory(Adapter(
            "NVIDIA 8GB", 0x10DE, 8 * Gib, 16 * Gib));

        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(machine, gpu);
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile);

        Assert.True(profile.GpuPresent);
        Assert.True(profile.AcceleratorEligible);
        Assert.Equal("text-cuda", profile.Backend);
        Assert.Equal(8 * Gib, budget.VramBytes);
        Assert.Equal(16 * Gib, budget.OffloadableRamBytes);
    }

    [Fact]
    public void Nvidia_above_12GB_uses_dedicated_VRAM_plus_bounded_RAM_offload()
    {
        RuntimeManagerStatus machine = Machine(
            64 * Gib, cudaInstalled: true, vulkanInstalled: true,
            legacyHeavyGpu: true, legacyVramMib: 24 * 1024);
        GpuInventoryStatus gpu = Inventory(Adapter(
            "NVIDIA 24GB", 0x10DE, 24 * Gib, 32 * Gib));

        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, gpu);

        Assert.Equal(24 * Gib, budget.VramBytes);
        Assert.Equal(32 * Gib, budget.OffloadableRamBytes);
        Assert.Equal(56 * Gib, budget.TotalBytes);
        Assert.Equal("text-cuda", budget.Backend);
    }

    [Fact]
    public void Hardware_without_an_installed_eligible_text_backend_fails_closed_to_CPU_budget()
    {
        RuntimeManagerStatus machine = Machine(24 * Gib, cudaInstalled: false, vulkanInstalled: false);
        GpuInventoryStatus gpu = Inventory(Adapter(
            "AMD discrete", 0x1002, 12 * Gib, 12 * Gib));

        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(machine, gpu);
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile);

        Assert.True(profile.GpuPresent);
        Assert.False(profile.AcceleratorEligible);
        Assert.Equal("cpu", budget.Backend);
        Assert.Equal(24 * Gib, budget.TotalBytes);
    }

    [Fact]
    public void Vulkan_prefers_a_discrete_adapter_over_a_small_integrated_reservation()
    {
        RuntimeManagerStatus machine = Machine(32 * Gib, vulkanInstalled: true);
        GpuInventoryStatus gpu = Inventory(
            Adapter("Intel integrated", 0x8086, 128 * Mib, 16 * Gib),
            Adapter("AMD discrete", 0x1002, 8 * Gib, 16 * Gib));

        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(machine, gpu);
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile);

        Assert.Equal("AMD discrete", profile.AdapterName);
        Assert.False(profile.UsesSharedSystemMemoryAsPrimary);
        Assert.Equal(8 * Gib, budget.VramBytes);
        Assert.Equal(16 * Gib, budget.OffloadableRamBytes);
    }

    [Fact]
    public void Successful_DXGI_inventory_outranks_stale_legacy_GPU_heuristics()
    {
        RuntimeManagerStatus machine = Machine(
            32 * Gib, cudaInstalled: true, legacyHeavyGpu: true, legacyVramMib: 24 * 1024);

        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(machine, Inventory());
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile);

        Assert.False(profile.GpuPresent);
        Assert.False(profile.AcceleratorEligible);
        Assert.Equal(32 * Gib, budget.TotalBytes);
        Assert.Equal("cpu", budget.Backend);
    }

    [Fact]
    public void The_two_context_planners_agree_about_the_same_CPU_machine()
    {
        var model = new GgufModelInfo
        {
            FileName = "Qwen3.8-27B-UD-IQ4_XS.gguf",
            FilePath = "C:/models/Qwen3.8-27B-UD-IQ4_XS.gguf",
            BlockCount = 62,
            AttentionHeadCount = 40,
            KeyValueHeadCount = 8,
            EmbeddingLength = 5120,
            ContextLength = 262_144
        };
        const long weights = 13_270L * 1024 * 1024;
        RuntimeManagerStatus machine = Machine(26 * Gib);
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, Inventory());

        ContextCapacityPlanner.ContextCapacity capacity = ContextCapacityPlanner.Plan(
            model, budget.TotalBytes, weights);
        IReadOnlyList<ContextExpansionPlanner.ContextOption> options = ContextExpansionPlanner.Explore(
            model, budget.VramBytes, budget.OffloadableRamBytes, weights);

        Assert.True(capacity.Known);
        Assert.True(capacity.Recommended > 0);
        Assert.NotEmpty(options);
        Assert.Contains(options, option => option.ContextLength == capacity.Recommended);
    }
}
