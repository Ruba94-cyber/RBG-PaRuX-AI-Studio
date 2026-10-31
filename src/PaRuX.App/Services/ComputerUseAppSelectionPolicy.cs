using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure, deterministic app-name resolution for explicitly approved Computer
    /// Use launch targets. An agent never supplies an executable, path or command
    /// line. Exact names/aliases only; ambiguous names fail closed.
    /// </summary>
    internal static class ComputerUseAppSelectionPolicy
    {
        internal sealed record Candidate(string Name, IReadOnlyList<string> Aliases);

        internal const int NotFound = -1;
        internal const int Ambiguous = -2;
        internal const int MaxNameLength = 80;

        internal static bool IsValidName(string? name) =>
            !string.IsNullOrWhiteSpace(name)
            && name.Length <= MaxNameLength
            && name == name.Trim()
            && name.All(ch => !char.IsControl(ch));

        internal static int FindUniqueIndex(IReadOnlyList<Candidate> candidates, string? exactName)
        {
            if (!IsValidName(exactName) || candidates == null) return NotFound;
            int selected = NotFound;
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                if (!string.Equals(c.Name, exactName, StringComparison.OrdinalIgnoreCase)
                    && !(c.Aliases?.Any(alias =>
                        string.Equals(alias?.Trim(), exactName, StringComparison.OrdinalIgnoreCase)) ?? false))
                    continue;

                if (selected != NotFound) return Ambiguous;
                selected = i;
            }
            return selected;
        }
    }
}
