using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingAgentProactiveContextPolicyTests
{
    [Theory]
    [InlineData(2787, 8192, 4864, 3, false)] // below 85% of usable 3328
    [InlineData(2829, 8192, 4864, 3, true)]
    [InlineData(3328, 8192, 4864, 3, true)]  // hard fit but near-full
    [InlineData(3329, 8192, 4864, 3, false)] // no soft recovery claim if already over hard limit
    [InlineData(2829, 8192, 4864, 2, false)] // no meaningful observation history
    [InlineData(null, 8192, 4864, 3, false)] // unavailable tokenizer
    [InlineData(2829, 0, 4864, 3, false)] // unknown context
    [InlineData(2829, 4000, 4864, 3, false)] // impossible reserve
    [InlineData(75000, 100000, 5000, 5, false)] // ample headroom
    public void Exact_high_watermark_preemptively_trims_only_when_usable_window_is_near_full(
        int? input, int window, int reserve, int observed, bool expected)
    {
        Assert.Equal(expected, CodingAgentProactiveContextPolicy.ShouldTrim(
            input, window, reserve, observed));
    }

    [Theory]
    [InlineData(48000, 2500)]
    [InlineData(4500, 2250)]
    [InlineData(2500, 1250)]
    [InlineData(0, 0)]
    public void Proactive_pass_never_expands_replay_budget(int input, int expected)
    {
        Assert.Equal(expected,
            CodingAgentProactiveContextPolicy.SuggestedRecentEvidenceCharacters(input));
    }

    [Theory]
    [InlineData(2000, 1744, true)]
    [InlineData(2000, 1745, false)]
    [InlineData(2000, 2000, false)]
    [InlineData(0, 0, false)]
    public void Proactive_pass_avoids_second_expensive_tokenizer_call_for_trivial_savings(
        int original, int proposed, bool expected)
    {
        Assert.Equal(expected,
            CodingAgentProactiveContextPolicy.HasUsefulSaving(original, proposed));
    }
}
