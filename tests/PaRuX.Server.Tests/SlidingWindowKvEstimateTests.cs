using System.Text;
using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Root cause of Core refusing the Gemma 4 12B GGUFs ("needs 33.1 GB ... 25.5 GB of KV cache at 32,768 tokens"): the header reader
/// dropped array metadata, so head_count_kv (an array) fell back to the attention head count (16) and every block was sized at
/// full context although 40 of 48 blocks are sliding-window. The geometry below is read from the real file's header.
/// </summary>
public sealed class SlidingWindowKvEstimateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-swa-" + Guid.NewGuid().ToString("N"));

    public SlidingWindowKvEstimateTests()
    {
        Directory.CreateDirectory(_directory);
        GgufMetadataReader.InvalidateCache();
    }

    public void Dispose()
    {
        GgufMetadataReader.InvalidateCache();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private static void Str(BinaryWriter w, string v) { byte[] b = Encoding.UTF8.GetBytes(v); w.Write((ulong)b.Length); w.Write(b); }
    private static void KeyStr(BinaryWriter w, string k, string v) { Str(w, k); w.Write(8u); Str(w, v); }
    private static void KeyU32(BinaryWriter w, string k, uint v) { Str(w, k); w.Write(4u); w.Write(v); }
    private static void KeyArr(BinaryWriter w, string k, uint elementType, IEnumerable<object> items)
    {
        object[] list = items.ToArray();
        Str(w, k); w.Write(9u); w.Write(elementType); w.Write((ulong)list.Length);
        foreach (object o in list) { if (o is bool b) w.Write(b); else w.Write((uint)(int)o); }
    }

    private string WriteGemma4Like()
    {
        string path = Path.Combine(_directory, "gemma4.gguf");
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write(0x46554747u); w.Write(3u); w.Write(0ul); w.Write(11ul);
        KeyStr(w, "general.architecture", "gemma4");
        KeyU32(w, "gemma4.context_length", 262144);
        KeyU32(w, "gemma4.block_count", 48);
        KeyU32(w, "gemma4.embedding_length", 3840);
        KeyU32(w, "gemma4.attention.head_count", 16);
        KeyArr(w, "gemma4.attention.head_count_kv", 4u, Enumerable.Range(0, 48).Select(i => (object)(i % 6 == 5 ? 1 : 8)));
        KeyU32(w, "gemma4.attention.key_length", 512);
        KeyU32(w, "gemma4.attention.value_length", 512);
        KeyU32(w, "gemma4.attention.sliding_window", 1024);
        KeyArr(w, "gemma4.attention.sliding_window_pattern", 7u, Enumerable.Range(0, 48).Select(i => (object)(i % 6 != 5)));
        KeyU32(w, "gemma4.attention.key_length_swa", 256);
        return path;
    }

    [Fact]
    public void Reader_surfaces_per_layer_head_counts_and_the_sliding_window_pattern()
    {
        GgufModelInfo info = GgufMetadataReader.Read(WriteGemma4Like());
        Assert.True(info.ParsedSuccessfully);
        Assert.Equal(48, info.KeyValueHeadCountPerLayer!.Length);
        Assert.Equal(8, info.KeyValueHeadCount); // max of the array, not the attention head count (16)
        Assert.Equal(1024, info.SlidingWindow);
        Assert.Equal(40, info.SlidingWindowPattern!.Count(x => x));
        Assert.Equal(256, info.KeyLengthSwa);
    }

    [Fact]
    public void Gemma4_kv_estimate_follows_the_window_not_the_full_context()
    {
        GgufModelInfo info = GgufMetadataReader.Read(WriteGemma4Like());
        long q8 = KvCacheSizeEstimator.Estimate(info, 32768, "q8_0", "q8_0")!.Value;
        // 8 global blocks x 1 head x (512+512) x 32768 tokens + 40 windowed blocks x 8 heads x (256+512... see geometry) x 3072 tokens, q8_0.
        Assert.InRange(q8 / (1024.0 * 1024 * 1024), 0.3, 2.0); // about 1 GiB, not 25.5 GiB
        var withoutWindow = new GgufModelInfo
        {
            BlockCount = 48, KeyValueHeadCount = 16, KeyLength = 512, ValueLength = 512, EmbeddingLength = 3840, AttentionHeadCount = 16
        };
        Assert.True(KvCacheSizeEstimator.Estimate(withoutWindow, 32768, "q8_0", "q8_0") > 20L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void Dense_models_without_a_window_keep_the_previous_estimate()
    {
        var info = new GgufModelInfo { BlockCount = 32, KeyValueHeadCount = 8, EmbeddingLength = 4096, AttentionHeadCount = 32 };
        // 2 x 32 layers x 4096 ctx x 8 heads x 128 dim x 2 bytes
        Assert.Equal(2L * 32 * 4096 * 8 * 128 * 2, KvCacheSizeEstimator.Estimate(info, 4096, "f16", "f16"));
    }
}
