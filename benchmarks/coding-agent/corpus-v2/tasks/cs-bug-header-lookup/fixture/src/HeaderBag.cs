using System.Collections.Generic;

public class HeaderBag
{
    private readonly Dictionary<string, string> _values = new Dictionary<string, string>();

    public void Set(string name, string value) { _values[name] = value; }
    public string Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
    public bool Remove(string name) => _values.Remove(name);
    public IEnumerable<string> Names => _values.Keys;
}
