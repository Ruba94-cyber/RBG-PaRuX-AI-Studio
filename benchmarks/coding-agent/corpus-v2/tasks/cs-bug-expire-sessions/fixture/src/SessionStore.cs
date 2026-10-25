using System;
using System.Collections.Generic;

public class SessionStore
{
    private readonly List<(string Id, DateTime LastSeen)> _sessions = new List<(string, DateTime)>();

    public void Touch(string id, DateTime when)
    {
        int i = _sessions.FindIndex(s => s.Id == id);
        if (i >= 0) _sessions[i] = (id, when);
        else _sessions.Add((id, when));
    }

    public bool Contains(string id) => _sessions.Exists(s => s.Id == id);
    public int Count => _sessions.Count;

    public int ExpireOlderThan(DateTime cutoff)
    {
        int removed = 0;
        foreach (var s in _sessions)
        {
            if (s.LastSeen < cutoff)
            {
                _sessions.Remove(s);
                removed++;
            }
        }
        return removed;
    }
}
