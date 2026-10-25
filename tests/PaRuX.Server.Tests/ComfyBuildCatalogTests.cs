using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComfyBuildCatalogTests
{
    private static IReadOnlyList<RuntimeReleaseAsset> AllAssets => new RuntimeReleaseAsset[]
    {
        new("ComfyUI_windows_portable_nvidia.7z", "https://example/nvidia", 10, "sha256:" + new string('a', 64)),
        new("ComfyUI_windows_portable_nvidia_cu126.7z", "https://example/nvidia-cu126", 11),
        new("ComfyUI_windows_portable_amd.7z", "https://example/amd", 12),
        new("ComfyUI_windows_portable_intel.7z", "https://example/intel", 13),
        new("ComfyUI.tar.gz", "https://example/source", 1)
    };

    private static GpuInventoryStatus Gpu(uint vendor) => new()
    {
        Available = true,
        Source = "test",
        Adapters = new List<GpuAdapterStatus>
        {
            new() { Index = 0, Name = "test", VendorId = vendor, DedicatedVideoMemoryBytes = 16L * 1024 * 1024 * 1024 }
        }
    };

    [Fact]
    public void ListSelections_exposes_all_official_windows_variants_and_marks_nvidia_recommended()
    {
        IReadOnlyList<ComfyBuildCatalog.Selection> selections = ComfyBuildCatalog.ListSelections(AllAssets, Gpu(0x10DE));

        Assert.Equal(4, selections.Count);
        Assert.Equal("nvidia", Assert.Single(selections.Where(s => s.Recommended)).Variant);
        Assert.Contains(selections, s => s.Variant == "amd" && s.Asset.Name.EndsWith("_amd.7z"));
        Assert.DoesNotContain(selections, s => s.Asset.Name.EndsWith(".tar.gz"));
    }

    [Theory]
    [InlineData(0x10DEu, "nvidia")]
    [InlineData(0x1002u, "amd")]
    [InlineData(0x8086u, "intel")]
    public void InferRecommendedVariant_uses_real_adapter_vendor_ids(uint vendor, string expected)
    {
        Assert.Equal(expected, ComfyBuildCatalog.InferRecommendedVariant(Gpu(vendor)));
    }

    [Fact]
    public void InferRecommendedVariant_prefers_discrete_nvidia_when_multiple_vendors_are_present()
    {
        GpuInventoryStatus gpu = Gpu(0x1002);
        gpu.Adapters.Add(new GpuAdapterStatus { Index = 1, Name = "NVIDIA", VendorId = 0x10DE });
        Assert.Equal("nvidia", ComfyBuildCatalog.InferRecommendedVariant(gpu));
    }

    [Fact]
    public void SelectForInstall_honors_an_explicit_compatibility_variant()
    {
        ComfyBuildCatalog.Selection selected = ComfyBuildCatalog.SelectForInstall(AllAssets, Gpu(0x10DE), "nvidia_cu126");
        Assert.Equal("nvidia-cu126", selected.Variant);
        Assert.EndsWith("_nvidia_cu126.7z", selected.Asset.Name);
    }

    [Fact]
    public void SelectForInstall_falls_back_to_cu126_when_release_has_no_normal_nvidia_asset()
    {
        IReadOnlyList<RuntimeReleaseAsset> assets = AllAssets.Where(a => a.Name != "ComfyUI_windows_portable_nvidia.7z").ToList();
        ComfyBuildCatalog.Selection selected = ComfyBuildCatalog.SelectForInstall(assets, Gpu(0x10DE), null);
        Assert.Equal("nvidia-cu126", selected.Variant);
    }

    [Fact]
    public void Auto_selection_fails_honestly_when_gpu_inventory_is_unavailable()
    {
        var gpu = new GpuInventoryStatus { Available = false, Source = "test-error" };
        Assert.Throws<InvalidOperationException>(() => ComfyBuildCatalog.SelectForInstall(AllAssets, gpu, null));
    }

    [Fact]
    public void Explicit_unknown_variant_is_rejected_before_asset_matching()
    {
        Assert.Throws<InvalidDataException>(() => ComfyBuildCatalog.SelectForInstall(AllAssets, Gpu(0x10DE), "mystery"));
    }
}
