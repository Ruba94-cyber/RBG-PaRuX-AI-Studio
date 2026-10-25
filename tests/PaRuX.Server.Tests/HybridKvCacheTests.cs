using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Geometry of the local Qwen3.8-27B IQ4_XS GGUF (qwen35: 65 blocks, full_attention_interval=4,
/// 4 KV heads x 256). Measured on RTX 5080 16 GB with q8_0 cache: VRAM 13,488 / 13,794 / 14,406 MiB
/// at 8k / 16k / 32k context (~38 KB per token); llama.cpp's own --fit accepted 16k and warned at 32k.
/// </summary>
public sealed class HybridKvCacheTests
{
    private const long MiB = 1024L * 1024L;

    private static GgufModelInfo Qwen27B(bool hybrid = true) => new()
    {
        BlockCount = 65,
        FullAttentionInterval = hybrid ? 4 : null,
        AttentionHeadCount = 24,
        KeyValueHeadCount = 4,
        EmbeddingLength = 5120,
        KeyLength = 256,
        ValueLength = 256,
        ContextLength = 262_144
    };

    [Fact]
    public void Only_full_attention_blocks_allocate_kv_cache()
    {
        Assert.Equal(17, KvCacheSizeEstimator.KvCacheLayers(Qwen27B()));
        Assert.Equal(65, KvCacheSizeEstimator.KvCacheLayers(Qwen27B(hybrid: false)));

        long perToken = KvCacheSizeEstimator.Estimate(Qwen27B(), 1024, "q8_0", "q8_0")!.Value / 1024;
        // Measured growth ~38 KB/token; the estimate must be in that range, not ~3.8x above it.
        Assert.InRange(perToken, 30_000, 40_000);
    }

    [Theory]
    [InlineData(14_252_845_984L, 16384)] // Qwen3.8-27B-UD-IQ4_XS (13.27 GiB): llama.cpp's own fit accepted 16k
    [InlineData(15_082_506_720L, 8192)]  // 27B noMTP (14.05 GiB): documented CPU-spill case keeps 8k
    [InlineData(2_740_937_888L, 32768)]  // 4B-sized weights (27B KV geometry as a conservative proxy): coding gets 32k
    public void Rtx5080_coding_context_matches_measured_fit(long weightsBytes, long expected)
    {
        const long dxgiDedicatedBytes = 16_753_098_752L; // what GpuInventoryService reports on this PC
        long usable = dxgiDedicatedBytes - 1024L * MiB;   // --fit-target
        ContextCapacityPlanner.ContextCapacity capacity = ContextCapacityPlanner.Plan(Qwen27B(), usable, weightsBytes);

        Assert.Equal(expected, PaRuX.Server.RuntimePerformanceTuner.ChooseContext("coding", 8192, 0, capacity));
    }
}
