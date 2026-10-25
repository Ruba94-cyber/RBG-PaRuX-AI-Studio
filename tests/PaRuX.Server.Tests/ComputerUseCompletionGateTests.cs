using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseCompletionGateTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Only_uncancelled_explicit_completion_claim_can_reach_independent_verifier(
        bool goalReported, bool cancelled, bool eligible)
    {
        Assert.Equal(eligible, ComputerUseCompletionGate.MayRequestVerification(goalReported, cancelled));
    }
}
