using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Services
{
    /// <summary>
    /// Desktop worker for orchestrator tasks with role "coding". A task may own one or several
    /// explicit desktop-only repository authorities. Single-repository tasks preserve the historic
    /// WorktreeService path; multi-repository tasks materialize one independent Git worktree per
    /// authority below a PaRuX-owned aggregate workspace. No global workspace fallback exists.
    /// Coding self-delegation and durable user questions pause this worker without verifier handoff;
    /// when Core resumes the parent, bounded terminal child/user-input evidence is injected into the
    /// same durable worktree/checkpoint rather than restarting from repository HEAD.
    /// </summary>
    public sealed class AgentCodingWorkerService
    {
        public static AgentCodingWorkerService Current { get; } = new AgentCodingWorkerService();

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        public const string WorkspaceRequiredStage = "Waiting for desktop repository authority";
        private const string AuthorityClaimedStage = "Desktop worker claimed repository authority set";
        private const int MaxDelegatedChildrenEvidence = 6;
        private const int MaxDelegatedChildEvidenceCharacters = 20_000;

        private sealed class Tracked
        {
            public required AgentRepositorySetDto Authorities;
            public required CancellationTokenSource Cts;
            public string? WorkspaceRoot;
            public string? SingleRepositoryRoot;
            public string? SingleBranch;
            public List<MultiRepositoryWorktreeEntry> MultiRepositories { get; } = new();
            public bool IsExecuting;
            public bool IsMulti => Authorities.Repositories.Count > 1;
        }

        private readonly Dictionary<string, Tracked> _tracked = new();
        private readonly HashSet<string> _missingWorkspaceLogged = new(StringComparer.Ordinal);

        /// <summary>When each authority-less coding task was first seen. Refusing to borrow a global
        /// workspace is correct, but waiting for an authority that nothing is going to grant is not a
        /// state a task can sit in forever: one created from Settings with no task-scoped repository
        /// stayed "running" indefinitely, showing a stage and no way to act on it.</summary>
        private readonly Dictionary<string, DateTime> _missingWorkspaceSinceUtc = new(StringComparer.Ordinal);

        /// <summary>How long a coding task may wait for repository authority before it is failed with
        /// an actionable reason. Authority arrives within a poll or two when it arrives at all.</summary>
        private static readonly TimeSpan MissingWorkspaceGrace = TimeSpan.FromMinutes(2);
        private static readonly SemaphoreSlim _desktopExecutionLease = new(1, 1);
        public static bool HasActiveExecutionLease => _desktopExecutionLease.CurrentCount == 0;

        private bool _started;

        private AgentCodingWorkerService() { }

        public void Start()
        {
            if (_started) return;
            _started = true;
            CodingTaskChangeTracker.Configure(AppPaths.CodingAgentStateDir, new OrchestratorReviewStatusSink());
            _ = RunLoopAsync();
        }

        private async Task RunLoopAsync()
        {
            while (true)
            {
                try { await PollOnceAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log.Write("AgentCodingWorkerService poll failed: " + ex.Message); }
                await Task.Delay(PollInterval).ConfigureAwait(false);
            }
        }

        private async Task PollOnceAsync()
        {
            AgentTaskListResponse response;
            try { response = await AgentOrchestratorClient.Current.ListAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("AgentCodingWorkerService list failed: " + ex.Message); return; }

            var seenIds = new HashSet<string>(response.Tasks.Select(t => t.Id));

            foreach (AgentTaskDto task in response.Tasks)
            {
                if (!string.Equals(task.Role, "coding", StringComparison.Ordinal)) continue;

                await HandleReviewStatusSignalsAsync(task).ConfigureAwait(false);

                if (task.Status == AgentTaskStatus.Running && !_tracked.ContainsKey(task.Id))
                {
                    AgentRepositorySetDto? initial = await ResolveRepositorySetForTaskAsync(task).ConfigureAwait(false);
                    if (initial == null || initial.Repositories.Count == 0)
                    {
                        if (_missingWorkspaceLogged.Add(task.Id))
                        {
                            _missingWorkspaceSinceUtc[task.Id] = DateTime.UtcNow;
                            await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id,
                                new UpdateAgentTaskStatusRequest
                                {
                                    Status = AgentTaskStatus.Running,
                                    Stage = WorkspaceRequiredStage,
                                    ProgressPercent = 0
                                }, CancellationToken.None).ConfigureAwait(false);
                            await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                                "Coding task is waiting for explicit task-scoped desktop repository authority. PaRuX will not borrow a global Coding workspace.",
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        else if (_missingWorkspaceSinceUtc.TryGetValue(task.Id, out DateTime waitingSince)
                            && DateTime.UtcNow - waitingSince > MissingWorkspaceGrace)
                        {
                            _missingWorkspaceSinceUtc.Remove(task.Id);
                            _missingWorkspaceLogged.Remove(task.Id);
                            await FailAsync(task.Id,
                                "No repository authority was granted for this coding task, so it can never run. "
                                + "A coding task carries its own repository: create it with an explicit workspace "
                                + "(parux agent run \"<goal>\" --role coding --workspace \"<repo>\"), or set the Coding "
                                + "agent workspace in Settings › Agents before creating it. The global workspace is "
                                + "deliberately never borrowed for a task that did not ask for it.").ConfigureAwait(false);
                        }
                        continue;
                    }

                    await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id,
                        new UpdateAgentTaskStatusRequest
                        {
                            Status = AgentTaskStatus.Running,
                            Stage = AuthorityClaimedStage,
                            ProgressPercent = 1
                        }, CancellationToken.None).ConfigureAwait(false);
                    AgentRepositorySetDto? frozen = await ResolveRepositorySetForTaskAsync(task).ConfigureAwait(false);
                    if (frozen == null || frozen.Repositories.Count == 0)
                    {
                        await FailAsync(task.Id, "Repository authority disappeared while the desktop worker was freezing the task trust boundary.").ConfigureAwait(false);
                        continue;
                    }

                    _missingWorkspaceLogged.Remove(task.Id);
                    _missingWorkspaceSinceUtc.Remove(task.Id);
                    var tracked = new Tracked
                    {
                        Authorities = frozen,
                        Cts = new CancellationTokenSource(),
                        IsExecuting = true
                    };
                    _tracked[task.Id] = tracked;
                    _ = ExecuteAsync(task, tracked);
                    continue;
                }

                if (!_tracked.TryGetValue(task.Id, out Tracked? info) || info == null) continue;

                if (info.IsExecuting)
                {
                    if (task.Status == AgentTaskStatus.Cancelled && !info.Cts.IsCancellationRequested)
                        info.Cts.Cancel();
                    continue;
                }

                if (task.Status == AgentTaskStatus.Running)
                {
                    try { info.Cts.Dispose(); } catch { }
                    info.Cts = new CancellationTokenSource();
                    info.IsExecuting = true;
                    _ = ExecuteAsync(task, info);
                    continue;
                }

                if (task.Status == AgentTaskStatus.Cancelled)
                {
                    await CleanupTrackedAsync(info, deleteBranches: true).ConfigureAwait(false);
                    _tracked.Remove(task.Id);
                }
                else if (task.Status == AgentTaskStatus.Completed)
                {
                    _tracked.Remove(task.Id);
                }
                else if (task.Status == AgentTaskStatus.Failed || task.Status == AgentTaskStatus.Blocked)
                {
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Coding workspace preserved after " + task.Status + " for review/repair.",
                        CancellationToken.None).ConfigureAwait(false);
                    _tracked.Remove(task.Id);
                }
            }

            foreach (string staleId in _tracked.Keys.Where(id => !seenIds.Contains(id)).ToList())
                _tracked.Remove(staleId);
            _missingWorkspaceLogged.RemoveWhere(id => !seenIds.Contains(id));
            foreach (string staleId in _missingWorkspaceSinceUtc.Keys.Where(id => !seenIds.Contains(id)).ToList())
                _missingWorkspaceSinceUtc.Remove(staleId);
        }

        private static async Task<AgentRepositorySetDto?> ResolveRepositorySetForTaskAsync(AgentTaskDto task)
        {
            try
            {
                AgentRepositorySetDto set = await AgentOrchestratorClient.Current
                    .GetRepositorySetAsync(task.Id, CancellationToken.None).ConfigureAwait(false);
                foreach (AgentRepositoryAuthorityDto authority in set.Repositories)
                {
                    if (!Directory.Exists(authority.WorkspaceRoot))
                    {
                        await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                            "Assigned desktop repository '" + authority.Alias + "' no longer exists.", CancellationToken.None).ConfigureAwait(false);
                        return null;
                    }
                }
                return set;
            }
            catch (Exception ex)
            {
                Log.Write("AgentCodingWorkerService repository authority lookup failed for " + task.Id + ": " + ex.Message);
                return null;
            }
        }

        private async Task ExecuteAsync(AgentTaskDto task, Tracked tracked)
        {
            WorktreeResult? singleWorktree = null;
            MultiRepositoryWorktreeResult? multiWorktree = null;
            string? inheritedFromTaskId = null;
            bool leaseAcquired = false;
            try
            {
                await _desktopExecutionLease.WaitAsync(tracked.Cts.Token).ConfigureAwait(false);
                leaseAcquired = true;

                await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                    task.RepairAttemptsUsed > 0
                        ? "Desktop coding agent reclaimed verifier repair attempt " + task.RepairAttemptsUsed + "/" + task.MaxRepairAttempts
                        : "Desktop coding agent claimed the task - preparing " + tracked.Authorities.Repositories.Count + " isolated repository worktree(s)",
                    CancellationToken.None).ConfigureAwait(false);

                string? parentWorkspace = null;
                if (!string.IsNullOrWhiteSpace(task.ParentId))
                {
                    AgentTaskDto parent;
                    try { parent = await AgentOrchestratorClient.Current.GetAsync(task.ParentId, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        await FailAsync(task.Id, "This task requested continuation from parent " + task.ParentId
                            + ", but that parent could not be loaded: " + ex.Message).ConfigureAwait(false);
                        _tracked.Remove(task.Id);
                        return;
                    }

                    if (string.Equals(parent.Role, "coding", StringComparison.Ordinal))
                    {
                        AgentDesktopWorktreeDto parentDesktop;
                        AgentRepositorySetDto parentAuthorities;
                        try
                        {
                            parentDesktop = await AgentOrchestratorClient.Current
                                .GetDesktopWorktreeAsync(parent.Id, CancellationToken.None).ConfigureAwait(false);
                            parentAuthorities = await AgentOrchestratorClient.Current
                                .GetRepositorySetAsync(parent.Id, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            await FailAsync(task.Id, "This continuation could not resolve the parent's desktop coding workspace/authorities: " + ex.Message)
                                .ConfigureAwait(false);
                            _tracked.Remove(task.Id);
                            return;
                        }

                        if (!TopologyCompatible(parentAuthorities, tracked.Authorities))
                        {
                            await FailAsync(task.Id,
                                "Coding continuation repository topology differs from parent task " + parent.Id
                                + ". PaRuX refused to silently drop or remap a repository snapshot; create a new root task or use the same aliases/authorities.")
                                .ConfigureAwait(false);
                            _tracked.Remove(task.Id);
                            return;
                        }

                        parentWorkspace = parentDesktop.WorktreePath;
                        if (string.IsNullOrWhiteSpace(parentWorkspace) || !Directory.Exists(parentWorkspace))
                        {
                            await FailAsync(task.Id, "Parent coding task " + parent.Id
                                + " no longer has its real reviewable workspace. PaRuX refused to restart continuation from repository HEAD.")
                                .ConfigureAwait(false);
                            _tracked.Remove(task.Id);
                            return;
                        }
                        inheritedFromTaskId = parent.Id;
                        await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                            "Continuation will fork/resume each authorized repository from parent task " + parent.Id
                            + " including bounded tracked/untracked working state.", CancellationToken.None).ConfigureAwait(false);
                    }
                }

                if (tracked.IsMulti)
                {
                    multiWorktree = await MultiRepositoryWorktreeService.PrepareAsync(
                        tracked.Authorities, task.Id, parentWorkspace, tracked.Cts.Token).ConfigureAwait(false);
                    if (!multiWorktree.Success)
                    {
                        await FailAsync(task.Id, "Could not prepare multi-repository workspace: " + multiWorktree.Error).ConfigureAwait(false);
                        _tracked.Remove(task.Id);
                        return;
                    }
                    tracked.WorkspaceRoot = multiWorktree.WorkspaceRoot;
                    tracked.MultiRepositories.Clear();
                    tracked.MultiRepositories.AddRange(multiWorktree.Repositories);
                    await AgentOrchestratorClient.Current.SetWorktreeAsync(task.Id, multiWorktree.WorkspaceRoot, CancellationToken.None).ConfigureAwait(false);
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Prepared multi-repository workspace with aliases: "
                        + string.Join(", ", multiWorktree.Repositories.Select(entry => entry.Alias + " [" + entry.Branch + "]"))
                        + (inheritedFromTaskId == null ? string.Empty : "; inherited from parent " + inheritedFromTaskId),
                        CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    AgentRepositoryAuthorityDto authority = tracked.Authorities.Repositories.Single();
                    string repoRoot = Path.GetFullPath(authority.WorkspaceRoot);
                    singleWorktree = inheritedFromTaskId == null
                        ? await WorktreeService.CreateAsync(repoRoot, task.Id, tracked.Cts.Token).ConfigureAwait(false)
                        : await WorktreeService.ForkFromParentAsync(repoRoot, task.Id, parentWorkspace!, tracked.Cts.Token).ConfigureAwait(false);
                    if (!singleWorktree.Success)
                    {
                        await FailAsync(task.Id, "Could not prepare a worktree: " + singleWorktree.Error).ConfigureAwait(false);
                        _tracked.Remove(task.Id);
                        return;
                    }
                    tracked.WorkspaceRoot = singleWorktree.Path;
                    tracked.SingleRepositoryRoot = repoRoot;
                    tracked.SingleBranch = singleWorktree.Branch;
                    await AgentOrchestratorClient.Current.SetWorktreeAsync(task.Id, singleWorktree.Path, CancellationToken.None).ConfigureAwait(false);
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        (singleWorktree.Resumed ? "Resumed" : "Prepared") + " task worktree (branch " + singleWorktree.Branch + ")"
                        + (inheritedFromTaskId == null ? string.Empty : "; inherited real snapshot from parent " + inheritedFromTaskId),
                        CancellationToken.None).ConfigureAwait(false);
                }

                string workspace = tracked.WorkspaceRoot
                    ?? throw new InvalidOperationException("Coding workspace was prepared but no physical workspace root was recorded.");

                await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id,
                    new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Running,
                        Stage = "Preparing warm coding environment",
                        ProgressPercent = 3
                    }, CancellationToken.None).ConfigureAwait(false);

                CodingWarmEnvironmentResult warm = await CodingWarmEnvironmentService.PrepareAsync(
                    workspace, task.Id, task.GrantedCapabilities, tracked.Cts.Token).ConfigureAwait(false);
                await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                    "Warm environment [" + warm.PermissionProfile + "]: "
                    + (warm.ReusedWarmState ? "reused verified dependency fingerprint" : "evaluated dependency state")
                    + "; executed=" + warm.ExecutedCount + ", failed=" + warm.FailedCount + ".",
                    CancellationToken.None).ConfigureAwait(false);
                foreach (CodingWarmEnvironmentStep step in warm.Steps)
                {
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Warm [" + step.RelativeRoot + "/" + step.Ecosystem + "] "
                        + (step.Skipped ? "skipped: " : step.Success ? "ready: " : "warning: ")
                        + step.Detail,
                        CancellationToken.None).ConfigureAwait(false);
                }

                string goalForAgent = BuildGoal(task, tracked, inheritedFromTaskId);
                if (warm.FailedCount > 0)
                {
                    goalForAgent += "\n\nWARM ENVIRONMENT WARNING:\n"
                        + warm.FailedCount + " authorized dependency prewarm step(s) did not complete. "
                        + "Do not assume dependencies are installed; inspect the repository state and use only task-granted restore tools if another attempt is necessary.";
                }

                AgentDependencyEvidenceCompiler.Compiled dependencyEvidence =
                    await CompileDependencyEvidenceAsync(task, tracked.Cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(dependencyEvidence.PromptText))
                {
                    goalForAgent += "\n\nDIRECT PREDECESSOR EVIDENCE (verified durable results; no predecessor transcript):\n"
                        + dependencyEvidence.PromptText;
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Coding agent received bounded evidence from " + dependencyEvidence.DependencyCount
                        + " completed predecessor task(s) (" + dependencyEvidence.UsedCharacters + " chars).",
                        CancellationToken.None).ConfigureAwait(false);
                }

                string childEvidence = await CompileChildEvidenceAsync(task, tracked.Cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(childEvidence))
                {
                    goalForAgent += "\n\nDELEGATED CHILD EVIDENCE (bounded durable results; child transcripts are not loaded):\n"
                        + childEvidence;
                    CodingAgentArtifactStore.SaveText(task.Id, "delegated-child-evidence", "child-task-results", childEvidence);
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Coding agent resumed with bounded terminal evidence from delegated child task(s).",
                        CancellationToken.None).ConfigureAwait(false);
                }

                await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id,
                    new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Running,
                        Stage = "Indexing repository context",
                        ProgressPercent = 5
                    }, CancellationToken.None).ConfigureAwait(false);
                goalForAgent += await BuildRepositoryContextAsync(task, tracked, tracked.Cts.Token).ConfigureAwait(false);

                // Live feedback: the stage used to stay at "Preparing warm coding environment" for the
                // whole run, so the task surfaces showed no movement while the agent worked.
                var stageReporter = new CodingStepStageReporter(TimeSpan.FromSeconds(2));
                var agentClock = System.Diagnostics.Stopwatch.StartNew();
                CodingAgentResult result;
                try
                {
                    result = await CodingAgentBackendRouter.RunAsync(
                        workspace,
                        goalForAgent,
                        line =>
                        {
                            _ = AgentOrchestratorClient.Current.AppendLogAsync(task.Id, line, CancellationToken.None);
                            if (stageReporter.TryBuild(line, DateTime.UtcNow, out string stage, out int percent))
                                stageReporter.Enqueue(() => AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id,
                                    new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running, Stage = stage, ProgressPercent = percent },
                                    CancellationToken.None));
                        },
                        tracked.Cts.Token,
                        sessionId: task.Id,
                        permissionMode: CodingAgentPermissionMode.Normalize(task.CodingRunPermissionMode)).ConfigureAwait(false);
                }
                finally
                {
                    // No queued "running" stage may land after the terminal status below.
                    await stageReporter.DrainAsync().ConfigureAwait(false);
                }

                if (result.Cancelled)
                {
                    await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                    { Status = AgentTaskStatus.Cancelled, Stage = "Coding agent stopped" }, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (result.InfrastructureBlocked)
                {
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id, result.Summary,
                        CancellationToken.None).ConfigureAwait(false);
                    await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Failed,
                        Stage = CodingAgentStopReason.Stage(
                            string.IsNullOrEmpty(result.StopKind) ? CodingAgentStopReason.Sandbox : result.StopKind),
                        Error = result.Summary + CodingAgentStopReason.NextStep(result.StopKind)
                    }, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                // An explicit user question or delegated child is a durable
                // non-terminal pause, even if code verification is still due.
                // Preserve the pending gate for resume instead of failing the
                // task or handing it to an unrelated verifier.
                CodingWorkerHandoffDecision handoff =
                    CodingVerificationCommandPolicy.DecideHandoff(
                        result.WaitingForUserInput, result.WaitingForDelegatedChild,
                        result.GoalReported, result.VerificationPending);

                await PublishReviewStatusAsync(task, workspace, result).ConfigureAwait(false);

                if (handoff == CodingWorkerHandoffDecision.WaitForUser)
                {
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Coding execution paused for user question " + (result.UserQuestionId ?? "(unknown)")
                        + "; no verifier handoff was performed and the current workspace/checkpoint was preserved.",
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (handoff == CodingWorkerHandoffDecision.WaitForDelegatedChild)
                {
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Coding execution paused for delegated child " + (result.DelegatedChildTaskId ?? "(unknown)")
                        + "; no verifier handoff was performed and the current workspace/checkpoint was preserved.",
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (handoff == CodingWorkerHandoffDecision.RejectPendingVerification)
                {
                    string unverified = result.GoalReported
                        ? result.Summary
                            + " Testable code changed but no accepted verification command succeeded, so this attempt was not sent to the verifier. "
                            + "The worktree and checkpoint are preserved for review or resume."
                        : result.Summary
                            + " The coding agent did not explicitly finish its goal. An exhausted or interrupted attempt is not eligible "
                            + "for an independent PASS even when no test command is pending. The worktree and checkpoint are preserved.";
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id, unverified,
                        CancellationToken.None).ConfigureAwait(false);
                    await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Failed,
                        Stage = result.GoalReported
                            ? "Coding attempt paused without verification"
                            : CodingAgentStopReason.Stage(result.StopKind),
                        Error = unverified + CodingAgentStopReason.NextStep(result.StopKind)
                    }, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                CodingAgentCheckpoint? handoffCheckpoint =
                    CodingAgentCheckpointStore.Load(task.Id, workspace, goalForAgent);
                if (handoffCheckpoint == null || handoffCheckpoint.BrowserPostconditionPending)
                {
                    const string pendingBrowser = "Coding browser action evidence is missing or its typed DOM postcondition is still pending. "
                        + "Independent Core verification is blocked until the task is resumed and observed.";
                    await AgentOrchestratorClient.Current.AppendLogAsync(
                        task.Id, pendingBrowser, CancellationToken.None).ConfigureAwait(false);
                    await AgentOrchestratorClient.Current.UpdateStatusAsync(
                        task.Id, new UpdateAgentTaskStatusRequest
                        {
                            Status = AgentTaskStatus.Failed,
                            Stage = "Browser postcondition missing at coding handoff",
                            Error = pendingBrowser
                        }, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                string? verifiedWorkspaceFingerprint = null;
                if (result.VerifiedByCommand)
                {
                    // Between the model's last step and the independent
                    // verifier handoff, another command/process may have
                    // changed the worktree. Re-check the same task-owned
                    // checkpoint and full Git-state fingerprint now.
                    CodingAgentCheckpoint checkpoint = handoffCheckpoint;
                    string? currentFingerprint =
                        await CodingWorkspaceVerificationFingerprint.CaptureAsync(
                            workspace, tracked.Cts.Token).ConfigureAwait(false);
                    if (checkpoint == null
                        || string.IsNullOrWhiteSpace(checkpoint.VerifiedWorkspaceFingerprint)
                        || !string.Equals(checkpoint.VerifiedWorkspaceFingerprint,
                            currentFingerprint, StringComparison.Ordinal))
                    {
                        const string proofInvalid = "Coding worktree changed after its accepted verification command, "
                            + "or the task-owned fingerprint could not be verified. "
                            + "No independent verifier handoff was performed; inspect and rerun verification.";
                        await AgentOrchestratorClient.Current.AppendLogAsync(
                            task.Id, proofInvalid, CancellationToken.None).ConfigureAwait(false);
                        await AgentOrchestratorClient.Current.UpdateStatusAsync(
                            task.Id, new UpdateAgentTaskStatusRequest
                            {
                                Status = AgentTaskStatus.Failed,
                                Stage = "Coding proof invalidated before handoff",
                                Error = proofInvalid
                            }, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    verifiedWorkspaceFingerprint = currentFingerprint;
                }

                string fullDiff;
                object repositorySummary;
                string? branch = null;
                if (tracked.IsMulti)
                {
                    fullDiff = await MultiRepositoryWorktreeService.BuildCombinedDiffAsync(
                        tracked.MultiRepositories, CancellationToken.None, maxCharacters: 48_000).ConfigureAwait(false);
                    repositorySummary = tracked.MultiRepositories.Select(entry => new { alias = entry.Alias, branch = entry.Branch }).ToList();
                }
                else
                {
                    fullDiff = await CodingAgentTools.WorkingTreeDiffWithUntrackedAsync(workspace, CancellationToken.None).ConfigureAwait(false);
                    branch = tracked.SingleBranch;
                    repositorySummary = new[] { new { alias = tracked.Authorities.Repositories.Single().Alias, branch = tracked.SingleBranch } };
                }

                string? diffArtifactId = null;
                const int maxResultDiffChars = 12_000;
                if (fullDiff.Length > maxResultDiffChars)
                {
                    CodingAgentArtifactStore.ArtifactMetadata artifact = CodingAgentArtifactStore.SaveText(
                        task.Id, "final-diff", tracked.IsMulti ? "multi-repository final diff" : "final diff", fullDiff);
                    diffArtifactId = artifact.Id;
                }
                string boundedDiff = string.IsNullOrWhiteSpace(fullDiff) ? "(no working-tree changes)"
                    : fullDiff.Length <= maxResultDiffChars ? fullDiff
                    : fullDiff[..maxResultDiffChars] + "\n… (diff truncated in task result; full evidence artifactId=" + diffArtifactId + ")";

                string resultJson = JsonSerializer.Serialize(new
                {
                    summary = result.Summary,
                    stepsTaken = result.StepsTaken,
                    goalReported = result.GoalReported,
                    // Deterministic evidence Core uses for coding-model selection: an accepted
                    // verification command passed, and the agent's wall time for this attempt.
                    verifiedByCommand = result.VerifiedByCommand,
                    // Core's verifier gates "pass" on these real executed checks, not on a model's opinion.
                    executedChecks = result.ExecutedChecks.Select(check => new { command = check.Command, exitCode = check.ExitCode, scope = check.Scope }).ToList(),
                    verifiedWorkspaceFingerprint,
                    verificationPending = result.VerificationPending,
                    browserPostconditionPending = handoffCheckpoint.BrowserPostconditionPending,
                    verificationAvailable = CodingVerificationDiscovery.Discover(workspace).Count > 0,
                    agentElapsedSeconds = Math.Round(agentClock.Elapsed.TotalSeconds, 1),
                    workspaceMode = tracked.IsMulti ? "multi-repository" : "single-repository",
                    branch,
                    repositories = repositorySummary,
                    diff = boundedDiff,
                    diffArtifactId,
                    parentTaskId = task.ParentId,
                    inheritedWorkspaceFromTaskId = inheritedFromTaskId,
                    repairAttempt = task.RepairAttemptsUsed,
                    checkpointSessionId = task.Id,
                    sandbox = "Windows AppContainer / network capability denied"
                }, JsonOptions);

                await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Verifying,
                    Stage = result.GoalReported ? "Awaiting verification" : "Awaiting verifier decision on paused coding attempt",
                    ProgressPercent = 90,
                    ResultJson = resultJson
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (tracked.Cts.IsCancellationRequested)
            {
                await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                { Status = AgentTaskStatus.Cancelled, Stage = "Coding agent stopped" }, CancellationToken.None).ConfigureAwait(false);
                await CleanupTrackedAsync(tracked, deleteBranches: true).ConfigureAwait(false);
                _tracked.Remove(task.Id);
            }
            catch (Exception ex)
            {
                Log.Write("AgentCodingWorkerService execute failed: " + ex.Message);
                await FailAsync(task.Id, ex.Message).ConfigureAwait(false);
                _tracked.Remove(task.Id);
            }
            finally
            {
                if (leaseAcquired)
                {
                    try { _desktopExecutionLease.Release(); } catch { }
                }
                if (_tracked.TryGetValue(task.Id, out Tracked? stillTracked) && stillTracked == tracked)
                    tracked.IsExecuting = false;
            }
        }

        private static string BuildGoal(AgentTaskDto task, Tracked tracked, string? inheritedFromTaskId)
        {
            var goal = new StringBuilder(task.Goal);
            if (tracked.IsMulti)
            {
                goal.Append("\n\nMULTI-REPOSITORY WORKSPACE:\n")
                    .Append("The workspace root is NOT itself a Git repository. Each direct directory below is an independently sandboxed real Git worktree. Use alias-prefixed file paths and run repository commands from the correct alias. Do not move files between repositories unless the task explicitly requires that change.\n");
                foreach (AgentRepositoryAuthorityDto authority in tracked.Authorities.Repositories)
                    goal.Append("- ").Append(authority.Alias).Append("/\n");
                goal.Append("PaRuX requires the discovered all-repository verification gate after final testable mutations; use the exact verify-all command discovered for this aggregate workspace.");
            }
            if (!string.IsNullOrWhiteSpace(task.DelegatedContext))
                goal.Append("\n\nEXPLICIT DELEGATED CONTEXT (bounded; no parent transcript):\n").Append(task.DelegatedContext.Trim());
            if (!string.IsNullOrWhiteSpace(task.RepairContext))
            {
                goal.Append("\n\nINDEPENDENT VERIFIER REPAIR FEEDBACK (attempt ")
                    .Append(task.RepairAttemptsUsed).Append('/').Append(task.MaxRepairAttempts).Append("):\n")
                    .Append(task.RepairContext.Trim())
                    .Append("\nInspect the existing workspace/diffs first. Fix the concrete failure without discarding already-correct changes, then rerun relevant verification.");
            }
            if (inheritedFromTaskId != null)
                goal.Append("\n\nContinuation snapshot inherited from coding parent ").Append(inheritedFromTaskId).Append("; current files/diffs are authoritative.");
            return goal.ToString();
        }

        private static async Task<string> BuildRepositoryContextAsync(
            AgentTaskDto task, Tracked tracked, CancellationToken ct)
        {
            var output = new StringBuilder();
            IEnumerable<(string Alias, string Path)> repositories = tracked.IsMulti
                ? tracked.MultiRepositories.Select(entry => (entry.Alias, entry.WorktreePath))
                : new[] { (tracked.Authorities.Repositories.Single().Alias, tracked.WorkspaceRoot!) };

            const int maxTotalContext = 24_000;
            foreach ((string alias, string path) in repositories)
            {
                if (output.Length >= maxTotalContext) break;
                try
                {
                    var contextClock = System.Diagnostics.Stopwatch.StartNew();
                    await RepoIndexClient.Current.IndexRepositoryAsync(path, ct).ConfigureAwait(false);
                    long indexMs = contextClock.ElapsedMilliseconds;
                    ContextPackDto contextPack = await RepoIndexClient.Current
                        .BuildContextAsync(path, task.Goal, ct).ConfigureAwait(false);
                    long packMs = contextClock.ElapsedMilliseconds - indexMs;
                    if (contextPack.Items.Count == 0) continue;
                    string text = contextPack.PromptText ?? string.Empty;
                    int remaining = maxTotalContext - output.Length;
                    if (text.Length > remaining) text = text[..remaining] + "\n… (repository context truncated)";
                    output.Append("\n\nREPOSITORY CONTEXT [").Append(alias).Append("]:\n").Append(text);
                    int relevantFiles = contextPack.Items.Count(item => item.Kind == "repository_file");
                    int relevantSymbols = contextPack.Items.Count(item => item.Kind == "repository_symbol");
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Context Engine [" + alias + "] selected " + relevantFiles + " file(s), " + relevantSymbols
                        + " symbol(s), using " + contextPack.UsedCharacters + "/" + contextPack.MaxCharacters + " characters"
                        + " (index " + indexMs + " ms, context " + packMs + " ms)",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Write("AgentCodingWorkerService repo-index lookup failed for alias " + alias + " (continuing): " + ex.Message);
                }
            }
            return output.ToString();
        }

        private static bool TopologyCompatible(AgentRepositorySetDto parent, AgentRepositorySetDto child)
        {
            if (parent.Repositories.Count != child.Repositories.Count || parent.Repositories.Count == 0) return false;
            if (parent.Repositories.Count == 1)
                return PathEquals(parent.Repositories[0].WorkspaceRoot, child.Repositories[0].WorkspaceRoot);

            foreach (AgentRepositoryAuthorityDto childAuthority in child.Repositories)
            {
                AgentRepositoryAuthorityDto? parentAuthority = parent.Repositories.FirstOrDefault(value =>
                    string.Equals(value.Alias, childAuthority.Alias, StringComparison.OrdinalIgnoreCase));
                if (parentAuthority == null || !PathEquals(parentAuthority.WorkspaceRoot, childAuthority.WorkspaceRoot)) return false;
            }
            return true;
        }

        private static bool PathEquals(string left, string right)
        {
            try
            {
                string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static async Task CleanupTrackedAsync(Tracked tracked, bool deleteBranches)
        {
            if (tracked.IsMulti && tracked.MultiRepositories.Count > 0)
            {
                await MultiRepositoryWorktreeService.CleanupAsync(
                    tracked.MultiRepositories, deleteBranches, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            if (!string.IsNullOrWhiteSpace(tracked.SingleRepositoryRoot) && !string.IsNullOrWhiteSpace(tracked.WorkspaceRoot))
            {
                await WorktreeService.CleanupAsync(
                    tracked.SingleRepositoryRoot, tracked.WorkspaceRoot, tracked.SingleBranch,
                    deleteBranches, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static async Task<AgentDependencyEvidenceCompiler.Compiled> CompileDependencyEvidenceAsync(
            AgentTaskDto task,
            CancellationToken cancellationToken)
        {
            var inputs = new List<AgentDependencyEvidenceCompiler.Input>();
            foreach (string dependencyId in task.DependsOnTaskIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    AgentTaskDto dependency = await AgentOrchestratorClient.Current
                        .GetAsync(dependencyId, cancellationToken).ConfigureAwait(false);
                    if (dependency.Status != AgentTaskStatus.Completed) continue;
                    inputs.Add(new AgentDependencyEvidenceCompiler.Input(
                        dependency.Id, dependency.Role, dependency.VerificationStatus, dependency.ResultJson));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Write("Could not load predecessor evidence for coding task " + task.Id
                        + " from " + dependencyId + ": " + ex.Message);
                }
            }
            return AgentDependencyEvidenceCompiler.Compile(inputs);
        }

        private static async Task<string> CompileChildEvidenceAsync(
            AgentTaskDto task,
            CancellationToken cancellationToken)
        {
            try
            {
                AgentTaskListResponse response = await AgentOrchestratorClient.Current
                    .ListAsync(cancellationToken).ConfigureAwait(false);
                List<AgentTaskDto> terminalChildren = response.Tasks
                    .Where(child => string.Equals(child.ParentId, task.Id, StringComparison.Ordinal)
                        && AgentTaskStatus.Terminal.Contains(child.Status))
                    .OrderBy(child => child.CreatedUtc)
                    .Take(MaxDelegatedChildrenEvidence)
                    .ToList();
                if (terminalChildren.Count == 0) return string.Empty;

                var inputs = terminalChildren
                    .Where(child => child.Status == AgentTaskStatus.Completed)
                    .Select(child => new AgentDependencyEvidenceCompiler.Input(
                        child.Id, child.Role, child.VerificationStatus, child.ResultJson))
                    .ToList();
                AgentDependencyEvidenceCompiler.Compiled compiled = AgentDependencyEvidenceCompiler.Compile(inputs);
                var output = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(compiled.PromptText)) output.AppendLine(compiled.PromptText);
                foreach (AgentTaskDto child in terminalChildren.Where(child => child.Status != AgentTaskStatus.Completed))
                {
                    output.Append("- child ").Append(child.Id).Append(" [").Append(child.Role ?? "auto").Append("] ")
                        .Append(child.Status);
                    if (!string.IsNullOrWhiteSpace(child.Error)) output.Append(": ").Append(Bound(child.Error, 2_000));
                    output.AppendLine();
                }
                return Bound(output.ToString(), MaxDelegatedChildEvidenceCharacters);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Write("Could not compile delegated child evidence for coding task " + task.Id + ": " + ex.Message);
                return string.Empty;
            }
        }

        private static string Bound(string? text, int max)
        {
            string value = text ?? string.Empty;
            return value.Length <= max ? value : value[..max] + "\n… (truncated)";
        }

        /// <summary>
        /// After every patch update: re-anchor review findings (never dropped) and publish them, then apply the
        /// verification-failure policy to the agent's own changes (offer by default, perform only if authorized).
        /// Best effort: a publishing failure must never change the task outcome.
        /// </summary>
        private static async Task PublishReviewStatusAsync(AgentTaskDto task, string workspace, CodingAgentResult result)
        {
            try
            {
                await CodingTaskChangeTracker.Current.ReconcileAndPublishAsync(
                    task.Id, workspace, task.RepairContext, CancellationToken.None).ConfigureAwait(false);
                bool verificationFailed = result.ExecutedChecks.Count > 0 && result.ExecutedChecks[^1].ExitCode != 0;
                await CodingTaskChangeTracker.Current.OnVerificationOutcomeAsync(
                    task.Id, !verificationFailed,
                    CodingTaskChangeTracker.IsRevertAuthorizedByMode(task.CodingRunPermissionMode),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("AgentCodingWorkerService review status publish failed: " + ex.Message);
            }
        }

        private readonly HashSet<string> _verifierFailOfferPublished = new(StringComparer.Ordinal);

        /// <summary>Handles a desktop-owner revert request and the independent verifier's FAIL for tasks with own changes.</summary>
        private async Task HandleReviewStatusSignalsAsync(AgentTaskDto task)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(task.ReviewStatusJson))
                    await CodingTaskChangeTracker.Current.HandleRevertRequestAsync(
                        task.Id, task.ReviewStatusJson, CancellationToken.None).ConfigureAwait(false);
                if (string.Equals(task.Status, AgentTaskStatus.Failed, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(task.VerificationStatus, "fail", StringComparison.OrdinalIgnoreCase)
                    && _verifierFailOfferPublished.Add(task.Id))
                {
                    await CodingTaskChangeTracker.Current.OnVerificationOutcomeAsync(
                        task.Id, verificationPassed: false,
                        CodingTaskChangeTracker.IsRevertAuthorizedByMode(task.CodingRunPermissionMode),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Write("AgentCodingWorkerService review status signal failed: " + ex.Message);
            }
        }

        private sealed class OrchestratorReviewStatusSink : ICodingReviewStatusSink
        {
            public Task PutSectionAsync(string taskId, string section, object value, CancellationToken ct) =>
                AgentOrchestratorClient.Current.PutReviewStatusSectionAsync(taskId, section, value, ct);

            public Task AppendLogAsync(string taskId, string message, CancellationToken ct) =>
                AgentOrchestratorClient.Current.AppendLogAsync(taskId, message, ct);
        }

        private static async Task FailAsync(string taskId, string error)
        {
            try
            {
                await AgentOrchestratorClient.Current.AppendLogAsync(taskId,
                    "Desktop coding agent failed: " + error, CancellationToken.None).ConfigureAwait(false);
                await AgentOrchestratorClient.Current.UpdateStatusAsync(taskId, new UpdateAgentTaskStatusRequest
                { Status = AgentTaskStatus.Failed, Stage = "Coding agent failed", Error = error }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Write("AgentCodingWorkerService FailAsync reporting failed: " + ex.Message); }
        }
    }
}
