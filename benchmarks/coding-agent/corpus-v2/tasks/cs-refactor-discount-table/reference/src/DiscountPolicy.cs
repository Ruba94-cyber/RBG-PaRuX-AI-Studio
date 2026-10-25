using System;
using System.Collections.Generic;

public static class DiscountPolicy
{
    private static readonly Dictionary<string, int> Table = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["bronze"] = 2,
        ["silver"] = 5,
        ["gold"] = 10,
        ["platinum"] = 15,
        ["staff"] = 30,
    };

    public static int Percent(string customerTier)
    {
        if (customerTier == null) return 0;
        return Table.TryGetValue(customerTier, out var percent) ? percent : 0;
    }
}
