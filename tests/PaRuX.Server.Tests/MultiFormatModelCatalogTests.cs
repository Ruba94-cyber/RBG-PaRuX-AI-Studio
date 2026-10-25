using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MultiFormatModelCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-multiformat-models-" + Guid.NewGuid().ToString("N"));

    public MultiFormatModelCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Exl3_and_onnx_variants_can_coexist_for_same_logical_model()
    {
        MultiFormatModelCatalog catalog = Catalog();
        string exl3 = Path.Combine(catalog.GetManagedVariantRoot(ManagedModelFormat.Exl3), "qwen", "rev-a", "4bpw");
        Directory.CreateDirectory(exl3);
        File.WriteAllText(Path.Combine(exl3, "config.json"), "{}");
        File.WriteAllBytes(Path.Combine(exl3, "model.safetensors"), new byte[] { 1, 2, 3 });
        string onnx = Path.Combine(catalog.GetManagedVariantRoot(ManagedModelFormat.Onnx), "qwen", "rev-a", "fp16");
        Directory.CreateDirectory(onnx);
        File.WriteAllBytes(Path.Combine(onnx, "model.onnx"), new byte[] { 4, 5, 6 });
        File.WriteAllText(Path.Combine(onnx, "genai_config.json"), "{}");
        File.WriteAllText(Path.Combine(onnx, "chat_template.jinja"), "{{ messages }}");

        ManagedModelCatalogEntry exl3Entry = catalog.Register(new ManagedModelVariantIdentity(
            "Qwen/Qwen3", "rev-a", "exl3-4bpw", ManagedModelFormat.Exl3, "4.0bpw", exl3));
        ManagedModelCatalogEntry onnxEntry = catalog.Register(new ManagedModelVariantIdentity(
            "Qwen/Qwen3", "rev-a", "onnx-fp16", ManagedModelFormat.Onnx, "fp16", onnx));

        IReadOnlyList<ManagedModelCatalogEntry> entries = catalog.List(
            Array.Empty<ManagedModelInfo>(), new LocalInferenceEngineRegistry());

        Assert.Equal(2, entries.Count);
        Assert.NotEqual(exl3Entry.Variant.StableKey, onnxEntry.Variant.StableKey);
        Assert.Contains(LocalInferenceEngineRegistry.ExLlamaV3Id,
            entries.Single(entry => entry.Variant.Format == ManagedModelFormat.Exl3).CompatibleEngineIds);
        Assert.Contains(LocalInferenceEngineRegistry.WindowsMlGenAiId,
            entries.Single(entry => entry.Variant.Format == ManagedModelFormat.Onnx).CompatibleEngineIds);
        Assert.DoesNotContain(LocalInferenceEngineRegistry.TensorRtRtxWindowsMlId,
            entries.Single(entry => entry.Variant.Format == ManagedModelFormat.Onnx).CompatibleEngineIds);
    }

    [Fact]
    public void Legacy_gguf_is_projected_without_rewriting_old_library()
    {
        MultiFormatModelCatalog catalog = Catalog();
        string path = Path.Combine(_root, "legacy-Q4_K_M.gguf");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        var legacy = new ManagedModelInfo
        {
            FileName = Path.GetFileName(path),
            FilePath = path,
            SizeBytes = 4,
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(path),
            Quantization = "Q4_K_M",
            RepoId = "owner/model"
        };

        ManagedModelCatalogEntry entry = Assert.Single(catalog.List(
            new[] { legacy }, new LocalInferenceEngineRegistry()));

        Assert.Equal(ManagedModelFormat.Gguf, entry.Variant.Format);
        Assert.Equal("owner/model", entry.Variant.LogicalModelId);
        Assert.Equal(path, entry.Variant.ArtifactPath);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Invalid_exl3_structure_is_rejected()
    {
        MultiFormatModelCatalog catalog = Catalog();
        string exl3 = Path.Combine(catalog.GetManagedVariantRoot(ManagedModelFormat.Exl3), "broken");
        Directory.CreateDirectory(exl3);
        File.WriteAllText(Path.Combine(exl3, "config.json"), "{}");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => catalog.Register(
            new ManagedModelVariantIdentity(
                "owner/model", "rev", "broken", ManagedModelFormat.Exl3, "4bpw", exl3)));

        Assert.Contains("safetensors", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Managed_registration_cannot_escape_library_root()
    {
        MultiFormatModelCatalog catalog = Catalog();
        string outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".onnx");
        File.WriteAllBytes(outside, new byte[] { 1 });
        try
        {
            Assert.Throws<InvalidDataException>(() => catalog.Register(new ManagedModelVariantIdentity(
                "owner/model", "rev", "onnx", ManagedModelFormat.Onnx, "fp16", outside)));
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    // These tests are about catalog coexistence, not hardware. The adapter is made explicit here so the EXL3
    // assertions do not depend on the host GPU; the NVIDIA requirement has its own tests.
    private MultiFormatModelCatalog Catalog() => new(
        _root,
        Path.Combine(_root, ".catalog.json"),
        new ManagedModelRevisionStore(Path.Combine(_root, ".revisions.json")),
        hasNvidiaCudaAdapter: () => true);
}
