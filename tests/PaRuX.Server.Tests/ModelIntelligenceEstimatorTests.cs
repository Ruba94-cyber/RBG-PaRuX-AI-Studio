using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelIntelligenceEstimatorTests
{
    [Theory]
    [InlineData("Qwen2.5-27B-Instruct-Q4_K_M.gguf", 27.0)]
    [InlineData("model-4B-Q8_0.gguf", 4.0)]
    [InlineData("Mixtral-8x7B.gguf", 7.0)]
    [InlineData("Qwen3-30B-A3B-Q4_K_M.gguf", 30.0)]
    public void Parses_parameter_scale_without_confusing_quantization(string fileName, double expected)
    {
        Assert.Equal(expected, ModelIntelligenceEstimator.TryParseParameterBillions(fileName));
    }

    [Fact]
    public void Scale_prior_prefers_27b_over_4b_when_both_are_unmeasured()
    {
        ModelIntelligenceEstimator.Estimate small =
            ModelIntelligenceEstimator.EstimateFrom("model-4B-Q4_K_M.gguf", null);
        ModelIntelligenceEstimator.Estimate large =
            ModelIntelligenceEstimator.EstimateFrom("model-27B-Q4_K_M.gguf", null);

        Assert.NotNull(small.Score);
        Assert.NotNull(large.Score);
        Assert.True(large.Score > small.Score);
        Assert.Equal("scale-prior-unmeasured", large.Evidence);
    }

    [Fact]
    public void Measured_capability_remains_the_dominant_signal_when_available()
    {
        ModelIntelligenceEstimator.Estimate weakLarge =
            ModelIntelligenceEstimator.EstimateFrom("model-27B.gguf", 0.25);
        ModelIntelligenceEstimator.Estimate strongSmall =
            ModelIntelligenceEstimator.EstimateFrom("model-4B.gguf", 0.90);

        Assert.True(strongSmall.Score > weakLarge.Score);
        Assert.Equal("measured-capability+scale-prior", strongSmall.Evidence);
    }

    [Fact]
    public void Unknown_identity_stays_unmeasured_instead_of_fabricating_intelligence()
    {
        ModelIntelligenceEstimator.Estimate value =
            ModelIntelligenceEstimator.EstimateFrom("mystery-model.gguf", null);

        Assert.Null(value.Score);
        Assert.Null(value.ParameterBillions);
        Assert.Equal("unmeasured", value.Evidence);
    }
}
