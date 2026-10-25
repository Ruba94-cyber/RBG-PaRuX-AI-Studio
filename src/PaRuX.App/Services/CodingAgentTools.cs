using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// vNext Coding Runtime upgrade (roadmap P1/C1) - the repository-aware tools
    /// <see cref="CodingAgentService"/>'s action loop dispatches to, extracted into their own
    /// dependency-free static class so they can be unit-tested from the plain Server test project.
    /// Search/glob traversal is bounded and refuses reparse-point traversal, so a repository junction
    /// or symlink cannot silently turn a read-only search into access outside the selected workspace.
    /// </summary>
    public static class CodingAgentTools
    {
        private const long MaxFingerprintFileBytes = 32L * 1024 * 1024;
        private const long MaxFingerprintTotalBytes = 64L * 1024 * 1024;
        private static readonly string[] ExcludedDirectoryNames =
        {
            ".git", "bin", "obj", "node_modules", ".parux-agents", ".vs", "packages"
        };

        internal const int MaximumScannedFiles = 4000;
        internal const int MaximumVisitedDirectories = 10_000;
        internal const long MaximumSearchableFileBytes = 4L * 1024 * 1024;
        internal const int MaximumLinesPerFile = 200_000;

        public static string ReadNumberedFileLines(string path, int firstLine, int count) =>
            string.Join("\n", File.ReadLines(path)
                .Skip(Math.Max(0, firstLine - 1))
                .Take(Math.Clamp(count, 1, 100))
                .Select((line, index) => (firstLine + index) + ": " + line));

        public static string ReadTaskRelevantFileLines(string path, string task, int maxWindows = 8)
        {
            string[] lines = File.ReadAllLines(path);
            List<string> terms = Regex.Matches(task ?? string.Empty, @"[A-Za-z_][A-Za-z0-9_]{3,}")
                .Select(match => match.Value)
                .Where(value => value.Any(char.IsUpper) || value.Contains('_'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(value => value.Length)
                .Take(16)
                .ToList();
            if (terms.Count == 0) return string.Empty;

            var centers = lines.Select((line, index) => new
                {
                    Index = index,
                    Score = terms.Count(term => line.Contains(term, StringComparison.OrdinalIgnoreCase))
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Index)
                .Select(item => item.Index)
                .ToList();

            var selected = new SortedSet<int>();
            foreach (int center in centers)
            {
                if (selected.Any(existing => Math.Abs(existing - center) <= 8)) continue;
                for (int index = Math.Max(0, center - 5); index <= Math.Min(lines.Length - 1, center + 8); index++)
                    selected.Add(index);
                if (selected.Count >= maxWindows * 14) break;
            }
            return string.Join("\n", selected.Select(index => (index + 1) + ": " + lines[index]));
        }

        public static (bool Success, string NewContent, string? Error) TryReplaceRange(string content, string oldText, string newText)
        {
            (bool success, string updated, string? error, string? _) = TryReplaceRangeDetailed(content, oldText, newText);
            return (success, updated, error);
        }

        internal static async Task<(bool Success, bool Changed, string? Error, string? Note)> TryApplyReplaceRangeFileAsync(
            string fullPath, string oldText, string newText, CancellationToken cancellationToken)
        {
            string current = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            (bool success, string updated, string? error, string? note) =
                TryReplaceRangeDetailed(current, oldText, newText);
            if (!success) return (false, false, error, null);
            if (string.Equals(current, updated, StringComparison.Ordinal))
                return (true, false, null, note ?? "replacement produces no content change");

            await File.WriteAllTextAsync(fullPath, updated, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return (true, true, null, note);
        }

        /// <summary>
        /// Applies a precise edit, tolerating the differences a language model reliably gets wrong
        /// while still refusing anything ambiguous.
        ///
        /// Measured on the physical PC (2026-09-21): after the context and memory fixes the coding
        /// agent finally reached an edit - "Step 7: replace_range" - and the tool rejected it with
        /// "oldText was not found ... doesn't match exactly (including whitespace)". The model had
        /// the intent right and the literal text slightly wrong, and the error told it nothing it
        /// could act on, so the run ended without a diff.
        ///
        /// Each strategy is tried in order and must still resolve to EXACTLY ONE location; a
        /// strategy that matches in several places is reported as ambiguous rather than guessed.
        /// Nothing here weakens the stale-content check: a match still has to exist in the file as
        /// it is on disk right now.
        /// </summary>
        /// <returns>
        /// Note is non-null when the edit succeeded through something other than an exact match, so
        /// the agent's observation records how the text differed instead of hiding it.
        /// </returns>
        public static (bool Success, string NewContent, string? Error, string? Note) TryReplaceRangeDetailed(
            string content, string oldText, string newText)
        {
            content ??= string.Empty;
            newText ??= string.Empty;
            if (string.IsNullOrEmpty(oldText))
                return (false, content, "oldText must be non-empty - use create_file for genuinely new content, or write_file to replace an entire file.", null);

            // 1. Exact.
            (int index, int length, bool ambiguous) = FindUnique(content, oldText);
            if (ambiguous) return (false, content, AmbiguityError, null);
            if (index >= 0) return (true, Splice(content, index, length, newText), null, null);

            // 2. Line endings only. A model that saw the file as LF writing back into a CRLF file
            //    (or the reverse) is not a disagreement about the code.
            string normalizedContent = NormalizeEndings(content);
            string normalizedOld = NormalizeEndings(oldText);
            if (!string.Equals(normalizedContent, content, StringComparison.Ordinal)
                || !string.Equals(normalizedOld, oldText, StringComparison.Ordinal))
            {
                (index, length, ambiguous) = FindUnique(normalizedContent, normalizedOld);
                if (ambiguous) return (false, content, AmbiguityError, null);
                if (index >= 0)
                {
                    string updated = Splice(normalizedContent, index, length, NormalizeEndings(newText));
                    // Restore the file's dominant line ending so the edit does not rewrite the file.
                    if (content.Contains("\r\n", StringComparison.Ordinal))
                        updated = updated.Replace("\n", "\r\n", StringComparison.Ordinal);
                    return (true, updated, null, "matched after normalizing line endings");
                }
            }

            // 3. Trailing whitespace per line.
            (index, length, ambiguous) = FindByLines(content, oldText, trimStart: false);
            if (ambiguous) return (false, content, AmbiguityError, null);
            if (index >= 0)
                return (true, Splice(content, index, length, newText), null,
                    "matched ignoring trailing whitespace");

            // 4. Indentation. The model reproduced the right lines at the wrong indent level, which
            //    is the single most common near-miss in generated patches.
            (index, length, ambiguous) = FindByLines(content, oldText, trimStart: true);
            if (ambiguous) return (false, content, AmbiguityError, null);
            if (index >= 0)
            {
                string reindented = ReindentToMatch(content, index, oldText, newText);
                return (true, Splice(content, index, length, reindented), null,
                    "matched ignoring indentation; the replacement was re-indented to the file's own level");
            }

            return (false, content, NotFoundError(content, oldText), null);
        }

        private const string AmbiguityError =
            "oldText appears more than once - include more surrounding context so it identifies exactly one location.";

        /// <summary>
        /// A not-found error the model can actually act on: it names the closest region in the file
        /// and quotes it verbatim, so the next attempt can copy real text instead of guessing again.
        /// </summary>
        private static string NotFoundError(string content, string oldText)
        {
            var message = new StringBuilder(
                "oldText was not found. Line endings, trailing whitespace and indentation are already "
                + "tolerated, so the text itself differs from the file.");

            string[] wanted = SplitLines(oldText);
            string anchor = wanted.FirstOrDefault(line => line.Trim().Length >= 8) ?? wanted.FirstOrDefault() ?? string.Empty;
            if (anchor.Trim().Length == 0) return message.ToString();

            string[] lines = SplitLines(content);
            // Score every file line by how many meaningful tokens it shares with the anchor line.
            // A containment test is too brittle: the reason the edit failed is usually that the
            // model's line is *nearly* the file's line, so nothing contains anything exactly.
            string[] wantedTokens = Tokenize(anchor);
            int best = -1;
            int bestScore = 0;
            if (wantedTokens.Length > 0)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] tokens = Tokenize(lines[i]);
                    if (tokens.Length == 0) continue;
                    int score = tokens.Count(token => wantedTokens.Contains(token, StringComparer.Ordinal));
                    if (score > bestScore) { bestScore = score; best = i; }
                }
            }
            // One shared token is coincidence, not a location.
            if (bestScore < 2) best = -1;
            if (best < 0)
            {
                message.Append(" No line of oldText appears in the file at all - re-read the file before editing.");
                return message.ToString();
            }

            int from = Math.Max(0, best - 3);
            int to = Math.Min(lines.Length - 1, best + Math.Max(3, wanted.Length + 2));
            message.Append(" The closest region in the file is lines ").Append(from + 1).Append('-').Append(to + 1)
                .Append(". Copy oldText from exactly this text:\n");
            for (int i = from; i <= to; i++) message.Append(i + 1).Append(": ").Append(lines[i]).Append('\n');
            return message.ToString();
        }

        /// <summary>Identifier-ish tokens, long enough that sharing one is not a coincidence.</summary>
        private static string[] Tokenize(string line) =>
            Regex.Matches(line ?? string.Empty, @"[A-Za-z_][A-Za-z0-9_]{1,}|\d+")
                .Select(match => match.Value)
                .Where(value => value.Length >= 2)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        private static (int Index, int Length, bool Ambiguous) FindUnique(string content, string oldText)
        {
            int first = content.IndexOf(oldText, StringComparison.Ordinal);
            if (first < 0) return (-1, 0, false);
            if (content.IndexOf(oldText, first + 1, StringComparison.Ordinal) >= 0) return (-1, 0, true);
            return (first, oldText.Length, false);
        }

        /// <summary>
        /// Line-wise search that compares lines after trimming, and reports the exact character
        /// span of the matching region in the ORIGINAL content so the splice stays byte-accurate.
        /// </summary>
        private static (int Index, int Length, bool Ambiguous) FindByLines(string content, string oldText, bool trimStart)
        {
            string[] wanted = SplitLines(oldText);
            if (wanted.Length == 0) return (-1, 0, false);
            // A single short line is too weak an anchor to match loosely.
            if (wanted.Length == 1 && wanted[0].Trim().Length < 6) return (-1, 0, false);

            string[] lines = SplitLines(content);
            if (wanted.Length > lines.Length) return (-1, 0, false);

            int found = -1;
            for (int start = 0; start + wanted.Length <= lines.Length; start++)
            {
                bool all = true;
                for (int offset = 0; offset < wanted.Length; offset++)
                {
                    if (!LinesEqual(lines[start + offset], wanted[offset], trimStart)) { all = false; break; }
                }
                if (!all) continue;
                if (found >= 0) return (-1, 0, true);
                found = start;
            }
            if (found < 0) return (-1, 0, false);

            int index = OffsetOfLine(content, found);
            int end = OffsetOfLine(content, found + wanted.Length);
            // Do not swallow the line break that ends the matched region.
            int length = Math.Max(0, end - index);
            while (length > 0 && (content[index + length - 1] == '\n' || content[index + length - 1] == '\r')) length--;
            return (index, length, false);
        }

        private static bool LinesEqual(string actual, string wanted, bool trimStart) =>
            trimStart
                ? string.Equals(actual.Trim(), wanted.Trim(), StringComparison.Ordinal)
                : string.Equals(actual.TrimEnd(), wanted.TrimEnd(), StringComparison.Ordinal);

        /// <summary>
        /// Shifts the replacement by the difference between the indentation the model wrote and the
        /// indentation the file actually uses, so an indent-insensitive match cannot corrupt layout.
        /// </summary>
        private static string ReindentToMatch(string content, int index, string oldText, string newText)
        {
            string fileIndent = IndentOf(SplitLines(content.Substring(index))[0]);
            string modelIndent = IndentOf(SplitLines(oldText)[0]);
            if (string.Equals(fileIndent, modelIndent, StringComparison.Ordinal)) return newText;

            string[] replacement = SplitLines(newText);
            var rebuilt = new StringBuilder();
            for (int i = 0; i < replacement.Length; i++)
            {
                string line = replacement[i];
                if (line.Length > 0)
                {
                    line = line.StartsWith(modelIndent, StringComparison.Ordinal) && modelIndent.Length > 0
                        ? fileIndent + line.Substring(modelIndent.Length)
                        : fileIndent + line.TrimStart();
                }
                rebuilt.Append(line);
                if (i < replacement.Length - 1) rebuilt.Append('\n');
            }
            return rebuilt.ToString();
        }

        private static string IndentOf(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return line.Substring(0, i);
        }

        private static string NormalizeEndings(string value) =>
            value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        private static string[] SplitLines(string value) => NormalizeEndings(value).Split('\n');

        /// <summary>Character offset of a 0-based line index in the original content.</summary>
        private static int OffsetOfLine(string content, int lineIndex)
        {
            if (lineIndex <= 0) return 0;
            int seen = 0;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '\n')
                {
                    seen++;
                    if (seen == lineIndex) return i + 1;
                }
            }
            return content.Length;
        }

        private static string Splice(string content, int index, int length, string replacement) =>
            content.Substring(0, index) + replacement + content.Substring(index + length);

        public sealed class GrepMatch
        {
            public required string RelativePath { get; set; }
            public int LineNumber { get; set; }
            public required string Line { get; set; }
        }

        public static List<GrepMatch> Grep(string root, string pattern, bool useRegex, CancellationToken ct, int maxResults = 200)
        {
            var results = new List<GrepMatch>();
            if (string.IsNullOrEmpty(pattern) || maxResults <= 0) return results;

            Regex? regex = null;
            if (useRegex)
            {
                try { regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)); }
                catch (ArgumentException) { return results; }
            }

            int scanned = 0;
            foreach (string file in EnumerateRepositoryFiles(root))
            {
                ct.ThrowIfCancellationRequested();
                if (results.Count >= maxResults || scanned++ >= MaximumScannedFiles) break;
                if (!IsSearchableTextFile(file)) continue;

                foreach ((int lineNumber, string line) in EnumerateLines(file, ct))
                {
                    if (results.Count >= maxResults) break;
                    bool isMatch = useRegex
                        ? SafeIsMatch(regex!, line)
                        : line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isMatch) continue;

                    results.Add(new GrepMatch
                    {
                        RelativePath = Path.GetRelativePath(root, file).Replace('\\', '/'),
                        LineNumber = lineNumber,
                        Line = line.Length > 300 ? line.Substring(0, 300) + "…" : line
                    });
                }
            }
            return results;
        }

        public static Dictionary<string, List<GrepMatch>> GrepMultiple(
            string root, IReadOnlyList<string> patterns, CancellationToken ct, int maxResultsPerPattern = 200)
        {
            var results = new Dictionary<string, List<GrepMatch>>(StringComparer.OrdinalIgnoreCase);
            foreach (string pattern in patterns.Where(pattern => !string.IsNullOrEmpty(pattern)))
                results[pattern] = new List<GrepMatch>();
            if (results.Count == 0 || maxResultsPerPattern <= 0) return results;

            int scanned = 0;
            foreach (string file in EnumerateRepositoryFiles(root))
            {
                ct.ThrowIfCancellationRequested();
                if (scanned++ >= MaximumScannedFiles) break;
                if (results.Values.All(bucket => bucket.Count >= maxResultsPerPattern)) break;
                if (!IsSearchableTextFile(file)) continue;

                string? relative = null;
                foreach ((int lineNumber, string line) in EnumerateLines(file, ct))
                {
                    foreach ((string pattern, List<GrepMatch> bucket) in results)
                    {
                        if (bucket.Count >= maxResultsPerPattern) continue;
                        if (line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        relative ??= Path.GetRelativePath(root, file).Replace('\\', '/');
                        bucket.Add(new GrepMatch
                        {
                            RelativePath = relative,
                            LineNumber = lineNumber,
                            Line = line.Length > 300 ? line.Substring(0, 300) + "…" : line
                        });
                    }
                }
            }
            return results;
        }

        private static bool SafeIsMatch(Regex regex, string line)
        {
            try { return regex.IsMatch(line); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        public static List<string> Glob(string root, string pattern, CancellationToken ct, int maxResults = 500)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(pattern) || maxResults <= 0) return results;

            Regex regex;
            try { regex = GlobToRegex(pattern); }
            catch (ArgumentException) { return results; }

            int scanned = 0;
            foreach (string file in EnumerateRepositoryFiles(root))
            {
                ct.ThrowIfCancellationRequested();
                if (results.Count >= maxResults || scanned++ >= MaximumScannedFiles) break;
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (SafeIsMatch(regex, relative)) results.Add(relative);
            }
            return results;
        }

        internal static Regex GlobToRegex(string glob)
        {
            var sb = new StringBuilder("^");
            for (int i = 0; i < glob.Length; i++)
            {
                char c = glob[i];
                if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') i++;
                }
                else if (c == '*') sb.Append("[^/]*");
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        }

        private static IEnumerable<string> EnumerateRepositoryFiles(string root)
        {
            string canonicalRoot;
            try { canonicalRoot = Path.GetFullPath(root); }
            catch { yield break; }
            if (!Directory.Exists(canonicalRoot)) yield break;

            var stack = new Stack<string>();
            stack.Push(canonicalRoot);
            int visitedDirectories = 0;
            while (stack.Count > 0 && visitedDirectories++ < MaximumVisitedDirectories)
            {
                string dir = stack.Pop();
                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(dir); }
                catch { continue; }
                foreach (string sub in subdirs)
                {
                    if (ExcludedDirectoryNames.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) continue;
                    if (IsReparsePoint(sub)) continue;
                    stack.Push(sub);
                }

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir); }
                catch { continue; }
                foreach (string file in files)
                {
                    if (IsReparsePoint(file)) continue;
                    yield return file;
                }
            }
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        private static bool IsSearchableTextFile(string file)
        {
            try
            {
                var info = new FileInfo(file);
                return info.Exists && info.Length <= MaximumSearchableFileBytes;
            }
            catch { return false; }
        }

        private static IEnumerable<(int LineNumber, string Line)> EnumerateLines(string file, CancellationToken ct)
        {
            StreamReader? reader = null;
            try
            {
                reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                for (int lineNumber = 1; lineNumber <= MaximumLinesPerFile; lineNumber++)
                {
                    ct.ThrowIfCancellationRequested();
                    string? line = reader.ReadLine();
                    if (line == null) yield break;
                    yield return (lineNumber, line);
                }
            }
            finally { reader?.Dispose(); }
        }

        private static readonly string[] DeniedCommandPatterns =
        {
            "rm -rf /", "rm -rf ~", "rm -rf *", "format ", "mkfs", "dd if=",
            "del /s /q c:\\", "del /f /s /q c:\\", "rmdir /s /q c:\\",
            "shutdown", "diskpart", ":(){ :|:& };:", ":(){:|:&};:"
        };

        public static string? DenyReasonFor(string? command)
        {
            string? reviewed = CodingCommandPreflight.DenyReasonFor(command);
            if (reviewed != null) return reviewed;

            // Keep the original compact denylist as a compatibility guard even though the richer
            // deterministic preflight currently subsumes every entry. This prevents a future
            // preflight refactor from silently weakening long-standing denials.
            if (string.IsNullOrWhiteSpace(command)) return null;
            string normalized = command.Trim();
            foreach (string denied in DeniedCommandPatterns)
            {
                if (normalized.Contains(denied, StringComparison.OrdinalIgnoreCase))
                    return "This command matches a denied pattern (\"" + denied.Trim() + "\") and was not run.";
            }
            return null;
        }

        /// <summary>
        /// Fingerprint of the testable files that currently differ from HEAD (modified,
        /// added or untracked-but-not-ignored): path and content hash. Comparing it before
        /// and after a shell command detects code edits made through run_command (for example a
        /// PowerShell Set-Content), which would otherwise leave verification state untouched.
        /// Returns null when the root is not a Git working tree or Git fails.
        /// </summary>
        public static async Task<string?> TestableChangeFingerprintAsync(
            string root, Func<string, bool> isTestablePath, CancellationToken ct)
        {
            // stdout only: Git's routine CRLF warnings on stderr would otherwise be glued onto the
            // last status record and look like a change between two identical states.
            (int exit, string status) = await RunGitAsync(root, ct, includeStderr: false,
                "status", "--porcelain=v1", "-z", "--untracked-files=all").ConfigureAwait(false);
            if (exit != 0) return null;
            var entries = new List<string>();
            long totalBytes = 0;
            foreach (string record in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                // "XY path". RunGitAsync trims its output, so the first record may have lost the
                // leading space of an unstaged " M" status and arrive as "M path".
                string relative;
                if (record.Length >= 4 && record[2] == ' ') relative = record[3..];
                else if (record.Length >= 3 && record[1] == ' ') relative = record[2..];
                else continue; // rename source paths arrive as bare records after -z renames
                if (!isTestablePath(relative)) continue;
                string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(full);
                string contentHash = "missing";
                if (info.Exists)
                {
                    totalBytes += info.Length;
                    // The caller treats an unavailable fingerprint as changed, which safely
                    // invalidates passing checks instead of spending unbounded time hashing a
                    // generated or unexpectedly large source file.
                    if (info.Length > MaxFingerprintFileBytes || totalBytes > MaxFingerprintTotalBytes) return null;
                    using var stream = new FileStream(full, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[64 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                        hash.AppendData(buffer, 0, read);
                    contentHash = Convert.ToHexString(hash.GetHashAndReset());
                }
                entries.Add(relative + "|" + contentHash);
            }
            entries.Sort(StringComparer.Ordinal);
            return string.Join("\n", entries);
        }

        /// <summary>
        /// Agent worktrees are writable by the AppContainer sandbox. Host-side Git inside one is
        /// only safe while its ".git" is still the gitfile Git created, pointing at metadata
        /// OUTSIDE the worktree (which the sandbox can only read). A sandboxed command could
        /// otherwise replace ".git" with its own directory whose config (core.fsmonitor, filter
        /// drivers) the next host git call would execute outside the sandbox.
        /// Directories that are not agent worktrees are not sandbox-writable and are trusted.
        /// </summary>
        public static bool IsTrustedGitWorkingDirectory(string workingDirectory, out string reason) =>
            PaRuX.Contracts.AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(workingDirectory, out reason);

        private static readonly Regex NuGetMigrationMarker =
            new(@"(^|/)NuGet/Migrations/\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// NuGet records one-time migrations under LocalApplicationData\NuGet\Migrations. Inside
        /// the AppContainer that known folder resolves to an empty path, so sandboxed dotnet writes
        /// an empty marker file "NuGet/Migrations/&lt;n&gt;" relative to its working directory and the
        /// task diff (and every apply) carried it. Removes only such untracked, zero-byte markers
        /// and the directories they leave empty.
        /// </summary>
        public static async Task<int> RemoveNuGetMigrationMarkersAsync(string root, CancellationToken ct)
        {
            (int exit, string status) = await RunGitAsync(root, ct, includeStderr: false,
                "status", "--porcelain=v1", "-z", "--untracked-files=all").ConfigureAwait(false);
            if (exit != 0) return 0;
            int removed = 0;
            foreach (string record in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                string relative;
                if (record.StartsWith("?? ", StringComparison.Ordinal)) relative = record[3..];
                else if (record.StartsWith("? ", StringComparison.Ordinal)) relative = record[2..]; // first record trimmed
                else continue;
                if (!NuGetMigrationMarker.IsMatch(relative)) continue;
                string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(full);
                if (!info.Exists || info.Length != 0 || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    info.Delete();
                    removed++;
                    DirectoryInfo? migrations = info.Directory;
                    if (migrations != null && !migrations.EnumerateFileSystemInfos().Any())
                    {
                        DirectoryInfo? nuget = migrations.Parent;
                        migrations.Delete();
                        if (nuget != null && !nuget.EnumerateFileSystemInfos().Any()) nuget.Delete();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return removed;
        }

        /// <summary>
        /// Changed paths (modified, added, untracked-but-not-ignored) and the tracked file set of
        /// a worktree, for scope-aware verification. Null when Git is unavailable or untrusted.
        /// </summary>
        /// <summary>
        /// The task's working-tree change as a patch, including NEW files. Live parity run: a task
        /// whose deliverable was a new result.json was rejected twice because `git diff HEAD` omits
        /// untracked files, so the independent verifier saw "(no working-tree changes)". Read-only:
        /// the index is not touched (no intent-to-add); new files are rendered with
        /// `git diff --no-index /dev/null &lt;file&gt;` within file-count and byte budgets.
        /// </summary>
        public static async Task<string> WorkingTreeDiffWithUntrackedAsync(
            string root, CancellationToken ct, int maxUntrackedFiles = 200, long maxUntrackedBytes = 2_000_000)
        {
            string fullRoot = Path.GetFullPath(root);
            // This Git process runs on the host; attributes must not select a textconv helper.
            (int diffExit, string diff) = await RunGitAsync(fullRoot, ct, includeStderr: false,
                "diff", "--binary", "--no-ext-diff", "--no-textconv", "HEAD", "--").ConfigureAwait(false);
            var sb = new StringBuilder(diffExit == 0 ? diff : string.Empty);
            (int listExit, string listing) = await RunGitAsync(fullRoot, ct, includeStderr: false,
                "ls-files", "--others", "--exclude-standard", "-z").ConfigureAwait(false);
            if (listExit != 0) return sb.ToString();

            string[] untracked = listing.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            long bytes = 0;
            int shown = 0;
            string rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string relative in untracked)
            {
                ct.ThrowIfCancellationRequested();
                if (shown >= maxUntrackedFiles)
                {
                    sb.Append("\n… (").Append(untracked.Length - shown).Append(" more new file(s) not shown)\n");
                    break;
                }
                string full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
                // Never follow a link out of the worktree into the verifier prompt.
                if (IsUnderReparsePoint(full, fullRoot))
                {
                    sb.Append("\n(new file ").Append(relative).Append(" omitted: link or junction)\n");
                    continue;
                }
                long length = new FileInfo(full).Length;
                if (bytes + length > maxUntrackedBytes)
                {
                    sb.Append("\n(new file ").Append(relative).Append(" omitted: ").Append(length).Append(" bytes exceeds the diff budget)\n");
                    continue;
                }
                bytes += length;
                // --no-index exits 1 when the files differ, which is always the case here.
                (int newExit, string newFileDiff) = await RunGitAsync(fullRoot, ct, includeStderr: false,
                    "diff", "--no-index", "--binary", "--no-ext-diff", "--no-textconv", "--", "/dev/null", relative).ConfigureAwait(false);
                if (newExit is 0 or 1) sb.Append(newFileDiff);
                shown++;
            }
            return sb.ToString();
        }

        private static bool IsUnderReparsePoint(string fullPath, string root)
        {
            string trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar);
            try
            {
                for (string? current = fullPath; current != null && current.Length > trimmedRoot.Length; current = Path.GetDirectoryName(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                }
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        public static async Task<(List<string> Changed, HashSet<string> Tracked)?> ChangeScopeAsync(string root, CancellationToken ct)
        {
            (int statusExit, string status) = await RunGitAsync(root, ct, includeStderr: false,
                "status", "--porcelain=v1", "-z", "--untracked-files=all").ConfigureAwait(false);
            if (statusExit != 0) return null;
            var changed = new List<string>();
            foreach (string record in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (record.Length >= 4 && record[2] == ' ') changed.Add(record[3..]);
                else if (record.Length >= 3 && record[1] == ' ') changed.Add(record[2..]);
            }
            (int lsExit, string listing) = await RunGitAsync(root, ct, includeStderr: false, "ls-files", "-z").ConfigureAwait(false);
            if (lsExit != 0) return null;
            var tracked = new HashSet<string>(listing.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            return (changed, tracked);
        }

        /// <summary>
        /// The one host-side Git diff used for agent evidence. Repository attributes must not select a
        /// textconv helper or an external diff program, so both are disabled for every agent diff.
        /// </summary>
        public static Task<(int ExitCode, string Output)> RunGitDiffAsync(
            string workingDirectory, CancellationToken ct, params string[] pathArguments)
        {
            string[] arguments = new string[3 + pathArguments.Length];
            arguments[0] = "diff";
            arguments[1] = "--no-ext-diff";
            arguments[2] = "--no-textconv";
            pathArguments.CopyTo(arguments, 3);
            return RunGitAsync(workingDirectory, ct, arguments);
        }

        public static async Task<(int ExitCode, string Output)> RunGitAsync(string workingDirectory, CancellationToken ct, params string[] arguments)
            => await RunGitAsync(workingDirectory, ct, includeStderr: true, arguments).ConfigureAwait(false);

        public static async Task<(int ExitCode, string Output)> RunGitAsync(
            string workingDirectory, CancellationToken ct, bool includeStderr, params string[] arguments)
        {
            if (!IsTrustedGitWorkingDirectory(workingDirectory, out string untrusted))
                return (-1, "(git refused: " + untrusted + ". Host Git does not run in a worktree whose Git link the sandbox may have altered.)");
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                // Defence in depth: never launch a configured fsmonitor program from host Git.
                info.ArgumentList.Add("-c");
                info.ArgumentList.Add("core.fsmonitor=false");
                foreach (string arg in arguments) info.ArgumentList.Add(arg);

                using var process = new Process { StartInfo = info };
                process.Start();
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
                Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return (-1, "(git command timed out after 30 seconds and was terminated)");
                }

                string output = await outputTask.ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                if (!includeStderr) return (process.ExitCode, output.TrimEnd());
                string combined = output + (string.IsNullOrWhiteSpace(error) ? string.Empty : "\n[stderr]\n" + error);
                return (process.ExitCode, combined.Trim());
            }
            catch (Exception ex)
            {
                return (-1, "(could not run git: " + ex.Message + ")");
            }
        }
    }
}
