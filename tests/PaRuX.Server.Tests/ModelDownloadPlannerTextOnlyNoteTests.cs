using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDownloadPlannerTextOnlyNoteTests
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Vision_model_with_no_projector_keeps_the_existing_note()
    {
        ModelDownloadPlan plan = ModelDownloadPlanner.Plan("unsloth/Qwen3-VL-8B-Instruct-GGUF", Revision, new[]
        {
            E("Qwen3-VL-8B-Instruct-Q4_K_M.gguf")
        }, null);

        Assert.Equal(
            "This qwen3-vl model needs a vision projector, and this repository lists none. It will run text-only.",
            plan.TextOnlyNote);
    }

    [Fact]
    public void Vision_model_with_only_other_family_projectors_says_none_match_its_family()
    {
        ModelDownloadPlan plan = ModelDownloadPlanner.Plan("unsloth/Qwen3-VL-8B-Instruct-GGUF", Revision, new[]
        {
            E("Qwen3-VL-8B-Instruct-Q4_K_M.gguf"),
            E("mmproj-llava-v1.6-mistral-F16.gguf")
        }, null);

        Assert.Null(plan.Projector);
        Assert.Equal(
            "This qwen3-vl model needs a vision projector, but this repository lists projectors with no match for its family. It will run text-only.",
            plan.TextOnlyNote);
    }

    [Fact]
    public void Text_only_model_never_pairs_an_unrelated_projector()
    {
        ModelDownloadPlan plan = ModelDownloadPlanner.Plan("Qwen/Qwen3-8B-GGUF", Revision, new[]
        {
            E("Qwen3-8B-Q4_K_M.gguf", 50),
            E("mmproj-Qwen3-VL-8B-F16.gguf", 17)
        }, "Q4_K_M");

        Assert.Null(plan.Projector);
        Assert.Null(plan.TextOnlyNote);
        Assert.Equal(50, plan.TotalBytes);
    }

    private static GgufSplitSetGrouper.Entry E(string path, long size = 100) => new(path, size, null);
}
