using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The GGUF header parser reads untrusted binary. Model files come from HuggingFace, where anyone
/// can upload; `ModelManagerService.ListInstalledModels` parses every `.gguf` in the models folder;
/// and since 2026-09-02 Core's own load-time hardware gate reads the header too. Two hostile files
/// were built and run against the real reader before this was written, and both did real damage:
///
///   * a 600 KB file declaring 50,000 nested arrays **terminated the process** with a stack
///     overflow. `StackOverflowException` cannot be caught in .NET, so the try/catch wrapping the
///     whole read was powerless - the test host itself was killed.
///   * a **37-byte** file declaring a 1.5 GB string caused a **1430 MB** allocation before failing,
///     because `BinaryReader.ReadBytes` allocates the full buffer before noticing the stream is
///     shorter.
///
/// These tests build the same two files and assert the reader now refuses them cheaply. The
/// generators are code rather than checked-in binaries so the shape of each attack is readable.
/// </summary>
public sealed class GgufHostileFileTests : IDisposable
{
    private const uint Magic = 0x46554747;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-gguf-hostile-" + Guid.NewGuid().ToString("N"));

    public GgufHostileFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteHeader(BinaryWriter writer, ulong keyValueCount)
    {
        writer.Write(Magic);
        writer.Write(3u);              // version
        writer.Write(0UL);             // tensor_count
        writer.Write(keyValueCount);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }

    private string WriteFile(string name, Action<BinaryWriter> build)
    {
        string path = Path.Combine(_root, name);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            build(writer);
        }
        return path;
    }

    [Fact]
    public void A_nested_array_bomb_is_refused_instead_of_killing_the_process()
    {
        // 50,000 levels of ARRAY-of-ARRAY. One recursion per level in the unfixed reader.
        const int depth = 50_000;
        string path = WriteFile("nested-array-bomb.gguf", writer =>
        {
            WriteHeader(writer, 1);
            WriteString(writer, "bomb");
            writer.Write(9u);                                   // value type = ARRAY
            for (int i = 0; i < depth - 1; i++)
            {
                writer.Write(9u);                               // element type = ARRAY
                writer.Write(1UL);                              // count
            }
            writer.Write(0u);                                   // innermost element type = UINT8
            writer.Write(0UL);
        });

        // Reaching this assertion at all is the result: before the depth limit the test host
        // process died here and no assertion ran.
        GgufModelInfo info = GgufMetadataReader.Read(path);

        Assert.False(info.ParsedSuccessfully);
        Assert.NotNull(info.ParseError);
        Assert.Contains("nests more than", info.ParseError!);
    }

    [Fact]
    public void A_string_longer_than_the_file_is_refused_without_allocating_it()
    {
        string path = WriteFile("huge-string.gguf", writer =>
        {
            WriteHeader(writer, 1);
            writer.Write(1_500_000_000UL);                      // declared key length: ~1.5 GB
            writer.Write(System.Text.Encoding.UTF8.GetBytes("short"));
        });

        long before = GC.GetTotalAllocatedBytes(precise: true);
        GgufModelInfo info = GgufMetadataReader.Read(path);
        long allocatedMb = (GC.GetTotalAllocatedBytes(precise: true) - before) / 1024 / 1024;

        Assert.False(info.ParsedSuccessfully);
        Assert.Contains("bytes left in the file", info.ParseError!);
        // The unfixed reader allocated 1430 MB here. A generous ceiling still catches any
        // regression by three orders of magnitude.
        Assert.True(allocatedMb < 32, $"Refusing the file allocated {allocatedMb} MB.");
    }

    [Fact]
    public void A_truncated_file_is_reported_rather_than_throwing_out_of_the_reader()
    {
        string path = WriteFile("truncated.gguf", writer =>
        {
            WriteHeader(writer, 5);        // promises five key/value pairs
            WriteString(writer, "general.architecture");
            // ...and then simply stops.
        });

        GgufModelInfo info = GgufMetadataReader.Read(path);

        Assert.False(info.ParsedSuccessfully);
        Assert.NotNull(info.ParseError);
        // The filename fallback still populates what it can, so a corrupt file degrades rather
        // than disappearing from the model list entirely.
        Assert.Equal("truncated", info.Name);
    }

    [Fact]
    public void An_unknown_value_type_stops_the_parse_rather_than_desynchronising_it()
    {
        // GGUF has no index: a type the reader cannot size means every subsequent offset is
        // guesswork, so refusing is the only safe answer.
        string path = WriteFile("unknown-type.gguf", writer =>
        {
            WriteHeader(writer, 1);
            WriteString(writer, "weird");
            writer.Write(999u);
        });

        GgufModelInfo info = GgufMetadataReader.Read(path);

        Assert.False(info.ParsedSuccessfully);
        Assert.Contains("Unrecognized GGUF value type", info.ParseError!);
    }

    [Fact]
    public void A_legitimate_file_with_a_normal_array_still_parses()
    {
        // The depth limit must not break the ordinary case it exists to protect: real files carry
        // arrays of strings (tokenizer vocab) that the reader walks past.
        string path = WriteFile("normal.gguf", writer =>
        {
            WriteHeader(writer, 3);

            WriteString(writer, "general.architecture");
            writer.Write(8u);                                   // STRING
            WriteString(writer, "llama");

            WriteString(writer, "tokenizer.ggml.tokens");
            writer.Write(9u);                                   // ARRAY
            writer.Write(8u);                                   // of STRING
            writer.Write(3UL);
            WriteString(writer, "a");
            WriteString(writer, "b");
            WriteString(writer, "c");

            WriteString(writer, "llama.block_count");
            writer.Write(4u);                                   // UINT32
            writer.Write(32u);
        });

        GgufModelInfo info = GgufMetadataReader.Read(path);

        Assert.True(info.ParsedSuccessfully, info.ParseError);
        Assert.Equal("llama", info.Architecture);
        Assert.Equal(32, info.BlockCount);
    }
}
