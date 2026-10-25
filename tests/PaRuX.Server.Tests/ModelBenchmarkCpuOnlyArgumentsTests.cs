using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// llama-bench used to pin -dev Vulkan0 on every host without a CUDA card, including hosts with no GPU at
/// all, where that device does not exist. The argument builder is pure, so each host shape is pinned here.
/// </summary>
public sealed class ModelBenchmarkCpuOnlyArgumentsTests
{
    private const string Model = @"C:\models\example-q4.gguf";

    [Fact]
    public void A_host_with_no_usable_adapter_offloads_nothing_and_pins_no_device()
    {
        IReadOnlyList<string> args = LlamaBenchRunner.BuildArguments(Model, hasGpu: false, cpuOnly: true);

        Assert.Equal("0", ValueOf(args, "-ngl"));
        Assert.DoesNotContain("-dev", args);
        Assert.DoesNotContain("Vulkan0", args);
    }

    [Fact]
    public void A_vulkan_host_keeps_the_vulkan_device_and_full_offload()
    {
        IReadOnlyList<string> args = LlamaBenchRunner.BuildArguments(Model, hasGpu: false, cpuOnly: false);

        Assert.Equal("99", ValueOf(args, "-ngl"));
        Assert.Equal("Vulkan0", ValueOf(args, "-dev"));
    }

    [Fact]
    public void A_cuda_host_keeps_full_offload_without_a_device_pin()
    {
        IReadOnlyList<string> args = LlamaBenchRunner.BuildArguments(Model, hasGpu: true, cpuOnly: false);

        Assert.Equal("99", ValueOf(args, "-ngl"));
        Assert.DoesNotContain("-dev", args);
    }

    [Fact]
    public void The_model_and_the_measurement_shape_are_the_same_in_every_mode()
    {
        foreach ((bool hasGpu, bool cpuOnly) in new[] { (false, true), (false, false), (true, false) })
        {
            IReadOnlyList<string> args = LlamaBenchRunner.BuildArguments(Model, hasGpu, cpuOnly);

            Assert.Equal(Model, ValueOf(args, "-m"));
            Assert.Equal("0", ValueOf(args, "-p"));
            Assert.Equal("3", ValueOf(args, "-r"));
            Assert.Equal("json", ValueOf(args, "-o"));
        }
    }

    private static string? ValueOf(IReadOnlyList<string> arguments, string flag)
    {
        List<string> list = arguments.ToList();
        int index = list.IndexOf(flag);
        return index < 0 || index + 1 >= list.Count ? null : list[index + 1];
    }
}
