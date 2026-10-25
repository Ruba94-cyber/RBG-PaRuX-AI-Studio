using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingContextBudgetResolverTests
{
    [Fact]
    public void Unknown_context_uses_conservative_fallback_without_fabricating_tokens()
    {
        CodingContextBudgetPolicy.Budget budget = CodingContextBudgetPolicy.ForContextTokens(0);

        Assert.False(budget.Observed);
        Assert.Equal(0, budget.ContextTokens);
        Assert.Equal(CodingContextCompiler.DefaultMaxChars, budget.MaxCharacters);
    }

    [Theory]
    [InlineData(8192)]
    [InlineData(16384)]
    [InlineData(32768)]
    [InlineData(65536)]
    [InlineData(131072)]
    public void Observed_context_scales_repository_budget_inside_safe_bounds(int contextTokens)
    {
        CodingContextBudgetPolicy.Budget budget = CodingContextBudgetPolicy.ForContextTokens(contextTokens);

        Assert.True(budget.Observed);
        Assert.Equal(contextTokens, budget.ContextTokens);
        Assert.InRange(budget.MaxCharacters, 6000, CodingContextBudgetPolicy.MaximumContextCharacters);
    }

    [Fact]
    public void Larger_context_never_reduces_repository_budget()
    {
        int previous = 0;
        foreach (int tokens in new[] { 4096, 8192, 16384, 32768, 65536, 131072 })
        {
            int current = CodingContextBudgetPolicy.ForContextTokens(tokens).MaxCharacters;
            Assert.True(current >= previous);
            previous = current;
        }
    }

    [Fact]
    public void Large_context_is_capped_by_the_compiler_ceiling()
    {
        CodingContextBudgetPolicy.Budget budget = CodingContextBudgetPolicy.ForContextTokens(1_000_000);

        Assert.Equal(CodingContextBudgetPolicy.MaximumContextCharacters, budget.MaxCharacters);
    }
}
