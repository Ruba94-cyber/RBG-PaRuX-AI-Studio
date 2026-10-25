using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>BIC-09 S2b: pure policy for a coding parent that keeps working while read-only children run.</summary>
public sealed class CodingAgentContinuationTests
{
    [Theory]
    [InlineData("review")]
    [InlineData("planner")]
    [InlineData("research")]
    [InlineData("documentation")]
    [InlineData("REVIEW")]
    public void Read_only_roles_with_read_only_capabilities_are_accepted(string role)
    {
        Assert.True(CodingAgentContinuation.TryValidateRequest(
            new[] { (role, (IReadOnlyList<string>?)new[] { "repository.read", "context.read" }) }, out string error), error);
    }

    [Theory]
    [InlineData("coding")]
    [InlineData("testing")]
    [InlineData("security")]
    [InlineData("computer-use")]
    [InlineData("ui-verification")]
    [InlineData("binary-analysis")]
    [InlineData("")]
    [InlineData(null)]
    public void Roles_that_can_run_commands_or_write_are_refused(string? role)
    {
        Assert.False(CodingAgentContinuation.TryValidateRequest(
            new[] { (role, (IReadOnlyList<string>?)null) }, out string error));
        Assert.Contains("read-only role", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("web.research")]
    [InlineData("agent.delegate")]
    [InlineData("coding.restore.safe")]
    public void Capabilities_beyond_read_access_are_refused(string capability)
    {
        Assert.False(CodingAgentContinuation.TryValidateRequest(
            new[] { ("review", (IReadOnlyList<string>?)new[] { "repository.read", capability }) }, out string error));
        Assert.Contains(capability, error, StringComparison.Ordinal);
    }

    [Fact]
    public void One_unsafe_child_refuses_the_whole_request_and_empty_requests_are_refused()
    {
        Assert.False(CodingAgentContinuation.TryValidateRequest(
            new[] { ("review", (IReadOnlyList<string>?)null), ("coding", (IReadOnlyList<string>?)null) }, out _));
        Assert.False(CodingAgentContinuation.TryValidateRequest(
            Array.Empty<(string?, IReadOnlyList<string>?)>(), out _));
        Assert.False(CodingAgentContinuation.TryValidateRequest(null, out _));
    }

    [Fact]
    public void Continued_ids_are_bounded_deduplicated_and_sanitized_in_first_seen_order()
    {
        List<string> merged = CodingAgentContinuation.MergeContinuedIds(
            new[] { "a1", "b2" },
            new[] { "b2", " c3 ", "", "bad id", "../x", new string('z', 65), "d4", "e5", "f6", "g7" });

        Assert.Equal(new[] { "a1", "b2", "c3", "d4", "e5", "f6" }, merged);
    }

    [Fact]
    public void Done_completes_when_nothing_was_continued()
    {
        Assert.Equal(CodingContinuationDoneDecision.Complete, CodingAgentContinuation.DecideOnDone(
            Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, AgentTaskDto?>()));
    }

    [Fact]
    public void Done_pauses_while_any_child_is_live_and_treats_a_missing_record_as_live()
    {
        var states = new Dictionary<string, AgentTaskDto?>
        {
            ["a1"] = Task("a1", AgentTaskStatus.Completed),
            ["b2"] = Task("b2", AgentTaskStatus.Running)
        };
        Assert.Equal(CodingContinuationDoneDecision.PauseForChildren, CodingAgentContinuation.DecideOnDone(
            new[] { "a1", "b2" }, new[] { "a1", "b2" }, states));

        states["b2"] = null;
        Assert.Equal(CodingContinuationDoneDecision.PauseForChildren, CodingAgentContinuation.DecideOnDone(
            new[] { "a1", "b2" }, new[] { "a1" }, states));

        states.Remove("b2");
        Assert.Equal(CodingContinuationDoneDecision.PauseForChildren, CodingAgentContinuation.DecideOnDone(
            new[] { "a1", "b2" }, new[] { "a1" }, states));
    }

    [Fact]
    public void Done_is_held_once_for_finished_but_unseen_children_then_completes()
    {
        var states = new Dictionary<string, AgentTaskDto?>
        {
            ["a1"] = Task("a1", AgentTaskStatus.Completed),
            ["b2"] = Task("b2", AgentTaskStatus.Failed)
        };
        Assert.Equal(CodingContinuationDoneDecision.CollectFirst, CodingAgentContinuation.DecideOnDone(
            new[] { "a1", "b2" }, new[] { "a1" }, states));
        Assert.Equal(CodingContinuationDoneDecision.Complete, CodingAgentContinuation.DecideOnDone(
            new[] { "a1", "b2" }, new[] { "a1", "b2" }, states));
    }

    [Fact]
    public void Cancelled_children_count_as_terminal_but_still_have_to_be_shown()
    {
        var states = new Dictionary<string, AgentTaskDto?> { ["a1"] = Task("a1", AgentTaskStatus.Cancelled) };
        Assert.Equal(CodingContinuationDoneDecision.CollectFirst, CodingAgentContinuation.DecideOnDone(
            new[] { "a1" }, Array.Empty<string>(), states));
    }

    [Fact]
    public void Snapshot_shows_live_children_as_running_and_terminal_children_with_durable_evidence()
    {
        var states = new Dictionary<string, AgentTaskDto?>
        {
            ["a1"] = Task("a1", AgentTaskStatus.Completed, "review", "{\"response\":\"Tests live under tests/Foo and use xunit.\"}"),
            ["b2"] = Task("b2", AgentTaskStatus.Running, "planner", stage: "Worker executing"),
            ["c3"] = Task("c3", AgentTaskStatus.Failed, "research", error: "model unavailable"),
            ["d4"] = null
        };

        string text = CodingAgentContinuation.RenderSnapshot(new[] { "a1", "b2", "c3", "d4" }, states);

        Assert.Contains("Tests live under tests/Foo", text, StringComparison.Ordinal);
        Assert.Contains("child b2 [planner] STILL RUNNING", text, StringComparison.Ordinal);
        Assert.Contains("child c3 [research] FAILED: model unavailable", text, StringComparison.Ordinal);
        Assert.Contains("child d4 status unknown", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_is_bounded_and_never_exposes_unsafe_result_fields()
    {
        string huge = new('x', 50_000);
        var states = new Dictionary<string, AgentTaskDto?>
        {
            ["a1"] = Task("a1", AgentTaskStatus.Completed, "review",
                "{\"response\":\"" + huge + "\",\"worktreePath\":\"C:\\\\secret\\\\path\",\"token\":\"hunter2\"}")
        };

        string text = CodingAgentContinuation.RenderSnapshot(new[] { "a1" }, states);

        Assert.True(text.Length <= CodingAgentContinuation.MaxSnapshotCharacters + 64, text.Length.ToString());
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Checkpoint_round_trips_continued_children_and_drops_unknown_collected_ids()
    {
        string directory = Path.Combine(Path.GetTempPath(), "parux-continuation-checkpoint-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(directory, "ws");
        Directory.CreateDirectory(workspace);
        try
        {
            var checkpoint = new CodingAgentCheckpoint
            {
                SessionId = "task-1",
                WorkspaceRoot = workspace,
                Task = "goal",
                ContinuedChildTaskIds = new List<string> { "a1", "b2", "bad id" },
                CollectedChildTaskIds = new List<string> { "a1", "zz9" }
            };
            CodingAgentCheckpointStore.SaveToDirectory(directory, checkpoint);

            CodingAgentCheckpoint loaded = CodingAgentCheckpointStore.LoadFromDirectory(directory, "task-1", workspace, "goal")!;

            Assert.Equal(new[] { "a1", "b2" }, loaded.ContinuedChildTaskIds);
            Assert.Equal(new[] { "a1" }, loaded.CollectedChildTaskIds);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("OFF", false)]
    public void Feature_flags_default_on_and_only_explicit_off_values_disable(string? value, bool expected)
    {
        Assert.Equal(expected, CodingAgentFeatureFlags.IsEnabled(value));
    }

    private static AgentTaskDto Task(string id, string status, string role = "review", string? result = null,
        string? error = null, string? stage = null) => new()
    {
        Id = id,
        Goal = "child goal",
        Role = role,
        Status = status,
        Stage = stage,
        ResultJson = result,
        Error = error,
        VerificationStatus = "PASS",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };
}
