using System.Threading;

namespace PaRuX.Server.Tests;

/// <summary>Controllable monotonic time for tests that exercise TimeProvider-aware delays.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private long _timestampTicks;
    private TaskCompletionSource _timerChanged = NewSignal();

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _timestampTicks;
    }

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        AddTimer(timer);
        if (!timer.Change(dueTime, period))
            throw new ArgumentOutOfRangeException(nameof(dueTime));
        return timer;
    }

    public void AdvanceBy(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        long target = checked(GetTimestamp() + elapsed.Ticks);
        while (true)
        {
            List<(TimerCallback Callback, object? State)> callbacks;
            lock (_gate)
            {
                long? nextDue = NextDueTimestampLocked();
                if (nextDue is null || nextDue.Value > target)
                {
                    _timestampTicks = target;
                    return;
                }

                _timestampTicks = Math.Max(_timestampTicks, nextDue.Value);
                callbacks = TakeDueCallbacksLocked();
                PulseLocked();
            }
            Invoke(callbacks);
        }
    }

    public void AdvanceToNextTimer()
    {
        List<(TimerCallback Callback, object? State)> callbacks;
        lock (_gate)
        {
            long? nextDue = NextDueTimestampLocked();
            if (nextDue is null) throw new InvalidOperationException("No manual timer is scheduled.");
            _timestampTicks = Math.Max(_timestampTicks, nextDue.Value);
            callbacks = TakeDueCallbacksLocked();
            PulseLocked();
        }
        Invoke(callbacks);
    }

    public async Task AdvanceUntilCompleteAsync(Task completion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(completion);
        while (!completion.IsCompleted)
        {
            List<(TimerCallback Callback, object? State)>? callbacks = null;
            Task changed;
            lock (_gate)
            {
                long? nextDue = NextDueTimestampLocked();
                if (nextDue is null)
                {
                    changed = _timerChanged.Task;
                }
                else
                {
                    _timestampTicks = Math.Max(_timestampTicks, nextDue.Value);
                    callbacks = TakeDueCallbacksLocked();
                    PulseLocked();
                    changed = Task.CompletedTask;
                }
            }

            if (callbacks is null)
            {
                await Task.WhenAny(changed, completion).WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            Invoke(callbacks);
            await Task.Yield();
        }
    }

    private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) return false;
        if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan) return false;

        lock (_gate)
        {
            if (timer.IsDisposed) return false;
            long? dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : checked(_timestampTicks + dueTime.Ticks);
            long? periodTicks = period > TimeSpan.Zero ? period.Ticks : null;
            timer.SetSchedule(dueAt, periodTicks);
            PulseLocked();
            return true;
        }
    }

    private void Dispose(ManualTimer timer)
    {
        lock (_gate)
        {
            timer.MarkDisposed();
            PulseLocked();
        }
    }

    private void AddTimer(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Add(timer);
            PulseLocked();
        }
    }

    private long? NextDueTimestampLocked()
    {
        long? nextDue = null;
        foreach (ManualTimer timer in _timers)
        {
            if (timer.DueAtTicks is long dueAt && (nextDue is null || dueAt < nextDue.Value))
                nextDue = dueAt;
        }
        return nextDue;
    }

    private List<(TimerCallback Callback, object? State)> TakeDueCallbacksLocked()
    {
        var callbacks = new List<(TimerCallback Callback, object? State)>();
        foreach (ManualTimer timer in _timers)
        {
            if (timer.TryTakeDue(_timestampTicks, out TimerCallback? callback, out object? state))
                callbacks.Add((callback, state));
        }
        return callbacks;
    }

    private static void Invoke(IEnumerable<(TimerCallback Callback, object? State)> callbacks)
    {
        foreach ((TimerCallback callback, object? state) in callbacks) callback(state);
    }

    private void PulseLocked()
    {
        TaskCompletionSource previous = _timerChanged;
        _timerChanged = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private long? _dueAtTicks;
        private long? _periodTicks;
        private bool _disposed;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public long? DueAtTicks => _disposed ? null : _dueAtTicks;
        public bool IsDisposed => _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => _owner.Change(this, dueTime, period);

        public void Dispose() => _owner.Dispose(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void SetSchedule(long? dueAtTicks, long? periodTicks)
        {
            _dueAtTicks = dueAtTicks;
            _periodTicks = periodTicks;
        }

        public void MarkDisposed()
        {
            _disposed = true;
            _dueAtTicks = null;
        }

        public bool TryTakeDue(long nowTicks, out TimerCallback? callback, out object? state)
        {
            if (_disposed || _dueAtTicks is not long dueAt || dueAt > nowTicks)
            {
                callback = null;
                state = null;
                return false;
            }

            callback = _callback;
            state = _state;
            _dueAtTicks = _periodTicks is long periodTicks
                ? checked(dueAt + periodTicks)
                : null;
            return true;
        }
    }
}
