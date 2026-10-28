using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Mask inpainting (request checks, graph contract) and the multi-LoRA stack (validation, injection, routes).</summary>
public sealed class MediaInpaintAndLoraStackTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public MediaInpaintAndLoraStackTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>A header-only PNG: enough for the size reader the server uses, never decoded.</summary>
    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes);

    private static InstalledLora Lora(string name, string? baseModel = null) =>
        new(name, 1024, DateTime.UtcNow, baseModel, Array.Empty<string>(), null);

    private static MediaLoraSelection Pick(string name, double strength = 0.8) => new() { Name = name, Strength = strength };

    private static JsonObject Template(string file) => JsonNode.Parse(ConfigAssets.ReadEmbedded(file)!)!.AsObject();

    // ---- LoRA validation ----------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_stack_is_accepted_and_adds_nothing()
    {
        MediaLoraStackCheck check = MediaLoraStack.Validate(null, Array.Empty<InstalledLora>(), "zimage", "z.safetensors");
        Assert.True(check.Ok);
        Assert.Empty(check.Loras);
    }

    [Fact]
    public void More_than_three_loras_are_refused_with_the_limit_in_the_reason()
    {
        var installed = Enumerable.Range(1, 4).Select(i => Lora("l" + i + ".safetensors", "Z-Image Turbo")).ToList();
        MediaLoraStackCheck check = MediaLoraStack.Validate(installed.Select(l => Pick(l.Name)).ToList(), installed, "zimage", null);
        Assert.False(check.Ok);
        Assert.Equal("lora_too_many", check.Code);
        Assert.Contains("at most 3", check.Reason);
    }

    [Theory]
    [InlineData("sdxl", "Z-Image Turbo", "zimage", "SDXL", "Z-Image")]
    [InlineData("flux2", "SDXL 1.0", "sdxl", "FLUX.2", "SDXL")]
    [InlineData("qwen", "Flux.1 D", "flux", "Qwen-Image", "FLUX.1")]
    [InlineData("zimage", "Qwen-Image", "qwen", "Z-Image", "Qwen-Image")]
    public void A_lora_of_another_family_is_refused_and_names_both_families(string checkpointKey, string loraBase, string loraKey, string checkpointName, string loraName)
    {
        Assert.Equal(loraKey, MediaLoraStack.KeyForLabel(loraBase));
        var installed = new[] { Lora("style.safetensors", loraBase) };
        MediaLoraStackCheck check = MediaLoraStack.Validate(new[] { Pick("style.safetensors") }, installed, checkpointKey, "active.safetensors");
        Assert.False(check.Ok);
        Assert.Equal("lora_family_mismatch", check.Code);
        Assert.Contains(checkpointName, check.Reason);
        Assert.Contains(loraName, check.Reason);
        Assert.Contains("active.safetensors", check.Reason);
    }

    [Fact]
    public void Matching_families_are_accepted_in_order_with_rounded_strengths()
    {
        var installed = new[] { Lora("a.safetensors", "SDXL 1.0"), Lora("b.safetensors", "Pony"), Lora("c.safetensors", "Illustrious") };
        MediaLoraStackCheck check = MediaLoraStack.Validate(
            new[] { Pick("b.safetensors", 0.50004), Pick("a.safetensors", 1.2), Pick("c.safetensors", 2.0) }, installed, "sdxl", null);
        Assert.True(check.Ok);
        Assert.Equal(new[] { "b.safetensors", "a.safetensors", "c.safetensors" }, check.Loras.Select(l => l.Name));
        Assert.Equal(0.5, check.Loras[0].Strength);
        Assert.Empty(check.Warnings);
    }

    [Fact]
    public void An_unreadable_family_is_accepted_with_a_warning_not_guessed()
    {
        var installed = new[] { Lora("my_style.safetensors") };
        MediaLoraStackCheck check = MediaLoraStack.Validate(new[] { Pick("my_style.safetensors") }, installed, "zimage", null);
        Assert.True(check.Ok);
        Assert.Single(check.Warnings);
        Assert.Contains("not recorded", check.Warnings[0]);
    }

    [Theory]
    [InlineData("missing.safetensors", 1.0, "lora_not_installed")]
    [InlineData("a.safetensors", 0.0, "lora_strength_out_of_range")]
    [InlineData("a.safetensors", 2.5, "lora_strength_out_of_range")]
    [InlineData("a.safetensors", double.NaN, "lora_strength_out_of_range")]
    [InlineData("  ", 1.0, "lora_name_missing")]
    [InlineData("../outside.safetensors", 1.0, "lora_not_installed")]
    public void Bad_names_and_strengths_are_refused(string name, double strength, string code)
    {
        MediaLoraStackCheck check = MediaLoraStack.Validate(new[] { Pick(name, strength) }, new[] { Lora("a.safetensors", "Z-Image") }, "zimage", null);
        Assert.False(check.Ok);
        Assert.Equal(code, check.Code);
    }

    [Fact]
    public void The_same_lora_twice_is_refused()
    {
        var installed = new[] { Lora("a.safetensors", "Z-Image") };
        MediaLoraStackCheck check = MediaLoraStack.Validate(new[] { Pick("a.safetensors"), Pick("A.safetensors") }, installed, "zimage", null);
        Assert.Equal("lora_duplicate", check.Code);
    }

    [Theory]
    [InlineData("z-image", "zimage")]
    [InlineData("flux2-klein", "flux2")]
    [InlineData("flux1", "flux")]
    [InlineData("qwen-image", "qwen")]
    [InlineData("sdxl", "sdxl")]
    [InlineData("nope", null)]
    public void Registry_families_map_to_lora_family_keys(string registryId, string? key) =>
        Assert.Equal(key, MediaLoraStack.KeyForRegistryFamily(registryId));

    [Fact]
    public void Describe_marks_each_installed_lora_against_the_active_checkpoint()
    {
        var installed = new[] { Lora("z.safetensors", "Z-Image"), Lora("x.safetensors", "SDXL 1.0"), Lora("u.safetensors") };
        MediaLoraInstalledResponse response = MediaLoraStack.Describe(installed, "zimage", "z_image_turbo_nvfp4.safetensors");
        Assert.Equal(3, response.MaxLoras);
        Assert.Equal("Z-Image", response.CheckpointFamilyName);
        Assert.Equal(new[] { "compatible", "incompatible", "unknown" }, response.Loras.Select(l => l.Compatibility));
    }

    // ---- LoRA graph injection -----------------------------------------------------------------------------------

    [Fact]
    public void Three_loras_are_chained_in_order_and_every_consumer_is_rewired_to_the_end_of_the_chain()
    {
        JsonObject request = Template("z-image-smoke-api.json");
        var stack = new[] { new ValidatedLora("a.safetensors", 0.7, null), new ValidatedLora("b.safetensors", 0.5, null), new ValidatedLora("c.safetensors", 1.0, null) };

        MediaLoraStack.Inject(request, stack);

        JsonObject graph = request["prompt"]!.AsObject();
        Assert.Equal("LoraLoader", (string?)graph["900"]!["class_type"]);
        Assert.Equal("a.safetensors", (string?)graph["900"]!["inputs"]!["lora_name"]);
        Assert.Equal(0.7, (double)graph["900"]!["inputs"]!["strength_model"]!);
        Assert.Equal(new[] { "1", "0" }, LinkOf(graph, "900", "model"));
        Assert.Equal(new[] { "2", "0" }, LinkOf(graph, "900", "clip"));
        Assert.Equal(new[] { "900", "0" }, LinkOf(graph, "901", "model"));
        Assert.Equal(new[] { "900", "1" }, LinkOf(graph, "901", "clip"));
        Assert.Equal(new[] { "901", "0" }, LinkOf(graph, "902", "model"));
        Assert.Equal("c.safetensors", (string?)graph["902"]!["inputs"]!["lora_name"]);

        // The template's own consumers (ModelSamplingAuraFlow model, CLIPTextEncode clip) now read from the last loader.
        Assert.Equal(new[] { "902", "0" }, LinkOf(graph, "7", "model"));
        Assert.Equal(new[] { "902", "1" }, LinkOf(graph, "4", "clip"));
        // Nothing but the first LoRA still reads the raw loaders.
        foreach (KeyValuePair<string, JsonNode?> node in graph.Where(n => n.Key != "900"))
        {
            JsonObject inputs = node.Value!["inputs"]!.AsObject();
            foreach (KeyValuePair<string, JsonNode?> input in inputs)
                if (input.Value is JsonArray link && (string?)link[0] == "1") Assert.Fail("node " + node.Key + "." + input.Key + " still reads the raw model loader");
        }
    }

    [Fact]
    public void Injection_works_on_the_inpaint_graph_and_an_empty_stack_changes_nothing()
    {
        JsonObject untouched = Template("z-image-inpaint-api.json");
        string before = untouched.ToJsonString();
        MediaLoraStack.Inject(untouched, Array.Empty<ValidatedLora>());
        Assert.Equal(before, untouched.ToJsonString());

        MediaLoraStack.Inject(untouched, new[] { new ValidatedLora("a.safetensors", 1.0, null) });
        JsonObject graph = untouched["prompt"]!.AsObject();
        Assert.Equal(new[] { "900", "0" }, LinkOf(graph, "15", "model"));
        Assert.Equal(new[] { "900", "1" }, LinkOf(graph, "4", "clip"));
    }

    [Fact]
    public void Injection_refuses_a_graph_without_loaders_instead_of_dropping_the_lora()
    {
        var graph = new JsonObject { ["prompt"] = new JsonObject { ["1"] = new JsonObject { ["class_type"] = "KSampler", ["inputs"] = new JsonObject() } } };
        Assert.Throws<InvalidOperationException>(() => MediaLoraStack.Inject(graph, new[] { new ValidatedLora("a.safetensors", 1.0, null) }));
    }

    private static string[] LinkOf(JsonObject graph, string node, string input)
    {
        JsonArray link = graph[node]!["inputs"]![input]!.AsArray();
        return new[] { (string)link[0]!, ((int)link[1]!).ToString() };
    }

    // ---- inpaint request checks ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("a bright blue butterfly", "a bright blue butterfly")]
    [InlineData("  a   blue\r\nbutterfly ", "a blue butterfly")]
    [InlineData("", "a photograph")]
    [InlineData(null, "a photograph")]
    public void The_repaint_prompt_is_the_users_own_words_not_a_scene_rewrite(string? input, string expected) =>
        Assert.Equal(expected, MediaInpaintMask.RegionPrompt(input));

    [Fact]
    public void A_matching_png_mask_is_accepted_even_at_a_lower_resolution()
    {
        byte[] mask = MediaInpaintMask.Validate(PngHeader(1024, 768), B64(PngHeader(512, 384)));
        Assert.Equal(PngHeader(512, 384), mask);
    }

    [Fact]
    public void A_missing_mask_is_refused_with_the_next_step()
    {
        var ex = Assert.Throws<InvalidDataException>(() => MediaInpaintMask.Validate(PngHeader(512, 512), string.Empty));
        Assert.Contains("paint the area", ex.Message);
    }

    [Fact]
    public void A_mask_with_other_proportions_is_refused_because_the_region_would_land_in_the_wrong_place()
    {
        var ex = Assert.Throws<InvalidDataException>(() => MediaInpaintMask.Validate(PngHeader(1024, 768), B64(PngHeader(512, 512))));
        Assert.Contains("same proportions", ex.Message);
    }

    [Fact]
    public void Non_png_and_oversized_masks_and_unreadable_sources_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => MediaInpaintMask.Validate(PngHeader(64, 64), B64(new byte[] { 1, 2, 3, 4 })));
        Assert.Throws<InvalidDataException>(() => MediaInpaintMask.Validate(PngHeader(64, 64), "not base64 !!"));
        Assert.Throws<InvalidDataException>(() => MediaInpaintMask.Validate(PngHeader(64, 64), B64(PngHeader(8192, 8192))));
        Assert.Throws<InvalidDataException>(() => MediaInpaintMask.Validate(new byte[] { 1, 2, 3 }, B64(PngHeader(64, 64))));
    }

    [Theory]
    [InlineData(null, 6)]
    [InlineData(-5, 0)]
    [InlineData(10, 10)]
    [InlineData(500, 64)]
    public void Grow_is_clamped(int? requested, int expected) => Assert.Equal(expected, MediaInpaintMask.NormalizeGrow(requested));

    [Fact]
    public void Patch_writes_the_job_values_into_exactly_the_nodes_the_graph_declares()
    {
        JsonObject request = Template("z-image-inpaint-api.json");
        MediaInpaintMask.Patch(request, "a clear blue sky", 1234L, "src.png", "mask.png", 12);
        JsonObject graph = request["prompt"]!.AsObject();
        Assert.Equal("src.png", (string?)graph["6"]!["inputs"]!["image"]);
        Assert.Equal("mask.png", (string?)graph["8"]!["inputs"]!["image"]);
        Assert.Equal("a clear blue sky", (string?)graph["4"]!["inputs"]!["text"]);
        Assert.Equal(1234L, (long)graph["16"]!["inputs"]!["seed"]!);
        Assert.Equal(12, (int)graph["12"]!["inputs"]!["expand"]!);
        Assert.Equal("SaveImage", (string?)graph[MediaInpaintMask.SaveNode]!["class_type"]);
    }

    // ---- inpaint graph against the ComfyUI node set -------------------------------------------------------------

    /// <summary>Input names and combo options of the nodes the inpaint graph uses, read from the ComfyUI source that ships
    /// with the installed portable build (nodes.py, comfy_extras/nodes_mask.py, nodes_post_processing.py, nodes_images.py).
    /// This is the bundled node list the graph is checked against when no ComfyUI process is running.</summary>
    private static readonly Dictionary<string, string[]> NodeInputs = new()
    {
        ["UNETLoader"] = new[] { "unet_name", "weight_dtype" },
        ["CLIPLoader"] = new[] { "clip_name", "type", "device" },
        ["VAELoader"] = new[] { "vae_name" },
        ["CLIPTextEncode"] = new[] { "text", "clip" },
        ["ConditioningZeroOut"] = new[] { "conditioning" },
        ["LoadImage"] = new[] { "image" },
        ["ImageScaleToTotalPixels"] = new[] { "image", "upscale_method", "megapixels", "resolution_steps" },
        ["GetImageSize"] = new[] { "image" },
        ["ImageScale"] = new[] { "image", "upscale_method", "width", "height", "crop" },
        ["ImageToMask"] = new[] { "image", "channel" },
        ["GrowMask"] = new[] { "mask", "expand", "tapered_corners" },
        ["EmptySD3LatentImage"] = new[] { "width", "height", "batch_size" },
        ["VAEEncode"] = new[] { "pixels", "vae" },
        ["SetLatentNoiseMask"] = new[] { "samples", "mask" },
        ["ModelSamplingAuraFlow"] = new[] { "model", "shift" },
        ["KSampler"] = new[] { "model", "seed", "steps", "cfg", "sampler_name", "scheduler", "positive", "negative", "latent_image", "denoise" },
        ["VAEDecode"] = new[] { "samples", "vae" },
        ["ImageCompositeMasked"] = new[] { "destination", "source", "x", "y", "resize_source", "mask" },
        ["SaveImage"] = new[] { "images", "filename_prefix" },
        ["LoraLoader"] = new[] { "model", "clip", "lora_name", "strength_model", "strength_clip" }
    };

    private static ComfyNodeCatalog CatalogFromNodeInputs()
    {
        var info = new JsonObject();
        foreach ((string cls, string[] inputs) in NodeInputs)
        {
            var required = new JsonObject();
            foreach (string input in inputs) required[input] = new JsonArray("ANY");
            info[cls] = new JsonObject { ["input"] = new JsonObject { ["required"] = required } };
        }
        // Combos the graph sets literally; they must be offered by ComfyUI.
        void Combo(string cls, string input, params string[] options)
        {
            JsonObject required = info[cls]!["input"]!["required"]!.AsObject();
            required[input] = new JsonArray(new JsonArray(options.Select(o => (JsonNode)o).ToArray()), new JsonObject());
        }
        Combo("ImageScale", "upscale_method", "nearest-exact", "bilinear", "area", "bicubic", "lanczos");
        Combo("ImageScale", "crop", "disabled", "center");
        Combo("ImageScaleToTotalPixels", "upscale_method", "nearest-exact", "bilinear", "area", "bicubic", "lanczos");
        Combo("ImageToMask", "channel", "red", "green", "blue", "alpha");
        return ComfyLivePreflight.ParseCatalog(info.ToJsonString(), "0.37.0");
    }

    [Theory]
    [InlineData("z-image-inpaint-api.json")]
    [InlineData("z-image-smoke-api.json")]
    public void The_graph_uses_only_known_nodes_inputs_and_offered_combo_values(string template)
    {
        JsonObject graph = Template(template)["prompt"]!.AsObject();
        Assert.Empty(ComfyLivePreflight.Validate(graph, CatalogFromNodeInputs()));
        foreach (KeyValuePair<string, JsonNode?> node in graph)
        {
            string cls = (string)node.Value!["class_type"]!;
            if (!NodeInputs.TryGetValue(cls, out string[]? known)) continue; // other templates' nodes are covered by their own tests
            foreach (KeyValuePair<string, JsonNode?> input in node.Value["inputs"]!.AsObject())
                Assert.True(known.Contains(input.Key), template + " node " + node.Key + " (" + cls + ") sets unknown input \"" + input.Key + "\".");
        }
    }

    [Fact]
    public void Every_link_in_the_inpaint_graph_points_at_an_existing_node_and_output()
    {
        JsonObject graph = Template("z-image-inpaint-api.json")["prompt"]!.AsObject();
        // Output counts of the nodes that feed others; GetImageSize returns width, height, batch.
        var outputs = new Dictionary<string, int> { ["UNETLoader"] = 1, ["CLIPLoader"] = 1, ["VAELoader"] = 1, ["GetImageSize"] = 3 };
        foreach (KeyValuePair<string, JsonNode?> node in graph)
            foreach (KeyValuePair<string, JsonNode?> input in node.Value!["inputs"]!.AsObject())
            {
                if (input.Value is not JsonArray link) continue;
                string target = (string)link[0]!;
                Assert.True(graph.ContainsKey(target), "node " + node.Key + "." + input.Key + " links to missing node " + target);
                string targetClass = (string)graph[target]!["class_type"]!;
                int index = (int)link[1]!;
                Assert.True(index < (outputs.TryGetValue(targetClass, out int count) ? count : 1), "node " + node.Key + "." + input.Key + " reads a missing output of " + targetClass);
            }
        // The composite takes the original (scaled) pixels as destination and the sampled ones as source, masked by the grown mask.
        Assert.Equal(new[] { "7", "0" }, LinkOf(graph, "18", "destination"));
        Assert.Equal(new[] { "17", "0" }, LinkOf(graph, "18", "source"));
        Assert.Equal(new[] { "12", "0" }, LinkOf(graph, "18", "mask"));
        Assert.Equal(new[] { "14", "0" }, LinkOf(graph, "16", "latent_image"));
    }

    [Fact]
    public void The_inpaint_template_is_shipped_registered_and_runs_on_the_z_image_family()
    {
        Assert.Contains("z-image-inpaint-api.json", ConfigAssets.FileNames);
        Assert.EndsWith("z-image-inpaint-api.json", LocalAiPaths.InpaintWorkflowTemplate);
        Assert.Contains(MediaCompanionRegistry.Shipped.SupportedFor("inpaint"), f => f.Id == "z-image");
        // Same main loader node as every other template, so the checkpoint override and the companion resolver apply unchanged.
        JsonObject graph = Template("z-image-inpaint-api.json")["prompt"]!.AsObject();
        Assert.Equal("UNETLoader", (string?)graph[MediaCheckpointResolution.UnetNode]!["class_type"]);
        Assert.Equal("SaveImage", (string?)graph[MediaInpaintMask.SaveNode]!["class_type"]);
    }

    // ---- job service and routes ---------------------------------------------------------------------------------

    [Fact]
    public void The_job_queue_refuses_an_inpaint_job_without_a_mask_before_anything_is_queued()
    {
        MediaJobService jobs = _factory.Services.GetRequiredService<MediaJobService>();
        var ex = Assert.Throws<InvalidDataException>(() => jobs.Create(new MediaJobRequest
        {
            Type = "inpaint", Prompt = "a new sky", SourceImageBase64 = B64(PngHeader(512, 512))
        }));
        Assert.Contains("mask", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_job_queue_refuses_loras_on_job_types_that_cannot_use_them()
    {
        MediaJobService jobs = _factory.Services.GetRequiredService<MediaJobService>();
        var ex = Assert.Throws<InvalidDataException>(() => jobs.Create(new MediaJobRequest
        {
            Type = "video", Prompt = "x", Loras = new List<MediaLoraSelection> { Pick("a.safetensors") }
        }));
        Assert.Contains("image and inpaint", ex.Message);
    }

    [Fact]
    public async Task Jobs_route_answers_400_with_the_reason_for_a_four_lora_stack()
    {
        using HttpClient client = Authorised();
        var request = new MediaJobRequest
        {
            Type = "image", Prompt = "a fox",
            Loras = Enumerable.Range(1, 4).Select(i => Pick("l" + i + ".safetensors")).ToList()
        };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/jobs", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at most 3", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Jobs_route_answers_400_for_an_inpaint_request_with_a_wrong_shaped_mask()
    {
        using HttpClient client = Authorised();
        var request = new MediaJobRequest
        {
            Type = "inpaint", Prompt = "a new sky",
            SourceImageBase64 = B64(PngHeader(1024, 768)), MaskImageBase64 = B64(PngHeader(300, 300))
        };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/jobs", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("same proportions", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Installed_lora_list_needs_the_token_and_reports_the_stack_limits()
    {
        using (HttpClient anonymous = _factory.CreateClient())
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/media/loras/installed")).StatusCode);

        using HttpClient client = Authorised();
        HttpResponseMessage response = await client.GetAsync("/api/v1/media/loras/installed");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, doc.RootElement.GetProperty("maxLoras").GetInt32());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("loras").ValueKind);
    }

    [Fact]
    public async Task Validate_route_refuses_an_uninstalled_lora_with_a_conflict_and_the_reason()
    {
        using HttpClient client = Authorised();
        string missing = "parux-test-missing-" + Guid.NewGuid().ToString("N") + ".safetensors";
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/media/loras/validate",
            new MediaLoraStackRequest { Loras = new List<MediaLoraSelection> { Pick(missing) } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("lora_not_installed", doc.RootElement.GetProperty("code").GetString());
        Assert.Contains("not an installed LoRA", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Validate_route_accepts_an_empty_stack()
    {
        using HttpClient client = Authorised();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/media/loras/validate", new MediaLoraStackRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpClient Authorised()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }
}
