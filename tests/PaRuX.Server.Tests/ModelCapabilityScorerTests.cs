using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelCapabilityScorerTests
{
    [Fact]
    public void Coding_role_weights_coding_debugging_and_patch_more_than_unrelated_categories()
    {
        var codingStrong = new Dictionary<string, double>
        {
            [CapabilityProbeCategory.Coding] = 1,
            [CapabilityProbeCategory.Debugging] = 1,
            [CapabilityProbeCategory.PatchGeneration] = 1,
            [CapabilityProbeCategory.InstructionFollowing] = 1,
            [CapabilityProbeCategory.MathReasoning] = 0,
            [CapabilityProbeCategory.Multilingual] = 0
        };
        var codingWeak = new Dictionary<string, double>
        {
            [CapabilityProbeCategory.Coding] = 0,
            [CapabilityProbeCategory.Debugging] = 0,
            [CapabilityProbeCategory.PatchGeneration] = 0,
            [CapabilityProbeCategory.InstructionFollowing] = 1,
            [CapabilityProbeCategory.MathReasoning] = 1,
            [CapabilityProbeCategory.Multilingual] = 1
        };

        Assert.True(ModelCapabilityScorer.ForRole(codingStrong, "coding")
            > ModelCapabilityScorer.ForRole(codingWeak, "coding"));
    }

    [Fact]
    public void Reasoning_role_prefers_math_and_long_context_evidence()
    {
        var reasoningStrong = new Dictionary<string, double>
        {
            [CapabilityProbeCategory.MathReasoning] = 1,
            [CapabilityProbeCategory.LongContextRecall] = 1,
            [CapabilityProbeCategory.InstructionFollowing] = 1,
            [CapabilityProbeCategory.Coding] = 0
        };
        var reasoningWeak = new Dictionary<string, double>
        {
            [CapabilityProbeCategory.MathReasoning] = 0,
            [CapabilityProbeCategory.LongContextRecall] = 0,
            [CapabilityProbeCategory.InstructionFollowing] = 1,
            [CapabilityProbeCategory.Coding] = 1
        };

        Assert.True(ModelCapabilityScorer.ForRole(reasoningStrong, "reasoning")
            > ModelCapabilityScorer.ForRole(reasoningWeak, "reasoning"));
    }

    [Fact]
    public void Missing_preferred_categories_fall_back_to_real_overall_evidence()
    {
        var rates = new Dictionary<string, double>
        {
            [CapabilityProbeCategory.Multilingual] = 0.25
        };

        Assert.Equal(ModelCapabilityScorer.Overall(rates),
            ModelCapabilityScorer.ForRole(rates, "reviewer"));
    }

    [Fact]
    public void No_measurements_remain_unknown()
    {
        Assert.Null(ModelCapabilityScorer.Overall(new Dictionary<string, double>()));
        Assert.Null(ModelCapabilityScorer.ForRole(new Dictionary<string, double>(), "coding"));
    }
}
