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
        T.Run("available = stock", () => { var i = new Inventory(); i.Add("a", 10); return i.Available("a") == 10 && i.Available("zzz") == 0; });
        T.Run("reserve reduces available", () => { var i = new Inventory(); i.Add("a", 10); int r = i.Reserve("a", 4); return r == 1 && i.Available("a") == 6 && i.Stock("a") == 10; });
        T.Run("ids increase", () => { var i = new Inventory(); i.Add("a", 10); return i.Reserve("a", 1) == 1 && i.Reserve("a", 1) == 2; });
        T.Run("over-reserve throws", () => { var i = new Inventory(); i.Add("a", 3); i.Reserve("a", 2); try { i.Reserve("a", 2); return false; } catch (InvalidOperationException) { return true; } });
        T.Run("failed reserve changes nothing", () => { var i = new Inventory(); i.Add("a", 3); try { i.Reserve("a", 9); } catch (InvalidOperationException) { } return i.Available("a") == 3 && i.Reserve("a", 1) == 1; });
        T.Run("qty guard", () => { var i = new Inventory(); i.Add("a", 3); try { i.Reserve("a", 0); return false; } catch (ArgumentOutOfRangeException) { return true; } });
        T.Run("release", () => { var i = new Inventory(); i.Add("a", 5); int r = i.Reserve("a", 5); return i.Release(r) && i.Available("a") == 5 && !i.Release(r) && !i.Release(99); });
        T.Run("commit", () => { var i = new Inventory(); i.Add("a", 5); int r = i.Reserve("a", 2); return i.Commit(r) && i.Stock("a") == 3 && i.Available("a") == 3 && !i.Release(r) && !i.Commit(r); });
        T.Run("commit unknown", () => new Inventory().Commit(7) == false);
        T.Run("unknown sku reserve", () => { var i = new Inventory(); try { i.Reserve("nope", 1); return false; } catch (InvalidOperationException) { return true; } });
        return T.Done();
    }
}
