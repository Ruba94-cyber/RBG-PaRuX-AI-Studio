using System;

public class TokenBucket
{
    private readonly int _capacity;
    private readonly double _rate;
    private readonly Func<DateTime> _clock;
    private double _tokens;
    private DateTime _last;

    public TokenBucket(int capacity, double refillPerSecond, Func<DateTime> clock)
    {
        _capacity = capacity;
        _rate = refillPerSecond;
        _clock = clock;
        _tokens = capacity;
        _last = clock();
    }

    private void Refill()
    {
        var now = _clock();
        var elapsed = (now - _last).TotalSeconds;
        if (elapsed > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + elapsed * _rate);
            _last = now;
        }
        else if (elapsed < 0)
        {
            _last = now;
        }
    }

    public double Available
    {
        get { Refill(); return _tokens; }
    }

    public bool TryAcquire(int tokens = 1)
    {
        if (tokens < 1 || tokens > _capacity) throw new ArgumentOutOfRangeException(nameof(tokens));
        Refill();
        if (_tokens + 1e-12 < tokens) return false;
        _tokens -= tokens;
        return true;
    }
}
