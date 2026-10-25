namespace PaRuX.Server;

/// <summary>
/// Update-center bridge for the system-managed Windows ML TensorRT-RTX execution provider.
/// NotPresent/NotReady means PaRuX can explicitly ask Windows ML to make the provider ready.
/// Ready means Windows already owns the installed bytes; the library fingerprint is reported as an
/// already-applied update so a new Windows Update revision becomes a fresh PaRuX notification and
/// benchmark candidate rather than a fake pending download.
/// </summary>
public sealed class WindowsMlManagedUpdateSource : IManagedAiUpdateSource
{
    public static readonly ManagedAiAssetIdentity TensorRtAsset = new(
        ManagedAiAssetKind.InferenceEngine,
        WindowsMlPerformancePackService.TensorRtEngineId,
        "TensorRT-RTX · Windows ML");

    private readonly Func<CancellationToken, Task<WindowsMlPerformancePackStatus>> _status;

    public WindowsMlManagedUpdateSource(WindowsMlPerformancePackService? service = null)
        : this((service ?? new WindowsMlPerformancePackService()).GetStatusAsync) { }

    internal WindowsMlManagedUpdateSource(
        Func<CancellationToken, Task<WindowsMlPerformancePackStatus>> status)
    {
        _status = status ?? throw new ArgumentNullException(nameof(status));
    }

    public ManagedAiAssetIdentity Asset => TensorRtAsset;

    public async Task<ManagedAiUpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        WindowsMlPerformancePackStatus status = await _status(cancellationToken).ConfigureAwait(false);
        if (!status.SupportedOs || !status.WorkerAvailable || !status.TensorRtRtxCompatible)
        {
            return new ManagedAiUpdateCheckResult(
                Asset,
                UpdateAvailable: false,
                InstalledVersion: null,
                AvailableVersion: null,
                SourceRevision: null,
                SourceUri: null,
                ExpectedSha256: null,
                Message: status.Error ?? "TensorRT-RTX is not available on this Windows/GPU configuration.");
        }

        const string source = "https://learn.microsoft.com/windows/ai/new-windows-ml/supported-execution-providers";
        if (!status.TensorRtRtxReady)
        {
            string current = string.IsNullOrWhiteSpace(status.TensorRtRtxReadyState)
                ? "not-ready"
                : status.TensorRtRtxReadyState!;
            return new ManagedAiUpdateCheckResult(
                Asset,
                UpdateAvailable: true,
                InstalledVersion: current,
                AvailableVersion: "windowsml-current",
                SourceRevision: null,
                SourceUri: source,
                ExpectedSha256: null,
                Message: "TensorRT-RTX is compatible with this PC but is not Ready. PaRuX can ask Windows ML to download/register the certified performance pack.");
        }

        if (string.IsNullOrWhiteSpace(status.TensorRtRtxFingerprint)
            || string.IsNullOrWhiteSpace(status.TensorRtRtxLibrarySha256))
        {
            return new ManagedAiUpdateCheckResult(
                Asset,
                UpdateAvailable: false,
                InstalledVersion: status.TensorRtRtxReadyState,
                AvailableVersion: null,
                SourceRevision: status.TensorRtRtxLibraryFileVersion,
                SourceUri: source,
                ExpectedSha256: null,
                Message: "TensorRT-RTX is Ready, but PaRuX could not fingerprint its library bytes; automatic update-change notification remains unavailable for this installation.");
        }

        return new ManagedAiUpdateCheckResult(
            Asset,
            UpdateAvailable: false,
            InstalledVersion: status.TensorRtRtxFingerprint,
            AvailableVersion: null,
            SourceRevision: status.TensorRtRtxLibraryFileVersion,
            SourceUri: source,
            ExpectedSha256: status.TensorRtRtxLibrarySha256,
            Message: "TensorRT-RTX is Ready. Its Windows-managed runtime fingerprint is new or changed and must pass PaRuX Engine Race before promotion.",
            ObservedInstalledUpdate: true);
    }
}
