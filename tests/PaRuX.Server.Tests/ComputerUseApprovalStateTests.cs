using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// An "Allow once" card authorizes exactly one execution. Before this change its click handlers never consumed
/// the approval, so the stored payload ran again on every click, and a computer-agent goal could be started
/// repeatedly from the same card. The first click now consumes the approval, and the consumed flag is persisted
/// on the message so a restart cannot replay it.
/// </summary>
public sealed class ComputerUseApprovalStateTests
{
    [Fact]
    public void The_first_click_on_a_fresh_card_executes_and_consumes_it()
    {
        var (next, mayExecute) = new ComputerUseApprovalState(Consumed: false).TryExecute("{\"action\":\"open\"}");

        Assert.True(mayExecute);
        Assert.True(next.Consumed);
    }

    [Fact]
    public void A_second_click_on_a_consumed_card_never_executes_again()
    {
        var (first, _) = new ComputerUseApprovalState(Consumed: false).TryExecute("open the report");
        var (second, mayExecute) = first.TryExecute("open the report");

        Assert.False(mayExecute);
        Assert.True(second.Consumed);
    }

    [Fact]
    public void A_card_consumed_before_a_restart_stays_consumed_after_it()
    {
        // The flag is persisted on the message. A state rebuilt from the persisted value must still refuse.
        var (_, mayExecute) = new ComputerUseApprovalState(Consumed: true).TryExecute("open the report");

        Assert.False(mayExecute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_card_without_a_payload_never_executes_and_is_not_consumed(string? payload)
    {
        var (next, mayExecute) = new ComputerUseApprovalState(Consumed: false).TryExecute(payload);

        Assert.False(mayExecute);
        Assert.False(next.Consumed);
    }

    [Fact]
    public void A_spent_card_is_labelled_as_used_so_it_does_not_look_clickable()
    {
        Assert.Contains("already used", ComputerUseApprovalState.UsedLabel);
    }
}
