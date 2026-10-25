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
        T.Run("de total", () => { var o = new Order("DE"); o.AddLine(10.00m, 3); return o.Total() == 35.70m; });
        T.Run("rounding half up", () => { var o = new Order("FR"); o.AddLine(0.25m, 1); return o.Total() == 0.30m; });
        T.Run("us no tax", () => { var o = new Order("US"); o.AddLine(9.99m, 2); return o.Total() == 19.98m; });
        T.Run("unknown throws", () => { var o = new Order("XX"); o.AddLine(1m, 1); try { o.Total(); return false; } catch (ArgumentException) { return true; } });
        T.Run("calculator class", () => { var c = new TaxCalculator(); return c.Tax(100m, "IT") == 22.00m && c.Tax(100m, "US") == 0m && c.Tax(0.05m, "DE") == 0.01m; });
        T.Run("calculator unknown", () => { try { new TaxCalculator().Tax(1m, "ZZ"); return false; } catch (ArgumentException) { return true; } });
        T.Run("order has no inline rates", () =>
        {
            var cur = new DirectoryInfo(AppContext.BaseDirectory);
            while (cur != null && !File.Exists(Path.Combine(cur.FullName, "src", "Order.cs"))) cur = cur.Parent;
            if (cur == null) return false;
            var text = File.ReadAllText(Path.Combine(cur.FullName, "src", "Order.cs"));
            return !text.Contains("0.19") && !text.Contains("0.22") && text.Contains("TaxCalculator");
        });
        return T.Done();
    }
}
