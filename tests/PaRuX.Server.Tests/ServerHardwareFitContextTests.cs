using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The server's load-time compatibility gate - the check that actually refuses to load a model -
/// used `file size x 1.20`, which is context-blind. It therefore returned the same verdict for a
/// model at 4k and the same model at 128k, and the second is where the load really runs out of
/// memory. The client-side estimate was corrected first; this covers the gate itself, now that the
/// GGUF reader and KV estimator live in an assembly the server can reach.
///
/// The gate itself needs a real file on disk and real hardware probing, so what is pinned here is
/// the arithmetic it now uses, at the launch parameters it now uses (32k ceiling, q8_0 cache),
/// against hand-checkable values.
/// </summary>
public sealed class ServerHardwareFitContextTests
{
    /// <summary>Ordinary 8B-class grouped-query geometry: 32 layers, hidden 4096, 32 heads,
    /// 8 KV heads, so head_dim = 128.</summary>
    private static GgufModelInfo Model(long declaredContext) => new()
    {
        BlockCount = 32,
        EmbeddingLength = 4096,
        AttentionHeadCount = 32,
        KeyValueHeadCount = 8,
        ContextLength = declaredContext
    };

    [Fact]
    public void The_gate_now_estimates_against_the_context_it_will_actually_launch_with()
    {
        // 2 * 32 layers * 32768 tokens * 8 kv heads * 128 head_dim * (34/32) bytes for q8_0
        //   = 2,281,701,376 bytes ~= 2.13 GiB of KV cache alone, on a 4-5 GiB model.
        long? bytes = KvCacheSizeEstimator.Estimate(Model(131072), 32768, "q8_0", "q8_0");

        Assert.NotNull(bytes);
        Assert.Equal(2_281_701_376L, bytes!.Value);
    }

    [Fact]
    public void Estimating_at_the_cache_type_the_runtime_launches_with_matters_by_almost_half()
    {
        // LocalAiProcessManager launches with --cache-type-k/v q8_0. Estimating against f16 while
        // launching q8_0 would over-reserve by ~47% and refuse models that fit.
        long? f16 = KvCacheSizeEstimator.Estimate(Model(131072), 32768, "f16", "f16");
        long? q8 = KvCacheSizeEstimator.Estimate(Model(131072), 32768, "q8_0", "q8_0");

        Assert.True(q8!.Value < f16!.Value);
        Assert.Equal(f16.Value * 34.0 / 64.0, q8.Value, 0);
    }

    [Fact]
    public void A_declared_context_below_the_launch_ceiling_is_used_as_declared()
    {
        // A model that only supports 8k must not be estimated at 32k - that would reserve four
        // times the cache it can ever use and reject it for no reason.
        long? at8k = KvCacheSizeEstimator.Estimate(Model(8192), 8192, "q8_0", "q8_0");
        long? at32k = KvCacheSizeEstimator.Estimate(Model(8192), 32768, "q8_0", "q8_0");

        Assert.Equal(at32k!.Value / 4, at8k!.Value);
    }

    [Fact]
    public void A_file_without_geometry_produces_no_kv_figure_so_the_gate_keeps_its_old_estimate()
    {
        // The proportional fallback must stay reachable: a model whose header omits the geometry
        // has to remain loadable, not become un-estimatable and therefore refused.
        Assert.Null(KvCacheSizeEstimator.Estimate(new GgufModelInfo(), 32768, "q8_0", "q8_0"));
    }

    [Fact]
    public void The_kv_cache_can_exceed_the_old_flat_twenty_percent_overhead_outright()
    {
        // This is the whole point. On a 4 GiB weights file the old gate reserved 20% = 0.8 GiB for
        // everything non-weight. The real KV cache at 32k is over 2 GiB - so the old estimate was
        // short by more than the entire allowance it made.
        const long weightsBytes = 4L * 1024 * 1024 * 1024;
        long oldOverhead = (long)(weightsBytes * 0.20);
        long kv = KvCacheSizeEstimator.Estimate(Model(131072), 32768, "q8_0", "q8_0")!.Value;

        Assert.True(kv > oldOverhead * 2);
    }
}
