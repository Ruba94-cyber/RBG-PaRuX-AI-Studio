using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExLlamaV3ManagedPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-exllamav3-" + Guid.NewGuid().ToString("N"));

    public ExLlamaV3ManagedPackTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Catalog_selects_only_matching_windows_cp311_cuda128_torch210_wheel()
    {
        RuntimeReleaseAsset expected = Wheel("exllamav3-1.4.4+cu128.torch2.10.0-cp311-cp311-win_amd64.whl", 'a');
        RuntimeReleaseAsset[] assets =
        {
            Wheel("exllamav3-1.4.4+cu128.torch2.10.0-cp310-cp310-win_amd64.whl", 'b'),
            Wheel("exllamav3-1.4.4+cu128.torch2.10.0-cp311-cp311-linux_x86_64.whl", 'c'),
            Wheel("exllamav3-1.4.4+cu132.torch2.11.0-cp311-cp311-win_amd64.whl", 'd'),
            expected
        };

        RuntimeReleaseAsset? selected = ExLlamaV3BuildCatalog.SelectWindowsWheel(assets);

        Assert.NotNull(selected);
        Assert.Equal(expected.Name, selected!.Name);
    }

    [Fact]
    public void Catalog_rejects_compatible_name_without_authenticated_digest()
    {
        string name = "exllamav3-1.4.4+cu128.torch2.10.0-cp311-cp311-win_amd64.whl";
        var release = new RuntimeReleaseInfo("v1.4.4", DateTime.UtcNow,
            new[] { new RuntimeReleaseAsset(name, DownloadUrl("v1.4.4", name), 1000, null) });

        Assert.Throws<InvalidDataException>(() => ExLlamaV3BuildCatalog.RequireVerifiedWheel(release));
    }

    [Fact]
    public void Pack_versions_are_side_by_side_and_active_version_is_explicit()
    {
        CreateInstalled("v1.4.3");
        CreateInstalled("v1.4.4");
        var service = new ExLlamaV3ManagedPackService(root: _root);

        Assert.Equal(new[] { "v1.4.3", "v1.4.4" }, service.ListInstalledTags());
        Assert.Null(service.GetActiveTag());

        service.SetActive("v1.4.4");
        Assert.Equal("v1.4.4", service.GetActiveTag());
        Assert.Throws<InvalidOperationException>(() => service.Remove("v1.4.4"));

        service.SetActive("v1.4.3");
        service.Remove("v1.4.4");
        Assert.False(service.IsInstalled("v1.4.4"));
        Assert.Equal("v1.4.3", service.GetActiveTag());
    }

    private RuntimeReleaseAsset Wheel(string name, char digestChar)
    {
        return new RuntimeReleaseAsset(
            name,
            DownloadUrl("v1.4.4", name),
            260_000_000,
            "sha256:" + new string(digestChar, 64));
    }

    private static string DownloadUrl(string tag, string name) =>
        "https://github.com/turboderp-org/exllamav3/releases/download/" + tag + "/" + Uri.EscapeDataString(name);

    private void CreateInstalled(string tag)
    {
        string python = Path.Combine(_root, tag, "venv", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "test");
    }
}
