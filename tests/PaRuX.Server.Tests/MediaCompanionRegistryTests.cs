using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Companion registry validation, family detection from synthetic headers, and the resolver that computes the
/// missing companion set against a real (temp) ComfyUI models folder.
/// </summary>
public sealed class MediaCompanionRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-reg-" + Guid.NewGuid().ToString("N"));

    public MediaCompanionRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------- helpers ----------

    private static SafetensorsHeader Layout(params (string Name, long[] Shape)[] tensors) =>
        new(0, 0, tensors.Select(t => new SafetensorsTensor(t.Name, "BF16", t.Shape, 0, 0)).ToList(),
            new Dictionary<string, string>());

    private static SafetensorsHeader ZImageLayout() => Layout(
        ("noise_refiner.0.attention.qkv.weight", new long[] { 4, 4 }),
        ("context_refiner.0.attention.qkv.weight", new long[] { 4, 4 }),
        ("cap_embedder.1.weight", new long[] { 4, 4 }),
        ("x_embedder.weight", new long[] { 4, 4 }),
        ("layers.0.attention.qkv.weight", new long[] { 4, 4 }));

    private static SafetensorsHeader Flux2Layout() => Layout(
        ("double_blocks.0.img_attn.qkv.weight", new long[] { 4, 4 }),
        ("single_blocks.0.linear1.weight", new long[] { 4, 4 }),
        ("img_in.weight", new long[] { 4, 4 }),
        ("txt_in.weight", new long[] { 4, 4 }));

    private static SafetensorsHeader Wan5BLayout(long inputChannels = 48) => Layout(
        ("blocks.0.self_attn.q.weight", new long[] { 3072, 3072 }),
        ("patch_embedding.weight", new long[] { 3072, inputChannels, 1, 2, 2 }),
        ("text_embedding.0.weight", new long[] { 3072, 4096 }),
        ("time_embedding.0.weight", new long[] { 3072, 256 }),
        ("head.head.weight", new long[] { 48, 3072 }));

    // Tensor names read from the real header of Comfy-Org/Qwen-Image_ComfyUI qwen_image_fp8_e4m3fn.safetensors
    // (1933 tensors); the same top-level names are in the Qwen-Image-Edit and nvfp4 files.
    private static SafetensorsHeader QwenLayout() => Layout(
        ("transformer_blocks.0.attn.add_q_proj.weight", new long[] { 4, 4 }),
        ("time_text_embed.timestep_embedder.linear_1.weight", new long[] { 3072, 256 }),
        ("img_in.weight", new long[] { 3072, 64 }),
        ("txt_in.weight", new long[] { 3072, 3584 }),
        ("txt_norm.weight", new long[] { 3584 }),
        ("norm_out.linear.weight", new long[] { 4, 4 }),
        ("proj_out.weight", new long[] { 4, 4 }));

    private static SafetensorsHeader SdxlLayout() => Layout(
        ("model.diffusion_model.input_blocks.0.0.weight", new long[] { 4, 4 }),
        ("conditioner.embedders.0.transformer.text_model.embeddings.weight", new long[] { 4, 4 }),
        ("conditioner.embedders.1.model.ln_final.weight", new long[] { 4 }),
        ("first_stage_model.encoder.conv_in.weight", new long[] { 4, 4 }));

    private static SafetensorsHeader Flux1Layout() => Layout(
        ("double_blocks.0.img_attn.qkv.weight", new long[] { 4, 4 }),
        ("single_blocks.0.linear1.weight", new long[] { 4, 4 }),
        ("vector_in.in_layer.weight", new long[] { 4, 4 }),
        ("img_in.weight", new long[] { 4, 4 }));

    private static string MakeRegistry(string families) =>
        "{\"schemaVersion\":1,\"embeddedStems\":{\"textEncoder\":[\"text_encoders.\"],\"vae\":[\"vae.\"]},\"families\":[" + families + "]}";

    private const string DemoFamily =
        "{\"id\":\"demo\",\"displayName\":\"Demo\",\"support\":\"supported\",\"pipelines\":[\"image\"],"
        + "\"templateFile\":\"z-image-smoke-api.json\","
        + "\"mainLoader\":{\"node\":\"1\",\"classType\":\"UNETLoader\",\"input\":\"unet_name\",\"folder\":\"diffusion_models\"},"
        + "\"requiredNodes\":[\"UNETLoader\"],\"signature\":{\"requiredStems\":[\"a.\"]},"
        + "\"companions\":[{\"role\":\"text_encoder\",\"folder\":\"text_encoders\",\"fileName\":\"te.safetensors\",\"clipType\":\"demo\",\"source\":{}}]}";

    private string MakeModels(params (string Folder, string File, long Size)[] files)
    {
        string root = Path.Combine(_dir, "models-" + Guid.NewGuid().ToString("N"));
        foreach ((string folder, string file, long size) in files)
        {
            string directory = Path.Combine(root, folder);
            Directory.CreateDirectory(directory);
            using FileStream stream = File.Create(Path.Combine(directory, file));
            stream.SetLength(size);
        }
        Directory.CreateDirectory(Path.Combine(root, "diffusion_models"));
        Directory.CreateDirectory(Path.Combine(root, "text_encoders"));
        Directory.CreateDirectory(Path.Combine(root, "vae"));
        return root;
    }

    private static MediaCompanionPlan PlanFor(string type, string mainFile, SafetensorsHeader header, MediaCompanionRegistry registry, string modelsRoot)
    {
        MediaFamilyDetection detection = MediaFamilyDetector.Detect(header, registry);
        var installed = MediaCompanionResolver.ScanFolders(modelsRoot, new[] { "diffusion_models", "text_encoders", "vae" });
        return MediaCompanionResolver.BuildPlan(type, mainFile, detection, registry, installed);
    }

    // ---------- shipped manifest ----------

    [Fact]
    public void Shipped_registry_declares_exactly_the_families_this_repo_can_run()
    {
        string[] supported = MediaCompanionRegistry.Shipped.Families
            .Where(family => family.IsSupported).Select(family => family.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "chroma", "flux1-schnell", "flux2-klein", "qwen-image", "sdxl", "wan2.2-ti2v-5b", "z-image" }, supported);
    }

    [Fact]
    public void Each_media_type_maps_to_its_supported_families()
    {
        Assert.Equal(new[] { "chroma", "flux1-schnell", "qwen-image", "sdxl", "z-image" },
            MediaCompanionRegistry.Shipped.SupportedFor("image").Select(family => family.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "flux2-klein", "qwen-image" },
            MediaCompanionRegistry.Shipped.SupportedFor("edit").Select(family => family.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal("wan2.2-ti2v-5b", Assert.Single(MediaCompanionRegistry.Shipped.SupportedFor("video")).Id);
        Assert.Empty(MediaCompanionRegistry.Shipped.SupportedFor("upscale"));
    }

    [Fact]
    public void Families_that_are_not_supported_claim_no_pipeline_and_no_template()
    {
        foreach (RegistryFamily family in MediaCompanionRegistry.Shipped.Families.Where(family => !family.IsSupported))
        {
            Assert.Empty(family.Pipelines);
            Assert.Null(family.TemplateFile);
            Assert.False(string.IsNullOrWhiteSpace(family.Reason));
        }
    }

    [Fact]
    public void Shared_companions_are_declared_by_both_families()
    {
        RegistryFamily zImage = MediaCompanionRegistry.Shipped.FindFamily("z-image")!;
        RegistryFamily flux2 = MediaCompanionRegistry.Shipped.FindFamily("flux2-klein")!;

        Assert.Contains("flux2-klein", zImage.Companions.Single(c => c.FileName == "qwen_3_4b.safetensors").SharedWith);
        Assert.Contains("z-image", flux2.Companions.Single(c => c.FileName == "qwen_3_4b.safetensors").SharedWith);
    }

    [Fact]
    public void Shipped_supported_companions_are_pinned_to_an_immutable_revision_size_and_digest()
    {
        // A clean PC has none of these files, so a supported family whose companions are not pinned can never be set up.
        foreach (RegistryFamily family in MediaCompanionRegistry.Shipped.Families.Where(f => f.IsSupported))
            foreach (RegistryCompanion companion in family.Companions)
                Assert.True(companion.Source.IsPinned, family.Id + "/" + companion.FileName);
    }

    // ---------- registry validation (fail closed) ----------

    [Fact]
    public void Parse_accepts_a_minimal_valid_document()
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Parse(MakeRegistry(DemoFamily));
        Assert.Equal("demo", Assert.Single(registry.SupportedFor("image")).Id);
    }

    [Fact]
    public void Parse_rejects_an_unknown_key()
    {
        string family = DemoFamily.Replace("\"displayName\":\"Demo\"", "\"displayName\":\"Demo\",\"bogus\":1");
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(family)));
        Assert.Contains("unknown key", error.Message);
    }

    [Fact]
    public void Parse_rejects_a_supported_family_without_a_pipeline()
    {
        string family = DemoFamily.Replace("\"pipelines\":[\"image\"]", "\"pipelines\":[]");
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(family)));
        Assert.Contains("needs at least one pipeline", error.Message);
    }

    [Fact]
    public void A_pipeline_template_overrides_the_family_template_for_that_pipeline_only()
    {
        string family = DemoFamily.Replace("\"pipelines\":[\"image\"]", "\"pipelines\":[\"image\",\"edit\"]")
            .Replace("\"templateFile\":\"z-image-smoke-api.json\",",
                "\"templateFile\":\"z-image-smoke-api.json\",\"pipelineTemplates\":{\"edit\":\"flux2-klein-edit-api.json\"},");

        RegistryFamily parsed = Assert.Single(MediaCompanionRegistry.Parse(MakeRegistry(family)).Families);

        Assert.Equal("z-image-smoke-api.json", parsed.TemplateFor("image"));
        Assert.Equal("flux2-klein-edit-api.json", parsed.TemplateFor("edit"));
        Assert.Null(parsed.TemplateFor("video"));
        Assert.Equal(new[] { "z-image-smoke-api.json", "flux2-klein-edit-api.json" }, parsed.AllTemplateFiles);
    }

    [Fact]
    public void Parse_rejects_a_pipeline_template_for_a_pipeline_the_family_does_not_run()
    {
        string family = DemoFamily.Replace("\"templateFile\":\"z-image-smoke-api.json\",",
            "\"templateFile\":\"z-image-smoke-api.json\",\"pipelineTemplates\":{\"video\":\"wan-video-smoke-api.json\"},");
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(family)));
        Assert.Contains("does not run", error.Message);
    }

    [Theory]
    [InlineData("../x.json")]
    [InlineData("sub/x.json")]
    [InlineData("x.txt")]
    public void Parse_rejects_a_pipeline_template_that_is_not_a_plain_json_file_name(string file)
    {
        string family = DemoFamily.Replace("\"templateFile\":\"z-image-smoke-api.json\",",
            "\"templateFile\":\"z-image-smoke-api.json\",\"pipelineTemplates\":{\"image\":\"" + file + "\"},");
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(family)));
        Assert.Contains("plain .json file name", error.Message);
    }

    [Fact]
    public void The_minimum_comfyui_version_is_found_for_any_template_of_a_family()
    {
        string family = DemoFamily.Replace("\"pipelines\":[\"image\"]", "\"pipelines\":[\"image\",\"edit\"]")
            .Replace("\"templateFile\":\"z-image-smoke-api.json\",",
                "\"templateFile\":\"z-image-smoke-api.json\",\"minComfyUiVersion\":\"0.40.0\",\"pipelineTemplates\":{\"edit\":\"flux2-klein-edit-api.json\"},");
        MediaCompanionRegistry registry = MediaCompanionRegistry.Parse(MakeRegistry(family));

        Assert.Equal("0.40.0", registry.MinimumComfyUiVersionFor("flux2-klein-edit-api.json"));
        Assert.Equal("0.40.0", registry.MinimumComfyUiVersionFor("z-image-smoke-api.json"));
        Assert.Null(registry.MinimumComfyUiVersionFor("other.json"));
    }

    [Fact]
    public void Parse_rejects_a_mutable_revision_instead_of_a_commit_sha()
    {
        string family = DemoFamily.Replace("\"source\":{}", "\"source\":{\"repository\":\"owner/repo\",\"revision\":\"main\"}");
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(family)));
        Assert.Contains("immutable", error.Message);
    }

    [Fact]
    public void Parse_rejects_a_path_traversal_in_a_companion_file_name()
    {
        string family = DemoFamily.Replace("te.safetensors", "../evil.safetensors");
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(family)));
        Assert.Contains("plain file name", error.Message);
    }

    [Fact]
    public void Parse_rejects_a_family_that_is_not_supported_but_lists_pipelines()
    {
        string family = "{\"id\":\"nope\",\"displayName\":\"Nope\",\"support\":\"resolver_only\",\"reason\":\"not wired\","
            + "\"pipelines\":[\"image\"],\"signature\":{\"requiredStems\":[\"a.\"]},\"companions\":[]}";
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(family)));
        Assert.Contains("only a supported family may list pipelines", error.Message);
    }

    [Fact]
    public void Parse_rejects_duplicate_family_ids()
    {
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(DemoFamily + "," + DemoFamily)));
        Assert.Contains("more than once", error.Message);
    }

    [Fact]
    public void Parse_rejects_a_shared_file_that_one_family_does_not_declare()
    {
        string other = DemoFamily.Replace("\"id\":\"demo\"", "\"id\":\"other\"").Replace("\"clipType\":\"demo\"", "\"clipType\":\"other\"");
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(DemoFamily + "," + other)));
        Assert.Contains("must list each other", error.Message);
    }

    [Fact]
    public void Parse_rejects_a_shared_file_whose_sources_differ()
    {
        string other = DemoFamily.Replace("\"id\":\"demo\"", "\"id\":\"other\"").Replace("\"clipType\":\"demo\"", "\"clipType\":\"other\"")
            .Replace("\"source\":{}", "\"source\":{\"repository\":\"owner/other\"}")
            .Replace("\"fileName\":\"te.safetensors\",", "\"fileName\":\"te.safetensors\",\"sharedWith\":[\"demo\"],");
        string shared = DemoFamily.Replace("\"fileName\":\"te.safetensors\",", "\"fileName\":\"te.safetensors\",\"sharedWith\":[\"other\"],");
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(shared + "," + other)));
        Assert.Contains("different sources", error.Message);
    }

    [Fact]
    public void Parse_rejects_an_unknown_companion_role_and_a_bad_sha256()
    {
        string badRole = DemoFamily.Replace("\"role\":\"text_encoder\"", "\"role\":\"mystery\"");
        Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(badRole)));

        string badDigest = DemoFamily.Replace("\"source\":{}", "\"source\":{\"sha256\":\"ABC\"}");
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(badDigest)));
        Assert.Contains("sha256", error.Message);
    }

    // ---------- family detection from headers ----------

    [Fact]
    public void Z_image_layout_is_identified_as_the_supported_z_image_family()
    {
        MediaFamilyDetection detection = MediaFamilyDetector.Detect(ZImageLayout(), MediaCompanionRegistry.Shipped);

        Assert.Equal("z-image", detection.FamilyId);
        Assert.Equal("supported", detection.Support);
        Assert.Null(detection.Reason);
    }

    [Fact]
    public void Flux2_layout_is_identified_and_not_confused_with_flux1()
    {
        Assert.Equal("flux2-klein", MediaFamilyDetector.Detect(Flux2Layout(), MediaCompanionRegistry.Shipped).FamilyId);

        MediaFamilyDetection flux1 = MediaFamilyDetector.Detect(Flux1Layout(), MediaCompanionRegistry.Shipped);
        Assert.Equal("flux1", flux1.FamilyId);
        Assert.Equal("resolver_only", flux1.Support);
    }

    [Fact]
    public void Wan_5B_needs_48_input_channels_so_a_14B_style_layout_is_not_accepted()
    {
        Assert.Equal("wan2.2-ti2v-5b", MediaFamilyDetector.Detect(Wan5BLayout(48), MediaCompanionRegistry.Shipped).FamilyId);

        MediaFamilyDetection refused = MediaFamilyDetector.Detect(Wan5BLayout(16), MediaCompanionRegistry.Shipped);
        Assert.Null(refused.FamilyId);
        Assert.Equal("unidentified", refused.Support);
    }

    [Fact]
    public void Qwen_image_is_identified_as_a_supported_image_family_and_no_other_layout_matches_it()
    {
        MediaFamilyDetection detection = MediaFamilyDetector.Detect(QwenLayout(), MediaCompanionRegistry.Shipped);

        Assert.Equal("qwen-image", detection.FamilyId);
        Assert.Equal("supported", detection.Support);
        Assert.Contains(MediaCompanionRegistry.Shipped.SupportedFor("image"), family => family.Id == "qwen-image");
        Assert.Contains(MediaCompanionRegistry.Shipped.SupportedFor("edit"), family => family.Id == "qwen-image");
        Assert.DoesNotContain(MediaCompanionRegistry.Shipped.SupportedFor("video"), family => family.Id == "qwen-image");

        // The reverse direction: none of the other shipped layouts is mistaken for Qwen-Image.
        foreach (SafetensorsHeader other in new[] { ZImageLayout(), Flux2Layout(), Wan5BLayout(), Flux1Layout(), SdxlLayout() })
            Assert.NotEqual("qwen-image", MediaFamilyDetector.Detect(other, MediaCompanionRegistry.Shipped).FamilyId);
    }

    [Fact]
    public void A_qwen_file_saved_with_the_model_diffusion_model_prefix_is_not_identified_rather_than_guessed()
    {
        // qwen_image_2512_fp8_e4m3fn.safetensors in Comfy-Org/Qwen-Image_ComfyUI carries this prefix on all 1933 tensors.
        SafetensorsHeader prefixed = Layout(QwenLayout().Tensors
            .Select(tensor => ("model.diffusion_model." + tensor.Name, tensor.Shape)).ToArray());

        MediaFamilyDetection detection = MediaFamilyDetector.Detect(prefixed, MediaCompanionRegistry.Shipped);

        Assert.Null(detection.FamilyId);
        Assert.Equal("unidentified", detection.Support);
    }

    [Fact]
    public void Sdxl_checkpoint_reports_its_embedded_text_encoder_and_is_supported_for_images()
    {
        MediaFamilyDetection detection = MediaFamilyDetector.Detect(SdxlLayout(), MediaCompanionRegistry.Shipped);

        Assert.Equal("sdxl", detection.FamilyId);
        Assert.Equal("supported", detection.Support);
        Assert.True(detection.EmbedsTextEncoder);
        Assert.True(detection.EmbedsVae);
    }

    [Fact]
    public void A_layout_matching_two_families_is_unidentified_rather_than_guessed()
    {
        SafetensorsHeader both = Layout(
            ("noise_refiner.0.weight", new long[] { 1 }), ("context_refiner.0.weight", new long[] { 1 }),
            ("cap_embedder.0.weight", new long[] { 1 }), ("x_embedder.weight", new long[] { 1 }),
            ("blocks.0.self_attn.q.weight", new long[] { 1 }), ("patch_embedding.weight", new long[] { 1, 48, 1, 1, 1 }),
            ("text_embedding.0.weight", new long[] { 1 }), ("time_embedding.0.weight", new long[] { 1 }),
            ("head.head.weight", new long[] { 1 }));

        MediaFamilyDetection detection = MediaFamilyDetector.Detect(both, MediaCompanionRegistry.Shipped);

        Assert.Null(detection.FamilyId);
        Assert.Contains("more than one family", detection.Reason);
    }

    [Fact]
    public void Non_safetensors_files_are_not_identified_from_their_name_alone()
    {
        string gguf = Path.Combine(_dir, "model.gguf");
        File.WriteAllText(gguf, "GGUF");

        MediaFamilyDetection detection = MediaFamilyDetector.DetectFile(gguf, MediaCompanionRegistry.Shipped);

        Assert.Null(detection.FamilyId);
        Assert.Contains("Only .safetensors", detection.Reason);
    }

    // ---------- resolver against a real (temp) ComfyUI folder ----------

    [Fact]
    public void Z_image_plan_lists_only_its_own_missing_companions()
    {
        string root = MakeModels(("diffusion_models", "z_image_turbo_nvfp4.safetensors", 4));

        MediaCompanionPlan plan = PlanFor("image", "z_image_turbo_nvfp4.safetensors", ZImageLayout(), MediaCompanionRegistry.Shipped, root);

        Assert.Equal("z-image", plan.FamilyId);
        Assert.Equal(new[] { "ae.safetensors", "qwen_3_4b.safetensors" }, plan.Missing.Select(item => item.FileName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(plan.Missing, item => item.FileName.Contains("flux2", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Missing, item => item.FileName.Contains("xl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_is_installable_and_every_missing_companion_fetchable_because_the_sources_are_pinned()
    {
        string root = MakeModels(("diffusion_models", "z_image_turbo_nvfp4.safetensors", 4));

        MediaCompanionPlan plan = PlanFor("image", "z_image_turbo_nvfp4.safetensors", ZImageLayout(), MediaCompanionRegistry.Shipped, root);

        Assert.True(plan.Installable);
        Assert.Equal(2, plan.Missing.Count);
        Assert.All(plan.Missing, item => Assert.True(item.Fetchable));
    }

    [Fact]
    public void Qwen_image_plan_never_requires_an_sdxl_checkpoint_and_is_installable_from_pinned_sources()
    {
        string root = MakeModels(("diffusion_models", "qwen_image_fp8.safetensors", 4));

        MediaCompanionPlan plan = PlanFor("image", "qwen_image_fp8.safetensors", QwenLayout(), MediaCompanionRegistry.Shipped, root);

        Assert.Equal("supported", plan.Support);
        Assert.True(plan.Installable);
        Assert.Equal(new[] { "qwen_2.5_vl_7b_fp8_scaled.safetensors", "qwen_image_vae.safetensors" },
            plan.Missing.Select(item => item.FileName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(plan.Missing, item => Assert.True(item.Fetchable));
        Assert.DoesNotContain(plan.Companions, item => item.FileName.Contains("xl", StringComparison.OrdinalIgnoreCase)
            || item.FileName.Contains("sdxl", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Companions, item => item.Role == "checkpoint");
    }

    [Fact]
    public void A_shared_file_already_installed_is_recognised_for_the_other_family()
    {
        // qwen_3_4b.safetensors is shared by z-image and flux2-klein; installed once, found by exact name.
        string root = MakeModels(("diffusion_models", "z_image_turbo_nvfp4.safetensors", 4),
            ("text_encoders", "qwen_3_4b.safetensors", 8044982048));

        MediaCompanionPlan plan = PlanFor("image", "z_image_turbo_nvfp4.safetensors", ZImageLayout(), MediaCompanionRegistry.Shipped, root);

        MediaCompanionItem shared = plan.Companions.Single(item => item.FileName == "qwen_3_4b.safetensors");
        Assert.Equal("installed", shared.State);
        Assert.Equal("exact", shared.MatchedBy);
        Assert.Equal(new[] { "ae.safetensors" }, plan.Missing.Select(item => item.FileName));
    }

    [Fact]
    public void A_bare_substring_never_satisfies_a_companion()
    {
        // "my_ae_backup" contains "ae"; the VAE must still be reported missing.
        string root = MakeModels(("diffusion_models", "z_image_turbo_nvfp4.safetensors", 4),
            ("vae", "my_ae_backup.safetensors", 4));

        MediaCompanionPlan plan = PlanFor("image", "z_image_turbo_nvfp4.safetensors", ZImageLayout(), MediaCompanionRegistry.Shipped, root);

        Assert.Contains(plan.Missing, item => item.FileName == "ae.safetensors");
    }

    [Fact]
    public void Same_name_with_a_different_size_is_a_size_mismatch_when_the_pin_is_known()
    {
        string pinned = "{\"schemaVersion\":1,\"embeddedStems\":{\"textEncoder\":[],\"vae\":[]},\"families\":[" + DemoFamily
            .Replace("\"source\":{}", "\"source\":{\"repository\":\"owner/repo\",\"path\":\"split/te.safetensors\",\"sizeBytes\":1000}") + "]}";
        MediaCompanionRegistry registry = MediaCompanionRegistry.Parse(pinned);
        string root = MakeModels(("text_encoders", "te.safetensors", 999));
        var installed = MediaCompanionResolver.ScanFolders(root, new[] { "text_encoders" });
        MediaFamilyDetection detection = new(registry.Families[0].Id, "Demo", "supported", null, false, false, 1);

        MediaCompanionPlan plan = MediaCompanionResolver.BuildPlan("image", "main.safetensors", detection, registry, installed);

        Assert.Equal("size_mismatch", Assert.Single(plan.Companions).State);
        Assert.Single(plan.Missing);
    }

    [Fact]
    public void Pattern_match_is_used_only_when_declared_and_respects_exclusions()
    {
        string family = DemoFamily.Replace("\"fileName\":\"te.safetensors\",", "\"fileName\":\"te.safetensors\",\"match\":{\"pattern\":\"qwen_3_4b*.safetensors\",\"exclude\":[\"*fp4*\"]},");
        MediaCompanionRegistry registry = MediaCompanionRegistry.Parse(MakeRegistry(family));
        MediaFamilyDetection detection = new("demo", "Demo", "supported", null, false, false, 1);

        string accepted = MakeModels(("text_encoders", "qwen_3_4b_fp8.safetensors", 10));
        MediaCompanionPlan matched = MediaCompanionResolver.BuildPlan("image", "m.safetensors", detection, registry,
            MediaCompanionResolver.ScanFolders(accepted, new[] { "text_encoders" }));
        Assert.Equal("pattern", matched.Companions.Single().MatchedBy);
        Assert.Equal("installed", matched.Companions.Single().State);

        string excluded = MakeModels(("text_encoders", "qwen_3_4b_fp4.safetensors", 10));
        MediaCompanionPlan refused = MediaCompanionResolver.BuildPlan("image", "m.safetensors", detection, registry,
            MediaCompanionResolver.ScanFolders(excluded, new[] { "text_encoders" }));
        Assert.Equal("missing", refused.Companions.Single().State);

        string substring = MakeModels(("text_encoders", "old_qwen_3_4b_copy.safetensors", 10));
        MediaCompanionPlan anchored = MediaCompanionResolver.BuildPlan("image", "m.safetensors", detection, registry,
            MediaCompanionResolver.ScanFolders(substring, new[] { "text_encoders" }));
        Assert.Equal("missing", anchored.Companions.Single().State);
    }

    [Fact]
    public void Glob_matching_is_whole_name_and_case_insensitive()
    {
        Assert.True(MediaCompanionResolver.GlobMatch("qwen_3_4b*.safetensors", "QWEN_3_4B_fp8.safetensors"));
        Assert.False(MediaCompanionResolver.GlobMatch("qwen_3_4b*.safetensors", "x_qwen_3_4b.safetensors"));
        Assert.True(MediaCompanionResolver.GlobMatch("a?c.pt", "abc.pt"));
    }

    [Fact]
    public void Plan_hash_is_stable_for_the_same_state_and_changes_when_the_missing_set_changes()
    {
        string root = MakeModels(("diffusion_models", "z_image_turbo_nvfp4.safetensors", 4));
        string first = PlanFor("image", "z_image_turbo_nvfp4.safetensors", ZImageLayout(), MediaCompanionRegistry.Shipped, root).PlanHash;
        string again = PlanFor("image", "z_image_turbo_nvfp4.safetensors", ZImageLayout(), MediaCompanionRegistry.Shipped, root).PlanHash;
        Assert.Equal(first, again);

        string withVae = MakeModels(("diffusion_models", "z_image_turbo_nvfp4.safetensors", 4), ("vae", "ae.safetensors", 4));
        string changed = PlanFor("image", "z_image_turbo_nvfp4.safetensors", ZImageLayout(), MediaCompanionRegistry.Shipped, withVae).PlanHash;
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void A_family_that_cannot_run_the_requested_type_produces_no_companions()
    {
        string root = MakeModels(("diffusion_models", "z_image_turbo_nvfp4.safetensors", 4));

        MediaCompanionPlan plan = PlanFor("video", "z_image_turbo_nvfp4.safetensors", ZImageLayout(), MediaCompanionRegistry.Shipped, root);

        Assert.False(plan.Installable);
        Assert.Empty(plan.Companions);
        Assert.Contains("cannot run the video pipeline", plan.Reason);
    }

    [Fact]
    public void An_unidentified_file_gets_no_companions_and_no_install()
    {
        string root = MakeModels(("diffusion_models", "mystery.safetensors", 4));
        MediaCompanionPlan plan = PlanFor("image", "mystery.safetensors", Layout(("random.weight", new long[] { 1 })),
            MediaCompanionRegistry.Shipped, root);

        Assert.Equal("unidentified", plan.Support);
        Assert.False(plan.Installable);
        Assert.Empty(plan.Companions);
    }

    [Fact]
    public void Scan_ignores_subfolders_and_empty_files()
    {
        string root = MakeModels(("vae", "real.safetensors", 7), ("vae", "empty.safetensors", 0));
        Directory.CreateDirectory(Path.Combine(root, "vae", "nested"));

        IReadOnlyList<InstalledComfyFile> files = MediaCompanionResolver.ScanFolders(root, new[] { "vae" })["vae"];

        Assert.Equal(new[] { "real.safetensors" }, files.Select(file => file.Name));
    }

    [Fact]
    public void A_declared_companion_licence_is_carried_into_the_plan_and_an_absent_one_stays_null()
    {
        MediaCompanionRegistry withLicence = MediaCompanionRegistry.Parse(
            MakeRegistry(DemoFamily.Replace("\"clipType\":\"demo\"", "\"clipType\":\"demo\",\"license\":\"example-license-1.0\"")));
        MediaCompanionRegistry withoutLicence = MediaCompanionRegistry.Parse(MakeRegistry(DemoFamily));
        string root = MakeModels(("diffusion_models", "main.safetensors", 4));
        var detection = new MediaFamilyDetection("demo", "Demo", "supported", null, false, false, 1);
        var installed = MediaCompanionResolver.ScanFolders(root, new[] { "text_encoders" });

        MediaCompanionItem declared = Assert.Single(MediaCompanionResolver.BuildPlan("image", "main.safetensors", detection, withLicence, installed).Missing);
        MediaCompanionItem absent = Assert.Single(MediaCompanionResolver.BuildPlan("image", "main.safetensors", detection, withoutLicence, installed).Missing);

        Assert.Equal("example-license-1.0", declared.License);
        Assert.Null(absent.License);
    }

    [Fact]
    public void A_family_minimum_comfyui_version_is_validated_and_found_by_its_template()
    {
        string withMinimum = DemoFamily.Replace("\"templateFile\":\"z-image-smoke-api.json\"",
            "\"templateFile\":\"z-image-smoke-api.json\",\"minComfyUiVersion\":\"0.37.0\"");
        MediaCompanionRegistry registry = MediaCompanionRegistry.Parse(MakeRegistry(withMinimum));

        Assert.Equal("0.37.0", registry.MinimumComfyUiVersionFor("z-image-smoke-api.json"));
        Assert.Null(registry.MinimumComfyUiVersionFor("wan-video-smoke-api.json"));

        string malformed = DemoFamily.Replace("\"templateFile\":\"z-image-smoke-api.json\"",
            "\"templateFile\":\"z-image-smoke-api.json\",\"minComfyUiVersion\":\"latest\"");
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(malformed)));
        Assert.Contains("minComfyUiVersion", error.Message);
    }

    [Fact]
    public void Parse_rejects_a_shape_check_dimension_that_would_wrap_when_narrowed()
    {
        // 4294967296 wraps to 0 when cast to int, which used to pass the 0..7 range check.
        string family = DemoFamily.Replace("\"signature\":{\"requiredStems\":[\"a.\"]}",
            "\"signature\":{\"requiredStems\":[\"a.\"],\"shapeChecks\":[{\"tensor\":\"a.w\",\"dim\":4294967296,\"equals\":1}]}");
        var error = Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(MakeRegistry(family)));
        Assert.Contains("shape check dimension", error.Message);
    }
}
