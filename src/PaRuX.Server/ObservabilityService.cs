using PaRuX.Contracts;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.AutoLab;
using PaRuX.Server.Automation;
using PaRuX.Server.Orchestration;
using PaRuX.Server.Workflow;

namespace PaRuX.Server;

/// <summary>
/// Unified read-only observability over records already owned by existing subsystems. It deliberately
/// adds no event bus or cloud telemetry. Timeline merges source records; Dashboard computes a current
/// cross-surface health/benchmark snapshot from those same authoritative owners.
/// </summary>
public sealed class ObservabilityService
{
    private readonly TaskStore _tasks;
    private readonly ModelDownloadService _models;
    private readonly DatasetDownloadService _datasets;
    private readonly TrainingLabService _training;
    private readonly AdapterManagerService _adapters;
    private readonly AutoLabService _autoLab;
    private readonly ArtifactStore _artifacts;
    private readonly AutomationStore _automations;
    private readonly WorkflowStore _workflows;
    private readonly SmartRouterService? _router;
    private readonly ModelBenchmarkStore? _modelBenchmarks;
    private readonly RuntimeRegressionEvidenceStore _runtimeRegressions = new();

    public ObservabilityService(
        TaskStore tasks, ModelDownloadService models, DatasetDownloadService datasets,
        TrainingLabService training, AdapterManagerService adapters, AutoLabService autoLab,
        ArtifactStore artifacts, AutomationStore automations, WorkflowStore workflows,
        SmartRouterService? router = null, ModelBenchmarkStore? modelBenchmarks = null)
    {
        _tasks = tasks;
        _models = models;
        _datasets = datasets;
        _training = training;
        _adapters = adapters;
        _autoLab = autoLab;
        _artifacts = artifacts;
        _automations = automations;
        _workflows = workflows;
        _router = router;
        _modelBenchmarks = modelBenchmarks;
    }

    public ObservabilityDashboardResponse GetDashboard(int recentLimit = 30, int benchmarkLimit = 12)
    {
        recentLimit = Math.Clamp(recentLimit, 5, 200);
        benchmarkLimit = Math.Clamp(benchmarkLimit, 1, 50);
        List<AgentTaskDto> tasks = _tasks.List().ToList();
        List<TrainingJobStatus> training = _training.List().ToList();
        List<ModelDownloadStatus> downloads = _models.List().ToList();
        List<DatasetDownloadStatus> datasets = _datasets.List().ToList();
        List<RouteDecisionDto> routeDecisions = _router?.ListDecisions().ToList() ?? new List<RouteDecisionDto>();
        ExecutionProfileStatus profile = ExecutionProfileStore.Current.Get();

        List<ModelBenchmarkLeaderboardEntry> leaderboard = new();
        if (_modelBenchmarks != null)
        {
            leaderboard = _modelBenchmarks.List()
                .Where(run => run.Status == "completed" && run.TokensPerSecond.HasValue)
                .GroupBy(run => new
                {
                    Path = run.ModelPath,
                    Backend = run.Backend ?? string.Empty,
                    Hardware = run.HardwareKey ?? string.Empty
                })
                .Select(group => group.OrderByDescending(run => run.CompletedUtc ?? run.StartedUtc).First())
                .OrderByDescending(run => run.TokensPerSecond!.Value)
                .Take(benchmarkLimit)
                .Select(run => new ModelBenchmarkLeaderboardEntry
                {
                    ModelPath = run.ModelPath,
                    Label = run.Label,
                    Backend = run.Backend ?? string.Empty,
                    HardwareKey = run.HardwareKey ?? string.Empty,
                    TokensPerSecond = run.TokensPerSecond!.Value,
                    Repetitions = run.Repetitions,
                    MeasuredUtc = run.CompletedUtc ?? run.StartedUtc
                }).ToList();
        }

        int activeTasks = tasks.Count(task => task.Status is AgentTaskStatus.Queued or AgentTaskStatus.Running
            or AgentTaskStatus.Waiting or AgentTaskStatus.Verifying or AgentTaskStatus.Blocked);
        int activeTraining = training.Count(job => job.Status is "queued" or "running");
        int activeDownloads = downloads.Count(item => item.Status is "queued" or "downloading" or "running")
            + datasets.Count(item => item.Status is "queued" or "downloading" or "running");
        int routeSuccess = routeDecisions.Count(decision => decision.Success);
        double? averageRouterMs = routeDecisions.Count == 0 ? null : routeDecisions.Average(decision => (double)decision.DurationMs);

        var metrics = new List<ObservabilityMetric>
        {
            new() { Id = "execution_profile", Label = "Execution profile", Value = profile.ActiveQualityProfile,
                Detail = $"{profile.Effective.Budget.MaxParallelTasks} parallel task(s), {profile.Effective.Budget.MaxWallClockMinutes} min wall-clock envelope" },
            new() { Id = "active_tasks", Label = "Active agent work", Value = activeTasks.ToString(),
                Detail = tasks.Count + " durable task(s) total" },
            new() { Id = "active_training", Label = "Active training", Value = activeTraining.ToString(),
                Detail = training.Count + " training job(s) in this Core lifetime" },
            new() { Id = "active_downloads", Label = "Active downloads", Value = activeDownloads.ToString(),
                Detail = (downloads.Count + datasets.Count) + " model/dataset download record(s)" },
            new() { Id = "model_benchmarks", Label = "Completed model benchmarks", Value = leaderboard.Count.ToString(),
                Detail = leaderboard.Count == 0 ? "No comparable completed evidence yet" : "Latest evidence per model/backend/hardware combination" },
            new() { Id = "router_success", Label = "Router decisions", Value = routeDecisions.Count == 0 ? "n/a" : $"{routeSuccess}/{routeDecisions.Count}",
                Detail = averageRouterMs.HasValue ? $"average decision latency {averageRouterMs.Value:0.0} ms" : "No in-memory router decisions yet" }
        };

        return new ObservabilityDashboardResponse
        {
            CapturedUtc = DateTime.UtcNow,
            ExecutionProfile = profile,
            Metrics = metrics,
            TaskStatuses = CountBy(tasks.Select(task => task.Status)),
            TrainingStatuses = CountBy(training.Select(job => job.Status)),
            ModelBenchmarks = leaderboard,
            RecentActivity = GetTimeline(recentLimit)
        };
    }

