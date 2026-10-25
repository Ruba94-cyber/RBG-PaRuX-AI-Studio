using System.Collections.Concurrent;

namespace PaRuX.Server;

/// <summary>
/// Process-local GPU reservation ledger shared by text/media/training/embedding workloads. A
/// reservation is not proof that VRAM is physically allocated; it is a conservative scheduling
/// claim used before PaRuX admits extra resident models. Claims are bounded, expire automatically
/// and never override the exact requested model or Manual lock.
/// </summary>
public static class GpuResidencyReservationStore
{
    public sealed record Reservation(
        string Owner,
        string Workload,
        long Bytes,
        DateTime CreatedUtc,
        DateTime? ExpiresUtc,
        string Reason);

    private static readonly ConcurrentDictionary<string, Reservation> Reservations =
        new(StringComparer.OrdinalIgnoreCase);

    public static Reservation Set(
        string owner,
        string workload,
        long bytes,
        string? reason = null,
        TimeSpan? ttl = null)
    {
        owner = NormalizeOwner(owner);
        workload = NormalizeWorkload(workload);
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (ttl.HasValue && ttl.Value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl));

        DateTime now = DateTime.UtcNow;
        var reservation = new Reservation(
            owner,
            workload,
            bytes,
            now,
            ttl.HasValue ? now + ttl.Value : null,
            Bound(reason, 500));
        Reservations[owner] = reservation;
        return reservation;
    }

    public static void Release(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return;
        Reservations.TryRemove(owner.Trim(), out _);
    }

    public static long GetReservedBytes(params string[] excludedOwners)
    {
        DateTime now = DateTime.UtcNow;
        var excluded = new HashSet<string>(
            excludedOwners?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
                ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach ((string key, Reservation reservation) in Reservations.ToArray())
        {
            if (reservation.ExpiresUtc.HasValue && reservation.ExpiresUtc.Value <= now)
            {
                Reservations.TryRemove(key, out _);
                continue;
            }
            if (excluded.Contains(reservation.Owner)) continue;
            total = SaturatingAdd(total, Math.Max(0, reservation.Bytes));
        }
        return total;
    }

    public static IReadOnlyList<Reservation> Snapshot()
    {
        _ = GetReservedBytes(); // expires stale claims first
        return Reservations.Values
            .OrderBy(item => item.Workload, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Owner, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static void ClearForTests() => Reservations.Clear();

    private static string NormalizeOwner(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length is < 1 or > 128)
            throw new InvalidDataException("GPU reservation owner must be 1..128 characters.");
        return value;
    }

    private static string NormalizeWorkload(string value)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length is < 1 or > 64)
            throw new InvalidDataException("GPU reservation workload must be 1..64 characters.");
        return value;
    }

    private static string Bound(string? value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static long SaturatingAdd(long left, long right) =>
        right <= 0 ? left : left > long.MaxValue - right ? long.MaxValue : left + right;
}
