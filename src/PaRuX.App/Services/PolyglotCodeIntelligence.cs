using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PaRuX.Services
{
    /// <summary>
    /// Lightweight cross-language repository intelligence for the coding context path. C# keeps its
    /// Roslyn graph; TS/JS, Python, Java/Kotlin, Go and Rust get declarations plus import/module
    /// dependencies with bounded regexes and deterministic local edge resolution. The parsed facts
    /// are fingerprint-cached, so an agent step only reparses when relevant source metadata changed.
    /// Unknown/external imports remain named evidence rather than fabricated local edges.
    /// </summary>
    public static class PolyglotCodeIntelligence
    {
        private const int MaxFiles = 1_500;
        private const int MaxFileBytes = 2 * 1024 * 1024;
        private const int MaxDeclarationsPerFile = 80;
        private const int MaxImportsPerFile = 80;
        private const int MaxSelectedFiles = 36;
        private const int MaxRenderedEdges = 96;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(300);

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".java", ".kt", ".kts", ".go", ".rs" };

        private static readonly HashSet<string> NoiseDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "build", "target", ".gradle", ".parux-agents", ".next", "coverage", "__pycache__", ".pytest_cache" };

        private static readonly Regex JsImport = Rx("(?:import\\s+(?:[^;]*?\\s+from\\s+)?|export\\s+[^;]*?\\s+from\\s+|require\\s*\\()\\s*['\\\"]([^'\\\"]+)['\\\"]");
        private static readonly Regex JsDeclaration = Rx(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?(?:class|interface|type|enum|function)\s+([A-Za-z_$][\w$]*)|^\s*(?:export\s+)?(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>");
        private static readonly Regex PyImport = Rx(@"^\s*(?:from\s+([\.\w]+)\s+import\s+|import\s+([\w\.]+))");
        private static readonly Regex PyDeclaration = Rx(@"^\s*(?:async\s+)?def\s+([A-Za-z_]\w*)|^\s*class\s+([A-Za-z_]\w*)");
        private static readonly Regex JvmImport = Rx(@"^\s*import\s+([A-Za-z_]\w*(?:\.[A-Za-z_*]\w*)*)\s*;?");
        private static readonly Regex JvmPackage = Rx(@"^\s*package\s+([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*;?");
        private static readonly Regex JvmDeclaration = Rx(@"^\s*(?:(?:public|private|protected|internal|open|abstract|sealed|data|static|final|suspend|inline)\s+)*(?:class|interface|enum\s+class|enum|record|object|fun)\s+([A-Za-z_]\w*)");
        private static readonly Regex GoImportSingle = Rx("^\\s*import\\s+(?:[._A-Za-z]\\w*\\s+)?\\\"([^\\\"]+)\\\"");
        private static readonly Regex GoImportBlock = Rx("^\\s*(?:[._A-Za-z]\\w*\\s+)?\\\"([^\\\"]+)\\\"");
        private static readonly Regex GoDeclaration = Rx(@"^\s*(?:func\s+(?:\([^)]*\)\s*)?([A-Za-z_]\w*)|type\s+([A-Za-z_]\w*)\s+(?:struct|interface))");
        private static readonly Regex RustImport = Rx(@"^\s*(?:pub\s+)?(?:use|mod)\s+([^;]+)");
        private static readonly Regex RustDeclaration = Rx(@"^\s*(?:(?:pub(?:\([^)]*\))?|async|unsafe|const)\s+)*(?:fn|struct|enum|trait|type|mod)\s+([A-Za-z_]\w*)");

        public sealed record Evidence(string Body, double DiffRelation, double TestRelation, int FilesConsidered, int LocalEdges);
        internal sealed record FileFacts(string RelativePath, string? Namespace, IReadOnlyList<string> Declarations, IReadOnlyList<string> Imports);
        private sealed record SourceFile(string FullPath, string RelativePath, long Length, long LastWriteTicks);
        private sealed record CacheEntry(string Fingerprint, List<FileFacts> Facts);

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static Evidence? BuildEvidence(
            string workspaceRoot,
            string task,
            IReadOnlyCollection<string> changedPaths,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot)) return null;
            string root = Path.GetFullPath(workspaceRoot);
            List<SourceFile> sourceFiles = EnumerateSources(root, ct);
            if (sourceFiles.Count == 0) return null;

            string fingerprint = BuildFingerprint(sourceFiles);
            if (!Cache.TryGetValue(root, out CacheEntry? cached)
                || !string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                cached = new CacheEntry(fingerprint, ReadFacts(sourceFiles, ct));
                Cache[root] = cached;
            }
            List<FileFacts> facts = cached.Facts;
            if (facts.Count == 0) return null;

            var byPath = facts.ToDictionary(f => Normalize(f.RelativePath), StringComparer.OrdinalIgnoreCase);
            var changed = new HashSet<string>(changedPaths.Select(Normalize), StringComparer.OrdinalIgnoreCase);
            List<string> terms = ExtractTerms(task);

            var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (FileFacts file in facts)
            {
                ct.ThrowIfCancellationRequested();
                string path = Normalize(file.RelativePath);
                double score = changed.Contains(path) ? 5.0 : 0.0;
                foreach (string term in terms)
                {
                    if (path.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 1.6;
                    if (file.Declarations.Any(d => d.Contains(term, StringComparison.OrdinalIgnoreCase))) score += 2.4;
                    if (file.Imports.Any(i => i.Contains(term, StringComparison.OrdinalIgnoreCase))) score += .7;
                }
                if (score > 0) scores[path] = score;
            }

            var localEdges = new List<(string From, string Import, string To)>();
            foreach (FileFacts file in facts)
            {
                ct.ThrowIfCancellationRequested();
                string from = Normalize(file.RelativePath);
                foreach (string import in file.Imports)
                {
                    string? resolved = ResolveLocalImport(root, file, import, byPath.Keys);
                    if (resolved != null) localEdges.Add((from, import, resolved));
                }
            }

            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in scores
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(22)
                .Select(pair => pair.Key))
                selected.Add(path);
            foreach (string path in changed.Where(byPath.ContainsKey)) selected.Add(path);
            if (selected.Count == 0) return null;

            // One-hop dependency expansion in both directions: if the task names a caller, include
            // its imported dependency; if it names a dependency, include callers that rely on it.
            foreach ((string from, _, string to) in localEdges)
            {
                if (selected.Contains(from)) selected.Add(to);
                if (selected.Contains(to)) selected.Add(from);
                if (selected.Count >= MaxSelectedFiles) break;
            }

            var sb = new StringBuilder();
            sb.Append("Polyglot graph: ").Append(facts.Count).Append(" files, ")
                .Append(localEdges.Count).AppendLine(" resolved local dependency edges.");
            foreach (string path in selected
                .OrderByDescending(path => scores.TryGetValue(path, out double score) ? score : 0)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSelectedFiles))
            {
                FileFacts file = byPath[path];
                sb.Append("- ").Append(path);
                if (!string.IsNullOrWhiteSpace(file.Namespace)) sb.Append(" [module ").Append(file.Namespace).Append(']');
                if (file.Declarations.Count > 0)
                    sb.Append(" symbols: ").Append(string.Join(", ", file.Declarations.Take(14)));
                sb.AppendLine();
            }

            int rendered = 0;
            foreach ((string from, string import, string to) in localEdges
                .Where(edge => selected.Contains(edge.From) || selected.Contains(edge.To)))
            {
                if (rendered++ >= MaxRenderedEdges) break;
                sb.Append("  ").Append(from).Append(" --imports ").Append(import).Append("--> ").AppendLine(to);
            }

            bool testRelation = selected.Any(IsTestPath);
            bool diffRelation = selected.Any(changed.Contains);
            return new Evidence(sb.ToString().TrimEnd(), diffRelation ? 1.0 : 0.0,
                testRelation ? 1.0 : 0.0, facts.Count, localEdges.Count);
        }

        internal static FileFacts ParseFile(string relativePath, string text)
        {
            string ext = Path.GetExtension(relativePath).ToLowerInvariant();
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var declarations = new List<string>();
            var imports = new List<string>();
            string? module = null;
            bool goImportBlock = false;

            foreach (string raw in lines)
            {
                if (declarations.Count >= MaxDeclarationsPerFile && imports.Count >= MaxImportsPerFile) break;
                string line = raw.Length > 8_000 ? raw[..8_000] : raw;
                try
                {
                    if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
                    {
                        AddMatches(JsImport, line, imports, MaxImportsPerFile);
                        AddMatches(JsDeclaration, line, declarations, MaxDeclarationsPerFile);
                    }
                    else if (ext == ".py")
                    {
                        AddMatches(PyImport, line, imports, MaxImportsPerFile);
                        AddMatches(PyDeclaration, line, declarations, MaxDeclarationsPerFile);
                    }
                    else if (ext is ".java" or ".kt" or ".kts")
                    {
                        Match package = JvmPackage.Match(line);
                        if (package.Success && module == null) module = FirstGroup(package);
                        AddMatches(JvmImport, line, imports, MaxImportsPerFile);
                        AddMatches(JvmDeclaration, line, declarations, MaxDeclarationsPerFile);
                    }
                    else if (ext == ".go")
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("package ", StringComparison.Ordinal) && module == null)
                            module = trimmed[8..].Trim();
                        if (trimmed == "import (") { goImportBlock = true; continue; }
                        if (goImportBlock && trimmed == ")") { goImportBlock = false; continue; }
                        AddMatches(goImportBlock ? GoImportBlock : GoImportSingle, line, imports, MaxImportsPerFile);
                        AddMatches(GoDeclaration, line, declarations, MaxDeclarationsPerFile);
                    }
                    else if (ext == ".rs")
                    {
                        AddMatches(RustImport, line, imports, MaxImportsPerFile);
                        AddMatches(RustDeclaration, line, declarations, MaxDeclarationsPerFile);
                    }
                }
                catch (RegexMatchTimeoutException) { }
            }

            return new FileFacts(Normalize(relativePath), module,
                declarations.Distinct(StringComparer.Ordinal).Take(MaxDeclarationsPerFile).ToList(),
                imports.Distinct(StringComparer.Ordinal).Take(MaxImportsPerFile).ToList());
        }

        private static List<SourceFile> EnumerateSources(string root, CancellationToken ct)
        {
            var result = new List<SourceFile>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && result.Count < MaxFiles)
            {
                ct.ThrowIfCancellationRequested();
                string directory = stack.Pop();

                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(directory); }
                catch { continue; }
                foreach (string sub in subdirs)
                    if (!NoiseDirectories.Contains(Path.GetFileName(sub))) stack.Push(sub);

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(directory); }
                catch { continue; }
                foreach (string file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (result.Count >= MaxFiles) break;
                    if (!SupportedExtensions.Contains(Path.GetExtension(file))) continue;
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length > MaxFileBytes) continue;
                        result.Add(new SourceFile(
                            file,
                            Normalize(Path.GetRelativePath(root, file)),
                            info.Length,
                            info.LastWriteTimeUtc.Ticks));
                    }
                    catch { }
                }
            }
            return result.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<FileFacts> ReadFacts(IReadOnlyList<SourceFile> files, CancellationToken ct)
        {
            var result = new List<FileFacts>(files.Count);
            foreach (SourceFile file in files)
            {
                ct.ThrowIfCancellationRequested();
                try { result.Add(ParseFile(file.RelativePath, File.ReadAllText(file.FullPath))); }
                catch { }
            }
            return result;
        }

        private static string BuildFingerprint(IReadOnlyList<SourceFile> files)
        {
            var builder = new StringBuilder(files.Count * 64);
            foreach (SourceFile file in files)
                builder.Append(file.RelativePath).Append('|').Append(file.Length).Append('|')
                    .Append(file.LastWriteTicks).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        private static string? ResolveLocalImport(
            string root,
            FileFacts from,
            string import,
            IEnumerable<string> paths)
        {
            string ext = Path.GetExtension(from.RelativePath).ToLowerInvariant();
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string fromDir = Normalize(Path.GetDirectoryName(from.RelativePath) ?? string.Empty);
            var known = new HashSet<string>(paths.Select(Normalize), StringComparer.OrdinalIgnoreCase);

            if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
            {
                if (!import.StartsWith(".", StringComparison.Ordinal)) return null;
                string? basePath = ResolveRelativeInsideRoot(root, fromDir, import);
                if (basePath == null) return null;
                if (SupportedExtensions.Contains(Path.GetExtension(basePath))) candidates.Add(basePath);
                AddScriptCandidates(candidates, basePath);
            }
            else if (ext == ".py")
            {
                string dotted = import.Trim();
                int leadingDots = dotted.TakeWhile(c => c == '.').Count();
                string module = dotted.TrimStart('.').Replace('.', '/');
                string baseDir = leadingDots > 0 ? fromDir : string.Empty;
                for (int i = 1; i < leadingDots; i++)
                    baseDir = Normalize(Path.GetDirectoryName(baseDir) ?? string.Empty);
                string basePath = string.IsNullOrEmpty(baseDir) ? module : baseDir + "/" + module;
                candidates.Add(basePath + ".py");
                candidates.Add(basePath.TrimEnd('/') + "/__init__.py");
            }
            else if (ext is ".java" or ".kt" or ".kts")
            {
                string suffix = import.TrimEnd('*').TrimEnd('.').Replace('.', '/');
                foreach (string path in known)
                {
                    if (path.EndsWith(suffix + ".java", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(suffix + ".kt", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(suffix + ".kts", StringComparison.OrdinalIgnoreCase))
                        return path;
                }
            }
            else if (ext == ".go")
            {
                string clean = import.Trim('/');
                foreach (string path in known)
                {
                    string dir = Normalize(Path.GetDirectoryName(path) ?? string.Empty);
                    if (dir.EndsWith(clean, StringComparison.OrdinalIgnoreCase)
                        || clean.EndsWith(dir, StringComparison.OrdinalIgnoreCase))
                        return path;
                }
            }
            else if (ext == ".rs")
            {
                string cleaned = import.Replace("crate::", string.Empty, StringComparison.Ordinal)
                    .Replace("self::", string.Empty, StringComparison.Ordinal)
                    .Replace("super::", string.Empty, StringComparison.Ordinal);
                string[] pieces = cleaned.Split(new[] { "::", "{" }, StringSplitOptions.RemoveEmptyEntries);
                string module = pieces.Length == 0 ? string.Empty : pieces[0].Trim();
                if (module.Length > 0)
                {
                    candidates.Add((string.IsNullOrEmpty(fromDir) ? string.Empty : fromDir + "/") + module + ".rs");
                    candidates.Add((string.IsNullOrEmpty(fromDir) ? string.Empty : fromDir + "/") + module + "/mod.rs");
                    candidates.Add("src/" + module + ".rs");
                    candidates.Add("src/" + module + "/mod.rs");
                }
            }

            return candidates.Select(Normalize).FirstOrDefault(known.Contains);
        }

        private static string? ResolveRelativeInsideRoot(string root, string relativeDirectory, string import)
        {
            try
            {
                string canonicalRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string baseDirectory = Path.Combine(canonicalRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
                string candidate = Path.GetFullPath(Path.Combine(baseDirectory, import.Replace('/', Path.DirectorySeparatorChar)));
                string rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
                StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!candidate.StartsWith(rootPrefix, comparison) && !string.Equals(candidate, canonicalRoot, comparison)) return null;
                return Normalize(Path.GetRelativePath(canonicalRoot, candidate));
            }
            catch { return null; }
        }

        private static void AddScriptCandidates(ISet<string> target, string basePath)
        {
            string extension = Path.GetExtension(basePath);
            string withoutKnownExtension = SupportedExtensions.Contains(extension)
                ? basePath[..^extension.Length]
                : basePath;
            foreach (string ext in new[] { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs" })
            {
                target.Add(withoutKnownExtension + ext);
                target.Add(withoutKnownExtension.TrimEnd('/') + "/index" + ext);
            }
        }

        private static void AddMatches(Regex regex, string line, ICollection<string> target, int maximum)
        {
            if (target.Count >= maximum) return;
            foreach (Match match in regex.Matches(line))
            {
                string? value = FirstGroup(match);
                if (!string.IsNullOrWhiteSpace(value)) target.Add(value.Trim());
                if (target.Count >= maximum) break;
            }
        }

        private static string? FirstGroup(Match match) =>
            match.Groups.Cast<Group>().Skip(1).FirstOrDefault(group => group.Success && group.Value.Length > 0)?.Value;

        private static Regex Rx(string pattern) => new(pattern, RegexOptions.Compiled, RegexTimeout);

        private static List<string> ExtractTerms(string? task)
        {
            if (string.IsNullOrWhiteSpace(task)) return new List<string>();
            return Regex.Matches(task, @"[A-Za-z_$][A-Za-z0-9_.$/-]{2,}")
                .Select(match => match.Value.Trim('.', '/', '-'))
                .Where(value => value.Length >= 4 && !StopWords.Contains(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(value => value.Length)
                .Take(18)
                .ToList();
        }

        private static bool IsTestPath(string path)
        {
            string p = Normalize(path);
            string name = Path.GetFileName(p);
            return p.Contains("/test/", StringComparison.OrdinalIgnoreCase)
                || p.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
                || p.Contains("/__tests__/", StringComparison.OrdinalIgnoreCase)
                || name.Contains(".test.", StringComparison.OrdinalIgnoreCase)
                || name.Contains(".spec.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('/');

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "this", "that", "with", "from", "into", "then", "when", "where", "what", "should",
            "could", "would", "have", "using", "used", "task", "code", "file", "files", "change",
            "changes", "implement", "implementation", "update", "please", "questo", "questa", "della",
            "delle", "sono", "come", "fare", "anche", "tutto", "senza"
        };
    }
}
