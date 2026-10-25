using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Drives the real text-launch planner (StartTextAsync) through an injected GPU inventory and stops at the launch seam,
/// which records the argument list the planner built. This is the call-site check that the CPU-only decision reaches the
/// arguments: a host with no usable adapter launches with no GPU layers and no device pin, a host with an integrated
/// adapter keeps the Vulkan launch, and an unavailable inventory is not read as a CPU-only host.
/// </summary>
public sealed class RuntimeLaunchPlannerCpuOnlyTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-planner-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;
    private readonly IDisposable _state;

    public RuntimeLaunchPlannerCpuOnlyTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        // The planner requires the runtime and the model to exist before it builds the launch. The launch itself is intercepted.
        Directory.CreateDirectory(LocalAiPaths.RuntimeVulkanPath);
        File.WriteAllBytes(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable), new byte[16]);
        Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.SmallModel)!);
        File.WriteAllBytes(LocalAiPaths.SmallModel, new byte[16]);
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task An_empty_inventory_launches_on_the_cpu_with_no_gpu_layers_and_no_device_pin()
    {
        List<IReadOnlyList<string>> launches = await PlanTextLaunchAsync(() => GpuInventoryFixtures.Inventory());

        // The CPU attempt is final, so the planner makes exactly one launch and no GPU attempt.
        Assert.Single(launches);
        Assert.Equal("0", ValueOf(launches[0], "--gpu-layers"));
        Assert.DoesNotContain("--device", launches[0]);
        Assert.DoesNotContain("Vulkan0", launches[0]);
    }

    [Fact]
    public async Task An_integrated_vulkan_adapter_launches_with_full_offload_on_vulkan0()
    {
        List<IReadOnlyList<string>> launches = await PlanTextLaunchAsync(
            () => GpuInventoryFixtures.Inventory(GpuInventoryFixtures.IntegratedAmd()));

        Assert.NotEmpty(launches);
        Assert.Equal("all", ValueOf(launches[0], "--gpu-layers"));
        Assert.Contains("Vulkan0", launches[0]);
    }

    [Fact]
    public async Task An_unavailable_inventory_is_not_read_as_a_cpu_only_host()
    {
        List<IReadOnlyList<string>> launches = await PlanTextLaunchAsync(GpuInventoryFixtures.Unavailable);

        Assert.NotEmpty(launches);
        Assert.NotEqual("0", ValueOf(launches[0], "--gpu-layers"));
        Assert.Contains("Vulkan0", launches[0]);
    }

    /// <summary>Runs the planner once with a fixed inventory and returns every argument list it tried to launch.</summary>
    private static async Task<List<IReadOnlyList<string>>> PlanTextLaunchAsync(Func<GpuInventoryStatus> inventory)
    {
        var launches = new List<IReadOnlyList<string>>();
        // A zero VRAM override keeps the planner off the GPU probe, so the inventory alone decides the CPU-only question.
        var manager = new LocalAiProcessManager(0)
        {
            InventoryProbe = inventory,
            LaunchInterceptForTests = (_, arguments) =>
            {
                launches.Add(arguments);
                return new LocalAiProcessManager.RuntimeResult(false, "intercepted before launch");
            }
        };

        LocalAiProcessManager.RuntimeResult result = await manager.StartTextAsync(CancellationToken.None);

        Assert.False(result.Success);
        return launches;
    }

    private static string? ValueOf(IReadOnlyList<string> arguments, string flag)
    {
        List<string> list = arguments.ToList();
        int index = list.IndexOf(flag);
        return index < 0 || index + 1 >= list.Count ? null : list[index + 1];
    }
}
