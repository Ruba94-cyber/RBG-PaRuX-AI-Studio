using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// PaRuX estimated a model's loaded footprint as <c>file size x 1.16 + 768 MiB</c>. The KV cache is
/// the largest variable part of that footprint and it does not scale with file size at all - it
/// scales with context length and with the model's key/value head count. The consequence was a
/// single number that could not distinguish the same model at 4k from the same model at 128k, so a
/// variant reported as an "excellent full-GPU fit" could still exhaust VRAM once a long
/// conversation filled the context, and the failure then looked like a runtime bug rather than a
/// capacity one.
///
/// These pin the arithmetic against hand-computed values, and pin the honesty rule that a file
/// which does not declare its geometry gets the old conservative estimate and says so, rather than
/// a precise-looking number built on zeros.
/// </summary>
public sealed class KvCacheSizeEstimatorTests
{
    /// <summary>An 8B-class grouped-query model: 32 layers, hidden 4096, 32 heads, 8 KV heads,
    /// so head_dim = 128. These are ordinary Llama-3-8B numbers, chosen so the expected values can
    /// be checked by hand rather than taken from the implementation.</summary>
    private static GgufModelInfo Gqa8B() => new()
    {
        FileName = "model-Q4_K_M.gguf",
        BlockCount = 32,
        EmbeddingLength = 4096,
        AttentionHeadCount = 32,
        KeyValueHeadCount = 8,
        ContextLength = 8192
    };

    [Fact]
    public void The_cache_is_computed_from_the_models_real_geometry()
    {
        // 2 (K and V) * 32 layers * 4096 tokens * 8 kv heads * 128 head_dim * 2 bytes (f16)
        //   = 536,870,912 bytes = exactly 512 MiB.
        long? bytes = KvCacheSizeEstimator.Estimate(Gqa8B(), contextLength: 4096, "f16", "f16");

        Assert.Equal(512L * 1024L * 1024L, bytes);
    }

    [Fact]
    public void The_cache_scales_linearly_with_context_which_is_the_whole_point()
    {
        long? at4k = KvCacheSizeEstimator.Estimate(Gqa8B(), 4096, "f16", "f16");
        long? at32k = KvCacheSizeEstimator.Estimate(Gqa8B(), 32768, "f16", "f16");

        Assert.Equal(at4k!.Value * 8, at32k!.Value);
    }

    [Fact]
    public void Grouped_query_attention_is_honored_rather_than_assuming_one_kv_head_per_head()
    {
        // Treating this model as if it had 32 KV heads instead of 8 would over-reserve by 4x and
        // wrongly reject models that fit comfortably.
        GgufModelInfo grouped = Gqa8B();
        GgufModelInfo full = Gqa8B();
        full.KeyValueHeadCount = 32;

        long? groupedBytes = KvCacheSizeEstimator.Estimate(grouped, 4096, "f16", "f16");
        long? fullBytes = KvCacheSizeEstimator.Estimate(full, 4096, "f16", "f16");

        Assert.Equal(groupedBytes!.Value * 4, fullBytes!.Value);
    }

    [Fact]
    public void Quantized_cache_types_account_for_their_per_block_scale()
    {
        long? f16 = KvCacheSizeEstimator.Estimate(Gqa8B(), 4096, "f16", "f16");
        long? q8 = KvCacheSizeEstimator.Estimate(Gqa8B(), 4096, "q8_0", "q8_0");

        // q8_0 stores 32 values in 34 bytes, so it is 34/64 of f16 - NOT the nominal half. The
        // difference is ~6%, which is exactly the margin a memory-safety estimate must not lose.
        Assert.Equal(f16!.Value * 34.0 / 64.0, q8!.Value, 0);
        Assert.True(q8.Value < f16.Value);
    }

    [Fact]
    public void An_unknown_cache_type_falls_back_to_the_largest_not_the_cheapest()
    {
        // Guessing low here would under-reserve memory, which is the dangerous direction.
        Assert.Equal(2.0, KvCacheSizeEstimator.BytesPerElement("something-new"));
        // Passing null deliberately: an unknown cache type must fall back to f16, not to the cheapest.
        Assert.Equal(2.0, KvCacheSizeEstimator.BytesPerElement(null!));
    }

    [Fact]
    public void An_explicit_key_length_wins_over_the_derived_head_dimension()
    {
        GgufModelInfo model = Gqa8B();
        model.KeyLength = 256;   // decoupled head dimension, twice the derived 4096/32 = 128
        model.ValueLength = 128;

        long? bytes = KvCacheSizeEstimator.Estimate(model, 4096, "f16", "f16");

        // K at 256 + V at 128 = 1.5x the symmetric 128/128 case.
        long? symmetric = KvCacheSizeEstimator.Estimate(Gqa8B(), 4096, "f16", "f16");
        Assert.Equal((long)(symmetric!.Value * 1.5), bytes!.Value);
    }

