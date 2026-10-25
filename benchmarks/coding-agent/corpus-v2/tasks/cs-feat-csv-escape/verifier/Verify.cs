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
        T.Run("plain", () => CsvWriter.Row(new[] { "a", "b" }) == "a,b");
        T.Run("comma", () => CsvWriter.Row(new[] { "a,b", "c" }) == "\"a,b\",c");
        T.Run("quote", () => CsvWriter.Row(new[] { "say \"hi\"" }) == "\"say \"\"hi\"\"\"");
        T.Run("newline", () => CsvWriter.Row(new[] { "l1\nl2", "x" }) == "\"l1\nl2\",x");
        T.Run("cr", () => CsvWriter.Row(new[] { "a\rb" }) == "\"a\rb\"");
        T.Run("null empty", () => CsvWriter.Row(new string[] { "a", null, "c" }) == "a,,c");
        T.Run("empty row", () => CsvWriter.Row(new string[0]) == "");
        T.Run("table", () => CsvWriter.Table(new[] { new[] { "h1", "h,2" }, new[] { "1", "2" } }) == "h1,\"h,2\"\r\n1,2");
        T.Run("table empty", () => CsvWriter.Table(new string[0][]) == "");
        T.Run("leading space untouched", () => CsvWriter.Row(new[] { " a " }) == " a ");
        return T.Done();
    }
}
