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
        T.Run("get any case", () => { var h = new HeaderBag(); h.Set("Content-Type", "a"); return h.Get("content-type") == "a" && h.Get("CONTENT-TYPE") == "a"; });
        T.Run("set replaces", () => { var h = new HeaderBag(); h.Set("X-A", "1"); h.Set("x-a", "2"); return h.Get("X-A") == "2" && h.Names.Count() == 1; });
        T.Run("name casing is latest", () => { var h = new HeaderBag(); h.Set("X-A", "1"); h.Set("x-a", "2"); return h.Names.Single() == "x-a"; });
        T.Run("remove any case", () => { var h = new HeaderBag(); h.Set("Accept", "x"); return h.Remove("ACCEPT") && h.Get("accept") == null && !h.Remove("accept"); });
        T.Run("absent null", () => new HeaderBag().Get("nope") == null);
        T.Run("multiple names", () => { var h = new HeaderBag(); h.Set("A", "1"); h.Set("B", "2"); return h.Names.OrderBy(x => x).SequenceEqual(new[] { "A", "B" }); });
        return T.Done();
    }
}
