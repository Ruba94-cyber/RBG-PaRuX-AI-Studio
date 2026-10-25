using System.Runtime.InteropServices;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal sealed record ExecutionResourceSnapshot(
    double? SystemRamUsedPercent,
    IReadOnlyList<GpuPerformanceTelemetryDto> Gpus,
    double SystemRamReclaimablePercent = 0);

internal sealed record ExecutionResourceDecision(
    bool Allowed,
    bool TelemetryComplete,
    double? SystemRamUsedPercent,
    double? MaximumVramUsedPercent,
    double? MaximumGpuTemperatureC,
    string Message);

/// <summary>
/// Measured admission guard for the resource limits carried by Execution Profiles. It consumes the
/// existing nvidia-smi telemetry service and OS physical-memory counters; it never fabricates GPU
/// temperature/VRAM data when a driver does not expose them. Unknown telemetry is explicit and does
/// not make AMD/iGPU/older-driver machines unusable, while every value that is known is enforced
/// fail-closed against the active profile.
///
/// Current VRAM occupancy is opt-in per call. A text-model swap must not be rejected just because
/// the old model is occupying memory that the swap itself will release; additive workloads such as
/// local training can request strict current-VRAM admission because they coexist with other loads.
/// </summary>
internal static class ExecutionResourceGuard
{
    public static async Task<ExecutionResourceDecision> CheckCurrentAsync(
        ModelExecutionProfile profile,
        bool enforceCurrentVram,
        CancellationToken ct,
        long reclaimableSystemRamBytes = 0,
        bool enforceCurrentSystemRam = true,
        IGpuPerformanceTelemetrySnapshotProvider? gpuTelemetryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ExecutionResourceBudget budget = profile.Budget
            ?? throw new InvalidDataException("Execution Profile contains no resource budget.");

        gpuTelemetryProvider ??= CachedGpuPerformanceTelemetrySnapshotProvider.Shared;
        GpuPerformanceTelemetrySnapshot? telemetry = enforceCurrentVram
            ? await gpuTelemetryProvider.GetSnapshotAsync(ct).ConfigureAwait(false)
            : await gpuTelemetryProvider.GetLatestForResourceGuardAsync(ct).ConfigureAwait(false);
        List<GpuPerformanceTelemetryDto> gpus = telemetry?.Devices.ToList() ?? new List<GpuPerformanceTelemetryDto>();
        SystemRamSnapshot? ram = TryGetSystemRamSnapshot();
        double? usedPercent = ram?.UsedPercent;
        ulong totalBytes = ram?.TotalBytes ?? 0;
        double reclaimablePercent = totalBytes > 0 && reclaimableSystemRamBytes > 0
            ? Math.Clamp(reclaimableSystemRamBytes * 100d / totalBytes, 0d, 100d)
            : 0d;
        return Evaluate(
            new ExecutionResourceSnapshot(usedPercent, gpus, reclaimablePercent),
            budget,
            enforceCurrentVram,
            enforceCurrentSystemRam);
    }

