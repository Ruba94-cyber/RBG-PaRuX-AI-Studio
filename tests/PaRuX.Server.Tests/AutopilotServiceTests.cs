using PaRuX.Contracts;
using PaRuX.Server.Autopilot;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;
using PaRuX.Server.ProjectManager;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>vNext Phase 50/51 - AutopilotService's permission-mode gate and final-report
/// rollup. The property that matters most is that SUPERVISED mode is a REAL gate (the held
/// tasks are not schedulable until approved - verified via AgentOrchestrator.RunSchedulingPass
/// itself, not just by reading the stored status back), and that the final report only appears
/// once every task genuinely reached a terminal state.</summary>
public sealed class AutopilotServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-autopilot-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private (AutopilotService autopilot, TaskStore tasks, AgentOrchestrator orchestrator, FakeLocalCompletionClient client) Build()
    {
        var client = new FakeLocalCompletionClient();
        var tasks = new TaskStore(_tempDir);
        var graph = new ContextGraphStore(_tempDir);
        var pm = new ProjectManagerService(client, tasks, graph);
        var autopilotStore = new AutopilotStore(_tempDir);
        var autopilot = new AutopilotService(autopilotStore, pm, tasks);
        var orchestrator = new AgentOrchestrator(tasks);
        return (autopilot, tasks, orchestrator, client);
    }

    [Fact]
    public async Task StartRunAsync_rejects_an_empty_goal_or_unknown_permission_mode()
    {
        var (autopilot, _, _, client) = Build();
        client.EnqueueOk("[]");
        await Assert.ThrowsAsync<InvalidDataException>(() => autopilot.StartRunAsync("  ", AutopilotPermissionMode.Balanced, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => autopilot.StartRunAsync("goal", "not_a_real_mode", CancellationToken.None));
    }

    [Fact]
    public async Task Balanced_mode_leaves_tasks_immediately_schedulable()
    {
        var (autopilot, tasks, orchestrator, client) = Build();
        client.EnqueueOk(@"[{""description"":""Do the thing"",""role"":""planner"",""dependsOn"":[]}]");

        AutopilotRunDto run = await autopilot.StartRunAsync("Ship it", AutopilotPermissionMode.Balanced, CancellationToken.None);

        Assert.Equal(AutopilotRunStatus.Running, run.Status);
        Assert.Equal(AgentTaskStatus.Queued, tasks.Get(run.TaskIds[0])!.Status);

        List<string> dispatched = orchestrator.RunSchedulingPass();
        Assert.Contains(run.TaskIds[0], dispatched); // the real scheduler picks it up immediately
    }

    /// <summary>The core "real gate" property: a SUPERVISED task must not be schedulable even
    /// after a real scheduling pass runs, until ApproveRun is called.</summary>
    [Fact]
    public async Task Supervised_mode_holds_tasks_until_approved_and_the_real_scheduler_respects_the_hold()
    {
        var (autopilot, tasks, orchestrator, client) = Build();
        client.EnqueueOk(@"[{""description"":""Do the sensitive thing"",""role"":""coding"",""dependsOn"":[]}]");

        AutopilotRunDto run = await autopilot.StartRunAsync("Sensitive change", AutopilotPermissionMode.Supervised, CancellationToken.None);
        Assert.Equal(AutopilotRunStatus.PendingApproval, run.Status);
        Assert.Equal(AgentTaskStatus.Blocked, tasks.Get(run.TaskIds[0])!.Status);

        List<string> dispatchedBeforeApproval = orchestrator.RunSchedulingPass();
        Assert.DoesNotContain(run.TaskIds[0], dispatchedBeforeApproval); // the real scheduler must not touch a blocked task

        bool approved = autopilot.ApproveRun(run.Id);
        Assert.True(approved);
        Assert.Equal(AgentTaskStatus.Queued, tasks.Get(run.TaskIds[0])!.Status);

        List<string> dispatchedAfterApproval = orchestrator.RunSchedulingPass();
        Assert.Contains(run.TaskIds[0], dispatchedAfterApproval); // now the real scheduler picks it up
    }

    [Fact]
    public void ApproveRun_returns_false_for_an_unknown_or_already_running_run()
    {
        var (autopilot, _, _, _) = Build();
        Assert.False(autopilot.ApproveRun("parux-test-unknown-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task CheckActiveRuns_does_not_finalize_a_run_while_any_task_is_still_in_flight()
    {
        var (autopilot, tasks, _, client) = Build();
        client.EnqueueOk(@"[
            {""description"":""Task A"",""role"":""planner"",""dependsOn"":[]},
            {""description"":""Task B"",""role"":""planner"",""dependsOn"":[]}
        ]");
        AutopilotRunDto run = await autopilot.StartRunAsync("Two tasks", AutopilotPermissionMode.Balanced, CancellationToken.None);

        // Complete only the first task - the second is still "queued", genuinely in flight.
        tasks.UpdateStatus(run.TaskIds[0], new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed });

        autopilot.CheckActiveRuns();

        AutopilotRunDto stillRunning = autopilot.GetRun(run.Id)!;
        Assert.Equal(AutopilotRunStatus.Running, stillRunning.Status);
        Assert.Null(stillRunning.FinalReportJson);
    }

    [Fact]
    public async Task CheckActiveRuns_finalizes_with_status_completed_when_every_task_completed()
    {
        var (autopilot, tasks, _, client) = Build();
        client.EnqueueOk(@"[{""description"":""Task A"",""role"":""planner"",""dependsOn"":[]}]");
        AutopilotRunDto run = await autopilot.StartRunAsync("One task", AutopilotPermissionMode.Balanced, CancellationToken.None);
        tasks.UpdateStatus(run.TaskIds[0], new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed, VerificationStatus = "pass" });

        autopilot.CheckActiveRuns();

        AutopilotRunDto finalized = autopilot.GetRun(run.Id)!;
        Assert.Equal(AutopilotRunStatus.Completed, finalized.Status);
        Assert.NotNull(finalized.FinalReportJson);
        Assert.Contains("\"completedTasks\":1", finalized.FinalReportJson);
        Assert.Contains("\"failedTasks\":0", finalized.FinalReportJson);
    }

    /// <summary>The other half of "never fabricate success": a run with even one failed task
    /// must finalize as failed, not completed.</summary>
    [Fact]
    public async Task CheckActiveRuns_finalizes_with_status_failed_when_any_task_failed()
    {
        var (autopilot, tasks, _, client) = Build();
        client.EnqueueOk(@"[
            {""description"":""Task A"",""role"":""planner"",""dependsOn"":[]},
            {""description"":""Task B"",""role"":""planner"",""dependsOn"":[]}
        ]");
        AutopilotRunDto run = await autopilot.StartRunAsync("Mixed outcome", AutopilotPermissionMode.Balanced, CancellationToken.None);
        tasks.UpdateStatus(run.TaskIds[0], new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed });
        tasks.UpdateStatus(run.TaskIds[1], new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed, Error = "the model refused" });

        autopilot.CheckActiveRuns();

        AutopilotRunDto finalized = autopilot.GetRun(run.Id)!;
        Assert.Equal(AutopilotRunStatus.Failed, finalized.Status);
        Assert.Contains("\"failedTasks\":1", finalized.FinalReportJson);
        Assert.Contains("the model refused", finalized.FinalReportJson);
    }

    /// <summary>The real, previously-missing BALANCED vs AUTONOMOUS distinction: BALANCED never
    /// auto-retries a failed task - it stays failed and the run finalizes as failed, exactly like
    /// before this change.</summary>
    [Fact]
    public async Task Balanced_mode_never_auto_retries_a_failed_task()
    {
        var (autopilot, tasks, _, client) = Build();
        client.EnqueueOk(@"[{""description"":""Task A"",""role"":""planner"",""dependsOn"":[]}]");
        AutopilotRunDto run = await autopilot.StartRunAsync("One task", AutopilotPermissionMode.Balanced, CancellationToken.None);
        tasks.UpdateStatus(run.TaskIds[0], new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed, Error = "boom" });

        autopilot.CheckActiveRuns();

        Assert.Equal(AgentTaskStatus.Failed, tasks.Get(run.TaskIds[0])!.Status); // never requeued
        AutopilotRunDto finalized = autopilot.GetRun(run.Id)!;
        Assert.Equal(AutopilotRunStatus.Failed, finalized.Status);
        Assert.NotNull(finalized.FinalReportJson);
        Assert.Contains("\"retriesUsed\":0", finalized.FinalReportJson);
    }

    /// <summary>AUTONOMOUS mode's real, bounded auto-retry: a failed task is requeued (picked up
    /// by the real scheduler on its next tick, same mechanism ApproveRun already uses) up to
    /// MaxAutoRetries times, with the budget persisted after every retry; once exhausted, one more
    /// failure is left failed - never an unbounded loop.</summary>
    [Fact]
    public async Task Autonomous_mode_auto_retries_a_failed_task_up_to_the_bounded_budget_then_gives_up()
    {
        var (autopilot, tasks, _, client) = Build();
        client.EnqueueOk(@"[{""description"":""Task A"",""role"":""planner"",""dependsOn"":[]}]");
        AutopilotRunDto run = await autopilot.StartRunAsync("One task", AutopilotPermissionMode.Autonomous, CancellationToken.None);

        for (int attempt = 1; attempt <= AutopilotService.MaxAutoRetries; attempt++)
        {
            tasks.UpdateStatus(run.TaskIds[0], new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed, Error = "boom " + attempt });
            autopilot.CheckActiveRuns();

            Assert.Equal(AgentTaskStatus.Queued, tasks.Get(run.TaskIds[0])!.Status); // requeued, not left failed
            AutopilotRunDto stillRunning = autopilot.GetRun(run.Id)!;
            Assert.Equal(AutopilotRunStatus.Running, stillRunning.Status);
            Assert.Equal(attempt, stillRunning.RetryCounts[run.TaskIds[0]]); // budget persisted after every retry
        }

        // Budget exhausted - the next failure must be left failed, not retried again.
        tasks.UpdateStatus(run.TaskIds[0], new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed, Error = "final failure" });
        autopilot.CheckActiveRuns();

        Assert.Equal(AgentTaskStatus.Failed, tasks.Get(run.TaskIds[0])!.Status);
        AutopilotRunDto finalized = autopilot.GetRun(run.Id)!;
        Assert.Equal(AutopilotRunStatus.Failed, finalized.Status);
        Assert.NotNull(finalized.FinalReportJson);
        Assert.Contains($"\"retriesUsed\":{AutopilotService.MaxAutoRetries}", finalized.FinalReportJson);
    }
}
