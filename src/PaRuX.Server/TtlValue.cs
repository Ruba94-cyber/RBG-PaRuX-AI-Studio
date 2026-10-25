namespace PaRuX.Server;

/// <summary>
/// A value recomputed at most once per <c>ttl</c>. Used to keep per-second background polls from repeating
/// expensive machine probes (for example a full process-table scan) whose answer changes rarely.
/// </summary>
internal sealed class TtlValue<T>
{
    private readonly TimeSpan _ttl;
    private readonly Func<T> _compute;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private T _value = default!;
    private DateTime _computedUtc = DateTime.MinValue;
    private bool _has;

    internal TtlValue(TimeSpan ttl, Func<T> compute, Func<DateTime>? utcNow = null)
    {
        _ttl = ttl;
        _compute = compute;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal T Get()
    {
        lock (_gate)
        {
            DateTime now = _utcNow();
            if (!_has || now - _computedUtc >= _ttl || now < _computedUtc)
            {
                _value = _compute();
                _computedUtc = now;
                _has = true;
            }
            return _value;
        }
    }
}
