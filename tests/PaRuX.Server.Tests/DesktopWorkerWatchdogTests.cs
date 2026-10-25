using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Regression coverage for server-only/headless deployments: desktop-only tasks must not
/// remain Running forever when the Windows desktop worker is absent, but a real desktop claim or
/// an explicit Computer Use approval wait must never be raced or failed by the server watchdog.</summary>
public sealed class DesktopWorkerWatchdogTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-desktop-watchdog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private sealed class NeverCompletionClient : ILocalCompletionClient
    {
        public Task<CompletionResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Desktop-only watchdog tests must never invoke a model.");
    }

    private sealed class NeverRouter : IRoleRouter
    {
        public Task<RouteDecisionDto> ResolveAsync(RouteRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Desktop-only watchdog tests must never route a model.");
    }

    private AgentWorkerService CreateWorker(TaskStore store) => new(store, new NeverCompletionClient(), new NeverRouter());

    private static AgentTaskDto Dispatch(TaskStore store, string role)
    {
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "desktop-only task", Role = role });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Dispatched to " + role + " role",
            ProgressPercent = 0
        });
        return store.Get(task.Id)!;
    }

    [Theory]
    [InlineData("coding")]
    [InlineData("binary-analysis")]
    [InlineData("ui-verification")]
    [InlineData("computer-use")]
    public void Unclaimed_desktop_task_fails_after_the_bounded_claim_window(string role)
    {
        var store = new TaskStore(_tempDir);
        var worker = CreateWorker(store);
        AgentTaskDto dispatched = Dispatch(store, role);

        List<string> failed = worker.FailUnclaimedDesktopTasks(
            dispatched.UpdatedUtc + AgentWorkerService.DesktopClaimTimeout + TimeSpan.FromSeconds(1));

        Assert.Contains(dispatched.Id, failed);
        AgentTaskDto terminal = store.Get(dispatched.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, terminal.Status);
        Assert.Equal("Worker failed", terminal.Stage);
        Assert.Contains("No desktop worker claimed", terminal.Error);
        Assert.Contains(role, terminal.Error);
    }

    [Fact]
    public void Freshly_dispatched_desktop_task_is_not_failed_before_the_claim_window()
    {
        var store = new TaskStore(_tempDir);
        var worker = CreateWorker(store);
        AgentTaskDto dispatched = Dispatch(store, "coding");

        List<string> failed = worker.FailUnclaimedDesktopTasks(
            dispatched.UpdatedUtc + AgentWorkerService.DesktopClaimTimeout - TimeSpan.FromMilliseconds(1));

        Assert.Empty(failed);
        Assert.Equal(AgentTaskStatus.Running, store.Get(dispatched.Id)!.Status);
    }

    [Fact]
    public void Binary_analysis_claimed_by_the_desktop_worker_is_never_failed_by_watchdog()
    {
        var store = new TaskStore(_tempDir);
        var worker = CreateWorker(store);
        AgentTaskDto dispatched = Dispatch(store, "binary-analysis");

        store.UpdateStatus(dispatched.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Inspecting binary artifacts",
            ProgressPercent = 10
        });
        AgentTaskDto claimed = store.Get(dispatched.Id)!;

        List<string> failed = worker.FailUnclaimedDesktopTasks(claimed.UpdatedUtc + TimeSpan.FromHours(1));

        Assert.Empty(failed);
        Assert.Equal(AgentTaskStatus.Running, store.Get(dispatched.Id)!.Status);
    }

    [Fact]
    public void Computer_use_waiting_for_explicit_approval_is_never_failed_by_claim_watchdog()
    {
        var store = new TaskStore(_tempDir);
        var worker = CreateWorker(store);
        AgentTaskDto dispatched = Dispatch(store, "computer-use");

        store.UpdateStatus(dispatched.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Waiting,
            Stage = "Computer Use approval required",
            ProgressPercent = 1
        });
        AgentTaskDto waiting = store.Get(dispatched.Id)!;

        List<string> failed = worker.FailUnclaimedDesktopTasks(waiting.UpdatedUtc + TimeSpan.FromDays(1));

        Assert.Empty(failed);
        AgentTaskDto stillWaiting = store.Get(dispatched.Id)!;
        Assert.Equal(AgentTaskStatus.Waiting, stillWaiting.Status);
        Assert.Equal("Computer Use approval required", stillWaiting.Stage);
    }

    [Fact]
    public void Remotely_approved_computer_use_task_is_not_mistaken_for_unclaimed_dispatch()
    {
        var store = new TaskStore(_tempDir);
        var worker = CreateWorker(store);
        AgentTaskDto dispatched = Dispatch(store, "computer-use");

        store.UpdateStatus(dispatched.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Computer Use approval granted remotely",
            ProgressPercent = 1
        });
        AgentTaskDto approved = store.Get(dispatched.Id)!;

        List<string> failed = worker.FailUnclaimedDesktopTasks(approved.UpdatedUtc + TimeSpan.FromHours(1));

        Assert.Empty(failed);
        AgentTaskDto stillRunning = store.Get(dispatched.Id)!;
        Assert.Equal(AgentTaskStatus.Running, stillRunning.Status);
        Assert.Equal("Computer Use approval granted remotely", stillRunning.Stage);
    }

    [Fact]
    public void Desktop_claim_signalled_by_progress_or_stage_change_is_never_failed_by_watchdog()
    {
        var store = new TaskStore(_tempDir);
        var worker = CreateWorker(store);
        AgentTaskDto dispatched = Dispatch(store, "computer-use");

        store.UpdateStatus(dispatched.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Desktop Computer Use worker claimed task",
            ProgressPercent = 1
        });
        AgentTaskDto claimed = store.Get(dispatched.Id)!;

        List<string> failed = worker.FailUnclaimedDesktopTasks(
            claimed.UpdatedUtc + TimeSpan.FromHours(1));

        Assert.Empty(failed);
        AgentTaskDto stillRunning = store.Get(dispatched.Id)!;
        Assert.Equal(AgentTaskStatus.Running, stillRunning.Status);
        Assert.Equal("Desktop Computer Use worker claimed task", stillRunning.Stage);
    }

    [Fact]
    public void Non_desktop_running_roles_are_outside_the_watchdog_contract()
    {
        var store = new TaskStore(_tempDir);
        var worker = CreateWorker(store);
        AgentTaskDto dispatched = Dispatch(store, "planner");

        List<string> failed = worker.FailUnclaimedDesktopTasks(dispatched.UpdatedUtc + TimeSpan.FromDays(1));

        Assert.Empty(failed);
        Assert.Equal(AgentTaskStatus.Running, store.Get(dispatched.Id)!.Status);
    }
}
