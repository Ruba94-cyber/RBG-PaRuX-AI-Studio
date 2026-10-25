using System;
using System.Collections.Generic;
using System.Linq;

public static class Stats
{
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) throw new ArgumentException("values is empty", nameof(values));
        if (p < 0 || p > 100) throw new ArgumentOutOfRangeException(nameof(p));
        var sorted = values.OrderBy(v => v).ToArray();
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
