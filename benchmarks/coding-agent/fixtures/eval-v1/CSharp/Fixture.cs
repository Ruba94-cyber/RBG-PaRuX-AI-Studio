using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PaRuX.Benchmarks.EvalV1;

public static class EvalLogic
{
    public static int ClampBudget(int requested, int maximum)
        => requested;

    public static IReadOnlyList<string> MergeUniquePreserveOrder(IEnumerable<string> left, IEnumerable<string> right)
        => left.Concat(right).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    public static bool ShouldRetry(int statusCode, bool cancelled)
        => statusCode >= 400;

    public static IReadOnlyDictionary<string, string> ResolveLatest(IEnumerable<LedgerEvent> events)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in events.OrderBy(e => e.Sequence))
        {
            if (!entry.Tombstone)
                result.TryAdd(entry.Key, entry.Value);
        }
        return result;
    }

    public static string AssembleChunks(IEnumerable<(int Index, string Text)> chunks)
        => string.Concat(chunks.Select(c => c.Text));

    public static bool IsUnderRoot(string root, string candidate)
        => candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
}

public sealed record LedgerEvent(long Sequence, string Key, string Value, bool Tombstone);
