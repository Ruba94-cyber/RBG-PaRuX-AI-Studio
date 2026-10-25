using System.Net;
using PaRuX.Contracts;
using PaRuX.Server.Automation;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server;

/// <summary>
/// Product-level V2 surfaces that intentionally reuse the existing authoritative Core services.
/// These endpoints do not create alternate inference/agent/training runtimes; they expose durable
/// policy/specification/index state shared by desktop, CLI, integrations and paired clients.
/// </summary>
public static class ProductV2Endpoints
{
    public static void MapProductV2Endpoints(this WebApplication app)
    {
        app.MapManagedMediaV2Endpoints();
        app.MapMobileProductV2Endpoints();

        // Backup v2 is intentionally desktop-owner only even though the global auth layer already
        // authenticates paired devices and browser sessions. Backup/restore is desktop authority, not LAN
        // or browser authority. The import path is also covered by BackupImportGuardMiddleware's real
        // decompression budget before this handler sees the body.
        app.MapGet("/api/v2/backup/export", async (BackupService backups, HttpContext context, CancellationToken ct) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                byte[] bytes = await new ExecutionProfileBackupService(backups, ExecutionProfileStore.Current)
                    .ExportAsync(ct).ConfigureAwait(false);
                BackupImportGuardMiddleware.EnsureRestorable(bytes);
                return Results.File(bytes, "application/zip", "PaRuX-backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/v2/backup/import", async (BackupService backups, HttpContext context, CancellationToken ct) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                BackupImportResult result = await new ExecutionProfileBackupService(backups, ExecutionProfileStore.Current)
                    .ImportAsync(context.Request.Body, ct, AllowIncompleteRollbackSnapshot(context)).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (BackupService.IncompleteRollbackSnapshotException ex) { return RefusalResult(ex); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (IOException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError); }
        });

        app.MapGet("/api/v2/execution-profiles", (string? modelKey) =>
            Results.Ok(ExecutionProfileStore.Current.Get(modelKey)));

        app.MapPut("/api/v2/execution-profiles/quality", (SetExecutionQualityProfileRequest request) =>
        {
            try { return Results.Ok(ExecutionProfileStore.Current.SetQuality(request.QualityProfile, request.ModelKey)); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapPut("/api/v2/execution-profiles/models/{modelKey}", (string modelKey, ModelExecutionProfile request) =>
        {
            try
            {
                request.ModelKey = modelKey;
                return Results.Ok(ExecutionProfileStore.Current.SetModelOverride(request));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapDelete("/api/v2/execution-profiles/models/{modelKey}", (string modelKey) =>
        {
            try { return ExecutionProfileStore.Current.DeleteModelOverride(modelKey) ? Results.NoContent() : Results.NotFound(); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v2/plugins/inspect", (
            PluginBundleInspectRequest request, SkillRegistryService skills, AutomationStore automations) =>
        {
            var service = new PluginBundleService(skills, automations);
            PluginBundleInspection inspection = service.Inspect(request.BundlePath);
            return inspection.Valid ? Results.Ok(inspection) : Results.BadRequest(inspection);
        }).RequireDesktopOwner();

        app.MapPost("/api/v2/plugins/install", (
            PluginInstallRequest request, SkillRegistryService skills, AutomationStore automations) =>
        {
            try { return Results.Ok(new PluginBundleService(skills, automations).Install(request)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v2/plugins/trusted-publishers", (SkillRegistryService skills, AutomationStore automations) =>
            Results.Ok(new PluginBundleService(skills, automations).ListTrustedPublishers()));

        app.MapDelete("/api/v2/plugins/trusted-publishers/{fingerprint}", (
            string fingerprint, SkillRegistryService skills, AutomationStore automations) =>
        {
            try
            {
                return new PluginBundleService(skills, automations).RevokePublisher(fingerprint)
                    ? Results.NoContent() : Results.NotFound();
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v2/goals", (CreateGoalSpecificationRequest request) =>
        {
            try { return Results.Ok(GoalModeV2Store.Current.Create(request)); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v2/goals/{goalId}/versions", (string goalId, CreateGoalSpecificationVersionRequest request) =>
        {
            try { return Results.Ok(GoalModeV2Store.Current.CreateNext(goalId, request)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v2/goals/{goalId}", (string goalId, int? version) =>
        {
            try
            {
                GoalSpecificationVersionDto? value = GoalModeV2Store.Current.Get(goalId, version ?? 0);
                return value == null ? Results.NotFound() : Results.Ok(value);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v2/goals/{goalId}/versions", (string goalId) =>
        {
            try { return Results.Ok(GoalModeV2Store.Current.ListVersions(goalId)); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/v2/goals/tasks", (
            CreateGoalModeTaskRequest request, TaskStore tasks, AgentOrchestrator orchestrator) =>
        {
            try
            {
                GoalSpecificationVersionDto specification = GoalModeV2Store.Current.Get(request.GoalId, request.GoalVersion)
                    ?? throw new KeyNotFoundException("Goal specification/version not found.");
                string acceptance = GoalModeV2Store.BuildAcceptanceContext(specification);
                AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest
                {
                    Goal = specification.Goal,
                    Role = string.IsNullOrWhiteSpace(request.Role) ? null : request.Role.Trim(),
                    MaxRepairAttempts = Math.Clamp(request.MaxRepairAttempts, 0, 3),
                    GrantedCapabilities = request.GrantedCapabilities,
                    FinishCondition = request.FinishCondition,
                    DelegatedContext = acceptance,
                    DelegatedContextRefs = new List<string>
                    {
                        "goal-mode-v2:" + specification.GoalId + ":v" + specification.Version
                    }
                });
                GoalTaskBindingDto binding = GoalModeV2Store.Current.BindTask(task.Id, specification);
                tasks.AppendLog(task.Id,
                    $"Goal Mode V2 bound immutably to {binding.GoalId} v{binding.GoalVersion} sha256={binding.ContentSha256}.");
                _ = orchestrator.RunSchedulingPass();
                return Results.Ok(new GoalModeTaskResponse { Specification = specification, Binding = binding, Task = task });
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v2/goals/tasks/{taskId}", (string taskId) =>
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

        app.MapGet("/api/v2/goals/tasks/{taskId}/verification-records", (string taskId) =>
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

        app.MapPost("/api/v2/virtual-context/ledger", (AppendVirtualContextLedgerRequest request) =>
        {
            try { return Results.Ok(VirtualContextLedgerStore.Current.Append(request)); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v2/virtual-context/ledger/{scope}/{scopeId}", (string scope, string scopeId, int? maxEntries) =>
        {
            try { return Results.Ok(VirtualContextLedgerStore.Current.GetState(scope, scopeId, maxEntries ?? 1000)); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v2/semantic-index/repositories", (RepoIndexService index, EmbeddingService embeddings) =>
            Results.Ok(new SemanticIndexV2Service(index, embeddings).ListRepositories()));

        app.MapPut("/api/v2/semantic-index/repositories", (
            RegisterSemanticRepositoryRequest request, RepoIndexService index, EmbeddingService embeddings) =>
        {
            try { return Results.Ok(new SemanticIndexV2Service(index, embeddings).Register(request)); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or DirectoryNotFoundException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapDelete("/api/v2/semantic-index/repositories/{alias}", (
            string alias, bool? removeIndex, RepoIndexService index, EmbeddingService embeddings) =>
        {
            try
            {
                bool removed = new SemanticIndexV2Service(index, embeddings).Unregister(alias, removeIndex ?? true);
                return removed ? Results.NoContent() : Results.NotFound();
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v2/semantic-index/sync", (
            SemanticIndexSyncRequest request, RepoIndexService index, EmbeddingService embeddings) =>
        {
            try { return Results.Ok(new SemanticIndexV2Service(index, embeddings).Sync(request)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or DirectoryNotFoundException or IOException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v2/semantic-index/search", (
            SemanticIndexSearchRequest request, RepoIndexService index, EmbeddingService embeddings) =>
        {
            try { return Results.Ok(new SemanticIndexV2Service(index, embeddings).Search(request)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or DirectoryNotFoundException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();
    }

    /// <summary>Explicit restore opt-in: <c>?allowIncompleteRollbackSnapshot=true</c>.</summary>
    internal static bool AllowIncompleteRollbackSnapshot(HttpContext context) =>
        string.Equals(context.Request.Query["allowIncompleteRollbackSnapshot"], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>409 body that lists the stores the caller must explicitly accept losing rollback cover for.</summary>
    internal static IResult RefusalResult(BackupService.IncompleteRollbackSnapshotException ex) =>
        Results.Json(new BackupImportRefusal { Error = ex.Message, UnsnapshottableStores = ex.UnsnapshottableStores.ToList() },
            statusCode: StatusCodes.Status409Conflict);

    private static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote);

    // Loopback alone is not desktop authority: a browser session through a same-machine reverse proxy also arrives from
    // 127.0.0.1, so the owner the auth middleware stamped must be the desktop itself.
    private static bool IsDesktopOwner(HttpContext context) =>
        IsLoopback(context) && context.Items["PaRuXOwner"] as string == "desktop";
}
