using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Measured 2026-09-29, RTX 5080 16 GB: Qwen3.8-27B IQ4_XS stayed fully on the GPU at 32,768
/// context with no prefill/decode loss, while the coarse file-size estimate pinned coding to 8,192.
/// </summary>
public sealed class RuntimeContextChoiceTests
{
    private static ContextCapacityPlanner.ContextCapacity Capacity(long recommended) =>
        new(true, 262_144, recommended, recommended, 0, "test");

    [Fact]
    public void Known_geometry_lets_coding_use_what_actually_fits()
    {
        Assert.Equal(32768, RuntimePerformanceTuner.ChooseContext("coding", 16384, 0, Capacity(90_000)));
        Assert.Equal(20_480, RuntimePerformanceTuner.ChooseContext("coding", 16384, 0, Capacity(20_480)));
    }

    [Fact]
    public void Chat_keeps_its_latency_class_and_never_exceeds_capacity()
    {
        Assert.Equal(8192, RuntimePerformanceTuner.ChooseContext("chat", 8192, 0, Capacity(90_000)));
        Assert.Equal(6144, RuntimePerformanceTuner.ChooseContext("chat", 8192, 0, Capacity(6144)));
    }

    [Fact]
    public void Unknown_geometry_keeps_the_conservative_borderline_clamp()
    {
        Assert.Equal(8192, RuntimePerformanceTuner.ChooseContext("coding", 16384, 0, ContextCapacityPlanner.Unknown));
        Assert.Equal(16384, RuntimePerformanceTuner.ChooseContext("coding", 16384, 2L * 1024 * 1024 * 1024, ContextCapacityPlanner.Unknown));
    }
}
