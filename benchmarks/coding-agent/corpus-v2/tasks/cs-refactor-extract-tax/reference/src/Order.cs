using System;
using System.Collections.Generic;
using System.Linq;

public class Order
{
    private readonly List<(decimal Price, int Qty)> _lines = new List<(decimal, int)>();
    private readonly string _country;
    private readonly TaxCalculator _tax = new TaxCalculator();

    public Order(string countryCode) { _country = countryCode; }

    public void AddLine(decimal price, int qty) { _lines.Add((price, qty)); }

    public decimal Total()
    {
        decimal net = _lines.Sum(l => l.Price * l.Qty);
        return net + _tax.Tax(net, _country);
    }
}