    public List<TimelineEntry> GetTimeline(int limit)
    {
        limit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 1000);
        var entries = new List<TimelineEntry>();

        foreach (AgentTaskDto task in _tasks.List())
        {
            string stage = string.IsNullOrWhiteSpace(task.Stage) ? string.Empty : " · " + task.Stage;
            string progress = task.ProgressPercent > 0 ? " · " + task.ProgressPercent + "%" : string.Empty;
            entries.Add(new TimelineEntry
            {
                Category = "task", Title = task.Goal,
                Detail = (task.Role ?? "unassigned") + " - " + task.Status + stage + progress,
                RelatedId = task.Id, Timestamp = task.UpdatedUtc,
            });
        }

        foreach (ModelDownloadStatus model in _models.List())
            entries.Add(new TimelineEntry
            {
                Category = "model_download", Title = model.FileName,
                Detail = model.Status + (string.IsNullOrWhiteSpace(model.VerificationStatus)
                    ? string.Empty : " · checksum " + model.VerificationStatus),
                RelatedId = model.Id, Timestamp = model.UpdatedUtc,
            });

        foreach (DatasetDownloadStatus dataset in _datasets.List())
            entries.Add(new TimelineEntry
            {
                Category = "dataset_download", Title = dataset.RepoId + " / " + dataset.FileName,
                Detail = dataset.Status, RelatedId = dataset.Id, Timestamp = dataset.UpdatedUtc,
            });

        foreach (TrainingJobStatus job in _training.List())
        {
            string stage = string.IsNullOrWhiteSpace(job.Stage) ? string.Empty : " · " + job.Stage;
            entries.Add(new TimelineEntry
            {
                Category = "training_job", Title = "Training job " + job.Id[..Math.Min(8, job.Id.Length)],
                Detail = job.Status + stage + " · step " + job.CurrentStep + "/" + job.TotalSteps,
                RelatedId = job.Id, Timestamp = job.UpdatedUtc,
            });
        }

        foreach (AdapterDto adapter in _adapters.List())
            entries.Add(new TimelineEntry
            {
                Category = "adapter", Title = adapter.Name,
                Detail = adapter.BaseModel + " (" + adapter.Method + ")",
                RelatedId = adapter.Id, Timestamp = adapter.UpdatedUtc,
            });

