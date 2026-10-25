using System;
using System.Collections.Generic;

public class TaxCalculator
{
    private static readonly Dictionary<string, decimal> Rates = new Dictionary<string, decimal>
    {
        ["DE"] = 0.19m,
        ["FR"] = 0.20m,
        ["IT"] = 0.22m,
        ["US"] = 0m,
    };

    public decimal Tax(decimal netAmount, string countryCode)
    {
        if (countryCode == null || !Rates.TryGetValue(countryCode, out var rate))
            throw new ArgumentException("unknown country: " + countryCode);
        return Math.Round(netAmount * rate, 2, MidpointRounding.AwayFromZero);
    }
}
