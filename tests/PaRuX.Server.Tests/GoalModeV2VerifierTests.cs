using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Goal Mode V2 verifier tests use only temp-backed Task/Goal/Artifact stores. They prove the
/// completion gate is tied to the immutable task-bound goal version/hash and that malformed or
/// incomplete criterion verdicts fail closed instead of silently degrading to the legacy PASS gate.
/// </summary>
public sealed class GoalModeV2VerifierTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "parux-goal-v2-verifier-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static ExecutionProfileStatus Profile(int verificationPasses = 1) => new()
    {
        ActiveQualityProfile = verificationPasses >= 2
            ? ExecutionQualityProfile.MaximumQuality
            : ExecutionQualityProfile.Balanced,
        Effective = new ModelExecutionProfile
        {
            ModelKey = "goal-v2-test-model",
            ToolObservationCharacters = 12_000,
            Budget = new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 60,
                MaxToolSteps = 100,
                MaxDirectSubagents = 4,
                MaxDelegationDepth = 3,
                VerificationPasses = verificationPasses
            }
        }
    };

    private static string Verdict(params (string Id, bool Satisfied, string Evidence)[] criteria) =>
        JsonSerializer.Serialize(new
        {
            criteria = criteria.Select(item => new
            {
                criterionId = item.Id,
                satisfied = item.Satisfied,
                evidence = item.Evidence
            })
        });

    private static (AgentTaskDto Task, GoalSpecificationVersionDto Specification) CreateBoundVerifyingTask(
        TaskStore tasks,
        GoalModeV2Store goals,
        int maxRepairAttempts = 0,
        string result = "The implementation and tests are recorded in the task result.")
    {
        GoalSpecificationVersionDto specification = goals.Create(new CreateGoalSpecificationRequest
        {
            Goal = "Implement a durable feature and prove it works",
            AcceptanceCriteria = new List<string>
            {
                "The implementation is present in the recorded result evidence.",
                "The recorded result contains verification or test evidence."
            }
        });

        AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest
        {
            Goal = specification.Goal,
            Role = "planner",
            MaxRepairAttempts = maxRepairAttempts,
            DelegatedContext = GoalModeV2Store.BuildAcceptanceContext(specification)
        });
        goals.BindTask(task.Id, specification);
        tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying,
            ResultJson = JsonSerializer.Serialize(new { response = result, executionModelKey = "goal-v2-test-model" })
        });
        return (tasks.Get(task.Id)!, specification);
    }

    private VerifierService NewVerifier(
        TaskStore tasks,
        GoalModeV2Store goals,
        FakeLocalCompletionClient client,
        int verificationPasses = 1) =>
        new(tasks, client, new ArtifactStore(_tempDir), _ => Profile(verificationPasses), goals);

    [Fact]
    public async Task All_required_criteria_PASS_completes_and_persists_exact_bound_version_hash()
    {
        var tasks = new TaskStore(_tempDir);
        var goals = new GoalModeV2Store(_tempDir);
        (AgentTaskDto task, GoalSpecificationVersionDto version1) = CreateBoundVerifyingTask(tasks, goals);

        // Revise the goal after this task was bound. Verification must remain on v1 forever.
        GoalSpecificationVersionDto version2 = goals.CreateNext(version1.GoalId, new CreateGoalSpecificationVersionRequest
        {
            ExpectedPreviousVersion = version1.Version,
            Goal = "Implement a durable feature and prove it works with a new requirement",
            AcceptanceCriteria = new List<string>
            {
                "The implementation is present in the recorded result evidence.",
                "The recorded result contains verification or test evidence.",
                "A newly added criterion exists only in version two."
            },
            ChangeReason = "test later revision"
        });
        Assert.NotEqual(version1.ContentSha256, version2.ContentSha256);

        var client = new FakeLocalCompletionClient { HonorStructuredOutput = true };
        client.EnqueueOk(Verdict(
            ("AC-001", true, "The result explicitly records the implementation."),
            ("AC-002", true, "The result explicitly records verification/tests.")));
        VerifierService verifier = NewVerifier(tasks, goals, client);

        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto completed = tasks.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Completed, completed.Status);
        Assert.Equal("pass", completed.VerificationStatus);
        Assert.Contains("Goal Mode V2 verified", completed.Stage);

        GoalVerificationRecordDto record = Assert.Single(goals.ListVerificationRecords(task.Id));
        Assert.True(record.AllRequiredSatisfied);
        Assert.Equal(version1.GoalId, record.GoalId);
        Assert.Equal(version1.Version, record.GoalVersion);
        Assert.Equal(version1.ContentSha256, record.ContentSha256);
        Assert.Equal(2, record.Criteria.Count);
        Assert.DoesNotContain(record.Criteria, item => item.CriterionId == "AC-003");
    }

    [Fact]
    public async Task One_required_criterion_FAIL_prevents_completion_and_records_failure_evidence()
    {
        var tasks = new TaskStore(_tempDir);
        var goals = new GoalModeV2Store(_tempDir);
        (AgentTaskDto task, _) = CreateBoundVerifyingTask(tasks, goals, maxRepairAttempts: 0);
        var client = new FakeLocalCompletionClient { HonorStructuredOutput = true };
        client.EnqueueOk(Verdict(
            ("AC-001", true, "Implementation evidence is present."),
            ("AC-002", false, "No concrete test or verification output is present.")));
        VerifierService verifier = NewVerifier(tasks, goals, client);

        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto failed = tasks.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, failed.Status);
        Assert.Equal("fail", failed.VerificationStatus);
        Assert.Contains("AC-002", failed.Error);
        Assert.Contains("No concrete test", failed.Error);

        GoalVerificationRecordDto record = Assert.Single(goals.ListVerificationRecords(task.Id));
        Assert.False(record.AllRequiredSatisfied);
        Assert.False(record.Criteria.Single(item => item.CriterionId == "AC-002").Satisfied);
    }

    [Fact]
    public async Task Missing_criterion_blocks_as_inconclusive_and_does_not_persist_partial_record()
    {
        var tasks = new TaskStore(_tempDir);
        var goals = new GoalModeV2Store(_tempDir);
        (AgentTaskDto task, _) = CreateBoundVerifyingTask(tasks, goals);
        var client = new FakeLocalCompletionClient { HonorStructuredOutput = true };
        client.EnqueueOk(Verdict(("AC-001", true, "Implementation evidence is present.")));
        VerifierService verifier = NewVerifier(tasks, goals, client);

        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto blocked = tasks.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Blocked, blocked.Status);
        Assert.Equal("inconclusive", blocked.VerificationStatus);
        Assert.Contains("AC-002", blocked.Error);
        Assert.Empty(goals.ListVerificationRecords(task.Id));
    }

    [Fact]
    public async Task Duplicate_or_unknown_criterion_ids_fail_closed_before_recording()
    {
        foreach (string invalidVerdict in new[]
        {
            Verdict(
                ("AC-001", true, "first"),
                ("AC-001", true, "duplicate")),
            Verdict(
                ("AC-001", true, "first"),
                ("AC-999", true, "unknown"))
        })
        {
            string caseDir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
            var tasks = new TaskStore(caseDir);
            var goals = new GoalModeV2Store(caseDir);
            (AgentTaskDto task, _) = CreateBoundVerifyingTask(tasks, goals);
            var client = new FakeLocalCompletionClient { HonorStructuredOutput = true };
            client.EnqueueOk(invalidVerdict);
            var verifier = new VerifierService(
                tasks, client, new ArtifactStore(caseDir), _ => Profile(), goals);

            await verifier.ReviewAsync(task, CancellationToken.None);

            AgentTaskDto blocked = tasks.Get(task.Id)!;
            Assert.Equal(AgentTaskStatus.Blocked, blocked.Status);
            Assert.Equal("inconclusive", blocked.VerificationStatus);
            Assert.Empty(goals.ListVerificationRecords(task.Id));
        }
    }

    [Fact]
    public async Task Multiple_required_verification_passes_each_create_an_append_only_record()
    {
        var tasks = new TaskStore(_tempDir);
        var goals = new GoalModeV2Store(_tempDir);
        (AgentTaskDto task, GoalSpecificationVersionDto specification) = CreateBoundVerifyingTask(tasks, goals);
        var client = new FakeLocalCompletionClient { HonorStructuredOutput = true };
        client.EnqueueOk(Verdict(
            ("AC-001", true, "Pass one found implementation evidence."),
            ("AC-002", true, "Pass one found verification evidence.")));
        client.EnqueueOk(Verdict(
            ("AC-001", true, "Pass two independently found implementation evidence."),
            ("AC-002", true, "Pass two independently found verification evidence.")));
        VerifierService verifier = NewVerifier(tasks, goals, client, verificationPasses: 2);

        await verifier.ReviewAsync(task, CancellationToken.None);

        AgentTaskDto completed = tasks.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Completed, completed.Status);
        Assert.Contains("2 passes", completed.Stage);

        IReadOnlyList<GoalVerificationRecordDto> records = goals.ListVerificationRecords(task.Id);
        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { 1, 2 }, records.Select(record => record.VerificationPass).ToArray());
        Assert.All(records, record =>
        {
            Assert.True(record.AllRequiredSatisfied);
            Assert.Equal(specification.Version, record.GoalVersion);
            Assert.Equal(specification.ContentSha256, record.ContentSha256);
        });
        Assert.NotEqual(records[0].Id, records[1].Id);
    }
}
