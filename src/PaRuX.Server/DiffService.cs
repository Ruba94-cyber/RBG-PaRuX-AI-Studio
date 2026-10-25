using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Universal Diff (Phase 26): a standalone line-level text diff over the classic LCS (longest
/// common subsequence) dynamic-programming algorithm - no new third-party dependency for
/// something this well-understood. Takes two raw text blobs; deliberately not wired to any one
/// subsystem's storage (Artifacts/Skills/Config can each call it with whatever text they already
/// have), so this stays a single reusable primitive instead of N bespoke diff implementations.
/// </summary>
public sealed class DiffService
{
    // The DP table is (lines+1) x (lines+1) ints - 3000 lines is ~36MB, generous for the config/
    // prompt/skill-manifest/small-document text this targets while staying far short of the
    // multi-GB territory a naive "no limit" would risk on a pasted huge file.
    private const int MaxLines = 3000;

    public DiffResult Compute(string oldText, string newText)
    {
        string[] oldLines = SplitLines(oldText);
        string[] newLines = SplitLines(newText);
        if (oldLines.Length > MaxLines || newLines.Length > MaxLines)
            throw new InvalidDataException($"Text is too large to diff line-by-line (limit {MaxLines} lines per side).");

        int n = oldLines.Length, m = newLines.Length;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = oldLines[i] == newLines[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var lines = new List<DiffLine>();
        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (oldLines[a] == newLines[b])
            {
                lines.Add(new DiffLine { Kind = "unchanged", Text = oldLines[a] });
                a++; b++;
            }
            else if (lcs[a + 1, b] >= lcs[a, b + 1])
            {
                lines.Add(new DiffLine { Kind = "removed", Text = oldLines[a] });
                a++;
            }
            else
            {
                lines.Add(new DiffLine { Kind = "added", Text = newLines[b] });
                b++;
            }
        }
        while (a < n) { lines.Add(new DiffLine { Kind = "removed", Text = oldLines[a] }); a++; }
        while (b < m) { lines.Add(new DiffLine { Kind = "added", Text = newLines[b] }); b++; }

        return new DiffResult
        {
            Lines = lines,
            AddedCount = lines.Count(l => l.Kind == "added"),
            RemovedCount = lines.Count(l => l.Kind == "removed"),
            UnchangedCount = lines.Count(l => l.Kind == "unchanged"),
        };
    }

    private static string[] SplitLines(string? text) =>
        string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
