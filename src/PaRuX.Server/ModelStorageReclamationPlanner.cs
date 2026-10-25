namespace PaRuX.Server;

/// <summary>Classifies a managed storage entry for a dry-run reclamation plan.</summary>
public enum ModelStorageArtifactKind
{
    Other = 0,
    FailedOrAbandonedPartialDownload = 1,
    FailedOrAbandonedPartialIdentitySidecar = 2,
    SupersededFile = 3
}

[Flags]
public enum ModelStorageProtectionFlags
{
    None = 0,
    ManuallyPinned = 1 << 0,
    UserOriginRoleOverride = 1 << 1,
    ActiveRecommendation = 1 << 2,
    PreviousWinner = 1 << 3,
    InUseByRuntime = 1 << 4,
    ActiveDownload = 1 << 5
}

/// <summary>One caller-supplied storage observation. The planner never reads the path.</summary>
public sealed record ModelStorageReclamationInput(
    string Path,
    long SizeBytes,
    ModelStorageArtifactKind Kind,
    ModelStorageProtectionFlags ProtectionFlags = ModelStorageProtectionFlags.None,
    DateTimeOffset? LastUsedUtc = null,
    bool IsActiveJob = false);

public sealed record ReclaimPlanItem(string Path, long Bytes, string ReasonCode);

public sealed record ProtectedStorageItem(string Path, long Bytes, string ProtectionReasonCode);

/// <summary>Dry-run result. It contains no deletion operation.</summary>
public sealed class ReclaimPlan
{
    internal ReclaimPlan(
        IEnumerable<ReclaimPlanItem> items,
        IEnumerable<ProtectedStorageItem> protectedItems)
    {
        Items = Array.AsReadOnly(items.ToArray());
        ProtectedItems = Array.AsReadOnly(protectedItems.ToArray());
        TotalBytes = SumBytes(Items);
        ProtectedBytes = SumBytes(ProtectedItems);
    }

    public IReadOnlyList<ReclaimPlanItem> Items { get; }

    public IReadOnlyList<ProtectedStorageItem> ProtectedItems { get; }

    public long TotalBytes { get; }

    public int TotalItemCount => Items.Count;

    public long ProtectedBytes { get; }

    public int ProtectedItemCount => ProtectedItems.Count;

    private static long SumBytes<T>(IEnumerable<T> items) where T : notnull => items.Aggregate(
        0L,
        (total, item) => checked(total + (item switch
        {
            ReclaimPlanItem reclaimItem => reclaimItem.Bytes,
            ProtectedStorageItem protectedItem => protectedItem.Bytes,
            _ => throw new InvalidOperationException("Unsupported storage plan item.")
        })));
}

/// <summary>Builds deterministic, filesystem-free model storage reclamation plans.</summary>
public static class ModelStorageReclamationPlanner
{
    public const string FailedPartialReasonCode = "failed_partial";
    public const string SupersededUnprotectedReasonCode = "superseded_unprotected";

    private const ModelStorageProtectionFlags KnownProtectionFlags =
        ModelStorageProtectionFlags.ManuallyPinned
        | ModelStorageProtectionFlags.UserOriginRoleOverride
        | ModelStorageProtectionFlags.ActiveRecommendation
        | ModelStorageProtectionFlags.PreviousWinner
        | ModelStorageProtectionFlags.InUseByRuntime
        | ModelStorageProtectionFlags.ActiveDownload;

    /// <summary>Plans candidates and protected entries from caller-supplied inventory data only.</summary>
    public static ReclaimPlan Plan(IEnumerable<ModelStorageReclamationInput> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var candidates = new List<ReclaimPlanItem>();
        var protectedItems = new List<ProtectedStorageItem>();

        foreach (ModelStorageReclamationInput entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (string.IsNullOrWhiteSpace(entry.Path))
                throw new ArgumentException("Storage entry paths must be non-empty.", nameof(entries));
            if (entry.SizeBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(entries), "Storage entry sizes cannot be negative.");

            string? protectionReason = GetProtectionReason(entry);
            if (protectionReason is not null)
            {
                protectedItems.Add(new ProtectedStorageItem(entry.Path, entry.SizeBytes, protectionReason));
                continue;
            }

            string? reclaimReason = GetReclaimReason(entry.Kind);
            if (reclaimReason is not null)
                candidates.Add(new ReclaimPlanItem(entry.Path, entry.SizeBytes, reclaimReason));
        }

        ReclaimPlanItem[] orderedCandidates = candidates
            .OrderBy(item => item.ReasonCode, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
        ProtectedStorageItem[] orderedProtected = protectedItems
            .OrderBy(item => item.ProtectionReasonCode, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();

        return new ReclaimPlan(orderedCandidates, orderedProtected);
    }

    private static string? GetReclaimReason(ModelStorageArtifactKind kind) => kind switch
    {
        ModelStorageArtifactKind.FailedOrAbandonedPartialDownload => FailedPartialReasonCode,
        ModelStorageArtifactKind.FailedOrAbandonedPartialIdentitySidecar => FailedPartialReasonCode,
        ModelStorageArtifactKind.SupersededFile => SupersededUnprotectedReasonCode,
        _ => null
    };

    private static string? GetProtectionReason(ModelStorageReclamationInput entry)
    {
        if (entry.IsActiveJob) return "active_job";

        ModelStorageProtectionFlags flags = entry.ProtectionFlags;
        if ((flags & ModelStorageProtectionFlags.ManuallyPinned) != 0) return "manually_pinned";
        if ((flags & ModelStorageProtectionFlags.UserOriginRoleOverride) != 0) return "user_role_override";
        if ((flags & ModelStorageProtectionFlags.ActiveRecommendation) != 0) return "active_recommendation";
        if ((flags & ModelStorageProtectionFlags.PreviousWinner) != 0) return "previous_winner";
        if ((flags & ModelStorageProtectionFlags.InUseByRuntime) != 0) return "in_use_by_runtime";
        if ((flags & ModelStorageProtectionFlags.ActiveDownload) != 0) return "active_download";
        if ((flags & ~KnownProtectionFlags) != 0) return "unknown_protection";
        return null;
    }
}
