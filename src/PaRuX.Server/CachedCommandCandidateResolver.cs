namespace PaRuX.Server;

/// <summary>
/// Resolves an optional external command once when available, while keeping absence retryable and
/// making discovery obey the caller's cancellation token. Missing commands are negatively cached
/// briefly so machines without NVIDIA tooling do not pay repeated process-start costs.
/// </summary>
internal sealed class CachedCommandCandidateResolver
{
    private readonly string[] _candidates;
    private readonly Func<string, CancellationToken, Task<string?>> _probe;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _missingRetryInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _resolved;
    private long _retryAfterUtcTicks;

    internal CachedCommandCandidateResolver(
        IEnumerable<string> candidates,
        Func<string, CancellationToken, Task<string?>> probe,
        TimeSpan missingRetryInterval,
        Func<DateTime>? utcNow = null)
    {
        _candidates = candidates?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            ?? throw new ArgumentNullException(nameof(candidates));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        if (missingRetryInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(missingRetryInterval));
        _missingRetryInterval = missingRetryInterval;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal async Task<string?> ResolveAsync(CancellationToken ct)
    {
        string? resolved = Volatile.Read(ref _resolved);
        if (resolved != null) return resolved;
        if (_utcNow().Ticks < Volatile.Read(ref _retryAfterUtcTicks)) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            resolved = Volatile.Read(ref _resolved);
            if (resolved != null) return resolved;
            if (_utcNow().Ticks < Volatile.Read(ref _retryAfterUtcTicks)) return null;

            foreach (string candidate in _candidates)
            {
                if (await _probe(candidate, ct).ConfigureAwait(false) == null) continue;
                Volatile.Write(ref _resolved, candidate);
                return candidate;
            }

            Volatile.Write(ref _retryAfterUtcTicks, (_utcNow() + _missingRetryInterval).Ticks);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
