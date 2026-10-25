namespace PaRuX.Server;

/// <summary>One best-effort operating-system RAM sample. Invalid or unavailable samples fail closed.</summary>
internal sealed record SystemRamSnapshot(ulong TotalBytes, ulong AvailableBytes)
{
    public static SystemRamSnapshot? TryCreate(ulong totalBytes, ulong availableBytes) =>
        totalBytes > 0 && totalBytes <= long.MaxValue && availableBytes <= totalBytes
            ? new SystemRamSnapshot(totalBytes, availableBytes)
            : null;

    public bool IsValid => TotalBytes > 0 && TotalBytes <= long.MaxValue && AvailableBytes <= TotalBytes;

    public long TotalBytesAsLong => IsValid ? (long)TotalBytes : 0;

    public double? UsedPercent => IsValid
        ? Math.Clamp((TotalBytes - AvailableBytes) * 100d / TotalBytes, 0d, 100d)
        : null;
}

internal interface ISystemRamSnapshotProvider
{
    SystemRamSnapshot? TryGetSnapshot();
}

internal sealed class SystemRamSnapshotProvider : ISystemRamSnapshotProvider
{
    public SystemRamSnapshot? TryGetSnapshot() => ExecutionResourceGuard.TryGetSystemRamSnapshot();
}

/// <summary>Pure, fail-closed RAM admission math shared by model fit and context planning.</summary>
internal static class RuntimeMemoryBudget
{
    /// <summary>
    /// The RAM a model load may take, in bytes, under two limits that must both hold. The profile
    /// ceiling caps the whole system, so memory other applications already hold is taken off that
    /// ceiling first. The load itself can only use what is free plus what the swap reclaims.
    /// <paramref name="reclaimableBytes"/> is the resident model's working set: a swap stops it first,
    /// so it is available to the load, but it is not "used by others". Taking the smaller of the
    /// ceiling and the free memory alone let a busy host admit a model that pushed RAM past the ceiling.
    /// </summary>
    public static long CalculateAvailableSystemRamBytes(
        SystemRamSnapshot? snapshot,
        double maxSystemRamPercent,
        long reclaimableBytes = 0)
    {
        if (snapshot?.IsValid != true || !double.IsFinite(maxSystemRamPercent)) return 0;

        decimal total = Math.Min((decimal)snapshot.TotalBytes, long.MaxValue);
        decimal policyPercent = (decimal)Math.Clamp(maxSystemRamPercent, 0d, 100d);
        decimal policyCap = decimal.Floor(total * policyPercent / 100m);
        decimal reclaimable = Math.Min(Math.Max(0, reclaimableBytes), total);
        decimal usedByOthers = Math.Max(0, total - snapshot.AvailableBytes - reclaimable);
        decimal ceilingHeadroom = Math.Max(0, policyCap - usedByOthers);
        decimal postTransitionAvailable = Math.Min(total, snapshot.AvailableBytes + reclaimable);
        return (long)Math.Min(ceilingHeadroom, postTransitionAvailable);
    }
}
