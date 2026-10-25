using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The "low" demand tier (0.28-0.48) is the DEFAULT bucket for ordinary conversation - role
/// "standard" starts at demand 0.38 with no other signal - so its reasoning-budget setting lands
/// on most chat turns, not just a narrow low-latency path.
///
/// Measured live, 4 representative ordinary prompts ("recommend a pasta recipe", "how do I say
/// good morning in French", "what's a good way to organize my week", "what's the weather usually
/// like in autumn") through the previous budget of 768 vs 256 vs 0: 768 was slowest on every single
/// prompt (53-116s, one case exceeded 180s for a two-word answer), 256 was not reliably faster than
/// 768 and produced the most bloated answers of the three (up to 6,670 characters for a two-line
/// recipe question), and 0 was fastest on every prompt (10-39s) with no quality loss observed on
/// manual inspection - e.g. the pasta recipe answer at budget 0 was well-structured with genuine
/// technique, not shallow.
/// </summary>
public sealed class AdaptiveReasoningLowTierTests
{
    [Theory]
    [InlineData(0.28)]
    [InlineData(0.38)]
    [InlineData(0.47)]
    public void The_low_demand_tier_disables_hidden_reasoning(double demand)
    {
        AdaptiveReasoningPolicy.Decision decision = AdaptiveReasoningPolicy.Decide(demand, role: "standard");

        Assert.Equal("low", decision.Effort);
        Assert.Equal(0, decision.Budget);
    }

    /// <summary>The tier boundary itself is unchanged by this fix - only what each tier does with
    /// its budget changed. 0.48 must still select "medium", not "low".</summary>
    [Fact]
    public void The_tier_boundary_at_0_48_is_unchanged()
    {
        AdaptiveReasoningPolicy.Decision decision = AdaptiveReasoningPolicy.Decide(0.48, role: "standard");

        Assert.Equal("medium", decision.Effort);
        Assert.Equal(2048, decision.Budget);
    }

    /// <summary>An ordinary conversational prompt with no coding/reasoning/fast signal - the exact
    /// shape of prompt that lands in this tier by default - keeps its "standard" role and gets no
    /// hidden reasoning budget.</summary>
    [Theory]
    [InlineData("Can you recommend a simple pasta recipe?")]
    [InlineData("What's a good way to organize my week?")]
    [InlineData("How do I say good morning in French?")]
    public void An_ordinary_conversational_prompt_lands_in_the_low_tier_with_no_hidden_budget(string prompt)
    {
        string role = AdaptiveReasoningPolicy.InferRole(prompt);
        Assert.Equal("standard", role);

        double demand = PaRuX.Server.CandidatePoolRanker.EstimateTaskIntelligenceDemand(prompt, role);
        AdaptiveReasoningPolicy.Decision decision = AdaptiveReasoningPolicy.Decide(demand, role);

        Assert.Equal(0, decision.Budget);
    }
}
