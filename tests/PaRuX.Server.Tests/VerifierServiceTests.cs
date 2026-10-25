using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>VerifierService PASS/FAIL/inconclusive/repair behavior against an isolated fake local
/// model and an injected Execution Profile. Tests never read or mutate the workstation's real
/// execution-profiles.json.</summary>
public sealed class VerifierServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-verifier-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static ExecutionProfileStatus Profile(int verificationPasses = 1) => new()
    {
        ActiveQualityProfile = verificationPasses >= 3
            ? ExecutionQualityProfile.Overnight
            : verificationPasses == 2
                ? ExecutionQualityProfile.MaximumQuality
                : ExecutionQualityProfile.Balanced,
        Effective = new ModelExecutionProfile
        {
            ModelKey = "test-model",
            ToolObservationCharacters = 12_000,
            Budget = new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 60,
                MaxToolSteps = 1_000,
                MaxDirectSubagents = 4,
                MaxDelegationDepth = 3,
                VerificationPasses = verificationPasses
            }
        }
    };

    private static VerifierService NewVerifier(
        TaskStore store,
        ILocalCompletionClient client,
        ArtifactStore artifacts,
        int verificationPasses = 1,
        VisualContextV2Store? visualSnapshots = null) =>
        new(store, client, artifacts, _ => Profile(verificationPasses),
            visualSnapshots: visualSnapshots);

    private static AgentTaskDto CreateVerifyingTask(
        TaskStore store,
        string goal = "Plan the settings feature",
        string result = "Step 1, step 2.",
        string? executionModelKey = null)
    {
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = goal, Role = "planner" });
        string modelJson = executionModelKey == null ? string.Empty : ",\"executionModelKey\":\"" + executionModelKey + "\"";
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying,
            ResultJson = "{\"response\":\"" + result + "\"" + modelJson + "}"
        });
        return store.Get(task.Id)!;
    }

    [Fact]
    public async Task A_PASS_verdict_completes_the_task_and_records_the_verification_status()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - the plan directly addresses the goal.");
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir));

        AgentTaskDto task = CreateVerifyingTask(store);
        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Completed, fetched.Status);
        Assert.Equal("pass", fetched.VerificationStatus);
        Assert.Equal(100, fetched.ProgressPercent);
    }

    [Fact]
    public async Task A_PASS_verdict_records_the_result_as_a_real_final_artifact()
    {
        var store = new TaskStore(_tempDir);
        var artifacts = new ArtifactStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - solid plan.");
        var verifier = NewVerifier(store, client, artifacts);

        AgentTaskDto task = CreateVerifyingTask(store, result: "Step 1, step 2.");
        await verifier.ReviewAsync(task, CancellationToken.None);

        ArtifactDto artifact = Assert.Single(artifacts.List("agent-task-result"));
        Assert.Equal("final", artifact.Status);
        Assert.Equal(task.Id, artifact.CreatedByTaskId);
        Assert.Equal("orchestrator:planner", artifact.Source);
        ArtifactVersionDto version = Assert.Single(artifacts.ListVersions(artifact.Id));
        Assert.Equal("Step 1, step 2.", version.ContentRef);
    }

    [Fact]
    public async Task An_unverified_coding_result_is_archived_without_a_verified_artifact_claim()
    {
        var store = new TaskStore(_tempDir);
        var artifacts = new ArtifactStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - the diff appears to address the goal.");
        var verifier = NewVerifier(store, client, artifacts);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Fix a coding issue", Role = "coding" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying,
            ResultJson = JsonSerializer.Serialize(new
            {
                summary = "changed source",
                executedChecks = Array.Empty<object>(),
                verificationPending = false,
                browserPostconditionPending = false,
                verifiedByCommand = false,
                goalReported = true,
                checkpointSessionId = task.Id,
                verifiedWorkspaceFingerprint = new string('a', 64)
            })
        });
        task = store.Get(task.Id)!;

        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto completed = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Completed, completed.Status);
        Assert.Equal("unverified", completed.VerificationStatus);
        List<AgentTaskLogEntryDto> logs = store.GetLogs(task.Id);
        Assert.Contains(logs, entry => entry.Message.StartsWith("Recorded task result as artifact ", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, entry => entry.Message.StartsWith("Recorded verified result as artifact ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_FAIL_or_unparseable_verdict_never_creates_an_artifact()
    {
        var store = new TaskStore(_tempDir);
        var artifacts = new ArtifactStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("FAIL - off topic.");
        client.EnqueueOk("Not sure, looks okay I guess.");
        var verifier = NewVerifier(store, client, artifacts);

        AgentTaskDto failedTask = CreateVerifyingTask(store, goal: "Task A");
        await verifier.ReviewAsync(failedTask, CancellationToken.None);
        AgentTaskDto unparseableTask = CreateVerifyingTask(store, goal: "Task B");
        await verifier.ReviewAsync(unparseableTask, CancellationToken.None);

        Assert.Empty(artifacts.List("agent-task-result"));
    }

    [Fact]
    public async Task A_FAIL_verdict_fails_the_task_with_the_reason_as_the_error()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("FAIL - the result does not mention the goal at all.");
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir));

        AgentTaskDto task = CreateVerifyingTask(store);
        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, fetched.Status);
        Assert.Equal("fail", fetched.VerificationStatus);
        Assert.Contains("does not mention the goal", fetched.Error);
    }

    [Fact]
    public async Task A_provider_error_fails_the_task_with_verification_status_error()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueFailure("Could not reach the local model provider: connection refused");
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir));

        AgentTaskDto task = CreateVerifyingTask(store);
        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, fetched.Status);
        Assert.Equal("error", fetched.VerificationStatus);
    }

    [Fact]
    public async Task A_client_that_throws_during_verification_still_fails_the_task_not_strands_it()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient
        {
            ThrowOnNextCall = new InvalidOperationException("simulated verifier crash")
        };
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir));

        AgentTaskDto task = CreateVerifyingTask(store);
        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, fetched.Status);
        Assert.Equal("error", fetched.VerificationStatus);
        Assert.Contains("simulated verifier crash", fetched.Error);
    }

    [Fact]
    public async Task An_unparseable_verdict_blocks_the_task_instead_of_defaulting_to_pass()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("I think this looks pretty good overall, nice work!");
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir));

        AgentTaskDto task = CreateVerifyingTask(store);
        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Blocked, fetched.Status);
        Assert.Equal("inconclusive", fetched.VerificationStatus);
    }

    [Fact]
    public async Task Two_required_verification_passes_must_both_PASS_before_completion()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - first independent review is satisfied.");
        client.EnqueueOk("PASS - second independent review is satisfied.");
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir), verificationPasses: 2);

        AgentTaskDto task = CreateVerifyingTask(store, executionModelKey: "model-from-worker");
        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Completed, fetched.Status);
        Assert.Equal("pass", fetched.VerificationStatus);
        Assert.Contains("2 passes", fetched.Stage);
    }

    [Fact]
    public async Task A_later_FAIL_in_multi_pass_verification_prevents_completion()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - first review passed.");
        client.EnqueueOk("FAIL - second review found missing evidence.");
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir), verificationPasses: 2);

        AgentTaskDto task = CreateVerifyingTask(store);
        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, fetched.Status);
        Assert.Equal("fail", fetched.VerificationStatus);
        Assert.Contains("pass 2/2", fetched.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing evidence", fetched.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_FAIL_verdict_on_a_repair_eligible_task_restarts_it_instead_of_failing()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("FAIL - the plan is missing concrete steps.");
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir));

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Plan it", Role = "planner", MaxRepairAttempts = 2 });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying, ResultJson = "{\"response\":\"vague plan\"}"
        });
        task = store.Get(task.Id)!;

        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Running, fetched.Status);
        Assert.Equal(1, fetched.RepairAttemptsUsed);
        Assert.Contains("missing concrete steps", fetched.RepairContext);
        Assert.Null(fetched.Error);
    }

    [Fact]
    public async Task A_FAIL_verdict_after_the_repair_budget_is_exhausted_fails_the_task_normally()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("FAIL - still missing steps.");
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir));

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Plan it", Role = "planner", MaxRepairAttempts = 1 });
        store.TryBeginRepair(task.Id, "first failure");
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying, ResultJson = "{\"response\":\"still vague\"}"
        });
        task = store.Get(task.Id)!;

        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, fetched.Status);
        Assert.Equal("fail", fetched.VerificationStatus);
        Assert.Equal(1, fetched.RepairAttemptsUsed);
    }

    [Fact]
    public async Task A_FAIL_verdict_on_coding_task_enters_repair_now_that_desktop_worker_reclaims_running_tasks()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("FAIL - the change does not compile.");
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir));

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Fix the bug", Role = "coding", MaxRepairAttempts = 3
        });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying, ResultJson = JsonSerializer.Serialize(new { response = "changed a file", verificationPending = false, browserPostconditionPending = false, verifiedByCommand = true, goalReported = true, checkpointSessionId = task.Id, verifiedWorkspaceFingerprint = new string('a', 64) })
        });
        task = store.Get(task.Id)!;

        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Running, fetched.Status);
        Assert.Equal(1, fetched.RepairAttemptsUsed);
        Assert.Contains("does not compile", fetched.RepairContext);
    }

    [Theory]
    [InlineData("{\"verificationPending\":true,\"verifiedByCommand\":false,\"goalReported\":true}", "require")]
    [InlineData("{\"verificationPending\":false,\"browserPostconditionPending\":false,\"verifiedByCommand\":true,\"goalReported\":true}", "checkpoint")]
    [InlineData("{\"response\":\"a plausible code diff\"}", "missing")]
    public async Task Server_never_asks_model_to_approve_a_coding_result_without_valid_test_metadata(
        string metadata, string expectedReason)
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient(); // no PASS is queued
        var artifacts = new ArtifactStore(_tempDir);
        var verifier = NewVerifier(store, client, artifacts);

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Fix the bug", Role = "coding", MaxRepairAttempts = 0
        });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying, ResultJson = metadata
        });

        await verifier.ReviewAsync(store.Get(task.Id)!, CancellationToken.None);

        AgentTaskDto after = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, after.Status);
        Assert.Equal("fail", after.VerificationStatus);
        Assert.Contains("Coding verification evidence rejected", after.Error);
        Assert.Contains(expectedReason, after.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(artifacts.List("agent-task-result"));
    }

    [Fact]
    public async Task Server_can_verify_a_coding_result_with_explicit_nonpending_state()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - reviewed source diff and command result.");
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir));

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Inspect and repair the coding component", Role = "coding"
        });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying,
            ResultJson = JsonSerializer.Serialize(new { executedChecks = new[] { new { command = "dotnet build", exitCode = 0, scope = "." } }, verificationPending = false, browserPostconditionPending = false, verifiedByCommand = true, goalReported = true, checkpointSessionId = task.Id, verifiedWorkspaceFingerprint = new string('a', 64) })
        });

        await verifier.ReviewAsync(store.Get(task.Id)!, CancellationToken.None);
        AgentTaskDto after = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Completed, after.Status);
        Assert.Equal("pass", after.VerificationStatus);
    }

    private static string UiResult(string snapshotId) =>
        JsonSerializer.Serialize(new
        {
            response = "A visible application control was observed.",
            evidenceMode = "screen-plus-bounded-predecessors",
            uiaStatus = "winapp_not_configured",
            foregroundWindowStable = true,
            uiTreeMatchedCapture = false,
            visualSnapshotId = snapshotId
        });

    [Fact]
    public async Task UI_verification_requires_an_existing_task_owned_snapshot_before_model_PASS()
    {
        var store = new TaskStore(_tempDir);
        var visual = new VisualContextV2Store(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - reviewed persisted screenshot evidence.");
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir),
            visualSnapshots: visual);

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Review a native application window", Role = "ui-verification",
            MaxRepairAttempts = 0
        });
        VisualSnapshotDto snapshot = visual.CreateSnapshot(new CreateVisualSnapshotRequest
        {
            Scope = "task", ScopeId = task.Id,
            Source = "ui-verification", FileName = "screen.png",
            MediaType = "image/png",
            Base64Data = Convert.ToBase64String(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })
        });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying,
            ResultJson = UiResult(snapshot.Id)
        });

        await verifier.ReviewAsync(store.Get(task.Id)!, CancellationToken.None);
        AgentTaskDto after = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Completed, after.Status);
        Assert.Equal("pass", after.VerificationStatus);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Forged_or_other_task_screenshot_cannot_authorize_UI_verification(
        bool otherTaskSnapshot)
    {
        var store = new TaskStore(_tempDir);
        var visual = new VisualContextV2Store(_tempDir);
        var client = new FakeLocalCompletionClient(); // no verdict queued
        var artifacts = new ArtifactStore(_tempDir);
        var verifier = NewVerifier(store, client, artifacts,
            visualSnapshots: visual);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Verify the UI state", Role = "ui-verification",
            MaxRepairAttempts = 0
        });

        string snapshotId = "1234567890abcdef1234567890abcdef";
        if (otherTaskSnapshot)
        {
            VisualSnapshotDto other = visual.CreateSnapshot(new CreateVisualSnapshotRequest
            {
                Scope = "task", ScopeId = "not-this-task",
                Source = "ui-verification", FileName = "screen.png",
                MediaType = "image/png",
                Base64Data = Convert.ToBase64String(
                    new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })
            });
            snapshotId = other.Id;
        }

        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying,
            ResultJson = UiResult(snapshotId)
        });

        await verifier.ReviewAsync(store.Get(task.Id)!, CancellationToken.None);
        AgentTaskDto after = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, after.Status);
        Assert.Equal("fail", after.VerificationStatus);
        Assert.Contains("UI verification snapshot rejected", after.Error);
        Assert.Empty(artifacts.List("agent-task-result"));
    }

    [Fact]
    public void TryExtractExecutionModelKey_reads_worker_metadata_without_guessing()
    {
        Assert.Equal("C:/models/model.gguf",
            VerifierService.TryExtractExecutionModelKey("{\"response\":\"x\",\"executionModelKey\":\"C:/models/model.gguf\"}"));
        Assert.Null(VerifierService.TryExtractExecutionModelKey("{\"response\":\"x\"}"));
        Assert.Null(VerifierService.TryExtractExecutionModelKey("not json"));
    }

    [Fact]
    public void RunVerifierPass_only_reviews_tasks_awaiting_verification()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - fine.");
        var verifier = NewVerifier(store, client, new ArtifactStore(_tempDir));

        AgentTaskDto verifying = CreateVerifyingTask(store);
        AgentTaskDto queued = store.Create(new CreateAgentTaskRequest { Goal = "Not ready yet" });

        List<string> started = verifier.RunVerifierPass(CancellationToken.None);

        Assert.Contains(verifying.Id, started);
        Assert.DoesNotContain(queued.Id, started);
    }
}
