using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The video runner is driven by a per-family <see cref="VideoFamilyContract"/>, chosen by the family of the active video
/// checkpoint. These tests prove that the choice follows the checkpoint, that Wan 2.2 5B still gets exactly the graph and clip
/// plan it always got, that each family's templates honour their contract, and that the pins around LTX-Video are well formed.
/// Hermetic: no ComfyUI, GPU or network.
/// </summary>
public sealed class VideoFamilyContractTests
{
    private static JsonObject Template(string file) =>
        JsonNode.Parse(ConfigAssets.ReadEmbedded(file) ?? throw new InvalidDataException(file + " is not embedded."))!.AsObject();

    private static MediaCheckpointEffective Effective(string templateFile, string file = "model.safetensors", bool overridden = true) =>
        new("video", templateFile, file, overridden, new[] { "loader" }, null);

    // ---- selection by the family of the active checkpoint ----

    [Fact]
    public void The_resolved_family_id_wins_and_ltx_patches_ckpt_name_not_unet_name()
    {
        MediaCheckpointEffective ltx = new("video", "ltxv-t2v-api.json", "my-ltx.safetensors", true, new[] { "x" }, null,
            FamilyId: "ltx-video", LoaderNode: "1", LoaderClass: "CheckpointLoaderSimple", LoaderInput: "ckpt_name");
        Assert.Same(VideoFamilyContracts.Ltx, VideoFamilyContracts.For(ltx));
        JsonObject graph = Template("ltxv-t2v-api.json");
        VideoFamilyContracts.Ltx.ApplyCheckpoint(graph, ltx);
        Assert.Equal("my-ltx.safetensors", (string?)graph["prompt"]!["1"]!["inputs"]!["ckpt_name"]);
        Assert.Null(graph["prompt"]!["1"]!["inputs"]!["unet_name"]);
    }

    [Fact]
    public void The_contract_follows_the_template_of_the_family_the_checkpoint_belongs_to()
    {
        Assert.Same(VideoFamilyContracts.Wan, VideoFamilyContracts.For(Effective("wan-video-smoke-api.json", overridden: false)));
        Assert.Same(VideoFamilyContracts.Ltx, VideoFamilyContracts.For(Effective("ltxv-t2v-api.json")));
        Assert.Same(VideoFamilyContracts.Ltx, VideoFamilyContracts.For(Effective("ltxv-i2v-api.json")));
    }

    [Theory]
    [InlineData("z-image-smoke-api.json")]
    [InlineData("qwen-image-edit-api.json")]
    [InlineData("not-a-template.json")]
    public void A_template_no_video_family_runs_is_refused_not_guessed(string template) =>
        Assert.Throws<InvalidOperationException>(() => VideoFamilyContracts.For(Effective(template)));

    [Fact]
    public void Every_supported_video_family_has_a_contract_that_matches_its_registry_entry()
    {
        IReadOnlyList<RegistryFamily> families = MediaCompanionRegistry.Shipped.SupportedFor("video");
        Assert.Contains(families, f => f.Id == "wan2.2-ti2v-5b");
        Assert.Contains(families, f => f.Id == "ltx-video");
        foreach (RegistryFamily family in families)
        {
            VideoFamilyContract contract = Assert.IsType<VideoFamilyContract>(VideoFamilyContracts.ForFamilyId(family.Id));
            Assert.Equal(family.MainLoader!.Node, contract.LoaderNode);
            Assert.Equal(family.MainLoader.ClassType, contract.LoaderClass);
            Assert.Equal(family.MainLoader.Input, contract.LoaderInput);
            // A single-file checkpoint is read by CheckpointLoaderSimple from ComfyUI's checkpoints folder.
            Assert.Equal(contract.LoaderClass == "CheckpointLoaderSimple" ? "checkpoints" : "diffusion_models", family.MainLoader.Folder);
            Assert.Equal(contract.StartImageMode == VideoStartImageMode.SeparateTemplate, family.StartImageTemplate is not null);
        }
        Assert.Equal(VideoFamilyContracts.All.Count, VideoFamilyContracts.All.Select(c => c.FamilyId).Distinct().Count());
    }

