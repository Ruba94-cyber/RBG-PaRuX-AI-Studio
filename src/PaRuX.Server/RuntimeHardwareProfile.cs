using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// One normalized hardware view for routing and memory decisions. DXGI answers which physical
/// adapters exist, while RuntimeManagerStatus.Backends answers which PaRuX text runtimes are
/// actually installed. Keeping those facts separate prevents a Radeon/Intel Vulkan device from
/// being mistaken for CPU-only merely because the legacy NVIDIA probe reports no CUDA-class VRAM.
/// </summary>
public readonly record struct RuntimeHardwareProfile(
    bool GpuPresent,
    bool AcceleratorEligible,
    string Backend,
    string Vendor,
    string? AdapterName,
    long DedicatedVideoMemoryBytes,
    long SharedSystemMemoryLimitBytes,
    long SystemRamBytes,
    bool SharedMemoryPrimary,
    string InventorySource)
{
    public bool UsesSharedSystemMemoryAsPrimary => AcceleratorEligible && SharedMemoryPrimary;

    public long UsableSystemRamBytes => RuntimeHardwareProfileResolver.ConservativeUsableSystemRamBytes(SystemRamBytes);
}

public static class RuntimeHardwareProfileResolver
{
    private const uint NvidiaVendorId = 0x10DE;
    private const uint AmdVendorId = 0x1002;
    private const uint IntelVendorId = 0x8086;
    private const long IntegratedDedicatedMemoryCeilingBytes = 2L * 1024 * 1024 * 1024;
    private const long MeaningfulSharedMemoryBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan InventoryCacheDuration = TimeSpan.FromSeconds(5);
    private static readonly object InventoryGate = new();
    private static readonly GpuInventoryService SharedInventory = new();
    private static GpuInventoryStatus? _cachedInventory;
    private static DateTime _cachedInventoryUtc;

    /// <summary>
    /// Production resolver. DXGI enumeration is cached briefly so chat/context/routing hot paths do
    /// not repeatedly enumerate adapters, while still allowing an eGPU attach/detach to be noticed
    /// without restarting PaRuX.
    /// </summary>
    public static RuntimeHardwareProfile Resolve(RuntimeManagerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return Resolve(status, GetCachedInventory());
    }

    /// <summary>Deterministic overload used by tests and callers that already own an inventory snapshot.</summary>
    public static RuntimeHardwareProfile Resolve(RuntimeManagerStatus status, GpuInventoryStatus? inventory)
    {
        ArgumentNullException.ThrowIfNull(status);
        return Resolve(status, inventory, status.RamTotalBytes);
    }

    internal static RuntimeHardwareProfile Resolve(RuntimeManagerStatus status, long systemRamBytes)
    {
        ArgumentNullException.ThrowIfNull(status);
        return Resolve(status, GetCachedInventory(), systemRamBytes);
    }

