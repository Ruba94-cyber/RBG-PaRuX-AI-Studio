using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Intelligence;

/// <summary>
/// Reads just enough of the GGUF binary header to describe a model — no
/// tensor data is touched. Format: https://github.com/ggml-org/ggml/blob/master/docs/gguf.md
/// (magic, version, tensor_count, kv_count, then typed key/value metadata).
/// Every value must still be walked in order even when the key is not one
/// PaRuX cares about, since GGUF has no index — ReadValue's ARRAY case
/// recurses to skip large arrays (e.g. tokenizer vocab) correctly.
/// </summary>
public static class GgufMetadataReader
{
    private const uint Magic = 0x46554747; // little-endian bytes of ASCII "GGUF"

    private enum GgufType : uint
    {
        UInt8 = 0, Int8 = 1, UInt16 = 2, Int16 = 3, UInt32 = 4, Int32 = 5,
        Float32 = 6, Bool = 7, String = 8, Array = 9, UInt64 = 10, Int64 = 11, Float64 = 12
    }

    // Quantization is read from the filename, through GgufFileNaming (the one parser the
    // catalogue and the managed library share). It is far more reliable than hand-decoding
    // general.file_type's numeric LLAMA_FTYPE_* enum, which has many values and is easy to get wrong.

    /// <summary>
    /// Parsed headers, keyed by identity rather than by path alone.
    ///
    /// Reading a GGUF header means walking its whole key/value table, and one of those values is the
    /// tokenizer vocabulary - a six-figure array of strings. Measured at ~572 ms per call on a real
    /// quantised model. That was being paid on the chat path: the Smart Router estimates hardware
    /// fit for every candidate on every request, and the engine bridge asks again, so a single chat
    /// message parsed the same unchanged file three times, for about 1.7 s of the roughly 2.1 s
    /// PaRuX was adding to each request.
    ///
    /// The result is a pure function of the file&apos;s bytes, so it is cached on identity - path plus
    /// length plus last-write time. A file edited or replaced in place therefore misses the cache
    /// and is re-read, which matters because a model can be re-downloaded or re-quantised under the
    /// same name; nothing here can serve stale geometry for different bytes.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GgufModelInfo> Cache = new();

    /// <summary>Bound, so a machine that walks a very large library cannot grow this without limit.
    /// Real libraries hold tens of models; this is a safety valve, not a working set.</summary>
    private const int MaximumCachedHeaders = 256;

