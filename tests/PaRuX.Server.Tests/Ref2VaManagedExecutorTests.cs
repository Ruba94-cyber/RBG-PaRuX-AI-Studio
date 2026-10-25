using Xunit;

namespace PaRuX.Server.Tests;

public sealed class Ref2VaManagedExecutorTests
{
    [Theory]
    [InlineData(0, 1344, 1344)]
    [InlineData(255, 1344, 256)]
    [InlineData(1345, 1344, 1344)]
    [InlineData(1927, 1344, 1920)]
    public void Dimensions_are_clamped_and_aligned_to_eight(int requested, int fallback, int expected)
    {
        Assert.Equal(expected, Ref2VaManagedExecutor.NormalizeDimension(requested, fallback));
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(8, 8)]
    [InlineData(121, 120)]
    [InlineData(124, 124)]
    [InlineData(483, 480)]
    public void Frame_count_is_bounded_and_aligned_to_the_pinned_workflow(int requested, int expected)
    {
        Assert.Equal(expected, Ref2VaManagedExecutor.NormalizeFrameCount(requested));
    }

    [Fact]
    public void Required_assets_match_the_pinned_api_workflow_contract()
    {
        Assert.Equal(6, Ref2VaManagedExecutor.RequiredAssets.Length);
        Assert.Contains(Ref2VaManagedExecutor.RequiredAssets,
            asset => asset == ("diffusion_models", "minimax_h3_ref2va_pruned_int8_convrot.safetensors"));
        Assert.Contains(Ref2VaManagedExecutor.RequiredAssets,
            asset => asset == ("loras", "fasth3_vsa_gate.safetensors"));
        Assert.Contains(Ref2VaManagedExecutor.RequiredAssets,
            asset => asset == ("loras", "minimax_h3_ref2v_turbo_4step_v0.1_comfyui_bf16.safetensors"));
        Assert.Contains(Ref2VaManagedExecutor.RequiredAssets,
            asset => asset == ("text_encoders", "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors"));
        Assert.Contains(Ref2VaManagedExecutor.RequiredAssets,
            asset => asset == ("vae", "minimax_h3_video_vae_fp16.safetensors"));
        Assert.Contains(Ref2VaManagedExecutor.RequiredAssets,
            asset => asset == ("vae", "minimax_h3_audio_vae_fp32.safetensors"));
    }

    [Fact]
    public void Source_identity_is_immutable_and_catalog_aligned()
    {
        Assert.Equal(MediaEngineCatalogService.Ref2VaSourceRevision, Ref2VaManagedExecutor.SourceRevision);
        Assert.Equal("Kablex", Ref2VaManagedExecutor.SourceOwner);
        Assert.Equal("ComfyUI-Ref2VA-VSA", Ref2VaManagedExecutor.SourceRepository);
        Assert.True(Ref2VaManagedExecutor.MinimumDedicatedVramBytes >= 23L * 1024 * 1024 * 1024);
    }
}
