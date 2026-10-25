using System;
using System.Collections.Generic;
using System.Linq;

public class HeaderBag
{
    private readonly Dictionary<string, (string Name, string Value)> _values =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

    public void Set(string name, string value) { _values[name] = (name, value); }
    public string Get(string name) => _values.TryGetValue(name, out var v) ? v.Value : null;
    public bool Remove(string name) => _values.Remove(name);
    public IEnumerable<string> Names => _values.Values.Select(v => v.Name);
}
