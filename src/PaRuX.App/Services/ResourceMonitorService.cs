using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PaRuX.Contracts;
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
        /// cached briefly rather than re-requested from Core on every tick. A short
        /// TTL is important for eGPU unplug/replug and backend install changes.</summary>
        private static readonly TimeSpan InventoryTtl = TimeSpan.FromSeconds(10);
        private static readonly SemaphoreSlim InventoryGate = new(1, 1);
        private static RuntimeManagerHardwareStatus? _cachedInventory;
        private static DateTime _cachedInventoryUtc = DateTime.MinValue;
        private static DateTime _lastInventoryFailureUtc = DateTime.MinValue;
        private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(8);

        public static async Task<ResourceSnapshot> GetSnapshotAsync(CancellationToken ct)
        {
            var snapshot = new ResourceSnapshot { CoreHardwareProbeAttempted = true };
            PopulateRam(snapshot);
            // Run the OS telemetry probe and Core profile discovery concurrently.
            // Neither source waits for the other: slow NVIDIA tooling must not
            // double the latency on Intel/AMD or CPU-only laptops.
            Task<RuntimeManagerHardwareStatus?> inventoryTask = TryGetInventoryAsync(ct);
            await PopulateGpuAsync(snapshot, ct).ConfigureAwait(false);
            RuntimeManagerHardwareStatus? status = await inventoryTask.ConfigureAwait(false);
            if (status != null)
                ResourceHardwareSnapshotMapper.Apply(snapshot, status);
            return snapshot;
        }

        /// <summary>Core status is optional for diagnostics. Timeouts or an offline
        /// server do not prevent returning OS RAM or real GPU telemetry.</summary>
        private static async Task<RuntimeManagerHardwareStatus?> TryGetInventoryAsync(CancellationToken ct)
        {
            try { return await GetInventoryAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private static async Task<RuntimeManagerHardwareStatus?> GetInventoryAsync(CancellationToken ct)
        {
            if (_cachedInventory != null && DateTime.UtcNow - _cachedInventoryUtc < InventoryTtl)
                return _cachedInventory;

            await InventoryGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_cachedInventory != null && DateTime.UtcNow - _cachedInventoryUtc < InventoryTtl)
                    return _cachedInventory;
                if (DateTime.UtcNow - _lastInventoryFailureUtc < FailureRetryDelay)
                    return null;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    RuntimeManagerHardwareStatus status =
                        await RuntimeManagerClient.Current.GetStatusAsync(timeout.Token).ConfigureAwait(false);
                    _cachedInventory = status;
                    _cachedInventoryUtc = DateTime.UtcNow;
                    _lastInventoryFailureUtc = DateTime.MinValue;
                    return status;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch
                {
                    // Avoid restarting/offline-probing Core on every Settings tick.
                    // The short retry interval also permits eGPU/backend recovery.
                    _lastInventoryFailureUtc = DateTime.UtcNow;
                    throw;
                }
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
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch { /* The process already exited or Windows refused termination. */ }
                    if (ct.IsCancellationRequested) throw;
                    return; // Telemetry is optional and may be blocked on some PCs.
                }
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
