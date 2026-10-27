using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// An activated checkpoint of another supported family runs that family's own graph: the template follows the file's
/// header-detected family, the companion names in the graph are the registry's, and a family that cannot run a media type
/// is still refused for it. Isolated PaRuX data root and ComfyUI root, with real embedded templates written by ConfigAssets.
/// </summary>
public sealed class MediaFamilyTemplateSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-tplsel-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDataRoot;
    private readonly IDisposable _rootScope;

    public MediaFamilyTemplateSelectionTests()
    {
        Directory.CreateDirectory(_dir);
        _previousDataRoot = PaRuXDataRoot.Path;
        PaRuXDataRoot.UseForTests(Path.Combine(_dir, "data"));
        _rootScope = LocalAiPaths.UseRootForTests(Path.Combine(_dir, "root"));
        Directory.CreateDirectory(LocalAiPaths.DiffusionModelsPath);
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

    // The top-level tensor names of the real Qwen-Image header, and of the Z-Image and FLUX.2 layouts the other tests use.
    private static readonly string[] QwenNames =
    {
        "transformer_blocks.0.attn.to_q.weight", "time_text_embed.timestep_embedder.linear_1.weight",
        "img_in.weight", "txt_in.weight", "txt_norm.weight", "norm_out.linear.weight", "proj_out.weight"
    };
    private static readonly string[] ZImageNames = { "noise_refiner.0.w", "context_refiner.0.w", "cap_embedder.0.w", "x_embedder.w" };

    private string InstallQwen(string fileName = "qwen_image_fp8_e4m3fn.safetensors")
    {
        string path = Path.Combine(LocalAiPaths.DiffusionModelsPath, fileName);
        WriteHeader(path, QwenNames);
        return path;
    }

    [Fact]
    public void Without_an_override_the_default_family_template_is_used()
    {
        MediaCheckpointEffective effective = MediaCheckpointResolution.Resolve("image");

        Assert.Equal("z-image-smoke-api.json", effective.TemplateFile);
        Assert.Equal(LocalAiPaths.ImageWorkflowTemplate, MediaCheckpointResolution.TemplatePathFor(effective));
    }

    [Fact]
    public void An_activated_qwen_checkpoint_runs_the_qwen_template_and_the_status_says_so()
    {
        InstallQwen();
        LocalAiPaths.SetMediaCheckpointOverride("image", "qwen_image_fp8_e4m3fn.safetensors");

        MediaCheckpointEffective resolved = MediaCheckpointResolution.Resolve("image");
        MediaCheckpointEffective job = MediaCheckpointResolution.ResolveForJob("image");

        foreach (MediaCheckpointEffective effective in new[] { resolved, job })
        {
            Assert.True(effective.IsOverridden);
            Assert.Equal("qwen_image_fp8_e4m3fn.safetensors", effective.File);
            Assert.Equal("qwen-image-api.json", effective.TemplateFile);
            Assert.Equal(Path.Combine(LocalAiPaths.ConfigDir, "qwen-image-api.json"), MediaCheckpointResolution.TemplatePathFor(effective));
            Assert.Contains(effective.ReplacesInputs, input => input.Contains("qwen-image-api.json", StringComparison.Ordinal));
        }
        Assert.True(File.Exists(MediaCheckpointResolution.TemplatePathFor(job)), "ConfigAssets must have written the Qwen template.");
        Assert.Equal("Qwen-Image", MediaCheckpointResolution.FamilyOfInstalledFile("qwen_image_fp8_e4m3fn.safetensors"));
    }

    [Fact]
    public void An_activated_z_image_checkpoint_keeps_the_z_image_template()
    {
        string file = "zimage_custom.safetensors";
        WriteHeader(Path.Combine(LocalAiPaths.DiffusionModelsPath, file), ZImageNames);
        LocalAiPaths.SetMediaCheckpointOverride("image", file);

        Assert.Equal("z-image-smoke-api.json", MediaCheckpointResolution.ResolveForJob("image").TemplateFile);
    }

    [Fact]
    public void An_activated_qwen_edit_checkpoint_runs_the_qwen_edit_template_while_flux_keeps_its_own()
    {
        InstallQwen("qwen_image_edit_fp8_e4m3fn.safetensors");
        Assert.Equal("flux2-klein-edit-api.json", MediaCheckpointResolution.Resolve("edit").TemplateFile);

        LocalAiPaths.SetMediaCheckpointOverride("edit", "qwen_image_edit_fp8_e4m3fn.safetensors");
        MediaCheckpointEffective effective = MediaCheckpointResolution.ResolveForJob("edit");

        Assert.Equal("qwen_image_edit_fp8_e4m3fn.safetensors", effective.File);
        Assert.Equal("qwen-image-edit-api.json", effective.TemplateFile);
        Assert.True(File.Exists(MediaCheckpointResolution.TemplatePathFor(effective)));
        // The same weights layout as an image checkpoint resolves to the text-to-image graph for the image type.
        LocalAiPaths.SetMediaCheckpointOverride("image", "qwen_image_edit_fp8_e4m3fn.safetensors");
        Assert.Equal("qwen-image-api.json", MediaCheckpointResolution.ResolveForJob("image").TemplateFile);
    }

    [Fact]
    public void A_qwen_image_file_is_refused_for_the_video_type_which_it_cannot_run()
    {
        string path = InstallQwen();

        var video = Assert.Throws<InvalidDataException>(() => LocalAiPaths.SetMediaCheckpointOverride("video", Path.GetFileName(path)));
        Assert.Contains("Qwen-Image", video.Message);
        Assert.Contains("Wan 2.2", video.Message);
        Assert.Null(LocalAiPaths.GetMediaCheckpointOverride("video"));
        MediaCheckpointResolution.EnsureFamilyAllowed("image", path);
        MediaCheckpointResolution.EnsureFamilyAllowed("edit", path);
    }

    [Theory]
    [InlineData("image", "qwen_image_fp8_e4m3fn.safetensors", "qwen-image-api.json")]
    [InlineData("edit", "qwen_image_edit_fp8_e4m3fn.safetensors", "qwen-image-edit-api.json")]
    public void The_qwen_template_with_its_checkpoint_resolves_the_registry_companions_into_the_graph(string type, string checkpoint, string template)
    {
        string path = InstallQwen(checkpoint);
        string models = Path.Combine(LocalAiPaths.ComfyApp, "models");
        Directory.CreateDirectory(Path.Combine(models, "text_encoders"));
        Directory.CreateDirectory(Path.Combine(models, "vae"));
        File.WriteAllBytes(Path.Combine(models, "text_encoders", "qwen_2.5_vl_7b_fp8_scaled.safetensors"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(models, "vae", "qwen_image_vae.safetensors"), new byte[] { 1 });
        LocalAiPaths.SetMediaCheckpointOverride(type, Path.GetFileName(path));

        MediaCheckpointEffective effective = MediaCheckpointResolution.ResolveForJob(type);
        Assert.Equal(template, effective.TemplateFile);
        JsonObject request = JsonNode.Parse(File.ReadAllText(MediaCheckpointResolution.TemplatePathFor(effective)))!.AsObject();
        request["prompt"]!["1"]!["inputs"]!["unet_name"] = effective.File;

        ComfyWorkflowRunner.ApplyResolvedCompanionChoices(request, type);

        JsonObject prompt = request["prompt"]!.AsObject();
        Assert.Equal(checkpoint, (string?)prompt["1"]!["inputs"]!["unet_name"]);
        Assert.Equal("qwen_2.5_vl_7b_fp8_scaled.safetensors", (string?)prompt["2"]!["inputs"]!["clip_name"]);
        Assert.Equal("qwen_image", (string?)prompt["2"]!["inputs"]!["type"]);
        Assert.Equal("qwen_image_vae.safetensors", (string?)prompt["3"]!["inputs"]!["vae_name"]);
    }

    [Fact]
    public void Img2img_has_only_a_z_image_graph_so_a_qwen_file_in_it_fails_honestly_instead_of_running()
    {
        // Image-to-image still has a single Z-Image graph. Z-Image's graph with Qwen's weights in node 1 would be the
        // wrong-family failure the template selection exists to prevent, so the registry rejects it for that pipeline.
        InstallQwen();
        JsonObject request = JsonNode.Parse(ConfigAssets.ReadEmbedded("z-image-smoke-api.json")!)!.AsObject();
        request["prompt"]!["1"]!["inputs"]!["unet_name"] = "qwen_image_fp8_e4m3fn.safetensors";

        var error = Assert.Throws<InvalidOperationException>(() => ComfyWorkflowRunner.ApplyResolvedCompanionChoices(request, "img2img"));
        Assert.Contains("img2img", error.Message);
    }
}
