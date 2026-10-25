using System.IO.Compression;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SafeArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-archive-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteZip(string path, params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using Stream stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }
    }

    [Fact]
    public async Task ExtractAsync_writes_nested_entries_inside_destination()
    {
        string archive = Path.Combine(_root, "fixture.zip");
        string destination = Path.Combine(_root, "out");
        WriteZip(archive,
            ("ComfyUI_windows_portable/ComfyUI/main.py", "main"),
            ("ComfyUI_windows_portable/python_embeded/python.exe", "python"));

        await SafeArchiveExtractor.ExtractAsync(archive, destination, CancellationToken.None);

        Assert.Equal("main", File.ReadAllText(Path.Combine(destination, "ComfyUI_windows_portable", "ComfyUI", "main.py")));
        Assert.Equal("python", File.ReadAllText(Path.Combine(destination, "ComfyUI_windows_portable", "python_embeded", "python.exe")));
    }

    [Fact]
    public async Task ExtractAsync_uses_archive_signature_when_extension_says_7z()
    {
        string archive = Path.Combine(_root, "zip-data.7z");
        string destination = Path.Combine(_root, "zip-data-out");
        WriteZip(archive, ("ComfyUI/main.py", "zip-container"));

        await SafeArchiveExtractor.ExtractAsync(archive, destination, CancellationToken.None);

        Assert.Equal("zip-container", await File.ReadAllTextAsync(Path.Combine(destination, "ComfyUI", "main.py")));
    }

    [Fact]
    public async Task ExtractAsync_reads_7z_entries_sequentially()
    {
        Directory.CreateDirectory(_root);
        string archive = Path.Combine(_root, "fixture.7z");
        byte[] archiveBytes = Convert.FromBase64String(
            "N3q8ryccAASsXxZUoQAAAAAAAAAiAAAAAAAAAAe41E0BAA83ei1tYWluN3otY29uZmlnAAAAgTMHrg/PObAMB8hDf0Gx+v3i+3npbTzA+T34DqfX5UQ9cHAL+yEpTmvgi+eIbGHrzFCFcN9OXUiwoLii95tT2pxdgOkrnEE1TfX0k01YdTxuoULo5rluAWBddAZZK3ma++QNPUUxB4syS2xIRFcfUon9WPrMNfQEpA+BqdQJlsYsylGvqxp4JQUKABcGFAEJgI0ABwsBAAEjAwEBBV0AEAAADIDuCgGhxsmvAAA=");
        await File.WriteAllBytesAsync(archive, archiveBytes);

        string destination = Path.Combine(_root, "7z-out");
        await SafeArchiveExtractor.ExtractAsync(archive, destination, CancellationToken.None);

        Assert.Equal("7z-main", await File.ReadAllTextAsync(Path.Combine(destination, "ComfyUI", "main.py")));
        Assert.Equal("7z-config", await File.ReadAllTextAsync(Path.Combine(destination, "ComfyUI", "models", "config.json")));
    }

    [Fact]
    public async Task ExtractAsync_rejects_path_traversal_before_writing_outside_root()
    {
        string archive = Path.Combine(_root, "traversal.zip");
        string destination = Path.Combine(_root, "out");
        string escaped = Path.Combine(_root, "escaped.txt");
        WriteZip(archive, ("../escaped.txt", "must not escape"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeArchiveExtractor.ExtractAsync(archive, destination, CancellationToken.None));

        Assert.False(File.Exists(escaped));
    }

    [Fact]
    public async Task ExtractAsync_rejects_ntfs_alternate_data_stream_names_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        string archive = Path.Combine(_root, "ads.zip");
        string destination = Path.Combine(_root, "out");
        WriteZip(archive, ("visible.txt:hidden", "must not become an ADS"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeArchiveExtractor.ExtractAsync(archive, destination, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(destination, "visible.txt")));
    }

    [Fact]
    public async Task ExtractAsync_enforces_expanded_size_limit_using_real_bytes()
    {
        string archive = Path.Combine(_root, "large.zip");
        string destination = Path.Combine(_root, "out");
        WriteZip(archive, ("payload.bin", new string('x', 4096)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeArchiveExtractor.ExtractAsync(archive, destination, CancellationToken.None, maxExtractedBytes: 100));
    }
}
