using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>vNext Phase 5 - AgentWorkerService's actual execution against a fake local model.</summary>
public sealed class AgentWorkerServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-worker-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("planner")]
    [InlineData("research")]
    [InlineData("review")]
    [InlineData("documentation")]
    [InlineData("testing")]
    [InlineData("security")]
    public void CanExecute_is_true_for_every_server_executable_role(string role) => Assert.True(AgentWorkerService.CanExecute(role));

    [Theory]
    [InlineData("coding")]
    [InlineData("binary-analysis")]
    [InlineData("ui-verification")]
    [InlineData("computer-use")]
    [InlineData(null)]
    public void CanExecute_is_false_for_roles_needing_desktop_side_tooling(string? role) => Assert.False(AgentWorkerService.CanExecute(role));

    [Theory]
    [InlineData("coding")]
    [InlineData("binary-analysis")]
    [InlineData("ui-verification")]
    [InlineData("computer-use")]
    public void Headless_watchdog_fails_an_unclaimed_desktop_task_instead_of_leaving_it_running(string role)
    {
        var store = new TaskStore(_tempDir);
        var worker = new AgentWorkerService(store, new FakeLocalCompletionClient(), new FakeRoleRouter());
        var scheduler = new AgentOrchestrator(store);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Do desktop-only work", Role = role });

        Assert.Contains(task.Id, scheduler.RunSchedulingPass());
        AgentTaskDto dispatched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Running, dispatched.Status);
        Assert.Equal("Dispatched to " + role + " role", dispatched.Stage);

        List<string> failed = worker.FailUnclaimedDesktopTasks(
            dispatched.UpdatedUtc + TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(30));

        Assert.Contains(task.Id, failed);
        AgentTaskDto terminal = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, terminal.Status);
        Assert.Equal("Worker failed", terminal.Stage);
        Assert.Contains("No desktop worker claimed", terminal.Error);
        Assert.Contains("instead of being left running indefinitely", terminal.Error);
    }

    [Fact]
    public void Headless_watchdog_does_not_race_a_desktop_worker_that_has_claimed_the_task()
    {
        var store = new TaskStore(_tempDir);
        var worker = new AgentWorkerService(store, new FakeLocalCompletionClient(), new FakeRoleRouter());
        var scheduler = new AgentOrchestrator(store);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Edit source code", Role = "coding" });
        scheduler.RunSchedulingPass();

        // The desktop worker's first observable claim changes the untouched dispatch state.
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Desktop worker editing files",
            ProgressPercent = 10
        });
        AgentTaskDto claimed = store.Get(task.Id)!;

        List<string> failed = worker.FailUnclaimedDesktopTasks(
            claimed.UpdatedUtc + TimeSpan.FromHours(1), TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(task.Id, failed);
        Assert.Equal(AgentTaskStatus.Running, store.Get(task.Id)!.Status);
    }

    [Fact]
    public void Headless_watchdog_never_fails_a_server_executable_task()
    {
        var store = new TaskStore(_tempDir);
        var worker = new AgentWorkerService(store, new FakeLocalCompletionClient(), new FakeRoleRouter());
        var scheduler = new AgentOrchestrator(store);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Plan the work", Role = "planner" });
        scheduler.RunSchedulingPass();
        AgentTaskDto dispatched = store.Get(task.Id)!;

        List<string> failed = worker.FailUnclaimedDesktopTasks(
            dispatched.UpdatedUtc + TimeSpan.FromDays(1), TimeSpan.Zero);

        Assert.DoesNotContain(task.Id, failed);
        Assert.Equal(AgentTaskStatus.Running, store.Get(task.Id)!.Status);
    }

    [Fact]
    public async Task A_successful_completion_moves_the_task_to_verifying_with_the_result_recorded()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("Step 1: do the thing. Step 2: verify it.");
        var worker = new AgentWorkerService(store, client, new FakeRoleRouter());

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Plan the settings feature", Role = "planner" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        await worker.ExecuteAsync(task, CancellationToken.None);

        AgentTaskDto? fetched = store.Get(task.Id);
        Assert.Equal(AgentTaskStatus.Verifying, fetched!.Status);
        Assert.Equal(90, fetched.ProgressPercent);
        Assert.Contains("do the thing", fetched.ResultJson);
        Assert.Single(client.Calls);
        Assert.Contains("Planner", client.Calls[0].SystemPrompt);
        // "\n", not Environment.NewLine: the prompt a worker sends is an input to the model and
        // must be byte-identical whichever OS assembled it.
        Assert.Equal("Task goal:\nPlan the settings feature", client.Calls[0].UserPrompt);
    }

    [Fact]
    public async Task A_failed_completion_marks_the_task_failed_with_the_error_recorded()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueFailure("Could not reach the local model provider: connection refused");
        var worker = new AgentWorkerService(store, client, new FakeRoleRouter());

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Research embedding models", Role = "research" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        await worker.ExecuteAsync(task, CancellationToken.None);

        AgentTaskDto? fetched = store.Get(task.Id);
        Assert.Equal(AgentTaskStatus.Failed, fetched!.Status);
        Assert.Contains("connection refused", fetched.Error);
    }

    /// <summary>vNext Phase 55 (failure injection, directive section 119: "worker crash") - an
    /// ILocalCompletionClient that throws instead of returning a graceful Failure result must not
    /// strand the task in "running" forever with no error recorded.</summary>
    [Fact]
    public async Task A_client_that_throws_instead_of_failing_gracefully_still_fails_the_task_not_strands_it()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.ThrowOnNextCall = new InvalidOperationException("simulated worker crash: null reference deep in a provider client");
        var worker = new AgentWorkerService(store, client, new FakeRoleRouter());

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Plan the settings feature", Role = "planner" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        await worker.ExecuteAsync(task, CancellationToken.None);

        AgentTaskDto? fetched = store.Get(task.Id);
        Assert.Equal(AgentTaskStatus.Failed, fetched!.Status);
        Assert.Contains("simulated worker crash", fetched.Error);
    }

    [Theory]
    [InlineData("coding")]
    [InlineData("binary-analysis")]
    [InlineData("ui-verification")]
    [InlineData("computer-use")]
    public async Task Executing_a_task_with_a_desktop_side_role_throws_instead_of_faking_a_result(string role)
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        var worker = new AgentWorkerService(store, client, new FakeRoleRouter());

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Perform desktop-side work", Role = role });
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ExecuteAsync(task, CancellationToken.None));
        Assert.Empty(client.Calls);
    }

    /// <summary>Bounded repair loop (directive section 21): a task retried after VerifierService's
    /// TryBeginRepair must actually see the verifier's feedback on the next attempt, not blindly
    /// repeat the exact same prompt that already failed once.</summary>
    [Fact]
    public async Task A_repair_attempt_includes_the_verifier_feedback_in_the_prompt()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("Step 1: cover the missing case. Step 2: verify it.");
        var worker = new AgentWorkerService(store, client, new FakeRoleRouter());

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Plan the settings feature", Role = "planner", MaxRepairAttempts = 1 });
        Assert.True(store.TryBeginRepair(task.Id, "FAIL - missed the dark-mode case."));
        task = store.Get(task.Id)!;

        await worker.ExecuteAsync(task, CancellationToken.None);

        Assert.Single(client.Calls);
        Assert.Contains("FAIL - missed the dark-mode case.", client.Calls[0].UserPrompt);
        Assert.Contains("Plan the settings feature", client.Calls[0].UserPrompt);
    }

    /// <summary>2026-08-27: the worker must route through the Smart Router before completing -
    /// this is what actually gets it using the best available local model per task, not whatever
    /// happens to already be loaded. Worker roles are a different taxonomy from Model Roles' text
    /// roles, so the worker role is passed as a TaskType hint (Smart mode), not a Role.</summary>
    [Fact]
    public async Task ExecuteAsync_routes_through_the_role_router_before_completing_with_the_role_as_a_task_type_hint()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("some research answer");
        var router = new FakeRoleRouter { ResolvedRole = "reasoning", Swapped = true };
        var worker = new AgentWorkerService(store, client, router);

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Research embedding models", Role = "research" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        await worker.ExecuteAsync(task, CancellationToken.None);

        Assert.Single(router.Calls);
        Assert.Equal("research", router.Calls[0].Request.TaskType);
        Assert.Null(router.Calls[0].Request.Role);
        AgentTaskDto? fetched = store.Get(task.Id);
        Assert.Equal(AgentTaskStatus.Verifying, fetched!.Status);
        List<AgentTaskLogEntryDto> logs = store.GetLogs(task.Id);
        Assert.Contains(logs, log => log.Message.Contains("reasoning") && log.Message.Contains("swapped"));
    }

    /// <summary>A routing failure (e.g. no GPU/runtime available at all) must fail the task
    /// honestly, never silently complete against whatever happens to already be loaded (or nothing
    /// at all) - the same "no fabricated success" discipline this worker already applies to a
    /// completion-client failure.</summary>
    [Fact]
    public async Task A_routing_failure_fails_the_task_and_never_calls_the_completion_client()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("should never be reached");
        var router = new FakeRoleRouter { Succeed = false, FailureMessage = "No GPU detected on this machine." };
        var worker = new AgentWorkerService(store, client, router);

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Plan the settings feature", Role = "planner" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        await worker.ExecuteAsync(task, CancellationToken.None);

        AgentTaskDto? fetched = store.Get(task.Id);
        Assert.Equal(AgentTaskStatus.Failed, fetched!.Status);
        Assert.Contains("No GPU detected on this machine.", fetched.Error);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Cancelling_task_stops_an_already_running_completion_promptly()
    {
        var store = new TaskStore(_tempDir);
        var client = new BlockingCompletionClient();
        var worker = new AgentWorkerService(store, client, new FakeRoleRouter());
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Research safely", Role = "research" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });

        Task executing = worker.ExecuteAsync(task, CancellationToken.None);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled }));

        await executing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(client.CancellationObserved.Task.IsCompleted);
        Assert.Equal(AgentTaskStatus.Cancelled, store.Get(task.Id)!.Status);
    }

    [Fact]
    public void Failure_cancellation_snapshot_does_not_cancel_a_manual_resume_registered_before_dispatch()
    {
        var store = new TaskStore(_tempDir);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Review safely", Role = "review" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        using TaskExecutionLease priorAttempt = store.TryClaimExecution(task.Id)!;
        TaskExecutionLease? resumedAttempt = null;

        store.BeforeExecutionCancellationDispatchForTests = () =>
        {
            Assert.True(store.TryManualResume(task.Id, AgentTaskStatus.Failed, "resumed after failure"));
            resumedAttempt = store.TryClaimExecution(task.Id);
        };

        Assert.True(store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed }));

        store.BeforeExecutionCancellationDispatchForTests = null;
        Assert.True(priorAttempt.StopToken.IsCancellationRequested);
        Assert.NotNull(resumedAttempt);
        Assert.False(resumedAttempt!.StopToken.IsCancellationRequested);
        Assert.Equal(AgentTaskStatus.Running, store.Get(task.Id)!.Status);

        Assert.True(store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled }));
        Assert.True(resumedAttempt.StopToken.IsCancellationRequested);
        resumedAttempt.Dispose();
    }

    [Fact]
    public async Task Cancelled_old_worker_cannot_fail_a_manual_resume_while_unwinding()
    {
        var store = new TaskStore(_tempDir);
        var client = new BlockingCompletionClient();
        var worker = new AgentWorkerService(store, client, new FakeRoleRouter());
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Review safely", Role = "review" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        Task priorAttempt = worker.ExecuteAsync(task, CancellationToken.None);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task? resumedAttempt = null;
        store.BeforeExecutionCancellationDispatchForTests = () =>
        {
            Assert.True(store.TryManualResume(task.Id, AgentTaskStatus.Failed, "resumed after failure"));
            resumedAttempt = worker.ExecuteAsync(store.Get(task.Id)!, CancellationToken.None);
        };

        Assert.True(store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed }));
        store.BeforeExecutionCancellationDispatchForTests = null;
        await client.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await priorAttempt.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(resumedAttempt);
        Assert.False(client.SecondCancellationObserved.Task.IsCompleted);
        Assert.Equal(AgentTaskStatus.Running, store.Get(task.Id)!.Status);

        Assert.True(store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled }));
        await resumedAttempt!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(client.SecondCancellationObserved.Task.IsCompleted);
        Assert.Equal(AgentTaskStatus.Cancelled, store.Get(task.Id)!.Status);
    }

    [Theory]
    [InlineData(false, "failure")]
    [InlineData(false, "success")]
    [InlineData(false, "exception")]
    [InlineData(true, "failure")]
    [InlineData(true, "success")]
    [InlineData(true, "exception")]
    public async Task Revoked_worker_outcomes_cannot_mutate_a_manually_resumed_attempt(bool dispatchBeforeOldCompletion, string outcome)
    {
        var store = new TaskStore(_tempDir);
        var client = new AttemptOutcomeCompletionClient();
        var worker = new AgentWorkerService(store, client, new FakeRoleRouter());
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Review safely", Role = "review" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        Task prior = worker.ExecuteAsync(task, CancellationToken.None);
        await client.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var callbackEntered = new ManualResetEventSlim();
        using var allowCancellationDispatch = new ManualResetEventSlim();
        Task? resumed = null;
        store.BeforeExecutionCancellationDispatchForTests = () =>
        {
            Assert.True(store.TryManualResume(task.Id, AgentTaskStatus.Failed, "resumed attempt B"));
            resumed = worker.ExecuteAsync(store.Get(task.Id)!, CancellationToken.None);
            callbackEntered.Set();
            Assert.True(allowCancellationDispatch.Wait(TimeSpan.FromSeconds(10)));
        };

        Task<bool> stop = Task.Run(() => store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed,
            Error = "attempt A revoked"
        }));
        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
        await client.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int logsBeforeOldUnwind = store.GetLogs(task.Id).Count;

        if (!dispatchBeforeOldCompletion)
        {
            client.ReleaseFirst(outcome);
            await prior.WaitAsync(TimeSpan.FromSeconds(5));
            allowCancellationDispatch.Set();
        }
        else
        {
            allowCancellationDispatch.Set();
            Assert.True(await stop.WaitAsync(TimeSpan.FromSeconds(5)));
            await client.FirstCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.ReleaseFirst(outcome);
            await prior.WaitAsync(TimeSpan.FromSeconds(5));
        }

        store.BeforeExecutionCancellationDispatchForTests = null;
        Assert.True(await stop.WaitAsync(TimeSpan.FromSeconds(5)));
        AgentTaskDto stillAttemptB = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Running, stillAttemptB.Status);
        Assert.Equal("resumed attempt B", stillAttemptB.Stage);
        Assert.Null(stillAttemptB.Error);
        Assert.Null(stillAttemptB.ResultJson);
        Assert.Equal(logsBeforeOldUnwind, store.GetLogs(task.Id).Count);

        Assert.True(store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled }));
        await client.SecondCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.ReleaseSecond("success");
        await resumed!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentTaskStatus.Cancelled, store.Get(task.Id)!.Status);
    }

    private sealed class BlockingCompletionClient : ILocalCompletionClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public async Task<CompletionResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);
            if (call == 1) Started.TrySetResult();
            else if (call == 2) SecondStarted.TrySetResult();
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                CancellationObserved.TrySetResult();
                if (call == 2) SecondCancellationObserved.TrySetResult();
            });
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CompletionResult.Ok("unexpected completion");
        }
    }

    private sealed class AttemptOutcomeCompletionClient : ILocalCompletionClient
    {
        private readonly TaskCompletionSource<CompletionResult> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CompletionResult> _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CompletionResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);
            cancellationToken.Register(() => (call == 1 ? FirstCancellationObserved : SecondCancellationObserved).TrySetResult());
            if (call == 1)
            {
                FirstStarted.TrySetResult();
                return await _first.Task.ConfigureAwait(false);
            }
            SecondStarted.TrySetResult();
            return await _second.Task.ConfigureAwait(false);
        }

        public void ReleaseFirst(string outcome) => Complete(_first, outcome);
        public void ReleaseSecond(string outcome) => Complete(_second, outcome);

        private static void Complete(TaskCompletionSource<CompletionResult> completion, string outcome)
        {
            if (outcome == "exception") completion.SetException(new InvalidOperationException("stale attempt failure"));
            else if (outcome == "failure") completion.SetResult(CompletionResult.Failure("stale attempt result"));
            else completion.SetResult(CompletionResult.Ok("stale successful result"));
        }
    }

    [Fact]
    public void RunWorkerPass_only_starts_running_tasks_with_a_server_executable_role()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("some plan");
        var worker = new AgentWorkerService(store, client, new FakeRoleRouter());

        AgentTaskDto plannerTask = store.Create(new CreateAgentTaskRequest { Goal = "Plan it", Role = "planner" });
        store.UpdateStatus(plannerTask.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        AgentTaskDto codingTask = store.Create(new CreateAgentTaskRequest { Goal = "Code it", Role = "coding" });
        store.UpdateStatus(codingTask.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        AgentTaskDto binaryTask = store.Create(new CreateAgentTaskRequest { Goal = "Analyze binary", Role = "binary-analysis" });
        store.UpdateStatus(binaryTask.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        AgentTaskDto queuedTask = store.Create(new CreateAgentTaskRequest { Goal = "Not ready yet", Role = "planner" });

        List<string> started = worker.RunWorkerPass(CancellationToken.None);

        Assert.Contains(plannerTask.Id, started);
        Assert.DoesNotContain(codingTask.Id, started);
        Assert.DoesNotContain(binaryTask.Id, started);
        Assert.DoesNotContain(queuedTask.Id, started);
    }
}
