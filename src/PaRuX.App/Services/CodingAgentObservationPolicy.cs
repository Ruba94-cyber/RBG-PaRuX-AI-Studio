using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    public static class CodingAgentObservationPolicy
    {
        public static IReadOnlyList<string> Recent(IReadOnlyList<string> observations, int maxEntries, int maxCharacters)
        {
            if (maxEntries <= 0 || maxCharacters <= 0 || observations.Count == 0)
                return Array.Empty<string>();
            var selected = new List<string>();
            int used = 0;
            for (int index = observations.Count - 1; index >= 0 && selected.Count < maxEntries; index--)
            {
                string compact = Compact(observations[index], Math.Min(900, maxCharacters));
                if (used + compact.Length > maxCharacters) break;
                selected.Add(compact);
                used += compact.Length;
            }
            selected.Reverse();
            return selected;
        }

        public static string Compact(string observation, int limit)
        {
            observation ??= string.Empty;
            if (observation.Length <= limit) return observation;
            if (limit < 80) return observation[..Math.Max(0, limit)];
            const string marker = "\n[Middle omitted from prompt; full observation remains in checkpoint/artifact.]\n";
            int available = limit - marker.Length;
            int head = available * 2 / 3;
            return observation[..head] + marker + observation[^ (available - head)..];
        }
    }
}
