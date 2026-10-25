using PaRuX.Contracts;
using PaRuX.Server;

namespace PaRuX.Server.Tests;

/// <summary>
/// Hermetic GPU inventories for the CPU-only and NVIDIA-requirement tests. Nothing here touches DXGI, so the
/// results do not depend on the host's hardware.
/// </summary>
internal static class GpuInventoryFixtures
{
    internal const long Gib = 1024L * 1024 * 1024;

    internal static GpuInventoryStatus Inventory(params GpuAdapterStatus[] adapters) =>
        new GpuInventoryService(new FakeEnumerator(adapters), () => true).GetStatus();

    internal static GpuInventoryStatus Unavailable() =>
        new GpuInventoryService(new FakeEnumerator(Array.Empty<GpuAdapterStatus>()), () => false).GetStatus();

    internal static GpuAdapterStatus Nvidia(long dedicatedVideoMemoryBytes = 8 * Gib) => new()
    {
        Index = 0,
        Name = "NVIDIA Test Adapter",
        VendorId = GpuInventoryService.NvidiaVendorId,
        DeviceId = 0x2684,
        DedicatedVideoMemoryBytes = dedicatedVideoMemoryBytes
    };

    /// <summary>The integrated Radeon of the Vulkan reference machine: a usable hardware adapter.</summary>
    internal static GpuAdapterStatus IntegratedAmd() => new()
    {
        Index = 0,
        Name = "AMD Radeon 780M Graphics",
        VendorId = 0x1002,
        DeviceId = 0x15BF,
        DedicatedVideoMemoryBytes = 512L * 1024 * 1024,
        SharedSystemMemoryBytes = 16 * Gib
    };

    /// <summary>What DXGI reports on a host with no GPU at all.</summary>
    internal static GpuAdapterStatus SoftwareRenderer() => new()
    {
        Index = 0,
        Name = "Microsoft Basic Render Driver",
        VendorId = 0x1414,
        DeviceId = 0x008C,
        IsSoftware = true
    };

    private sealed class FakeEnumerator : IGpuAdapterEnumerator
    {
        private readonly GpuAdapterStatus[] _adapters;
        public FakeEnumerator(GpuAdapterStatus[] adapters) => _adapters = adapters;
        public IReadOnlyList<GpuAdapterStatus> Enumerate() => _adapters;
    }
}
