using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The runtime config files ship inside the Server binary and are written out when missing.
///
/// This exists because their absence was not a degraded feature, it was a dead product: llama.cpp
/// refuses to start when <c>--ui-config-file</c> names a file it cannot open, so a LocalAI root
/// without <c>llama-ui.json</c> could not load any text model at all, and a root without the
/// ComfyUI graphs could not generate anything. Both states were reachable by a first install and by
/// tidying up a folder.
///
/// The template assertions are the contract with <see cref="ComfyWorkflowRunner"/>: it patches
/// specific node ids and fields by name, so a renumbered graph would still be valid ComfyUI JSON
/// while silently sending the prompt, the seed or the output path nowhere.
/// </summary>
public sealed class ConfigAssetsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "parux-config-assets-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void Every_declared_asset_is_actually_embedded_in_the_binary()
    {
        foreach (string fileName in ConfigAssets.FileNames)
            Assert.False(string.IsNullOrWhiteSpace(ConfigAssets.ReadEmbedded(fileName)),
                fileName + " is declared but not embedded - EnsureAll would silently do nothing.");
    }

    [Fact]
    public void A_missing_asset_is_written_out()
    {
        string? path = ConfigAssets.EnsureIn(_dir, ConfigAssets.UiConfigFileName);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void An_existing_file_is_never_overwritten()
    {
        // A user who tuned a workflow keeps their version; PaRuX only ever fills a hole.
        Directory.CreateDirectory(_dir);
        string target = Path.Combine(_dir, "upscale-api.json");
        File.WriteAllText(target, "{\"prompt\":{\"mine\":true}}");

        string? path = ConfigAssets.EnsureIn(_dir, "upscale-api.json");

        Assert.Equal(target, path);
        Assert.Equal("{\"prompt\":{\"mine\":true}}", File.ReadAllText(target));
    }

    [Fact]
    public void An_unknown_name_reports_failure_rather_than_writing_an_empty_file()
    {
        Assert.Null(ConfigAssets.EnsureIn(_dir, "not-an-asset.json"));
        Assert.False(File.Exists(Path.Combine(_dir, "not-an-asset.json")));
    }

    [Fact]
    public void Every_workflow_template_is_a_ComfyUI_api_format_prompt()
    {
        foreach (string fileName in ConfigAssets.FileNames.Where(f => f.EndsWith("-api.json", StringComparison.Ordinal)))
        {
            JsonNode root = JsonNode.Parse(ConfigAssets.ReadEmbedded(fileName)!)!;
            JsonObject? prompt = root["prompt"]?.AsObject();
            Assert.True(prompt is { Count: > 0 }, fileName + " has no \"prompt\" object.");
            foreach (KeyValuePair<string, JsonNode?> node in prompt!)
            {
                Assert.True(node.Value?["class_type"] is not null, fileName + ": node " + node.Key + " has no class_type.");
                Assert.True(node.Value?["inputs"] is JsonObject, fileName + ": node " + node.Key + " has no inputs object.");
            }
        }
    }

    /// <summary>Exactly what ComfyWorkflowRunner writes into each graph, node id by field name.
    /// Kept as data so the two can be compared at a glance when either changes.</summary>
    public static TheoryData<string, string, string[]> RunnerContract => new()
    {
        { "z-image-smoke-api.json",   "4",  new[] { "text" } },
        { "z-image-smoke-api.json",   "6",  new[] { "width", "height" } },
        { "z-image-smoke-api.json",   "8",  new[] { "seed" } },
        { "z-image-smoke-api.json",   "10", new[] { "filename_prefix" } },
        { "z-image-smoke-api.json",   "1",  new[] { "unet_name" } },
        { "z-image-img2img-api.json", "4",  new[] { "text" } },
        { "z-image-img2img-api.json", "6",  new[] { "image" } },
        { "z-image-img2img-api.json", "9",  new[] { "seed", "denoise" } },
        { "z-image-img2img-api.json", "11", new[] { "filename_prefix" } },
        { "z-image-img2img-api.json", "1",  new[] { "unet_name" } },
        { "flux2-klein-edit-api.json", "4",  new[] { "image" } },
        { "flux2-klein-edit-api.json", "7",  new[] { "text" } },
        { "flux2-klein-edit-api.json", "13", new[] { "noise_seed" } },
        { "flux2-klein-edit-api.json", "19", new[] { "filename_prefix" } },
        { "flux2-klein-edit-api.json", "1",  new[] { "unet_name" } },
        { "wan-video-smoke-api.json", "4",  new[] { "text" } },
        { "wan-video-smoke-api.json", "6",  new[] { "width", "height", "length" } },
        { "wan-video-smoke-api.json", "8",  new[] { "seed", "steps" } },
        { "wan-video-smoke-api.json", "10", new[] { "fps" } },
        { "wan-video-smoke-api.json", "11", new[] { "filename_prefix" } },
        { "wan-video-smoke-api.json", "1",  new[] { "unet_name" } },
        { "upscale-api.json",         "1",  new[] { "image" } },
    };

    [Theory]
    [MemberData(nameof(RunnerContract))]
    public void The_nodes_the_runner_patches_exist_with_those_fields(string fileName, string nodeId, string[] fields)
    {
        JsonObject prompt = JsonNode.Parse(ConfigAssets.ReadEmbedded(fileName)!)!["prompt"]!.AsObject();

        Assert.True(prompt.ContainsKey(nodeId), fileName + " has no node \"" + nodeId + "\".");
        JsonObject inputs = prompt[nodeId]!["inputs"]!.AsObject();
        foreach (string field in fields)
            Assert.True(inputs.ContainsKey(field),
                fileName + ": node \"" + nodeId + "\" has no \"" + field + "\" input.");
    }

    /// <summary>The node the runner reads the finished file back from. A save node that is not an
    /// output node completes without ever appearing in ComfyUI's history outputs, which the runner
    /// reports as "Generation completed with no output file".</summary>
    [Theory]
    [InlineData("z-image-smoke-api.json", "10", "SaveImage")]
    [InlineData("z-image-img2img-api.json", "11", "SaveImage")]
    [InlineData("flux2-klein-edit-api.json", "19", "SaveImage")]
    [InlineData("wan-video-smoke-api.json", "11", "SaveVideo")]
    [InlineData("upscale-api.json", "4", "SaveImage")]
    public void The_save_node_is_the_expected_output_node(string fileName, string nodeId, string classType)
    {
        JsonObject prompt = JsonNode.Parse(ConfigAssets.ReadEmbedded(fileName)!)!["prompt"]!.AsObject();

        Assert.Equal(classType, prompt[nodeId]!["class_type"]!.GetValue<string>());
    }

    [Fact]
    public void The_ui_config_is_valid_json_because_llama_server_refuses_to_start_otherwise()
    {
        Assert.NotNull(JsonDocument.Parse(ConfigAssets.ReadEmbedded(ConfigAssets.UiConfigFileName)!));
    }
}
