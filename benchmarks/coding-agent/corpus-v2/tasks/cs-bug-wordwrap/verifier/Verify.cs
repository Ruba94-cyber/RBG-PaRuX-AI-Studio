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
        T.Run("simple", () => TextTools.Wrap("the quick brown fox", 9) == "the quick\nbrown fox");
        T.Run("exact fit", () => TextTools.Wrap("abc def", 7) == "abc def");
        T.Run("one over", () => TextTools.Wrap("abc def", 6) == "abc\ndef");
        T.Run("long word alone", () => TextTools.Wrap("hi extraordinarily ok", 5) == "hi\nextraordinarily\nok");
        T.Run("multi space", () => TextTools.Wrap("a   b \t c", 10) == "a b c");
        T.Run("no trailing", () => !TextTools.Wrap("aa bb cc", 5).EndsWith(" ") && !TextTools.Wrap("aa bb cc", 5).EndsWith("\n"));
        T.Run("empty", () => TextTools.Wrap("   ", 5) == "");
        T.Run("width guard", () => { try { TextTools.Wrap("a", 0); return false; } catch (ArgumentOutOfRangeException) { return true; } });
        return T.Done();
    }
}
