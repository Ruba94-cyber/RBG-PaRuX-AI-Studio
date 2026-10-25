using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A multi-model panel is only worth more than a single model if its combined verdict is honest.
/// Two failure modes would make it actively worse, and both are tested here: claiming support from
/// a participant that never said so, and presenting a confident single answer over a real split.
/// </summary>
public sealed class GroupChatVerdictTests
{
    private static IReadOnlyList<GroupChatTurn> Panel() => new[]
    {
        new GroupChatTurn { Label = "Qwen3.5-4B", Response = "Use a hash map." },
        new GroupChatTurn { Label = "Qwen3.8-27B", Response = "A hash map is right, but watch memory." }
    };

    [Fact]
    public void A_verdict_keeps_the_answer_disagreements_and_real_supporters()
    {
        GroupChatVerdict? verdict = GroupChatService.ParseVerdict(
            "{\"answer\":\"Use a hash map.\",\"disagreements\":[\"memory overhead\"],"
            + "\"supportedBy\":[\"Qwen3.5-4B\",\"Qwen3.8-27B\"]}", Panel());

        Assert.NotNull(verdict);
        Assert.Equal("Use a hash map.", verdict!.Answer);
        Assert.Equal(new[] { "memory overhead" }, verdict.Disagreements);
        Assert.Equal(2, verdict.SupportedBy.Count);
    }

    /// <summary>Support may only be claimed for participants that actually spoke - otherwise a
    /// verdict could borrow authority from a model that was never on the panel.</summary>
    [Fact]
    public void Support_from_a_participant_that_never_spoke_is_discarded()
    {
        GroupChatVerdict? verdict = GroupChatService.ParseVerdict(
            "{\"answer\":\"Use a hash map.\",\"disagreements\":[],"
            + "\"supportedBy\":[\"Qwen3.5-4B\",\"GPT-5\",\"SomeoneElse\"]}", Panel());

        Assert.NotNull(verdict);
        Assert.Equal(new[] { "Qwen3.5-4B" }, verdict!.SupportedBy);
    }

    [Fact]
    public void Genuine_disagreement_is_preserved_rather_than_smoothed_away()
    {
        GroupChatVerdict? verdict = GroupChatService.ParseVerdict(
            "{\"answer\":\"A hash map, with caveats.\","
            + "\"disagreements\":[\"whether memory cost is acceptable\",\"whether a B-tree is better\"],"
            + "\"supportedBy\":[\"Qwen3.8-27B\"]}", Panel());

        Assert.NotNull(verdict);
        Assert.Equal(2, verdict!.Disagreements.Count);
    }

    [Fact]
    public void A_verdict_wrapped_in_prose_or_a_code_fence_is_still_parsed()
    {
        GroupChatVerdict? verdict = GroupChatService.ParseVerdict(
            "Here is my reconciliation:\n```json\n{\"answer\":\"Use a hash map.\",\"disagreements\":[],\"supportedBy\":[]}\n```",
            Panel());

        Assert.NotNull(verdict);
        Assert.Equal("Use a hash map.", verdict!.Answer);
    }

    /// <summary>An unusable reconciliation must yield null so the caller leaves the individual
    /// turns as the result, rather than inventing a consensus that was never reached.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here at all")]
    [InlineData("{\"answer\":\"\"}")]
    [InlineData("{\"disagreements\":[\"x\"]}")]
    [InlineData("{ this is not valid json }")]
    public void An_unusable_reconciliation_produces_no_verdict(string raw)
    {
        Assert.Null(GroupChatService.ParseVerdict(raw, Panel()));
    }

    [Fact]
    public void Malformed_entries_inside_the_lists_are_skipped_not_fatal()
    {
        GroupChatVerdict? verdict = GroupChatService.ParseVerdict(
            "{\"answer\":\"ok\",\"disagreements\":[\"real\",\"\",null,42],\"supportedBy\":[\"Qwen3.5-4B\",null,7]}",
            Panel());

        Assert.NotNull(verdict);
        Assert.Equal(new[] { "real" }, verdict!.Disagreements);
        Assert.Equal(new[] { "Qwen3.5-4B" }, verdict.SupportedBy);
    }
}
