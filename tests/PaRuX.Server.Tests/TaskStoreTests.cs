using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Covers the vNext Multi-Agent Orchestrator's persistent task store (Phase 3). Unlike
/// PersonaTests/EmbeddingServiceTests, TaskStore exposes a test-injectable database directory
/// (see BackupServiceTests for the same pattern on MemoryStore/ConversationStore), so the
/// meaningful CRUD/DAG/restart-recovery coverage below runs fully hermetically against a temp
/// directory instead of touching the real %LOCALAPPDATA%\PaRuX\server\orchestrator.db. API-level
/// tests only cover the validation/auth paths, which never persist anything.
/// </summary>
public sealed class TaskStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-taskstore-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // Same best-effort cleanup as BackupServiceTests - Microsoft.Data.Sqlite pools native
        // connections per connection string, so the file can stay briefly locked after Dispose().
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Cancelled_is_terminal_and_a_late_worker_status_cannot_revive_the_task()
    {
        // Live benchmark: a per-step "running" stage update after an API cancel revived the task,
        // the worker never stopped, and it resumed after an App restart and competed for the GPU.
        var store = new TaskStore(_tempDir);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Fix the drawer", Role = "coding", MaxRepairAttempts = 2 });
        Assert.True(store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running, Stage = "Coding step 3" }));

        Assert.True(store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled, Stage = "Cancelled" }));
        Assert.False(store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running, Stage = "Coding step 4" }));
        Assert.False(store.TryBeginRepair(task.Id, "verifier said FAIL"));
        // Re-cancelling stays idempotent.
        Assert.True(store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled, Stage = "Coding agent stopped" }));

        AgentTaskDto after = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Cancelled, after.Status);
        Assert.Equal("Coding agent stopped", after.Stage);
    }

    [Fact]
    public void Create_rejects_an_empty_goal()
    {
        var store = new TaskStore(_tempDir);
        Assert.Throws<InvalidDataException>(() => store.Create(new CreateAgentTaskRequest { Goal = "  " }));
    }

    [Fact]
    public void Create_rejects_an_unknown_dependency_id()
    {
        var store = new TaskStore(_tempDir);
        Assert.Throws<InvalidDataException>(() => store.Create(new CreateAgentTaskRequest
        {
            Goal = "Do the thing",
            DependsOnTaskIds = new List<string> { "does-not-exist" }
        }));
    }

    [Fact]
    public void Create_then_get_round_trips_the_task_with_queued_status()
    {
        var store = new TaskStore(_tempDir);
        AgentTaskDto created = store.Create(new CreateAgentTaskRequest { Goal = "Implement the settings screen", Role = "coding" });

        AgentTaskDto? fetched = store.Get(created.Id);
        Assert.NotNull(fetched);
        Assert.Equal("Implement the settings screen", fetched!.Goal);
        Assert.Equal("coding", fetched.Role);
        Assert.Equal(AgentTaskStatus.Queued, fetched.Status);
        Assert.Equal(0, fetched.ProgressPercent);
    }

    /// <summary>vNext Phase 61 (performance) - the whole point of ListByStatus is filtering at
    /// the SQL layer instead of loading every task ever created and discarding most of them in
    /// memory. Proven directly: mixed-status tasks in the store, only the requested statuses come
    /// back, with the same full DTO shape (including DependsOnTaskIds) List() already provided.</summary>
    [Fact]
    public void ListByStatus_returns_only_matching_tasks_with_the_full_dto_shape()
    {
        var store = new TaskStore(_tempDir);
        AgentTaskDto dependency = store.Create(new CreateAgentTaskRequest { Goal = "Dependency task" });
        store.UpdateStatus(dependency.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed });

        AgentTaskDto queued = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Queued task", DependsOnTaskIds = new List<string> { dependency.Id }
        });
        AgentTaskDto running = store.Create(new CreateAgentTaskRequest { Goal = "Running task" });
        store.UpdateStatus(running.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        AgentTaskDto failed = store.Create(new CreateAgentTaskRequest { Goal = "Failed task" });
        store.UpdateStatus(failed.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed });

        List<AgentTaskDto> result = store.ListByStatus(AgentTaskStatus.Queued, AgentTaskStatus.Running);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, t => t.Id == queued.Id);
        Assert.Contains(result, t => t.Id == running.Id);
        Assert.DoesNotContain(result, t => t.Id == failed.Id);
        Assert.DoesNotContain(result, t => t.Id == dependency.Id);
        Assert.Contains(dependency.Id, result.Single(t => t.Id == queued.Id).DependsOnTaskIds);
    }

    [Fact]
    public void ListByStatus_with_no_statuses_returns_an_empty_list_without_querying()
    {
        var store = new TaskStore(_tempDir);
        store.Create(new CreateAgentTaskRequest { Goal = "Some task" });

        Assert.Empty(store.ListByStatus());
    }

    [Fact]
    public void A_task_with_an_incomplete_dependency_is_not_ready_to_run()
    {
        var store = new TaskStore(_tempDir);
        AgentTaskDto dependency = store.Create(new CreateAgentTaskRequest { Goal = "Backend change" });
        AgentTaskDto dependent = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Integration test",
            DependsOnTaskIds = new List<string> { dependency.Id }
        });

        Assert.False(store.IsReadyToRun(dependent.Id));

        store.UpdateStatus(dependency.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed });

        Assert.True(store.IsReadyToRun(dependent.Id));
    }

    [Fact]
    public void UpdateStatus_rejects_an_unknown_status_value()
    {
        var store = new TaskStore(_tempDir);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Whatever" });
        Assert.Throws<InvalidDataException>(() =>
            store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = "not-a-real-status" }));
    }

    [Fact]
    public void UpdateStatus_preserves_fields_not_included_in_the_request()
    {
        var store = new TaskStore(_tempDir);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Whatever" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running, Stage = "Building", ProgressPercent = 40 });

        // A later update that only touches status must not clobber the stage/progress already recorded.
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Verifying });

        AgentTaskDto? fetched = store.Get(task.Id);
        Assert.Equal(AgentTaskStatus.Verifying, fetched!.Status);
        Assert.Equal("Building", fetched.Stage);
        Assert.Equal(40, fetched.ProgressPercent);
    }

    [Fact]
    public void SetWorktreePath_updates_the_task_and_returns_false_for_an_unknown_id()
    {
        var store = new TaskStore(_tempDir);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Implement the settings screen", Role = "coding" });

        Assert.True(store.SetWorktreePath(task.Id, @"C:\repo\.parux-agents\abc"));
        Assert.Equal(@"C:\repo\.parux-agents\abc", store.Get(task.Id)!.WorktreePath);
        Assert.False(store.SetWorktreePath("parux-test-unknown-" + Guid.NewGuid().ToString("N"), @"C:\x"));
    }

    [Fact]
    public void Logs_are_appended_and_returned_in_chronological_order()
    {
        var store = new TaskStore(_tempDir);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Whatever" });
        store.AppendLog(task.Id, "started");
        store.AppendLog(task.Id, "halfway");
        store.AppendLog(task.Id, "done");

        List<AgentTaskLogEntryDto> logs = store.GetLogs(task.Id);
        Assert.Equal(new[] { "started", "halfway", "done" }, logs.Select(l => l.Message));
    }

    /// <summary>
    /// The real-world requirement this proves (vNext directive section 18/124/148): a task and
    /// its dependency graph created before a backend restart must still be there afterward. A new
    /// TaskStore instance pointed at the same directory is the faithful unit-level equivalent of
    /// "kill the process, start a new one" - the SQLite file on disk is the only thing carrying
    /// state across that boundary, exactly as it would across a real process restart.
    /// </summary>
    [Fact]
    public void A_task_and_its_dependency_graph_survive_a_simulated_process_restart()
    {
        string taskId;
        string dependencyId;
        {
            var storeBeforeRestart = new TaskStore(_tempDir);
            AgentTaskDto dependency = storeBeforeRestart.Create(new CreateAgentTaskRequest { Goal = "Backend change" });
            AgentTaskDto task = storeBeforeRestart.Create(new CreateAgentTaskRequest
            {
                Goal = "Ship the feature",
                DependsOnTaskIds = new List<string> { dependency.Id }
            });
            storeBeforeRestart.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
            {
                Status = AgentTaskStatus.Running, Stage = "Implementing", ProgressPercent = 55
            });
            storeBeforeRestart.AppendLog(task.Id, "agent picked up the task");
            taskId = task.Id;
            dependencyId = dependency.Id;
            // storeBeforeRestart intentionally goes out of scope here with no explicit flush call -
            // TaskStore has no in-memory cache to lose, every write above already hit disk.
        }

        var storeAfterRestart = new TaskStore(_tempDir);
        AgentTaskDto? recovered = storeAfterRestart.Get(taskId);
        Assert.NotNull(recovered);
        Assert.Equal("Ship the feature", recovered!.Goal);
        Assert.Equal(AgentTaskStatus.Running, recovered.Status);
        Assert.Equal("Implementing", recovered.Stage);
        Assert.Equal(55, recovered.ProgressPercent);
        Assert.Contains(dependencyId, recovered.DependsOnTaskIds);
        Assert.Single(storeAfterRestart.GetLogs(taskId));
        Assert.False(storeAfterRestart.IsReadyToRun(taskId));
    }

    [Fact]
    public async Task Create_task_endpoint_rejects_missing_token()
    {
        using var factory = new ApiSecurityTests.PaRuXFactory();
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/orchestrator/tasks", new CreateAgentTaskRequest { Goal = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_task_endpoint_rejects_an_empty_goal()
    {
        using var factory = new ApiSecurityTests.PaRuXFactory();
        using HttpClient client = factory.CreateClient();
        ServerState state = factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/orchestrator/tasks", new CreateAgentTaskRequest { Goal = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_task_endpoint_returns_404_for_an_unknown_id()
    {
        using var factory = new ApiSecurityTests.PaRuXFactory();
        using HttpClient client = factory.CreateClient();
        ServerState state = factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/orchestrator/tasks/parux-test-unknown-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Set_worktree_endpoint_returns_404_for_an_unknown_id()
    {
        using var factory = new ApiSecurityTests.PaRuXFactory();
        using HttpClient client = factory.CreateClient();
        ServerState state = factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/orchestrator/tasks/parux-test-unknown-" + Guid.NewGuid().ToString("N") + "/worktree",
            new SetAgentTaskWorktreeRequest { WorktreePath = @"C:\x" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void A_task_created_without_MaxRepairAttempts_defaults_to_zero_not_repair_eligible()
    {
        var store = new TaskStore(_tempDir);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Plan it" });
        Assert.Equal(0, task.MaxRepairAttempts);
        Assert.Equal(0, task.RepairAttemptsUsed);
        Assert.Null(task.RepairContext);
    }

    [Fact]
    public void TryBeginRepair_consumes_one_attempt_and_sends_the_task_back_to_running()
    {
        var store = new TaskStore(_tempDir);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Plan it", MaxRepairAttempts = 2 });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying, VerificationStatus = null, Error = null
        });

        bool started = store.TryBeginRepair(task.Id, "FAIL - missed the goal entirely.");

        Assert.True(started);
        AgentTaskDto? fetched = store.Get(task.Id);
        Assert.Equal(AgentTaskStatus.Running, fetched!.Status);
        Assert.Equal(1, fetched.RepairAttemptsUsed);
        Assert.Equal(2, fetched.MaxRepairAttempts);
        Assert.Equal("FAIL - missed the goal entirely.", fetched.RepairContext);
        Assert.Null(fetched.VerificationStatus);
        Assert.Null(fetched.Error);
    }

    [Fact]
    public void TryBeginRepair_refuses_once_the_bounded_budget_is_exhausted()
    {
        var store = new TaskStore(_tempDir);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Plan it", MaxRepairAttempts = 1 });

        Assert.True(store.TryBeginRepair(task.Id, "first failure"));
        Assert.False(store.TryBeginRepair(task.Id, "second failure"));

        AgentTaskDto? fetched = store.Get(task.Id);
        Assert.Equal(1, fetched!.RepairAttemptsUsed);
        Assert.Equal("first failure", fetched.RepairContext);
    }

    [Fact]
    public void TryBeginRepair_returns_false_for_a_task_created_with_no_repair_budget()
    {
        var store = new TaskStore(_tempDir);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Plan it" });
        Assert.False(store.TryBeginRepair(task.Id, "FAIL"));
    }

    [Fact]
    public void TryBeginRepair_returns_false_for_an_unknown_task_id()
    {
        var store = new TaskStore(_tempDir);
        Assert.False(store.TryBeginRepair("does-not-exist", "FAIL"));
    }
}
