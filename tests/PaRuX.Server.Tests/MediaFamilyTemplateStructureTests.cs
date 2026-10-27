using System.Text.Json.Nodes;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Proves that every workflow template of every supported media family is a structurally valid ComfyUI API graph: each link
/// points at a real node and a real output slot, every node and input name exists on a real ComfyUI (the
/// Fixtures/comfy-node-specs.json snapshot of /object_info from ComfyUI 0.42.1), every required input is supplied, the
/// loaders carry the names the companion registry pins, and the nodes the runner patches by id are the ones it expects.
/// Hermetic: no ComfyUI, GPU or network is used here. It says the graph is well formed; only a live run proves the
/// pictures are good.
/// </summary>
public sealed class MediaFamilyTemplateStructureTests
{
    private static readonly Lazy<JsonObject> Specs = new(() =>
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "comfy-node-specs.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    });

    /// <summary>(family id, pipeline, template file) for every pipeline of every supported family.</summary>
    public static IEnumerable<object[]> Templates() =>
        MediaCompanionRegistry.Shipped.Families.Where(family => family.IsSupported)
            .SelectMany(family => family.Pipelines.Select(pipeline => new object[] { family.Id, pipeline, family.TemplateFor(pipeline)! }));

    private static JsonObject LoadPrompt(string templateFile)
    {
        string text = ConfigAssets.ReadEmbedded(templateFile)
            ?? throw new InvalidDataException("The template " + templateFile + " is not embedded.");
        return JsonNode.Parse(text)!.AsObject()["prompt"]!.AsObject();
    }

    private static JsonObject SpecOf(string classType)
    {
        JsonObject nodes = Specs.Value["nodes"]!.AsObject();
        Assert.True(nodes.ContainsKey(classType), classType + " is not a node of the real ComfyUI snapshot.");
        return nodes[classType]!.AsObject();
    }

    private static bool IsLink(JsonNode? value) => value is JsonArray { Count: 2 };

    private static string ClassOf(JsonObject prompt, string id) => prompt[id]!["class_type"]!.GetValue<string>();

    [Theory]
    [MemberData(nameof(Templates))]
    public void Every_link_targets_an_existing_node_and_a_real_output_slot(string familyId, string pipeline, string template)
    {
        JsonObject prompt = LoadPrompt(template);
        foreach ((string id, JsonNode? nodeValue) in prompt)
        {
            foreach ((string name, JsonNode? value) in nodeValue!["inputs"]!.AsObject())
            {
                if (!IsLink(value)) continue;
                string target = value![0]!.GetValue<string>();
                int slot = value[1]!.GetValue<int>();
                Assert.True(prompt.ContainsKey(target), $"{template}: node {id}.{name} links to missing node {target}.");
                int outputs = SpecOf(ClassOf(prompt, target))["outputs"]!.GetValue<int>();
                Assert.True(slot >= 0 && slot < outputs, $"{template}: node {id}.{name} reads output {slot} of {ClassOf(prompt, target)}, which has {outputs}.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Every_node_and_input_exists_on_the_real_server_and_every_required_input_is_supplied(string familyId, string pipeline, string template)
    {
        JsonObject prompt = LoadPrompt(template);
        foreach ((string id, JsonNode? nodeValue) in prompt)
        {
            string classType = nodeValue!["class_type"]!.GetValue<string>();
            JsonObject spec = SpecOf(classType);
            string[] required = spec["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            string[] optional = spec["optional"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            string[] supplied = nodeValue["inputs"]!.AsObject().Select(pair => pair.Key).ToArray();

            Assert.All(supplied, input => Assert.True(required.Contains(input) || optional.Contains(input),
                $"{template}: node {id} ({classType}) gives unknown input \"{input}\"."));
            Assert.All(required, input => Assert.True(supplied.Contains(input),
                $"{template}: node {id} ({classType}) is missing required input \"{input}\"."));
        }
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void The_graph_is_acyclic_and_every_node_feeds_a_save_node(string familyId, string pipeline, string template)
    {
        JsonObject prompt = LoadPrompt(template);
        string[] saves = prompt.Where(pair => ClassOf(prompt, pair.Key).StartsWith("Save", StringComparison.Ordinal))
            .Select(pair => pair.Key).ToArray();
        Assert.NotEmpty(saves);

        var reached = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id, HashSet<string> path)
        {
            Assert.True(path.Add(id), $"{template}: cycle through node {id}.");
            reached.Add(id);
            foreach ((_, JsonNode? value) in prompt[id]!["inputs"]!.AsObject())
                if (IsLink(value)) Visit(value![0]!.GetValue<string>(), path);
            path.Remove(id);
        }
        foreach (string save in saves) Visit(save, new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(prompt.Select(pair => pair.Key).OrderBy(k => k, StringComparer.Ordinal),
            reached.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Loaders_carry_the_names_the_registry_pins_and_the_main_loader_is_where_the_runner_patches(string familyId, string pipeline, string template)
    {
        RegistryFamily family = MediaCompanionRegistry.Shipped.FindFamily(familyId)!;
        JsonObject prompt = LoadPrompt(template);

        RegistryMainLoader main = family.MainLoader!;
        Assert.Equal(main.ClassType, ClassOf(prompt, main.Node));
        Assert.False(string.IsNullOrWhiteSpace(prompt[main.Node]!["inputs"]![main.Input]!.GetValue<string>()));
        Assert.Equal(MediaCheckpointResolution.UnetNode, main.Node);

        JsonObject[] Nodes(string classType) =>
            prompt.Select(pair => pair.Value!.AsObject()).Where(node => node["class_type"]!.GetValue<string>() == classType).ToArray();

        // A single-file checkpoint (SDXL, FLUX.1 schnell) embeds its text encoders and VAE: it declares no companion and its
        // graph must not load any. A split family declares exactly one of each and its graph loads exactly those files.
        RegistryCompanion? textEncoder = family.Companions.SingleOrDefault(c => c.Role == "text_encoder");
        if (textEncoder is null) Assert.Empty(Nodes("CLIPLoader"));
        else
        {
            JsonObject clip = Assert.Single(Nodes("CLIPLoader"))["inputs"]!.AsObject();
            Assert.Equal(textEncoder.FileName, clip["clip_name"]!.GetValue<string>());
            Assert.Equal(textEncoder.ClipType, clip["type"]!.GetValue<string>());
        }

        RegistryCompanion? vae = family.Companions.SingleOrDefault(c => c.Role == "vae");
        if (vae is null) Assert.Empty(Nodes("VAELoader"));
        else Assert.Equal(vae.FileName, Assert.Single(Nodes("VAELoader"))["inputs"]!["vae_name"]!.GetValue<string>());
    }

    [Fact]
    public void The_registry_required_nodes_are_all_in_the_family_graphs_and_on_the_real_server()
    {
        foreach (RegistryFamily family in MediaCompanionRegistry.Shipped.Families.Where(f => f.IsSupported))
        {
            HashSet<string> inGraphs = family.AllTemplateFiles
                .SelectMany(file => LoadPrompt(file).Select(pair => pair.Value!["class_type"]!.GetValue<string>()))
                .ToHashSet(StringComparer.Ordinal);
            foreach (string required in family.RequiredNodes)
            {
                Assert.True(inGraphs.Contains(required), $"{family.Id}: required node {required} is in none of its templates.");
                SpecOf(required);
            }
        }
    }

    [Fact]
    public void Every_image_template_honours_the_node_contract_the_image_runner_patches()
    {
        // RunImageAsync and the engine race write the prompt to node 4, the seed and steps to node 8, the size to
        // node 6 and read the picture from node 10, whichever family's template is submitted.
        foreach (RegistryFamily family in MediaCompanionRegistry.Shipped.SupportedFor("image"))
        {
            JsonObject prompt = LoadPrompt(family.TemplateFor("image")!);
            Assert.Equal("CLIPTextEncode", ClassOf(prompt, "4"));
            Assert.True(prompt["4"]!["inputs"]!.AsObject().ContainsKey("text"));
            Assert.Equal("KSampler", ClassOf(prompt, "8"));
            Assert.True(prompt["8"]!["inputs"]!.AsObject().ContainsKey("seed"));
            Assert.True(prompt["8"]!["inputs"]!.AsObject().ContainsKey("steps"));
            // SDXL needs the 4-channel EmptyLatentImage; the flow-matching families use the 16-channel EmptySD3LatentImage.
            Assert.Contains(ClassOf(prompt, "6"), new[] { "EmptySD3LatentImage", "EmptyLatentImage" });
            Assert.True(prompt["6"]!["inputs"]!.AsObject().ContainsKey("width"));
            Assert.True(prompt["6"]!["inputs"]!.AsObject().ContainsKey("height"));
            Assert.Equal("SaveImage", ClassOf(prompt, "10"));
            Assert.True(prompt["10"]!["inputs"]!.AsObject().ContainsKey("filename_prefix"));
            // The prompt node really feeds the sampler's positive input, so patching node 4 changes the picture.
            Assert.Equal("4", prompt["8"]!["inputs"]!["positive"]![0]!.GetValue<string>());
        }
    }

    [Fact]
    public void Every_edit_template_honours_the_node_contract_the_edit_runner_patches()
    {
        // RunEditAsync writes the uploaded image to node 4, the instruction to node 7, the seed to node 13 and reads node 19.
        foreach (RegistryFamily family in MediaCompanionRegistry.Shipped.SupportedFor("edit"))
        {
            JsonObject prompt = LoadPrompt(family.TemplateFor("edit")!);
            Assert.Equal("LoadImage", ClassOf(prompt, "4"));
            Assert.True(prompt["4"]!["inputs"]!.AsObject().ContainsKey("image"));
            Assert.Equal("SaveImage", ClassOf(prompt, "19"));

            string instruction = ComfyWorkflowRunner.EditInstructionInput(JsonNode.Parse(PromptJson(prompt))!.AsObject());
            Assert.True(prompt["7"]!["inputs"]!.AsObject().ContainsKey(instruction), family.Id + ": node 7 has no " + instruction);
            string seed = ComfyWorkflowRunner.EditSeedInput(JsonNode.Parse(PromptJson(prompt))!.AsObject());
            Assert.True(prompt["13"]!["inputs"]!.AsObject().ContainsKey(seed), family.Id + ": node 13 has no " + seed);

            // The instruction node really conditions the sampling, so patching node 7 changes the picture.
            Assert.True(ReachesFrom(prompt, "19", "7"), family.Id + ": node 7 does not feed the saved picture.");
            Assert.True(ReachesFrom(prompt, "19", "13"), family.Id + ": node 13 does not feed the saved picture.");
        }
    }

    private static string PromptJson(JsonObject prompt) => new JsonObject { ["prompt"] = JsonNode.Parse(prompt.ToJsonString()) }.ToJsonString();

    /// <summary>True when node <paramref name="from"/> depends, directly or not, on node <paramref name="needs"/>.</summary>
    private static bool ReachesFrom(JsonObject prompt, string from, string needs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Walk(string id)
        {
            if (id == needs) return true;
            if (!seen.Add(id) || prompt[id] is null) return false;
            return prompt[id]!["inputs"]!.AsObject().Any(pair => IsLink(pair.Value) && Walk(pair.Value![0]!.GetValue<string>()));
        }
        return Walk(from);
    }

    [Fact]
    public void Qwen_image_graph_uses_the_official_comfyui_qwen_image_settings()
    {
        // Values from ComfyUI's own image_qwen_image workflow template (comfyui_workflow_templates 0.11.89), the
        // non-Lightning branch: ModelSamplingAuraFlow shift 3.1, euler/simple, 20 steps, CFG 4, native 1328x1328.
        JsonObject prompt = LoadPrompt("qwen-image-api.json");
        Assert.Equal(3.1, prompt["7"]!["inputs"]!["shift"]!.GetValue<double>(), 6);
        JsonObject sampler = prompt["8"]!["inputs"]!.AsObject();
        Assert.Equal(20, sampler["steps"]!.GetValue<int>());
        Assert.Equal(4.0, sampler["cfg"]!.GetValue<double>(), 6);
        Assert.Equal("euler", sampler["sampler_name"]!.GetValue<string>());
        Assert.Equal("simple", sampler["scheduler"]!.GetValue<string>());
        Assert.Equal(1328, prompt["6"]!["inputs"]!["width"]!.GetValue<int>());
        Assert.Equal("qwen_image_fp8_e4m3fn.safetensors", prompt["1"]!["inputs"]!["unet_name"]!.GetValue<string>());
        // A real negative branch exists (empty text), because CFG 4 needs one.
        Assert.Equal("CLIPTextEncode", ClassOf(prompt, "5"));
        Assert.Equal("5", sampler["negative"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Qwen_image_edit_graph_follows_the_official_edit_wiring()
    {
        // Wiring from ComfyUI's image_qwen_image_edit template: the source image is VAE-encoded as the sampled latent, and
        // both text encoders see the image and the VAE; AuraFlow shift 3, CFGNorm 1, euler/simple, 20 steps, CFG 2.5.
        // PaRuX scales the source to 1 megapixel first (the official graph leaves that node unconnected).
        JsonObject prompt = LoadPrompt("qwen-image-edit-api.json");
        Assert.Equal("qwen_image_edit_fp8_e4m3fn.safetensors", prompt["1"]!["inputs"]!["unet_name"]!.GetValue<string>());
        Assert.Equal("TextEncodeQwenImageEdit", ClassOf(prompt, "7"));
        Assert.Equal("TextEncodeQwenImageEdit", ClassOf(prompt, "8"));
        Assert.Equal("", prompt["8"]!["inputs"]!["prompt"]!.GetValue<string>());
        foreach (string encoder in new[] { "7", "8" })
        {
            Assert.Equal("5", prompt[encoder]!["inputs"]!["image"]![0]!.GetValue<string>());
            Assert.Equal("3", prompt[encoder]!["inputs"]!["vae"]![0]!.GetValue<string>());
        }
        JsonObject sampler = prompt["13"]!["inputs"]!.AsObject();
        Assert.Equal("7", sampler["positive"]![0]!.GetValue<string>());
        Assert.Equal("8", sampler["negative"]![0]!.GetValue<string>());
        Assert.Equal("6", sampler["latent_image"]![0]!.GetValue<string>());
        Assert.Equal("10", sampler["model"]![0]!.GetValue<string>());
        Assert.Equal(1.0, sampler["denoise"]!.GetValue<double>(), 6);
        Assert.Equal(20, sampler["steps"]!.GetValue<int>());
        Assert.Equal(2.5, sampler["cfg"]!.GetValue<double>(), 6);
        Assert.Equal("CFGNorm", ClassOf(prompt, "10"));
        Assert.Equal("9", prompt["10"]!["inputs"]!["model"]![0]!.GetValue<string>());
        Assert.Equal("ModelSamplingAuraFlow", ClassOf(prompt, "9"));
        Assert.Equal(3.0, prompt["9"]!["inputs"]!["shift"]!.GetValue<double>(), 6);
        Assert.Equal("5", prompt["6"]!["inputs"]!["pixels"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Edit_runner_patches_the_input_names_each_family_graph_really_has()
    {
        JsonObject flux = JsonNode.Parse(ConfigAssets.ReadEmbedded("flux2-klein-edit-api.json")!)!.AsObject();
        ComfyWorkflowRunner.SetEditInstruction(flux, "make it night");
        ComfyWorkflowRunner.SetEditSeed(flux, 123);
        Assert.Equal("make it night", (string?)flux["prompt"]!["7"]!["inputs"]!["text"]);
        Assert.Equal(123L, (long?)flux["prompt"]!["13"]!["inputs"]!["noise_seed"]);

        JsonObject qwen = JsonNode.Parse(ConfigAssets.ReadEmbedded("qwen-image-edit-api.json")!)!.AsObject();
        ComfyWorkflowRunner.SetEditInstruction(qwen, "make it night");
        ComfyWorkflowRunner.SetEditSeed(qwen, 123);
        Assert.Equal("make it night", (string?)qwen["prompt"]!["7"]!["inputs"]!["prompt"]);
        Assert.Equal(123L, (long?)qwen["prompt"]!["13"]!["inputs"]!["seed"]);
        Assert.Equal("", (string?)qwen["prompt"]!["8"]!["inputs"]!["prompt"]);
    }

    [Fact]
    public void Every_template_the_app_ships_is_written_to_the_config_folder_by_config_assets()
    {
        foreach (RegistryFamily family in MediaCompanionRegistry.Shipped.Families.Where(f => f.IsSupported))
            foreach (string file in family.AllTemplateFiles)
            {
                Assert.Contains(file, ConfigAssets.FileNames);
                Assert.NotNull(ConfigAssets.ReadEmbedded(file));
            }
    }
}
