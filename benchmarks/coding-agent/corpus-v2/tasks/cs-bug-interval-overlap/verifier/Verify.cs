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
    static Slot R(int s, int e) => new Slot(new DateTime(2024, 1, 1, 0, 0, 0).AddHours(s), new DateTime(2024, 1, 1, 0, 0, 0).AddHours(e));
    public static int Main()
    {
        T.Run("partial overlap", () => Booking.Overlaps(R(1, 5), R(3, 8)));
        T.Run("contained", () => Booking.Overlaps(R(1, 10), R(3, 4)) && Booking.Overlaps(R(3, 4), R(1, 10)));
        T.Run("same start", () => Booking.Overlaps(R(2, 4), R(2, 6)));
        T.Run("identical", () => Booking.Overlaps(R(2, 4), R(2, 4)));
        T.Run("back to back is fine", () => !Booking.Overlaps(R(1, 3), R(3, 5)) && !Booking.Overlaps(R(3, 5), R(1, 3)));
        T.Run("disjoint", () => !Booking.Overlaps(R(1, 2), R(5, 6)));
        T.Run("invalid", () => { try { Booking.Overlaps(R(3, 3), R(1, 2)); return false; } catch (ArgumentException) { return true; } });
        T.Run("conflicts list", () =>
        {
            var ex = new List<Slot> { R(0, 2), R(2, 4), R(3, 6), R(8, 9) };
            var c = Booking.FindConflicts(ex, R(3, 5));
            return c.Count == 2 && c[0] == ex[1] && c[1] == ex[2];
        });
        T.Run("no conflicts", () => Booking.FindConflicts(new List<Slot> { R(0, 1) }, R(1, 2)).Count == 0);
        return T.Done();
    }
}