    private static string? IdentityKey(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            return file.FullName.ToLowerInvariant() + "|" + file.Length.ToString() + "|"
                + file.LastWriteTimeUtc.Ticks.ToString();
        }
        catch { return null; }
    }

    /// <summary>Drops every cached header. For tests, and for an explicit "rescan my library".</summary>
    public static void InvalidateCache() => Cache.Clear();

    public static GgufModelInfo Read(string path)
    {
        string? key = IdentityKey(path);
        if (key != null && Cache.TryGetValue(key, out GgufModelInfo? cached)) return cached;

        GgufModelInfo parsed = ReadUncached(path);

        if (key != null)
        {
            if (Cache.Count >= MaximumCachedHeaders) Cache.Clear();
            Cache[key] = parsed;
        }
        return parsed;
    }

    private static GgufModelInfo ReadUncached(string path)
    {
        var info = new GgufModelInfo { FileName = Path.GetFileName(path), FilePath = path };
        try { info.SizeBytes = new FileInfo(path).Length; } catch { /* best-effort */ }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != Magic) return WithFilenameFallback(info);

            uint version = reader.ReadUInt32();
            ulong kvCount;
            ulong tensorCount;
            if (version >= 2)
            {
                tensorCount = reader.ReadUInt64();
                kvCount = reader.ReadUInt64();
            }
            else
            {
                tensorCount = reader.ReadUInt32();
                kvCount = reader.ReadUInt32();
            }
            long alignment = DefaultAlignment;

            for (ulong i = 0; i < kvCount; i++)
            {
                string key = ReadGgufString(reader);
                var type = (GgufType)reader.ReadUInt32();
                object? value = ReadValue(reader, type, depth: 0);

                if (key == "general.architecture" && value is string arch) info.Architecture = arch;
                else if (key == "general.parameter_count") info.ParameterCount = ToLong(value);
                else if (key.EndsWith(".context_length", StringComparison.Ordinal)) info.ContextLength = ToLong(value);
                // Architecture-prefixed keys are matched by suffix for the same reason
                // .context_length already is: the prefix is the architecture name
                // ("llama.", "qwen3.", "gemma3."), which varies per file.
                else if (key.EndsWith(".block_count", StringComparison.Ordinal)) info.BlockCount = ToLong(value);
                else if (key.EndsWith(".embedding_length", StringComparison.Ordinal)) info.EmbeddingLength = ToLong(value);
                else if (key.EndsWith(".attention.head_count", StringComparison.Ordinal)) info.AttentionHeadCount = ToLong(value);
                else if (key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal))
                {
                    if (value is long[] perLayer && perLayer.Length > 0)
                    {
                        info.KeyValueHeadCountPerLayer = perLayer;
                        info.KeyValueHeadCount = perLayer.Max(); // the safe direction for a memory estimate
                    }
                    else info.KeyValueHeadCount = ToLong(value);
                }
                else if (key.EndsWith(".attention.sliding_window", StringComparison.Ordinal)) info.SlidingWindow = ToLong(value);
                else if (key.EndsWith(".attention.sliding_window_pattern", StringComparison.Ordinal)) info.SlidingWindowPattern = value as bool[];
                else if (key.EndsWith(".attention.key_length_swa", StringComparison.Ordinal)) info.KeyLengthSwa = ToLong(value);
                else if (key.EndsWith(".attention.value_length_swa", StringComparison.Ordinal)) info.ValueLengthSwa = ToLong(value);
                else if (key.EndsWith(".full_attention_interval", StringComparison.Ordinal)) info.FullAttentionInterval = ToLong(value);
                else if (key.EndsWith(".attention.key_length", StringComparison.Ordinal)) info.KeyLength = ToLong(value);
                else if (key.EndsWith(".attention.value_length", StringComparison.Ordinal)) info.ValueLength = ToLong(value);
                else if (key.EndsWith(".expert_count", StringComparison.Ordinal)) info.ExpertCount = ToLong(value);
                else if (key.EndsWith(".expert_used_count", StringComparison.Ordinal)) info.ExpertUsedCount = ToLong(value);
                else if (key.EndsWith(".expert_shared_count", StringComparison.Ordinal)) info.ExpertSharedCount = ToLong(value);
                else if (key.EndsWith(".expert_feed_forward_length", StringComparison.Ordinal)) info.ExpertFeedForwardLength = ToLong(value);
                else if (key == "general.alignment" && ToLong(value) is long declared and > 0 and <= 1048576) alignment = declared;
            }

            info.ParsedSuccessfully = true;
            // Best effort and independent of the key/value result: a hostile or truncated tensor
            // directory only costs the exact expert size, never the metadata already read.
            info.ExpertTensorBytes = TryMeasureExpertTensorBytes(reader, tensorCount, alignment, out bool embeddedMtp);
            info.HasEmbeddedMtpHead = embeddedMtp;
        }
        catch (Exception ex)
        {
            // Reported, not logged from in here: a binary-format reader has no business
            // knowing where this app writes its diagnostics, and keeping it free of that
            // dependency is what lets the parsing itself be unit-tested.
            info.ParseError = ex.Message;
        }

        return WithFilenameFallback(info);
    }

    private const long DefaultAlignment = 32;

    /// <summary>Real models hold a few thousand tensors; this only bounds a hostile declaration.</summary>
    private const ulong MaximumTensorCount = 200_000;

    private const uint MaximumTensorDimensions = 8;

    /// <summary>Largest array whose contents are kept (per-layer metadata is a few hundred entries at most).</summary>
    private const ulong MaximumSurfacedArray = 4096;

    /// <summary>
    /// Sums the bytes of routed-expert tensors from the tensor directory without touching tensor data.
    /// Tensor data offsets are relative to the aligned data start, so a tensor's size is the distance
    /// to the next offset (the last one runs to the end of the file). That is exact up to alignment
    /// padding and needs no per-quantization block-size table. Every read is bounded by the file, and
    /// any inconsistency returns null instead of a guess.
    /// </summary>
    private static long? TryMeasureExpertTensorBytes(BinaryReader reader, ulong tensorCount, long alignment, out bool embeddedMtp)
    {
        embeddedMtp = false;
        try
        {
            if (tensorCount == 0 || tensorCount > MaximumTensorCount) return null;
            var tensors = new List<(bool Expert, ulong Offset)>((int)tensorCount);
            for (ulong i = 0; i < tensorCount; i++)
            {
                string name = ReadGgufString(reader);
                uint dims = reader.ReadUInt32();
                if (dims > MaximumTensorDimensions) return null;
                for (uint d = 0; d < dims; d++) reader.ReadUInt64();
                reader.ReadUInt32(); // ggml type, not needed
                ulong offset = reader.ReadUInt64();
                if (name.Contains(".nextn.", StringComparison.Ordinal)) embeddedMtp = true;
                tensors.Add((name.Contains("_exps.", StringComparison.Ordinal), offset));
            }

            long length = reader.BaseStream.Length;
            long position = reader.BaseStream.Position;
            long step = Math.Max(1, alignment);
            long dataStart = (position + step - 1) / step * step;
            if (dataStart > length) return null;
            ulong dataBytes = (ulong)(length - dataStart);

            tensors.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            ulong expertBytes = 0;
            for (int i = 0; i < tensors.Count; i++)
            {
                ulong end = i + 1 < tensors.Count ? tensors[i + 1].Offset : dataBytes;
                if (end < tensors[i].Offset || end > dataBytes) return null;
                if (tensors[i].Expert) expertBytes += end - tensors[i].Offset;
            }
            return expertBytes == 0 || expertBytes > long.MaxValue ? null : (long)expertBytes;
        }
        catch { return null; }
    }

    private static GgufModelInfo WithFilenameFallback(GgufModelInfo info)
    {
        info.Quantization = GgufFileNaming.QuantizationOrUnknown(info.FileName);
        // The filename is used unconditionally, not just as a fallback for a missing
        // general.name: real community-quantized files often embed a terse, uninformative
        // general.name (e.g. "Abl 27b" for "Qwen3.8-27B-Uncensored-noMTP-IQ4_XS.gguf" -
        // found via a real installed model on this machine), while the filename convention
        // this app's models actually follow is consistently more descriptive.
        info.Name = Path.GetFileNameWithoutExtension(info.FileName);
        return info;
    }

    /// <summary>
    /// How deeply a metadata value may nest before the file is rejected as hostile.
    ///
    /// GGUF's ARRAY element type may itself be ARRAY, and <see cref="ReadValue"/> recurses once per
    /// level. Nothing bounded that: a 600 KB file declaring 50,000 nested arrays overflowed the
    /// stack, and <c>StackOverflowException</c> cannot be caught in .NET - the process is
    /// terminated outright, so the try/catch around the whole read was powerless. Demonstrated
    /// against the real reader, which killed its host process.
    ///
    /// This is not one app's problem: model files come from HuggingFace, where anyone can upload;
    /// ModelManagerService parses every .gguf in the models folder; and Core's own load-time
    /// hardware gate reads the header too. Real GGUF metadata never nests past an array of scalars
    /// or strings, so 64 is far beyond anything legitimate.
    /// </summary>
    private const int MaximumValueDepth = 64;

    private static string ReadGgufString(BinaryReader reader)
    {
        ulong length = reader.ReadUInt64();

        // The declared length is attacker-controlled, and BinaryReader.ReadBytes allocates the
        // whole buffer before discovering the stream is shorter: a 37-byte file declaring a 1.5 GB
        // string really did allocate 1430 MB before failing. A string cannot be longer than the
        // bytes left in the file, which is an exact bound rather than a guessed cap.
        long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        if (length > (ulong)Math.Max(0, remaining))
            throw new InvalidDataException(
                "GGUF declares a " + length + "-byte string with only " + remaining + " bytes left in the file.");

        byte[] bytes = reader.ReadBytes((int)length);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <returns>The decoded scalar; a long[]/bool[] for a short integer/bool ARRAY; null for any other array (walked to keep the stream aligned, contents dropped).</returns>
    private static object? ReadValue(BinaryReader reader, GgufType type, int depth)
    {
        if (depth > MaximumValueDepth)
            throw new InvalidDataException(
                "GGUF metadata nests more than " + MaximumValueDepth + " levels deep; refusing to parse it.");

        switch (type)
        {
            case GgufType.UInt8: return (long)reader.ReadByte();
            case GgufType.Int8: return (long)reader.ReadSByte();
            case GgufType.UInt16: return (long)reader.ReadUInt16();
            case GgufType.Int16: return (long)reader.ReadInt16();
            case GgufType.UInt32: return (long)reader.ReadUInt32();
            case GgufType.Int32: return (long)reader.ReadInt32();
            case GgufType.Float32: return (double)reader.ReadSingle();
            case GgufType.Bool: return reader.ReadBoolean();
            case GgufType.String: return ReadGgufString(reader);
            case GgufType.UInt64: return unchecked((long)reader.ReadUInt64());
            case GgufType.Int64: return reader.ReadInt64();
            case GgufType.Float64: return reader.ReadDouble();
            case GgufType.Array:
                {
                    var elementType = (GgufType)reader.ReadUInt32();
                    ulong count = reader.ReadUInt64();
                    // No bound on count is needed beyond the stream itself - every element
                    // consumes at least one byte, so a lying count ends at EOF - but each element
                    // may recurse, which is what the depth limit above stops.
                    // Short numeric/bool arrays (per-layer head counts, the sliding-window pattern) are surfaced; everything else
                    // (tokenizer vocab, merges, scores) is walked to keep the stream aligned and dropped.
                    bool surface = count is > 0 and <= MaximumSurfacedArray
                        && elementType is GgufType.UInt8 or GgufType.Int8 or GgufType.UInt16 or GgufType.Int16
                            or GgufType.UInt32 or GgufType.Int32 or GgufType.UInt64 or GgufType.Int64 or GgufType.Bool;
                    if (!surface)
                    {
                        for (ulong i = 0; i < count; i++) ReadValue(reader, elementType, depth + 1);
                        return null;
                    }
                    var items = new object?[(int)count];
                    for (int i = 0; i < items.Length; i++) items[i] = ReadValue(reader, elementType, depth + 1);
                    if (elementType == GgufType.Bool) return items.Select(x => x is true).ToArray();
                    return items.Select(x => x is long l ? l : 0L).ToArray();
                }
            default:
                throw new InvalidDataException("Unrecognized GGUF value type " + (uint)type + " — cannot safely skip the rest of the file.");
        }
    }

    /// <summary>Null-tolerant by design: an ARRAY value arrives here as null, and every type this
    /// does not understand answers null rather than throwing.</summary>
    private static long? ToLong(object? value) => value switch
    {
        long l => l,
        double d => (long)d,
        _ => null
    };
}
