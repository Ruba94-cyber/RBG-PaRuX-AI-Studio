using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BundledBaselineRuntimeBootstrapTests
{
    [Theory]
    [InlineData("Cuda13", "Vulkan,Cuda13")]
    [InlineData("Vulkan", "Vulkan")]
    [InlineData("None", "Vulkan")]
    public void Vulkan_baseline_is_always_installed_and_cuda13_is_added_only_for_supported_hardware(string target, string expected)
    {
        // Real clean install on an RTX 5080: only CUDA 13.4 was installed, and the media assistant (which runs on the
        // bundled Vulkan runtime) failed with "File not found ...llama.cpp-b10549-vulkan\llama-server.exe".
        var kind = System.Enum.Parse<BundledBaselineRuntimeKind>(target);

        Assert.Equal(expected, string.Join(",", BundledBaselineRuntimeBootstrap.BaselinesToInstall(kind)));
    }

    [Fact]
    public void Bootstrap_is_not_a_module_initializer_because_its_hardware_probe_deadlocks_there()
    {
        System.Reflection.MethodInfo initialize = typeof(BundledBaselineRuntimeBootstrap).GetMethod(
            "Initialize", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        Assert.NotNull(initialize);
        Assert.Null(System.Attribute.GetCustomAttribute(
            initialize, typeof(System.Runtime.CompilerServices.ModuleInitializerAttribute)));
    }

    [Theory]
    [InlineData(false, false, null, null, "None")]
    [InlineData(true, false, null, null, "Vulkan")]
    [InlineData(true, true, 12.0, "32.0.16.1714", "Cuda13")]
    [InlineData(true, true, 12.0, "32.0.15.5710", "None")]
    [InlineData(true, true, 8.9, "32.0.16.1714", "None")]
    [InlineData(true, true, null, "32.0.16.1714", "None")]
    public void Bundled_runtime_is_selected_only_for_the_supported_hardware(
        bool inventoryAvailable,
        bool hasNvidiaAdapter,
        double? computeCapability,
        string? driverVersion,
        string expected)
    {
        Assert.Equal(expected, BundledBaselineRuntimeBootstrap.SelectRuntimeTarget(
            inventoryAvailable, hasNvidiaAdapter, computeCapability, driverVersion).ToString());
    }
}
