using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// One capability taxonomy for role assignment, search and routing. Keywords match whole tokens, so a word that
/// merely contains a family name gains no capability, and one name gets the same answer on every screen.
/// </summary>
public sealed class ModelCapabilityTaxonomyTests
{
    [Theory]
    [InlineData("unicode-chat-7b.gguf")]
    [InlineData("whisper-decoder-small.gguf")]
    [InlineData("decode-helper-3b.gguf")]
    public void Words_that_only_contain_a_coding_keyword_are_not_coding(string fileName)
    {
        Assert.DoesNotContain(ModelCapabilityIds.Coding, FirstModelAdvisor.InferCapabilities(fileName));
    }

    [Theory]
    [InlineData("vllm-serve-7b.gguf")]
    [InlineData("revision-notes-3b.gguf")]
    public void A_vision_keyword_inside_another_word_is_not_vision(string fileName)
    {
        Assert.DoesNotContain(ModelCapabilityIds.Vision, FirstModelAdvisor.InferCapabilities(fileName));
    }

    [Fact]
    public void DeepSeek_V3_is_general_across_search_advisor_and_routing()
    {
        const string file = "DeepSeek-V3-Q4_K_M.gguf";
        const string repo = "deepseek-ai/DeepSeek-V3";

        IReadOnlyList<string> advisorCapabilities = FirstModelAdvisor.InferCapabilities(file, null, repo);
        ModelCapabilityScorer.Assessment routed = ModelCapabilityScorer.Assess(
            file, null, repo, null, "standard");
        ModelIntelligenceEstimator.Estimate intelligence = ModelIntelligenceEstimator.EstimateFrom(
            file, routed.RoleEmpiricalScore, routed.ModelCapabilities);

        Assert.Equal(new[] { ModelCapabilityIds.GeneralChat }, advisorCapabilities);
        Assert.Equal(ModelCategoryClassifier.General,
            ModelCategoryClassifier.Classify(repo, new[] { file }, null));
        Assert.Equal(ModelCategoryClassifier.General, routed.ModelCategory);
        Assert.Empty(routed.ModelCapabilities);
        Assert.Equal(routed.ModelCapabilities, intelligence.ModelCapabilities);
        Assert.Null(intelligence.Score); // A general name alone is not measured capability or a scale prior.
    }

    [Theory]
    [InlineData("Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf", "Qwen/Qwen2.5-Coder-7B-GGUF", ModelCategoryClassifier.Coding, ModelCapabilityIds.Coding)]
    [InlineData("Qwen2-VL-7B-Instruct-Q4_K_M.gguf", "Qwen/Qwen2-VL-7B-GGUF", ModelCategoryClassifier.Vision, ModelCapabilityIds.Vision)]
    [InlineData("DeepSeek-R1-Distill-Qwen-7B-Q4_K_M.gguf", "deepseek-ai/DeepSeek-R1-Distill-Qwen-7B-GGUF", ModelCategoryClassifier.Reasoning, ModelCapabilityIds.Reasoning)]
    public void Search_advisor_and_router_share_the_category_while_scores_remain_empirical(
        string file,
        string repo,
        string expectedCategory,
        string expectedCapability)
    {
        var rates = new Dictionary<string, double>
        {
            [CapabilityProbeCategory.Coding] = 0.4,
            [CapabilityProbeCategory.Debugging] = 0.8,
            [CapabilityProbeCategory.PatchGeneration] = 0.6,
            [CapabilityProbeCategory.InstructionFollowing] = 0.5,
            [CapabilityProbeCategory.MathReasoning] = 0.9,
            [CapabilityProbeCategory.LongContextRecall] = 0.7
        };

        IReadOnlyList<string> advisorCapabilities = FirstModelAdvisor.InferCapabilities(file, null, repo);
        ModelCapabilityScorer.Assessment routed = ModelCapabilityScorer.Assess(
            file, null, repo, rates, "coding");
        ModelIntelligenceEstimator.Estimate intelligence = ModelIntelligenceEstimator.EstimateFrom(
            file, routed.RoleEmpiricalScore, routed.ModelCapabilities);

        Assert.Equal(expectedCategory, ModelCategoryClassifier.Classify(repo, new[] { file }, null));
        Assert.Equal(expectedCategory, routed.ModelCategory);
        Assert.Contains(expectedCapability, advisorCapabilities);
        Assert.Contains(expectedCategory, routed.ModelCapabilities);
        Assert.Equal(routed.ModelCapabilities, intelligence.ModelCapabilities);
        Assert.Equal(ModelCapabilityScorer.Overall(rates), routed.OverallEmpiricalScore);
        Assert.Equal(ModelCapabilityScorer.ForRole(rates, "coding"), routed.RoleEmpiricalScore);
        Assert.Equal("measured-capability+scale-prior", intelligence.Evidence);
    }

    [Theory]
    [InlineData("qwen2-vl-7b-instruct.gguf")]
    [InlineData("qwen2.5-vl.gguf")]
    [InlineData("pixtral-12b-Q4_K_M.gguf")]
    [InlineData("MiniCPM-V-2_6-Q4_K_M.gguf")]
    public void Known_vision_families_are_vision(string fileName)
    {
        Assert.Contains(ModelCapabilityIds.Vision, FirstModelAdvisor.InferCapabilities(fileName));
    }

    [Fact]
    public void A_coder_model_is_coding_on_the_category_screen_and_the_capability_screen()
    {
        const string file = "Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf";
        const string repo = "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF";

        Assert.Equal(ModelCategoryClassifier.Coding, ModelCategoryClassifier.Classify(repo, new[] { file }, null));
        Assert.Contains(ModelCapabilityIds.Coding, FirstModelAdvisor.InferCapabilities(file, null, repo));
    }

    [Fact]
    public void A_distilled_reasoning_model_is_reasoning_on_both_screens()
    {
        const string file = "DeepSeek-R1-Distill-Qwen-7B-Q4_K_M.gguf";
        const string repo = "deepseek-ai/DeepSeek-R1-Distill-Qwen-7B-GGUF";

        Assert.Equal(ModelCategoryClassifier.Reasoning, ModelCategoryClassifier.Classify(repo, new[] { file }, null));
        Assert.Contains(ModelCapabilityIds.Reasoning, FirstModelAdvisor.InferCapabilities(file, null, repo));
    }

    [Fact]
    public void CapabilitiesOf_reports_every_capability_a_name_carries_in_taxonomy_order()
    {
        IReadOnlyList<string> found = ModelCategoryClassifier.CapabilitiesOf("qwen2-vl-coder-reasoning-r1.gguf", null, null);

        Assert.Equal(new[] { ModelCategoryClassifier.Coding, ModelCategoryClassifier.Vision, ModelCategoryClassifier.Reasoning }, found);
    }
}
