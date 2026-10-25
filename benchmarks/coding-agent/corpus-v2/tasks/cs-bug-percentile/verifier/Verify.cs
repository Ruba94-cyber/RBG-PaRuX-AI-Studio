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
        T.Run("median odd", () => Stats.Percentile(new List<double> { 5, 1, 3 }, 50) == 3);
        T.Run("median even interpolates", () => Math.Abs(Stats.Percentile(new List<double> { 4, 1, 3, 2 }, 50) - 2.5) < 1e-9);
        T.Run("p0", () => Stats.Percentile(new List<double> { 9, 2, 7 }, 0) == 2);
        T.Run("p100", () => Stats.Percentile(new List<double> { 9, 2, 7 }, 100) == 9);
        T.Run("p25", () => Math.Abs(Stats.Percentile(new List<double> { 10, 20, 30, 40, 50 }, 25) - 20) < 1e-9);
        T.Run("p90 interpolated", () => Math.Abs(Stats.Percentile(new List<double> { 1, 2, 3, 4 }, 90) - 3.7) < 1e-9);
        T.Run("single", () => Stats.Percentile(new List<double> { 7 }, 80) == 7);
        T.Run("no reorder", () => { var l = new List<double> { 3, 1, 2 }; Stats.Percentile(l, 50); return l[0] == 3 && l[1] == 1 && l[2] == 2; });
        T.Run("empty throws", () => { try { Stats.Percentile(new List<double>(), 50); return false; } catch (ArgumentException e) when (e is not ArgumentOutOfRangeException) { return true; } });
        T.Run("range throws", () => { try { Stats.Percentile(new List<double> { 1 }, 101); return false; } catch (ArgumentOutOfRangeException) { return true; } });
        T.Run("negative throws", () => { try { Stats.Percentile(new List<double> { 1 }, -1); return false; } catch (ArgumentOutOfRangeException) { return true; } });
        return T.Done();
    }
}
