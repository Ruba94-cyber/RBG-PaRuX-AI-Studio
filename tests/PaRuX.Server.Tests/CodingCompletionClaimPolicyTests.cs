using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingCompletionClaimPolicyTests
{
    [Theory]
    [InlineData("All tests passed.", true)]
    [InlineData("Tests passed successfully; implementation done.", true)]
    [InlineData("4 tests passed after patch.", true)]
    [InlineData("Unit tests are passing.", true)]
    [InlineData("The test suite is green.", true)]
    [InlineData("Fixed parser; tests not run.", false)]
    [InlineData("Tests have not passed.", false)]
    [InlineData("The tests haven't passed yet.", false)]
    [InlineData("No test command was executed.", false)]
    [InlineData("Completed the documentation task.", false)]
    [InlineData("If tests passed, the change would be ready.", false)]
    public void Completion_must_not_claim_unverified_test_success(
        string summary, bool requiresCorrection)
    {
        Assert.Equal(requiresCorrection,
            CodingCompletionClaimPolicy.RequiresCorrection(summary, verifiedByCommand: false));
        Assert.False(CodingCompletionClaimPolicy.RequiresCorrection(
            summary, verifiedByCommand: true));
    }

    [Fact]
    public void Empty_summary_is_not_a_fake_execution_claim()
    {
        Assert.False(CodingCompletionClaimPolicy.RequiresCorrection(null, false));
        Assert.False(CodingCompletionClaimPolicy.RequiresCorrection(string.Empty, false));
    }
}
