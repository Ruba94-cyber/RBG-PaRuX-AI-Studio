using System;
using System.Collections.Generic;
using System.Linq;

public class Order
{
    private readonly List<(decimal Price, int Qty)> _lines = new List<(decimal, int)>();
    private readonly string _country;

    public Order(string countryCode) { _country = countryCode; }

    public void AddLine(decimal price, int qty) { _lines.Add((price, qty)); }

    public decimal Total()
    {
        decimal net = _lines.Sum(l => l.Price * l.Qty);
        decimal rate;
        switch (_country)
        {
            case "DE": rate = 0.19m; break;
            case "FR": rate = 0.20m; break;
            case "IT": rate = 0.22m; break;
            case "US": rate = 0m; break;
            default: throw new ArgumentException("unknown country: " + _country);
        }
        return net + Math.Round(net * rate, 2, MidpointRounding.AwayFromZero);
    }
}
