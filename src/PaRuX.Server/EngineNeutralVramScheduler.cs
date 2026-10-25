namespace PaRuX.Server;

public enum VramWorkloadPriority
{
    Background = 0,
    Prefetch = 1,
    Interactive = 2,
    Critical = 3
}

public sealed record VramAdapterCapacity(
    int AdapterIndex,
    string Name,
    long DedicatedBytes,
    double TransportPenalty = 0,
    bool IsPreferred = true);

public sealed record VramAllocationSlice(int AdapterIndex, long Bytes);

public sealed record VramResidentAllocation(
    string EngineId,
    string ModelVariantId,
    IReadOnlyList<VramAllocationSlice> Slices,
    VramWorkloadPriority Priority,
    bool InUse,
    bool Pinned,
    DateTime LastUsedUtc)
{
    public long TotalResidentBytes => Slices.Sum(slice => Math.Max(0, slice.Bytes));
}

public sealed record VramLoadRequest(
    string EngineId,
    string ModelVariantId,
    long EstimatedResidentBytes,
    VramWorkloadPriority Priority,
    bool AllowMultiGpu,
    bool EngineSupportsMultiGpu,
    IReadOnlyList<int>? PreferredAdapterIndices = null);

public enum VramScheduleDecision
{
    KeepResident = 0,
    Load = 1,
    LoadAfterEviction = 2,
    Reject = 3
}

public sealed record VramSchedulePlan(
    VramScheduleDecision Decision,
    IReadOnlyList<VramAllocationSlice> TargetSlices,
    IReadOnlyList<VramResidentAllocation> Evict,
    long RequestedBytes,
    long TotalUsableDedicatedBytes,
    string Reason)
{
    public bool CanLoad => Decision != VramScheduleDecision.Reject;
}

/// <summary>
/// Engine-neutral VRAM planner for llama.cpp, ExLlamaV3, TensorRT-RTX/Windows ML and future local
/// backends. The scheduler operates only on dedicated adapter memory. Shared system memory is never
/// silently counted as VRAM, which prevents iGPU/shared-memory totals from producing unsafe plans.
///
/// The planner is deliberately pure: it does not kill processes or load models. Engine adapters
/// execute a returned plan and must report the resulting real allocation/telemetry back to PaRuX.
/// This keeps policy, measurement and engine-specific lifecycle separate and rollback-safe.
/// </summary>
public sealed class EngineNeutralVramScheduler
{
    private const long MiB = 1024L * 1024L;
    private const long MinimumHeadroomBytes = 1024L * MiB;
    private const double MaximumDedicatedFraction = 0.90;

