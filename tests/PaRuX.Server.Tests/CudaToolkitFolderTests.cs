using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CudaToolkitFolderTests
{
    [Theory]
    [InlineData("llama-b11871-bin-win-cuda-13.4-x64.zip", "13.4")]
    [InlineData("cudart-llama-bin-win-cuda-13.4-x64.zip", "13.4")]
    [InlineData("llama-b10600-bin-win-cuda-cu12.4-x64.zip", "12.4")]
    [InlineData("llama.cpp-b10549-cuda12.4", "12.4")]
    [InlineData("llama-b11871-bin-win-vulkan-x64.zip", null)]
    [InlineData(null, null)]
    public void ParseCudaToolkit_reads_the_version_from_the_asset_or_folder_name(string? name, string? expected) =>
        Assert.Equal(expected, LocalAiPaths.ParseCudaToolkit(name));

    [Theory]
    [InlineData("b11871", "13.4", "llama.cpp-b11871-cuda13.4")]
    [InlineData("b11871", "12.4", "llama.cpp-b11871-cuda12.4")]
    [InlineData("b10549", null, "llama.cpp-b10549-cuda12.4")]
    public void RuntimeFolderName_carries_the_real_cuda_toolkit(string tag, string? toolkit, string expected) =>
        Assert.Equal(expected, LocalAiPaths.RuntimeFolderName("text-cuda", tag, toolkit));

    [Fact]
    public void Vulkan_folder_name_ignores_a_toolkit() =>
        Assert.Equal("llama.cpp-b1-vulkan", LocalAiPaths.RuntimeFolderName("text-vulkan", "b1", "13.4"));

    [Theory]
    [InlineData("llama.cpp-b11871-cuda13.4", "b11871", "13.4")]
    [InlineData("llama.cpp-b10549-cuda12.4", "b10549", "12.4")]
    public void TryParseRuntimeFolder_recognises_every_cuda_version(string folder, string tag, string toolkit)
    {
        Assert.True(LocalAiPaths.TryParseRuntimeFolder("text-cuda", folder, out string? t, out string? k));
        Assert.Equal(tag, t);
        Assert.Equal(toolkit, k);
        Assert.Equal(tag, LocalAiPaths.TryParseRuntimeBuildTag("text-cuda", folder));
        Assert.Null(LocalAiPaths.TryParseRuntimeBuildTag("text-vulkan", folder));
    }

    // Selection: Blackwell (cc >= 12.0) with NVIDIA driver >= 580 (DXGI 32.0.15.8142 = 581.42) -> CUDA 13, else 12.4.
    [Theory]
    [InlineData(12.0, "32.0.15.8142", 13)]
    [InlineData(12.0, "32.0.15.7000", 12)]
    [InlineData(8.9, "32.0.15.8142", 12)]
    [InlineData(7.5, "32.0.16.1714", 12)]
    [InlineData(null, "32.0.16.1714", 12)]
    [InlineData(12.0, null, 12)]
    public void Blackwell_with_a_current_driver_selects_cuda13_everything_else_cuda12(double? cc, string? driver, int major) =>
        Assert.Equal(major, RuntimeBuildCatalog.PreferredCudaMajor(cc, driver));

    private static string Isolated() => Path.Combine(Path.GetTempPath(), "parux-cuda-" + Guid.NewGuid().ToString("N"));

    private static void FakeBuild(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, LocalAiPaths.LlamaServerExecutable), "x");
    }

    [Fact]
    public void Inventory_detects_both_toolkits_and_keeps_legacy_12_4_working()
    {
        string root = Isolated();
        try
        {
            using (LocalAiPaths.UseRootForTests(root))
            {
                FakeBuild(Path.Combine(LocalAiPaths.RuntimeRoot, "llama.cpp-b11871-cuda12.4"));
                FakeBuild(Path.Combine(LocalAiPaths.RuntimeRoot, "llama.cpp-b11871-cuda13.4"));
                Assert.Equal(new[] { "12.4", "13.4" }, LocalAiPaths.ListInstalledCudaToolkits("b11871").OrderBy(x => x).ToArray());
                Assert.Single(LocalAiPaths.ListInstalledRuntimeBuildTags("text-cuda"));

                LocalAiPaths.SetActiveRuntimeBuildTag("text-cuda", "b11871", "12.4");
                Assert.EndsWith("llama.cpp-b11871-cuda12.4", LocalAiPaths.RuntimeCudaPath);
                Assert.Equal("llama.cpp (CUDA 12.4)", ComponentInventoryService.WithCudaToolkit("llama.cpp (CUDA)", LocalAiPaths.RuntimeCudaPath));

                LocalAiPaths.SetActiveRuntimeBuildTag("text-cuda", "b11871", "13.4");
                Assert.EndsWith("llama.cpp-b11871-cuda13.4", LocalAiPaths.RuntimeCudaPath);
                Assert.Equal("llama.cpp (CUDA 13.4)", ComponentInventoryService.WithCudaToolkit("llama.cpp (CUDA)", LocalAiPaths.RuntimeCudaPath));
                Assert.Equal("13.4", LocalAiPaths.GetActiveCudaToolkit());

                LocalAiPaths.ClearActiveRuntimeBuildTag("text-cuda");
                Assert.Null(LocalAiPaths.GetActiveCudaToolkit());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Legacy_only_folder_resolves_without_any_persisted_toolkit()
    {
        string root = Isolated();
        try
        {
            using (LocalAiPaths.UseRootForTests(root))
            {
                FakeBuild(Path.Combine(LocalAiPaths.RuntimeRoot, "llama.cpp-b10549-cuda12.4"));
                Assert.Equal("12.4", LocalAiPaths.ResolveCudaToolkit("b10549"));
                Assert.EndsWith("llama.cpp-b10549-cuda12.4", LocalAiPaths.RuntimeCudaPath);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
