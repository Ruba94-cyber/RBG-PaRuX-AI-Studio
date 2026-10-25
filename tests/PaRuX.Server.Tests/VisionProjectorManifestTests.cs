using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Vision-projector pairing as data. The manifest decides which families take a projector and which
/// projector to prefer. The planner attaches the repository's projector as a second file, and says so
/// in plain words when a vision family has none. The library pairs a projector with its model and never
/// lists a projector as a model of its own.
/// </summary>
public sealed class VisionProjectorManifestTests
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void A_two_character_token_is_refused()
    {
        const string json = "{ \"schemaVersion\": 1, \"projectorQuantizationPreference\": [\"F16\"], \"families\": [{ \"id\": \"x-family\", \"visionCapable\": true, \"tokens\": [\"ab\"] }] }";

        Assert.Throws<InvalidDataException>(() => VisionProjectorManifest.Parse(json));
    }

    [Fact]
    public void A_three_character_token_is_the_shortest_accepted()
    {
        VisionProjectorManifest manifest = VisionProjectorManifest.Parse(
            "{ \"schemaVersion\": 1, \"projectorQuantizationPreference\": [\"F16\"], \"families\": [{ \"id\": \"x-family\", \"visionCapable\": true, \"tokens\": [\"xyz\"] }] }");

        Assert.Equal("x-family", manifest.FamilyFor("model-xyz.gguf")?.Id);
    }

    [Fact]
    public void A_projector_of_another_family_is_never_planned_with_a_text_only_model()
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

    [Fact]
    public void Embedded_manifest_loads_with_its_vision_families()
    {
        VisionProjectorManifest manifest = VisionProjectorManifest.Default;

        Assert.Contains(manifest.Families, f => f.Id == "qwen3-vl" && f.VisionCapable);
        Assert.Contains(manifest.Families, f => f.Id == "qwen3.8" && f.VisionCapable);
        Assert.Equal("Qwen3.8-27B-UD-IQ4_XS.gguf", manifest.BundledPairForRole("standard")?.ModelFileName);
        Assert.Equal("mmproj-Qwen3.8-27B-Q8_0.gguf", manifest.BundledPairForRole("standard")?.ProjectorFileName);
        Assert.Equal("Qwen3.8-27B-Uncensored-noMTP-IQ4_XS.gguf", manifest.BundledPairForRole("uncensored")?.ModelFileName);
        Assert.Equal("Qwen3.8-27B-Uncensored-vision-f16.gguf", manifest.BundledPairForRole("uncensored")?.ProjectorFileName);
    }

    [Fact]
    public void Bundled_role_pair_lookup_uses_manifest_data_and_accepts_the_main_alias()
    {
        VisionProjectorManifest manifest = VisionProjectorManifest.Parse("""
            {
              "schemaVersion": 1,
              "projectorQuantizationPreference": ["F16"],
              "families": [{
                "id": "vision-x",
                "visionCapable": true,
                "tokens": ["vision-x"],
                "bundledModels": [{
                  "role": "standard",
                  "modelFileName": "vision-x-model.gguf",
                  "projectorFileName": "vision-x-projector.gguf"
                }]
              }]
            }
            """);

        Assert.Equal(
            new VisionProjectorManifest.BundledRoleModel("standard", "vision-x-model.gguf", "vision-x-projector.gguf"),
            manifest.BundledPairForRole("main"));
        Assert.Equal(manifest.BundledPairForRole("main"), manifest.BundledPairForRole("standard"));
    }

    [Theory]
    [InlineData("unsloth/Qwen3-VL-8B-Instruct-GGUF", "qwen3-vl")]
    [InlineData("Qwen3-VL-8B-Instruct-Q4_K_M.gguf", "qwen3-vl")]
    [InlineData("Qwen3.8-27B-UD-IQ4_XS.gguf", "qwen3.8")]
    [InlineData("llava-v1.6-mistral-7b.Q4_K_M.gguf", "llava")]
    [InlineData("Qwen3-8B-Q4_K_M.gguf", null)]
    [InlineData("nollavaish-model.gguf", null)]
    public void A_family_matches_only_at_a_token_boundary(string text, string? expectedFamily)
    {
        Assert.Equal(expectedFamily, VisionProjectorManifest.Default.FamilyFor(text)?.Id);
    }

    [Fact]
    public void Projector_choice_prefers_the_configured_quantisation_then_the_name()
    {
        VisionProjectorManifest manifest = VisionProjectorManifest.Default;

        Assert.Equal("mmproj-F16.gguf", manifest.ChooseProjector(new[] { "mmproj-Q8_0.gguf", "mmproj-F16.gguf" }));
        Assert.Equal("mmproj-Q8_0.gguf", manifest.ChooseProjector(new[] { "mmproj-x-Q4_K_M.gguf", "mmproj-Q8_0.gguf" }));
        Assert.Equal("a-mmproj.gguf", manifest.ChooseProjector(new[] { "b-mmproj.gguf", "a-mmproj.gguf" }));
        Assert.Null(manifest.ChooseProjector(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 2, \"projectorQuantizationPreference\": [\"F16\"], \"families\": [{ \"id\": \"x\", \"visionCapable\": true, \"tokens\": [\"xxx\"] }] }")]
    [InlineData("not json at all")]
    [InlineData("{ \"schemaVersion\": 1, \"projectorQuantizationPreference\": [\"F16\"], \"families\": [] }")]
    [InlineData("{ \"schemaVersion\": 1, \"projectorQuantizationPreference\": [\"Q9_Z\"], \"families\": [{ \"id\": \"x\", \"visionCapable\": true, \"tokens\": [\"xxx\"] }] }")]
    [InlineData("{ \"schemaVersion\": 1, \"projectorQuantizationPreference\": [\"F16\"], \"families\": [{ \"id\": \"x\", \"visionCapable\": true, \"tokens\": [\"../etc\"] }] }")]
    public void A_malformed_manifest_is_refused(string json)
    {
        Assert.Throws<InvalidDataException>(() => VisionProjectorManifest.Parse(json));
    }

    [Fact]
    public void The_plan_carries_the_repository_projector_as_a_second_file_without_a_note()
    {
        ModelDownloadPlan plan = ModelDownloadPlanner.Plan("unsloth/Qwen3-VL-8B-Instruct-GGUF", Revision, new[]
        {
            E("Qwen3-VL-8B-Instruct-Q4_K_M.gguf", 50),
            E("mmproj-Qwen3-VL-8B-Instruct-Q8_0.gguf", 9),
            E("mmproj-Qwen3-VL-8B-Instruct-F16.gguf", 17)
        }, "Q4_K_M");

        Assert.True(plan.Complete);
        Assert.Equal("Qwen3-VL-8B-Instruct-Q4_K_M.gguf", Assert.Single(plan.Parts).FileName);
        Assert.NotNull(plan.Projector);
        Assert.Equal("mmproj-Qwen3-VL-8B-Instruct-F16.gguf", plan.Projector!.FileName);
        Assert.Null(plan.TextOnlyNote);
        Assert.Equal(67, plan.TotalBytes);
    }

    [Fact]
    public void A_vision_family_without_a_projector_gets_a_visible_text_only_note()
    {
        ModelDownloadPlan plan = ModelDownloadPlanner.Plan("unsloth/Qwen3-VL-8B-Instruct-GGUF", Revision, new[]
        {
            E("Qwen3-VL-8B-Instruct-Q4_K_M.gguf")
        }, null);

        Assert.Null(plan.Projector);
        Assert.NotNull(plan.TextOnlyNote);
        Assert.Contains("text-only", plan.TextOnlyNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_text_only_family_gets_neither_a_projector_nor_a_note()
    {
        ModelDownloadPlan plan = ModelDownloadPlanner.Plan("Qwen/Qwen3-8B-GGUF", Revision, new[]
        {
            E("Qwen3-8B-Q4_K_M.gguf")
        }, null);

        Assert.Null(plan.Projector);
        Assert.Null(plan.TextOnlyNote);
    }

    [Fact]
    public void A_projector_alone_is_never_planned_as_the_model()
    {
        Assert.Throws<InvalidDataException>(() =>
            ModelDownloadPlanner.Plan("acme/vision", Revision, new[] { E("mmproj-F16.gguf") }, null));
    }

    [Fact]
    public void A_split_model_plans_its_parts_and_its_projector_together()
    {
        ModelDownloadPlan plan = ModelDownloadPlanner.Plan("unsloth/Qwen3-VL-8B-GGUF", Revision, new[]
        {
            E("Q4_K_M/Qwen3-VL-8B-Q4_K_M-00001-of-00002.gguf", 10),
            E("Q4_K_M/Qwen3-VL-8B-Q4_K_M-00002-of-00002.gguf", 20),
            E("mmproj-Qwen3-VL-8B-F16.gguf", 5)
        }, "Q4_K_M");

        Assert.True(plan.Complete);
        Assert.Equal(2, plan.Parts.Count);
        Assert.NotNull(plan.Projector);
        Assert.Equal(35, plan.TotalBytes);
        Assert.Null(plan.TextOnlyNote);
    }

    [Fact]
    public void Library_pairs_a_projector_with_its_model_and_never_lists_the_projector_as_a_model()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using IDisposable scope = ModelDownloadService.UseModelsDirectoryForTests(root);
            File.WriteAllBytes(Path.Combine(root, "Qwen3-VL-8B-Instruct-Q4_K_M.gguf"), new byte[12]);
            File.WriteAllBytes(Path.Combine(root, "mmproj-Qwen3-VL-8B-Instruct-F16.gguf"), new byte[4]);
            File.WriteAllBytes(Path.Combine(root, "Qwen3-8B-Q4_K_M.gguf"), new byte[8]);
            var service = new ModelDownloadService();

            IReadOnlyList<ManagedModelInfo> models = service.ListManagedModels();

            Assert.DoesNotContain(models, m => m.FileName.StartsWith("mmproj-", StringComparison.Ordinal));
            ManagedModelInfo vision = Assert.Single(models, m => m.FileName == "Qwen3-VL-8B-Instruct-Q4_K_M.gguf");
            Assert.Equal("mmproj-Qwen3-VL-8B-Instruct-F16.gguf", vision.ProjectorFileName);
            ManagedModelInfo text = Assert.Single(models, m => m.FileName == "Qwen3-8B-Q4_K_M.gguf");
            Assert.Null(text.ProjectorFileName);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
