using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Services
{
    /// <summary>
    /// Desktop worker for durable orchestrator tasks routed to the "binary-analysis" role.
    /// Analysis is read-only against a task-scoped desktop workspace authority, so unlike coding
    /// tasks it does not need a git worktree or merge lifecycle. Results still pass through the
    /// common verifier and never serialize workstation paths into the remotely visible task result.
    /// </summary>
    public sealed class AgentBinaryAnalysisWorkerService
    {
        public static AgentBinaryAnalysisWorkerService Current { get; } = new AgentBinaryAnalysisWorkerService();
        public const string WorkspaceRequiredStage = "Waiting for desktop Binary Analysis workspace authority";
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private sealed class Tracked
        {
            public required CancellationTokenSource Cts { get; init; }
            public bool IsExecuting { get; set; }
        }

        private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.Ordinal);
        private readonly HashSet<string> _missingWorkspaceLogged = new(StringComparer.Ordinal);
        private bool _started;

        private AgentBinaryAnalysisWorkerService() { }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _ = RunLoopAsync();
        }

        private async Task RunLoopAsync()
        {
            while (true)
            {
                try { await PollOnceAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log.Write("AgentBinaryAnalysisWorkerService poll failed: " + ex.Message); }
                await Task.Delay(PollInterval).ConfigureAwait(false);
            }
        }

        private async Task PollOnceAsync()
        {
            AgentTaskListResponse response;
            try { response = await AgentOrchestratorClient.Current.ListAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("AgentBinaryAnalysisWorkerService list failed: " + ex.Message); return; }

            var seenIds = new HashSet<string>(response.Tasks.Select(t => t.Id), StringComparer.Ordinal);
            foreach (AgentTaskDto task in response.Tasks)
            {
                if (!string.Equals(task.Role, "binary-analysis", StringComparison.Ordinal)) continue;

                if (task.Status == AgentTaskStatus.Running && !_tracked.ContainsKey(task.Id))
                {
                    string? workspaceRoot = await ResolveWorkspaceForTaskAsync(task).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(workspaceRoot)) continue;

                    _missingWorkspaceLogged.Remove(task.Id);
                    var tracked = new Tracked { Cts = new CancellationTokenSource(), IsExecuting = true };
                    _tracked[task.Id] = tracked;
                    _ = ExecuteAsync(task, workspaceRoot, tracked);
                    continue;
                }

                if (!_tracked.TryGetValue(task.Id, out Tracked? current)) continue;
                if (current.IsExecuting && task.Status == AgentTaskStatus.Cancelled && !current.Cts.IsCancellationRequested)
                    current.Cts.Cancel();
                if (!current.IsExecuting && AgentTaskStatus.Terminal.Contains(task.Status))
                {
                    current.Cts.Dispose();
                    _tracked.Remove(task.Id);
                }
            }

            foreach (string stale in _tracked.Keys.Where(id => !seenIds.Contains(id)).ToList())
            {
                _tracked[stale].Cts.Dispose();
                _tracked.Remove(stale);
            }
            _missingWorkspaceLogged.RemoveWhere(id => !seenIds.Contains(id));
        }

        private async Task<string?> ResolveWorkspaceForTaskAsync(AgentTaskDto task)
        {
            try
            {
                AgentWorkspaceAssignmentDto? assigned = await AgentOrchestratorClient.Current
                    .TryGetWorkspaceAssignmentAsync(task.Id, CancellationToken.None).ConfigureAwait(false);
                if (assigned != null)
                {
                    if (Directory.Exists(assigned.WorkspaceRoot))
                        return Path.GetFullPath(assigned.WorkspaceRoot);

                    await MarkWaitingForWorkspaceAsync(task.Id,
                        "Assigned Binary Analysis workspace no longer exists. Execution is paused until the task receives a valid desktop workspace authority.")
                        .ConfigureAwait(false);
                    return null;
                }

                string configured = SettingsStore.Current.Data.BinaryAnalysisWorkspaceRoot?.Trim() ?? string.Empty;
                if (configured.Length > 0 && Directory.Exists(configured))
                {
                    AgentWorkspaceAssignmentDto migrated = await AgentOrchestratorClient.Current
                        .SetWorkspaceAssignmentAsync(task.Id, Path.GetFullPath(configured), CancellationToken.None)
                        .ConfigureAwait(false);
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Migrated the configured Binary Analysis workspace into task-scoped desktop authority.",
                        CancellationToken.None).ConfigureAwait(false);
                    return migrated.WorkspaceRoot;
                }

                await MarkWaitingForWorkspaceAsync(task.Id,
                    "Binary Analysis is waiting for an explicit desktop workspace assignment. PaRuX will not silently reuse the Coding Agent repository.")
                    .ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                Log.Write("Binary Analysis workspace authority lookup failed for " + task.Id + ": " + ex.Message);
                await MarkWaitingForWorkspaceAsync(task.Id,
                    "Binary Analysis workspace authority could not be read safely; execution remains blocked until the authority is repaired: " + ex.Message)
                    .ConfigureAwait(false);
                return null;
            }
        }

        private async Task MarkWaitingForWorkspaceAsync(string taskId, string message)
        {
            if (!_missingWorkspaceLogged.Add(taskId)) return;
            try
            {
                await AgentOrchestratorClient.Current.UpdateStatusAsync(taskId, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Running,
                    Stage = WorkspaceRequiredStage,
                    ProgressPercent = 0
                }, CancellationToken.None).ConfigureAwait(false);
                await AgentOrchestratorClient.Current.AppendLogAsync(taskId, message, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("Binary Analysis waiting-state reporting failed for " + taskId + ": " + ex.Message);
            }
        }

        private async Task ExecuteAsync(AgentTaskDto task, string workspaceRoot, Tracked tracked)
        {
            try
            {
                await AgentOrchestratorClient.Current.AppendLogAsync(
                    task.Id,
                    "Desktop binary-analysis agent claimed the task with its task-scoped read-only workspace authority.",
                    CancellationToken.None).ConfigureAwait(false);

                await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Running,
                    Stage = "Inspecting binary artifacts",
                    ProgressPercent = 10
                }, CancellationToken.None).ConfigureAwait(false);

                string goalForAgent = task.Goal;
                if (!string.IsNullOrWhiteSpace(task.DelegatedContext))
                    goalForAgent += "\n\nEXPLICIT DELEGATED CONTEXT (bounded; no parent transcript):\n" + task.DelegatedContext.Trim();

                AgentDependencyEvidenceCompiler.Compiled dependencyEvidence =
                    await CompileDependencyEvidenceAsync(task, tracked.Cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(dependencyEvidence.PromptText))
                {
                    goalForAgent += "\n\nDIRECT PREDECESSOR EVIDENCE (verified durable results; no predecessor transcript):\n"
                        + dependencyEvidence.PromptText;
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Binary Analysis received bounded evidence from " + dependencyEvidence.DependencyCount
                        + " completed predecessor task(s) (" + dependencyEvidence.UsedCharacters + " chars).",
                        CancellationToken.None).ConfigureAwait(false);
                }
                if (!string.IsNullOrWhiteSpace(task.RepairContext))
                {
                    goalForAgent += "\n\nINDEPENDENT VERIFIER REPAIR FEEDBACK (attempt "
                        + task.RepairAttemptsUsed + "/" + task.MaxRepairAttempts + "):\n" + task.RepairContext.Trim();
                }

                BinaryAnalysisAgentResult result = await BinaryAnalysisAgentService.RunAsync(
                    workspaceRoot,
                    goalForAgent,
                    line => { _ = AgentOrchestratorClient.Current.AppendLogAsync(task.Id, line, CancellationToken.None); },
                    tracked.Cts.Token).ConfigureAwait(false);

                if (result.Cancelled)
                {
                    await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Cancelled,
                        Stage = "Binary analysis stopped"
                    }, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                string resultJson = JsonSerializer.Serialize(new
                {
                    summary = result.Summary,
                    stepsTaken = result.StepsTaken,
                    goalReported = result.GoalReported,
                    analysisMode = "read-only-evidence",
                    workspaceAuthority = "desktop-task-scoped"
                }, JsonOptions);

                await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Verifying,
                    Stage = "Awaiting verification",
                    ProgressPercent = 90,
                    ResultJson = resultJson
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (tracked.Cts.IsCancellationRequested)
            {
                await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Cancelled,
                    Stage = "Binary analysis stopped"
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("AgentBinaryAnalysisWorkerService execute failed: " + ex.Message);
                try
                {
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id, "Binary analysis failed: " + ex.Message, CancellationToken.None).ConfigureAwait(false);
                    await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Failed,
                        Stage = "Binary analysis failed",
                        Error = ex.Message
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception reportEx)
                {
                    Log.Write("AgentBinaryAnalysisWorkerService failure reporting failed: " + reportEx.Message);
                }
            }
            finally
            {
                if (_tracked.TryGetValue(task.Id, out Tracked? current) && ReferenceEquals(current, tracked))
                    tracked.IsExecuting = false;
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
                    Log.Write("Could not load predecessor evidence for Binary Analysis task " + task.Id
                        + " from " + dependencyId + ": " + ex.Message);
                }
            }
            return AgentDependencyEvidenceCompiler.Compile(inputs);
        }
    }
}
