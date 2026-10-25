namespace PaRuX.Server;

/// <summary>
/// One process-wide GPU/runtime mutation lane for tuning/racing. Re-entrancy is tracked through
/// AsyncLocal so RuntimeOptimizationService can hold the lane while the internally spawned quick
/// tuner inherits the same execution context. Independent user requests still serialize.
///
/// AcquireAsync is deliberately NOT declared `async`. An AsyncLocal write made inside a method that
/// actually suspends at an `await` is invisible to the caller once that method's Task completes -
/// the compiler-generated state machine forks a local ExecutionContext for the suspension that is
/// discarded on return, so a caller that awaits an async AcquireAsync would never observe the
/// depth/ownership it just established (proven by a scratch repro: a value assigned after `await
/// Task.Delay(1)` inside a called async method never reaches the awaiter). Splitting the method so
/// the AsyncLocal write happens synchronously - before delegating the actual semaphore wait to a
/// private async helper that this method returns without awaiting - keeps that write in the
/// caller's own execution context, since no suspension has occurred yet at that point.
/// </summary>
public sealed class RuntimePerformanceCoordinator
{
    public static RuntimePerformanceCoordinator Shared { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<int> _depth = new();

    private RuntimePerformanceCoordinator() { }

    public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken ct)
    {
        if (_depth.Value > 0)
        {
            _depth.Value++;
            return new ValueTask<IAsyncDisposable>(new Lease(this, ownsGate: false));
        }

        _depth.Value = 1;
        return AcquireGateAsync(ct);
    }

    private async ValueTask<IAsyncDisposable> AcquireGateAsync(CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // The synchronous prefix in AcquireAsync already marked this call chain as depth 1
            // on the assumption the gate would be acquired; undo that if the wait itself failed
            // (cancellation) so a later, unrelated AcquireAsync in the same logical chain doesn't
            // wrongly believe it is reentrant into a lease that was never actually granted.
            _depth.Value = 0;
            throw;
        }
        return new Lease(this, ownsGate: true);
    }

    private void Release(bool ownsGate)
    {
        if (_depth.Value <= 0) return;
        _depth.Value--;
        if (_depth.Value == 0 && ownsGate) _gate.Release();
    }

    private sealed class Lease : IAsyncDisposable
    {
        private RuntimePerformanceCoordinator? _owner;
        private readonly bool _ownsGate;

        public Lease(RuntimePerformanceCoordinator owner, bool ownsGate)
        {
            _owner = owner;
            _ownsGate = ownsGate;
        }

        public ValueTask DisposeAsync()
        {
            RuntimePerformanceCoordinator? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_ownsGate);
            return ValueTask.CompletedTask;
        }
    }
}
