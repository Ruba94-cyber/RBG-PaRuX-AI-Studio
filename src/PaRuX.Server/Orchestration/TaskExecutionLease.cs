namespace PaRuX.Server.Orchestration;

/// <summary>
/// Immutable authority for one server-worker attempt. Its token helps stop work promptly; the
/// persisted attempt ID is the authority for every durable worker mutation.
/// </summary>
internal sealed class TaskExecutionLease : IDisposable
{
    private readonly TaskStore _store;
    private readonly object _sync = new();
    private bool _disposeRequested;
    private bool _sourceDisposed;
    private int _reservedCancellationDispatches;
    internal CancellationTokenSource StopSource { get; }

    internal TaskExecutionLease(TaskStore store, string taskId, string attemptId, CancellationTokenSource stopSource)
    {
        _store = store;
        TaskId = taskId;
        AttemptId = attemptId;
        StopSource = stopSource;
        StopToken = stopSource.Token;
    }

    internal string TaskId { get; }
    internal string AttemptId { get; }
    internal CancellationToken StopToken { get; }

    internal void ReserveCancellationDispatch()
    {
        lock (_sync) _reservedCancellationDispatches++;
    }

    internal void CancelReserved()
    {
        try { StopSource.Cancel(); }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException) { }
        finally
        {
            lock (_sync)
            {
                _reservedCancellationDispatches--;
                DisposeSourceIfReady();
            }
        }
    }

    internal void RequestDispose()
    {
        lock (_sync)
        {
            _disposeRequested = true;
            DisposeSourceIfReady();
        }
    }

    private void DisposeSourceIfReady()
    {
        if (_disposeRequested && _reservedCancellationDispatches == 0 && !_sourceDisposed)
        {
            _sourceDisposed = true;
            StopSource.Dispose();
        }
    }

    public void Dispose() => _store.ReleaseExecutionLease(this);
}
