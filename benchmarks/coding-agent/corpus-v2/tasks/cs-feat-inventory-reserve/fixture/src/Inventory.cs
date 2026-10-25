using System.Collections.Generic;

public class Inventory
{
    private readonly Dictionary<string, int> _stock = new Dictionary<string, int>();

    public void Add(string sku, int qty)
    {
        _stock[sku] = (_stock.TryGetValue(sku, out var cur) ? cur : 0) + qty;
    }

    public int Stock(string sku) => _stock.TryGetValue(sku, out var cur) ? cur : 0;
}
