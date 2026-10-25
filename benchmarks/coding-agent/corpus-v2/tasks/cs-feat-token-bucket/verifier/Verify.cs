static class T
{
    static int failed;
    public static void Run(string name, Func<bool> f)
    {
        try { if (!f()) { failed++; Console.WriteLine("FAIL: " + name); } }
        catch (Exception e) { failed++; Console.WriteLine("FAIL: " + name + " :: " + e.GetType().Name + " " + e.Message); }
    }
    public static int Done() { Console.WriteLine(failed == 0 ? "ok" : failed + " failed"); return failed == 0 ? 0 : 1; }
}
public static class Program
{
    public static int Main()
    {
        var now = new DateTime(2024, 1, 1);
        var b = new TokenBucket(5, 2.0, () => now);
        T.Run("starts full", () => b.Available == 5);
        T.Run("consume", () => b.TryAcquire(3) && Math.Abs(b.Available - 2) < 1e-9);
        T.Run("insufficient consumes nothing", () => !b.TryAcquire(3) && Math.Abs(b.Available - 2) < 1e-9);
        T.Run("refill fractional", () => { now = now.AddMilliseconds(250); return Math.Abs(b.Available - 2.5) < 1e-9; });
        T.Run("refill enough", () => { now = now.AddSeconds(1); return b.TryAcquire(4) && Math.Abs(b.Available - 0.5) < 1e-9; });
        T.Run("capped at capacity", () => { now = now.AddHours(1); return Math.Abs(b.Available - 5) < 1e-9; });
        T.Run("default one token", () => { var c = new TokenBucket(1, 1, () => now); return c.TryAcquire() && !c.TryAcquire(); });
        T.Run("bad tokens", () =>
        {
            try { b.TryAcquire(0); return false; } catch (ArgumentOutOfRangeException) { }
            try { b.TryAcquire(6); return false; } catch (ArgumentOutOfRangeException) { }
            return true;
        });
        T.Run("clock going backwards does not add tokens", () =>
        {
            var t0 = new DateTime(2024, 1, 1); var c = new TokenBucket(3, 1, () => t0);
            c.TryAcquire(3); t0 = t0.AddSeconds(-10);
            return c.Available == 0;
        });
        return T.Done();
    }
}
