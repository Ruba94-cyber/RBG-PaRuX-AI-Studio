using System;
using System.Collections.Generic;
using System.Linq;

public class Inventory
{
    private readonly Dictionary<string, int> _stock = new Dictionary<string, int>();
    private readonly Dictionary<int, (string Sku, int Qty)> _reservations = new Dictionary<int, (string, int)>();
    private int _nextId = 1;

    public void Add(string sku, int qty)
    {
        _stock[sku] = (_stock.TryGetValue(sku, out var cur) ? cur : 0) + qty;
    }

    public int Stock(string sku) => _stock.TryGetValue(sku, out var cur) ? cur : 0;

    public int Available(string sku) =>
        Stock(sku) - _reservations.Values.Where(r => r.Sku == sku).Sum(r => r.Qty);

    public int Reserve(string sku, int qty)
    {
        if (qty < 1) throw new ArgumentOutOfRangeException(nameof(qty));
        if (qty > Available(sku)) throw new InvalidOperationException("insufficient stock");
        int id = _nextId++;
        _reservations[id] = (sku, qty);
        return id;
    }

    public bool Release(int reservationId) => _reservations.Remove(reservationId);

    public bool Commit(int reservationId)
    {
        if (!_reservations.TryGetValue(reservationId, out var r)) return false;
        _reservations.Remove(reservationId);
        _stock[r.Sku] = Stock(r.Sku) - r.Qty;
        return true;
    }
}
