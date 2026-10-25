using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
    /// Characterization tests for the public <see cref="ContextMemoryBudget"/> API: null handling,
    /// saturating arithmetic of <see cref="ContextMemoryBudget.Budget.TotalBytes"/> (negative
    /// components are clamped to zero before the saturating sum) and the CPU / discrete /
    /// shared-memory budget branches, using only deterministic snapshots.
/// </summary>
public sealed class ContextMemoryBudgetCharacterizationTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private static RuntimeManagerStatus Machine(
        long ramBytes,
        bool cudaInstalled = false,
        bool vulkanInstalled = false) => new()
    {
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
        long sharedBytes) => new()
    {
        Name = name,
        VendorId = vendorId,
        DedicatedVideoMemoryBytes = dedicatedBytes,
        SharedSystemMemoryBytes = sharedBytes
    };

    [Fact]
    public void Null_status_returns_zero_cpu_budget_on_every_public_overload()
    {
        Assert.Equal(0, ContextMemoryBudget.For(null!).TotalBytes);
        Assert.Equal(0, ContextMemoryBudget.For(null!, Inventory()).TotalBytes);
        Assert.Equal(0, ContextMemoryBudget.For(null!, 8 * Gib).TotalBytes);
        Assert.Equal(0, ContextMemoryBudget.For(null!, Inventory(), 8 * Gib).TotalBytes);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(4, 0, 4)]
    [InlineData(0, 5, 5)]
    [InlineData(3, 4, 7)]
    [InlineData(-1, 5, 5)]
    [InlineData(2, -7, 2)]
    [InlineData(-5, -3, 0)]
    [InlineData(long.MaxValue, 0, long.MaxValue)]
    [InlineData(long.MaxValue, 1, long.MaxValue)]
    [InlineData(1, long.MaxValue, long.MaxValue)]
    public void Total_bytes_is_a_saturating_sum_that_never_overflows(long vram, long offload, long expected)
    {
        ContextMemoryBudget.Budget budget = new(vram, offload);

        Assert.Equal(expected, budget.TotalBytes);
    }

    [Fact]
    public void Cpu_only_machine_exposes_RAM_as_the_single_pool()
    {
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(Machine(20 * Gib), Inventory());

        Assert.Equal(20 * Gib, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.Equal(20 * Gib, budget.TotalBytes);
        Assert.Equal("cpu", budget.Backend);
        Assert.False(budget.PrimaryPoolIsSharedSystemMemory);
    }

    [Fact]
    public void Discrete_gpu_uses_full_VRAM_and_caps_offload_at_half_RAM_or_live_budget()
    {
        var machine = Machine(32 * Gib, cudaInstalled: true, vulkanInstalled: true);
        var profile = RuntimeHardwareProfileResolver.Resolve(
            machine, Inventory(Adapter("NVIDIA 16GB", 0x10DE, 16 * Gib, 32 * Gib)));

        ContextMemoryBudget.Budget full = ContextMemoryBudget.For(machine, profile);
        Assert.Equal(16 * Gib, full.VramBytes);
        Assert.Equal(16 * Gib, full.OffloadableRamBytes);
        Assert.Equal(32 * Gib, full.TotalBytes);
        Assert.Equal("text-cuda", full.Backend);
        Assert.False(full.PrimaryPoolIsSharedSystemMemory);

        ContextMemoryBudget.Budget pressured = ContextMemoryBudget.For(machine, profile, 10 * Gib);
        Assert.Equal(16 * Gib, pressured.VramBytes);
        Assert.Equal(10 * Gib, pressured.OffloadableRamBytes);
        Assert.Equal(26 * Gib, pressured.TotalBytes);
    }

    [Fact]
    public void Shared_memory_gpu_counts_RAM_exactly_once_and_marks_the_shared_pool()
    {
        var machine = Machine(32 * Gib, vulkanInstalled: true);
        var profile = RuntimeHardwareProfileResolver.Resolve(
            machine, Inventory(Adapter("AMD Radeon 780M", 0x1002, 512 * 1024 * 1024, 15 * Gib)));

        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile);

        Assert.True(budget.PrimaryPoolIsSharedSystemMemory);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.Equal(15 * Gib, budget.VramBytes);
        Assert.Equal(15 * Gib, budget.TotalBytes);
        Assert.Equal("text-vulkan", budget.Backend);
    }

    [Fact]
    public void Eligible_backend_with_zero_memory_fails_closed_to_cpu_budget()
    {
        var machine = Machine(24 * Gib, vulkanInstalled: true);
        GpuInventoryStatus inventory = new() { Available = true, Source = "test-dxgi", Adapters = new List<GpuAdapterStatus>() };
        var profile = RuntimeHardwareProfileResolver.Resolve(machine, inventory);

        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(machine, profile);

        Assert.Equal("cpu", budget.Backend);
        Assert.Equal(24 * Gib, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.Equal(24 * Gib, budget.TotalBytes);
    }

    [Fact]
    public void Discrete_gpu_without_verified_free_VRAM_exposes_no_capacity_but_keeps_backend()
    {
        var machine = Machine(64 * Gib, cudaInstalled: true, vulkanInstalled: true);
        var profile = RuntimeHardwareProfileResolver.Resolve(
            machine, Inventory(Adapter("NVIDIA 16GB", 0x10DE, 16 * Gib, 32 * Gib)), 64 * Gib);

        ContextMemoryBudget.Budget budget = ContextMemoryBudget.ForCurrentResources(
            machine, profile, 12 * Gib, 64 * Gib, 0);

        Assert.Equal(0, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.Equal(0, budget.TotalBytes);
        Assert.Equal("text-cuda", budget.Backend);
        Assert.False(budget.PrimaryPoolIsSharedSystemMemory);
    }

    [Fact]
    public void Dedicated_gpu_credit_is_clamped_to_the_device_VRAM_total()
    {
        var machine = Machine(64 * Gib, cudaInstalled: true, vulkanInstalled: true);
        var profile = RuntimeHardwareProfileResolver.Resolve(
            machine, Inventory(Adapter("NVIDIA 16GB", 0x10DE, 16 * Gib, 32 * Gib)), 64 * Gib);

        ContextMemoryBudget.Budget budget = ContextMemoryBudget.ForCurrentResources(
            machine, profile, 12 * Gib, 64 * Gib, 40 * Gib);

        Assert.Equal(16 * Gib, budget.VramBytes);
        Assert.Equal(12 * Gib, budget.OffloadableRamBytes);
        Assert.Equal(28 * Gib, budget.TotalBytes);
    }

    [Fact]
    public void Cpu_budget_with_unknown_total_RAM_uses_only_the_live_budget()
    {
        ContextMemoryBudget.Budget budget = ContextMemoryBudget.For(
            Machine(0, vulkanInstalled: true), Inventory(), 5 * Gib, 32 * Gib);

        Assert.Equal(5 * Gib, budget.VramBytes);
        Assert.Equal(0, budget.OffloadableRamBytes);
        Assert.Equal(5 * Gib, budget.TotalBytes);
        Assert.Equal("cpu", budget.Backend);
    }

    [Fact]
    public void Null_gpu_telemetry_provider_is_rejected_on_the_live_admission_path()
    {
        var machine = Machine(64 * Gib, cudaInstalled: true);
        var profile = RuntimeHardwareProfileResolver.Resolve(machine, Inventory());

        Assert.Throws<ArgumentNullException>(() =>
            ContextMemoryBudget.ForCurrentResourcesAsync(
                machine, profile, 4 * Gib, 64 * Gib, null!, CancellationToken.None).GetAwaiter().GetResult());
    }
}
