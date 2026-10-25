using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PromptCacheBenefitPolicyTests
{
    [Fact]
    public void Material_ttft_gain_recommends_prompt_cache()
    {
        Assert.True(LlamaServerPerformanceProbe.IsPromptCacheRecommended(400, 300));
        Assert.Equal(25.0,
            LlamaServerPerformanceProbe.ComputePromptCacheImprovementPercent(400, 300), 3);
    }

    [Fact]
    public void Small_percentage_gain_is_not_enough()
    {
        Assert.False(LlamaServerPerformanceProbe.IsPromptCacheRecommended(200, 193));
    }

    [Fact]
    public void Percentage_gain_without_meaningful_absolute_saving_is_not_enough()
    {
        // 6% is above the percentage floor, but 12ms is below the absolute guard.
        Assert.False(LlamaServerPerformanceProbe.IsPromptCacheRecommended(200, 188));
    }

    [Fact]
    public void Regression_never_recommends_prompt_cache()
    {
        Assert.False(LlamaServerPerformanceProbe.IsPromptCacheRecommended(250, 275));
        Assert.True(LlamaServerPerformanceProbe.ComputePromptCacheImprovementPercent(250, 275) < 0);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(double.NaN, 100)]
    public void Invalid_measurements_fail_closed_as_no_benefit(double cold, double warm)
    {
        Assert.False(LlamaServerPerformanceProbe.IsPromptCacheRecommended(cold, warm));
    }
}
