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
        T.Run("tiers", () => DiscountPolicy.Percent("bronze") == 2 && DiscountPolicy.Percent("silver") == 5 && DiscountPolicy.Percent("gold") == 10 && DiscountPolicy.Percent("platinum") == 15 && DiscountPolicy.Percent("staff") == 30);
        T.Run("case-insensitive", () => DiscountPolicy.Percent("GoLD") == 10 && DiscountPolicy.Percent("STAFF") == 30);
        T.Run("unknown", () => DiscountPolicy.Percent("diamond") == 0 && DiscountPolicy.Percent("") == 0);
        T.Run("null", () => DiscountPolicy.Percent(null) == 0);
        T.Run("no else-if", () =>
        {
            var dir = AppContext.BaseDirectory;
            var cur = new DirectoryInfo(dir);
            while (cur != null && !File.Exists(Path.Combine(cur.FullName, "src", "DiscountPolicy.cs"))) cur = cur.Parent;
            if (cur == null) return false;
            var text = File.ReadAllText(Path.Combine(cur.FullName, "src", "DiscountPolicy.cs"));
            return !System.Text.RegularExpressions.Regex.IsMatch(text, "else\\s+if") && text.Contains("Dictionary<string");
        });
        return T.Done();
    }
}
