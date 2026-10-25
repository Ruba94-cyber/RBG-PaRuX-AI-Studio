using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// What the context planners may draw on, expressed once so two endpoints cannot disagree about the
/// same machine. The primary pool is deliberately named VramBytes for compatibility with the
/// existing planners, but for an integrated GPU it represents the bounded shared-system-memory
/// aperture used by the accelerator rather than fabricated dedicated VRAM.
///
/// Rules:
///   * CPU-only / no eligible installed GPU backend: the model already runs in system RAM, so RAM
///     is the primary pool and there is no second offload pool.
///   * Discrete eligible GPU: dedicated video memory is primary; up to half of system RAM is an
///     optional KV/offload pool.
///   * Integrated/shared-memory GPU: shared memory is the primary accelerator limit. It is the same
///     physical system RAM, so it is never added to RAM again as an offload pool.
/// </summary>
public static class ContextMemoryBudget
{
    /// <summary>Share of system RAM offered as a KV-cache offload target for a discrete GPU.</summary>
    private const double OffloadableRamFraction = 0.5;

    public readonly record struct Budget(
        long VramBytes,
        long OffloadableRamBytes,
        string Backend = "cpu",
        bool PrimaryPoolIsSharedSystemMemory = false)
    {
        /// <summary>Everything the model may occupy without double-counting shared system RAM.</summary>
        public long TotalBytes => SaturatingAdd(VramBytes, OffloadableRamBytes);

        private static long SaturatingAdd(long left, long right)
        {
            left = Math.Max(0, left);
            right = Math.Max(0, right);
            return long.MaxValue - left < right ? long.MaxValue : left + right;
        }
    }

    /// <summary>Production path. Hardware inventory is briefly cached by the unified resolver.</summary>
    public static Budget For(RuntimeManagerStatus status)
    {
        if (status == null) return new Budget(0, 0);
        return For(status, RuntimeHardwareProfileResolver.Resolve(status), status.RamTotalBytes);
    }

    /// <summary>Deterministic overload for tests and callers that already own an inventory snapshot.</summary>
    public static Budget For(RuntimeManagerStatus status, GpuInventoryStatus? inventory)
    {
        if (status == null) return new Budget(0, 0);
        return For(status, RuntimeHardwareProfileResolver.Resolve(status, inventory), status.RamTotalBytes);
    }

    public static Budget For(RuntimeManagerStatus status, long currentRamBudgetBytes)
    {
        if (status == null) return new Budget(0, 0);
        return For(status, RuntimeHardwareProfileResolver.Resolve(status), currentRamBudgetBytes);
    }

    /// <summary>Uses the same OS snapshot total that produced the live available-RAM budget.</summary>
    internal static Budget For(
        RuntimeManagerStatus status,
        long currentRamBudgetBytes,
        long systemRamTotalBytes)
    {
        if (status == null) return new Budget(0, 0);
        RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(status, systemRamTotalBytes);
        return For(status, hardware, currentRamBudgetBytes, systemRamTotalBytes);
    }

    internal static Budget For(
        RuntimeManagerStatus status,
        GpuInventoryStatus? inventory,
        long currentRamBudgetBytes,
        long systemRamTotalBytes)
    {
        if (status == null) return new Budget(0, 0);
        RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(status, inventory, systemRamTotalBytes);
        return For(status, hardware, currentRamBudgetBytes, systemRamTotalBytes);
    }

    public static Budget For(RuntimeManagerStatus status, GpuInventoryStatus? inventory, long currentRamBudgetBytes)
    {
        if (status == null) return new Budget(0, 0);
        return For(status, RuntimeHardwareProfileResolver.Resolve(status, inventory), currentRamBudgetBytes);
    }

    public static Budget For(RuntimeManagerStatus status, RuntimeHardwareProfile hardware)
    {
        if (status == null) return new Budget(0, 0);
        return For(status, hardware, status.RamTotalBytes);
    }

    public static Budget For(RuntimeManagerStatus status, RuntimeHardwareProfile hardware, long currentRamBudgetBytes)
        => For(status, hardware, currentRamBudgetBytes, status?.RamTotalBytes ?? 0);

