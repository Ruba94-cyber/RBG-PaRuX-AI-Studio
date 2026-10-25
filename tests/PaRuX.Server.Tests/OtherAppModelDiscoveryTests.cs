using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OtherAppModelDiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-otherapps-" + Guid.NewGuid().ToString("N"));
    private string Library => Path.Combine(_dir, "library");

    public OtherAppModelDiscoveryTests() => Directory.CreateDirectory(Library);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static byte[] Gguf(byte version = 3, int size = 64)
    {
        var bytes = new byte[size];
        "GGUF"u8.CopyTo(bytes);
        bytes[4] = version;
        return bytes;
    }

    private string Write(string relative, byte[] bytes)
    {
        string path = Path.GetFullPath(Path.Combine(_dir, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Jan_and_KoboldCpp_models_are_listed_read_only_with_their_source()
    {
        string jan = Write("jan/data/models/tinyllama-q4/tinyllama.Q4_K_M.gguf", Gguf());
        string kobold = Write("kobold/gemma-2b-Q5_K_M.gguf", Gguf());
        var roots = new ExternalModelStoreImporter.StoreRoots(null, Array.Empty<string>(),
            new[] { Path.GetFullPath(Path.Combine(_dir, "jan/data/models")) }, new[] { Path.GetFullPath(Path.Combine(_dir, "kobold")) });
        string before = File.GetLastWriteTimeUtc(jan).Ticks + "|" + File.GetLastWriteTimeUtc(kobold).Ticks;

        var found = ExternalModelStoreImporter.Discover(roots, Library);

        Assert.Contains(found, c => c.Source == "jan" && c.SourcePath == jan);
        Assert.Contains(found, c => c.Source == "koboldcpp" && c.SourcePath == kobold);
        Assert.Equal(before, File.GetLastWriteTimeUtc(jan).Ticks + "|" + File.GetLastWriteTimeUtc(kobold).Ticks);
        Assert.Empty(Directory.GetFiles(Library));
        var lines = ExternalModelsPresenter.Lines(found);
        Assert.All(lines, l => Assert.Contains("read-only", l.Detail));
        Assert.Contains(lines, l => l.Detail.Contains("Q4_K_M") && l.Source == "Jan");
    }

    [Fact]
    public void Files_with_bad_gguf_header_are_not_offered()
    {
        Write("kobold/notamodel.gguf", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Write("kobold/badversion.gguf", Gguf(version: 99));
        Write("kobold/short.gguf", "GGU"u8.ToArray());
        var roots = new ExternalModelStoreImporter.StoreRoots(null, Array.Empty<string>(), null, new[] { Path.GetFullPath(Path.Combine(_dir, "kobold")) });
        Assert.Empty(ExternalModelStoreImporter.Discover(roots, Library));
    }

    [Fact]
    public void Unrecognised_layouts_are_reported_honestly()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "ollama-odd"));
        Write("jan/data/models/readme.txt", new byte[] { 1 });
        var roots = new ExternalModelStoreImporter.StoreRoots(Path.Combine(_dir, "ollama-odd"), new[] { Path.Combine(_dir, "missing") },
            new[] { Path.GetFullPath(Path.Combine(_dir, "jan/data/models")) });

        var reports = ExternalModelStoreImporter.Inspect(roots, Library);

        Assert.Contains("not recognised as an Ollama store", reports.Single(r => r.Source == "ollama").Note);
        Assert.Equal("Folder not found.", reports.Single(r => r.Source == "lmstudio").Note);
        Assert.Contains("No valid GGUF files", reports.Single(r => r.Source == "jan").Note);
        Assert.Contains("No models from other apps were found.", ExternalModelsPresenter.EmptyState(reports));
    }
}