    internal static ExecutionResourceDecision Evaluate(
        ExecutionResourceSnapshot snapshot,
        ExecutionResourceBudget budget,
        bool enforceCurrentVram,
        bool enforceCurrentSystemRam = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(budget);
        ValidateBudget(budget);

        List<double> temperatures = snapshot.Gpus
            .Where(gpu => gpu.TemperatureC.HasValue && double.IsFinite(gpu.TemperatureC.Value))
            .Select(gpu => gpu.TemperatureC!.Value)
            .ToList();
        double? maxTemperature = temperatures.Count == 0 ? null : temperatures.Max();

        List<double> vramPercentages = snapshot.Gpus
            .Where(gpu => gpu.MemoryTotalMib is > 0 && gpu.MemoryUsedMib is >= 0)
            .Select(gpu => Math.Clamp(gpu.MemoryUsedMib!.Value * 100d / gpu.MemoryTotalMib!.Value, 0d, 100d))
            .ToList();
        double? maxVramUsed = vramPercentages.Count == 0 ? null : vramPercentages.Max();

        double? admissionRamUsed = snapshot.SystemRamUsedPercent.HasValue
            ? Math.Max(0d, snapshot.SystemRamUsedPercent.Value - snapshot.SystemRamReclaimablePercent)
            : null;

        var violations = new List<string>();
        // Refusing is right; refusing without saying what would clear the refusal leaves the person
        // stuck in front of a threshold they have no stated way to get under. The usual cause of
        // each of these is specific enough to name.
        var remedies = new List<string>();
        if (enforceCurrentSystemRam
            && admissionRamUsed.HasValue
            && admissionRamUsed.Value > budget.MaxSystemRamPercent)
        {
            string reclaimNote = snapshot.SystemRamReclaimablePercent > 0
                ? $" after excluding {snapshot.SystemRamReclaimablePercent:0.0}% held by the PaRuX runtime being replaced"
                : string.Empty;
            violations.Add($"system RAM {admissionRamUsed.Value:0.0}% > {budget.MaxSystemRamPercent}%{reclaimNote}");
            remedies.Add(snapshot.SystemRamReclaimablePercent > 0
                ? "free memory used by other applications"
                : "free system memory - a chat model held in memory is usually the largest single user, "
                    + "and stopping it in Runtime Manager releases it");
        }
        if (maxTemperature.HasValue && maxTemperature.Value > budget.MaxGpuTemperatureC)
        {
            violations.Add($"GPU temperature {maxTemperature.Value:0.0}°C > {budget.MaxGpuTemperatureC}°C");
            remedies.Add("let the GPU cool down and start the job again");
        }
        if (enforceCurrentVram && maxVramUsed.HasValue && maxVramUsed.Value > budget.MaxVramPercent)
        {
            violations.Add($"GPU VRAM {maxVramUsed.Value:0.0}% > {budget.MaxVramPercent}%");
            remedies.Add("free video memory by stopping the runtime that currently holds it in Runtime Manager");
        }

        bool ramKnown = !enforceCurrentSystemRam || snapshot.SystemRamUsedPercent.HasValue;
        bool gpuTelemetryKnown = snapshot.Gpus.Count > 0;
        bool temperatureKnown = gpuTelemetryKnown && maxTemperature.HasValue;
        bool vramKnown = !enforceCurrentVram || (gpuTelemetryKnown && maxVramUsed.HasValue);

        var unknown = new List<string>();
        if (enforceCurrentSystemRam && !ramKnown) unknown.Add("system RAM usage");
        if (!gpuTelemetryKnown) unknown.Add("GPU telemetry");
        else
        {
            if (!temperatureKnown) unknown.Add("GPU temperature");
            if (enforceCurrentVram && !vramKnown) unknown.Add("GPU VRAM usage");
        }
        bool complete = unknown.Count == 0;

        if (violations.Count > 0)
        {
            return new ExecutionResourceDecision(
                false,
                complete,
                admissionRamUsed,
                maxVramUsed,
                maxTemperature,
                "Execution Profile resource admission rejected the workload: " + string.Join("; ", violations) + ". "
                + "To run it, " + string.Join("; ", remedies) + ".");
        }

        string message = unknown.Count == 0
            ? "Execution Profile resource admission passed measured hardware guardrails."
            : "Execution Profile resource admission passed all available measurements; unavailable telemetry: "
                + string.Join(", ", unknown) + ".";
        return new ExecutionResourceDecision(
            true,
            complete,
            admissionRamUsed,
            maxVramUsed,
            maxTemperature,
            message);
    }

    private static void ValidateBudget(ExecutionResourceBudget budget)
    {
        if (budget.MaxGpuTemperatureC is < 60 or > 95)
            throw new InvalidDataException("Execution Profile GPU temperature guard must be between 60 and 95°C.");
        if (budget.MaxSystemRamPercent is < 40 or > 98)
            throw new InvalidDataException("Execution Profile system RAM guard must be between 40 and 98 percent.");
        if (budget.MaxVramPercent is < 40 or > 99)
            throw new InvalidDataException("Execution Profile VRAM guard must be between 40 and 99 percent.");
    }

    internal static double? TryGetSystemRamUsedPercent() => TryGetSystemRamSnapshot()?.UsedPercent;

    internal static SystemRamSnapshot? TryGetSystemRamSnapshot()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx();
                if (GlobalMemoryStatusEx(status))
                    return SystemRamSnapshot.TryCreate(status.TotalPhys, status.AvailPhys);
                return null;
            }

            if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
            {
                long? totalKib = null;
                long? availableKib = null;
                foreach (string line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) totalKib = ParseMemInfoKib(line);
                    else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) availableKib = ParseMemInfoKib(line);
                    if (totalKib.HasValue && availableKib.HasValue) break;
                }
                if (totalKib is > 0 && availableKib is >= 0 && availableKib.Value <= totalKib.Value)
                    return SystemRamSnapshot.TryCreate(
                        checked((ulong)totalKib.Value * 1024UL),
                        checked((ulong)availableKib.Value * 1024UL));
            }
        }
        catch { }
        return null;
    }

    private static long? ParseMemInfoKib(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out long value) && value >= 0 ? value : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);
}
