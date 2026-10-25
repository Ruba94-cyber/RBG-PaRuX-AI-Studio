using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The text runtime's loopback port: the preferred port when it is free, the next free candidate when another program holds it, and an
/// honest refusal when every candidate is held. The listener check is injected here, so the order and the bound are fixed without
/// sockets. The real-socket launch behaviour is covered by TextRuntimePortLaunchTests.
/// </summary>
public sealed class TextRuntimePortSelectorTests
{
    [Fact]
    public void A_free_preferred_port_is_chosen_without_checking_any_other_candidate()
    {
        var checkedPorts = new List<int>();

        TextRuntimePortChoice choice = TextRuntimePortSelector.Choose(1234, port =>
        {
            checkedPorts.Add(port);
            return false;
        });

        Assert.True(choice.Found);
        Assert.Equal(1234, choice.Port);
        Assert.Empty(choice.Skipped);
        Assert.Equal(new[] { 1234 }, checkedPorts);
    }

    [Fact]
    public void A_port_held_by_another_program_is_skipped_and_the_next_free_candidate_is_chosen()
    {
        TextRuntimePortChoice choice = TextRuntimePortSelector.Choose(1234, port => port is 1234 or 1235);

        Assert.True(choice.Found);
        Assert.Equal(1236, choice.Port);
        Assert.Equal(new[] { 1234, 1235 }, choice.Skipped);
    }

    [Fact]
    public void When_every_candidate_is_held_the_choice_is_refused_and_the_message_names_the_checked_range()
    {
        TextRuntimePortChoice choice = TextRuntimePortSelector.Choose(1234, _ => true);

        Assert.False(choice.Found);
        Assert.Equal(new[] { 1234, 1235, 1236, 1237, 1238 }, choice.Skipped);
        string message = TextRuntimePortSelector.ExhaustedMessage(choice.Skipped);
        Assert.Contains("ports 1234-1238", message, StringComparison.Ordinal);
        Assert.Contains("does not stop programs it did not start", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_search_checks_exactly_the_candidate_count_and_no_more()
    {
        int checks = 0;

        TextRuntimePortChoice choice = TextRuntimePortSelector.Choose(1234, _ =>
        {
            checks++;
            return true;
        });

        Assert.False(choice.Found);
        Assert.Equal(TextRuntimePortSelector.CandidateCount, checks);
    }

    [Fact]
    public void The_search_never_goes_past_the_last_valid_port_number()
    {
        TextRuntimePortChoice choice = TextRuntimePortSelector.Choose(65534, port => port == 65534);

        Assert.True(choice.Found);
        Assert.Equal(65535, choice.Port);
        Assert.Equal(new[] { 65534 }, choice.Skipped);
    }

    [Fact]
    public void A_held_preferred_port_at_the_top_of_the_range_is_refused_with_a_single_port_message()
    {
        TextRuntimePortChoice choice = TextRuntimePortSelector.Choose(65535, _ => true);

        Assert.False(choice.Found);
        Assert.Equal(new[] { 65535 }, choice.Skipped);
        Assert.Contains("port 65535", TextRuntimePortSelector.ExhaustedMessage(choice.Skipped), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void An_invalid_preferred_port_is_rejected(int preferred)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextRuntimePortSelector.Choose(preferred, _ => false));
    }
}
