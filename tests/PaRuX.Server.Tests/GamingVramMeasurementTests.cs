using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The gaming hard-off reports what it unloaded. On a host with no usable GPU adapter that must say CPU, and
/// an unknown inventory must keep the GPU wording it always had, because absence is not proven by an error.
/// The suspended-game VRAM contract must carry "unknown" as null, never as a number.
/// </summary>
public sealed class GamingVramMeasurementTests
{
    [Fact]
    public void An_unloaded_text_model_is_described_as_cpu_when_no_usable_adapter_exists()
    {
        Assert.Equal("llama-server model unloaded (CPU only)",
            WorkloadOrchestrator.TextModelUnloadedLabel(GpuInventoryFixtures.Inventory()));
    }

    [Fact]
    public void A_software_renderer_alone_is_still_a_cpu_host()
    {
        Assert.Equal("llama-server model unloaded (CPU only)",
            WorkloadOrchestrator.TextModelUnloadedLabel(GpuInventoryFixtures.Inventory(GpuInventoryFixtures.SoftwareRenderer())));
    }

    [Fact]
    public void A_hardware_adapter_keeps_the_gpu_wording()
    {
        Assert.Equal("llama-server GPU model unloaded",
            WorkloadOrchestrator.TextModelUnloadedLabel(GpuInventoryFixtures.Inventory(GpuInventoryFixtures.IntegratedAmd())));
        Assert.Equal("llama-server GPU model unloaded",
            WorkloadOrchestrator.TextModelUnloadedLabel(GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia())));
    }

    [Fact]
    public void An_unavailable_inventory_keeps_the_gpu_wording_because_absence_is_not_proven()
    {
        Assert.Equal("llama-server GPU model unloaded",
            WorkloadOrchestrator.TextModelUnloadedLabel(GpuInventoryFixtures.Unavailable()));
    }

    [Fact]
    public void Unknown_suspended_game_vram_is_serialized_as_null_not_as_a_number()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        string unknown = JsonSerializer.Serialize(new GamingStatusDto { SuspendedVramMib = null }, options);
        string known = JsonSerializer.Serialize(new GamingStatusDto { SuspendedVramMib = 2048 }, options);

        Assert.Contains("\"suspendedVramMib\":null", unknown);
        Assert.Contains("\"suspendedVramMib\":2048", known);
    }
}
