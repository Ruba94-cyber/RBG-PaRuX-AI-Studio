using System.Net;
using PaRuX.Contracts;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;
using PaRuX.Server.ProjectManager;

namespace PaRuX.Server;

public static class MobileControlEndpoints
{
    public static void MapMobileControlEndpoints(this WebApplication app)
    {
        app.UseMiddleware<PairedDeviceAbuseMiddleware>();
        app.UseMiddleware<BackupImportGuardMiddleware>();

        app.MapLocalProtocolCompatibilityEndpoints();

        // These are deliberately registered behind the same default-deny paired-device middleware
        // as the mobile control plane but require the authenticated desktop owner as a second gate.
        // Export is a complete inspectable graph snapshot; traversal is bounded by Core-owned depth
        // and entity caps. Neither surface is remotely callable merely because entity/neighborhood
        // reads are safe for the Android companion.
        app.MapGet("/api/v1/context/export", (HttpContext context, ContextGraphStore graph) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(graph.ExportSnapshot());
        });

        app.MapGet("/api/v1/context/entities/{entityId}/traverse", (
            string entityId,
            int? maxDepth,
            int? maxEntities,
            HttpContext context,
            ContextGraphStore graph) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                ContextTraversalResponse? traversal = graph.Traverse(
                    entityId,
                    maxDepth ?? 2,
                    maxEntities ?? 64);
                return traversal == null
                    ? Results.NotFound(new { error = "Context entity not found." })
                    : Results.Ok(traversal);
            }
            catch (InvalidDataException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapGet("/api/v1/chat/model-routing", () =>
            Results.Ok(ChatRoutingPreferenceStore.GetDto()));

        app.MapPost("/api/v1/chat/model-routing", (SetChatRoutingPreferenceRequest request) =>
        {
            try { return Results.Ok(ChatRoutingPreferenceStore.Set(request)); }
            catch (ManualSelectionGateBusyException exception) { return Results.Conflict(new { error = exception.Code }); }
            catch (FileNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        app.MapGet("/api/v1/chat/model-routing/tuning", () =>
            Results.Ok(AutoRoutingTuningStore.Get()));

        app.MapPost("/api/v1/chat/model-routing/tuning", (SetAutoRoutingTuningRequest request) =>
        {
            try { return Results.Ok(AutoRoutingTuningStore.Set(request)); }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        app.MapDelete("/api/v1/chat/model-routing/tuning", () =>
            Results.Ok(AutoRoutingTuningStore.Reset()));

        // Per-capability Automatic / Prefer / Force. Text capabilities (chat.general, chat.reasoning, coding, vision)
        // change live routing in SmartRouterService; media capabilities are persisted for the media routing decision.
        app.MapGet("/api/v1/routing/capability-modes", () =>
        {
            try
            {
                IReadOnlyList<CapabilityModeOverride> rows = CapabilityRoutingPreferenceStore.ListCapabilityModes();
                return Results.Ok(CapabilityRoutingPreferenceStore.RoutableCapabilities.Select(capability =>
                    rows.FirstOrDefault(row => row.Capability == capability)
                    ?? new CapabilityModeOverride { Capability = capability }).ToArray());
            }
            catch (InvalidDataException)
            {
                return Results.Conflict(new { error = "The capability routing settings need repair before they can be read." });
            }
        });

        // Dry-run of what each capability override will do now (media: the real checkpoint decision) plus the trace
        // of the last media job that used it, and the model choices a picker can offer.
        app.MapGet("/api/v1/routing/capability-status", () =>
        {
            try
            {
                var list = new List<CapabilityRoutingStatusDto>();
                foreach (string capability in CapabilityRoutingPreferenceStore.RoutableCapabilities)
                    list.Add(MediaCapabilityRouting.BuildStatus(capability,
                        CapabilityRoutingPreferenceStore.GetCapabilityMode(capability, MediaCapabilityRouting.CapabilityModeRoot)));
                return Results.Ok(list);
            }
            catch (InvalidDataException)
            {
                return Results.Conflict(new { error = "The capability routing settings need repair before they can be read." });
            }
        });

        app.MapPut("/api/v1/routing/capability-modes/{capability}", (
            string capability, SetCapabilityModeRequest request) =>
        {
            if (request is null) return Results.BadRequest(new { error = "A capability mode is required." });
            if (request.ModelId is { } id && (id.Contains('/') || id.Contains('\\') || id.Contains("..")))
                return Results.BadRequest(new { error = "The model id must be a bare model name." });
            try { return Results.Ok(CapabilityRoutingPreferenceStore.SetCapabilityMode(capability, request.Mode, request.ModelId)); }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireDesktopOwner();

        // These capability overrides are local desktop controls, not paired-device settings.
        // Force and Prefer are accepted only for a currently installed, hash-verified Candidate;
        // routing revalidates that identity and Force performs a fresh exact load on every turn.
        app.MapGet("/api/v1/chat/capability-overrides", (HttpContext context) =>
        {
            if (!IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(CapabilityRoutingPreferenceStore.List()); }
            catch (InvalidDataException)
            {
                return Results.Conflict(new { error = "The scoped routing settings need repair before they can be read." });
            }
        });

        app.MapGet("/api/v1/chat/capability-overrides/{role}/{profile}", (
            string role, string profile, HttpContext context) =>
        {
            if (!IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                (string normalizedRole, string normalizedProfile) = CapabilityRoutingPreferenceStore.Scope(role, profile);
                ScopedRoutingOverrideDto? row = CapabilityRoutingPreferenceStore.Get(normalizedRole, normalizedProfile);
                return row == null ? Results.NoContent() : Results.Ok(row);
            }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        app.MapPut("/api/v1/chat/capability-overrides/{role}/{profile}", async (
            string role, string profile, SetScopedRoutingOverrideRequest request, HttpContext context,
            CandidateEvidenceService evidence, CancellationToken ct) =>
        {
            if (!IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request == null) return Results.BadRequest(new { error = "A scoped routing override is required." });
            try
            {
                (string normalizedRole, string normalizedProfile) = CapabilityRoutingPreferenceStore.Scope(role, profile);
                if (string.Equals(request.Mode?.Trim(), ScopedRouteMode.Force, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(request.Mode?.Trim(), ScopedRouteMode.Prefer, StringComparison.OrdinalIgnoreCase))
                {
                    CandidateEvidenceIdentity? candidate = await evidence.TryCaptureSnapshotAsync(
                        request.CandidateKey ?? string.Empty, request.ManagedFileName ?? string.Empty, ct).ConfigureAwait(false);
                    if (candidate == null || candidate.Revision != request.Revision)
                        return Results.Conflict(new { error = "The selected Candidate is no longer active or its installed bytes do not match provenance." });
                }
                return Results.Ok(CapabilityRoutingPreferenceStore.Set(normalizedRole, normalizedProfile, request));
            }
            catch (ManualSelectionGateBusyException exception) { return Results.Conflict(new { error = exception.Code }); }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        app.MapDelete("/api/v1/chat/capability-overrides/{role}/{profile}", (
            string role, string profile, HttpContext context) =>
        {
            if (!IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                (string normalizedRole, string normalizedProfile) = CapabilityRoutingPreferenceStore.Scope(role, profile);
                return CapabilityRoutingPreferenceStore.Delete(normalizedRole, normalizedProfile)
                    ? Results.NoContent() : Results.NotFound();
            }
            catch (ManualSelectionGateBusyException exception) { return Results.Conflict(new { error = exception.Code }); }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        app.MapGet("/api/v1/chat/model-routing/models", (
            ModelDownloadService models, ModelBenchmarkService benchmarks) =>
        {
            Dictionary<string, ModelBenchmarkSummary> byPath = benchmarks.ListModels()
                .GroupBy(item => item.ModelPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var safe = models.ListManagedModels().Select(model =>
            {
                byPath.TryGetValue(model.FilePath, out ModelBenchmarkSummary? benchmark);
                return new
                {
                    model.FileName,
                    model.SizeBytes,
                    model.Quantization,
                    model.Alias,
                    LastBenchmarkTokensPerSecond = benchmark?.AverageTokensPerSecond
                };
            }).ToList();
            return Results.Ok(safe);
        });

        app.MapPost("/api/v1/mobile/skills", (SkillCreateRequest request, SkillRegistryService skills) =>
        {
            try
            {
                SkillManifest safe = MobileSkillCreationPolicy.Sanitize(request.Manifest);
                return Results.Ok(skills.CreateOrUpdateUserSkill(safe, isImport: false));
            }
            catch (InvalidDataException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapPost("/api/v1/orchestrator/tasks/{parentId}/delegate", (
            string parentId, DelegateAgentTaskRequest request, TaskStore tasks) =>
        {
            try
            {
                request.ParentTaskId = parentId;
                return Results.Ok(tasks.DelegateSubtaskWithExecutionProfile(request));
            }
            catch (InvalidDataException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).RequireDesktopOwner();

        // Desktop coding delegation uses one prepare -> authority -> commit protocol for both a
        // single child and parallel fan-out. TaskStore freezes newly-created children before this
        // endpoint returns from prepare; an interrupted prior prepare is surfaced again through
        // PreparedChildTaskIds. Every repository-aware prepared child receives the complete parent
        // authority set before CommitPreparedDelegationBatch atomically moves ALL prepared children
        // to Queued and the parent to Waiting under the same TaskStore/SQLite transaction. The
        // scheduler therefore cannot observe a runnable child while the parent still executes or
        // while any newly-prepared peer lacks authority.
        app.MapPost("/api/v1/orchestrator/tasks/{parentId}/delegate-batch", (
            string parentId,
            DelegateAgentTaskBatchRequest request,
            HttpContext context,
            TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            foreach (DelegateAgentTaskRequest child in request.Children)
                child.ParentTaskId = parentId;

            DelegateAgentTaskBatchResponse? batch = null;
            try
            {
                batch = tasks.DelegateSubtasksWithExecutionProfile(
                    request,
                    AgentRepositoryAuthoritySetStore.Current.Get(parentId) is { Repositories.Count: > 0 });
                AgentTaskDto parent = tasks.Get(parentId)
                    ?? throw new InvalidDataException("Parent task disappeared during delegation preparation.");

                var preparedTasks = new List<AgentTaskDto>(batch.PreparedChildTaskIds.Count);
                foreach (string childId in batch.PreparedChildTaskIds)
                {
                    AgentTaskDto child = tasks.Get(childId)
                        ?? throw new InvalidDataException("Prepared delegated child disappeared before authority inheritance: " + childId);
                    preparedTasks.Add(child);
                }

                List<AgentTaskDto> repositoryAware = preparedTasks
                    .Where(child => AgentWorkspaceAssignmentStore.RoleSupportsWorkspace(child.Role)
                        || string.IsNullOrWhiteSpace(child.Role)
                        || string.Equals(child.Role, "auto", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                AgentRepositorySetDto? parentSet = null;
                if (repositoryAware.Count > 0)
                {
                    parentSet = GetRepositorySet(parentId, parent);
                    if (parentSet.Repositories.Count == 0)
                        throw new InvalidDataException("A repository-aware delegated child requires the parent task to own repository authority.");
                }

                foreach (AgentTaskDto child in repositoryAware)
                {
                    AgentRepositoryAuthorityDto primary = parentSet!.Repositories.Single(repository => repository.IsPrimary);
                    AgentRepositoryAuthoritySetStore.Current.Replace(
                        child.Id,
                        new ReplaceAgentRepositorySetRequest
                        {
                            PrimaryWorkspaceRoot = primary.WorkspaceRoot,
                            AdditionalRepositories = parentSet.Repositories
                                .Where(repository => !repository.IsPrimary)
                                .Select(repository => new AddAgentRepositoryAuthorityRequest
                                {
                                    Alias = repository.Alias,
                                    WorkspaceRoot = repository.WorkspaceRoot
                                })
                                .ToList()
                        },
                        child);
                    AgentWorkspaceAssignmentStore.Current.Set(child.Id, primary.WorkspaceRoot, child);
                    tasks.AppendLog(child.Id,
                        "Inherited the parent's complete desktop repository authority set before delegated dispatch.");
                }

                List<string> delegatedIds = batch.Children
                    .Select(child => child.ChildTaskId)
                    .ToList();
                tasks.CommitPreparedDelegationBatch(parentId, batch.PreparedChildTaskIds, delegatedIds, request.ContinueParent);
                return Results.Ok(batch);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                if (batch != null)
                {
                    foreach (string childId in batch.NewChildTaskIds.AsEnumerable().Reverse())
                    {
                        AgentTaskDto? child = tasks.Get(childId);
                        if (child == null) continue;
                        try
                        {
                            AgentRepositorySetDto? authoritative = AgentRepositoryAuthoritySetStore.Current.Get(childId);
                            if (authoritative != null) AgentRepositoryAuthoritySetStore.Current.Delete(childId, child);
                        }
                        catch { }
                        try { AgentWorkspaceAssignmentStore.Current.Delete(childId); } catch { }
                        try { tasks.Delete(childId); } catch { }
                    }
                    tasks.AppendLog(parentId,
                        "Desktop delegation authority/commit failed before safe dispatch; newly-created children were rolled back: " + exception.Message);
                }
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        // A continuing parent (delegate-batch with continueParent) keeps running beside its
        // read-only children. When it wants to finish while a child is still live, it asks Core to
        // pause it behind them; Core reuses the ordinary delegation Waiting stage so the existing
        // resume path brings it back with bounded child evidence once every child is terminal.
        app.MapPost("/api/v1/orchestrator/tasks/{parentId}/await-children", (
            string parentId,
            HttpContext context,
            TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                IReadOnlyList<string> live = tasks.PauseParentForLiveDelegatedChildren(parentId);
                return Results.Ok(new AwaitDelegatedChildrenResponse
                {
                    Waiting = live.Count > 0,
                    LiveChildTaskIds = live.ToList(),
                    Message = live.Count > 0
                        ? "Parent paused behind " + live.Count + " live delegated child task(s)."
                        : "No delegated child is live; the parent was not paused."
                });
            }
            catch (InvalidDataException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        // Desktop-only root coding creation. The complete repository authority set is persisted as
        // one immutable pre-execution document before the worker may freeze it. The public task DTO
        // never carries workstation paths.
        app.MapPost("/api/v1/orchestrator/coding-tasks", (
            CreateDesktopCodingTaskRequest request,
            HttpContext context,
            TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(request.Goal))
                return Results.BadRequest(new { error = "A coding task goal is required." });
            if (string.IsNullOrWhiteSpace(request.WorkspaceRoot) || !Directory.Exists(request.WorkspaceRoot))
                return Results.BadRequest(new { error = "workspaceRoot must be an existing local directory." });
            if (!CodingTaskPermissionProfile.IsKnown(request.CodingPermissionProfile))
                return Results.BadRequest(new { error = "Unknown coding permission profile. Use offline, package-fetch or trusted-restore." });
            if (!CodingRunPermissionModes.IsKnown(request.CodingRunPermissionMode))
                return Results.BadRequest(new { error = "Unknown coding run permission mode. Use bypass, ask or plan." });

            AgentTaskDto? task = null;
            try
            {
                string profile = CodingTaskPermissionProfile.Normalize(request.CodingPermissionProfile);
                task = tasks.Create(new CreateAgentTaskRequest
                {
                    Goal = request.Goal.Trim(),
                    Role = "coding",
                    GrantedCapabilities = CodingTaskPermissionProfile.RootCapabilitiesFor(profile).ToList(),
                    CodingRunPermissionMode = CodingRunPermissionModes.Normalize(request.CodingRunPermissionMode),
                    MaxRepairAttempts = Math.Clamp(request.MaxRepairAttempts, 0, 3)
                });

                AgentRepositorySetDto set = AgentRepositoryAuthoritySetStore.Current.Replace(
                    task.Id,
                    new ReplaceAgentRepositorySetRequest
                    {
                        PrimaryWorkspaceRoot = request.WorkspaceRoot,
                        AdditionalRepositories = request.AdditionalRepositories ?? new List<AddAgentRepositoryAuthorityRequest>()
                    },
                    task);
                AgentRepositoryAuthorityDto primary = set.Repositories.Single(repository => repository.IsPrimary);
                AgentWorkspaceAssignmentStore.Current.Set(task.Id, primary.WorkspaceRoot, task);
                return Results.Ok(task);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                if (task != null)
                {
                    try { AgentRepositoryAuthoritySetStore.Current.Delete(task.Id, task); } catch { }
                    try { AgentWorkspaceAssignmentStore.Current.Delete(task.Id); } catch { }
                    try { tasks.Delete(task.Id); } catch { }
                }
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapGet("/api/v1/orchestrator/tasks/{taskId}/repositories", (
            string taskId, HttpContext context, TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            AgentTaskDto? task = tasks.Get(taskId);
            if (task == null) return Results.NotFound(new { error = "Task not found." });
            try { return Results.Ok(GetRepositorySet(taskId, task)); }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        app.MapPut("/api/v1/orchestrator/tasks/{taskId}/repositories", (
            string taskId,
            ReplaceAgentRepositorySetRequest request,
            HttpContext context,
            TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            AgentTaskDto? task = tasks.Get(taskId);
            if (task == null) return Results.NotFound(new { error = "Task not found." });
            try
            {
                AgentRepositorySetDto set = AgentRepositoryAuthoritySetStore.Current.Replace(taskId, request, task);
                AgentRepositoryAuthorityDto primary = set.Repositories.Single(repository => repository.IsPrimary);
                AgentWorkspaceAssignmentStore.Current.Set(taskId, primary.WorkspaceRoot, task);
                return Results.Ok(set);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapPost("/api/v1/orchestrator/tasks/{taskId}/repositories", (
            string taskId,
            AddAgentRepositoryAuthorityRequest request,
            HttpContext context,
            TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            AgentTaskDto? task = tasks.Get(taskId);
            if (task == null) return Results.NotFound(new { error = "Task not found." });
            try { return Results.Ok(AddRepository(taskId, task, request)); }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        app.MapDelete("/api/v1/orchestrator/tasks/{taskId}/repositories/{alias}", (
            string taskId,
            string alias,
            HttpContext context,
            TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            AgentTaskDto? task = tasks.Get(taskId);
            if (task == null) return Results.NotFound(new { error = "Task not found." });
            try
            {
                return RemoveRepository(taskId, task, alias)
                    ? Results.NoContent()
                    : Results.NotFound(new { error = "Repository alias not found." });
            }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        // The desktop client reads (and, for legacy single-workspace callers, writes) the Core-stored
        // workspace binding of a task. Without this route every coding task looked unbound to the
        // agent ("task identity and workspace assignment could not be confirmed"), so no task ever
        // received verifier handoff authority.
        app.MapGet("/api/v1/orchestrator/tasks/{taskId}/workspace", (
            string taskId,
            HttpContext context,
            TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (tasks.Get(taskId) == null) return Results.NotFound(new { error = "Task not found." });
            AgentWorkspaceAssignmentDto? assignment = AgentWorkspaceAssignmentStore.Current.Get(taskId);
            return assignment == null
                ? Results.NotFound(new { error = "Task has no workspace assignment." })
                : Results.Ok(assignment);
        });

        app.MapPut("/api/v1/orchestrator/tasks/{taskId}/workspace", (
            string taskId,
            SetAgentWorkspaceAssignmentRequest request,
            HttpContext context,
            TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            AgentTaskDto? task = tasks.Get(taskId);
            if (task == null) return Results.NotFound(new { error = "Task not found." });
            try { return Results.Ok(AgentWorkspaceAssignmentStore.Current.Set(taskId, request.WorkspaceRoot, task)); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            { return Results.BadRequest(new { error = exception.Message }); }
        });

        app.MapGet("/api/v1/orchestrator/tasks/{taskId}/desktop-worktree", (
            string taskId,
            HttpContext context,
            TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            AgentTaskDto? task = tasks.Get(taskId);
            if (task == null) return Results.NotFound(new { error = "Task not found." });
            if (string.IsNullOrWhiteSpace(task.WorktreePath)) return Results.NotFound(new { error = "Task has no desktop worktree." });
            return Results.Ok(new AgentDesktopWorktreeDto
            {
                TaskId = task.Id,
                WorktreePath = task.WorktreePath
            });
        });

        // Additive review status document (findings with state, child timeline, own-change revert).
        // Writers are the desktop worker (findings, revert) and Core (child timeline); only the desktop
        // owner may write or request. Paired/mobile clients read the document through AgentTaskDto.
        app.MapPut("/api/v1/orchestrator/tasks/{id}/review-status/{section}", (
            string id,
            string section,
            System.Text.Json.Nodes.JsonNode? body,
            HttpContext context,
            TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!TaskStore.ReviewStatusSections.Contains(section))
                return Results.BadRequest(new { error = "Unknown review status section." });
            return tasks.SetReviewStatusSection(id, section, body)
                ? Results.NoContent()
                : Results.NotFound(new { error = "Task not found." });
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/orchestrator/tasks/{id}/revert-own-changes", (
            string id,
            HttpContext context,
            TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            (bool ok, string error) = tasks.RequestOwnChangeRevert(id);
            if (!ok)
                return Results.Json(new { error }, statusCode: error == "Task not found."
                    ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict);
            tasks.AppendLog(id, "Revert of the agent's own changes requested by the desktop owner.");
            return Results.Accepted();
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/orchestrator/tasks/{id}/resume", (
            string id,
            ResumeDesktopAgentTaskRequest request,
            HttpContext context,
            TaskStore tasks,
            AgentOrchestrator orchestrator) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            AgentTaskResumeService.ResumeResult result = new AgentTaskResumeService(tasks).Resume(id, request.Source);
            if (!result.Success)
                return Results.Json(new { error = result.Message }, statusCode: result.StatusCode);
            _ = orchestrator.RunSchedulingPass();
            return Results.Ok(result.Task);
        });

        app.MapGet("/api/v1/orchestrator/tasks/{taskId}/user-question", (
            string taskId, TaskStore tasks) =>
        {
            try { return Results.Ok(new AgentUserQuestionService(tasks).GetState(taskId)); }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        // Asking is desktop/system-owned: a paired device may answer a question but cannot make an
        // agent manufacture a pause. The coding agent reaches this route through the authenticated
        // desktop Core client, never with raw Core credentials in model context.
        app.MapPost("/api/v1/orchestrator/tasks/{taskId}/user-question", (
            string taskId,
            AskAgentUserQuestionRequest request,
            HttpContext context,
            TaskStore tasks) =>
        {
            if (!IsDesktopOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(new AgentUserQuestionService(tasks).Ask(taskId, request)); }
            catch (InvalidDataException exception) { return Results.Conflict(new { error = exception.Message }); }
        });

        app.MapPost("/api/v1/orchestrator/tasks/{taskId}/user-question/answer", (
            string taskId,
            AnswerAgentUserQuestionRequest request,
            TaskStore tasks,
            AgentOrchestrator orchestrator) =>
        {
            try
            {
                AgentUserQuestionStateDto state = new AgentUserQuestionService(tasks).Answer(taskId, request);
                _ = orchestrator.RunSchedulingPass();
                return Results.Ok(state);
            }
            catch (InvalidDataException exception) { return Results.Conflict(new { error = exception.Message }); }
        });

        // Mobile-safe project reads and task attachment use the single ProjectManagerService/context
        // graph implementation. Program.cs owns project creation, so no duplicate POST /projects is
        // registered here.
        app.MapGet("/api/v1/projects", (ProjectManagerService projects) =>
            Results.Ok(new ProjectListResponse { Projects = projects.ListProjects() }));

        // Android identifies an Artifact by id, never a Windows path; MobileArtifactService already
        // strips content refs/file paths from the listed metadata and validates every download
        // against the PaRuX-owned generated/attachments roots.
        app.MapGet("/api/v1/mobile/artifacts", (string? projectId, MobileArtifactService artifacts) =>
        {
            try { return Results.Ok(artifacts.List(projectId)); }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        app.MapGet("/api/v1/mobile/artifacts/{artifactId}/content", (
            string artifactId, int? version, MobileArtifactService artifacts) =>
        {
            MobileArtifactService.DownloadTarget? target = artifacts.ResolveDownload(artifactId, version);
            return target == null
                ? Results.NotFound(new { error = "Artifact content is not available for download." })
                : Results.File(target.Path, target.MediaType, target.FileName);
        });

        app.MapGet("/api/v1/projects/{projectId}", (string projectId, ProjectManagerService projects) =>
        {
            ProjectDto? project = projects.GetProject(projectId);
            return project == null ? Results.NotFound(new { error = "Project not found." }) : Results.Ok(project);
        });

        app.MapPost("/api/v1/projects/{projectId}/tasks", (
            string projectId,
            AttachProjectTaskRequest request,
            ProjectManagerService projects) =>
        {
            try { return Results.Ok(projects.AttachTask(projectId, request.TaskId)); }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        app.MapPost("/api/v1/orchestrator/tasks/{taskId}/computer-use/approve-once", (
            string taskId,
            HttpContext context) =>
        {
            string? owner = context.Items["PaRuXOwner"] as string;
            string deviceId = owner?.StartsWith("device:", StringComparison.Ordinal) == true
                ? owner["device:".Length..]
                : string.Empty;
            if (deviceId.Length == 0)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            RemoteComputerUseApprovalResult result = new RemoteComputerUseApprovalService().ApproveOnce(taskId, deviceId);
            return result switch
            {
                RemoteComputerUseApprovalResult.Approved => Results.NoContent(),
                RemoteComputerUseApprovalResult.NotFound => Results.NotFound(new { error = "Task not found." }),
                _ => Results.Conflict(new { error = "This task is not awaiting one-task Computer Use approval." })
            };
        });
    }

    private static AgentRepositorySetDto GetRepositorySet(string taskId, AgentTaskDto task) =>
        AgentRepositoryAuthoritySetStore.Current.Get(taskId)
        ?? AgentRepositorySetStore.Current.Get(taskId, task);

    private static AgentRepositorySetDto AddRepository(
        string taskId,
        AgentTaskDto task,
        AddAgentRepositoryAuthorityRequest request)
    {
        AgentRepositorySetDto? authoritative = AgentRepositoryAuthoritySetStore.Current.Get(taskId);
        if (authoritative == null)
            return AgentRepositorySetStore.Current.Add(taskId, request, task);

        AgentRepositoryAuthorityDto primary = authoritative.Repositories.Single(repository => repository.IsPrimary);
        var additional = authoritative.Repositories
            .Where(repository => !repository.IsPrimary)
            .Select(repository => new AddAgentRepositoryAuthorityRequest
            {
                Alias = repository.Alias,
                WorkspaceRoot = repository.WorkspaceRoot
            })
            .ToList();
        additional.Add(request);
        return AgentRepositoryAuthoritySetStore.Current.Replace(taskId, new ReplaceAgentRepositorySetRequest
        {
            PrimaryWorkspaceRoot = primary.WorkspaceRoot,
            AdditionalRepositories = additional
        }, task);
    }

    private static bool RemoveRepository(string taskId, AgentTaskDto task, string alias)
    {
        AgentRepositorySetDto? authoritative = AgentRepositoryAuthoritySetStore.Current.Get(taskId);
        if (authoritative == null)
            return AgentRepositorySetStore.Current.Remove(taskId, alias, task);

        if (string.Equals(alias?.Trim(), AgentRepositorySetStore.PrimaryAlias, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The primary repository authority cannot be removed from an existing coding task.");

        AgentRepositoryAuthorityDto primary = authoritative.Repositories.Single(repository => repository.IsPrimary);
        List<AgentRepositoryAuthorityDto> remaining = authoritative.Repositories
            .Where(repository => repository.IsPrimary
                || !string.Equals(repository.Alias, alias?.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (remaining.Count == authoritative.Repositories.Count) return false;

        return AgentRepositoryAuthoritySetStore.Current.Replace(taskId, new ReplaceAgentRepositorySetRequest
        {
            PrimaryWorkspaceRoot = primary.WorkspaceRoot,
            AdditionalRepositories = remaining
                .Where(repository => !repository.IsPrimary)
                .Select(repository => new AddAgentRepositoryAuthorityRequest
                {
                    Alias = repository.Alias,
                    WorkspaceRoot = repository.WorkspaceRoot
                })
                .ToList()
        }, task).Repositories.Count == remaining.Count;
    }

    private static bool IsDesktopOwner(HttpContext context) =>
        string.Equals(context.Items["PaRuXOwner"] as string, "desktop", StringComparison.Ordinal);

    private static bool IsDesktopLoopbackOwner(HttpContext context) =>
        IsDesktopOwner(context)
        && (context.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(context.Connection.RemoteIpAddress));
}
