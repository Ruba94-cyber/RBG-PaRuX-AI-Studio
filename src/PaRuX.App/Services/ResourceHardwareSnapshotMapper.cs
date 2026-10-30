using System;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Copy the Core's normalized hardware identity into the desktop monitor.
    /// Physical VRAM telemetry is not proof of an eligible local text backend.
    /// On an iGPU the DXGI shared aperture is part of system RAM, not dedicated VRAM.
    /// Kept pure so CPU, AMD, Intel, NVIDIA and eGPU transitions are testable.
    /// </summary>
    public static class ResourceHardwareSnapshotMapper
    {
        private const long MiB = 1024L * 1024L;

        public static void Apply(ResourceSnapshot snapshot, RuntimeManagerHardwareStatus status)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(status);
            snapshot.CoreHardware = status;

            RuntimeAcceleratorProfile? profile = status.AcceleratorProfile;
            if (profile != null)
            {
                snapshot.GpuBackendEligible = profile.AcceleratorEligible;
                // If DXGI inventory failed, a genuine nvidia-smi reading still
                // proves physical presence (not executable backend eligibility).
                snapshot.GpuAvailable = profile.GpuPresent
                    || (snapshot.GpuAvailable && !status.GpuInventoryAvailable);
                snapshot.GpuMemoryIsShared = profile.SharedMemoryPrimary;
                snapshot.SharedGpuMemoryLimitMb = profile.SharedMemoryPrimary
                    ? Math.Max(0, profile.SharedSystemMemoryLimitBytes) / MiB : 0;

                if (!profile.GpuPresent)
                {
                    if (status.GpuInventoryAvailable) ClearGpuTelemetry(snapshot);
                    return;
                }

                string name = profile.AdapterName ?? string.Empty;
                bool sameAdapter = !string.IsNullOrWhiteSpace(name)
                    && string.Equals(name, snapshot.GpuName, StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(name))
                    snapshot.GpuName = name;

                if (profile.SharedMemoryPrimary)
                {
                    // A previous nvidia-smi sample must not become a fake VRAM
                    // figure for the integrated adapter selected by Core.
                    snapshot.VramTotalMb = 0;
                    snapshot.VramUsedMb = 0;
                    snapshot.GpuUtilizationPercent = 0;
                    snapshot.GpuUsageMeasured = false;
                }
                else
                {
                    long dedicated = Math.Max(0, profile.DedicatedVideoMemoryBytes) / MiB;
                    if (dedicated > 0)
                        snapshot.VramTotalMb = dedicated;
                    // A telemetry sample from another adapter is not this device's
                    // utilisation. Keep usage only when names identify the same GPU.
                    if (!sameAdapter || dedicated <= 0)
                    {
                        snapshot.GpuUsageMeasured = false;
                        snapshot.VramUsedMb = 0;
                        snapshot.GpuUtilizationPercent = 0;
                    }
                }
                return;
            }

            // Older Core builds do not return AcceleratorProfile. Inventory can
            // describe physical capacity, but cannot certify backend eligibility.
            snapshot.GpuBackendEligible = null;
            GpuAdapterStatus? adapter = (status.GpuAdapters ?? new())
                .Where(item => !item.IsSoftware)
                .OrderByDescending(item => item.DedicatedVideoMemoryBytes)
                .ThenByDescending(item => item.SharedSystemMemoryBytes)
                .FirstOrDefault();

            if (adapter == null) return;
            snapshot.GpuAvailable = true;
            snapshot.GpuName = adapter.Name;
            bool shared = (adapter.VendorId == 0x1002 || adapter.VendorId == 0x8086)
                && adapter.DedicatedVideoMemoryBytes <= 2L * 1024 * MiB
                && adapter.SharedSystemMemoryBytes > adapter.DedicatedVideoMemoryBytes
                && adapter.SharedSystemMemoryBytes >= 2L * 1024 * MiB;
            snapshot.GpuMemoryIsShared = shared;
            snapshot.SharedGpuMemoryLimitMb = shared
                ? Math.Max(0, adapter.SharedSystemMemoryBytes) / MiB : 0;
            snapshot.VramTotalMb = shared ? 0 : Math.Max(0, adapter.DedicatedVideoMemoryBytes) / MiB;
            snapshot.VramUsedMb = 0;
            snapshot.GpuUtilizationPercent = 0;
            snapshot.GpuUsageMeasured = false;
        }

        private static void ClearGpuTelemetry(ResourceSnapshot snapshot)
        {
            snapshot.GpuName = string.Empty;
            snapshot.GpuMemoryIsShared = false;
            snapshot.SharedGpuMemoryLimitMb = 0;
            snapshot.VramTotalMb = 0;
            snapshot.VramUsedMb = 0;
            snapshot.GpuUtilizationPercent = 0;
            snapshot.GpuUsageMeasured = false;
        }

        public static string DescribeGpu(ResourceSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (!snapshot.GpuAvailable) return "No GPU detected; CPU/RAM fallback";
            string name = string.IsNullOrWhiteSpace(snapshot.GpuName) ? "GPU" : snapshot.GpuName;
            string memory = snapshot.GpuMemoryIsShared
                ? snapshot.SharedGpuMemoryLimitMb > 0
                    ? "up to " + snapshot.SharedGpuMemoryLimitMb.ToString("N0") + " MB shared system RAM (not VRAM)"
                    : "shared system RAM (capacity unknown)"
                : snapshot.VramTotalMb > 0
                    ? snapshot.VramTotalMb.ToString("N0") + " MB dedicated VRAM"
                    : "GPU memory capacity unknown";
            string eligibility = snapshot.GpuBackendEligible == false
                ? " · no eligible local GPU backend; CPU fallback"
                : snapshot.GpuBackendEligible == null
                    ? " · backend eligibility not confirmed"
                    : string.Empty;
            return name + " · " + memory + eligibility;
        }
    }
}