    internal static RuntimeHardwareProfile Resolve(
        RuntimeManagerStatus status,
        GpuInventoryStatus? inventory,
        long systemRamBytes)
    {
        ArgumentNullException.ThrowIfNull(status);
        inventory ??= new GpuInventoryStatus { Available = false, Source = "unavailable" };

        List<GpuAdapterStatus> hardwareAdapters = inventory.Available
            ? inventory.Adapters
                .Where(adapter => !adapter.IsSoftware)
                .Where(adapter => adapter.DedicatedVideoMemoryBytes > 0 || adapter.SharedSystemMemoryBytes > 0)
                .ToList()
            : new List<GpuAdapterStatus>();

        bool cudaInstalled = status.Backends.Any(backend =>
            string.Equals(backend.Backend, "text-cuda", StringComparison.OrdinalIgnoreCase) && backend.Installed);
        bool vulkanInstalled = status.Backends.Any(backend =>
            string.Equals(backend.Backend, "text-vulkan", StringComparison.OrdinalIgnoreCase) && backend.Installed);

        GpuAdapterStatus? selected = null;
        string backendName = "cpu";

        // CUDA is only eligible on an NVIDIA adapter. The old >=12 GiB threshold was a product
        // profile choice, not a definition of whether a GPU exists or can execute local inference.
        if (cudaInstalled)
        {
            selected = hardwareAdapters
                .Where(adapter => adapter.VendorId == NvidiaVendorId)
                .OrderByDescending(adapter => adapter.DedicatedVideoMemoryBytes)
                .ThenByDescending(adapter => adapter.SharedSystemMemoryBytes)
                .FirstOrDefault();
            if (selected != null) backendName = "text-cuda";
        }

        // Vulkan is vendor-neutral. Prefer a genuinely discrete adapter, then the largest
        // shared-memory aperture. This avoids selecting a tiny iGPU reservation merely because it
        // is non-zero when a real discrete Vulkan adapter is also present.
        if (selected == null && vulkanInstalled)
        {
            selected = hardwareAdapters
                .OrderByDescending(adapter => IsLikelyDiscrete(adapter))
                .ThenByDescending(adapter => adapter.DedicatedVideoMemoryBytes)
                .ThenByDescending(adapter => adapter.SharedSystemMemoryBytes)
                .FirstOrDefault();
            if (selected != null) backendName = "text-vulkan";
        }

        // Preserve a fail-soft path on hosts where DXGI inventory is unavailable but the legacy
        // NVIDIA probe observed physical VRAM. GpuDetected historically means a >=12 GiB CUDA
        // product tier, not physical presence. Memory remains observable even if no text backend
        // is installed; it must not be treated as executable capacity in that case.
        if (selected == null && !inventory.Available && status.VramTotalMib > 0)
        {
            long legacyDedicated = checked((long)status.VramTotalMib * 1024 * 1024);
            bool legacyBackendEligible = cudaInstalled || vulkanInstalled;
            return new RuntimeHardwareProfile(
                GpuPresent: true,
                AcceleratorEligible: legacyBackendEligible,
                Backend: cudaInstalled ? "text-cuda" : vulkanInstalled ? "text-vulkan" : "cpu",
                Vendor: "nvidia-legacy",
                AdapterName: null,
                DedicatedVideoMemoryBytes: legacyDedicated,
                SharedSystemMemoryLimitBytes: 0,
                SystemRamBytes: Math.Max(0, systemRamBytes),
                SharedMemoryPrimary: false,
                InventorySource: inventory.Source);
        }

        // Keep physical identity/capacity available to UI and diagnostics even when there is no
        // compatible installed runtime. Runtime consumers must gate all capacity on eligibility.
        GpuAdapterStatus? profileAdapter = selected ?? hardwareAdapters
            .OrderByDescending(IsLikelyDiscrete)
            .ThenByDescending(adapter => adapter.DedicatedVideoMemoryBytes)
            .ThenByDescending(adapter => adapter.SharedSystemMemoryBytes)
            .FirstOrDefault();

        long ram = Math.Max(0, systemRamBytes);
        long dedicated = profileAdapter == null ? 0 : Math.Max(0, profileAdapter.DedicatedVideoMemoryBytes);
        long sharedLimit = profileAdapter == null ? 0 : Math.Max(0, profileAdapter.SharedSystemMemoryBytes);
        if (ram > 0 && sharedLimit > ram) sharedLimit = ram;

        bool sharedMemoryPrimary = profileAdapter != null && IsSharedMemoryPrimary(profileAdapter);
        // For an iGPU the small DXGI "dedicated" reservation is not a standalone capacity pool.
        // Preserve the raw figure in GpuInventoryStatus, but do not feed it into capacity math where
        // it would be added to the same system RAM that backs the shared aperture.
        long effectiveDedicated = sharedMemoryPrimary ? 0 : dedicated;

        return new RuntimeHardwareProfile(
            GpuPresent: hardwareAdapters.Count > 0,
            AcceleratorEligible: selected != null,
            Backend: backendName,
            Vendor: VendorName(profileAdapter?.VendorId),
            AdapterName: profileAdapter?.Name,
            DedicatedVideoMemoryBytes: effectiveDedicated,
            SharedSystemMemoryLimitBytes: sharedLimit,
            SystemRamBytes: ram,
            SharedMemoryPrimary: sharedMemoryPrimary,
            InventorySource: inventory.Source);
    }

    internal static RuntimeHardwareProfile Resolve(
        RuntimeManagerStatus status,
        GpuInventoryStatus? inventory,
        long systemRamBytes,
        TextLaunchTier launchTier)
    {
        ArgumentNullException.ThrowIfNull(launchTier);

        RuntimeHardwareProfile profile = Resolve(status, inventory, systemRamBytes);
        bool isNvidia = string.Equals(profile.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profile.Vendor, "nvidia-legacy", StringComparison.OrdinalIgnoreCase);
        if (!isNvidia || launchTier.VramSource != TextLaunchTierSelector.NvidiaSmiSource)
            return profile;

        long vramMib = Math.Clamp(launchTier.VramMib, 0, long.MaxValue / (1024L * 1024L));
        return profile with { DedicatedVideoMemoryBytes = vramMib * 1024L * 1024L };
    }

    private static bool IsLikelyDiscrete(GpuAdapterStatus adapter) =>
        adapter.DedicatedVideoMemoryBytes > IntegratedDedicatedMemoryCeilingBytes;

    private static bool IsSharedMemoryPrimary(GpuAdapterStatus adapter)
    {
        long dedicated = Math.Max(0, adapter.DedicatedVideoMemoryBytes);
        long shared = Math.Max(0, adapter.SharedSystemMemoryBytes);
        bool integratedVendor = adapter.VendorId is AmdVendorId or IntelVendorId;
        return integratedVendor
            && dedicated <= IntegratedDedicatedMemoryCeilingBytes
            && shared >= MeaningfulSharedMemoryBytes
            && shared > dedicated;
    }

    private static GpuInventoryStatus GetCachedInventory()
    {
        lock (InventoryGate)
        {
            DateTime now = DateTime.UtcNow;
            if (_cachedInventory != null && now - _cachedInventoryUtc < InventoryCacheDuration)
                return _cachedInventory;

            _cachedInventory = SharedInventory.GetStatus();
            _cachedInventoryUtc = now;
            return _cachedInventory;
        }
    }

    private static string VendorName(uint? vendorId) => vendorId switch
    {
        NvidiaVendorId => "nvidia",
        AmdVendorId => "amd",
        IntelVendorId => "intel",
        null => "none",
        _ => "other"
    };

    public static long ConservativeUsableSystemRamBytes(long totalRamBytes)
    {
        if (totalRamBytes <= 0) return 0;
        // Reserve the larger of 8 GiB or 25% for Windows, PaRuX and other active applications.
        long reserve = Math.Max(8L * 1024 * 1024 * 1024, totalRamBytes / 4);
        return Math.Max(0, totalRamBytes - reserve);
    }
}
