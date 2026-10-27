using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services;

/// <summary>Result of windowing a long list: what to render now and what stays collapsed.</summary>
public sealed record DiscoveredWindowResult<T>(
    IReadOnlyList<T> Visible, int HiddenCount, int TotalCount, int Limit, bool CanShowMore, bool CanShowFewer, int NextRevealCount)
{
    public string ShowMoreLabel => "Show more (" + HiddenCount + ")";
    public string ShowFewerLabel => "Show fewer";
}

/// <summary>
/// Pure windowing for the (potentially ~700 entry) discovered-model lists. Pinned items (installed models) are always
/// visible and do not count against the limit; the remaining items keep their given order (best proposed first) and only
/// the first <c>limit</c> of them are returned, so off-screen cards are never built.
/// </summary>
public static class DiscoveredListWindow
{
    public const int DefaultLimit = 6;
    public const int ExpandStep = 12;

    public static DiscoveredWindowResult<T> Compute<T>(IReadOnlyList<T> items, Func<T, bool> isPinned, int limit)
    {
        int cap = Math.Max(0, limit);
        int shown = 0;
        var visible = new List<T>();
        foreach (T item in items)
        {
            if (isPinned(item)) { visible.Add(item); continue; }
            if (shown < cap) { visible.Add(item); shown++; }
        }
        int unpinnedTotal = items.Count(i => !isPinned(i));
        int hidden = unpinnedTotal - shown;
        return new DiscoveredWindowResult<T>(visible, hidden, items.Count, cap, hidden > 0,
            cap > DefaultLimit && unpinnedTotal > DefaultLimit, Math.Min(ExpandStep, hidden));
    }

    public static int Expand(int limit) => Math.Max(DefaultLimit, limit) + ExpandStep;
    public static int Collapse(int limit) => DefaultLimit;
}
