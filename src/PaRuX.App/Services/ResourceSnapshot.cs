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
        public bool GpuUsageMeasured { get; set; }
        public long RamUsedMb { get; set; }
        public long RamTotalMb { get; set; }
        public bool RamAvailable { get; set; }
    }
}
