using PaRuX.Services;
using Xunit;

public sealed class CodingAgentProtocolRetryPolicyTests
{
    [Fact]
    public void TwoCorrectionsAreAllowedBeforeTheThirdProtocolFailureStops()
    {
        string first = CodingAgentProtocolRetryPolicy.CorrectionInstruction(1, "empty response");
        string second = CodingAgentProtocolRetryPolicy.CorrectionInstruction(2, "invalid action");

        Assert.Contains("Retry 1 of 2", first);
        Assert.Contains("Final retry", second);
        Assert.NotEqual(first, second);
        Assert.True(CodingAgentProtocolRetryPolicy.ShouldRetry(1));
        Assert.True(CodingAgentProtocolRetryPolicy.ShouldRetry(2));
        Assert.False(CodingAgentProtocolRetryPolicy.ShouldRetry(3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CodingAgentProtocolRetryPolicy.CorrectionInstruction(3, "invalid"));
        Assert.Contains("3 invalid action responses", CodingAgentProtocolRetryPolicy.StopReason(3));
    }
}
