namespace PaRuX.Server;

/// <summary>
/// Process-wide writer-priority reader/writer authority for PaRuX GPU residency.
///
/// Text inference uses shared read leases, preserving llama.cpp continuous batching and independent
/// concurrent chat turns. Image/video/Engine Race uses an exclusive media writer lease because a
/// media runtime transition releases text GPU residency and two media renderers cannot safely share
/// the workstation GPU. As soon as a media writer queues, new text readers wait; already-admitted
/// text responses drain naturally, then exactly one media writer runs. This prevents both text
/// starvation of media and media truncation of already-streaming text.
/// </summary>
public sealed class MediaExecutionCoordinator
{
    public static MediaExecutionCoordinator Current { get; } = new();

    private static readonly AsyncLocal<ContextScope?> CurrentScope = new();

    internal sealed class ContextScope
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<MediaExecutionCoordinator, int> Readers = new();
        public readonly System.Collections.Concurrent.ConcurrentDictionary<MediaExecutionCoordinator, int> Parked = new();
    }

    private sealed class ScopeDisposable : IDisposable
    {
        private readonly ContextScope? _previous;
        public ScopeDisposable(ContextScope? previous) => _previous = previous;
        public void Dispose() => CurrentScope.Value = _previous;
    }

    public static IDisposable EnterContextScope()
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = new ContextScope();
        return new ScopeDisposable(previous);
    }

    /// <summary>
    /// Returns the number of text reader leases owned by the current async context scope.
    /// This lets callers that already hold the exclusive writer distinguish between readers that
    /// belong to this same async flow (safe to ignore) and external readers (must drain to zero).
    /// </summary>
    public static int ContextOwnedReaders
    {
        get { lock (Current._gate) return Current.ReadersInScope(CurrentScope.Value); }
    }

    private int ReadersInScope(ContextScope? scope) =>
        scope != null && scope.Readers.TryGetValue(this, out int count) ? count : 0;

    private void ChangeScopeReaders(ContextScope? scope, int delta)
    {
        if (scope != null) scope.Readers[this] = ReadersInScope(scope) + delta;
    }

    // A request has not started generation when it prepares a model. Release its reservation
    // while queuing a writer, otherwise two requests upgrading simultaneously deadlock.
    // Never suspend multiple readers: those may be sibling generations in an inherited context.
    private int ParkReader(ContextScope? scope)
    {
        int readers = ReadersInScope(scope);
        if (readers > 1) throw new InvalidOperationException("Model preparation requires its own request context, not a context shared by multiple text readers.");
        _activeText -= readers;
        ChangeScopeReaders(scope, -readers);
        if (scope != null) scope.Parked[this] = scope.Parked.GetValueOrDefault(this) + readers;
        return readers;
    }

    private void RestoreReader(ContextScope? scope, int count)
    {
        if (scope == null) return;
        int restore = Math.Min(count, scope.Parked.GetValueOrDefault(this));
        scope.Parked[this] = scope.Parked.GetValueOrDefault(this) - restore;
        _activeText += restore;
        ChangeScopeReaders(scope, restore);
    }

    private readonly object _gate = new();
    private string? _activeMediaOwner;
    private DateTime? _activeMediaSinceUtc;
    private int _waitingMedia;
    private int _activeText;
    private TaskCompletionSource<bool> _stateChanged = NewSignal();

    internal MediaExecutionCoordinator() { }

    public async ValueTask<Lease> AcquireAsync(string owner, CancellationToken cancellationToken)
    {
        string normalized = ValidateOwner(owner);
        ContextScope? scope = CurrentScope.Value;
        int parked;
        lock (_gate)
        {
            parked = ParkReader(scope);
            _waitingMedia++;
            SignalStateChangedUnderLock();
        }

        bool acquired = false;
        try
        {
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    if (_activeMediaOwner == null && _activeText == 0)
                    {
                        _waitingMedia--;
                        _activeMediaOwner = normalized;
                        _activeMediaSinceUtc = DateTime.UtcNow;
                        acquired = true;
                        SignalStateChangedUnderLock();
                        return new Lease(this, normalized, LeaseKind.Media, scope, parked);
                    }
                    signal = _stateChanged.Task;
                }
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!acquired)
            {
                lock (_gate)
                {
                    _waitingMedia = Math.Max(0, _waitingMedia - 1);
                    // A cancelled request must dispose its parked reader. Restoring it here
                    // could introduce a reader underneath another request's active writer.
                    SignalStateChangedUnderLock();
                }
            }
        }
    }

    public async ValueTask<Lease> AcquireTextAsync(string owner, CancellationToken cancellationToken)
    {
        string normalized = ValidateOwner(owner);
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                if (_activeMediaOwner == null && _waitingMedia == 0)
                {
                    _activeText++;
                    ChangeScopeReaders(CurrentScope.Value, 1);
                    SignalStateChangedUnderLock();
                    return new Lease(this, normalized, LeaseKind.Text, CurrentScope.Value);
                }
                signal = _stateChanged.Task;
            }
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WaitForMediaAvailabilityAsync(CancellationToken cancellationToken)
    {
        ContextScope? scope = CurrentScope.Value;
        int parked;
        lock (_gate)
        {
            if (_activeMediaOwner == null && _waitingMedia == 0) return;
            parked = ParkReader(scope);
            SignalStateChangedUnderLock();
        }
        try
        {
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    if (_activeMediaOwner == null && _waitingMedia == 0)
                    {
                        RestoreReader(scope, parked);
                        parked = 0;
                        return;
                    }
                    signal = _stateChanged.Task;
                }
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate) { SignalStateChangedUnderLock(); }
        }
    }

    public Status Snapshot()
    {
        lock (_gate)
        {
            return new Status(
                _activeMediaOwner,
                _activeMediaSinceUtc,
                _waitingMedia,
                _activeText);
        }
    }

    internal bool IsCurrentMediaWriter(Lease? lease, string ownerPrefix)
    {
        if (lease == null
            || !ReferenceEquals(lease.Coordinator, this)
            || lease.Kind != LeaseKind.Media)
            return false;

        lock (_gate)
            return _activeMediaOwner != null
                && string.Equals(_activeMediaOwner, lease.Name, StringComparison.Ordinal)
                && _activeMediaOwner.StartsWith(ownerPrefix, StringComparison.Ordinal);
    }

    private void Release(LeaseKind kind, ContextScope? scope, int parked)
    {
        lock (_gate)
        {
            if (kind == LeaseKind.Media)
            {
                _activeMediaOwner = null;
                _activeMediaSinceUtc = null;
                RestoreReader(scope, parked);
            }
            else
            {
                if (scope != null && scope.Parked.GetValueOrDefault(this) > 0)
                    scope.Parked[this]--;
                else
                {
                    _activeText = Math.Max(0, _activeText - 1);
                    ChangeScopeReaders(scope, -1);
                }
            }
            SignalStateChangedUnderLock();
        }
    }

    private static string ValidateOwner(string? owner)
    {
        string normalized = (owner ?? string.Empty).Trim();
        if (normalized.Length is < 1 or > 160 || normalized.Any(char.IsControl))
            throw new ArgumentException("GPU execution owner must be 1-160 printable characters.", nameof(owner));
        return normalized;
    }

    private void SignalStateChangedUnderLock()
    {
        TaskCompletionSource<bool> prior = _stateChanged;
        _stateChanged = NewSignal();
        prior.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal enum LeaseKind { Text, Media }

    public sealed class Lease : IAsyncDisposable, IDisposable
    {
        private MediaExecutionCoordinator? _owner;
        private readonly string _name;
        private readonly LeaseKind _kind;
        private readonly ContextScope? _scope;
        private readonly int _parked;

        internal Lease(MediaExecutionCoordinator owner, string name, LeaseKind kind, ContextScope? scope = null, int parked = 0)
        {
            _owner = owner;
            _name = name;
            _kind = kind;
            _scope = scope;
            _parked = parked;
        }

        internal MediaExecutionCoordinator? Coordinator => Volatile.Read(ref _owner);
        internal string Name => _name;
        internal LeaseKind Kind => _kind;

        public void Dispose()
        {
            MediaExecutionCoordinator? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_kind, _scope, _parked);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    public sealed record Status(
        string? ActiveOwner,
        DateTime? ActiveSinceUtc,
        int Waiting,
        int ActiveTextReaders);
}
