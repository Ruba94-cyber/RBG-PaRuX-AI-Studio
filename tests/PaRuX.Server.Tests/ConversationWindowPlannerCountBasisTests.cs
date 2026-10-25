using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConversationWindowPlannerCountBasisTests
{
    [Fact]
    public void Plan_without_a_counter_is_labeled_estimated_even_when_a_tokenizer_basis_is_passed()
    {
        // Without countTokens the planner measures with EstimateTokens, so a Tokenizer label would be false.
        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            new[] { new ConversationWindowPlanner.Turn("user", "hello") },
            contextTokens: 4096,
            reservedForReplyTokens: 32,
            countBasis: ConversationWindowPlanner.TokenCountBasis.Tokenizer);

        Assert.Equal(nameof(ConversationWindowPlanner.TokenCountBasis.Estimated), plan.CountBasis);
    }

    [Fact]
    public void Plan_without_a_counter_ignores_a_tokenizer_basis_provider()
    {
        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            new[] { new ConversationWindowPlanner.Turn("user", "hello") },
            contextTokens: 4096,
            reservedForReplyTokens: 32,
            countBasisProvider: () => ConversationWindowPlanner.TokenCountBasis.Tokenizer);

        Assert.Equal(nameof(ConversationWindowPlanner.TokenCountBasis.Estimated), plan.CountBasis);
    }
}
