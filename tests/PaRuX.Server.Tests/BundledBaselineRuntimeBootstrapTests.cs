using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BundledBaselineRuntimeBootstrapTests
{
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
