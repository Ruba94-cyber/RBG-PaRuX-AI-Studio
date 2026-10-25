using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Upstream llama.cpp now marks every release as a prerelease and publishes both a CUDA 12.4 and a CUDA 13.x
/// Windows server build. Skipping prereleases made the catalog empty for every backend, and taking the first
/// "cuda" asset picked 12.4, which has no native Blackwell (sm_120) kernels and JIT-compiles from PTX on first use.
/// </summary>
public sealed class RuntimeCudaVariantSelectionTests
{
    private const string Base = "https://github.com/ggml-org/llama.cpp/releases/download/b11871/";

    private const string Json = """
    [{"tag_name":"b11871","prerelease":true,"draft":false,"published_at":"2026-10-25T00:00:00Z","assets":[
      {"name":"cudart-llama-bin-win-cuda-12.4-x64.zip","browser_download_url":"https://github.com/ggml-org/llama.cpp/releases/download/b11871/cudart-llama-bin-win-cuda-12.4-x64.zip","size":10},
      {"name":"cudart-llama-bin-win-cuda-13.4-x64.zip","browser_download_url":"https://github.com/ggml-org/llama.cpp/releases/download/b11871/cudart-llama-bin-win-cuda-13.4-x64.zip","size":11},
      {"name":"llama-b11871-bin-win-cuda-12.4-x64.zip","browser_download_url":"https://github.com/ggml-org/llama.cpp/releases/download/b11871/llama-b11871-bin-win-cuda-12.4-x64.zip","size":12},
      {"name":"llama-b11871-bin-win-cuda-13.4-x64.zip","browser_download_url":"https://github.com/ggml-org/llama.cpp/releases/download/b11871/llama-b11871-bin-win-cuda-13.4-x64.zip","size":13},
      {"name":"llama-b11871-bin-win-vulkan-x64.zip","browser_download_url":"https://github.com/ggml-org/llama.cpp/releases/download/b11871/llama-b11871-bin-win-vulkan-x64.zip","size":14}]},
     {"tag_name":"b1","prerelease":false,"draft":true,"assets":[]}]
    """;

    [Fact]
    public void Prereleases_are_listed_when_requested_and_drafts_never_are()
    {
        Assert.Empty(RuntimeBuildCatalog.ParseReleases(Json));
        List<RuntimeReleaseInfo> all = RuntimeBuildCatalog.ParseReleases(Json, includePrereleases: true);
        Assert.Equal("b11871", Assert.Single(all).Tag);
    }

    [Theory]
    [InlineData(12, "llama-b11871-bin-win-cuda-12.4-x64.zip")]
    [InlineData(13, "llama-b11871-bin-win-cuda-13.4-x64.zip")]
    public void The_preferred_cuda_major_picks_the_matching_server_build_and_its_runtime_bundle(int major, string expected)
    {
        IReadOnlyList<RuntimeReleaseAsset> assets = RuntimeBuildCatalog.ParseReleases(Json, true)[0].Assets;
        RuntimeReleaseAsset? asset = RuntimeBuildCatalog.SelectAsset(
            "text-cuda", assets, RuntimeBuildCatalog.RuntimePlatform.WindowsX64, major);
        Assert.Equal(expected, asset!.Name);
        RuntimeReleaseAsset? deps = RuntimeBuildCatalog.SelectCudaDependencyAsset(
            asset, assets, RuntimeBuildCatalog.RuntimePlatform.WindowsX64);
        Assert.Equal(expected.Replace("llama-b11871-bin-", "cudart-llama-bin-"), deps!.Name);
    }

    [Fact]
    public void A_preferred_major_that_is_not_published_falls_back_to_what_is()
    {
        var assets = new List<RuntimeReleaseAsset>
        {
            new("llama-b11871-bin-win-cuda-12.4-x64.zip", Base + "llama-b11871-bin-win-cuda-12.4-x64.zip", 1)
        };
        Assert.Equal("llama-b11871-bin-win-cuda-12.4-x64.zip",
            RuntimeBuildCatalog.SelectAsset("text-cuda", assets, RuntimeBuildCatalog.RuntimePlatform.WindowsX64, 13)!.Name);
    }

    [Theory]
    [InlineData(12.0, "32.0.16.1714", 13)]   // RTX 5080, driver 617.14
    [InlineData(12.0, "32.0.15.5710", 12)]   // Blackwell on driver 571.10, older than the CUDA 13 minimum
    [InlineData(8.9, "32.0.16.1714", 12)]    // Ada keeps the broadly compatible build
    [InlineData(null, "32.0.16.1714", 12)]   // unknown capability
    [InlineData(12.0, null, 12)]             // unknown driver
    [InlineData(12.0, "garbage", 12)]
    public void Cuda_major_follows_gpu_generation_and_driver(double? cc, string? driver, int expected) =>
        Assert.Equal(expected, RuntimeBuildCatalog.PreferredCudaMajor(cc, driver));

    [Theory]
    [InlineData("32.0.16.1714", 617.14)]
    [InlineData("32.0.15.6190", 561.90)]
    [InlineData("31.0.101.5186", null)]
    [InlineData(null, null)]
    public void Dxgi_driver_versions_map_to_nvidia_numbers(string? dxgi, double? expected)
    {
        double? parsed = RuntimeBuildCatalog.ParseNvidiaDriver(dxgi);
        if (expected is null) Assert.Null(parsed); else Assert.Equal(expected.Value, parsed!.Value, 2);
    }
}