    [Fact]
    public void A_real_ltx_checkpoint_header_is_identified_as_ltx_and_nothing_else_claims_it_or_its_neighbours()
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Shipped;
        MediaFamilyDetection ltx = MediaFamilyDetector.Detect(RealLtxHeader(), registry);
        Assert.Equal("ltx-video", ltx.FamilyId);
        Assert.Equal("supported", ltx.Support);
        Assert.True(ltx.EmbedsVae);

        // The family -> template -> contract chain the runner follows for an activated LTX file.
        RegistryFamily family = registry.FindFamily(ltx.FamilyId!)!;
        Assert.Same(VideoFamilyContracts.Ltx, VideoFamilyContracts.For(Effective(family.TemplateFor("video")!)));

        // A Wan 5B layout is still Wan, and an LTX 13B layout (4096-wide patchify) is not claimed as the 2B family.
        var wan = new SafetensorsHeader(1, 1, new[]
        {
            new SafetensorsTensor("blocks.0.self_attn.q.weight", "BF16", new long[] { 3072, 3072 }, 0, 1),
            new SafetensorsTensor("patch_embedding.weight", "BF16", new long[] { 3072, 48, 1, 2, 2 }, 0, 1),
            new SafetensorsTensor("text_embedding.0.weight", "BF16", new long[] { 3072, 4096 }, 0, 1),
            new SafetensorsTensor("time_embedding.0.weight", "BF16", new long[] { 3072, 256 }, 0, 1),
            new SafetensorsTensor("head.head.weight", "BF16", new long[] { 192, 3072 }, 0, 1)
        }, new Dictionary<string, string>());
        Assert.Equal("wan2.2-ti2v-5b", MediaFamilyDetector.Detect(wan, registry).FamilyId);

