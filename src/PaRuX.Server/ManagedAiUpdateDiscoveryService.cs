namespace PaRuX.Server;

public sealed record ManagedAiUpdateCheckResult(
    ManagedAiAssetIdentity Asset,
    bool UpdateAvailable,
    string? InstalledVersion,
    string? AvailableVersion,
    string? SourceRevision,
    string? SourceUri,
    string? ExpectedSha256,
    string? Message = null,
    bool UserManaged = false,
    bool ObservedInstalledUpdate = false);

public interface IManagedAiUpdateSource
{
    ManagedAiAssetIdentity Asset { get; }
    Task<ManagedAiUpdateCheckResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed class ManagedAiUpdateDiscoverySummary
{
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
    public int SourcesChecked { get; set; }
    public int UpdatesFound { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<ManagedAiUpdateRecord> Updates { get; set; } = new();
}

/// <summary>
/// Bounded update discovery coordinator. Network/provider failures are collected as diagnostics and
/// never become startup/Chat failures. Discovery can report either a PaRuX-installable update or a
/// new version that was already installed by an external trusted lifecycle such as Windows Update.
/// Download, verification, benchmark and promotion remain separate privileged operations.
/// </summary>
public sealed class ManagedAiUpdateDiscoveryService
{
    private readonly ManagedAiUpdateStore _store;
    private readonly IReadOnlyList<IManagedAiUpdateSource> _sources;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ManagedAiUpdateDiscoveryService(
        ManagedAiUpdateStore store,
        IEnumerable<IManagedAiUpdateSource> sources)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        var resolved = (sources ?? throw new ArgumentNullException(nameof(sources))).ToList();

        // The Windows ML source is a release-surface capability, not a universal server
        // dependency. Auto-register it only when the optional PaRuX worker is physically present
        // (installed MSI or explicit development override). Unit tests/dev builds without the
        // worker preserve their exact source count and remain platform-neutral.
        var windowsMl = new WindowsMlPerformancePackService();
        if (windowsMl.WorkerAvailable
            && resolved.All(source => !string.Equals(
                source.Asset.StableKey,
                WindowsMlManagedUpdateSource.TensorRtAsset.StableKey,
                StringComparison.OrdinalIgnoreCase)))
            resolved.Add(new WindowsMlManagedUpdateSource(windowsMl));

        _sources = resolved;
    }

    public async Task<ManagedAiUpdateDiscoverySummary> CheckAllAsync(CancellationToken cancellationToken)
    {
        var summary = new ManagedAiUpdateDiscoverySummary();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (IManagedAiUpdateSource source in _sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                summary.SourcesChecked++;
                try
                {
                    ManagedAiUpdateCheckResult result = await source.CheckAsync(cancellationToken).ConfigureAwait(false);
                    ValidateSourceIdentity(source, result);

                    if (result.ObservedInstalledUpdate)
                    {
                        if (string.IsNullOrWhiteSpace(result.InstalledVersion))
                            throw new InvalidDataException("Update source reported an observed installed update without an installed version.");
                        ManagedAiUpdateRecord? observed = _store.RecordObservedInstalledVersion(
                            result.Asset,
                            result.InstalledVersion,
                            result.SourceRevision,
                            result.SourceUri,
                            result.ExpectedSha256,
                            result.UserManaged,
                            result.Message);
                        if (observed != null)
                        {
                            summary.UpdatesFound++;
                            summary.Updates.Add(observed);
                        }
                        continue;
                    }

                    if (!result.UpdateAvailable) continue;
                    if (string.IsNullOrWhiteSpace(result.AvailableVersion))
                        throw new InvalidDataException("Update source reported availability without an available version.");

                    ManagedAiUpdateRecord record = _store.UpsertAvailable(
                        result.Asset,
                        result.InstalledVersion,
                        result.AvailableVersion,
                        result.SourceRevision,
                        result.SourceUri,
                        result.ExpectedSha256,
                        result.UserManaged,
                        result.Message);
                    summary.UpdatesFound++;
                    summary.Updates.Add(record);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    summary.Errors.Add(source.Asset.StableKey + ": " + ex.Message);
                }
            }
            summary.CheckedUtc = DateTime.UtcNow;
            return summary;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateSourceIdentity(IManagedAiUpdateSource source, ManagedAiUpdateCheckResult result)
    {
        if (!string.Equals(source.Asset.StableKey, result.Asset.StableKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update source returned a different asset identity than the one it owns.");
        if (result.UpdateAvailable && result.ObservedInstalledUpdate)
            throw new InvalidDataException("Update source cannot report the same version as both available and already installed.");
    }
}

/// <summary>
/// Small delegate-backed source used by adapters that already know how to query their authoritative
/// upstream (for example current llama.cpp release code or Model Hub HF metadata). It avoids
/// duplicating transport logic merely to participate in the common notification/update pipeline.
/// </summary>
public sealed class DelegateManagedAiUpdateSource : IManagedAiUpdateSource
{
    private readonly Func<CancellationToken, Task<ManagedAiUpdateCheckResult>> _check;

    public DelegateManagedAiUpdateSource(
        ManagedAiAssetIdentity asset,
        Func<CancellationToken, Task<ManagedAiUpdateCheckResult>> check)
    {
        Asset = asset;
        _check = check ?? throw new ArgumentNullException(nameof(check));
    }

    public ManagedAiAssetIdentity Asset { get; }

    public Task<ManagedAiUpdateCheckResult> CheckAsync(CancellationToken cancellationToken) => _check(cancellationToken);
}
