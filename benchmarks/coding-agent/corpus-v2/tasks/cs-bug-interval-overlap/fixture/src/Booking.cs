using System;
using System.Collections.Generic;
using System.Linq;

public record Slot(DateTime Start, DateTime End);

public static class Booking
{
    public static bool Overlaps(Slot a, Slot b)
    {
        return a.Start <= b.End && b.Start <= a.End && a.Start != b.Start;
    }

    public static List<Slot> FindConflicts(IEnumerable<Slot> existing, Slot candidate)
    {
        return existing.Where(e => Overlaps(e, candidate)).ToList();
    }
}
