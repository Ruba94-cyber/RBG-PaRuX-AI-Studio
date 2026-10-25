using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The CPU-only launch decision is the one benchmarks and text launches use to choose between the Vulkan and CPU
/// argument sets. It takes its inventory from the caller, so a regression that hard-codes the decision, or ignores
/// the inventory, is caught on a host with no GPU.
/// </summary>
public sealed class CpuOnlyLaunchDecisionTests
{
    [Fact]
    public void A_host_with_no_usable_adapter_and_no_heavy_gpu_launches_on_the_cpu_and_reads_the_inventory_once()
    {
        int reads = 0;

        bool cpuOnly = RuntimeLaunchOptimizer.ResolveCpuOnly(hasHeavyGpu: false, () =>
        {
            reads++;
            return GpuInventoryFixtures.Inventory();
        });

        Assert.True(cpuOnly);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void A_software_only_adapter_is_still_a_cpu_only_host()
    {
        Assert.True(RuntimeLaunchOptimizer.ResolveCpuOnly(
            hasHeavyGpu: false, () => GpuInventoryFixtures.Inventory(GpuInventoryFixtures.SoftwareRenderer())));
    }

    [Fact]
    public void An_integrated_vulkan_adapter_keeps_the_gpu_launch_rather_than_going_to_the_cpu()
    {
        Assert.False(RuntimeLaunchOptimizer.ResolveCpuOnly(
            hasHeavyGpu: false, () => GpuInventoryFixtures.Inventory(GpuInventoryFixtures.IntegratedAmd())));
    }

    [Fact]
    public void A_heavy_gpu_is_never_cpu_only_and_the_inventory_is_not_read()
    {
        int reads = 0;

        bool cpuOnly = RuntimeLaunchOptimizer.ResolveCpuOnly(hasHeavyGpu: true, () =>
        {
            reads++;
            return GpuInventoryFixtures.Inventory();
        });

        Assert.False(cpuOnly);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void An_unavailable_inventory_is_not_proof_of_a_cpu_only_host()
    {
        Assert.False(RuntimeLaunchOptimizer.ResolveCpuOnly(hasHeavyGpu: false, GpuInventoryFixtures.Unavailable));
    }
}