    public VramSchedulePlan Plan(
        IReadOnlyList<VramAdapterCapacity> adapters,
        IReadOnlyList<VramResidentAllocation> residents,
        VramLoadRequest request)
    {
        Validate(request);
        if (adapters == null || adapters.Count == 0)
            return Reject(request, 0, "No dedicated GPU adapter capacity is available.");

        List<VramAdapterCapacity> usableAdapters = adapters
            .Where(adapter => adapter.DedicatedBytes > 0)
            .OrderBy(adapter => PreferenceRank(adapter.AdapterIndex, request.PreferredAdapterIndices))
            .ThenBy(adapter => adapter.TransportPenalty)
            .ThenByDescending(adapter => adapter.DedicatedBytes)
            .ThenBy(adapter => adapter.AdapterIndex)
            .ToList();
        if (usableAdapters.Count == 0)
            return Reject(request, 0, "GPU inventory reports no dedicated video memory.");

        VramResidentAllocation? alreadyResident = residents.FirstOrDefault(resident =>
            string.Equals(resident.EngineId, request.EngineId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(resident.ModelVariantId, request.ModelVariantId, StringComparison.Ordinal));
        if (alreadyResident != null)
        {
            return new VramSchedulePlan(
                VramScheduleDecision.KeepResident,
                alreadyResident.Slices.ToList(),
                Array.Empty<VramResidentAllocation>(),
                request.EstimatedResidentBytes,
                usableAdapters.Sum(adapter => UsableBytes(adapter.DedicatedBytes)),
                "The requested engine/model variant is already resident; reuse it instead of reloading VRAM.");
        }

        Dictionary<int, long> budgets = usableAdapters.ToDictionary(
            adapter => adapter.AdapterIndex,
            adapter => UsableBytes(adapter.DedicatedBytes));
        long totalBudget = SaturatingSum(budgets.Values);
        Dictionary<int, long> used = BuildUsedByAdapter(residents, budgets.Keys);

        // Prefer a single adapter first. This minimizes cross-device traffic and is especially
        // important for eGPU topologies where a theoretically larger aggregate pool can lose badly
        // to one local GPU in latency and sustained decode throughput.
        foreach (VramAdapterCapacity adapter in usableAdapters)
        {
            VramSchedulePlan? single = TryPlanSingleAdapter(adapter.AdapterIndex, budgets, used, residents, request, totalBudget);
            if (single != null) return single;
        }

        if (!request.AllowMultiGpu || !request.EngineSupportsMultiGpu || usableAdapters.Count < 2)
            return Reject(request, totalBudget,
                request.EngineSupportsMultiGpu
                    ? "The model does not fit one adapter and this request does not allow multi-GPU placement."
                    : "The model does not fit one adapter and the selected engine does not support multi-GPU placement.");

        return PlanMultiGpu(usableAdapters, budgets, used, residents, request, totalBudget);
    }

    private static VramSchedulePlan? TryPlanSingleAdapter(
        int adapterIndex,
        IReadOnlyDictionary<int, long> budgets,
        IReadOnlyDictionary<int, long> used,
        IReadOnlyList<VramResidentAllocation> residents,
        VramLoadRequest request,
        long totalBudget)
    {
        long budget = budgets[adapterIndex];
        if (request.EstimatedResidentBytes > budget) return null;
        long currentUsed = used.TryGetValue(adapterIndex, out long value) ? value : 0;
        long free = Math.Max(0, budget - currentUsed);
        if (request.EstimatedResidentBytes <= free)
        {
            return new VramSchedulePlan(
                VramScheduleDecision.Load,
                new[] { new VramAllocationSlice(adapterIndex, request.EstimatedResidentBytes) },
                Array.Empty<VramResidentAllocation>(),
                request.EstimatedResidentBytes,
                totalBudget,
                "The model fits the preferred single-adapter budget without eviction.");
        }

        long needed = request.EstimatedResidentBytes - free;
        List<VramResidentAllocation> evictable = residents
            .Where(resident => resident.Slices.Any(slice => slice.AdapterIndex == adapterIndex && slice.Bytes > 0))
            .Where(resident => CanEvict(resident, request))
            .OrderBy(resident => resident.Priority)
            .ThenBy(resident => resident.LastUsedUtc)
            .ThenByDescending(resident => resident.TotalResidentBytes)
            .ToList();

        long reclaimed = 0;
        var evict = new List<VramResidentAllocation>();
        foreach (VramResidentAllocation resident in evictable)
        {
            long onAdapter = resident.Slices
                .Where(slice => slice.AdapterIndex == adapterIndex)
                .Sum(slice => Math.Max(0, slice.Bytes));
            if (onAdapter <= 0) continue;
            reclaimed = SaturatingAdd(reclaimed, onAdapter);
            evict.Add(resident);
            if (reclaimed >= needed) break;
        }
        if (reclaimed < needed) return null;

        return new VramSchedulePlan(
            VramScheduleDecision.LoadAfterEviction,
            new[] { new VramAllocationSlice(adapterIndex, request.EstimatedResidentBytes) },
            evict,
            request.EstimatedResidentBytes,
            totalBudget,
            $"Evict {evict.Count} idle lower-priority resident allocation(s) and keep the new model on one adapter.");
    }

    private static VramSchedulePlan PlanMultiGpu(
        IReadOnlyList<VramAdapterCapacity> adapters,
        IReadOnlyDictionary<int, long> budgets,
        IReadOnlyDictionary<int, long> used,
        IReadOnlyList<VramResidentAllocation> residents,
        VramLoadRequest request,
        long totalBudget)
    {
        Dictionary<int, long> projectedUsed = used.ToDictionary(pair => pair.Key, pair => pair.Value);
        long immediatelyFree = adapters.Sum(adapter =>
            Math.Max(0, budgets[adapter.AdapterIndex] - projectedUsed.GetValueOrDefault(adapter.AdapterIndex)));

        var evict = new List<VramResidentAllocation>();
        if (immediatelyFree < request.EstimatedResidentBytes)
        {
            IEnumerable<VramResidentAllocation> candidates = residents
                .Where(resident => CanEvict(resident, request))
                .OrderBy(resident => resident.Priority)
                .ThenBy(resident => resident.LastUsedUtc)
                .ThenByDescending(resident => resident.TotalResidentBytes);

            long free = immediatelyFree;
            foreach (VramResidentAllocation resident in candidates)
            {
                long reclaimed = 0;
                foreach (VramAllocationSlice slice in resident.Slices)
                {
                    if (!projectedUsed.ContainsKey(slice.AdapterIndex)) continue;
                    long bytes = Math.Max(0, slice.Bytes);
                    projectedUsed[slice.AdapterIndex] = Math.Max(0, projectedUsed[slice.AdapterIndex] - bytes);
                    reclaimed = SaturatingAdd(reclaimed, bytes);
                }
                if (reclaimed <= 0) continue;
                evict.Add(resident);
                free = SaturatingAdd(free, reclaimed);
                if (free >= request.EstimatedResidentBytes) break;
            }
            if (free < request.EstimatedResidentBytes)
                return Reject(request, totalBudget,
                    "Aggregate dedicated VRAM remains insufficient without evicting pinned, active, or equal/higher-priority work.");
        }

        long remaining = request.EstimatedResidentBytes;
        var slices = new List<VramAllocationSlice>();
        foreach (VramAdapterCapacity adapter in adapters)
        {
            long free = Math.Max(0, budgets[adapter.AdapterIndex] - projectedUsed.GetValueOrDefault(adapter.AdapterIndex));
            if (free <= 0) continue;
            long take = Math.Min(free, remaining);
            if (take > 0) slices.Add(new VramAllocationSlice(adapter.AdapterIndex, take));
            remaining -= take;
            if (remaining == 0) break;
        }
        if (remaining > 0)
            return Reject(request, totalBudget, "Multi-GPU allocation could not produce a complete placement plan.");

        return new VramSchedulePlan(
            evict.Count == 0 ? VramScheduleDecision.Load : VramScheduleDecision.LoadAfterEviction,
            slices,
            evict,
            request.EstimatedResidentBytes,
            totalBudget,
            evict.Count == 0
                ? $"Place the model across {slices.Count} adapters after single-GPU placement failed."
                : $"Evict {evict.Count} idle lower-priority allocation(s), then place the model across {slices.Count} adapters.");
    }

    private static bool CanEvict(VramResidentAllocation resident, VramLoadRequest request)
    {
        if (resident.Pinned || resident.InUse) return false;
        if (request.Priority == VramWorkloadPriority.Critical) return true;
        return resident.Priority < request.Priority;
    }

    private static Dictionary<int, long> BuildUsedByAdapter(
        IReadOnlyList<VramResidentAllocation> residents,
        IEnumerable<int> adapterIndices)
    {
        var used = adapterIndices.Distinct().ToDictionary(index => index, _ => 0L);
        foreach (VramResidentAllocation resident in residents)
        {
            foreach (VramAllocationSlice slice in resident.Slices)
            {
                if (!used.ContainsKey(slice.AdapterIndex)) continue;
                used[slice.AdapterIndex] = SaturatingAdd(used[slice.AdapterIndex], Math.Max(0, slice.Bytes));
            }
        }
        return used;
    }

    internal static long UsableBytes(long dedicatedBytes)
    {
        if (dedicatedBytes <= 0) return 0;
        long fraction = (long)Math.Floor(dedicatedBytes * MaximumDedicatedFraction);
        long reserveLimited = Math.Max(0, dedicatedBytes - MinimumHeadroomBytes);
        return Math.Max(0, Math.Min(fraction, reserveLimited));
    }

    private static int PreferenceRank(int adapterIndex, IReadOnlyList<int>? preferred)
    {
        if (preferred == null || preferred.Count == 0) return int.MaxValue / 2;
        for (int i = 0; i < preferred.Count; i++)
            if (preferred[i] == adapterIndex) return i;
        return int.MaxValue / 2;
    }

    private static VramSchedulePlan Reject(VramLoadRequest request, long totalBudget, string reason) =>
        new(
            VramScheduleDecision.Reject,
            Array.Empty<VramAllocationSlice>(),
            Array.Empty<VramResidentAllocation>(),
            request.EstimatedResidentBytes,
            totalBudget,
            reason);

    private static void Validate(VramLoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.EngineId)) throw new ArgumentException("Engine id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ModelVariantId)) throw new ArgumentException("Model variant id is required.", nameof(request));
        if (request.EstimatedResidentBytes <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Estimated resident bytes must be positive.");
    }

    private static long SaturatingSum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (long value in values) total = SaturatingAdd(total, Math.Max(0, value));
        return total;
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && long.MaxValue - left < right ? long.MaxValue : left + right;
}