    /// <summary>
    /// Live admission path shared by both context endpoints. Dedicated GPU memory is credited only
    /// from a fresh, uniquely matched device-wide NVIDIA sample; other discrete adapters receive
    /// no fabricated free-memory credit, while UMA continues to use the same RAM pool exactly once.
    /// </summary>
    internal static async Task<Budget> ForCurrentResourcesAsync(
        RuntimeManagerStatus status,
        RuntimeHardwareProfile hardware,
        long currentRamBudgetBytes,
        long systemRamTotalBytes,
        IGpuPerformanceTelemetrySnapshotProvider gpuTelemetryProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gpuTelemetryProvider);
        long gpuAvailableBytes = 0;
        if (hardware.AcceleratorEligible
            && !hardware.UsesSharedSystemMemoryAsPrimary
            && hardware.DedicatedVideoMemoryBytes > 0)
        {
            // NVIDIA is measured by nvidia-smi; AMD/Intel discrete by the DXGI budget minus the
            // system-wide PDH usage. Either way an unknown sample is 0 free (no context capacity).
            GpuPerformanceTelemetrySnapshot? snapshot = await gpuTelemetryProvider.GetSnapshotAsync(ct)
                .ConfigureAwait(false);
            gpuAvailableBytes = RuntimeGpuMemoryBudget.ResolveAvailableDedicatedGpuBytes(
                hardware, snapshot, DateTime.UtcNow);
        }

        return ForCurrentResources(status, hardware, currentRamBudgetBytes, systemRamTotalBytes, gpuAvailableBytes);
    }

    internal static Budget ForCurrentResources(
        RuntimeManagerStatus status,
        RuntimeHardwareProfile hardware,
        long currentRamBudgetBytes,
        long systemRamTotalBytes,
        long currentDedicatedGpuBytes)
    {
        if (status == null) return new Budget(0, 0);
        if (hardware.DedicatedVideoMemoryBytes > 0 && currentDedicatedGpuBytes <= 0)
        {
            // Context expansion can offer host-RAM KV offload, but that must not manufacture a
            // viable GPU configuration when no free primary VRAM has been verified. Keep the
            // selected backend visible for diagnostics while exposing no context capacity.
            return new Budget(0, 0, hardware.Backend, PrimaryPoolIsSharedSystemMemory: false);
        }

        return ForCore(status, hardware, currentRamBudgetBytes, systemRamTotalBytes,
            Math.Clamp(currentDedicatedGpuBytes, 0, Math.Max(0, hardware.DedicatedVideoMemoryBytes)));
    }

    private static Budget For(
        RuntimeManagerStatus status,
        RuntimeHardwareProfile hardware,
        long currentRamBudgetBytes,
        long systemRamTotalBytes)
        => ForCore(status, hardware, currentRamBudgetBytes, systemRamTotalBytes, null);

    private static Budget ForCore(
        RuntimeManagerStatus status,
        RuntimeHardwareProfile hardware,
        long currentRamBudgetBytes,
        long systemRamTotalBytes,
        long? currentDedicatedGpuBytes)
    {
        if (status == null) return new Budget(0, 0);
        long ram = Math.Max(0, systemRamTotalBytes);
        long liveRamBudget = Math.Min(ram, Math.Max(0, currentRamBudgetBytes));

        if (!hardware.AcceleratorEligible)
            return new Budget(liveRamBudget, 0, "cpu");

        if (hardware.DedicatedVideoMemoryBytes > 0)
        {
            return new Budget(
                currentDedicatedGpuBytes ?? hardware.DedicatedVideoMemoryBytes,
                Math.Min((long)(ram * OffloadableRamFraction), liveRamBudget),
                hardware.Backend,
                PrimaryPoolIsSharedSystemMemory: false);
        }

        if (hardware.SharedSystemMemoryLimitBytes > 0)
        {
            // DXGI shared memory is a limit into system RAM, not extra physical memory. Counting it
            // again as OffloadableRamBytes would overstate capacity precisely on iGPU systems. Also
            // reserve the same conservative host headroom used by onboarding and Model Hub.
            long usableRam = hardware.UsableSystemRamBytes;
            // A missing total-RAM reading is unknown, not permission to use the full DXGI
            // aperture. The computed usable pool is zero in that case, so stay fail-closed.
            long sharedPrimary = Math.Min(usableRam, hardware.SharedSystemMemoryLimitBytes);
            sharedPrimary = Math.Min(sharedPrimary, liveRamBudget);
            return new Budget(
                sharedPrimary,
                0,
                hardware.Backend,
                PrimaryPoolIsSharedSystemMemory: true);
        }

        // A backend may be installed while inventory reports no usable memory. Fail closed to CPU
        // budgeting instead of inventing accelerator capacity.
        return new Budget(liveRamBudget, 0, "cpu");
    }
}
