using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExecutionProfileChatPolicyTests
{
    [Fact]
    public void Fast_profile_caps_expensive_reasoning_without_disabling_it()
    {
        var adaptive = new AdaptiveReasoningPolicy.Decision("xhigh", 8_192, 0.92);
        ModelExecutionProfile profile = ExecutionProfileStore.BuildDefault("model", ExecutionQualityProfile.Fast);

        AdaptiveReasoningPolicy.Decision decision = AdaptiveReasoningPolicy.ApplyExecutionProfile(adaptive, profile);

        Assert.Equal("medium", decision.Effort);
        Assert.Equal(2_048, decision.Budget);
        Assert.Equal(adaptive.Demand, decision.Demand);
    }

    [Fact]
    public void Balanced_profile_preserves_existing_adaptive_depth()
    {
        var adaptive = new AdaptiveReasoningPolicy.Decision("xhigh", 8_192, 0.92);
        ModelExecutionProfile profile = ExecutionProfileStore.BuildDefault("model", ExecutionQualityProfile.Balanced);

        AdaptiveReasoningPolicy.Decision decision = AdaptiveReasoningPolicy.ApplyExecutionProfile(adaptive, profile);

        Assert.Equal(adaptive, decision);
    }

    [Theory]
    [InlineData(ExecutionQualityProfile.MaximumQuality)]
    [InlineData(ExecutionQualityProfile.Overnight)]
    public void Quality_profiles_do_not_invent_reasoning_for_zero_budget_turns(string quality)
    {
        var adaptive = new AdaptiveReasoningPolicy.Decision("low", 0, 0.38);
        ModelExecutionProfile profile = ExecutionProfileStore.BuildDefault("model", quality);

        AdaptiveReasoningPolicy.Decision decision = AdaptiveReasoningPolicy.ApplyExecutionProfile(adaptive, profile);

        Assert.Equal(adaptive, decision);
    }

    [Fact]
    public void Explicit_zero_reasoning_override_is_fail_closed()
    {
        var adaptive = new AdaptiveReasoningPolicy.Decision("high", 4_096, 0.75);
        ModelExecutionProfile profile = ExecutionProfileStore.BuildDefault("model", ExecutionQualityProfile.Balanced);
        profile.ReasoningEffort = 0;

        AdaptiveReasoningPolicy.Decision decision = AdaptiveReasoningPolicy.ApplyExecutionProfile(adaptive, profile);

        Assert.Equal("minimal", decision.Effort);
        Assert.Equal(0, decision.Budget);
    }
}
