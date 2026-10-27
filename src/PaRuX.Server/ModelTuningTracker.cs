using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Observable state of the automatic "tuning for your PC" jobs (text calibration and media flag measurement), published at
/// GET /api/v1/models/tuning. In-memory and bounded: the durable truth stays in the calibration registry / profile stores.
/// </summary>
internal sealed class ModelTuningTracker
{
    internal const int MaximumItems = 40;
    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow;
    private readonly List<ModelTuningItemDto> _items = new();

    internal ModelTuningTracker(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    internal static string KeyOf(string kind, string modelPath, string workload) =>
        kind + "|" + modelPath.ToLowerInvariant() + "|" + workload;

    /// <summary>(Re)queues a job. A job that is already running keeps running.</summary>
    internal string Queued(string kind, string modelPath, string label, string workload, string? reason = null)
    {
        string id = KeyOf(kind, modelPath, workload);
        lock (_gate)
        {
            ModelTuningItemDto item = Upsert(id, kind, modelPath, label, workload);
            if (item.State == ModelTuningStates.Running) return id;
            Set(item, ModelTuningStates.Queued, reason ?? "Waiting for the PC to be idle", null);
        }
        return id;
    }

    internal string Running(string kind, string modelPath, string label, string workload, string? reason = null)
    {
        string id = KeyOf(kind, modelPath, workload);
        lock (_gate) Set(Upsert(id, kind, modelPath, label, workload), ModelTuningStates.Running, reason ?? "Measuring the fastest settings on this PC", null);
        return id;
    }

    internal void Applied(string id, double gainPercent, string? reason = null)
    {
        lock (_gate) if (Find(id) is { } item) Set(item, ModelTuningStates.Applied, reason ?? $"Applied: +{gainPercent:0}% faster", gainPercent);
    }

    internal void Skipped(string id, string reason)
    {
        lock (_gate) if (Find(id) is { } item) Set(item, ModelTuningStates.Skipped, reason, null);
    }

    /// <summary>
    /// Terminal "nothing faster than the current settings" state. Still published as <see cref="ModelTuningStates.Skipped"/> (only
    /// five states exist) but never a dead end: the reason states what was measured and the suggestions say what could help next.
    /// </summary>
    internal void Optimal(string id, string reason, IReadOnlyList<ModelTuningSuggestionDto>? suggestions = null)
    {
        lock (_gate)
            if (Find(id) is { } item)
            {
                Set(item, ModelTuningStates.Skipped, reason, null);
                item.Suggestions = Bound(suggestions);
            }
    }

    /// <summary>Replaces the suggestions of a job (used when the slower, network-backed ones arrive after the result).</summary>
    internal void SetSuggestions(string id, IReadOnlyList<ModelTuningSuggestionDto>? suggestions)
    {
        lock (_gate) if (Find(id) is { } item && ModelTuningStates.IsTerminal(item.State)) { item.Suggestions = Bound(suggestions); item.UpdatedUtc = _utcNow(); }
    }

    internal const int MaximumSuggestions = 6;
    private static List<ModelTuningSuggestionDto> Bound(IReadOnlyList<ModelTuningSuggestionDto>? suggestions) =>
        suggestions is null ? new() : suggestions.Take(MaximumSuggestions).Select(CloneSuggestion).ToList();

    private static ModelTuningSuggestionDto CloneSuggestion(ModelTuningSuggestionDto s) => new()
    {
        Kind = s.Kind, Title = s.Title, Detail = s.Detail, ExpectedGain = s.ExpectedGain, ActionKind = s.ActionKind,
        RepoId = s.RepoId, FileName = s.FileName, SizeBytes = s.SizeBytes
    };

    internal void Failed(string id, string reason)
    {
        lock (_gate) if (Find(id) is { } item) Set(item, ModelTuningStates.Failed, reason, null);
    }

    /// <summary>Back to the queue with the reason it was paused (the user started generating, Gaming began...).</summary>
    internal void Requeue(string id, string reason)
    {
        lock (_gate) if (Find(id) is { } item) Set(item, ModelTuningStates.Queued, reason, null);
    }

    /// <summary>Updates the waiting reason of every queued job (why the last scheduling step did not start it).</summary>
    internal void Waiting(string reason)
    {
        lock (_gate)
            foreach (ModelTuningItemDto item in _items.Where(i => i.State == ModelTuningStates.Queued))
                if (item.Reason != reason) { item.Reason = reason; item.UpdatedUtc = _utcNow(); }
    }

    internal void SetProposal(string id, ModelTuningProposalDto? proposal)
    {
        lock (_gate) if (Find(id) is { } item) { item.Proposal = proposal; item.UpdatedUtc = _utcNow(); }
    }

    internal ModelTuningItemDto? Get(string id)
    {
        lock (_gate) return Find(id) is { } item ? Clone(item) : null;
    }

    internal ModelTuningStatusDto Snapshot()
    {
        lock (_gate)
        {
            List<ModelTuningItemDto> items = _items.OrderByDescending(i => i.UpdatedUtc).Select(Clone).ToList();
            return new ModelTuningStatusDto { Items = items, Active = items.Any(i => !ModelTuningStates.IsTerminal(i.State)) };
        }
    }

    private ModelTuningItemDto? Find(string id) => _items.FirstOrDefault(i => i.Id == id);

    private ModelTuningItemDto Upsert(string id, string kind, string path, string label, string workload)
    {
        ModelTuningItemDto? item = Find(id);
        if (item is not null) { item.ModelLabel = label; return item; }
        item = new ModelTuningItemDto { Id = id, Kind = kind, ModelPath = path, ModelLabel = label, Workload = workload, UpdatedUtc = _utcNow() };
        _items.Add(item);
        while (_items.Count > MaximumItems)
        {
            ModelTuningItemDto drop = _items.Where(i => i != item && ModelTuningStates.IsTerminal(i.State)).OrderBy(i => i.UpdatedUtc).FirstOrDefault()
                ?? _items.Where(i => i != item).OrderBy(i => i.UpdatedUtc).First();
            _items.Remove(drop);
        }
        return item;
    }

    private void Set(ModelTuningItemDto item, string state, string? reason, double? gain)
    {
        item.State = state;
        if (!ModelTuningStates.IsTerminal(state)) item.Suggestions = new List<ModelTuningSuggestionDto>();
        item.Reason = reason;
        item.GainPercent = gain;
        item.UpdatedUtc = _utcNow();
    }

    private static ModelTuningItemDto Clone(ModelTuningItemDto i) => new()
    {
        Id = i.Id, Kind = i.Kind, ModelLabel = i.ModelLabel, ModelPath = i.ModelPath, Workload = i.Workload, State = i.State,
        GainPercent = i.GainPercent, Reason = i.Reason, UpdatedUtc = i.UpdatedUtc,
        Proposal = i.Proposal is null ? null : new ModelTuningProposalDto { Kind = i.Proposal.Kind, RepoId = i.Proposal.RepoId, FileName = i.Proposal.FileName, SizeBytes = i.Proposal.SizeBytes },
        Suggestions = i.Suggestions.Select(CloneSuggestion).ToList()
    };
}

/// <summary>Measured speed-up of a finished text calibration: the winner's decode speed against the first (baseline) completed candidate.</summary>
internal static class ModelTuningGain
{
    /// <summary>Below this the saved profile is not worth announcing; it is reported as "no faster profile found".</summary>
    internal const double MinimumReportedPercent = 5.0;

    internal static double? Compute(RuntimeOptimizationRun run)
    {
        if (run.Winner is null) return null;
        List<RuntimeOptimizationServerResult> done = run.ServerCandidates.Where(c => c.Status == "completed" && c.DecodeTokensPerSecond > 0).ToList();
        RuntimeOptimizationServerResult? baseline = done.FirstOrDefault();
        RuntimeOptimizationServerResult? winner = done.FirstOrDefault(c => c.Profile.Id == run.Winner.Id);
        if (baseline is null || winner is null || ReferenceEquals(baseline, winner)) return 0;
        return (winner.DecodeTokensPerSecond!.Value / baseline.DecodeTokensPerSecond!.Value - 1.0) * 100.0;
    }
}
