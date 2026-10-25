using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BlackwellRuntimeRacePolicyTests
{
    [Fact]
    public void Blackwell_adds_safe_no_graphs_experiment()
    {
        IReadOnlyList<RuntimeBuildFlavor> flavors = BlackwellRuntimeRacePolicy.BuildFlavors(
            new[] { Gpu(12.0) }, paRuXNativeBuildAvailable: false);

        RuntimeBuildFlavor safe = Assert.Single(flavors.Where(flavor => flavor.Id == "blackwell-safe-no-graphs"));
        Assert.Equal("1", safe.Environment[BlackwellRuntimeRacePolicy.DisableCudaGraphsVariable]);
        Assert.True(safe.AllowAutomaticPromotion);
    }

    [Fact]
    public void Non_blackwell_only_gets_generic_upstream_flavor()
    {
        IReadOnlyList<RuntimeBuildFlavor> flavors = BlackwellRuntimeRacePolicy.BuildFlavors(
            new[] { Gpu(8.9) }, paRuXNativeBuildAvailable: true, includeExperimentalThirdParty: true);

        RuntimeBuildFlavor flavor = Assert.Single(flavors);
        Assert.Equal("upstream-default", flavor.Id);
    }

    [Fact]
    public void Third_party_build_can_never_auto_promote()
    {
        RuntimeBuildFlavor thirdParty = Assert.Single(BlackwellRuntimeRacePolicy.BuildFlavors(
            new[] { Gpu(12.0) }, false, includeExperimentalThirdParty: true)
            .Where(flavor => flavor.Trust == RuntimeBuildTrust.ExperimentalThirdParty));

        Assert.False(BlackwellRuntimeRacePolicy.CanAutoPromote(thirdParty, stabilityPassed: true, qualityPassed: true));
    }

    [Fact]
    public void Verified_native_build_still_requires_quality_and_stability()
    {
        RuntimeBuildFlavor native = Assert.Single(BlackwellRuntimeRacePolicy.BuildFlavors(
            new[] { Gpu(12.0) }, paRuXNativeBuildAvailable: true)
            .Where(flavor => flavor.Id == "parux-blackwell-native"));

        Assert.False(BlackwellRuntimeRacePolicy.CanAutoPromote(native, stabilityPassed: false, qualityPassed: true));
        Assert.False(BlackwellRuntimeRacePolicy.CanAutoPromote(native, stabilityPassed: true, qualityPassed: false));
        Assert.True(BlackwellRuntimeRacePolicy.CanAutoPromote(native, stabilityPassed: true, qualityPassed: true));
    }

    private static GpuPerformanceTelemetryDto Gpu(double computeCapability) => new()
    {
        Index = 0,
        Name = "GPU",
        ComputeCapability = computeCapability,
        SampledUtc = DateTime.UtcNow
    };
}
