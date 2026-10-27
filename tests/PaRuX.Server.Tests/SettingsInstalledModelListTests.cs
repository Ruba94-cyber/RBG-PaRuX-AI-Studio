using PaRuX.Intelligence;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SettingsInstalledModelListTests
{
    private static GgufModelInfo M(string path, long size = 1) =>
        new() { FileName = Path.GetFileName(path), FilePath = path, SizeBytes = size };

    [Fact]
    public void Managed_library_models_are_listed_when_the_external_folder_is_empty()
    {
        // Clean install: the managed folder (PaRuX data root) differs from the Local AI models folder, which has nothing.
        var managed = new[] { M(@"C:\data\PaRuX\models\a-Q4_K_M.gguf"), M(@"C:\data\PaRuX\models\b-Q8_0.gguf") };
        List<GgufModelInfo> merged = InstalledModelMerge.Merge(managed, Array.Empty<GgufModelInfo>());
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void A_file_visible_through_both_sources_is_listed_once_with_the_managed_entry()
    {
        var managed = new[] { M(@"C:\data\models\a.gguf", 300) };
        var external = new[] { M(@"C:\DATA\models\..\models\a.gguf", 100), M(@"D:\localai\models\ext.gguf") };
        List<GgufModelInfo> merged = InstalledModelMerge.Merge(managed, external);
        Assert.Equal(2, merged.Count);
        Assert.Equal(300, merged[0].SizeBytes);
        Assert.Equal("ext.gguf", merged[1].FileName);
    }
}
