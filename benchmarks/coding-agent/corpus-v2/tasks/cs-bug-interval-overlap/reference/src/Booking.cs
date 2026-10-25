using System;
using System.Collections.Generic;
using System.Linq;

public record Slot(DateTime Start, DateTime End);

public static class Booking
{
    public static bool Overlaps(Slot a, Slot b)
    {
        if (a.End <= a.Start) throw new ArgumentException("invalid range", nameof(a));
        if (b.End <= b.Start) throw new ArgumentException("invalid range", nameof(b));
        return a.Start < b.End && b.Start < a.End;
    }

    public static List<Slot> FindConflicts(IEnumerable<Slot> existing, Slot candidate)
    {
        return existing.Where(e => Overlaps(e, candidate)).ToList();
    }
}
