using PaRuX.Contracts;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server;

/// <summary>
/// Sanitized V2 companion surface for paired Android clients. These endpoints deliberately project
/// the same authoritative Core state used by Windows without exposing model/worktree paths,
/// per-model tuning overrides, installer/plugin authority or privileged desktop-only stores.
/// </summary>
public static class MobileProductV2Endpoints
{
    public static void MapMobileProductV2Endpoints(this WebApplication app)
    {
        app.MapGet("/api/v2/mobile/execution-profile", () =>
            Results.Ok(ProjectExecutionProfile(ExecutionProfileStore.Current.Get())));

        app.MapPut("/api/v2/mobile/execution-profile/quality", (MobileExecutionQualityRequest request) =>
        {
            try
            {
                ExecutionProfileStatus updated = ExecutionProfileStore.Current.SetQuality(request.QualityProfile, modelKey: null);
                return Results.Ok(ProjectExecutionProfile(updated));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/v2/mobile/observability/dashboard", (
            int? recentLimit, int? benchmarkLimit, ObservabilityService observability) =>
        {
            ObservabilityDashboardResponse dashboard = observability.GetDashboard(recentLimit ?? 30, benchmarkLimit ?? 12);
            IReadOnlyList<EngineRaceEvidenceDto> races = EngineRaceEvidenceStore.Current.List(20);
            return Results.Ok(new
            {
                capturedUtc = dashboard.CapturedUtc,
                executionProfile = ProjectExecutionProfile(dashboard.ExecutionProfile),
                metrics = dashboard.Metrics,
                taskStatuses = dashboard.TaskStatuses,
                trainingStatuses = dashboard.TrainingStatuses,
                recentActivity = dashboard.RecentActivity,
                engineRaceSummary = new
                {
                    total = races.Count,
                    completed = races.Count(item => string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase)),
                    media = races.Count(item => string.Equals(item.Kind, "media", StringComparison.OrdinalIgnoreCase)),
                    runtime = races.Count(item => string.Equals(item.Kind, "runtime", StringComparison.OrdinalIgnoreCase))
                }
            });
        });

        app.MapPost("/api/v2/mobile/goals", (CreateGoalSpecificationRequest request) =>
        {
            try { return Results.Ok(GoalModeV2Store.Current.Create(request)); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/v2/mobile/goals/{goalId}/versions", (
            string goalId, CreateGoalSpecificationVersionRequest request) =>
        {
            try { return Results.Ok(GoalModeV2Store.Current.CreateNext(goalId, request)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v2/mobile/goals/{goalId}", (string goalId, int? version) =>
        {
            try
            {
                GoalSpecificationVersionDto? value = GoalModeV2Store.Current.Get(goalId, version ?? 0);
                return value == null ? Results.NotFound() : Results.Ok(value);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v2/mobile/goals/{goalId}/versions", (string goalId) =>
        {
            try { return Results.Ok(GoalModeV2Store.Current.ListVersions(goalId)); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/v2/mobile/goals/tasks", (
            MobileCreateGoalModeTaskRequest request,
            TaskStore tasks,
            AgentOrchestrator orchestrator) =>
        {
            try
            {
                GoalSpecificationVersionDto specification = GoalModeV2Store.Current.Get(request.GoalId, request.GoalVersion)
                    ?? throw new KeyNotFoundException("Goal specification/version not found.");
                string acceptance = GoalModeV2Store.BuildAcceptanceContext(specification);

                // A paired device may choose the goal/role but never grant itself new Core
                // capabilities. The resulting task follows the same Windows worker/approval gates
                // as an ordinary mobile-created AgentTask.
                AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest
                {
                    Goal = specification.Goal,
                    Role = string.IsNullOrWhiteSpace(request.Role) ? null : request.Role.Trim(),
                    MaxRepairAttempts = Math.Clamp(request.MaxRepairAttempts, 0, 3),
                    GrantedCapabilities = new List<string>(),
                    DelegatedContext = acceptance,
                    DelegatedContextRefs = new List<string>
                    {
                        "goal-mode-v2:" + specification.GoalId + ":v" + specification.Version
                    }
                });
                GoalTaskBindingDto binding = GoalModeV2Store.Current.BindTask(task.Id, specification);
                tasks.AppendLog(task.Id,
                    $"Goal Mode V2 mobile bind immutable to {binding.GoalId} v{binding.GoalVersion} sha256={binding.ContentSha256}.");
                _ = orchestrator.RunSchedulingPass();
                return Results.Ok(new
                {
                    specification,
                    binding,
                    task = ProjectTask(task)
                });
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v2/mobile/goals/tasks/{taskId}", (string taskId) =>
        {
            try
            {
                GoalTaskBindingDto? binding = GoalModeV2Store.Current.GetBinding(taskId);
                if (binding == null) return Results.NotFound();
                GoalSpecificationVersionDto specification = GoalModeV2Store.Current.GetBoundSpecification(taskId)!;
                return Results.Ok(new { binding, specification });
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v2/mobile/goals/tasks/{taskId}/verification-records", (string taskId) =>
        {
            try
            {
                GoalTaskBindingDto? binding = GoalModeV2Store.Current.GetBinding(taskId);
                if (binding == null) return Results.NotFound();
                return Results.Ok(GoalModeV2Store.Current.ListVerificationRecords(taskId));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    private static object ProjectExecutionProfile(ExecutionProfileStatus status) => new
    {
        activeQualityProfile = status.ActiveQualityProfile,
        preferredContextTokens = status.Effective.PreferredContextTokens,
        reasoningEffort = status.Effective.ReasoningEffort,
        structuredOutputStrategy = status.Effective.StructuredOutputStrategy,
        toolStrategy = status.Effective.ToolStrategy,
        evidenceSource = status.Effective.EvidenceSource,
        updatedUtc = status.Effective.UpdatedUtc,
        budget = new
        {
            status.Effective.Budget.MaxWallClockMinutes,
            status.Effective.Budget.MaxToolSteps,
            status.Effective.Budget.MaxDirectSubagents,
            status.Effective.Budget.MaxDelegationDepth,
            status.Effective.Budget.ContextFraction,
            status.Effective.Budget.VerificationPasses,
            status.Effective.Budget.MaxParallelTasks,
            status.Effective.Budget.MaxGpuTemperatureC,
            status.Effective.Budget.MaxSystemRamPercent,
            status.Effective.Budget.MaxVramPercent
        }
    };

    private static object ProjectTask(AgentTaskDto task) => new
    {
        task.Id,
        task.ParentId,
        task.Goal,
        task.Role,
        task.Status,
        task.Stage,
        task.ProgressPercent,
        task.Error,
        task.VerificationStatus,
        task.CreatedUtc,
        task.UpdatedUtc
    };

    public sealed class MobileExecutionQualityRequest
    {
        public string QualityProfile { get; set; } = ExecutionQualityProfile.Balanced;
    }

    public sealed class MobileCreateGoalModeTaskRequest
    {
        public string GoalId { get; set; } = string.Empty;
        public int GoalVersion { get; set; }
        public string? Role { get; set; }
        public int MaxRepairAttempts { get; set; } = 2;
    }
}
