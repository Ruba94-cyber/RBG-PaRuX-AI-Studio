using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BlackwellRuntimeRacePolicyCharacterizationTests
{
    [Theory]
    [InlineData(11.9)]
    [InlineData(8.9)]
    [InlineData(0.0)]
    public void BuildFlavors_non_blackwell_returns_only_upstream_default(double computeCapability)
    {
        IReadOnlyList<RuntimeBuildFlavor> flavors = BlackwellRuntimeRacePolicy.BuildFlavors(
            new[] { Gpu(computeCapability) }, paRuXNativeBuildAvailable: true, includeExperimentalThirdParty: true);

        RuntimeBuildFlavor flavor = Assert.Single(flavors);
        Assert.Equal("upstream-default", flavor.Id);
        Assert.Equal("Upstream default", flavor.DisplayName);
        Assert.Equal(RuntimeBuildTrust.UpstreamVerified, flavor.Trust);
        Assert.Null(flavor.CudaArchitecture);
        Assert.Empty(flavor.Environment);
        Assert.False(flavor.BlackwellOnly);
        Assert.True(flavor.AllowAutomaticPromotion);
    }

    [Fact]
    public void BuildFlavors_blackwell_without_flags_returns_two_flavors()
    {
        IReadOnlyList<RuntimeBuildFlavor> flavors = BlackwellRuntimeRacePolicy.BuildFlavors(
            new[] { Gpu(12.0) }, paRuXNativeBuildAvailable: false);

        Assert.Equal(2, flavors.Count);
        Assert.Equal("upstream-default", flavors[0].Id);
        RuntimeBuildFlavor safe = flavors[1];
        Assert.Equal("blackwell-safe-no-graphs", safe.Id);
        Assert.Equal("sm_120", safe.CudaArchitecture);
        Assert.Equal(RuntimeBuildTrust.UpstreamVerified, safe.Trust);
        Assert.True(safe.BlackwellOnly);
        Assert.True(safe.AllowAutomaticPromotion);
        Assert.Equal("1", safe.Environment[BlackwellRuntimeRacePolicy.DisableCudaGraphsVariable]);
    }

    [Fact]
    public void BuildFlavors_blackwell_with_all_flags_returns_four_flavors_in_order()
    {
        IReadOnlyList<RuntimeBuildFlavor> flavors = BlackwellRuntimeRacePolicy.BuildFlavors(
            new[] { Gpu(12.0) }, paRuXNativeBuildAvailable: true, includeExperimentalThirdParty: true);

        Assert.Equal(4, flavors.Count);
        Assert.Equal("upstream-default", flavors[0].Id);
        Assert.Equal("blackwell-safe-no-graphs", flavors[1].Id);
        Assert.Equal("parux-blackwell-native", flavors[2].Id);
        Assert.Equal("experimental-third-party-sm120", flavors[3].Id);
    }

    [Fact]
    public void BuildFlavors_native_flavor_uses_pa_rux_trust_and_120a_architecture()
    {
        RuntimeBuildFlavor native = Assert.Single(BlackwellRuntimeRacePolicy.BuildFlavors(
            new[] { Gpu(12.0) }, paRuXNativeBuildAvailable: true)
            .Where(flavor => flavor.Id == "parux-blackwell-native"));

        Assert.Equal(RuntimeBuildTrust.PaRuXVerified, native.Trust);
        Assert.Equal("120a-real", native.CudaArchitecture);
        Assert.Empty(native.Environment);
        Assert.True(native.BlackwellOnly);
        Assert.True(native.AllowAutomaticPromotion);
    }

    [Fact]
    public void CanAutoPromote_upstream_default_requires_stability_and_quality()
    {
        RuntimeBuildFlavor flavor = BlackwellRuntimeRacePolicy.BuildFlavors(
            new[] { Gpu(8.9) }, false)[0];

        Assert.True(BlackwellRuntimeRacePolicy.CanAutoPromote(flavor, stabilityPassed: true, qualityPassed: true));
        Assert.False(BlackwellRuntimeRacePolicy.CanAutoPromote(flavor, stabilityPassed: false, qualityPassed: true));
        Assert.False(BlackwellRuntimeRacePolicy.CanAutoPromote(flavor, stabilityPassed: true, qualityPassed: false));
    }

    [Fact]
    public void CanAutoPromote_null_flavor_throws_argument_null()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BlackwellRuntimeRacePolicy.CanAutoPromote(null!, stabilityPassed: true, qualityPassed: true));
    }

    [Fact]
    public void ApplyEnvironment_sets_only_flavor_variables_on_start_info()
    {
        var start = new ProcessStartInfo { WorkingDirectory = "." };
        start.Environment["GGML_CUDA_DISABLE_GRAPHS"] = "0";

        BlackwellRuntimeRacePolicy.ApplyEnvironment(start, BlackwellRuntimeRacePolicy.BuildFlavors(
            new[] { Gpu(12.0) }, false)[1]);

        Assert.Equal("1", start.Environment[BlackwellRuntimeRacePolicy.DisableCudaGraphsVariable]);

        BlackwellRuntimeRacePolicy.ApplyEnvironment(start, BlackwellRuntimeRacePolicy.BuildFlavors(
            new[] { Gpu(8.9) }, false)[0]);

        Assert.Equal("1", start.Environment[BlackwellRuntimeRacePolicy.DisableCudaGraphsVariable]);
    }

    [Theory]
    [InlineData(12.0)]
    [InlineData(13.5)]
    public void BuildFlavors_accepts_any_compute_capability_at_or_above_12(double computeCapability)
    {
        IReadOnlyList<RuntimeBuildFlavor> flavors = BlackwellRuntimeRacePolicy.BuildFlavors(
            new[] { Gpu(computeCapability) }, paRuXNativeBuildAvailable: true, includeExperimentalThirdParty: true);

        Assert.Equal(4, flavors.Count);
    }

    private static GpuPerformanceTelemetryDto Gpu(double computeCapability) => new()
    {
        Index = 0,
        Name = "GPU",
        ComputeCapability = computeCapability,
        SampledUtc = DateTime.UtcNow
    };
}
