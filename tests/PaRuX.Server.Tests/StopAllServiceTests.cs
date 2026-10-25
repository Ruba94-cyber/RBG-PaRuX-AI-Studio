using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Automation;
using PaRuX.Server.Autopilot;
using PaRuX.Server.Control;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;
using PaRuX.Server.ProjectManager;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>vNext Phase 54 - Universal STOP (directive section 139). The property that matters
/// most: every non-terminal task, every active Autopilot run, and future automation-created tasks
/// are all genuinely stopped by one call, while nothing already terminal is touched (no persistent
/// state corruption - only status transitions on what was actually in flight).</summary>
public sealed class StopAllServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-stopall-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private (StopAllService stopAll, TaskStore tasks, AutopilotStore runs, AutomationEngine automations, FakeLocalCompletionClient client) Build()
    {
        var tasks = new TaskStore(_tempDir);
        var runs = new AutopilotStore(_tempDir);
        var automationStore = new AutomationStore(_tempDir);
        var automations = new AutomationEngine(automationStore, tasks, new ModelDownloadService(),
            new ComponentInventoryService(new RuntimeManagerService(new LocalAiProcessManager())));
        var stopAll = new StopAllService(tasks, runs, automations);
        var client = new FakeLocalCompletionClient();
        return (stopAll, tasks, runs, automations, client);
    }

    [Fact]
    public void StopAll_cancels_every_non_terminal_task_and_leaves_terminal_ones_untouched()
    {
        var (stopAll, tasks, _, _, _) = Build();
        AgentTaskDto queued = tasks.Create(new CreateAgentTaskRequest { Goal = "Queued work" });
        AgentTaskDto running = tasks.Create(new CreateAgentTaskRequest { Goal = "Running work" });
        tasks.UpdateStatus(running.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        AgentTaskDto blocked = tasks.Create(new CreateAgentTaskRequest { Goal = "Blocked work" });
        tasks.UpdateStatus(blocked.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Blocked });
        AgentTaskDto alreadyDone = tasks.Create(new CreateAgentTaskRequest { Goal = "Already done" });
        tasks.UpdateStatus(alreadyDone.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed, VerificationStatus = "pass" });

        StopAllResult result = stopAll.StopAll();

        Assert.Equal(3, result.TasksCancelled); // queued + running + blocked, not the completed one
        Assert.Equal(AgentTaskStatus.Cancelled, tasks.Get(queued.Id)!.Status);
        Assert.Equal(AgentTaskStatus.Cancelled, tasks.Get(running.Id)!.Status);
        Assert.Equal(AgentTaskStatus.Cancelled, tasks.Get(blocked.Id)!.Status);
        Assert.Equal(AgentTaskStatus.Completed, tasks.Get(alreadyDone.Id)!.Status); // untouched
        Assert.Equal("pass", tasks.Get(alreadyDone.Id)!.VerificationStatus); // not corrupted
    }

    [Fact]
    public async Task StopAll_cancels_every_active_autopilot_run()
    {
        var (stopAll, tasks, runs, _, client) = Build();
        var graph = new ContextGraphStore(_tempDir);
        var pm = new ProjectManagerService(client, tasks, graph);
        var autopilot = new AutopilotService(runs, pm, tasks);

        client.EnqueueOk(@"[{""description"":""Do the thing"",""role"":""planner"",""dependsOn"":[]}]");
        AutopilotRunDto run = await autopilot.StartRunAsync("Ship it", AutopilotPermissionMode.Balanced, CancellationToken.None);
        Assert.Equal(AutopilotRunStatus.Running, run.Status);

        StopAllResult result = stopAll.StopAll();

        Assert.Equal(1, result.AutopilotRunsCancelled);
        Assert.Equal(AutopilotRunStatus.Cancelled, runs.Get(run.Id)!.Status);
    }

    [Fact]
    public void StopAll_pauses_automations_and_ResumeAutomations_unpauses_them()
    {
        var (stopAll, _, _, automations, _) = Build();
        Assert.False(automations.IsPaused);

        StopAllResult result = stopAll.StopAll();
        Assert.True(result.AutomationsPaused);
        Assert.True(automations.IsPaused);
        Assert.True(stopAll.GetStatus().AutomationsPaused);

        stopAll.ResumeAutomations();
        Assert.False(automations.IsPaused);
        Assert.False(stopAll.GetStatus().AutomationsPaused);
    }

    /// <summary>The other half of "does not corrupt persistent state": calling StopAll a second
    /// time when nothing new is in flight must be a safe no-op, not double-cancel or throw.</summary>
    [Fact]
    public void StopAll_called_twice_in_a_row_is_a_safe_no_op_the_second_time()
    {
        var (stopAll, tasks, _, _, _) = Build();
        tasks.Create(new CreateAgentTaskRequest { Goal = "Some work" });

        StopAllResult first = stopAll.StopAll();
        Assert.Equal(1, first.TasksCancelled);

        StopAllResult second = stopAll.StopAll();
        Assert.Equal(0, second.TasksCancelled);
    }
}
