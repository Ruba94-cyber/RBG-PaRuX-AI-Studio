using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatRenderWindowPolicyTests
{
    [Fact]
    public void Long_conversation_initial_window_is_bounded_to_recent_messages()
    {
        Assert.Equal(120, ChatRenderWindowPolicy.InitialBudget(10_000));
        Assert.Equal(9_880, ChatRenderWindowPolicy.StartIndex(10_000, 120));
        Assert.Equal(9_880, ChatRenderWindowPolicy.HiddenCount(10_000, 120));
    }

    [Fact]
    public void Loading_earlier_history_grows_one_page_and_never_exceeds_total()
    {
        Assert.Equal(200, ChatRenderWindowPolicy.GrowBudget(10_000, 120));
        Assert.Equal(150, ChatRenderWindowPolicy.GrowBudget(150, 120));
    }

    [Theory]
    [InlineData(144, 120, false)]
    [InlineData(145, 120, true)]
    [InlineData(224, 200, false)]
    [InlineData(225, 200, true)]
    public void Live_compaction_uses_slack_instead_of_rebuilding_every_turn(
        int renderedMessages, int budget, bool expected)
    {
        Assert.Equal(expected, ChatRenderWindowPolicy.NeedsLiveCompaction(renderedMessages, budget));
    }

    [Fact]
    public void Small_and_empty_conversations_do_not_hide_history()
    {
        Assert.Equal(0, ChatRenderWindowPolicy.InitialBudget(0));
        Assert.Equal(0, ChatRenderWindowPolicy.StartIndex(0, 120));
        Assert.Equal(0, ChatRenderWindowPolicy.HiddenCount(0, 120));
        Assert.Equal(37, ChatRenderWindowPolicy.InitialBudget(37));
        Assert.Equal(0, ChatRenderWindowPolicy.StartIndex(37, 120));
    }
}
