using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>The text runtime build a heavy or light launch runs.</summary>
internal enum TextRuntimeBackend
{
    Cuda,
    Vulkan
}

/// <summary>
/// The launch tier and backend chosen for one text start. <see cref="HeavyGpu"/> selects the large model profile, and
/// <see cref="Backend"/> selects the build. <see cref="VramMib"/> and <see cref="VramSource"/> say where the figure came from.
/// </summary>
internal sealed record TextLaunchTier(bool HeavyGpu, TextRuntimeBackend Backend, long VramMib, string VramSource);

/// <summary>
/// Decides the launch tier and backend from one hardware inventory. nvidia-smi is the only VRAM source for an NVIDIA device, so an
/// NVIDIA device whose figure nvidia-smi did not report keeps the light tier exactly as before. DXGI is the source for every other adapter.
/// A heavy tier needs the existing bar on one device: nvidia-smi MiB for an NVIDIA probe, and the DXGI byte bar for every other
/// adapter, the same two thresholds the rest of the process manager already uses. CUDA is used only for an NVIDIA device, so an
/// AMD or Intel card that has the VRAM runs the Vulkan build instead of being held to the 4B profile. Pure: the inventory and the
/// probe figure are plain values, so the decision is testable without DXGI or nvidia-smi.
/// </summary>
internal static class TextLaunchTierSelector
{
    internal const uint NvidiaVendorId = 0x10DE;
    internal const string NvidiaSmiSource = "nvidia-smi";
    internal const string DxgiSource = "dxgi";
    internal const string NoneSource = "none";

    private const long BytesPerMib = 1024L * 1024L;

    /// <param name="inventory">The DXGI inventory. An unavailable inventory contributes no adapters.</param>
    /// <param name="nvidiaSmiMib">The nvidia-smi total for the NVIDIA device, or 0 when that probe reported nothing.</param>
    internal static TextLaunchTier Select(GpuInventoryStatus inventory, int nvidiaSmiMib)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        if (nvidiaSmiMib >= LocalAiProcessManager.HeavyGpuVramThresholdMib)
            return new TextLaunchTier(true, TextRuntimeBackend.Cuda, nvidiaSmiMib, NvidiaSmiSource);

        List<GpuAdapterStatus> hardware = inventory.Available
            ? inventory.Adapters.Where(adapter => !adapter.IsSoftware).ToList()
            : [];

        // A non-NVIDIA adapter with the VRAM runs the Vulkan build. This is the case the old single-probe tier missed.
        GpuAdapterStatus? heavyOther = hardware
            .Where(adapter => adapter.VendorId != NvidiaVendorId && IsHeavyDxgi(adapter))
            .OrderByDescending(MebibytesOf)
            .FirstOrDefault();
        if (heavyOther is not null)
            return new TextLaunchTier(true, TextRuntimeBackend.Vulkan, MebibytesOf(heavyOther), DxgiSource);

        // Light tier: report the largest figure this inventory knows, so the status can say what was seen. An NVIDIA device
        // that nvidia-smi reported keeps that figure, because nvidia-smi was trusted over DXGI for it.
        long largestOther = hardware.Where(adapter => adapter.VendorId != NvidiaVendorId).Select(MebibytesOf).DefaultIfEmpty(0).Max();
        if (nvidiaSmiMib > 0 && nvidiaSmiMib >= largestOther)
            return new TextLaunchTier(false, TextRuntimeBackend.Vulkan, nvidiaSmiMib, NvidiaSmiSource);
        if (largestOther > 0)
            return new TextLaunchTier(false, TextRuntimeBackend.Vulkan, largestOther, DxgiSource);

        long largestNvidia = hardware.Where(adapter => adapter.VendorId == NvidiaVendorId).Select(MebibytesOf).DefaultIfEmpty(0).Max();
        return largestNvidia > 0
            ? new TextLaunchTier(false, TextRuntimeBackend.Vulkan, largestNvidia, DxgiSource)
            : new TextLaunchTier(false, TextRuntimeBackend.Vulkan, 0, NoneSource);
    }

    private static bool IsHeavyDxgi(GpuAdapterStatus adapter) =>
        Math.Max(0, adapter.DedicatedVideoMemoryBytes) >= LocalAiProcessManager.HeavyGpuDedicatedVramThresholdBytes;

    private static long MebibytesOf(GpuAdapterStatus adapter) => Math.Max(0, adapter.DedicatedVideoMemoryBytes) / BytesPerMib;
}
