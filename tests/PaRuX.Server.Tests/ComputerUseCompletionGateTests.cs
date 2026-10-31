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
    [Theory]
    [InlineData(true, false, true, false, 0, false, false)]
    [InlineData(true, false, true, true, 0, true, false)]
    [InlineData(true, false, true, false, 3, true, false)]
    [InlineData(true, false, true, true, 1, false, false)]
    [InlineData(true, false, true, true, 1, true, true)]
    [InlineData(true, false, true, true, 4, true, true)]
    [InlineData(true, true, true, true, 2, true, false)]
    [InlineData(false, false, true, true, 2, true, false)]
    [InlineData(true, false, false, false, 0, false, true)]
    public void Gui_testing_tasks_need_verified_final_readback_not_just_old_assertions(
        bool goalReported, bool cancelled, bool proofRequired,
        bool proofSatisfied, int assertions, bool finalReadback, bool allowed)
    {
        Assert.Equal(allowed, ComputerUseCompletionGate.MayRequestVerification(
            goalReported, cancelled, proofRequired, proofSatisfied,
            assertions, finalReadback));
    }

}
