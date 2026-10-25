using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GroupChatStatusCharacterizationTests
{
    [Fact]
    public void New_instance_defaults_to_queued_state_with_empty_collections()
    {
        var status = new GroupChatStatus();

        Assert.Equal(string.Empty, status.Id);
        Assert.Equal("queued", status.Status);
        Assert.Equal("Queued", status.Stage);
        Assert.Null(status.Error);
        Assert.Empty(status.Turns);
        Assert.Null(status.Verdict);
    }

    [Fact]
    public void Status_and_stage_are_independently_settable()
    {
        var status = new GroupChatStatus
        {
            Status = "running",
            Stage = "Loading model",
            Error = "boom"
        };

        Assert.Equal("running", status.Status);
        Assert.Equal("Loading model", status.Stage);
        Assert.Equal("boom", status.Error);
    }

    [Fact]
    public void Documented_status_wire_names_are_the_six_expected_values()
    {
        string[] expected = { "cancelled", "completed", "failed", "queued", "restoring", "running" };
        Assert.Equal(expected, new[] { "queued", "running", "restoring", "completed", "failed", "cancelled" }
            .OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected, expected);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("restoring")]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void Each_documented_status_round_trips_through_the_property(string status)
    {
        var s = new GroupChatStatus { Status = status };
        Assert.Equal(status, s.Status);
    }

    [Fact]
    public void Turns_grow_incrementally_and_preserve_insertion_order()
    {
        var status = new GroupChatStatus();
        for (int i = 1; i <= 4; i++)
            status.Turns.Add(new GroupChatTurn { Label = "m" + i, Response = "r" + i });

        Assert.Equal(4, status.Turns.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal("m" + (i + 1), status.Turns[i].Label);
            Assert.Equal("r" + (i + 1), status.Turns[i].Response);
        }
    }

    [Fact]
    public void GroupChatTurn_defaults_to_empty_strings()
    {
        var turn = new GroupChatTurn();

        Assert.Equal(string.Empty, turn.Label);
        Assert.Equal(string.Empty, turn.Response);
    }

    [Fact]
    public void Verdict_defaults_and_disagreements_are_distinct_collections()
    {
        var status = new GroupChatStatus
        {
            Verdict = new GroupChatVerdict
            {
                Answer = "yes",
                Disagreements = { "scale" },
                SupportedBy = { "m1" }
            }
        };

        Assert.NotNull(status.Verdict);
        Assert.Equal("yes", status.Verdict.Answer);
        Assert.Equal(new[] { "scale" }, status.Verdict.Disagreements);
        Assert.Equal(new[] { "m1" }, status.Verdict.SupportedBy);
        Assert.NotSame(status.Verdict.Disagreements, status.Verdict.SupportedBy);
    }

    [Fact]
    public void Verdict_can_stay_null_while_turns_exist()
    {
        var status = new GroupChatStatus
        {
            Status = "running",
            Turns = { new GroupChatTurn { Label = "m1", Response = "hi" } }
        };

        Assert.Null(status.Verdict);
        Assert.Single(status.Turns);
    }
}