    [Theory]
    [InlineData(0, 4096, 32, 32)]      // no layer count
    [InlineData(32, 0, 32, 32)]        // no hidden size
    [InlineData(32, 4096, 0, 0)]       // no head counts at all
    public void A_file_that_does_not_declare_its_geometry_yields_no_number_at_all(
        long blocks, long embedding, long heads, long kvHeads)
    {
        var model = new GgufModelInfo
        {
            BlockCount = blocks == 0 ? null : blocks,
            EmbeddingLength = embedding == 0 ? null : embedding,
            AttentionHeadCount = heads == 0 ? null : heads,
            KeyValueHeadCount = kvHeads == 0 ? null : kvHeads
        };

        Assert.Null(KvCacheSizeEstimator.Estimate(model, 4096, "f16", "f16"));
    }

    [Fact]
    public void A_missing_kv_head_count_falls_back_to_the_attention_head_count()
    {
        // Pre-GQA models declare only head_count. Assuming one KV head would massively
        // under-reserve; assuming none would refuse to estimate a model that is estimable.
        GgufModelInfo model = Gqa8B();
        model.KeyValueHeadCount = null;

        long? bytes = KvCacheSizeEstimator.Estimate(model, 4096, "f16", "f16");

        Assert.NotNull(bytes);
        Assert.Equal(KvCacheSizeEstimator.Estimate(WithKvHeads(32), 4096, "f16", "f16"), bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_nonsensical_context_yields_no_number(long context)
    {
        Assert.Null(KvCacheSizeEstimator.Estimate(Gqa8B(), context, "f16", "f16"));
    }

    private static GgufModelInfo WithKvHeads(long kvHeads)
    {
        GgufModelInfo model = Gqa8B();
        model.KeyValueHeadCount = kvHeads;
        return model;
    }
}

/// <summary>
/// The fit estimate that consumes the above. The behaviour under test is not only "the number is
/// bigger/smaller" but that the estimate reports WHICH basis it used, so a result built on a
/// conservative guess is never presented as if it had been measured.
/// </summary>
public sealed class ModelHardwareFitContextTests
{
    private const long GiB = 1024L * 1024L * 1024L;

    private static RuntimeManagerHardwareStatus Hardware(int vramMib, long ramBytes) => new()
    {
        GpuDetected = vramMib > 0,
        VramTotalMib = vramMib,
        RamTotalBytes = ramBytes,
        GpuAdapters = new List<GpuAdapterStatus>()
    };

    private static GgufModelInfo Model() => new()
    {
        BlockCount = 32,
        EmbeddingLength = 4096,
        AttentionHeadCount = 32,
        KeyValueHeadCount = 8,
        ContextLength = 131072
    };

    [Fact]
    public void The_same_model_needs_measurably_more_memory_at_a_longer_context()
    {
        ModelHardwareFitEstimate atShort = ModelHardwareFitEstimator.Estimate(
            5 * GiB, Hardware(16384, 32 * GiB), Model(), 4096, "f16");
        ModelHardwareFitEstimate atLong = ModelHardwareFitEstimator.Estimate(
            5 * GiB, Hardware(16384, 32 * GiB), Model(), 131072, "f16");

        // The old estimate returned an identical footprint for both, because it never looked at
        // context at all. That is the defect these two lines exist to prevent returning.
        Assert.True(atLong.EstimatedRuntimeBytes > atShort.EstimatedRuntimeBytes);
        Assert.Equal("measured-kv-cache", atShort.EstimateBasis);
        Assert.Equal(4096, atShort.AssumedContextLength);
        Assert.Equal(131072, atLong.AssumedContextLength);
    }

    [Fact]
    public void A_long_context_can_turn_a_comfortable_fit_into_one_that_does_not_fit()
    {
        // 5 GiB of weights on a 16 GiB card is comfortable; the same model's cache at 128k is not.
        ModelHardwareFitEstimate atShort = ModelHardwareFitEstimator.Estimate(
            5 * GiB, Hardware(16384, 32 * GiB), Model(), 4096, "f16");
        ModelHardwareFitEstimate atLong = ModelHardwareFitEstimator.Estimate(
            5 * GiB, Hardware(16384, 32 * GiB), Model(), 131072, "f16");

        Assert.Equal(ModelHardwareFitTier.ExcellentFullGpu, atShort.Tier);
        Assert.NotEqual(ModelHardwareFitTier.ExcellentFullGpu, atLong.Tier);
    }

    [Fact]
    public void A_model_without_geometry_keeps_the_old_estimate_and_says_so()
    {
        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(
            5 * GiB, Hardware(16384, 32 * GiB), new GgufModelInfo(), 4096, "f16");

        Assert.Equal("file-size-heuristic", estimate.EstimateBasis);
        Assert.Null(estimate.KvCacheBytes);
        Assert.Null(estimate.AssumedContextLength);
        Assert.Contains("could not be computed", estimate.Detail);
    }

    [Fact]
    public void The_original_two_argument_call_is_unchanged()
    {
        // Every existing caller passes no metadata, and must keep the behaviour it had.
        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(5 * GiB, Hardware(16384, 32 * GiB));

        Assert.Equal("file-size-heuristic", estimate.EstimateBasis);
        Assert.Equal(
            (long)System.Math.Ceiling(5 * GiB * 1.16) + 768L * 1024L * 1024L,
            estimate.EstimatedRuntimeBytes);
    }
}
