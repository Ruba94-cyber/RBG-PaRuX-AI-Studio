namespace PaRuX.Server;

/// <summary>
/// Deterministic, engine-neutral residency planner used by PaRuX before launching a multi-model
/// serving pool. It does not inspect GPUs itself and therefore stays unit-testable: callers provide
/// the physical budget, explicit reservations and measured/estimated footprints. The planner's
/// output is advisory; existing runtime health checks and router self-tests remain authoritative.
/// </summary>
public static class VramResidencyPlanner
{
    public sealed record Candidate(
        string Id,
        long ResidentBytes,
        bool Required,
        bool ManualPinned,
        DateTime? LastUsedUtc,
        int RecentUseCount,
        string Workload,
        double QualityWeight = 1.0,
        double SwitchCostWeight = 1.0);

    public sealed record Decision(
        string Id,
        long ResidentBytes,
        bool KeepResident,
        double ValueScore,
        string Reason);

    public sealed record Plan(
        long PhysicalBytes,
        long ReservedBytes,
        long UsableBytes,
        long SelectedBytes,
        IReadOnlyList<Decision> Decisions)
    {
        public IReadOnlyList<Decision> Selected => Decisions.Where(item => item.KeepResident).ToList();
        public IReadOnlyList<Decision> Evicted => Decisions.Where(item => !item.KeepResident).ToList();
    }

    public static Plan Build(
        IEnumerable<Candidate> candidates,
        long physicalBytes,
        long reservedBytes,
        string activeWorkload,
        DateTime nowUtc,
        TimeSpan idleTtl,
        int maxResidents = 3)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (physicalBytes < 0) throw new ArgumentOutOfRangeException(nameof(physicalBytes));
        if (reservedBytes < 0) throw new ArgumentOutOfRangeException(nameof(reservedBytes));
        if (maxResidents < 1) throw new ArgumentOutOfRangeException(nameof(maxResidents));
        if (idleTtl < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTtl));

        string workload = NormalizeWorkload(activeWorkload);
        long usable = Math.Max(0, physicalBytes - Math.Min(physicalBytes, reservedBytes));
        var normalized = candidates
            .Where(candidate => candidate != null)
            .Select(candidate => Normalize(candidate, workload, nowUtc, idleTtl))
            .GroupBy(candidate => candidate.Candidate.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Candidate.Required)
                .ThenByDescending(item => item.Candidate.ManualPinned)
                .ThenByDescending(item => item.Score)
                .First())
            .ToList();

        if (normalized.Count == 0)
            return new Plan(physicalBytes, reservedBytes, usable, 0, Array.Empty<Decision>());

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long used = 0;

        // Required candidates are selected first. A required target is allowed to exceed the
        // advisory budget: the caller must still attempt the exact requested model and let the
        // established runtime/OOM path fail honestly rather than silently switching models.
        foreach (var item in normalized
            .Where(item => item.Candidate.Required)
            .OrderByDescending(item => item.Candidate.ManualPinned)
            .ThenByDescending(item => item.Score))
        {
            if (selected.Count >= maxResidents) break;
            selected.Add(item.Candidate.Id);
            used = SaturatingAdd(used, item.Candidate.ResidentBytes);
        }

        foreach (var item in normalized
            .Where(item => !selected.Contains(item.Candidate.Id))
            .OrderByDescending(item => item.Candidate.ManualPinned)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.ResidentBytes))
        {
            if (selected.Count >= maxResidents) break;
            if (item.Expired && !item.Candidate.ManualPinned) continue;
            long proposed = SaturatingAdd(used, item.Candidate.ResidentBytes);
            if (usable > 0 && proposed > usable) continue;
            selected.Add(item.Candidate.Id);
            used = proposed;
        }

        var decisions = normalized
            .Select(item =>
            {
                bool keep = selected.Contains(item.Candidate.Id);
                string reason;
                if (keep && item.Candidate.Required) reason = item.Candidate.ManualPinned
                    ? "Required target and Manual-pinned."
                    : "Required target for the current request.";
                else if (keep && item.Candidate.ManualPinned) reason = "Manual-pinned model retained within the VRAM budget.";
                else if (keep) reason = item.WorkloadMatch
                    ? "Retained for active-workload value and recent use."
                    : "Retained for recent-use/switch-cost value.";
                else if (item.Expired) reason = "Evicted because the model exceeded the idle TTL.";
                else if (selected.Count >= maxResidents) reason = "Evicted because the resident-model count limit was reached.";
                else reason = "Evicted because the VRAM reservation/budget would be exceeded.";
                return new Decision(item.Candidate.Id, item.Candidate.ResidentBytes, keep, item.Score, reason);
            })
            .OrderByDescending(item => item.KeepResident)
            .ThenByDescending(item => item.ValueScore)
            .ToList();

        return new Plan(physicalBytes, reservedBytes, usable, used, decisions);
    }

    private sealed record Scored(Candidate Candidate, double Score, bool WorkloadMatch, bool Expired);

    private static Scored Normalize(Candidate candidate, string activeWorkload, DateTime nowUtc, TimeSpan idleTtl)
    {
        if (string.IsNullOrWhiteSpace(candidate.Id))
            throw new InvalidDataException("VRAM residency candidate id is required.");
        if (candidate.ResidentBytes < 0)
            throw new InvalidDataException("VRAM residency candidate footprint cannot be negative.");

        DateTime? lastUsed = candidate.LastUsedUtc?.ToUniversalTime();
        bool expired = lastUsed.HasValue && idleTtl > TimeSpan.Zero && nowUtc - lastUsed.Value > idleTtl;
        bool workloadMatch = string.Equals(NormalizeWorkload(candidate.Workload), activeWorkload, StringComparison.Ordinal);

        double recency = 0;
        if (lastUsed.HasValue)
        {
            double minutes = Math.Max(0, (nowUtc - lastUsed.Value).TotalMinutes);
            recency = 1.0 / (1.0 + minutes / 10.0);
        }
        double use = Math.Log2(1 + Math.Max(0, candidate.RecentUseCount));
        double score =
            (candidate.Required ? 1_000_000 : 0)
            + (candidate.ManualPinned ? 100_000 : 0)
            + (workloadMatch ? 1_000 : 0)
            + recency * 500
            + use * 100
            + Math.Clamp(candidate.QualityWeight, 0, 10) * 25
            + Math.Clamp(candidate.SwitchCostWeight, 0, 10) * 10;
        if (expired && !candidate.ManualPinned && !candidate.Required) score -= 10_000;
        return new Scored(candidate, score, workloadMatch, expired);
    }

    private static string NormalizeWorkload(string? workload)
    {
        string value = (workload ?? "chat").Trim().ToLowerInvariant();
        return value.Length == 0 ? "chat" : value;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0) return left;
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }
}
