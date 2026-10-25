using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingModelSelectionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-coding-selection-" + Guid.NewGuid().ToString("N"));
    private readonly TaskStore _store;
    private string _hardware = "hw-a";

    public CodingModelSelectionServiceTests()
    {
        Directory.CreateDirectory(_root);
        _store = new TaskStore(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private CodingModelSelectionService CreateService() => new(
        _store,
        _ => Task.FromResult(new List<CandidateEvidenceDto>
        {
            new() { FileName = "small.gguf", FilePath = @"C:\m\small.gguf", IsCurrentAssignment = true, HardwareCompatible = true },
            new() { FileName = "large.gguf", FilePath = @"C:\m\large.gguf", IsCurrentAssignment = false, HardwareCompatible = true }
        }),
        () => _hardware,
        _root);

    private AgentTaskDto RunningCodingTask(string role = "coding")
    {
        AgentTaskDto task = _store.Create(new CreateAgentTaskRequest { Goal = "Fix the bug", Role = role });
        _store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        return task;
    }

    private void Finish(AgentTaskDto task, bool verified, double seconds = 120)
    {
        _store.UpdateStatus(task.Id, verified
            ? new UpdateAgentTaskStatusRequest
            {
                Status = AgentTaskStatus.Completed, VerificationStatus = "pass",
                ResultJson = "{\"verifiedByCommand\":true,\"agentElapsedSeconds\":" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"
            }
            : new UpdateAgentTaskStatusRequest
            {
                Status = AgentTaskStatus.Failed, VerificationStatus = "fail",
                ResultJson = "{\"verifiedByCommand\":false,\"verificationAvailable\":true}"
            });
    }

    [Fact]
    public void Outcomes_come_from_core_task_state_attributed_to_the_model_that_served_the_task()
    {
        CodingModelSelectionService service = CreateService();
        AgentTaskDto verified = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\large.gguf", "auto: test");
        Finish(verified, verified: true);
        AgentTaskDto failed = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\small.gguf", null);
        _store.AppendLog(failed.Id, "Step 4: invalid action protocol — missing field");
        Finish(failed, verified: false);

        List<CodingModelSelector.Outcome> outcomes = service.CollectOutcomes("hw-a");

        Assert.Equal(2, outcomes.Count);
        Assert.Contains(outcomes, o => o.FileName == "large.gguf" && o.Verified && !o.ProtocolTrouble && o.WallSeconds == 120);
        Assert.Contains(outcomes, o => o.FileName == "small.gguf" && !o.Verified && o.ProtocolTrouble);
        Assert.Contains(_store.GetLogs(verified.Id), l => l.Message.StartsWith("Coding model: large.gguf — auto: test", StringComparison.Ordinal));
    }

    [Fact]
    public void Cancelled_infrastructure_mixed_other_hardware_and_non_coding_tasks_are_not_model_evidence()
    {
        CodingModelSelectionService service = CreateService();
        AgentTaskDto cancelled = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\small.gguf", null);
        _store.UpdateStatus(cancelled.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled });

        AgentTaskDto infra = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\small.gguf", null);
        _store.UpdateStatus(infra.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed, Stage = "Coding sandbox unavailable" });

        AgentTaskDto mixed = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\small.gguf", null);
        service.RecordCodingResolution(@"C:\m\large.gguf", null);
        Finish(mixed, verified: true);


        AgentTaskDto verifierOnly = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\large.gguf", null);
        _store.UpdateStatus(verifierOnly.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Completed, VerificationStatus = "pass",
            ResultJson = "{\"verifiedByCommand\":false,\"agentElapsedSeconds\":30}"
        });

        AgentTaskDto verifierError = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\large.gguf", null);
        _store.UpdateStatus(verifierError.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed, VerificationStatus = "error", Stage = "Verification crashed"
        });

        AgentTaskDto concurrentA = RunningCodingTask();
        AgentTaskDto concurrentB = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\large.gguf", null);
        Finish(concurrentA, verified: true);
        Finish(concurrentB, verified: true);

        AgentTaskDto review = RunningCodingTask(role: "review");
        service.RecordCodingResolution(@"C:\m\large.gguf", null);
        Finish(review, verified: true);

        Assert.Empty(service.CollectOutcomes("hw-a"));

        // Another GPU (e.g. an external GPU attached later): a new Core instance fingerprints it
        // and its evidence stays separate from the first hardware's.
        _hardware = "hw-b";
        CodingModelSelectionService otherCore = CreateService();
        AgentTaskDto otherHardware = RunningCodingTask();
        otherCore.RecordCodingResolution(@"C:\m\large.gguf", null);
        Finish(otherHardware, verified: true);
        Assert.Single(otherCore.CollectOutcomes("hw-b"));
        Assert.Empty(otherCore.CollectOutcomes("hw-a"));
    }

    [Fact]
    public void Verifier_fail_without_deterministic_verification_and_repair_timing_are_not_trusted()
    {
        CodingModelSelectionService service = CreateService();
        AgentTaskDto untestable = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\small.gguf", null);
        _store.UpdateStatus(untestable.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed, VerificationStatus = "fail",
            ResultJson = "{\"verifiedByCommand\":false,\"verificationAvailable\":false}"
        });
        AgentTaskDto repaired = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\large.gguf", null);
        _store.UpdateStatus(repaired.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Completed, VerificationStatus = "pass",
            ResultJson = "{\"verifiedByCommand\":true,\"verificationAvailable\":true,\"repairAttempt\":1,\"agentElapsedSeconds\":15}"
        });

        CodingModelSelector.Outcome outcome = Assert.Single(service.CollectOutcomes("hw-a"));
        Assert.Equal("large.gguf", outcome.FileName);
        Assert.True(outcome.Verified);
        Assert.Equal(0, outcome.WallSeconds);
    }

    [Fact]
    public void Cancellation_is_neutral_even_after_model_steps()
    {
        CodingModelSelectionService service = CreateService();
        AgentTaskDto slow = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\large.gguf", null);
        _store.AppendLog(slow.Id, "Step 1: read_file \"src/A.cs\"");
        _store.UpdateStatus(slow.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled, Stage = "Coding agent stopped" });
        AgentTaskDto early = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\large.gguf", null);
        _store.UpdateStatus(early.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled });

        // Universal STOP, a change of mind or an external time limit say nothing reliable about
        // the model; loops end as "paused without verification", which does count.
        Assert.Empty(service.CollectOutcomes("hw-a"));
    }

    [Fact]
    public void Unverified_pause_counts_against_the_model_that_produced_it()
    {
        CodingModelSelectionService service = CreateService();
        AgentTaskDto task = RunningCodingTask();
        service.RecordCodingResolution(@"C:\m\small.gguf", null);
        _store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed, Stage = "Coding attempt paused without verification"
        });

        CodingModelSelector.Outcome outcome = Assert.Single(service.CollectOutcomes("hw-a"));
        Assert.False(outcome.Verified);
        Assert.Equal("small.gguf", outcome.FileName);
    }

    [Fact]
    public async Task Decision_uses_measured_outcomes_and_mode_is_persisted()
    {
        CodingModelSelectionService service = CreateService();
        for (int i = 0; i < 3; i++)
        {
            AgentTaskDto small = RunningCodingTask();
            service.RecordCodingResolution(@"C:\m\small.gguf", null);
            Finish(small, verified: i == 0);
            AgentTaskDto large = RunningCodingTask();
            service.RecordCodingResolution(@"C:\m\large.gguf", null);
            Finish(large, verified: true);
        }

        CodingModelSelectionDto auto = await service.GetDecisionAsync(CancellationToken.None, refresh: true);
        Assert.Equal(CodingModelSelectionMode.Auto, auto.EffectiveSource);
        Assert.Equal("large.gguf", auto.SelectedFileName);

        service.SetMode("assigned");
        CodingModelSelectionDto assigned = await CreateService().GetDecisionAsync(CancellationToken.None, refresh: true);
        Assert.Equal(CodingModelSelectionMode.Assigned, assigned.Mode);
        Assert.Equal("small.gguf", assigned.SelectedFileName);
        Assert.Throws<InvalidDataException>(() => service.SetMode("fastest"));
    }
}
