using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WindowsMlManagedUpdateSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-winml-update-" + Guid.NewGuid().ToString("N"));

    public WindowsMlManagedUpdateSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Compatible_not_ready_provider_is_installable_update()
    {
        var source = new WindowsMlManagedUpdateSource(_ => Task.FromResult(new WindowsMlPerformancePackStatus
        {
            SupportedOs = true,
            WorkerAvailable = true,
            TensorRtRtxCompatible = true,
            TensorRtRtxReady = false,
            TensorRtRtxReadyState = "NotPresent"
        }));

        ManagedAiUpdateCheckResult result = await source.CheckAsync(CancellationToken.None);

        Assert.True(result.UpdateAvailable);
        Assert.False(result.ObservedInstalledUpdate);
        Assert.Equal("windowsml-current", result.AvailableVersion);
    }

    [Fact]
    public async Task Ready_provider_is_reported_as_already_installed_fingerprint()
    {
        string sha = new string('a', 64);
        var source = new WindowsMlManagedUpdateSource(_ => Task.FromResult(new WindowsMlPerformancePackStatus
        {
            SupportedOs = true,
            WorkerAvailable = true,
            TensorRtRtxCompatible = true,
            TensorRtRtxReady = true,
            TensorRtRtxReadyState = "Ready",
            TensorRtRtxLibraryFileVersion = "2.30.43.0",
            TensorRtRtxLibrarySha256 = sha
        }));

        ManagedAiUpdateCheckResult result = await source.CheckAsync(CancellationToken.None);

        Assert.False(result.UpdateAvailable);
        Assert.True(result.ObservedInstalledUpdate);
        Assert.Equal("2.30.43.0:" + sha, result.InstalledVersion);
        Assert.Equal(sha, result.ExpectedSha256);
    }

    [Fact]
    public void Observed_installed_version_notifies_only_when_fingerprint_changes()
    {
        string path = Path.Combine(_root, "updates.json");
        var store = new ManagedAiUpdateStore(path);
        ManagedAiAssetIdentity asset = WindowsMlManagedUpdateSource.TensorRtAsset;

        ManagedAiUpdateRecord? first = store.RecordObservedInstalledVersion(
            asset, "1.0:" + new string('a', 64), "1.0", "https://example.invalid", new string('a', 64), message: "ready");
        ManagedAiUpdateRecord? duplicate = store.RecordObservedInstalledVersion(
            asset, "1.0:" + new string('a', 64), "1.0", "https://example.invalid", new string('a', 64), message: "ready");
        ManagedAiUpdateRecord? changed = store.RecordObservedInstalledVersion(
            asset, "2.0:" + new string('b', 64), "2.0", "https://example.invalid", new string('b', 64), message: "updated");

        Assert.NotNull(first);
        Assert.Null(duplicate);
        Assert.NotNull(changed);
        Assert.Equal(ManagedAiUpdateState.Installed, changed!.State);
        Assert.Equal(first!.InstalledVersion, changed.RollbackVersion);
        Assert.False(changed.NotificationAcknowledged);
    }
}
