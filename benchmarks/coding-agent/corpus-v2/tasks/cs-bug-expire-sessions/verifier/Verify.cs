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
        var d0 = new DateTime(2024, 1, 1);
        T.Run("removes old only", () =>
        {
            var s = new SessionStore();
            s.Touch("a", d0); s.Touch("b", d0.AddHours(2)); s.Touch("c", d0.AddHours(1)); s.Touch("d", d0.AddHours(3));
            int n = s.ExpireOlderThan(d0.AddHours(2));
            return n == 2 && !s.Contains("a") && !s.Contains("c") && s.Contains("b") && s.Contains("d") && s.Count == 2;
        });
        T.Run("strictly before", () => { var s = new SessionStore(); s.Touch("x", d0); return s.ExpireOlderThan(d0) == 0 && s.Contains("x"); });
        T.Run("all expire", () => { var s = new SessionStore(); for (int i = 0; i < 50; i++) s.Touch("s" + i, d0); return s.ExpireOlderThan(d0.AddDays(1)) == 50 && s.Count == 0; });
        T.Run("empty", () => new SessionStore().ExpireOlderThan(d0) == 0);
        return T.Done();
    }
}
