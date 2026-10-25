using System.Collections.Generic;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComponentLifecycleHealthHonestyTests
{
    [Fact]
    public void Located_runtime_that_did_not_identify_itself_is_not_reported_as_ready()
    {
        SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(
            new List<ComponentStatus> { LocatedRuntime("unverified") });

        Assert.False(info.IsHealthy);
        Assert.Equal("Needs attention", info.HealthState);
        Assert.Equal("Check runtime status in Settings", info.NextAction);
        Assert.Equal("Needs attention · Check runtime status in Settings", info.OneLineSummary);
    }

    [Fact]
    public void Located_runtime_stays_ready_when_the_local_engine_reports_healthy()
    {
        SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(
            new List<ComponentStatus> { LocatedRuntime("unverified") },
            new LocalAiState { Healthy = true, ServerProcessPresent = true });

        Assert.True(info.IsHealthy);
        Assert.Equal("Ready", info.HealthState);
    }

    [Fact]
    public void Validated_located_runtime_is_ready()
    {
        SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(
            new List<ComponentStatus> { LocatedRuntime("validated") });

        Assert.True(info.IsHealthy);
        Assert.Equal("Ready", info.HealthState);
    }

    [Fact]
    public void Parux_managed_runtime_that_is_installed_but_stopped_stays_ready()
    {
        var managed = new ComponentStatus
        {
            Id = "llama-server-vulkan",
            DisplayName = "llama.cpp (Vulkan)",
            Ownership = ComponentOwnership.PARUX_MANAGED,
            Detected = true,
            Health = "installed",
            ValidationStatus = "unverified",
            Actions = new[] { "rollback", "skip" }
        };

        SimpleModeRuntimeHealthInfo info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(
            new List<ComponentStatus> { managed });

        Assert.True(info.IsHealthy);
        Assert.Equal("Ready", info.HealthState);
    }

    private static ComponentStatus LocatedRuntime(string validationStatus) => new()
    {
        Id = "llama-server-cuda",
        DisplayName = "llama.cpp (CUDA)",
        Ownership = ComponentOwnership.USER_MANAGED,
        Detected = true,
        Health = "detected",
        ValidationStatus = validationStatus,
        Path = "D:/fixtures/llama/llama-server.exe",
        Actions = new[] { "relocate", "remove" }
    };
}
