using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    /// <summary>
    /// Select an already-open Windows app without taking the first accidental
    /// substring match. Operates on titles supplied by the Win32 enumeration;
    /// no model-chosen HWND, process ID or executable is trusted.
    /// </summary>
    internal static class ComputerUseWindowSelectionPolicy
    {
        internal const int NotFound = -1;
        internal const int Ambiguous = -2;

        internal static int FindUniqueIndex(IReadOnlyList<string> titles, string? search)
        {
            if (titles == null || string.IsNullOrWhiteSpace(search))
                return NotFound;
            string desired = search.Trim();
            if (desired.Length > 256)
                return NotFound;

            int exact = NotFound;
            int exactCount = 0;
            for (int index = 0; index < titles.Count; index++)
            {
                if (!string.Equals(titles[index], desired, StringComparison.OrdinalIgnoreCase))
                    continue;
                exact = index;
                if (++exactCount > 1) return Ambiguous;
            }
            if (exactCount == 1) return exact;

            // A one- or two-letter fragment could target almost any application.
            // Exact names may be short; partial selections must be meaningful.
            if (desired.Length < 3) return NotFound;

            int result = NotFound;
            for (int index = 0; index < titles.Count; index++)
            {
                if (string.IsNullOrWhiteSpace(titles[index])
                    || titles[index].IndexOf(desired, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (result != NotFound) return Ambiguous;
                result = index;
            }
            return result;
        }
    }
}
