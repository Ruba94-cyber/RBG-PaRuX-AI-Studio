using PaRuX.Contracts;
using PaRuX.Server.EvalLab;

namespace PaRuX.Server.AutoLab;

/// <summary>
/// PaRuX AutoLab (Phase 14): experiments, compare, promote - built entirely on services that
/// already exist (EvalRunnerService for the actual scoring, AiProfileService/LocalAiPaths for
/// real model AND (2026-08-27) real adapter identity, LocalAiPaths.SetRoleOverride for promotion)
/// rather than a second execution/scoring engine. See AutoLabContracts.cs for the honest scope
/// this deliberately stays inside (no automatic model-swapping outside the explicit comparison
/// sweep below).
/// </summary>
public sealed class AutoLabService
{
    private readonly AutoLabStore _store;
    private readonly EvalStore _evalStore;
    private readonly EvalRunnerService _runner;
    private readonly AiProfileService _profiles;
    private readonly SmartRouterService _router;

    public AutoLabService(AutoLabStore store, EvalStore evalStore, EvalRunnerService runner, AiProfileService profiles, SmartRouterService router)
    {
        _store = store;
        _evalStore = evalStore;
        _runner = runner;
        _profiles = profiles;
        _router = router;
    }

    public async Task<ExperimentDto> CreateExperimentAsync(CreateExperimentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DatasetId) || _evalStore.GetDataset(request.DatasetId) == null)
            throw new InvalidDataException("datasetId does not reference an existing Eval Lab dataset.");

        // Captured NOW, from the real system, not assumed - if a manual model swap happens while
        // the run is still executing in the background, this experiment still honestly reflects
        // what was configured when the run started, which is the best any point-in-time record can do.
        AiProfileStatus profile = await _profiles.GetStatusAsync(ct);
        bool uncensored = string.Equals(profile.TextProfile, "UNCENSORED", StringComparison.OrdinalIgnoreCase);
        string modelPath = uncensored ? LocalAiPaths.UncensoredModel : LocalAiPaths.StandardModel;
        // Same real GGUF file EnsureRoleLoadedAsync's own JIT path would load alongside this
        // role's base model via --lora - captured here so it's recorded, not a second inference
        // mechanism claiming to load anything itself.
        string? adapterPath = LocalAiPaths.GetRoleAdapter(uncensored ? "uncensored" : "standard");

        EvalRunDto run = _runner.StartRun(request.DatasetId);
        _ = _runner.RunCasesAsync(run.Id, request.DatasetId, CancellationToken.None);

        var experiment = new ExperimentDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? "Experiment " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")
                : request.Name,
            DatasetId = request.DatasetId,
            EvalRunId = run.Id,
            ModelProfile = profile.TextProfile,
            ModelPath = modelPath,
            AdapterPath = adapterPath,
            CreatedUtc = DateTime.UtcNow,
        };
        return _store.Create(experiment);
    }

    /// <summary>Directive Phase 14's "multi-model automatic comparison", closed 2026-08-27 once
    /// Smart Router (Phase 8) existed to actually do the swapping. Sequential by necessity - this
    /// machine's runtime can only serve one role at a time, so each role's eval run is awaited to
    /// completion before the router swaps to the next role's model (swapping mid-run would
    /// corrupt that run's in-flight results, not just delay them). A role that can't be
    /// JIT-loaded (unassigned, missing file, hardware failure) is skipped with an honest reason
    /// and the sweep continues - one bad role must never abort every other role's comparison.</summary>
    public async Task<ComparisonSweepResponse> CreateComparisonSweepAsync(CreateComparisonSweepRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DatasetId) || _evalStore.GetDataset(request.DatasetId) == null)
            throw new InvalidDataException("datasetId does not reference an existing Eval Lab dataset.");
        if (request.Roles == null || request.Roles.Count == 0)
            throw new InvalidDataException("At least one role is required.");

        var results = new List<SweepRoleResult>();
        foreach (string role in request.Roles)
        {
            RouteDecisionDto routed = await _router.ResolveAsync(new RouteRequest { Role = role }, ct);
            if (!routed.Success || routed.ModelPath == null)
            {
                results.Add(new SweepRoleResult { Role = role, Skipped = true, SkipReason = routed.Message });
                continue;
            }

            EvalRunDto run = _runner.StartRun(request.DatasetId);
            // CancellationToken.None deliberately for the background execution itself - matches
            // CreateExperimentAsync's own existing pattern and CapabilityProfilerService's
            // documented fix (a request-scoped token cancels once the eventual HTTP response is
            // sent, which would corrupt this in-progress run). This method's own await below
            // still honors the caller's real ct for the sweep as a whole.
            _ = _runner.RunCasesAsync(run.Id, request.DatasetId, CancellationToken.None);
            await WaitForRunAsync(run.Id, ct);

            ExperimentDto created = _store.Create(new ExperimentDto
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Sweep: " + role + " " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm"),
                DatasetId = request.DatasetId,
                EvalRunId = run.Id,
                ModelProfile = routed.ResolvedRole,
                ModelPath = routed.ModelPath,
                AdapterPath = LocalAiPaths.GetRoleAdapter(role),
                CreatedUtc = DateTime.UtcNow,
            });
            results.Add(new SweepRoleResult { Role = role, Skipped = false, Experiment = created });
        }

        // Reuses Compare's own recommendation logic against exactly the experiments this sweep
        // created - no second "which one is best" algorithm.
        List<string> createdIds = results.Where(r => r.Experiment != null).Select(r => r.Experiment!.Id).ToList();
        ExperimentComparisonResponse comparison = Compare(createdIds);
        return new ComparisonSweepResponse
        {
            Results = results,
            RecommendedExperimentId = comparison.RecommendedExperimentId,
            RecommendationReason = comparison.RecommendationReason,
        };
    }

    /// <summary>Polls until the run leaves "running" or a generous 10-minute-per-role timeout
    /// elapses - whichever comes first. Never fabricates a result: on timeout, returns whatever
    /// the run's real current state is (most likely still "running"), which Compare's own
    /// "completed AND scored" filter already treats as not-yet-recommendable.</summary>
    private async Task<EvalRunDto?> WaitForRunAsync(string runId, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            EvalRunDto? run = _evalStore.GetRun(runId);
            if (run == null || !string.Equals(run.Status, EvalRunStatus.Running, StringComparison.OrdinalIgnoreCase))
                return run;
            await Task.Delay(1000, ct);
        }
        return _evalStore.GetRun(runId);
    }

    public List<ExperimentDto> List() => _store.List();
    public ExperimentDto? Get(string id) => _store.Get(id);

    /// <summary>Joins each experiment with its live Eval run status/counts and proposes the
    /// highest-pass-rate experiment among those whose run has actually finished. Never guesses
    /// from a still-running or errored run, and never applies the recommendation itself - see
    /// Promote for the one explicit action that does.</summary>
    public ExperimentComparisonResponse Compare(IEnumerable<string> experimentIds)
    {
        var entries = new List<ExperimentComparisonEntry>();
        foreach (string id in experimentIds)
        {
            ExperimentDto? experiment = _store.Get(id);
            if (experiment == null) continue;
            EvalRunDto? run = _evalStore.GetRun(experiment.EvalRunId);
            int pass = run?.PassCount ?? 0, fail = run?.FailCount ?? 0, inconclusive = run?.InconclusiveCount ?? 0, error = run?.ErrorCount ?? 0;
            int total = pass + fail + inconclusive;
            entries.Add(new ExperimentComparisonEntry
            {
                ExperimentId = experiment.Id,
                Name = experiment.Name,
                ModelPath = experiment.ModelPath,
                AdapterPath = experiment.AdapterPath,
                RunStatus = run?.Status ?? "unknown",
                PassCount = pass,
                FailCount = fail,
                InconclusiveCount = inconclusive,
                ErrorCount = error,
                PassRate = total > 0 ? (double)pass / total : 0,
            });
        }

        // Completed AND has at least one scored case - a run where every case errored (e.g. no
        // model reachable) reports PassCount=FailCount=InconclusiveCount=0, indistinguishable from
        // an empty dataset; live-verified against the real server (2026-08-26) that this really
        // happens with no model loaded, and recommending a 0/0 experiment as "best" would be
        // fabricating a signal that doesn't exist.
        List<ExperimentComparisonEntry> finished = entries
            .Where(e => string.Equals(e.RunStatus, EvalRunStatus.Completed, StringComparison.OrdinalIgnoreCase))
            .Where(e => e.PassCount + e.FailCount + e.InconclusiveCount > 0)
            .ToList();

        string? recommendedId = null;
        string? reason = null;
        if (finished.Count > 0)
        {
            ExperimentComparisonEntry best = finished
                .OrderByDescending(e => e.PassRate)
                .ThenBy(e => e.FailCount)
                .First();
            int total = best.PassCount + best.FailCount + best.InconclusiveCount;
            recommendedId = best.ExperimentId;
            // Invariant, not ambient: this string crosses the API to every client, and on a host
            // whose culture formats percentages as "90 %" the same run would describe itself
            // differently than on one that writes "90%".
            // "{0:P0}" is not it: even InvariantCulture renders that as "90 %" with a space, and a
            // machine-generated explanation should not vary by a space depending on who formats it.
            reason = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Highest pass rate ({0:0}%, {1}/{2} cases) among experiments with a completed run.",
                best.PassRate * 100, best.PassCount, total);
        }

        return new ExperimentComparisonResponse
        {
            Experiments = entries,
            RecommendedExperimentId = recommendedId,
            RecommendationReason = reason,
        };
    }

    /// <summary>"Promote" means exactly one thing: point the given role at this experiment's
    /// captured model, via the existing Phase 6 override mechanism - reused, not duplicated. This
    /// never runs automatically; it is only ever a direct, explicit call from the caller. Widened
    /// (2026-08-27, return-to-close pass) from a hardcoded standard/uncensored check to the full
    /// generalized text-role set, now that Phase 6 exists - an experiment run against any model
    /// can be promoted to any text role, not just the original two.</summary>
    public ExperimentDto Promote(string id, string role)
    {
        ExperimentDto experiment = _store.Get(id) ?? throw new InvalidDataException("Experiment not found.");
        if (LocalAiPaths.NormalizeTextRole(role) == null)
            throw new InvalidDataException("Unknown role \"" + role + "\". Known roles: " + string.Join(", ", LocalAiPaths.KnownTextRoles) + ".");
        LocalAiPaths.SetRoleOverride(role, experiment.ModelPath);
        return experiment;
    }
}
