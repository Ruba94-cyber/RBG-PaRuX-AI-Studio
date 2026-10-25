namespace PaRuX.Intelligence;

public sealed class GgufModelInfo
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Architecture { get; set; } = "unknown";
    public string Name { get; set; } = string.Empty;
    public string Quantization { get; set; } = "unknown";
    public long? ContextLength { get; set; }
    public long? ParameterCount { get; set; }
    public bool ParsedSuccessfully { get; set; }
    /// <summary>Why the header could not be read, when it could not. Null on success. The
    /// reader reports this instead of logging it itself, so the parser stays free of any
    /// filesystem/logging dependency and can be tested in isolation.</summary>
    public string? ParseError { get; set; }

    // ---- Attention geometry (added 2026-09-02) ----
    // Enough of the architecture to compute the KV cache exactly instead of folding it into a
    // flat percentage of file size. KV cache scales with CONTEXT, not with weight size, so a
    // percentage-of-file estimate is wrong in both directions: it over-reserves for a small
    // context and badly under-reserves for a long one, which is how a model that "fits"
    // according to the estimate still runs out of VRAM at 32k.
    //
    // Null whenever the file did not declare the key. Every consumer must treat null as
    // "cannot compute exactly" and say so, never as zero.

    /// <summary>Transformer layers ({arch}.block_count).</summary>
    public long? BlockCount { get; set; }
    /// <summary>Model hidden size ({arch}.embedding_length).</summary>
    public long? EmbeddingLength { get; set; }
    /// <summary>Attention heads ({arch}.attention.head_count).</summary>
    public long? AttentionHeadCount { get; set; }
    /// <summary>Key/value heads ({arch}.attention.head_count_kv). Smaller than
    /// <see cref="AttentionHeadCount"/> under grouped-query attention, which is exactly what
    /// makes a file-size-proportional KV estimate wrong - GQA models need far less KV memory
    /// than their weight size suggests.</summary>
    public long? KeyValueHeadCount { get; set; }

    /// <summary>
    /// Hybrid attention models (e.g. Qwen3.5/3.8 "qwen35") declare that only every Nth block uses
    /// full attention; the other blocks keep a fixed-size recurrent state instead of a per-token
    /// KV cache. Null for ordinary transformers, where every block has a KV cache.
    /// </summary>
    public long? FullAttentionInterval { get; set; }
    /// <summary>Explicit per-head key dimension ({arch}.attention.key_length) when the file
    /// declares one; otherwise the head dimension is embedding_length / head_count.</summary>
    public long? KeyLength { get; set; }
    /// <summary>Explicit per-head value dimension ({arch}.attention.value_length).</summary>
    public long? ValueLength { get; set; }

    /// <summary>Per-layer key/value head counts when {arch}.attention.head_count_kv is an array (Gemma 4 declares 8 for sliding-window layers and 1 for global layers).</summary>
    public long[]? KeyValueHeadCountPerLayer { get; set; }
    /// <summary>Sliding-window attention span in tokens ({arch}.attention.sliding_window); null when no layer is windowed.</summary>
    public long? SlidingWindow { get; set; }
    /// <summary>Per block: true when that block uses sliding-window attention ({arch}.attention.sliding_window_pattern).</summary>
    public bool[]? SlidingWindowPattern { get; set; }
    /// <summary>Key/value dimensions of the sliding-window blocks when they differ from the global ones.</summary>
    public long? KeyLengthSwa { get; set; }
    public long? ValueLengthSwa { get; set; }

    // ---- Mixture-of-experts geometry ----
    // Null when the file does not declare the key. A model is MoE only when it declares more than
    // one routed expert; an absent key means "dense", never "unknown MoE".

    /// <summary>Routed experts per MoE layer ({arch}.expert_count).</summary>
    public long? ExpertCount { get; set; }
    /// <summary>Experts activated per token ({arch}.expert_used_count).</summary>
    public long? ExpertUsedCount { get; set; }
    /// <summary>Always-active shared experts ({arch}.expert_shared_count).</summary>
    public long? ExpertSharedCount { get; set; }
    /// <summary>Feed-forward width of one routed expert ({arch}.expert_feed_forward_length).</summary>
    public long? ExpertFeedForwardLength { get; set; }

    /// <summary>
    /// Exact bytes of the routed-expert tensors (names containing "_exps."), measured from the tensor
    /// directory offsets without reading tensor data. Null when the directory could not be walked
    /// safely, in which case consumers must estimate and say so.
    /// </summary>
    public long? ExpertTensorBytes { get; set; }

    /// <summary>True when the tensor directory contains multi-token-prediction head tensors (<c>blk.N.nextn.*</c>), i.e. the file can self-speculate with <c>--spec-type draft-mtp</c> without any separate draft model.</summary>
    public bool HasEmbeddedMtpHead { get; set; }

    /// <summary>True when the header declares more than one routed expert.</summary>
    public bool IsMixtureOfExperts => ExpertCount is > 1;
}
