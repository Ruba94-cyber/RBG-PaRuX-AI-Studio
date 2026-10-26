using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingContextRemedyPolicyTests
{
    private static ModelContextOption Option(long tokens, bool ram = false, double rope = 1.0, string cache = "q8_0") =>
        new() { ContextLength = tokens, UsesSystemRam = ram, RopeScale = rope, CacheType = cache };

    [Theory]
    [InlineData(0, 16_384)]
    [InlineData(8_192, 16_384)]
    [InlineData(16_384, 32_768)]
    public void Needed_tokens_double_the_window_with_a_useful_minimum(long current, long expected) =>
        Assert.Equal(expected, CodingContextRemedyPolicy.NeededTokens(current));

    [Fact]
    public void Cheapest_sufficient_vram_option_wins_over_ram_and_longer_windows()
    {
        var options = new[] { Option(8_192), Option(16_384, ram: true), Option(32_768), Option(16_384), Option(24_576) };

        ModelContextOption? chosen = CodingContextRemedyPolicy.ChooseOption(options, 8_192, 16_384);

        Assert.NotNull(chosen);
        Assert.Equal(16_384, chosen!.ContextLength);
        Assert.False(chosen.UsesSystemRam);
    }

    [Fact]
    public void System_ram_is_used_only_when_no_vram_option_reaches_the_target()
    {
        var options = new[] { Option(8_192), Option(16_384, ram: true) };

        ModelContextOption? chosen = CodingContextRemedyPolicy.ChooseOption(options, 8_192, 16_384);

        Assert.True(chosen!.UsesSystemRam);
    }

    [Fact]
    public void Options_beyond_the_trained_window_are_never_chosen()
    {
        var options = new[] { Option(8_192), Option(65_536, rope: 2.0) };

        Assert.Null(CodingContextRemedyPolicy.ChooseOption(options, 8_192, 16_384));
    }

    [Fact]
    public void No_option_larger_than_the_current_window_means_no_remedy()
    {
        Assert.Null(CodingContextRemedyPolicy.ChooseOption(new[] { Option(8_192) }, 8_192, 16_384));
        Assert.Null(CodingContextRemedyPolicy.ChooseOption(null, 8_192, 16_384));
    }

    [Theory]
    [InlineData(CodingAgentStopReason.ContextTooSmall, "context")]
    [InlineData(CodingAgentStopReason.RepeatedAction, "repeated")]
    [InlineData(CodingAgentStopReason.NoProgress, "progress")]
    public void Every_stop_reason_names_its_cause_and_a_next_step(string kind, string word)
    {
        Assert.Contains(word, CodingAgentStopReason.Stage(kind), StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(CodingAgentStopReason.NextStep(kind)));
    }

    [Fact]
    public void Unknown_reason_keeps_the_generic_label_without_invented_advice()
    {
        Assert.Equal("Coding attempt unfinished", CodingAgentStopReason.Stage(""));
        Assert.Equal(string.Empty, CodingAgentStopReason.NextStep(""));
    }
}
