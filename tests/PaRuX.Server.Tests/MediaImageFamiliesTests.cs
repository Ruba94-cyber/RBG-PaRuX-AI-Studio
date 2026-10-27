using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The SDXL (single-file), FLUX.1 schnell (single-file) and Chroma image families: how a checkpoint is recognised, which
/// graph and loader it runs with, where it is installed, and that every catalogue pin and lane claim is well formed.
/// Hermetic: synthetic safetensors headers in an isolated root; the graphs are checked against a real ComfyUI snapshot in
/// <see cref="MediaFamilyTemplateStructureTests"/>.
/// </summary>
public sealed class MediaImageFamiliesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-imgfam-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDataRoot;
    private readonly IDisposable _rootScope;

    public MediaImageFamiliesTests()
    {
        Directory.CreateDirectory(_dir);
        _previousDataRoot = PaRuXDataRoot.Path;
        PaRuXDataRoot.UseForTests(Path.Combine(_dir, "data"));
        _rootScope = LocalAiPaths.UseRootForTests(Path.Combine(_dir, "root"));
        Directory.CreateDirectory(LocalAiPaths.DiffusionModelsPath);
        Directory.CreateDirectory(LocalAiPaths.CheckpointsPath);
        ConfigAssets.EnsureAll();
    }

    public void Dispose()
    {
        _rootScope.Dispose();
        PaRuXDataRoot.UseForTests(_previousDataRoot);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void WriteHeader(string path, params string[] tensorNames)
    {
        var parts = new List<string>();
        for (int i = 0; i < tensorNames.Length; i++)
            parts.Add("\"" + tensorNames[i] + "\":{\"dtype\":\"U8\",\"shape\":[1],\"data_offsets\":[" + i + "," + (i + 1) + "]}");
        byte[] header = Encoding.UTF8.GetBytes("{" + string.Join(",", parts) + "}");
        var bytes = new byte[8 + header.Length + tensorNames.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), (ulong)header.Length);
        header.CopyTo(bytes, 8);
        File.WriteAllBytes(path, bytes);
    }

    // Tensor-name stems read from the real safetensors headers (HTTP range reads of the pinned revisions).
    private static readonly string[] SdxlNames =
    {
        "model.diffusion_model.input_blocks.0.0.weight", "conditioner.embedders.0.transformer.text_model.embeddings.position_embedding.weight",
        "conditioner.embedders.1.model.ln_final.bias", "first_stage_model.encoder.conv_in.weight"
    };
    private static readonly string[] SdxlRefinerNames =
    {
        "model.diffusion_model.input_blocks.0.0.weight", "conditioner.embedders.0.model.ln_final.bias", "first_stage_model.encoder.conv_in.weight"
    };
    private static readonly string[] FluxSchnellNames =
    {
        "model.diffusion_model.double_blocks.0.img_attn.qkv.weight", "model.diffusion_model.single_blocks.0.linear1.weight",
        "model.diffusion_model.vector_in.in_layer.weight", "text_encoders.t5xxl.transformer.encoder.block.0.layer.0.SelfAttention.q.weight",
        "text_encoders.clip_l.transformer.text_model.embeddings.token_embedding.weight", "vae.decoder.conv_in.weight"
    };
    private static readonly string[] ChromaNames =
    {
        "distilled_guidance_layer.in_proj.weight", "double_blocks.0.img_attn.qkv.weight", "single_blocks.0.linear1.weight",
        "img_in.weight", "txt_in.weight", "final_layer.linear.weight"
    };
    private static readonly string[] Flux2Names = { "double_blocks.0.w", "single_blocks.0.w", "img_in.w", "txt_in.w" };
    private static readonly string[] ZImageNames = { "noise_refiner.0.w", "context_refiner.0.w", "cap_embedder.0.w", "x_embedder.w" };

    private static string Detect(params string[] names)
    {
        string path = Path.Combine(LocalAiPaths.DiffusionModelsPath, Guid.NewGuid().ToString("N") + ".safetensors");
        WriteHeader(path, names);
        MediaFamilyDetection d = MediaFamilyDetector.DetectFile(path, MediaCompanionRegistry.Shipped);
        return d.FamilyId ?? "unidentified";
    }

    // ---- recognition ----

    [Fact]
    public void Single_file_layouts_are_recognised_and_never_confused_with_each_other()
    {
        Assert.Equal("sdxl", Detect(SdxlNames));
        Assert.Equal("flux1-schnell", Detect(FluxSchnellNames));
        Assert.Equal("chroma", Detect(ChromaNames));
        Assert.Equal("flux2-klein", Detect(Flux2Names));
        Assert.Equal("z-image", Detect(ZImageNames));
    }

    [Fact]
    public void The_sdxl_refiner_and_a_guidance_distilled_flux_dev_checkpoint_are_not_taken_for_a_runnable_family()
    {
        Assert.Equal("unidentified", Detect(SdxlRefinerNames));
        Assert.Equal("unidentified", Detect(FluxSchnellNames.Append("model.diffusion_model.guidance_in.in_layer.weight").ToArray()));
    }

    [Fact]
    public void The_embedded_families_declare_no_companions_and_chroma_pins_its_text_encoder_and_vae()
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Shipped;
        Assert.Empty(registry.FindFamily("sdxl")!.Companions);
        Assert.Empty(registry.FindFamily("flux1-schnell")!.Companions);
        RegistryFamily chroma = registry.FindFamily("chroma")!;
        Assert.All(chroma.Companions, c => Assert.True(c.Source.IsPinned, c.FileName + " must be pinned"));
        Assert.Equal("chroma", chroma.Companions.Single(c => c.Role == "text_encoder").ClipType);
        // ae.safetensors is the file Z-Image already pins; the registry insists the sources are identical.
        Assert.Equal(registry.FindFamily("z-image")!.Companions.Single(c => c.FileName == "ae.safetensors").Source,
            chroma.Companions.Single(c => c.FileName == "ae.safetensors").Source);
    }

    [Fact]
    public void A_checkpoint_loader_main_model_must_read_the_checkpoints_folder_and_nothing_else_may()
    {
        string family(string loader, string input, string folder) =>
            "{\"schemaVersion\":1,\"embeddedStems\":{\"textEncoder\":[\"text_encoders.\"],\"vae\":[\"vae.\"]},\"families\":[{\"id\":\"x\",\"displayName\":\"X\",\"support\":\"supported\","
            + "\"pipelines\":[\"image\"],\"templateFile\":\"sdxl-checkpoint-api.json\",\"mainLoader\":{\"node\":\"1\",\"classType\":\"" + loader + "\",\"input\":\"" + input
            + "\",\"folder\":\"" + folder + "\"},\"requiredNodes\":[\"KSampler\"],\"signature\":{\"requiredStems\":[\"a.\"]},\"companions\":[]}]}";
        MediaCompanionRegistry.Parse(family("CheckpointLoaderSimple", "ckpt_name", "checkpoints"));
        Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(family("CheckpointLoaderSimple", "ckpt_name", "diffusion_models")));
        Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(family("UNETLoader", "unet_name", "checkpoints")));
        Assert.Throws<InvalidDataException>(() => MediaCompanionRegistry.Parse(family("CheckpointLoaderSimple", "unet_name", "checkpoints")));
    }

    // ---- installation folders and resolution ----

    [Fact]
    public void A_recognised_single_file_checkpoint_in_the_checkpoints_folder_is_offered_but_other_files_there_are_not()
    {
        WriteHeader(Path.Combine(LocalAiPaths.CheckpointsPath, "sdxl_custom.safetensors"), SdxlNames);
        WriteHeader(Path.Combine(LocalAiPaths.CheckpointsPath, "ace_step_music.safetensors"), "model.diffusion_model.music.w");
        WriteHeader(Path.Combine(LocalAiPaths.DiffusionModelsPath, "chroma_custom.safetensors"), ChromaNames);

        List<string> files = LocalAiPaths.ListDiffusionModelCheckpoints();

        Assert.Contains("sdxl_custom.safetensors", files);
        Assert.Contains("chroma_custom.safetensors", files);
        Assert.DoesNotContain("ace_step_music.safetensors", files);
        Assert.Equal(Path.Combine(LocalAiPaths.CheckpointsPath, "sdxl_custom.safetensors"), LocalAiPaths.MainModelPath("sdxl_custom.safetensors"));
        Assert.Equal(Path.Combine(LocalAiPaths.DiffusionModelsPath, "chroma_custom.safetensors"), LocalAiPaths.MainModelPath("chroma_custom.safetensors"));
    }

    [Fact]
    public void A_download_is_placed_in_the_folder_its_family_loader_reads()
    {
        string sdxl = Path.Combine(_dir, "sdxl.safetensors"), chroma = Path.Combine(_dir, "chroma.safetensors"), zimage = Path.Combine(_dir, "z.safetensors");
        WriteHeader(sdxl, SdxlNames); WriteHeader(chroma, ChromaNames); WriteHeader(zimage, ZImageNames);

        Assert.Equal(LocalAiPaths.CheckpointsPath, MediaCheckpointResolution.DestinationFolderFor(sdxl));
        Assert.Equal(LocalAiPaths.DiffusionModelsPath, MediaCheckpointResolution.DestinationFolderFor(chroma));
        Assert.Equal(LocalAiPaths.DiffusionModelsPath, MediaCheckpointResolution.DestinationFolderFor(zimage));
    }

    [Fact]
    public void An_activated_sdxl_checkpoint_runs_the_sdxl_graph_through_checkpoint_loader_simple()
    {
        WriteHeader(Path.Combine(LocalAiPaths.CheckpointsPath, "animagine-xl-4.0.safetensors"), SdxlNames);
        LocalAiPaths.SetMediaCheckpointOverride("image", "animagine-xl-4.0.safetensors");

        MediaCheckpointEffective effective = MediaCheckpointResolution.Resolve("image");

        Assert.Equal("sdxl-checkpoint-api.json", effective.TemplateFile);
        Assert.Equal("sdxl", effective.FamilyId);
        Assert.Equal("CheckpointLoaderSimple", effective.LoaderClass);
        Assert.Equal("ckpt_name", effective.LoaderInput);
        Assert.Contains(effective.ReplacesInputs, i => i.StartsWith("CheckpointLoaderSimple.ckpt_name", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(LocalAiPaths.ConfigDir, "sdxl-checkpoint-api.json"), MediaCheckpointResolution.TemplatePathFor(effective));

        JsonObject request = JsonNode.Parse(File.ReadAllText(MediaCheckpointResolution.TemplatePathFor(effective)))!.AsObject();
        MediaImageGraph.ApplyEffective(request, effective);
        JsonObject prompt = request["prompt"]!.AsObject();
        Assert.Equal("animagine-xl-4.0.safetensors", (string?)prompt["1"]!["inputs"]!["ckpt_name"]);
        // The catalogue entry of that file carries the sampler settings its model card recommends.
        JsonObject sampler = prompt["8"]!["inputs"]!.AsObject();
        Assert.Equal(28, (int?)sampler["steps"]);
        Assert.Equal(5.0, (double?)sampler["cfg"]);
        Assert.Equal("euler_ancestral", (string?)sampler["sampler_name"]);
        Assert.StartsWith("masterpiece, high score, great score, absurdres, ", MediaImageGraph.WithPromptPrefix("a castle", effective));
        Assert.EndsWith("a castle", MediaImageGraph.WithPromptPrefix("a castle", effective));
    }

    [Fact]
    public void Resolution_reads_the_companion_free_sdxl_graph_without_asking_for_a_text_encoder_or_vae()
    {
        WriteHeader(Path.Combine(LocalAiPaths.CheckpointsPath, "sd_xl_base_1.0.safetensors"), SdxlNames);
        LocalAiPaths.SetMediaCheckpointOverride("image", "sd_xl_base_1.0.safetensors");
        MediaCheckpointEffective effective = MediaCheckpointResolution.ResolveForJob("image");
        JsonObject request = JsonNode.Parse(File.ReadAllText(MediaCheckpointResolution.TemplatePathFor(effective)))!.AsObject();
        MediaImageGraph.ApplyEffective(request, effective);

        ComfyWorkflowRunner.ApplyResolvedCompanionChoices(request, "image");

        Assert.Equal("sd_xl_base_1.0.safetensors", (string?)request["prompt"]!["1"]!["inputs"]!["ckpt_name"]);
        Assert.Empty(request["prompt"]!.AsObject().Where(p => (string?)p.Value!["class_type"] is "CLIPLoader" or "VAELoader"));
    }

    [Fact]
    public void An_activated_chroma_checkpoint_runs_the_chroma_graph_and_names_its_pinned_companions()
    {
        WriteHeader(Path.Combine(LocalAiPaths.DiffusionModelsPath, "Chroma1-HD.safetensors"), ChromaNames);
        LocalAiPaths.SetMediaCheckpointOverride("image", "Chroma1-HD.safetensors");
        MediaCheckpointEffective effective = MediaCheckpointResolution.ResolveForJob("image");
        Assert.Equal("chroma-api.json", effective.TemplateFile);
        Assert.Equal("unet_name", effective.LoaderInput);

        JsonObject request = JsonNode.Parse(File.ReadAllText(MediaCheckpointResolution.TemplatePathFor(effective)))!.AsObject();
        request["prompt"]!["2"]!["inputs"]!["clip_name"] = "wrong.safetensors";
        MediaImageGraph.ApplyEffective(request, effective);
        ComfyWorkflowRunner.ApplyResolvedCompanionChoices(request, "image");

        JsonObject prompt = request["prompt"]!.AsObject();
        Assert.Equal("Chroma1-HD.safetensors", (string?)prompt["1"]!["inputs"]!["unet_name"]);
        Assert.Equal("t5xxl_fp8_e4m3fn_scaled.safetensors", (string?)prompt["2"]!["inputs"]!["clip_name"]);
        Assert.Equal("chroma", (string?)prompt["2"]!["inputs"]!["type"]);
        Assert.Equal("ae.safetensors", (string?)prompt["3"]!["inputs"]!["vae_name"]);
    }

    [Fact]
    public void Without_an_activated_checkpoint_the_z_image_default_graph_is_unchanged()
    {
        MediaCheckpointEffective effective = MediaCheckpointResolution.Resolve("image");
        Assert.Equal("z-image-smoke-api.json", effective.TemplateFile);
        Assert.Null(effective.FamilyId);
        Assert.Equal("unet_name", effective.LoaderInput);
        Assert.Equal("a castle", MediaImageGraph.WithPromptPrefix("a castle", effective));
    }

    [Fact]
    public void Image_to_image_refuses_by_name_when_the_active_model_cannot_do_it_and_the_z_image_default_is_missing()
    {
        WriteHeader(Path.Combine(LocalAiPaths.CheckpointsPath, "sd_xl_base_1.0.safetensors"), SdxlNames);
        LocalAiPaths.SetMediaCheckpointOverride("image", "sd_xl_base_1.0.safetensors");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => MediaCheckpointResolution.ResolveForImg2Img());

        Assert.Contains("SDXL", error.Message);
        Assert.Contains("image-to-image", error.Message);
        Assert.Contains("z_image_turbo", error.Message);
    }

    [Fact]
    public void Image_to_image_uses_the_installed_z_image_default_instead_of_pushing_an_sdxl_file_into_the_graph()
    {
        WriteHeader(Path.Combine(LocalAiPaths.CheckpointsPath, "sd_xl_base_1.0.safetensors"), SdxlNames);
        LocalAiPaths.SetMediaCheckpointOverride("image", "sd_xl_base_1.0.safetensors");
        string defaultFile = (string)JsonNode.Parse(File.ReadAllText(LocalAiPaths.Img2ImgWorkflowTemplate))!["prompt"]!["1"]!["inputs"]!["unet_name"]!;
        WriteHeader(Path.Combine(LocalAiPaths.DiffusionModelsPath, defaultFile), ZImageNames);

        MediaCheckpointEffective effective = MediaCheckpointResolution.ResolveForImg2Img();

        Assert.False(effective.IsOverridden);
        Assert.Equal(defaultFile, effective.File);
        Assert.Equal(defaultFile, MediaCheckpointResolution.FileNameForImg2ImgProvenance());
        Assert.Equal("sd_xl_base_1.0.safetensors", MediaCheckpointResolution.FileNameForProvenance("image"));
    }

    // ---- catalogue pins and lanes ----

    private static readonly string[] NewFamilyIds = { "sdxl", "sdxl-anime", "realvisxl-v5", "flux1-schnell", "chroma" };

    private static IEnumerable<(MediaCatalogueFamily Family, MediaCatalogueVariant Variant)> NewVariants() =>
        MediaCatalogue.Shipped.Families.Where(f => NewFamilyIds.Contains(f.Id)).SelectMany(f => f.Variants.Select(v => (f, v)));

    [Fact]
    public void Every_new_family_is_supported_has_strengths_and_maps_to_a_registry_family_with_a_template()
    {
        foreach (string id in NewFamilyIds)
        {
            MediaCatalogueFamily family = MediaCatalogue.Shipped.Families.Single(f => f.Id == id);
            Assert.True(family.IsSupported, id);
            Assert.NotEmpty(family.DeclaredStrengths);
            RegistryFamily registry = MediaCompanionRegistry.Shipped.FindFamily(family.RegistryFamily!)!;
            Assert.NotNull(registry.TemplateFor("image"));
        }
    }

    [Fact]
    public void Every_new_variant_pins_an_exact_listing_commit_and_checksum_and_downloads_from_that_commit()
    {
        var variants = NewVariants().ToList();
        Assert.Equal(9, variants.Count);
        foreach (var (family, v) in variants)
        {
            Assert.True(v.ListingVerified, v.Id);
            Assert.Equal("exact_listing", v.SizeBasis);
            Assert.True(v.SizeBytes > 1_000_000_000, v.Id);
            Assert.Matches("^[0-9a-f]{40}$", v.Revision ?? "");
            Assert.Matches("^[0-9a-f]{64}$", v.Sha256 ?? "");
            Assert.EndsWith(".safetensors", v.FileName);
            Assert.Equal("https://huggingface.co/" + v.Repository + "/resolve/" + v.Revision + "/" + v.Path, v.DownloadUrl);
            Assert.False(string.IsNullOrWhiteSpace(v.License));
            Assert.InRange(v.Tier, 1, 5);
        }
        Assert.Equal(variants.Count, variants.Select(x => x.Variant.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Uncensored_entries_quote_the_repository_text_and_standard_entries_claim_nothing()
    {
        foreach (var (_, v) in NewVariants())
        {
            if (v.Lane == "uncensored")
            {
                Assert.False(string.IsNullOrWhiteSpace(v.LaneEvidence), v.Id);
                Assert.Contains("Repository README (" + v.Repository, v.LaneEvidence);
                Assert.Contains("\"", v.LaneEvidence);
            }
            else Assert.Null(v.LaneEvidence);
        }
        string[] uncensored = NewVariants().Where(x => x.Variant.Lane == "uncensored").Select(x => x.Variant.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "chroma1-hd-bf16", "chroma1-hd-fp8mixed", "noobai-xl-1.1", "pony-diffusion-v6-xl", "realvisxl-v5.0-fp16" }, uncensored);
        Assert.Equal(new[] { "animagine-xl-4.0", "flux1-schnell-fp8", "juggernaut-xl-v9", "sdxl-base-1.0" },
            NewVariants().Where(x => x.Variant.Lane == "standard").Select(x => x.Variant.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Every_ungated_open_licence_is_recorded_and_no_non_commercial_dev_checkpoint_is_offered()
    {
        Assert.DoesNotContain(MediaCatalogue.Shipped.Families.SelectMany(f => f.Variants), v => v.Repository!.Contains("FLUX.1-dev", StringComparison.OrdinalIgnoreCase)
            || v.FileName.Contains("flux1-dev", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Apache-2.0", MediaCatalogue.Shipped.Families.Single(f => f.Id == "flux1-schnell").Variants.Single().License);
        Assert.All(MediaCatalogue.Shipped.Families.Single(f => f.Id == "chroma").Variants, v => Assert.Equal("Apache-2.0", v.License));
    }

    [Fact]
    public void Default_checkpoint_names_in_each_new_template_match_a_catalogue_variant_of_that_family()
    {
        (string template, string input, string family)[] defaults =
        {
            ("sdxl-checkpoint-api.json", "ckpt_name", "sdxl"), ("flux1-schnell-api.json", "ckpt_name", "flux1-schnell"), ("chroma-api.json", "unet_name", "chroma")
        };
        foreach (var (template, input, familyId) in defaults)
        {
            string file = (string)JsonNode.Parse(ConfigAssets.ReadEmbedded(template)!)!["prompt"]!["1"]!["inputs"]![input]!;
            Assert.Contains(MediaCatalogue.Shipped.Families.Where(f => f.Id == familyId).SelectMany(f => f.Variants), v => v.FileName == file);
        }
    }

    [Fact]
    public void Sampler_scheduler_and_clip_type_names_exist_on_the_real_comfyui_snapshot()
    {
        JsonObject combos = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "comfy-combo-options.json")))!.AsObject();
        string[] Options(string key) => combos[key]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        string[] samplers = Options("KSampler.sampler_name"), schedulers = Options("KSampler.scheduler"), clipTypes = Options("CLIPLoader.type");

        foreach (string template in new[] { "sdxl-checkpoint-api.json", "flux1-schnell-api.json", "chroma-api.json" })
        {
            JsonObject sampler = JsonNode.Parse(ConfigAssets.ReadEmbedded(template)!)!["prompt"]!["8"]!["inputs"]!.AsObject();
            Assert.Contains((string)sampler["sampler_name"]!, samplers);
            Assert.Contains((string)sampler["scheduler"]!, schedulers);
        }
        foreach (var (_, v) in NewVariants().Where(x => x.Variant.Sampling is not null))
        {
            if (v.Sampling!.Sampler is { } s) Assert.Contains(s, samplers);
            if (v.Sampling.Scheduler is { } sc) Assert.Contains(sc, schedulers);
        }
        Assert.Contains("chroma", clipTypes);
        foreach (RegistryFamily family in MediaCompanionRegistry.Shipped.Families.Where(f => f.IsSupported))
            foreach (RegistryCompanion c in family.Companions.Where(c => c.ClipType is not null))
                Assert.Contains(c.ClipType!, clipTypes);
    }

    [Fact]
    public void A_catalogue_prefix_never_leaves_the_safe_tag_in_front_of_an_uncensored_prompt()
    {
        foreach (var (_, v) in NewVariants().Where(x => x.Variant.Lane == "uncensored" && x.Variant.PromptPrefix is not null))
            Assert.DoesNotMatch(new Regex(@"(^|,\s*)(safe|sfw|rating_safe)(\s*,|$)", RegexOptions.IgnoreCase), v.PromptPrefix!);
    }

    [Fact]
    public void The_standard_automatic_pick_for_image_generation_is_still_z_image_when_it_is_installed_alongside_the_new_families()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "z_image_turbo_bf16.safetensors", "flux1-schnell-fp8.safetensors", "sd_xl_base_1.0.safetensors", "Juggernaut-XL_v9_RunDiffusionPhoto_v2.safetensors"
        };
        var hw = new MediaHardware(24L * 1024 * 1024 * 1024, 64L * 1024 * 1024 * 1024);
        MediaSelection s = MediaSelectionEngine.Select(MediaCatalogue.Shipped, "image.generate", "standard", hw, installed, _ => null);
        Assert.Equal("z_image_turbo_bf16.safetensors", s.Automatic!.Variant.FileName);
    }
}
