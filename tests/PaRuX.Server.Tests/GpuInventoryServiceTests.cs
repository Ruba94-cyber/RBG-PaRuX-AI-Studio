using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Hermetic coverage for the multi-GPU inventory. DXGI itself is a Windows hardware API
/// and is live-verified on the development machine; unit tests inject the enumerator/platform seam
/// so aggregate semantics and failure honesty do not depend on whichever GPU a CI runner happens
/// to have.</summary>
public sealed class GpuInventoryServiceTests
{
    [Fact]
    public void Inventory_keeps_each_adapter_and_computes_separate_memory_totals()
    {
        const long gib = 1024L * 1024 * 1024;
        var adapters = new[]
        {
            new GpuAdapterStatus
            {
                Index = 0, Name = "Discrete A", VendorId = 0x10DE, DeviceId = 1,
                DedicatedVideoMemoryBytes = 16 * gib,
                DedicatedSystemMemoryBytes = 256 * 1024 * 1024,
                SharedSystemMemoryBytes = 8 * gib
            },
            new GpuAdapterStatus
            {
                Index = 1, Name = "Integrated B", VendorId = 0x8086, DeviceId = 2,
                DedicatedVideoMemoryBytes = 512 * 1024 * 1024,
                DedicatedSystemMemoryBytes = 0,
                SharedSystemMemoryBytes = 16 * gib
            }
        };
        var service = new GpuInventoryService(new FakeEnumerator(adapters), () => true);

        GpuInventoryStatus status = service.GetStatus();

        Assert.True(status.Available);
        Assert.Equal("dxgi", status.Source);
        Assert.Equal(2, status.Adapters.Count);
        Assert.Equal(16 * gib + 512 * 1024 * 1024, status.TotalDedicatedVideoMemoryBytes);
        Assert.Equal(256 * 1024 * 1024, status.TotalDedicatedSystemMemoryBytes);
        Assert.Equal(24 * gib, status.TotalSharedSystemMemoryBytes);
        Assert.Equal(16 * gib, status.LargestDedicatedVideoMemoryBytes);
        Assert.Equal("Discrete A", status.Adapters[0].Name);
        Assert.Equal("Integrated B", status.Adapters[1].Name);
    }

    [Fact]
    public void Inventory_does_not_conflate_software_adapters_with_missing_hardware()
    {
        var service = new GpuInventoryService(new FakeEnumerator(new[]
        {
            new GpuAdapterStatus { Index = 0, Name = "Microsoft Basic Render Driver", IsSoftware = true }
        }), () => true);

        GpuInventoryStatus status = service.GetStatus();

        Assert.True(status.Available);
        Assert.Single(status.Adapters);
        Assert.True(status.Adapters[0].IsSoftware);
    }

    [Fact]
    public void Inventory_reports_unsupported_platform_without_attempting_dxgi()
    {
        var enumerator = new FakeEnumerator(Array.Empty<GpuAdapterStatus>());
        var service = new GpuInventoryService(enumerator, () => false);

        GpuInventoryStatus status = service.GetStatus();

        Assert.False(status.Available);
        Assert.Equal("unsupported-platform", status.Source);
        Assert.Equal(0, enumerator.CallCount);
    }

    [Fact]
    public void Inventory_reports_dxgi_failure_instead_of_fabricating_an_empty_success()
    {
        var service = new GpuInventoryService(new ThrowingEnumerator("simulated DXGI failure"), () => true);

        GpuInventoryStatus status = service.GetStatus();

        Assert.False(status.Available);
        Assert.Equal("dxgi-error", status.Source);
        Assert.Contains("simulated DXGI failure", status.Error);
        Assert.Empty(status.Adapters);
    }

    [Fact]
    public void Aggregate_clamps_negative_provider_values_and_saturates_pathological_sums()
    {
        GpuInventoryStatus status = GpuInventoryService.BuildStatus(new[]
        {
            new GpuAdapterStatus
            {
                Index = 0, Name = "bad provider values",
                DedicatedVideoMemoryBytes = -1,
                DedicatedSystemMemoryBytes = long.MaxValue,
                SharedSystemMemoryBytes = long.MaxValue
            },
            new GpuAdapterStatus
            {
                Index = 1, Name = "second",
                DedicatedVideoMemoryBytes = 2,
                DedicatedSystemMemoryBytes = 100,
                SharedSystemMemoryBytes = 100
            }
        }, "test");

        Assert.Equal(0, status.Adapters[0].DedicatedVideoMemoryBytes);
        Assert.Equal(long.MaxValue, status.TotalDedicatedSystemMemoryBytes);
        Assert.Equal(long.MaxValue, status.TotalSharedSystemMemoryBytes);
    }

    private sealed class FakeEnumerator : IGpuAdapterEnumerator
    {
        private readonly IReadOnlyList<GpuAdapterStatus> _adapters;
        public FakeEnumerator(IReadOnlyList<GpuAdapterStatus> adapters) => _adapters = adapters;
        public int CallCount { get; private set; }
        public IReadOnlyList<GpuAdapterStatus> Enumerate()
        {
            CallCount++;
            return _adapters;
        }
    }

    private sealed class ThrowingEnumerator : IGpuAdapterEnumerator
    {
        private readonly string _message;
        public ThrowingEnumerator(string message) => _message = message;
        public IReadOnlyList<GpuAdapterStatus> Enumerate() => throw new InvalidOperationException(_message);
    }
}

public sealed class GpuInventoryRuntimeApiTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public GpuInventoryRuntimeApiTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Runtime_status_keeps_legacy_fields_and_adds_gpu_inventory_shape()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/runtime/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;

        // Existing API surface stays present.
        Assert.True(root.TryGetProperty("backends", out _));
        Assert.True(root.TryGetProperty("gpuDetected", out _));
        Assert.True(root.TryGetProperty("vramTotalMib", out _));
        Assert.True(root.TryGetProperty("ramTotalBytes", out _));

        // New additive hardware inventory.
        Assert.True(root.TryGetProperty("gpuAdapters", out JsonElement adapters));
        Assert.Equal(JsonValueKind.Array, adapters.ValueKind);
        Assert.True(root.TryGetProperty("gpuDedicatedVideoMemoryTotalBytes", out _));
        Assert.True(root.TryGetProperty("gpuDedicatedSystemMemoryTotalBytes", out _));
        Assert.True(root.TryGetProperty("gpuSharedSystemMemoryTotalBytes", out _));
        Assert.True(root.TryGetProperty("gpuLargestDedicatedVideoMemoryBytes", out _));
        Assert.True(root.TryGetProperty("gpuInventoryAvailable", out _));
        Assert.True(root.TryGetProperty("gpuInventorySource", out _));
        Assert.True(root.TryGetProperty("acceleratorProfile", out JsonElement profile));
        Assert.Equal(JsonValueKind.Object, profile.ValueKind);
        Assert.True(profile.TryGetProperty("gpuPresent", out _));
        Assert.True(profile.TryGetProperty("acceleratorEligible", out _));
        Assert.True(profile.TryGetProperty("backend", out _));
        Assert.True(profile.TryGetProperty("dedicatedVideoMemoryBytes", out _));
        Assert.True(profile.TryGetProperty("sharedSystemMemoryLimitBytes", out _));
        Assert.True(profile.TryGetProperty("usableSystemRamBytes", out _));
        Assert.True(profile.TryGetProperty("sharedMemoryPrimary", out _));
    }
}
