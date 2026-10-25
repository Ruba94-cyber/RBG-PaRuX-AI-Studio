using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Default DirectML adapter choice when the user/settings did not pin one: the non-software adapter
/// from an allow-listed vendor with the most dedicated video memory. The result is only a proposal
/// of a "vvvv:dddd" pin; the worker handshake still verifies the exact adapter on every spawn.
/// Two physically identical adapters (same vendor:device) cannot be told apart by a pin, so that
/// case yields no default instead of a silent guess.
/// </summary>
public static class WindowsMlDmlAdapterPolicy
{
    private static readonly HashSet<uint> AllowedVendors = new() { 0x10de, 0x1002, 0x8086 };

    public static string? SelectDefaultPin(GpuInventoryStatus? inventory)
    {
        if (inventory is not { Available: true }) return null;
        List<GpuAdapterStatus> candidates = inventory.Adapters
            .Where(a => !a.IsSoftware && AllowedVendors.Contains(a.VendorId) && a.DedicatedVideoMemoryBytes > 0)
            .OrderByDescending(a => a.DedicatedVideoMemoryBytes)
            .ThenBy(a => a.VendorId)
            .ThenBy(a => a.DeviceId)
            .ToList();
        if (candidates.Count == 0) return null;
        GpuAdapterStatus best = candidates[0];
        int identical = inventory.Adapters.Count(a => a.VendorId == best.VendorId && a.DeviceId == best.DeviceId);
        if (identical != 1) return null;
        return best.VendorId.ToString("x4") + ":" + best.DeviceId.ToString("x4");
    }
}
