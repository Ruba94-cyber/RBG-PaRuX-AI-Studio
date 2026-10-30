using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>What the hardware monitor observed. Split out of ResourceMonitorService so the
    /// classification that consumes it can be unit-tested without linking the monitor's P/Invoke
    /// and diagnostic-logging dependencies - the logger writes to the real user's app data
    /// directory, which a test run must never touch.</summary>
    public sealed class ResourceSnapshot
    {
        public bool GpuAvailable { get; set; }
        public string GpuName { get; set; } = string.Empty;
        public long VramUsedMb { get; set; }
        public long VramTotalMb { get; set; }
        public int GpuUtilizationPercent { get; set; }

        /// <summary>
        /// Whether live VRAM usage and utilization were actually measured, as opposed to only the
        /// adapter and its installed VRAM being known. nvidia-smi reports usage; the vendor-agnostic
        /// DXGI inventory reports the adapter and its memory but not what is currently in use. The
        /// two must stay distinguishable, otherwise a machine with no NVIDIA telemetry would be
        /// shown a confident "0 MB used · 0% util" that nobody measured.
        /// </summary>
        /// <summary>Core-authoritative adapter/backend pair, or null when Core is unavailable.
        /// Raw GPU names and memory totals are telemetry, not proof that a text backend is installed.</summary>
        public RuntimeManagerHardwareStatus? CoreHardware { get; set; }

        /// <summary>Shared iGPU aperture is part of system RAM, not additional dedicated VRAM.</summary>
        public bool GpuMemoryIsShared { get; set; }
        public long SharedGpuMemoryLimitMb { get; set; }
        /// <summary>Null if Core has not confirmed backend eligibility.</summary>
        public bool? GpuBackendEligible { get; set; }

        public bool GpuUsageMeasured { get; set; }
        public long RamUsedMb { get; set; }
        public long RamTotalMb { get; set; }
        public bool RamAvailable { get; set; }
    }
}
