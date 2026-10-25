using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Runtime admission boundary for Training Lab jobs. The asynchronous v2 API resolves the complete
/// pinned runtime status once and then calls ValidateStatus; the synchronous helper remains for
/// non-HTTP callers that need the same fail-closed policy.
/// </summary>
internal static class TrainingRuntimeJobGate
{
    internal static void Validate(string runtimeRoot, string venvRoot, string method) =>
        _ = GetVerifiedStatus(runtimeRoot, venvRoot, method);

    internal static TrainingRuntimeStatus GetVerifiedStatus(string runtimeRoot, string venvRoot, string method)
    {
        TrainingRuntimeStatus status = TrainingRuntimeCatalog
            .GetStatusAsync(runtimeRoot, venvRoot, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        ValidateStatus(status, method);
        return status;
    }

    internal static void ValidateStatus(TrainingRuntimeStatus status, string method)
    {
        ArgumentNullException.ThrowIfNull(status);
        string normalized = TrainingMethod.Normalize(method);

        if (!status.Installed)
            throw new TrainingRuntimeRefusalException(TrainingEligibility.NotInstalled,
                "Training Lab's PaRuX-managed engine is not installed on this machine yet.");
        if (!status.Ready)
            throw new InvalidOperationException(
                "Training Lab runtime is not ready for new jobs. " + status.Detail);
        string? backend = status.Probe?.Backend ?? status.Manifest?.Backend;
        (bool eligible, string code) = TrainingEligibility.Check(normalized, backend, status.Installed);
        if (!eligible || !status.AvailableMethods.Contains(normalized, StringComparer.Ordinal))
            throw new TrainingRuntimeRefusalException(eligible ? TrainingEligibility.BackendUnvalidated : code,
                "Training method '" + normalized + "' is not enabled for backend '"
                + (status.Probe?.Backend ?? status.Manifest?.Backend ?? "unknown")
                + "'. PaRuX will not start a trainer on an unvalidated execution path.");
    }
}
