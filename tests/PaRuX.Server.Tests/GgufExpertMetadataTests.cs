using System.Text;
using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Expert (MoE) metadata and the tensor-directory expert size, read from tiny fake GGUF files.</summary>
public sealed class GgufExpertMetadataTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-gguf-moe-" + Guid.NewGuid().ToString("N"));

    public GgufExpertMetadataTests()
    {
        Directory.CreateDirectory(_directory);
        GgufMetadataReader.InvalidateCache();
    }

    public void Dispose()
    {
        GgufMetadataReader.InvalidateCache();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private static void Str(BinaryWriter w, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        w.Write((ulong)bytes.Length);
        w.Write(bytes);
    }

    private static void KvString(BinaryWriter w, string key, string value) { Str(w, key); w.Write(8u); Str(w, value); }
    private static void KvU32(BinaryWriter w, string key, uint value) { Str(w, key); w.Write(4u); w.Write(value); }

    private string Write(string name, string architecture, uint? experts, (string Name, int Bytes)[] tensors, ulong? declaredTensorCount = null)
    {
        string path = Path.Combine(_directory, name);
        using (var stream = File.Create(path))
        using (var w = new BinaryWriter(stream))
        {
            var kvs = new List<Action<BinaryWriter>>
            {
                x => KvString(x, "general.architecture", architecture),
                x => KvU32(x, architecture + ".block_count", 2),
                x => KvU32(x, architecture + ".embedding_length", 64),
                x => KvU32(x, architecture + ".attention.head_count", 4),
                x => KvU32(x, architecture + ".attention.head_count_kv", 2),
                x => KvU32(x, architecture + ".context_length", 4096),
            };
            if (experts is { } count)
            {
                kvs.Add(x => KvU32(x, architecture + ".expert_count", count));
                kvs.Add(x => KvU32(x, architecture + ".expert_used_count", 2));
                kvs.Add(x => KvU32(x, architecture + ".expert_feed_forward_length", 32));
            }

            w.Write(0x46554747u);
            w.Write(3u);
            w.Write(declaredTensorCount ?? (ulong)tensors.Length);
            w.Write((ulong)kvs.Count);
            foreach (Action<BinaryWriter> kv in kvs) kv(w);

            ulong offset = 0;
            foreach ((string tensorName, int bytes) in tensors)
            {
                Str(w, tensorName);
                w.Write(1u);
                w.Write((ulong)bytes);
                w.Write(0u);
                w.Write(offset);
                offset += (ulong)bytes;
            }

            long pad = (32 - stream.Position % 32) % 32;
            w.Write(new byte[pad]);
            foreach ((_, int bytes) in tensors) w.Write(new byte[bytes]);
        }
        return path;
    }

    [Fact]
    public void A_moe_header_reports_experts_and_the_exact_expert_tensor_bytes()
    {
        string path = Write("moe.gguf", "qwen3moe", 8,
        [
            ("blk.0.attn_q.weight", 256),
            ("blk.0.ffn_gate_exps.weight", 1024),
            ("blk.0.ffn_down_exps.weight", 1024),
            ("blk.1.ffn_gate_shexp.weight", 64),
            ("blk.1.ffn_up_exps.weight", 512),
            ("output.weight", 128)
        ]);

        GgufModelInfo info = GgufMetadataReader.Read(path);

        Assert.True(info.ParsedSuccessfully);
        Assert.True(info.IsMixtureOfExperts);
        Assert.Equal(8, info.ExpertCount);
        Assert.Equal(2, info.ExpertUsedCount);
        Assert.Equal(32, info.ExpertFeedForwardLength);
        Assert.Equal(2, info.BlockCount);
        Assert.Equal(1024 + 1024 + 512, info.ExpertTensorBytes);
    }

    [Fact]
    public void A_dense_header_is_not_moe_and_has_no_expert_bytes()
    {
        string path = Write("dense.gguf", "qwen3", null, [("blk.0.attn_q.weight", 256), ("blk.0.ffn_up.weight", 512)]);

        GgufModelInfo info = GgufMetadataReader.Read(path);

        Assert.True(info.ParsedSuccessfully);
        Assert.False(info.IsMixtureOfExperts);
        Assert.Null(info.ExpertCount);
        Assert.Null(info.ExpertTensorBytes);
    }

    [Fact]
    public void A_single_expert_header_is_not_treated_as_moe()
    {
        GgufModelInfo info = GgufMetadataReader.Read(Write("one.gguf", "llama", 1, [("blk.0.attn_q.weight", 64)]));

        Assert.False(info.IsMixtureOfExperts);
    }

    [Fact]
    public void A_hostile_tensor_count_keeps_the_metadata_and_drops_only_the_expert_size()
    {
        string path = Write("hostile.gguf", "qwen3moe", 8, [("blk.0.ffn_up_exps.weight", 64)], declaredTensorCount: 1UL << 40);

        GgufModelInfo info = GgufMetadataReader.Read(path);

        Assert.True(info.ParsedSuccessfully);
        Assert.Equal(8, info.ExpertCount);
        Assert.Null(info.ExpertTensorBytes);
    }

    [Fact]
    public void A_truncated_tensor_directory_does_not_throw_or_guess()
    {
        string path = Write("truncated.gguf", "qwen3moe", 8, [("blk.0.ffn_up_exps.weight", 64)], declaredTensorCount: 5);

        GgufModelInfo info = GgufMetadataReader.Read(path);

        Assert.True(info.ParsedSuccessfully);
        Assert.Null(info.ExpertTensorBytes);
    }
}
