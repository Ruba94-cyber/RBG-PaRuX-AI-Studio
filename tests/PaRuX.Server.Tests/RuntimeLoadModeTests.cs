using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Measured 2026-09-29 (RTX 5080 16 GB, 25.8 GB RAM, Qwen3.8-27B IQ4_XS fully offloaded):
/// mmap kept ~12.4 GB of the file resident in system RAM (free 13.6 → 0.8 GB) while "none"
/// left 13.2 GB free, loaded 2x faster and decoded at the same speed.
/// </summary>
public sealed class RuntimeLoadModeTests
{
    private const long MiB = 1024L * 1024L;

    [Theory]
    [InlineData(13_270L * MiB, 16_303L * MiB, "none")] // 27B IQ4_XS on RTX 5080: measured case
    [InlineData(2_550L * MiB, 16_303L * MiB, "none")]  // 4B Q4_K_M
    [InlineData(15_500L * MiB, 16_303L * MiB, "auto")] // leaves no CUDA/KV headroom: keep mmap
    [InlineData(20_000L * MiB, 16_303L * MiB, "auto")] // partial offload
    [InlineData(0L, 16_303L * MiB, "auto")]
    [InlineData(2_550L * MiB, 0L, "auto")]
    public void Weights_that_fit_the_gpu_load_without_mmap(long weights, long vram, string expected)
    {
        Assert.Equal(expected, RuntimeLaunchOptimizer.RecommendLoadMode(weights, vram));
    }
}
