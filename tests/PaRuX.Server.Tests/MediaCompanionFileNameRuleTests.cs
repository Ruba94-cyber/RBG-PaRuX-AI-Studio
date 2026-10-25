using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaCompanionFileNameRuleTests
{
    private const string ValidManifest = """
        {
          "schemaVersion": 1,
          "embeddedStems": { "textEncoder": ["text_encoders."], "vae": ["vae."] },
          "families": [{
            "id": "demo",
            "displayName": "Demo",
            "support": "supported",
            "pipelines": ["image"],
            "templateFile": "z-image-smoke-api.json",
            "mainLoader": { "node": "1", "classType": "UNETLoader", "input": "unet_name", "folder": "diffusion_models" },
            "requiredNodes": ["UNETLoader"],
            "signature": { "requiredStems": ["a."] },
            "companions": [{ "role": "text_encoder", "folder": "text_encoders", "fileName": "te.safetensors", "clipType": "demo", "source": {} }]
          }]
        }
        """;

    [Theory]
    [InlineData("D:weights.pt")]
    [InlineData("weights:stream.pt")]
    public void Parse_rejects_drive_and_alternate_stream_colons_with_a_typed_error(string fileName)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => MediaCompanionRegistry.Parse(ManifestWithFileName(fileName)));

        Assert.Contains("plain file name", error.Message);
    }

    [Fact]
    public void Shipped_manifest_entries_still_load()
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Shipped;
        RegistryFamily family = Assert.Single(registry.Families, candidate => candidate.Id == "z-image");

        Assert.Contains(family.Companions, companion => companion.FileName == "qwen_3_4b.safetensors");
    }

    [Fact]
    public void Plain_names_remain_valid_and_existing_traversal_and_extension_rules_remain()
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Parse(ManifestWithFileName("te.safetensors"));
        Assert.Equal("te.safetensors", Assert.Single(Assert.Single(registry.Families).Companions).FileName);

        InvalidDataException traversalError = Assert.Throws<InvalidDataException>(
            () => MediaCompanionRegistry.Parse(ManifestWithFileName("../evil.safetensors")));
        Assert.Contains("plain file name", traversalError.Message);

        InvalidDataException extensionError = Assert.Throws<InvalidDataException>(
            () => MediaCompanionRegistry.Parse(ManifestWithFileName("te.txt")));
        Assert.Contains("must be a .safetensors, .pth or .pt file", extensionError.Message);
    }

    private static string ManifestWithFileName(string fileName) =>
        ValidManifest.Replace("te.safetensors", fileName, StringComparison.Ordinal);
}
