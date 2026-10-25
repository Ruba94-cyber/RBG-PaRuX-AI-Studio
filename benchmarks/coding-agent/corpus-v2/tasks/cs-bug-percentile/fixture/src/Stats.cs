using System.Collections.Generic;

public static class Stats
{
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        int idx = (int)(p / 100.0 * values.Count);
        return values[idx];
    }
}
