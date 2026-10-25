using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TrainingExecutionProfileBudgetTests
{
    [Theory]
    [InlineData(ExecutionQualityProfile.Fast, 15)]
    [InlineData(ExecutionQualityProfile.Balanced, 60)]
    [InlineData(ExecutionQualityProfile.MaximumQuality, 180)]
    [InlineData(ExecutionQualityProfile.Overnight, 720)]
    public void Training_uses_the_active_profile_wall_clock_budget(string quality, int expectedMinutes)
    {
        ModelExecutionProfile profile = ExecutionProfileStore.BuildDefault("training", quality);

        Assert.Equal(expectedMinutes, TrainingLabService.ResolveWallClockMinutes(profile));
    }

    [Fact]
    public void Missing_profile_falls_back_to_balanced_budget()
    {
        Assert.Equal(60, TrainingLabService.ResolveWallClockMinutes(null));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(5000, 1440)]
    public void Training_budget_is_clamped_fail_closed(int configuredMinutes, int expectedMinutes)
    {
        ModelExecutionProfile profile = ExecutionProfileStore.BuildDefault("training", ExecutionQualityProfile.Balanced);
        profile.Budget.MaxWallClockMinutes = configuredMinutes;

        Assert.Equal(expectedMinutes, TrainingLabService.ResolveWallClockMinutes(profile));
    }
}
