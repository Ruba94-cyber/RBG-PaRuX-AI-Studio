using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingAgentNarrationStallPolicyTests
{
    [Fact]
    public void Prose_only_responses_get_distinct_bounded_nudges_then_stop()
    {
        Assert.True(CodingAgentNarrationStallPolicy.IsNarrationOnly("I will inspect the project first."));
        Assert.False(CodingAgentNarrationStallPolicy.IsNarrationOnly("```json\n{}\n```"));
        Assert.False(CodingAgentNarrationStallPolicy.IsNarrationOnly(""));

        string? firstNudge = CodingAgentNarrationStallPolicy.NudgeFor(2);
        string? finalNudge = CodingAgentNarrationStallPolicy.NudgeFor(3);
        Assert.False(string.IsNullOrWhiteSpace(firstNudge));
        Assert.False(string.IsNullOrWhiteSpace(finalNudge));
        Assert.NotEqual(firstNudge, finalNudge);
        Assert.False(CodingAgentNarrationStallPolicy.ShouldStop(3));
        Assert.True(CodingAgentNarrationStallPolicy.ShouldStop(4));
    }
}
