using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>Per-finding state. Maps to the VS Code extension states: open=open, fixed=resolved, rejected=dismissed.</summary>
    public enum CodingFindingState { Open, Accepted, Rejected, Fixed }

    public enum CodingFindingAnchorOutcome { Kept, Relocated, RelocatedFuzzy, FixedOrOutdated, AlreadyFixed }

    public sealed class CodingFindingAuditEntry
    {
        public DateTimeOffset AtUtc { get; set; }
        public string Event { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }

    /// <summary>A review finding bound to a file, a 1-based inclusive line range and a content-hash anchor.</summary>
    public sealed class CodingReviewFinding
    {
        public string Id { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string Severity { get; set; } = "medium";
        public string Message { get; set; } = string.Empty;
        public CodingFindingState State { get; set; } = CodingFindingState.Open;
        public string ContentHash { get; set; } = string.Empty;
        public string? BeforeHash { get; set; }
        public string? AfterHash { get; set; }
        public string Snippet { get; set; } = string.Empty;
        public string? OutdatedReason { get; set; }
        public List<CodingFindingAuditEntry> Audit { get; set; } = new();
    }

    public sealed record CodingFindingReconciliation(string FindingId, CodingFindingAnchorOutcome Outcome, int StartLine, int EndLine);

    /// <summary>
    /// Persistable ledger of review findings for one coding task. After the worker updates the patch,
    /// <see cref="Reconcile"/> re-anchors every finding by content hash and fuzzy location. Findings are
    /// never dropped: a vanished anchor becomes Fixed ("fixed or outdated") with an audit trail.
    /// </summary>
    public sealed class CodingReviewFindingLedger
    {
        public const double FuzzyThreshold = 0.6;
        private const int MaxSnippetChars = 2000;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

        public string TaskId { get; set; } = string.Empty;
        public List<CodingReviewFinding> Findings { get; set; } = new();

        public CodingReviewFinding Add(string file, int startLine, int endLine, string severity, string message,
            string fileText, DateTimeOffset nowUtc)
        {
            string normalizedFile = NormalizeFile(file);
            string[] lines = SplitLines(fileText);
            if (startLine < 1 || endLine < startLine || endLine > lines.Length)
                throw new ArgumentOutOfRangeException(nameof(startLine), "Finding range is outside the file.");
            var finding = new CodingReviewFinding
            {
                File = normalizedFile, StartLine = startLine, EndLine = endLine,
                Severity = severity, Message = message
            };
            Anchor(finding, lines, startLine - 1, endLine - startLine + 1);
            finding.Id = Hash(string.Join("|", normalizedFile, finding.ContentHash, finding.BeforeHash ?? "-",
                finding.AfterHash ?? "-", severity.ToLowerInvariant(), message.Trim())).Substring(0, 16);
            CodingReviewFinding? existing = Findings.FirstOrDefault(f => f.Id == finding.Id);
            if (existing != null) return existing;
            finding.Audit.Add(new CodingFindingAuditEntry { AtUtc = nowUtc, Event = "created", Detail = $"{normalizedFile}:{startLine}-{endLine}" });
            Findings.Add(finding);
            return finding;
        }

        public bool SetState(string id, CodingFindingState state, DateTimeOffset nowUtc)
        {
            CodingReviewFinding? f = Findings.FirstOrDefault(x => x.Id == id);
            if (f == null || f.State == state) return false;
            f.Audit.Add(new CodingFindingAuditEntry { AtUtc = nowUtc, Event = "state", Detail = f.State + "->" + state });
            f.State = state;
            return true;
        }

        /// <param name="currentFiles">repo-relative path -> current text; a missing key or null means the file is gone.</param>
        public IReadOnlyList<CodingFindingReconciliation> Reconcile(
            IReadOnlyDictionary<string, string?> currentFiles, DateTimeOffset nowUtc)
        {
            var results = new List<CodingFindingReconciliation>();
            foreach (CodingReviewFinding f in Findings)
            {
                if (f.State == CodingFindingState.Fixed)
                {
                    results.Add(new(f.Id, CodingFindingAnchorOutcome.AlreadyFixed, f.StartLine, f.EndLine));
                    continue;
                }
                currentFiles.TryGetValue(f.File, out string? text);
                if (text == null) { MarkFixed(f, nowUtc, "file removed"); results.Add(Result(f, CodingFindingAnchorOutcome.FixedOrOutdated)); continue; }

                string[] lines = SplitLines(text);
                int count = f.EndLine - f.StartLine + 1;
                int original = f.StartLine - 1;

                if (original + count <= lines.Length && WindowHash(lines, original, count) == f.ContentHash)
                {
                    Anchor(f, lines, original, count); // refresh neighbour hashes only
                    results.Add(Result(f, CodingFindingAnchorOutcome.Kept));
                    continue;
                }

                int exact = FindExact(lines, f, count, original);
                if (exact >= 0)
                {
                    Relocate(f, lines, exact, count, nowUtc, "exact");
                    results.Add(Result(f, CodingFindingAnchorOutcome.Relocated));
                    continue;
                }

                int fuzzy = FindFuzzy(lines, f, count, original);
                if (fuzzy >= 0)
                {
                    Relocate(f, lines, fuzzy, count, nowUtc, "fuzzy");
                    results.Add(Result(f, CodingFindingAnchorOutcome.RelocatedFuzzy));
                    continue;
                }

                MarkFixed(f, nowUtc, "anchored lines no longer present");
                results.Add(Result(f, CodingFindingAnchorOutcome.FixedOrOutdated));
            }
            return results;
        }

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        public static CodingReviewFindingLedger FromJson(string json) =>
            JsonSerializer.Deserialize<CodingReviewFindingLedger>(json, Json) ?? new CodingReviewFindingLedger();

        /// <summary>Atomic persistence next to the coding task state (caller supplies a task-owned directory).</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temp = path + ".tmp";
            System.IO.File.WriteAllText(temp, ToJson(), new UTF8Encoding(false));
            System.IO.File.Move(temp, path, overwrite: true);
        }

        public static CodingReviewFindingLedger Load(string path)
        {
            if (!System.IO.File.Exists(path)) return new CodingReviewFindingLedger();
            try { return FromJson(System.IO.File.ReadAllText(path)); }
            catch (JsonException) { return new CodingReviewFindingLedger(); }
        }

        private static CodingFindingReconciliation Result(CodingReviewFinding f, CodingFindingAnchorOutcome o) =>
            new(f.Id, o, f.StartLine, f.EndLine);

        private static void MarkFixed(CodingReviewFinding f, DateTimeOffset now, string reason)
        {
            f.Audit.Add(new CodingFindingAuditEntry { AtUtc = now, Event = "fixed-or-outdated", Detail = $"{reason}; was {f.File}:{f.StartLine}-{f.EndLine}; state {f.State}" });
            f.State = CodingFindingState.Fixed;
            f.OutdatedReason = "fixed or outdated: " + reason;
        }

        private static void Relocate(CodingReviewFinding f, string[] lines, int newStart, int count, DateTimeOffset now, string how)
        {
            string detail = $"{how}: {f.StartLine}-{f.EndLine} -> {newStart + 1}-{newStart + count}";
            f.StartLine = newStart + 1;
            f.EndLine = newStart + count;
            Anchor(f, lines, newStart, count);
            f.Audit.Add(new CodingFindingAuditEntry { AtUtc = now, Event = "relocated", Detail = detail });
        }

        private static int FindExact(string[] lines, CodingReviewFinding f, int count, int original)
        {
            int best = -1, bestScore = -1, bestDist = int.MaxValue;
            for (int s = 0; s + count <= lines.Length; s++)
            {
                if (WindowHash(lines, s, count) != f.ContentHash) continue;
                int score = (f.BeforeHash != null && s > 0 && Hash(Norm(lines[s - 1])) == f.BeforeHash ? 1 : 0)
                          + (f.AfterHash != null && s + count < lines.Length && Hash(Norm(lines[s + count])) == f.AfterHash ? 1 : 0);
                int dist = Math.Abs(s - original);
                if (score > bestScore || (score == bestScore && dist < bestDist)) { best = s; bestScore = score; bestDist = dist; }
            }
            return best;
        }

        private static int FindFuzzy(string[] lines, CodingReviewFinding f, int count, int original)
        {
            string[] anchor = SplitLines(f.Snippet).Select(Norm).Where(l => l.Length > 0).ToArray();
            if (anchor.Length == 0) return -1;
            int best = -1; double bestSim = 0; int bestDist = int.MaxValue;
            for (int s = 0; s + count <= lines.Length; s++)
            {
                string[] window = lines.Skip(s).Take(count).Select(Norm).Where(l => l.Length > 0).ToArray();
                double sim = Dice(anchor, window);
                int dist = Math.Abs(s - original);
                if (sim > bestSim + 1e-9 || (Math.Abs(sim - bestSim) <= 1e-9 && sim > 0 && dist < bestDist))
                { best = s; bestSim = sim; bestDist = dist; }
            }
            return bestSim >= FuzzyThreshold ? best : -1;
        }

        private static double Dice(string[] a, string[] b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string x in a) counts[x] = counts.GetValueOrDefault(x) + 1;
            int common = 0;
            foreach (string y in b)
                if (counts.TryGetValue(y, out int c) && c > 0) { counts[y] = c - 1; common++; }
            return 2.0 * common / (a.Length + b.Length);
        }

        private static void Anchor(CodingReviewFinding f, string[] lines, int start, int count)
        {
            f.ContentHash = WindowHash(lines, start, count);
            f.BeforeHash = start > 0 ? Hash(Norm(lines[start - 1])) : null;
            f.AfterHash = start + count < lines.Length ? Hash(Norm(lines[start + count])) : null;
            string snippet = string.Join("\n", lines.Skip(start).Take(count));
            f.Snippet = snippet.Length > MaxSnippetChars ? snippet.Substring(0, MaxSnippetChars) : snippet;
        }

        private static string WindowHash(string[] lines, int start, int count) =>
            Hash(string.Join("\n", lines.Skip(start).Take(count).Select(Norm)));

        private static string Norm(string line) => line.TrimEnd();

        private static string[] SplitLines(string? text) =>
            (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private static string NormalizeFile(string file) => file.Replace('\\', '/').Trim();

        private static string Hash(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
