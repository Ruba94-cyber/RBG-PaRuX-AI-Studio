using System.Buffers.Binary;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Override scope and the family gate. Runs against an isolated PaRuX data root and an isolated ComfyUI root, so
/// the operator's real checkpoints and override file are never read or written.
/// </summary>
public sealed class MediaCheckpointGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-gate-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDataRoot;
    private readonly IDisposable _rootScope;

    public MediaCheckpointGateTests()
    {
        Directory.CreateDirectory(_dir);
        _previousDataRoot = PaRuXDataRoot.Path;
        PaRuXDataRoot.UseForTests(Path.Combine(_dir, "data"));
        _rootScope = LocalAiPaths.UseRootForTests(Path.Combine(_dir, "root"));
        Directory.CreateDirectory(LocalAiPaths.DiffusionModelsPath);
        Directory.CreateDirectory(LocalAiPaths.ConfigDir);
        File.WriteAllText(LocalAiPaths.ImageWorkflowTemplate,
            "{\"prompt\":{\"1\":{\"class_type\":\"UNETLoader\",\"inputs\":{\"unet_name\":\"z_image_turbo_nvfp4.safetensors\",\"weight_dtype\":\"default\"}}}}");
    }

    public void Dispose()
    {
        _rootScope.Dispose();
        PaRuXDataRoot.UseForTests(_previousDataRoot);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A valid safetensors file whose header names the given tensors (one byte each), so detection
    /// sees the layout and the file is genuinely readable.</summary>
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

    private static readonly string[] ZImageNames = { "noise_refiner.0.w", "context_refiner.0.w", "cap_embedder.0.w", "x_embedder.w" };
    private static readonly string[] Flux2Names = { "double_blocks.0.w", "single_blocks.0.w", "img_in.w", "txt_in.w" };

    [Fact]
    public void Without_an_override_the_reported_checkpoint_is_the_template_default()
    {
        MediaCheckpointEffective effective = MediaCheckpointResolution.Resolve("image");

        Assert.Equal("z_image_turbo_nvfp4.safetensors", effective.File);
        Assert.False(effective.IsOverridden);
        Assert.Equal("z-image-smoke-api.json", effective.TemplateFile);
        Assert.Contains(effective.ReplacesInputs, input => input.StartsWith("UNETLoader.unet_name", StringComparison.Ordinal));
        Assert.Null(effective.ClearedOverrideFile);
    }

    [Fact]
    public void An_override_is_reported_and_is_cleared_once_its_file_is_removed()
    {
        string file = "zimage_custom.safetensors";
        WriteHeader(Path.Combine(LocalAiPaths.DiffusionModelsPath, file), ZImageNames);
        LocalAiPaths.SetMediaCheckpointOverride("image", file);

        MediaCheckpointEffective overridden = MediaCheckpointResolution.Resolve("image");
        Assert.True(overridden.IsOverridden);
        Assert.Equal(file, overridden.File);

        File.Delete(Path.Combine(LocalAiPaths.DiffusionModelsPath, file));
        MediaCheckpointEffective afterRemoval = MediaCheckpointResolution.Resolve("image");

        Assert.False(afterRemoval.IsOverridden);
        Assert.Equal("z_image_turbo_nvfp4.safetensors", afterRemoval.File);
        Assert.Equal(file, afterRemoval.ClearedOverrideFile);
        Assert.Null(LocalAiPaths.GetMediaCheckpointOverride("image"));
    }

    [Fact]
    public void A_wrong_family_override_is_refused_and_nothing_is_saved()
    {
        string file = "flux2_for_image.safetensors";
        WriteHeader(Path.Combine(LocalAiPaths.DiffusionModelsPath, file), Flux2Names);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => LocalAiPaths.SetMediaCheckpointOverride("image", file));

        Assert.Contains("FLUX.2 Klein", error.Message);
        Assert.Contains("Z-Image Turbo", error.Message);
        Assert.Null(LocalAiPaths.GetMediaCheckpointOverride("image"));
    }

    [Fact]
    public void A_file_whose_family_cannot_be_identified_is_refused_for_the_workflow()
    {
        string path = Path.Combine(_dir, "unknown.safetensors");
        WriteHeader(path, "random.layer.weight");

        MediaDependencyError? refusal = null;
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => MediaCheckpointResolution.EnsureFamilyAllowed("image", path, out refusal));

        Assert.Contains("could not identify", error.Message);
        Assert.Contains("Z-Image Turbo", error.Message);
        Assert.NotNull(refusal);
        Assert.Equal("checkpoint_family_unidentified", refusal!.Reason);
        Assert.Equal("main_model", refusal.Role);
        Assert.Equal("diffusion_models", refusal.ComfyFolder);
        Assert.Contains("Choose a .safetensors checkpoint", refusal.NextAction);
        Assert.Equal(Path.GetFileName(path), refusal.ExpectedFileName);
    }

    [Fact]
    public void A_wrong_family_import_has_a_typed_refusal_and_a_supported_family_remediation()
    {
        string path = Path.Combine(_dir, "flux2_for_image.safetensors");
        WriteHeader(path, Flux2Names);

        MediaDependencyError? refusal = null;
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => MediaCheckpointResolution.EnsureFamilyAllowed("image", path, out refusal));

        Assert.Contains("FLUX.2 Klein", error.Message);
        Assert.NotNull(refusal);
        Assert.Equal("checkpoint_family_unsupported", refusal!.Reason);
        Assert.Equal(Path.GetFileName(path), refusal.ExpectedFileName);
        Assert.Contains("Z-Image Turbo", refusal.NextAction);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Message));
    }

    [Fact]
    public void A_type_without_any_workflow_refuses_every_checkpoint()
    {
        string path = Path.Combine(_dir, "any.safetensors");
        WriteHeader(path, ZImageNames);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => MediaCheckpointResolution.EnsureFamilyAllowed("upscale", path));

        Assert.Contains("no workflow", error.Message);
    }

    [Fact]
    public void A_matching_family_passes_the_gate_for_its_own_type()
    {
        string path = Path.Combine(_dir, "edit.safetensors");
        WriteHeader(path, Flux2Names);

        MediaCheckpointResolution.EnsureFamilyAllowed("edit", path);
    }
}
