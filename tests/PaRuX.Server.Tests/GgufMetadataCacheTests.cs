using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The GGUF header cache, which exists because parsing one costs ~572 ms - it walks the whole
/// key/value table, including the tokenizer vocabulary - and the Smart Router was paying that for
/// every candidate on every chat request.
///
/// Caching model geometry is only safe while the cached bytes are still the file's bytes. A model
/// can legitimately be replaced under the same name: re-downloaded, re-quantised, or swapped by
/// hand. Serving the previous file's attention geometry for the new one would feed a wrong KV-cache
/// size into the hardware-fit gate and the context planner, which is worse than the slow parse it
/// replaced. So these cover invalidation, not just the hit.
/// </summary>
public sealed class GgufMetadataCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parux-gguf-cache-" + Guid.NewGuid().ToString("N"));

    public GgufMetadataCacheTests()
    {
        Directory.CreateDirectory(_directory);
        GgufMetadataReader.InvalidateCache();
    }

    public void Dispose()
    {
        GgufMetadataReader.InvalidateCache();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    /// <summary>A file that is not a GGUF at all still produces a result (filename fallback), which
    /// is all these need: the identity behaviour is independent of what the parse found.</summary>
    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void The_same_unchanged_file_is_parsed_once()
    {
        string path = WriteFile("model-Q4_K_M.gguf", "not really a gguf");

        GgufModelInfo first = GgufMetadataReader.Read(path);
        GgufModelInfo second = GgufMetadataReader.Read(path);

        Assert.Same(first, second);
    }

    [Fact]
    public void A_file_replaced_under_the_same_name_is_parsed_again()
    {
        string path = WriteFile("model-Q4_K_M.gguf", "first contents");
        GgufModelInfo first = GgufMetadataReader.Read(path);

        // Different length and a later write time: a re-download or a re-quantisation.
        File.WriteAllText(path, "second contents, a different length entirely");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        GgufModelInfo second = GgufMetadataReader.Read(path);

        Assert.NotSame(first, second);
        Assert.Equal(new FileInfo(path).Length, second.SizeBytes);
    }

    [Fact]
    public void A_file_edited_to_the_same_length_is_still_parsed_again()
    {
        // Length alone would not catch this - an in-place edit that keeps the size is exactly the
        // case a size-only key would serve stale geometry for.
        string path = WriteFile("model-Q4_K_M.gguf", "aaaaaaaaaaaaaaaa");
        GgufModelInfo first = GgufMetadataReader.Read(path);

        File.WriteAllText(path, "bbbbbbbbbbbbbbbb");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        Assert.NotSame(first, GgufMetadataReader.Read(path));
    }

    [Fact]
    public void Two_different_files_do_not_share_an_entry()
    {
        string first = WriteFile("small-Q4_K_M.gguf", "one");
        string second = WriteFile("large-Q8_0.gguf", "two");

        GgufModelInfo a = GgufMetadataReader.Read(first);
        GgufModelInfo b = GgufMetadataReader.Read(second);

        Assert.NotSame(a, b);
        Assert.Equal("small-Q4_K_M.gguf", a.FileName);
        Assert.Equal("large-Q8_0.gguf", b.FileName);
    }

    [Fact]
    public void A_missing_file_is_not_cached_and_still_returns_a_result()
    {
        // Nothing to key on, so this must fall through to the parse every time rather than caching
        // a "missing" verdict that would then survive the model being downloaded.
        string path = Path.Combine(_directory, "not-here.gguf");

        GgufModelInfo first = GgufMetadataReader.Read(path);
        Assert.Equal("not-here.gguf", first.FileName);

        File.WriteAllText(path, "now it exists");
        GgufModelInfo second = GgufMetadataReader.Read(path);

        Assert.NotSame(first, second);
        Assert.Equal(new FileInfo(path).Length, second.SizeBytes);
    }

    [Fact]
    public void Invalidating_the_cache_forces_a_fresh_parse()
    {
        string path = WriteFile("model-Q4_K_M.gguf", "contents");
        GgufModelInfo first = GgufMetadataReader.Read(path);

        GgufMetadataReader.InvalidateCache();

        Assert.NotSame(first, GgufMetadataReader.Read(path));
    }
}