        foreach (ExperimentDto experiment in _autoLab.List())
            entries.Add(new TimelineEntry
            {
                Category = "experiment", Title = experiment.Name, Detail = experiment.ModelProfile,
                RelatedId = experiment.Id, Timestamp = experiment.CreatedUtc,
            });

        foreach (ArtifactDto artifact in _artifacts.List())
            entries.Add(new TimelineEntry
            {
                Category = "artifact",
                Title = artifact.Title,
                Detail = artifact.ArtifactType + " · " + artifact.Status + " · v" + artifact.CurrentVersionNumber
                    + (string.IsNullOrWhiteSpace(artifact.Source) ? string.Empty : " · " + artifact.Source),
                RelatedId = artifact.Id,
                Timestamp = artifact.UpdatedUtc
            });

        foreach (AutomationDto automation in _automations.List())
        {
            string detail = (automation.Enabled ? "enabled" : "disabled") + " · trigger " + automation.TriggerType;
            if (automation.LastTriggeredUtc.HasValue)
                detail += " · last run " + (string.IsNullOrWhiteSpace(automation.LastResult)
                    ? "recorded" : automation.LastResult);
            entries.Add(new TimelineEntry
            {
                Category = "automation", Title = automation.Name, Detail = detail,
                RelatedId = automation.Id,
                Timestamp = automation.LastTriggeredUtc ?? automation.UpdatedUtc
            });
        }

        foreach (WorkflowDto workflow in _workflows.List())
            entries.Add(new TimelineEntry
            {
                Category = "workflow",
                Title = workflow.Name + " · v" + workflow.VersionNumber,
                Detail = workflow.Steps.Count + " step(s)"
                    + (string.IsNullOrWhiteSpace(workflow.PreviousVersionId) ? string.Empty : " · revision"),
                RelatedId = workflow.Id,
                Timestamp = workflow.UpdatedUtc
            });

        foreach (RuntimeRegressionEvidenceDto regression in _runtimeRegressions.List())
        {
            string change = regression.OriginalTag + " → " + regression.CandidateTag;
            string improvement = regression.ImprovementPercent.HasValue
                ? " · " + regression.ImprovementPercent.Value.ToString("+0.0;-0.0;0.0") + "% score"
                : string.Empty;
            string model = string.IsNullOrWhiteSpace(regression.ModelFileName)
                ? string.Empty : " · " + regression.ModelFileName;
            entries.Add(new TimelineEntry
            {
                Category = "runtime_regression",
                Title = regression.Backend + " · " + change,
                Detail = regression.Decision + " · active " + regression.ActiveTag + model + improvement
                    + " · " + regression.Reason,
                RelatedId = regression.Id,
                Timestamp = regression.MeasuredUtc
            });
        }

        if (_router != null)
        {
            foreach (RouteDecisionDto decision in _router.ListDecisions())
            {
                string model = string.IsNullOrWhiteSpace(decision.ModelPath)
                    ? "no model" : Path.GetFileName(decision.ModelPath);
                string selection = decision.Mode + " → " + decision.ResolvedRole + " → " + model;
                string behavior = decision.Success ? (decision.Swapped ? "swapped" : "reused") : "failed";
                if (decision.FellBackToStandard) behavior += ", fallback to MAIN";
                if (decision.ExactCandidateForced) behavior += ", exact user override";
                string evidence = decision.SelectedIntelligenceScore.HasValue
                    ? "; capability=" + decision.SelectedIntelligenceScore.Value.ToString("0.000")
                    : string.Empty;
                if (decision.SelectedBenchmarkTokensPerSecond.HasValue)
                    evidence += "; speed=" + decision.SelectedBenchmarkTokensPerSecond.Value.ToString("0.0") + " tok/s";
                string reason = string.IsNullOrWhiteSpace(decision.SelectionReason)
                    ? string.Empty : "; " + decision.SelectionReason;
                entries.Add(new TimelineEntry
                {
                    Category = "router_decision",
                    Title = selection,
                    Detail = behavior + "; " + decision.DurationMs + " ms" + evidence + reason + "; " + decision.Message,
                    RelatedId = decision.ResolvedRole,
                    Timestamp = decision.DecidedUtc
                });
            }
        }

        return entries.OrderByDescending(entry => entry.Timestamp).Take(limit).ToList();
    }

    private static List<ObservabilityStatusCount> CountBy(IEnumerable<string?> values) => values
        .Select(value => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim())
        .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => new ObservabilityStatusCount { Name = group.Key, Count = group.Count() })
        .ToList();
}
