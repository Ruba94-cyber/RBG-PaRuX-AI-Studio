using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Serializes llama-bench probe execution with every other runtime/GPU performance mutation.
/// RuntimePerformanceCoordinator is AsyncLocal-reentrant, so the quick tuner invoked from inside a
/// full Optimize run can acquire this wrapper without deadlocking, while an independent direct
/// Tune/update/build-race request waits for the current measurement lane to finish.
/// </summary>
public sealed class CoordinatedLlamaPerformanceProbe : ILlamaPerformanceProbe
{
    private readonly ILlamaPerformanceProbe _inner;

    public CoordinatedLlamaPerformanceProbe(ILlamaPerformanceProbe inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public async Task<LlamaPerformanceProbeMeasurement> RunAsync(
        string modelPath,
        LlamaPerformanceProfileDto profile,
        CancellationToken cancellationToken)
    {
        await using IAsyncDisposable lane = await RuntimePerformanceCoordinator.Shared
            .AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.RunAsync(modelPath, profile, cancellationToken).ConfigureAwait(false);
    }
}
