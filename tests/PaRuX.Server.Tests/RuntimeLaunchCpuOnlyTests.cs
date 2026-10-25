using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// On a host with no usable GPU adapter the text launch used to pin --device Vulkan0 and all layers on the GPU,
/// and only retried on CPU after that launch failed. These tests pin the planned behaviour: CPU arguments are
/// built directly, GPU arguments are unchanged wherever a usable adapter or CUDA exists, and an unavailable
/// inventory never changes the launch.
/// </summary>
public sealed class RuntimeLaunchCpuOnlyTests
{
    [Fact]
    public async Task A_host_with_no_adapters_gets_cpu_arguments_built_directly()
    {
        GpuInventoryStatus inventory = GpuInventoryFixtures.Inventory();
        RuntimeLaunchOptimization optimization = await ResolveAsync(inventory);

        Assert.True(RuntimeLaunchOptimizer.HasNoUsableGpuAdapter(inventory));
        List<string> args = RuntimeLaunchOptimizer.BuildLaunchArguments(optimization.Profile, hasCuda: false, cpuOnly: true);

        Assert.Equal("0", ValueOf(args, "--gpu-layers"));
        Assert.DoesNotContain("--device", args);
        Assert.DoesNotContain("Vulkan0", args);
        Assert.DoesNotContain("--main-gpu", args);
        Assert.DoesNotContain("--split-mode", args);
        Assert.DoesNotContain("--fit", args);
    }

    [Fact]
    public async Task A_host_with_only_a_software_renderer_is_also_cpu_only()
    {
        GpuInventoryStatus inventory = GpuInventoryFixtures.Inventory(GpuInventoryFixtures.SoftwareRenderer());
        RuntimeLaunchOptimization optimization = await ResolveAsync(inventory);

        Assert.True(RuntimeLaunchOptimizer.HasNoUsableGpuAdapter(inventory));
        List<string> args = RuntimeLaunchOptimizer.BuildLaunchArguments(optimization.Profile, hasCuda: false, cpuOnly: true);

        Assert.Equal("0", ValueOf(args, "--gpu-layers"));
        Assert.DoesNotContain("Vulkan0", args);
    }

    [Fact]
    public async Task An_nvidia_adapter_keeps_the_vulkan_arguments_exactly_as_before()
    {
        GpuInventoryStatus inventory = GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia());
        RuntimeLaunchOptimization optimization = await ResolveAsync(inventory);

        Assert.False(RuntimeLaunchOptimizer.HasNoUsableGpuAdapter(inventory));
        List<string> args = RuntimeLaunchOptimizer.BuildLaunchArguments(optimization.Profile, hasCuda: false, cpuOnly: false);

        Assert.Equal(RuntimeLaunchOptimizer.BuildRuntimeArguments(optimization.Profile, hasCuda: false), args);
        Assert.Equal("all", ValueOf(args, "--gpu-layers"));
        Assert.Contains("Vulkan0", args);
    }

    [Fact]
    public async Task An_integrated_adapter_keeps_the_vulkan_path_because_that_device_works()
    {
        GpuInventoryStatus inventory = GpuInventoryFixtures.Inventory(GpuInventoryFixtures.IntegratedAmd());
        RuntimeLaunchOptimization optimization = await ResolveAsync(inventory);

        Assert.False(RuntimeLaunchOptimizer.HasNoUsableGpuAdapter(inventory));
        List<string> args = RuntimeLaunchOptimizer.BuildLaunchArguments(optimization.Profile, hasCuda: false, cpuOnly: false);

        Assert.Equal(RuntimeLaunchOptimizer.BuildRuntimeArguments(optimization.Profile, hasCuda: false), args);
        Assert.Contains("Vulkan0", args);
    }

    [Fact]
    public async Task Cuda_arguments_are_never_rewritten_even_when_the_caller_flags_cpu_only()
    {
        RuntimeLaunchOptimization optimization = await ResolveAsync(GpuInventoryFixtures.Inventory());

        List<string> cuda = RuntimeLaunchOptimizer.BuildRuntimeArguments(optimization.Profile, hasCuda: true);
        List<string> flagged = RuntimeLaunchOptimizer.BuildLaunchArguments(optimization.Profile, hasCuda: true, cpuOnly: true);

        Assert.Equal(cuda, flagged);
    }

    [Fact]
    public void An_unavailable_inventory_is_not_treated_as_proof_that_no_gpu_exists()
    {
        Assert.False(RuntimeLaunchOptimizer.HasNoUsableGpuAdapter(GpuInventoryFixtures.Unavailable()));
    }

    [Fact]
    public async Task Cpu_arguments_keep_the_context_window_and_the_thread_budget()
    {
        RuntimeLaunchOptimization optimization = await ResolveAsync(GpuInventoryFixtures.Inventory());

        List<string> gpuShaped = RuntimeLaunchOptimizer.BuildRuntimeArguments(optimization.Profile, hasCuda: false);
        List<string> cpu = RuntimeLaunchOptimizer.BuildLaunchArguments(optimization.Profile, hasCuda: false, cpuOnly: true);

        Assert.NotNull(ValueOf(cpu, "--ctx-size"));
        Assert.Equal(ValueOf(gpuShaped, "--ctx-size"), ValueOf(cpu, "--ctx-size"));
        Assert.NotNull(ValueOf(cpu, "--threads"));
    }

    private static Task<RuntimeLaunchOptimization> ResolveAsync(GpuInventoryStatus inventory) =>
        RuntimeLaunchOptimizer.ResolveAsync(
            modelPath: Path.Combine(Path.GetTempPath(), "parux-missing-model-" + Guid.NewGuid().ToString("N") + ".gguf"),
            workload: "chat",
            hasCuda: false,
            fallbackBatch: 512,
            fallbackUbatch: 128,
            fallbackCache: "q8_0",
            ct: CancellationToken.None,
            gpuInventory: inventory);

    private static string? ValueOf(IReadOnlyList<string> arguments, string flag)
    {
        List<string> list = arguments.ToList();
        int index = list.IndexOf(flag);
        return index < 0 || index + 1 >= list.Count ? null : list[index + 1];
    }
}
