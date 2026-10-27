using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure parser for GitHub-style Markdown tables. It is separate from the WinUI renderer so the
    /// detection rules are unit-tested; the renderer only turns the result into a grid.
    /// </summary>
    public static class MarkdownTableParser
    {
        public sealed record Table(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<char> Alignments, int LinesConsumed);

        private static readonly Regex SeparatorCell = new(@"^\s*:?-{3,}:?\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Returns a table when <paramref name="lines"/> at <paramref name="start"/> holds a header row
        /// followed by a separator row. Body rows continue while lines contain a pipe.
        /// </summary>
        public static Table? TryParse(IReadOnlyList<string> lines, int start)
        {
            if (start < 0 || start + 1 >= lines.Count) return null;
            if (!lines[start].Contains('|')) return null;
            List<string> header = SplitRow(lines[start]);
            List<string> separator = SplitRow(lines[start + 1]);
            if (header.Count < 2 || separator.Count != header.Count) return null;
            if (!separator.All(cell => SeparatorCell.IsMatch(cell))) return null;

            var alignments = separator.Select(cell =>
            {
                string trimmed = cell.Trim();
                bool left = trimmed.StartsWith(':');
                bool right = trimmed.EndsWith(':');
                return left && right ? 'c' : right ? 'r' : 'l';
            }).ToList();

            var rows = new List<IReadOnlyList<string>>();
            int index = start + 2;
            while (index < lines.Count && lines[index].Contains('|') && lines[index].Trim().Length > 0)
            {
                List<string> cells = SplitRow(lines[index]);
                while (cells.Count < header.Count) cells.Add(string.Empty);
                rows.Add(cells.Take(header.Count).ToList());
                index++;
            }
            return new Table(header, rows, alignments, index - start);
        }

        private static List<string> SplitRow(string line)
        {
            const char Backslash = (char)92;
            string trimmed = line.Trim();
            if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
            if (trimmed.EndsWith('|') && !(trimmed.Length >= 2 && trimmed[^2] == Backslash)) trimmed = trimmed[..^1];
            var cells = new List<string>();
            var current = new StringBuilder();
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == Backslash && i + 1 < trimmed.Length && trimmed[i + 1] == '|') { current.Append('|'); i++; }
                else if (trimmed[i] == '|') { cells.Add(current.ToString().Trim()); current.Clear(); }
                else current.Append(trimmed[i]);
            }
            cells.Add(current.ToString().Trim());
            return cells;
        }
    }
}