        SafetensorsHeader wide = new(1, 1, RealLtxHeader().Tensors.Select(t =>
            t.Name == "model.diffusion_model.patchify_proj.weight" ? t with { Shape = new long[] { 4096, 128 } } : t).ToList(),
            new Dictionary<string, string>());
        Assert.Null(MediaFamilyDetector.Detect(wide, registry).FamilyId);
    }

    private static SafetensorsHeader RealLtxHeader()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ltx-video-2b-v0.9.5-header.json");
        JsonObject doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var tensors = doc["tensors"]!.AsArray().Select(node => new SafetensorsTensor(
            node!["name"]!.GetValue<string>(), node["dtype"]!.GetValue<string>(),
            node["shape"]!.AsArray().Select(d => d!.GetValue<long>()).ToArray(), 0, 1)).ToList();
        Assert.True(doc["fullTensorCount"]!.GetValue<int>() >= 900);
        return new SafetensorsHeader(1, 1, tensors, new Dictionary<string, string>());
    }

    // ---- Wan 2.2 5B behaves exactly as before ----

    [Fact]
    public void Wan_clip_planning_is_the_legacy_algorithm_for_every_length()
    {
        for (int requested = 1; requested <= 600; requested++)
        {
            Assert.Equal(LegacyWanPlan(requested), ComfyWorkflowRunner.PlanWanClipFrameCounts(requested));
            Assert.Equal(LegacyWanPlan(requested), VideoFamilyContracts.Wan.PlanClipFrameCounts(requested));
        }
        for (int requested = -3; requested <= 80; requested++)
            Assert.Equal(1 + (int)(4 * Math.Ceiling((Math.Clamp(requested, 5, 49) - 1) / 4.0)), ComfyWorkflowRunner.NormalizeWanFrameCount(requested));
    }

    private static int[] LegacyWanPlan(int requestedFrames)
    {
        static int Normalize(int requested) => 1 + (int)(4 * Math.Ceiling((Math.Clamp(requested, 5, 49) - 1) / 4.0));
        int remaining = Math.Max(1, requestedFrames);
        var counts = new List<int>();
        while (remaining > 0)
        {
            int clip = Normalize(Math.Min(49, remaining + (counts.Count == 0 ? 0 : 1)));
            counts.Add(clip);
            remaining -= clip - (counts.Count == 1 ? 0 : 1);
        }
        return counts.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wan_graph_filled_through_the_contract_is_identical_to_the_hand_patched_legacy_graph(bool withStartImage)
    {
        JsonObject legacy = Template("wan-video-smoke-api.json");
        JsonObject prompt = legacy["prompt"]!.AsObject();
        prompt["4"]!["inputs"]!["text"] = "a fox in snow";
        prompt["8"]!["inputs"]!["seed"] = 123456789012L;
        prompt["6"]!["inputs"]!["width"] = 1280;
        prompt["6"]!["inputs"]!["height"] = 704;
        prompt["6"]!["inputs"]!["length"] = 49;
        prompt["8"]!["inputs"]!["steps"] = 30;
        prompt["10"]!["inputs"]!["fps"] = 24.0;
        prompt["11"]!["inputs"]!["filename_prefix"] = "LocalAI_videos/PaRuX_job_clip_2";
        if (withStartImage) ComfyWorkflowRunner.AddWanStartImage(legacy, "continuity.png");
        prompt["1"]!["inputs"]!["unet_name"] = "my-wan.safetensors";

        JsonObject viaContract = Template("wan-video-smoke-api.json");
        VideoFamilyContracts.Wan.ApplyClip(viaContract, new VideoClipSettings(
            "a fox in snow", 123456789012L, 1280, 704, 49, 30, 24, "job", 1, withStartImage ? "continuity.png" : null));
        VideoFamilyContracts.Wan.ApplyCheckpoint(viaContract, Effective("wan-video-smoke-api.json", "my-wan.safetensors"));

        Assert.Equal(legacy.ToJsonString(), viaContract.ToJsonString());
        // The test seam the recovery tests use is the same patch.
        JsonObject seam = Template("wan-video-smoke-api.json");
        ComfyWorkflowRunner.SetVideoPromptAndSeed(seam, "a fox in snow", 123456789012L);
        Assert.Equal("a fox in snow", (string?)seam["prompt"]!["4"]!["inputs"]!["text"]);
        Assert.Equal(123456789012L, (long?)seam["prompt"]!["8"]!["inputs"]!["seed"]);
    }

    [Fact]
    public void Wan_keeps_the_sizes_the_job_asked_for_and_keeps_its_loader()
    {
        Assert.Equal((1280, 720), VideoFamilyContracts.Wan.FitSize(1280, 720));
        Assert.Equal((264, 248), VideoFamilyContracts.Wan.FitSize(264, 248));
        // Without an override the template's own checkpoint stays.
        JsonObject graph = Template("wan-video-smoke-api.json");
        VideoFamilyContracts.Wan.ApplyCheckpoint(graph, Effective("wan-video-smoke-api.json", "other.safetensors", overridden: false));
        Assert.Equal("wan2.2_ti2v_5B_fp16.safetensors", (string?)graph["prompt"]!["1"]!["inputs"]!["unet_name"]);
    }

    // ---- LTX-Video clip planning and sizes ----

    [Theory]
    [InlineData(1, 9)]
    [InlineData(9, 9)]
    [InlineData(10, 17)]
    [InlineData(97, 97)]
    [InlineData(500, 97)]
    public void Ltx_clip_lengths_are_eight_n_plus_one_within_nine_and_ninety_seven(int requested, int expected)
    {
        Assert.Equal(expected, VideoFamilyContracts.Ltx.NormalizeFrameCount(requested));
    }

    [Fact]
    public void Ltx_plans_cover_the_requested_frames_with_one_repeated_start_frame_per_later_clip()
    {
        for (int requested = 1; requested <= 800; requested++)
        {
            int[] plan = VideoFamilyContracts.Ltx.PlanClipFrameCounts(requested);
            Assert.All(plan, count => { Assert.InRange(count, 9, 97); Assert.Equal(1, count % 8); });
            int unique = plan.Sum() - (plan.Length - 1);
            Assert.True(unique >= requested, $"{requested} frames asked, {unique} planned.");
            // No clip is wasted: dropping the last clip must fall short.
            if (plan.Length > 1) Assert.True(plan.Take(plan.Length - 1).Sum() - (plan.Length - 2) < requested);
        }
        Assert.Equal(new[] { 97 }, VideoFamilyContracts.Ltx.PlanClipFrameCounts(97));
        Assert.Equal(new[] { 97, 9 }, VideoFamilyContracts.Ltx.PlanClipFrameCounts(97 + 1));
    }

    [Theory]
    [InlineData(1280, 720, 1280, 704)]
    [InlineData(768, 512, 768, 512)]
    [InlineData(256, 256, 256, 256)]
    [InlineData(264, 264, 256, 256)]
    public void Ltx_sizes_are_rounded_down_to_a_multiple_of_32(int width, int height, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), VideoFamilyContracts.Ltx.FitSize(width, height));

    // ---- template integrity against each contract ----

    public static IEnumerable<object[]> ContractTemplates() =>
        VideoFamilyContracts.All.SelectMany(contract =>
        {
            RegistryFamily family = VideoFamilyContracts.RegistryFamilyOf(contract);
            return new[]
            {
                new object[] { contract.FamilyId, false },
                new object[] { contract.FamilyId, true }
            };
        });

    [Theory]
    [MemberData(nameof(ContractTemplates))]
    public void Every_node_the_contract_patches_exists_in_the_template_and_reaches_the_saved_video(string familyId, bool withStartImage)
    {
        VideoFamilyContract contract = VideoFamilyContracts.ForFamilyId(familyId)!;
        RegistryFamily family = VideoFamilyContracts.RegistryFamilyOf(contract);
        string file = contract.TemplateFileFor(family, withStartImage);
        JsonObject request = Template(file);
        JsonObject graph = request["prompt"]!.AsObject();
        JsonObject specs = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "comfy-node-specs.json")))!["nodes"]!.AsObject();

        // The loader is where the checkpoint is patched and where the registry reads the main file.
        Assert.Equal(contract.LoaderClass, (string?)graph[contract.LoaderNode]!["class_type"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)graph[contract.LoaderNode]!["inputs"]![contract.LoaderInput]));
        Assert.Equal("SaveVideo", (string?)graph[contract.OutputNode]!["class_type"]);

        var clip = new VideoClipSettings("PROMPT-MARK", 424242L, 640, 480, 17, 11, 12, "0123456789abcdef0123456789abcdef", 2,
            withStartImage ? "start-mark.png" : null);
        contract.ApplyClip(request, clip);

        foreach (VideoNodeInput target in contract.Prompt.Concat(contract.Seed).Concat(contract.Width).Concat(contract.Height)
                     .Concat(contract.Length).Concat(contract.Steps).Concat(contract.Fps).Append(contract.FilenamePrefix))
        {
            JsonObject node = Assert.IsType<JsonObject>(graph[target.Node]);
            string classType = (string)node["class_type"]!;
            JsonObject spec = specs[classType]!.AsObject();
            Assert.True(spec["required"]!.AsArray().Concat(spec["optional"]!.AsArray()).Any(i => (string?)i == target.Input),
                $"{classType} (node {target.Node}) has no input {target.Input} on the real server.");
            Assert.NotNull(node["inputs"]![target.Input]);
        }
        Assert.Equal("PROMPT-MARK", (string?)graph[contract.Prompt[0].Node]!["inputs"]![contract.Prompt[0].Input]);
        Assert.Equal(424242L, (long?)graph[contract.Seed[0].Node]!["inputs"]![contract.Seed[0].Input]);
        Assert.Equal(640, (int?)graph[contract.Width[0].Node]!["inputs"]![contract.Width[0].Input]);
        Assert.Equal(480, (int?)graph[contract.Height[0].Node]!["inputs"]![contract.Height[0].Input]);
        Assert.Equal(17, (int?)graph[contract.Length[0].Node]!["inputs"]![contract.Length[0].Input]);
        Assert.Equal(11, (int?)graph[contract.Steps[0].Node]!["inputs"]![contract.Steps[0].Input]);
        Assert.All(contract.Fps, f => Assert.Equal(12.0, (double?)graph[f.Node]!["inputs"]![f.Input]));
        Assert.Equal("LocalAI_videos/PaRuX_0123456789abcdef0123456789abcdef_clip_3",
            (string?)graph[contract.FilenamePrefix.Node]!["inputs"]![contract.FilenamePrefix.Input]);

        // Every patched node really feeds the saved video, so patching it changes the picture.
        foreach (VideoNodeInput target in contract.Prompt.Concat(contract.Seed).Concat(contract.Width).Concat(contract.Length).Concat(contract.Steps).Concat(contract.Fps))
            Assert.True(Feeds(graph, target.Node, contract.OutputNode), $"node {target.Node} does not feed {contract.OutputNode} in {file}.");

        // The start image reaches the graph through the contract's mechanism and feeds the saved video.
        string[] loadImages = graph.Where(p => (string?)p.Value!["class_type"] == "LoadImage").Select(p => p.Key).ToArray();
        if (withStartImage)
        {
            string loadNode = Assert.Single(loadImages);
            Assert.Equal("start-mark.png", (string?)graph[loadNode]!["inputs"]!["image"]);
            Assert.True(Feeds(graph, loadNode, contract.OutputNode));
            if (contract.StartImageLoadNode != null) Assert.Equal(contract.StartImageLoadNode, loadNode);
        }
        else Assert.Empty(loadImages);
    }

    private static bool Feeds(JsonObject graph, string from, string to)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Walk(string id)
        {
            if (id == from) return true;
            if (!seen.Add(id) || graph[id] is null) return false;
            return graph[id]!["inputs"]!.AsObject().Any(p => p.Value is JsonArray { Count: 2 } link && Walk(link[0]!.GetValue<string>()));
        }
        return Walk(to);
    }

    [Fact]
    public void Ltx_start_image_uses_its_own_template_and_the_text_template_has_no_image_node()
    {
        RegistryFamily ltx = MediaCompanionRegistry.Shipped.FindFamily("ltx-video")!;
        Assert.Equal("ltxv-t2v-api.json", VideoFamilyContracts.Ltx.TemplateFileFor(ltx, hasStartImage: false));
        Assert.Equal("ltxv-i2v-api.json", VideoFamilyContracts.Ltx.TemplateFileFor(ltx, hasStartImage: true));
        RegistryFamily wan = MediaCompanionRegistry.Shipped.FindFamily("wan2.2-ti2v-5b")!;
        Assert.Equal("wan-video-smoke-api.json", VideoFamilyContracts.Wan.TemplateFileFor(wan, hasStartImage: false));
        Assert.Equal("wan-video-smoke-api.json", VideoFamilyContracts.Wan.TemplateFileFor(wan, hasStartImage: true));

        JsonObject image = Template("ltxv-i2v-api.json")["prompt"]!.AsObject();
        Assert.Equal("LTXVImgToVideo", (string?)image["6"]!["class_type"]);
        Assert.Equal("14", image["6"]!["inputs"]!["image"]![0]!.GetValue<string>());
        // The conditioned latent, not an empty one, is what is scheduled and sampled.
        Assert.Equal(2, image["8"]!["inputs"]!["latent_image"]![1]!.GetValue<int>());
        Assert.Equal(2, image["12"]!["inputs"]!["latent"]![1]!.GetValue<int>());
        Assert.Equal("EmptyLTXVLatentVideo", (string?)Template("ltxv-t2v-api.json")["prompt"]!["6"]!["class_type"]);
    }

    [Fact]
    public void Ltx_graph_follows_the_official_comfyui_ltxv_template_settings()
    {
        // Values from ComfyUI's own ltxv_text_to_video / ltxv_image_to_video templates (comfyui_workflow_templates_json):
        // euler, LTXVScheduler 30 steps / max_shift 2.05 / base_shift 0.95 / stretch / terminal 0.1, SamplerCustom CFG 3,
        // LTXVImgToVideo strength 0.15, T5-XXL loaded with CLIP type ltxv, 768x512 and 97 frames.
        foreach (string file in new[] { "ltxv-t2v-api.json", "ltxv-i2v-api.json" })
        {
            JsonObject graph = Template(file)["prompt"]!.AsObject();
            Assert.Equal("euler", (string?)graph["13"]!["inputs"]!["sampler_name"]);
            JsonObject sigmas = graph["12"]!["inputs"]!.AsObject();
            Assert.Equal(30, (int?)sigmas["steps"]);
            Assert.Equal(2.05, (double?)sigmas["max_shift"]!);
            Assert.Equal(0.95, (double?)sigmas["base_shift"]!);
            Assert.True((bool?)sigmas["stretch"]);
            Assert.Equal(0.1, (double?)sigmas["terminal"]!);
            Assert.Equal(3.0, (double?)graph["8"]!["inputs"]!["cfg"]!);
            Assert.True((bool?)graph["8"]!["inputs"]!["add_noise"]);
            Assert.Equal("ltxv", (string?)graph["2"]!["inputs"]!["type"]);
            Assert.Equal(768, (int?)graph["6"]!["inputs"]!["width"]);
            Assert.Equal(512, (int?)graph["6"]!["inputs"]!["height"]);
            Assert.Equal(97, (int?)graph["6"]!["inputs"]!["length"]);
            // The negative branch is real: CFG 3 needs one, and the conditioning node feeds both sampler inputs.
            Assert.Equal("7", graph["8"]!["inputs"]!["positive"]![0]!.GetValue<string>());
            Assert.Equal(1, graph["8"]!["inputs"]!["negative"]![1]!.GetValue<int>());
        }
        Assert.Equal(0.15, (double?)Template("ltxv-i2v-api.json")["prompt"]!["6"]!["inputs"]!["strength"]!);
    }

    // ---- LTX companions, registry pins and catalogue ----

    [Fact]
    public void Ltx_registry_entry_pins_one_text_encoder_and_no_separate_vae()
    {
        RegistryFamily ltx = MediaCompanionRegistry.Shipped.FindFamily("ltx-video")!;
        Assert.True(ltx.IsSupported);
        Assert.Equal(new[] { "video" }, ltx.Pipelines);
        RegistryCompanion encoder = Assert.Single(ltx.Companions);
        Assert.Equal("text_encoder", encoder.Role);
        Assert.Equal("ltxv", encoder.ClipType);
        Assert.Equal("t5xxl_fp8_e4m3fn_scaled.safetensors", encoder.FileName);
        Assert.True(encoder.Source.IsPinned);
        Assert.Equal("comfyanonymous/flux_text_encoders", encoder.Source.Repository);
        Assert.Matches("^[0-9a-f]{40}$", encoder.Source.Revision!);
        Assert.Matches("^[0-9a-f]{64}$", encoder.Source.Sha256!);
        Assert.Equal(5157348688L, encoder.Source.SizeBytes);
        Assert.Equal("apache-2.0", encoder.License);
        // The template names the pinned encoder, so a fresh install runs without patching any name.
        Assert.Equal(encoder.FileName, (string?)Template("ltxv-t2v-api.json")["prompt"]!["2"]!["inputs"]!["clip_name"]);
        Assert.Equal(encoder.FileName, (string?)Template("ltxv-i2v-api.json")["prompt"]!["2"]!["inputs"]!["clip_name"]);
    }

    [Fact]
    public void Ltx_graph_resolves_through_the_companion_resolver_without_a_vae_companion()
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Shipped;
        MediaFamilyDetection detection = MediaFamilyDetector.Detect(RealLtxHeader(), registry);
        foreach (string file in new[] { "ltxv-t2v-api.json", "ltxv-i2v-api.json" })
        {
            JsonObject request = Template(file);
            var installed = new Dictionary<string, IReadOnlyList<InstalledComfyFile>>
            {
                ["text_encoders"] = new[] { new InstalledComfyFile("t5xxl_fp8_e4m3fn_scaled.safetensors", 5157348688L) }
            };
            MediaCompanionPlan plan = MediaCompanionGraph.PrepareSubmissionGraph(request, "video", detection, registry, installed);
            Assert.Equal("ltx-video", plan.FamilyId);
            Assert.Equal("ltx-video-2b-v0.9.5.safetensors", plan.MainFileName);
            Assert.Empty(plan.Missing);
            Assert.Equal("ltxv", (string?)request["prompt"]!["2"]!["inputs"]!["type"]);
        }

        // Without the encoder the plan says what is missing and that PaRuX can fetch it.
        MediaCompanionPlan missing = MediaCompanionResolver.BuildPlan("video", "ltx-video-2b-v0.9.5.safetensors", detection, registry,
            new Dictionary<string, IReadOnlyList<InstalledComfyFile>>());
        MediaCompanionItem item = Assert.Single(missing.Missing);
        Assert.Equal("text_encoders", item.Folder);
        Assert.True(item.Fetchable);
        Assert.True(missing.Installable);
        Assert.True(missing.EmbedsVae);
    }

    [Fact]
    public void Ltx_catalogue_entry_is_supported_pinned_and_carries_the_verified_licence()
    {
        MediaCatalogueFamily family = MediaCatalogue.Shipped.Families.Single(f => f.Id == "ltx-video");
        Assert.True(family.IsSupported);
        Assert.Equal("video.generate", family.Capability);
        Assert.Equal("ltx-video", family.RegistryFamily);
        Assert.Null(family.Reason);
        Assert.Contains("fast", family.Strengths);
        Assert.Contains("cinematic", family.Strengths);

        MediaCatalogueVariant variant = Assert.Single(family.Variants);
        Assert.Equal("Lightricks/LTX-Video", variant.Repository);
        Assert.Equal("8984fa25007f376c1a299016d0957a37a2f797bb", variant.Revision);
        Assert.Equal("720d15c9f19f7d0f6b2a92bbbc34410e2cfb2f6856a100b38f734fbf973d4adf", variant.Sha256);
        Assert.Equal(6340729500L, variant.SizeBytes);
        Assert.True(variant.ListingVerified);
        Assert.Equal("standard", variant.Lane);
        Assert.Contains("OpenRAIL-M", variant.License);
        Assert.Equal("https://huggingface.co/Lightricks/LTX-Video/resolve/8984fa25007f376c1a299016d0957a37a2f797bb/ltx-video-2b-v0.9.5.safetensors", variant.DownloadUrl);
        // The default fileName is the one the templates ship, and the text encoder weight matches the pinned companion.
        Assert.Equal((string?)Template("ltxv-t2v-api.json")["prompt"]!["1"]!["inputs"]!["ckpt_name"], variant.FileName);
        Assert.Equal(5.2, family.TextEncoderGb, 1);
        Assert.Equal(0.0, family.VaeGb);
    }

    [Fact]
    public void Ltx_fits_a_sixteen_gigabyte_card_without_offload_for_the_default_job()
    {
        MediaCatalogueFamily family = MediaCatalogue.Shipped.Families.Single(f => f.Id == "ltx-video");
        MediaCatalogueVariant variant = family.Variants.Single();
        MediaJobShape job = MediaCatalogue.Shipped.CapabilityOf("video.generate")!.DefaultJob;
        var input = new MediaFitInput("video.generate", variant.Precision, family.ParametersBillion, variant.SizeBytes,
            (long)(family.TextEncoderGb * 1e9), (long)(family.VaeGb * 1e9), job.Megapixels, job.Frames, job.Seconds);
        MediaFitResult fit = MediaFitEstimator.Estimate(input, new MediaHardware(16L * 1024 * 1024 * 1024, 32L * 1024 * 1024 * 1024));
        Assert.Equal(MediaFitClass.Fits, fit.Class);
    }

    // ---- job record ----

    [Fact]
    public void A_saved_job_without_a_video_family_is_a_wan_job_and_a_new_one_records_its_family()
    {
        var legacy = new MediaJobRecord { Id = "j", Type = "video" };
        string legacyJson = JsonSerializer.Serialize(legacy);
        Assert.DoesNotContain("VideoFamilyId", legacyJson);
        Assert.Null(JsonSerializer.Deserialize<MediaJobRecord>(legacyJson)!.VideoFamilyId);
        Assert.Same(VideoFamilyContracts.Wan, VideoFamilyContracts.ForFamilyId(legacy.VideoFamilyId ?? VideoFamilyContracts.Wan.FamilyId));

        var ltx = new MediaJobRecord { Id = "j", Type = "video", VideoFamilyId = "ltx-video" };
        Assert.Equal("ltx-video", JsonSerializer.Deserialize<MediaJobRecord>(JsonSerializer.Serialize(ltx))!.VideoFamilyId);
        Assert.Same(VideoFamilyContracts.Ltx, VideoFamilyContracts.ForFamilyId("ltx-video"));
        Assert.Null(VideoFamilyContracts.ForFamilyId("unknown"));
    }
}
