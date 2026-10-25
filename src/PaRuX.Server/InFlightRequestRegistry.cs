namespace PaRuX.Server;

/// <summary>
/// Cancellation scopes for work that a revocable authority admitted: a request authenticated by one API key, or
/// an outbound call made under the remote-provider or cloud-fallback opt-in. Admission registers a lease in a scope.
/// Revoking the authority cancels every lease still in that scope, so work that started before the revocation stops
/// instead of running on under a credential or consent the user has withdrawn.
///
/// Revocation cancels; it never authorizes. A caller must check its authority again after <see cref="Begin"/>, so a
/// request admitted just before a revocation cannot slip past it. Cancellation runs synchronously, so registered
/// callbacks must not block on this registry.
/// </summary>
public sealed class InFlightRequestRegistry
{
    /// <summary>Outbound calls made to a non-local target while the user's remote-provider opt-in is on.</summary>
    public const string RemoteProviderScope = "remote-provider";

    /// <summary>The cloud-fallback credential check. No inference is ever sent through this scope.</summary>
    public const string CloudFallbackScope = "cloud-fallback";

    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<InFlightRequestLease>> _scopes = new(StringComparer.Ordinal);

    /// <summary>
    /// The process-wide registry. Singletons and static probe paths share it, so a revocation reaches every caller
    /// that admitted work. Tests create their own instances to stay isolated.
    /// </summary>
    public static InFlightRequestRegistry Shared { get; } = new();

    /// <summary>Scope of the requests one API key authenticated.</summary>
    public static string ApiKeyScope(string keyId) => "apikey:" + keyId;

    /// <summary>
    /// Admits one unit of work into <paramref name="scope"/>. The lease's token is cancelled when the scope is revoked
    /// or when <paramref name="requestToken"/> fires, whichever happens first. Dispose the lease when the work ends.
    /// </summary>
    public InFlightRequestLease Begin(string scope, CancellationToken requestToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var lease = new InFlightRequestLease(this, scope, requestToken);
        lock (_gate)
        {
            if (!_scopes.TryGetValue(scope, out HashSet<InFlightRequestLease>? leases))
                _scopes[scope] = leases = new HashSet<InFlightRequestLease>();
            leases.Add(lease);
        }
        return lease;
    }

    /// <summary>Cancels every lease in <paramref name="scope"/> and forgets it. Returns how many were cancelled.</summary>
    public int Cancel(string scope)
    {
        lock (_gate)
        {
            if (!_scopes.Remove(scope, out HashSet<InFlightRequestLease>? leases)) return 0;
            foreach (InFlightRequestLease lease in leases) lease.Revoke();
            return leases.Count;
        }
    }

    /// <summary>How many leases are still admitted in <paramref name="scope"/>.</summary>
    public int ActiveCount(string scope)
    {
        lock (_gate)
            return _scopes.TryGetValue(scope, out HashSet<InFlightRequestLease>? leases) ? leases.Count : 0;
    }

    internal void Release(InFlightRequestLease lease)
    {
        lock (_gate)
        {
            if (_scopes.TryGetValue(lease.Scope, out HashSet<InFlightRequestLease>? leases))
            {
                leases.Remove(lease);
                if (leases.Count == 0) _scopes.Remove(lease.Scope);
            }
            lease.Close();
        }
    }
}

/// <summary>One admitted unit of in-flight work. Dispose it when the work completes, whether or not it was cancelled.</summary>
public sealed class InFlightRequestLease : IDisposable
{
    private readonly InFlightRequestRegistry _registry;
    private readonly CancellationTokenSource _source;
    private bool _revoked;
    private bool _closed;

    internal InFlightRequestLease(InFlightRequestRegistry registry, string scope, CancellationToken requestToken)
    {
        _registry = registry;
        Scope = scope;
        _source = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
    }

    public string Scope { get; }

    /// <summary>Cancelled when the request aborts or the scope is revoked.</summary>
    public CancellationToken Token => _source.Token;

    public void Dispose() => _registry.Release(this);

    // Both run under the registry gate, so a revoked lease is never disposed while it is being cancelled.
    internal void Revoke()
    {
        if (_revoked || _closed) return;
        _revoked = true;
        _source.Cancel();
    }

    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        if (!_revoked) _source.Dispose();
    }
}
