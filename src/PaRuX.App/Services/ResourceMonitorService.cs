using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Read-only desktop hardware visibility: actual NVIDIA VRAM (via nvidia-smi, since llama.cpp
    /// itself is started by PaRuX.Server's LocalAiProcessManager as a separate process, not linked
    /// into PaRuX) and system RAM (via GlobalMemoryStatusEx).
    ///
    /// This class intentionally remains a monitor rather than a second resource orchestrator.
    /// Dynamic model residency/load-unload decisions are already Core-owned by LocalAiProcessManager,
    /// HotModelPoolCatalog, Execution Profiles and the text/media resource coordinators. Keeping this
    /// UI-facing probe read-only avoids competing policy loops making contradictory residency choices.
    /// </summary>
    public static class ResourceMonitorService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        /// <summary>Installed adapters do not change between two polls, and the Settings
        /// diagnostics panel refreshes every few seconds, so the vendor-agnostic inventory is
        /// cached rather than re-requested from the Core on every tick.</summary>
        private static readonly TimeSpan InventoryTtl = TimeSpan.FromMinutes(5);
        private static readonly SemaphoreSlim InventoryGate = new(1, 1);
        private static (string Name, long VramMb)? _cachedInventory;
        private static DateTime _cachedInventoryUtc = DateTime.MinValue;

        public static async Task<ResourceSnapshot> GetSnapshotAsync(CancellationToken ct)
        {
            var snapshot = new ResourceSnapshot();
            PopulateRam(snapshot);
            await PopulateGpuAsync(snapshot, ct).ConfigureAwait(false);

            // Deliberately here rather than at the end of PopulateGpuAsync: that method returns
            // early from inside its try on every "nvidia-smi answered, but not usefully" path, and
            // on this AMD workstation that is exactly what happens - nvidia-smi.exe ships in
            // System32 with the NVIDIA App, runs, and exits with code 4 ("you do not have
            // sufficient permissions"). Hanging the fallback off the end of that method left it
            // unreachable on the one machine that needs it.
            if (!snapshot.GpuAvailable) await PopulateGpuFromInventoryAsync(snapshot, ct).ConfigureAwait(false);
            return snapshot;
        }

        /// <summary>
        /// The Core's DXGI-based inventory: vendor-agnostic, so it sees the AMD/Intel adapters
        /// nvidia-smi cannot. It reports the adapter and its installed memory but not live usage,
        /// so GpuUsageMeasured deliberately stays false and callers must not present a usage number.
        /// </summary>
        private static async Task PopulateGpuFromInventoryAsync(ResourceSnapshot snapshot, CancellationToken ct)
        {
            try
            {
                (string Name, long VramMb)? inventory = await GetInventoryAsync(ct).ConfigureAwait(false);
                if (inventory == null || inventory.Value.VramMb <= 0) return;

                snapshot.GpuAvailable = true;
                snapshot.GpuUsageMeasured = false;
                snapshot.GpuName = inventory.Value.Name;
                snapshot.VramTotalMb = inventory.Value.VramMb;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Core unreachable: leave GpuAvailable false rather than guess at hardware.
            }
        }

        private static async Task<(string Name, long VramMb)?> GetInventoryAsync(CancellationToken ct)
        {
            if (_cachedInventory != null && DateTime.UtcNow - _cachedInventoryUtc < InventoryTtl) return _cachedInventory;

            await InventoryGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_cachedInventory != null && DateTime.UtcNow - _cachedInventoryUtc < InventoryTtl) return _cachedInventory;

                PaRuX.Contracts.RuntimeManagerHardwareStatus status =
                    await RuntimeManagerClient.Current.GetStatusAsync(ct).ConfigureAwait(false);

                long vramMb = status.GpuLargestDedicatedVideoMemoryBytes / 1024 / 1024;
                string name = status.GpuAdapters is { Count: > 0 }
                    ? status.GpuAdapters
                        .OrderByDescending(adapter => adapter.DedicatedVideoMemoryBytes)
                        .First().Name
                    : string.Empty;
                if (vramMb <= 0) return null;

                _cachedInventory = (string.IsNullOrWhiteSpace(name) ? "GPU" : name, vramMb);
                _cachedInventoryUtc = DateTime.UtcNow;
                return _cachedInventory;
            }
            finally { InventoryGate.Release(); }
        }

        private static void PopulateRam(ResourceSnapshot snapshot)
        {
            try
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (!GlobalMemoryStatusEx(ref status)) return;

                snapshot.RamTotalMb = (long)(status.ullTotalPhys / 1024 / 1024);
                snapshot.RamUsedMb = snapshot.RamTotalMb - (long)(status.ullAvailPhys / 1024 / 1024);
                snapshot.RamAvailable = true;
            }
            catch (Exception ex)
            {
                Log.Write("ResourceMonitorService.PopulateRam failed: " + ex.Message);
            }
        }

        private static async Task PopulateGpuAsync(ResourceSnapshot snapshot, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name,memory.used,memory.total,utilization.gpu --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = new Process { StartInfo = psi };
                process.Start();
                Task<string> outTask = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                string output = await outTask.ConfigureAwait(false);
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return;

                // One line per GPU; a single discrete card is the common case this reports on.
                string firstLine = output.Split('\n')[0].Trim();
                string[] parts = firstLine.Split(',');
                if (parts.Length < 4) return;

                if (!long.TryParse(parts[1].Trim(), out long usedMb)) return;
                if (!long.TryParse(parts[2].Trim(), out long totalMb)) return;
                int.TryParse(parts[3].Trim(), out int utilization);

                snapshot.GpuAvailable = true;
                snapshot.GpuUsageMeasured = true;
                snapshot.GpuName = parts[0].Trim();
                snapshot.VramUsedMb = usedMb;
                snapshot.VramTotalMb = totalMb;
                snapshot.GpuUtilizationPercent = utilization;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // nvidia-smi missing, no NVIDIA GPU present, or a transient failure. That is not
                // the same as "this machine has no GPU": on the AMD reference workstation it left
                // every model in Settings labelled "Fit unknown (no GPU detected)" while Model Hub,
                // reading the Core's vendor-agnostic DXGI inventory, correctly showed the Radeon
                // 780M and its 5.9 GB. One app must not tell the user two different things about
                // the same hardware, so fall back to that same inventory here.
            }

        }
    }
}
