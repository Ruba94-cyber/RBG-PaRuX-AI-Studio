using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Services
{
    /// <summary>
    /// Desktop-only executor for orchestrator tasks with role "computer-use". Paired Android
    /// devices request work through the authenticated Core task queue; raw Windows mouse/keyboard
    /// privileges never cross the LAN boundary. A task may receive bounded result evidence from
    /// completed predecessors, but never their transcript or workstation paths.
    /// </summary>
    public sealed class ComputerUseWorkerService
    {
        public static ComputerUseWorkerService Current { get; } = new ComputerUseWorkerService();

        public const string ApprovalRequiredStage = "Computer Use approval required";
        public const string ApprovalGrantedStage = "Computer Use approval granted remotely";

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private sealed class Tracked
        {
            public CancellationTokenSource Cts = new();
            public bool IsExecuting;
        }

        private readonly Dictionary<string, Tracked> _tracked = new();
        private bool _started;

        private ComputerUseWorkerService() { }

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
                catch (Exception ex) { Log.Write("ComputerUseWorkerService poll failed: " + ex.Message); }
                await Task.Delay(PollInterval).ConfigureAwait(false);
            }
        }

        private async Task PollOnceAsync()
        {
            AgentTaskListResponse response;
            try { response = await AgentOrchestratorClient.Current.ListAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("ComputerUseWorkerService list failed: " + ex.Message); return; }

            bool fullAutomationEnabled = SettingsStore.Current.Data.ComputerUseFullAutomationEnabled;
            var seen = new HashSet<string>(response.Tasks.Select(t => t.Id), StringComparer.Ordinal);

            foreach (AgentTaskDto task in response.Tasks)
            {
                if (!string.Equals(task.Role, "computer-use", StringComparison.Ordinal)) continue;

                if (!_tracked.ContainsKey(task.Id))
                {
                    if (task.Status == AgentTaskStatus.Running)
                    {
                        bool perTaskApproved = string.Equals(task.Stage, ApprovalGrantedStage, StringComparison.Ordinal);
                        ComputerUseAuthorizationPolicy.RemoteAuthority? authority =
                            ComputerUseAuthorizationPolicy.ResolveRemoteAuthority(perTaskApproved, fullAutomationEnabled);
                        if (authority != null)
                        {
                            // The mouse and keyboard are shared with chat-initiated runs. While another run holds them, or
                            // Gaming Mode is on, leave this task Running and untracked so a later poll claims it.
                            if (ComputerUseRunGate.Shared.IsBusy) continue;
                            if (ComputerUseAuthorizationPolicy.GamingRefusal(GamingManager.Current.IsActive) != null) continue;

                            var tracked = new Tracked { IsExecuting = true };
                            _tracked[task.Id] = tracked;
                            // The authority decides both the revocation check and the audit label, from one value. A per-task grant
                            // alone survives a settings change. When the Full Computer Use switch is the authority, turning it off
                            // stops the run, even if the task also holds a grant.
                            bool remoteTaskApproval = authority == ComputerUseAuthorizationPolicy.RemoteAuthority.PerTaskApproval;
                            Func<bool>? persistentAuthorization =
                                authority == ComputerUseAuthorizationPolicy.RemoteAuthority.FullComputerUse ? IsFullAutomationEnabled : null;
                            _ = ExecuteAsync(task, tracked, remoteTaskApproval, persistentAuthorization, perTaskApproved);
                        }
                        else
                        {
                            await RequestApprovalAsync(task).ConfigureAwait(false);
                        }
                        continue;
                    }

                    if (task.Status == AgentTaskStatus.Waiting
                        && string.Equals(task.Stage, ApprovalRequiredStage, StringComparison.Ordinal))
                        continue;
                }

                if (!_tracked.TryGetValue(task.Id, out Tracked? current)) continue;
                if (current.IsExecuting)
                {
                    if (task.Status == AgentTaskStatus.Cancelled && !current.Cts.IsCancellationRequested)
                        current.Cts.Cancel();
                    continue;
                }

                _tracked.Remove(task.Id);
            }

            foreach (string stale in _tracked.Keys.Where(id => !seen.Contains(id)).ToList())
                _tracked.Remove(stale);
        }

        private static bool IsFullAutomationEnabled() => SettingsStore.Current.Data.ComputerUseFullAutomationEnabled;

        private static async Task RequestApprovalAsync(AgentTaskDto task)
        {
            await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
            {
                Status = AgentTaskStatus.Waiting,
                Stage = ApprovalRequiredStage,
                ProgressPercent = 1
            }, CancellationToken.None).ConfigureAwait(false);

            await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                "Computer Use requires approval before this Windows task may control mouse/keyboard. " +
                "Approve it from PaRuX Windows or a paired Android PC Studio session. The grant is task-scoped and does not enable Full Computer Use permanently.",
                CancellationToken.None).ConfigureAwait(false);
        }

        private async Task ExecuteAsync(
            AgentTaskDto task, Tracked tracked, bool remoteTaskApproval, Func<bool>? persistentAuthorization, bool restoreApprovalStage)
        {
            try
            {
                await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Running,
                    Stage = "Desktop Computer Use worker claimed task",
                    ProgressPercent = 1
                }, CancellationToken.None).ConfigureAwait(false);
                await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                    remoteTaskApproval
                        ? "Desktop Computer Use worker claimed the task using the explicit one-task remote approval."
                        : "Desktop Computer Use worker claimed the task under the persistent Windows Full Computer Use opt-in.",
                    CancellationToken.None).ConfigureAwait(false);

                string effectiveGoal = task.Goal;
                if (!string.IsNullOrWhiteSpace(task.DelegatedContext))
                    effectiveGoal += "\n\nEXPLICIT DELEGATED CONTEXT (bounded; no parent transcript):\n" + task.DelegatedContext.Trim();

                AgentDependencyEvidenceCompiler.Compiled dependencyEvidence =
                    await CompileDependencyEvidenceAsync(task, tracked.Cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(dependencyEvidence.PromptText))
                {
                    effectiveGoal += "\n\nDIRECT PREDECESSOR EVIDENCE (verified durable results; no predecessor transcript):\n"
                        + dependencyEvidence.PromptText;
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Computer Use received bounded evidence from " + dependencyEvidence.DependencyCount
                        + " completed predecessor task(s) (" + dependencyEvidence.UsedCharacters + " chars).",
                        CancellationToken.None).ConfigureAwait(false);
                }
                if (!string.IsNullOrWhiteSpace(task.RepairContext))
                    effectiveGoal += "\n\nINDEPENDENT VERIFIER REPAIR FEEDBACK:\n" + task.RepairContext.Trim();

                ComputerUseAgentResult result = await ComputerUseAgentService.RunAsync(
                    effectiveGoal,
                    line => { _ = AgentOrchestratorClient.Current.AppendLogAsync(task.Id, line, CancellationToken.None); },
                    tracked.Cts.Token,
                    persistentAuthorization).ConfigureAwait(false);

                if (result.NotStarted)
                {
                    // The run never took the mouse and keyboard, so it used none of its authority. The task stays Running
                    // and a later poll claims it again, rather than failing it for a condition that will clear. The claim
                    // above overwrote the grant stage, so restore it. Otherwise that poll no longer sees the one-task
                    // approval and asks for it again (CU-09).
                    if (restoreApprovalStage)
                    {
                        await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                        {
                            Status = AgentTaskStatus.Running,
                            Stage = ApprovalGrantedStage,
                            ProgressPercent = 1
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Desktop Computer Use did not start: " + result.Summary + " The task stays Running and a later check retries it.",
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (result.Cancelled || tracked.Cts.IsCancellationRequested)
                {
                    await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Cancelled,
                        Stage = "Computer Use stopped"
                    }, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                // A Computer Use run that exhausted its steps or refused an unsafe action
                // is NOT a candidate for independent PASS verification. Preserve the
                // actionable failure and never interpret a mere execution transcript as
                // successful task completion. Explicit "done" still proceeds to the
                // independent verifier, which must decide whether the claim is true.
                if (!ComputerUseCompletionGate.MayRequestVerification(result.GoalReported, result.Cancelled))
                {
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Computer Use did not report its goal complete: " + result.Summary,
                        CancellationToken.None).ConfigureAwait(false);
                    await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id,
                        new UpdateAgentTaskStatusRequest
                        {
                            Status = AgentTaskStatus.Failed,
                            Stage = "Computer Use stopped before completion",
                            Error = result.Summary
                        }, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                string resultJson = JsonSerializer.Serialize(new
                {
                    response = result.Summary,
                    result.GoalReported,
                    result.StepsTaken,
                    executor = "windows-computer-use",
                    authorization = remoteTaskApproval ? "task-scoped-remote-approval" : "windows-full-computer-use",
                    evidenceMode = dependencyEvidence.DependencyCount > 0 ? "bounded-predecessor-results" : "task-only"
                }, JsonOptions);

                await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Verifying,
                    Stage = "Awaiting verification",
                    ProgressPercent = 90,
                    ResultJson = resultJson
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Cancelled,
                        Stage = "Computer Use stopped"
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { Log.Write("ComputerUseWorkerService cancel reporting failed: " + ex.Message); }
            }
            catch (Exception ex)
            {
                Log.Write("ComputerUseWorkerService execute failed: " + ex.Message);
                try
                {
                    await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                        "Desktop Computer Use worker failed: " + ex.Message, CancellationToken.None).ConfigureAwait(false);
                    await AgentOrchestratorClient.Current.UpdateStatusAsync(task.Id, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Failed,
                        Stage = "Computer Use failed",
                        Error = ex.Message
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception reportEx) { Log.Write("ComputerUseWorkerService failure reporting failed: " + reportEx.Message); }
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
                    Log.Write("Could not load predecessor evidence for Computer Use task " + task.Id
                        + " from " + dependencyId + ": " + ex.Message);
                }
            }
            return AgentDependencyEvidenceCompiler.Compile(inputs);
        }
    }
}
