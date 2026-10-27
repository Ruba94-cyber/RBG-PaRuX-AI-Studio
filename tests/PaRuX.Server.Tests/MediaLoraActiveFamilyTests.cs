using System.Buffers.Binary;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>The LoRA stack's dry run must know the family of a full checkpoint (SDXL lives in models/checkpoints), so a LoRA for another family is flagged before a job.</summary>
public sealed class MediaLoraActiveFamilyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-loraactive-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDataRoot;
    private readonly IDisposable _root;

    public MediaLoraActiveFamilyTests()
    {
        _previousDataRoot = PaRuXDataRoot.Path;
        PaRuXDataRoot.UseForTests(Path.Combine(_dir, "data"));
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_dir, "root"));
        Directory.CreateDirectory(LocalAiPaths.CheckpointsPath);
        Directory.CreateDirectory(LocalAiPaths.DiffusionModelsPath);
        Directory.CreateDirectory(LocalAiPaths.ConfigDir);
        File.WriteAllText(LocalAiPaths.ImageWorkflowTemplate,
            "{\"prompt\":{\"1\":{\"class_type\":\"UNETLoader\",\"inputs\":{\"unet_name\":\"z_image_turbo_nvfp4.safetensors\",\"weight_dtype\":\"default\"}}}}");
    }

    public void Dispose()
    {
        _root.Dispose();
        PaRuXDataRoot.UseForTests(_previousDataRoot);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Active_family_of_an_sdxl_full_checkpoint_is_sdxl_even_when_the_name_gives_no_hint()
    {
        string[] names = { "model.diffusion_model.input_blocks.0.w", "conditioner.embedders.0.transformer.w", "conditioner.embedders.1.model.w" };
        var parts = names.Select((n, i) => "\"" + n + "\":{\"dtype\":\"U8\",\"shape\":[1],\"data_offsets\":[" + i + "," + (i + 1) + "]}");
        byte[] header = Encoding.UTF8.GetBytes("{" + string.Join(",", parts) + "}");
        var bytes = new byte[8 + header.Length + names.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), (ulong)header.Length);
        header.CopyTo(bytes, 8);
        string file = "mystery_model_v1.safetensors";
        File.WriteAllBytes(Path.Combine(LocalAiPaths.CheckpointsPath, file), bytes);
        LocalAiPaths.SetMediaCheckpointOverride("image", file);

        (string? key, string? active) = MediaLoraStack.ActiveCheckpointFamily("image");

        Assert.Equal(file, active);
        Assert.Equal("sdxl", key);
    }
}
