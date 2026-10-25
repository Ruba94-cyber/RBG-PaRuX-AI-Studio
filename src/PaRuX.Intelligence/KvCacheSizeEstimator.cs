namespace PaRuX.Intelligence;

/// <summary>
/// How much memory a loaded model's KV cache really needs, computed from the model's own
/// declared attention geometry rather than assumed to be a percentage of its file size.
///
/// Why this exists: PaRuX estimated runtime memory as <c>file size x a fixed factor</c>. The KV
/// cache does not scale with file size at all - it scales with CONTEXT LENGTH - so that
/// estimate is wrong in both directions and worst exactly where it matters. A 27B model at 4k
/// context needs a fraction of the cache the same model needs at 128k, and a grouped-query
/// model needs a small fraction of what its parameter count suggests. The practical symptom is
/// a model that the fit estimate says fits, which then runs out of VRAM once a long
/// conversation fills the context.
///
/// The arithmetic is the same one llama.cpp performs when it allocates the cache:
///
///   bytes = 2 (K and V) x layers x context x kv_heads x head_dim x bytes_per_element
///
/// with <c>head_dim = embedding_length / head_count</c> unless the file declares an explicit
/// key/value length (models with a decoupled head dimension do). Under grouped-query attention
/// <c>kv_heads &lt; head_count</c>, which is where most of the saving comes from.
///
/// Every input comes from the GGUF header. When the file does not declare enough of them the
/// answer is null - "this cannot be computed for this file" - never a fabricated number.
/// </summary>
public static class KvCacheSizeEstimator
{
    /// <summary>
    /// Bytes per cached element for the cache quantization llama-server is launched with
    /// (<c>--cache-type-k</c> / <c>--cache-type-v</c>). The quantized types carry a per-block
    /// scale, so they are not whole numbers: q8_0 stores 32 values in 34 bytes and q4_0 stores
    /// 32 in 18. Using the nominal 1.0 and 0.5 would under-estimate the cache by ~6% and ~12%,
    /// which is the wrong direction for a memory-safety estimate.
    /// </summary>
    public static double BytesPerElement(string cacheType) => (cacheType ?? "f16").Trim().ToLowerInvariant() switch
    {
        "f32" => 4.0,
        "f16" or "bf16" => 2.0,
        "q8_0" => 34.0 / 32.0,
        "q5_1" => 24.0 / 32.0,
        "q5_0" => 22.0 / 32.0,
        "q4_1" => 20.0 / 32.0,
        "q4_0" => 18.0 / 32.0,
        // An unrecognized cache type must not silently become the cheapest option. f16 is
        // llama.cpp's own default and the safest assumption when the type is unknown.
        _ => 2.0
    };

    /// <summary>
    /// The KV cache size in bytes for <paramref name="contextLength"/> tokens, or null when
    /// <paramref name="model"/> did not declare the geometry needed to compute it.
    /// </summary>
    /// <param name="contextLength">Tokens the runtime will be launched with. Not the model's
    /// maximum: PaRuX picks a working context, and reserving for a maximum the user will never
    /// configure would reject models that fit perfectly well.</param>
    /// <summary>
    /// Blocks that allocate a per-token KV cache. For a hybrid model declaring
    /// full_attention_interval = N only every Nth block does (rounded up, the safe direction).
    /// Measured on Qwen3.8-27B IQ4_XS (65 blocks, interval 4): VRAM grew ~38 KB per context token,
    /// matching 17 KV blocks; counting all 65 over-estimated the cache ~3.8x and pinned context to 8k.
    /// </summary>
    public static long? KvCacheLayers(GgufModelInfo model)
    {
        long? blocks = Positive(model?.BlockCount);
        if (blocks == null) return null;
        long? interval = Positive(model!.FullAttentionInterval);
        return interval is > 1 ? (blocks.Value + interval.Value - 1) / interval.Value : blocks;
    }

    public static long? Estimate(
        GgufModelInfo model, long contextLength, string keyCacheType = "f16", string? valueCacheType = null)
    {
        if (model == null || contextLength <= 0) return null;

        long? layers = KvCacheLayers(model);
        long? headDimension = ResolveHeadDimension(model);
        long? kvHeads = Positive(model.KeyValueHeadCount) ?? Positive(model.AttentionHeadCount);
        if (layers == null || headDimension == null || kvHeads == null) return null;

        double perElementKey = BytesPerElement(keyCacheType);
        double perElementValue = BytesPerElement(valueCacheType ?? keyCacheType);

        // Key and value are sized independently: a file may declare different key_length and
        // value_length, and llama-server accepts different quantization for each.
        long keyDimension = model.KeyLength is > 0 ? model.KeyLength.Value : headDimension.Value;
        long valueDimension = model.ValueLength is > 0 ? model.ValueLength.Value : headDimension.Value;

        double bytes;
        if (model.SlidingWindow is > 0 && model.SlidingWindowPattern is { Length: > 0 } pattern
            && model.BlockCount is > 0 && pattern.Length == model.BlockCount)
        {
            // Sliding-window architectures (Gemma 4): windowed blocks keep only window + one batch of tokens, global blocks
            // keep the full context, and the head count can differ per block. llama.cpp allocates the windowed cache this way
            // unless --swa-full is passed, which PaRuX never passes. Estimating every block at full context over-stated the
            // KV of a 12B Gemma 4 at 32k tokens as 25.5 GB (real: about 1 GB) and made Core refuse a model that runs fine.
            const long BatchSlack = 2048;
            long swaKey = model.KeyLengthSwa is > 0 ? model.KeyLengthSwa.Value : keyDimension;
            long swaValue = model.ValueLengthSwa is > 0 ? model.ValueLengthSwa.Value : valueDimension;
            bytes = 0;
            for (int block = 0; block < pattern.Length; block++)
            {
                bool windowed = pattern[block];
                long heads = model.KeyValueHeadCountPerLayer is { } perLayer && perLayer.Length == pattern.Length
                    ? perLayer[block] : kvHeads.Value;
                long tokens = windowed ? Math.Min(contextLength, model.SlidingWindow.Value + BatchSlack) : contextLength;
                bytes += (double)tokens * heads * ((windowed ? swaKey : keyDimension) * perElementKey
                    + (windowed ? swaValue : valueDimension) * perElementValue);
            }
        }
        else
        {
            bytes =
                (double)layers.Value * contextLength * kvHeads.Value * keyDimension * perElementKey
                + (double)layers.Value * contextLength * kvHeads.Value * valueDimension * perElementValue;
        }

        if (double.IsNaN(bytes) || double.IsInfinity(bytes) || bytes <= 0 || bytes > long.MaxValue) return null;
        return (long)Math.Ceiling(bytes);
    }

    /// <summary>
    /// Per-head key/value width. An explicit declaration wins; otherwise it is the hidden size
    /// divided by the number of attention heads, which is the definition for every standard
    /// multi-head architecture.
    /// </summary>
    private static long? ResolveHeadDimension(GgufModelInfo model)
    {
        if (model.KeyLength is > 0) return model.KeyLength;
        long? embedding = Positive(model.EmbeddingLength);
        long? heads = Positive(model.AttentionHeadCount);
        if (embedding == null || heads == null) return null;
        long dimension = embedding.Value / heads.Value;
        return dimension > 0 ? dimension : null;
    }

    private static long? Positive(long? value) => value is > 0 ? value : null;
}
