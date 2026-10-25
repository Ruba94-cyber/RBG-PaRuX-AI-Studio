using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Product honesty: a coding task is "verified" only by executed checks with exit codes. A model's
/// PASS can never override a failing command and can never stand in for having run nothing.
/// </summary>
public sealed class CodingEvidenceGateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-evidence-gate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static ExecutionProfileStatus Profile() => new()
    {
        ActiveQualityProfile = ExecutionQualityProfile.Balanced,
        Effective = new ModelExecutionProfile
        {
            ModelKey = "test-model",
            ToolObservationCharacters = 12_000,
            Budget = new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 60, MaxToolSteps = 1_000, MaxDirectSubagents = 4, MaxDelegationDepth = 3, VerificationPasses = 1
            }
        }
    };

    private AgentTaskDto Verifying(TaskStore store, string role, string resultJson, int maxRepair = 0, bool verifiedByCommand = false)
    {
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Fix the discount boundary", Role = role, MaxRepairAttempts = maxRepair });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying,
            ResultJson = CodingExecutedChecks.AppliesTo(role) ? WithValidReceipt(resultJson, task.Id, verifiedByCommand) : resultJson
        });
        return store.Get(task.Id)!;
    }

    // The V2 receipt gate runs before the executed-check gate, so each coding fixture needs a
    // well-formed receipt bound to its own task. verifiedByCommand must agree with the recorded
    // checks: callers pass true only when every recorded check exits 0.
    private static string WithValidReceipt(string resultJson, string taskId, bool verifiedByCommand)
    {
        JsonObject root = JsonNode.Parse(resultJson)!.AsObject();
        root["verificationPending"] = false;
        root["browserPostconditionPending"] = false;
        root["verifiedByCommand"] = verifiedByCommand;
        root["goalReported"] = true;
        root["checkpointSessionId"] = taskId;
        root["verifiedWorkspaceFingerprint"] = new string('a', 64);
        return root.ToJsonString();
    }

    private static string Result(params (string Command, int Exit)[] checks) =>
        "{\"summary\":\"done\",\"executedChecks\":[" + string.Join(",",
            checks.Select(c => "{\"command\":\"" + c.Command + "\",\"exitCode\":" + c.Exit + ",\"scope\":\".\"}")) + "]}";

    [Fact]
    public async Task Model_PASS_with_a_failing_executed_check_is_a_failure()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - the diff looks right.");
        var verifier = new VerifierService(store, client, new ArtifactStore(_tempDir), _ => Profile());

        AgentTaskDto task = Verifying(store, "coding", Result(("python verify.py", 1)));
        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, fetched.Status);
        Assert.Equal("fail", fetched.VerificationStatus);
        Assert.Contains("exited with code 1", fetched.Error, StringComparison.Ordinal);
        Assert.Contains("\"verificationBasis\":\"executed-checks-failed\"", fetched.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_non_zero_check_fails_even_when_another_passed()
    {
        var store = new TaskStore(_tempDir);
        var verifier = new VerifierService(store, new FakeLocalCompletionClient(), new ArtifactStore(_tempDir), _ => Profile());

        AgentTaskDto task = Verifying(store, "coding", Result(("dotnet build", 0), ("dotnet test", 1)));
        await verifier.ReviewAsync(task, CancellationToken.None);

        Assert.Equal("fail", store.Get(task.Id)!.VerificationStatus);
    }

    [Fact]
    public async Task A_failing_check_starts_a_bounded_repair_when_attempts_remain()
    {
        var store = new TaskStore(_tempDir);
        var verifier = new VerifierService(store, new FakeLocalCompletionClient(), new ArtifactStore(_tempDir), _ => Profile());

        AgentTaskDto task = Verifying(store, "coding", Result(("python verify.py", 2)), maxRepair: 1);
        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Running, fetched.Status);
        Assert.Equal(1, fetched.RepairAttemptsUsed);
    }

    [Fact]
    public async Task No_executed_checks_means_completed_but_unverified_with_the_model_opinion_kept_as_advisory()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - looks correct to me.");
        var verifier = new VerifierService(store, client, new ArtifactStore(_tempDir), _ => Profile());

        AgentTaskDto task = Verifying(store, "coding", "{\"summary\":\"edited inventory.py\"}");
        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Completed, fetched.Status);
        Assert.Equal("unverified", fetched.VerificationStatus);
        Assert.Equal("Completed (unverified: no executed checks)", fetched.Stage);
        Assert.Contains("llmAdvisory", fetched.ResultJson, StringComparison.Ordinal);
        Assert.Contains("\"verificationBasis\":\"none\"", fetched.ResultJson, StringComparison.Ordinal);
        Assert.Contains("looks correct to me", fetched.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_or_missing_check_evidence_is_no_evidence()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - fine.");
        client.EnqueueOk("PASS - fine.");
        var verifier = new VerifierService(store, client, new ArtifactStore(_tempDir), _ => Profile());

        AgentTaskDto first = Verifying(store, "coding", "{\"executedChecks\":[{\"command\":\"\",\"exitCode\":0},{\"exitCode\":0},{\"command\":\"x\",\"exitCode\":\"0\"}]}");
        await verifier.ReviewAsync(first, CancellationToken.None);
        // Non-JSON results never reach this gate: the receipt gate rejects them first (see
        // CodingResultVerifierGateTests). Here the check list itself is malformed, not an array.
        AgentTaskDto second = Verifying(store, "coding", "{\"executedChecks\":\"python verify.py\"}");
        await verifier.ReviewAsync(second, CancellationToken.None);

        Assert.Equal("unverified", store.Get(first.Id)!.VerificationStatus);
        Assert.Equal("unverified", store.Get(second.Id)!.VerificationStatus);
    }

    [Fact]
    public async Task Executed_checks_that_all_exit_zero_are_verified_and_say_so()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - consistent with the checks.");
        var verifier = new VerifierService(store, client, new ArtifactStore(_tempDir), _ => Profile());

        AgentTaskDto task = Verifying(store, "coding", Result(("python verify.py", 0), ("node --check app.mjs", 0)), verifiedByCommand: true);
        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Completed, fetched.Status);
        Assert.Equal("pass", fetched.VerificationStatus);
        Assert.Contains("2 executed check(s)", fetched.Stage, StringComparison.Ordinal);
        Assert.Contains("\"verificationBasis\":\"executed-checks\"", fetched.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Executed_checks_that_all_exit_zero_without_an_accepted_command_are_unverified_not_pass()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - consistent with the checks.");
        var verifier = new VerifierService(store, client, new ArtifactStore(_tempDir), _ => Profile());

        // verifiedByCommand defaults to false: no accepted command, so no checkpoint/fingerprint binding.
        AgentTaskDto task = Verifying(store, "coding", Result(("python verify.py", 0), ("node --check app.mjs", 0)));
        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto fetched = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Completed, fetched.Status);
        Assert.Equal("unverified", fetched.VerificationStatus);
        Assert.Equal("Completed (unverified: checks exited 0 but no accepted command)", fetched.Stage);
        Assert.Contains("\"verificationBasis\":\"none\"", fetched.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_FAIL_still_fails_a_task_whose_checks_passed()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("FAIL - the change does not address the goal.");
        var verifier = new VerifierService(store, client, new ArtifactStore(_tempDir), _ => Profile());

        AgentTaskDto task = Verifying(store, "coding", Result(("python verify.py", 0)), verifiedByCommand: true);
        await verifier.ReviewAsync(task, CancellationToken.None);

        Assert.Equal("fail", store.Get(task.Id)!.VerificationStatus);
    }

    [Fact]
    public async Task Non_coding_roles_keep_the_existing_model_verification()
    {
        var store = new TaskStore(_tempDir);
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("PASS - the plan addresses the goal.");
        var verifier = new VerifierService(store, client, new ArtifactStore(_tempDir), _ => Profile());

        AgentTaskDto task = Verifying(store, "planner", "{\"response\":\"Step 1\"}");
        await verifier.ReviewAsync(task, CancellationToken.None);

        Assert.Equal("pass", store.Get(task.Id)!.VerificationStatus);
    }

    [Fact]
    public void Gate_parsing_requires_a_command_an_integer_exit_code_and_defaults_the_scope()
    {
        CodingCheckGate gate = CodingExecutedChecks.Evaluate(
            "{\"executedChecks\":[{\"command\":\"npm test\",\"exitCode\":0}]}");
        Assert.Equal(CodingCheckVerdict.Pass, gate.Verdict);
        Assert.Equal(".", Assert.Single(gate.Checks).Scope);
        Assert.Equal(CodingCheckVerdict.Unverified, CodingExecutedChecks.Evaluate(null).Verdict);
        Assert.Equal(CodingCheckVerdict.Unverified, CodingExecutedChecks.Evaluate("{\"executedChecks\":[]}").Verdict);
    }
}
