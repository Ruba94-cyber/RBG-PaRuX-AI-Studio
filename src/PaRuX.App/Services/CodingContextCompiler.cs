using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Intelligence;

namespace PaRuX.Services
{
    /// <summary>
    /// Builds the evidence packet used by every coding-agent step. The packet is intentionally
    /// smaller than the physical model window: filling a long-context model with low-value repo text
    /// hurts reasoning more than it helps. Evidence is ranked across repository rules, current diff,
    /// verification commands, task lexical matches, C# Roslyn semantics, a cross-language
    /// declaration/import graph, recent git history/change coupling and durable prior tool evidence.
    ///
    /// C# receives compiler-grade Roslyn relations. TS/JS, Python, Java/Kotlin, Go and Rust receive
    /// bounded polyglot dependency intelligence. Everything degrades fail-soft to lexical/git
    /// evidence when a language-specific index cannot be built.
    /// </summary>
    public static class CodingContextCompiler
    {
        public const int DefaultMaxChars = 12_000;
        public const int MaximumMaxChars = 64_000;

        private const int MaxTreeEntries = 180;
        private const int MaxChangedFiles = 60;
        private const int MaxRelevantMatches = 48;
        private const int MaxSnippetLineChars = 300;
        private const int MaxGraphNodes = 72;
        private const int MaxGraphEdges = 96;
        private const int MaxPriorObservationChars = 8_000;
        private const int MaxGitHistoryChars = 7_000;

        private sealed record CodeGraphCacheEntry(string Fingerprint, CSharpCodeIntelligenceGraph Graph);
        private sealed record ChangedFileEvidence(string Body, IReadOnlyList<string> Paths);
        private sealed record SemanticGraphEvidence(string Body, double DiffRelation, double TestRelation);

        private static readonly ConcurrentDictionary<string, CodeGraphCacheEntry> CodeGraphCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> CodeGraphLocks =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ManifestNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "README.md", "CLAUDE.md", "AGENTS.md", "CONTRIBUTING.md", "CODEOWNERS",
            "package.json", "pnpm-workspace.yaml", "pyproject.toml", "requirements.txt",
            "Cargo.toml", "go.mod", "go.work", "Directory.Build.props", "Directory.Build.targets",
            "global.json", "gradle.properties", "settings.gradle", "settings.gradle.kts",
            "build.gradle", "build.gradle.kts", "CMakeLists.txt", "Makefile"
        };

        private static readonly HashSet<string> NoiseDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", ".parux-agents",
            "dist", "build", "target", ".gradle", ".next", "coverage", ".pytest_cache", "__pycache__"
        };

        public static async Task<string> CompileAsync(
            string workspaceRoot,
            string task,
            CancellationToken cancellationToken,
            int maxChars = DefaultMaxChars,
            IReadOnlyList<string>? priorObservations = null)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return "(repository context unavailable: workspace does not exist)";

            string root = Path.GetFullPath(workspaceRoot);
            int budget = Math.Clamp(maxChars, 3_000, MaximumMaxChars);
            var candidates = new List<ContextEvidenceCandidate>();

            AddCandidate(candidates, "repo-map", "Repository map", BuildTree(root, cancellationToken),
                project: .62, cache: .96, diversity: "repository-shape");

            AddCandidate(candidates, "manifests", "Repository instructions / manifests",
                ReadManifestHeaders(root, cancellationToken),
                lexical: .16, project: 1.0, cache: .96, diversity: "repository-rules");

            IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> verification =
                CodingVerificationDiscovery.Discover(root);
            if (verification.Count > 0)
            {
                string body = string.Join("\n", verification.Select(command =>
                    $"- {command.Kind}: {command.Command} — {command.Reason}"))
                    + "\n- In a large repository these whole-repository commands can be slow or fail offline. Prefer the smallest existing, "
                    + "unmodified project or verifier that covers your change; running it successfully also counts as verification.";
                AddCandidate(candidates, "verification", "Verification commands", body,
                    tests: 1.0, project: .72, cache: .78, diversity: "verification");
            }

            // Independent repository reads run concurrently. This is on every agent step, so serial
            // process spawning and whole-repo scans compound directly into agent latency.
            Task<(int ExitCode, string Output)> statusTask = CodingAgentTools.RunGitAsync(
                root, cancellationToken, "status", "--porcelain=v1", "--branch");
            Task<(int ExitCode, string Output)> diffTask = CodingAgentTools.RunGitDiffAsync(
                root, cancellationToken, "--unified=2");
            Task<ChangedFileEvidence> changedTask = BuildChangedFileSummaryAsync(root, cancellationToken);
            Task<string> relevantTask = Task.Run(
                () => BuildTaskRelevantEvidence(root, task, cancellationToken), cancellationToken);
            Task<string> historyTask = BuildGitHistoryEvidenceAsync(root, task, cancellationToken);

            (int statusExit, string status) = await statusTask.ConfigureAwait(false);
            if (statusExit == 0)
                AddCandidate(candidates, "git-status", "Current Git status", Limit(status, 3_500),
                    diff: .86, recency: 1.0, diversity: "git-working-tree");

            (int diffExit, string diff) = await diffTask.ConfigureAwait(false);
            if (diffExit == 0)
                AddCandidate(candidates, "git-diff", "Current working-tree diff", Limit(diff, Math.Min(12_000, budget / 3)),
                    lexical: .24, diff: 1.0, recency: 1.0, coupling: .82, diversity: "git-working-tree");

            ChangedFileEvidence changed = await changedTask.ConfigureAwait(false);
            // Jan-inspired hierarchical project guidance, constrained to the
            // user's explicit worktree. A nested AGENTS.md relevant to changed
            // code is more useful than arbitrary depth-2 manifest sampling.
            AddCandidate(candidates, "scoped-instructions", "Scoped repository instructions",
                CodingProjectInstructionResolver.Collect(root, changed.Paths, cancellationToken, task),
                project: 1.0, lexical: .45, diff: .8, cache: .94,
                diversity: "scoped-repository-rules");
            AddCandidate(candidates, "changed-files", "Changed files", changed.Body,
                diff: 1.0, recency: 1.0, coupling: .72, diversity: "changed-files");

            IReadOnlyList<string> scopedVerification = CodingVerificationDiscovery.SuggestScopedCommands(root, changed.Paths);
            if (scopedVerification.Count > 0)
            {
                AddCandidate(candidates, "verification-scoped", "Fastest verification for your current change",
                    string.Join("\n", scopedVerification.Select(command => "- " + command)),
                    tests: 1.0, diff: .95, recency: 1.0, diversity: "verification-scoped");
            }

            // C# compiler semantics and the polyglot graph are complementary, not competing
            // implementations. Mixed-language repos can contribute both and the shared ranker
            // decides how much of each belongs in this particular step's finite evidence budget.
            Task<SemanticGraphEvidence?> csharpTask = BuildSemanticGraphEvidenceAsync(
                root, task, changed.Paths, cancellationToken);
            Task<PolyglotCodeIntelligence.Evidence?> polyglotTask = Task.Run(
                () => PolyglotCodeIntelligence.BuildEvidence(root, task, changed.Paths, cancellationToken),
                cancellationToken);

            SemanticGraphEvidence? semantic = await csharpTask.ConfigureAwait(false);
            if (semantic != null)
            {
                AddCandidate(candidates, "semantic-graph", "C# semantic graph", semantic.Body,
                    lexical: .55, symbol: 1.0, graph: 1.0, diff: semantic.DiffRelation,
                    tests: semantic.TestRelation, coupling: .78, cache: .84, diversity: "csharp-semantic-graph");
            }

            PolyglotCodeIntelligence.Evidence? polyglot = await polyglotTask.ConfigureAwait(false);
            if (polyglot != null)
            {
                AddCandidate(candidates, "polyglot-graph", "Polyglot dependency graph", Limit(polyglot.Body, 8_000),
                    lexical: .62, symbol: .88, graph: .94, diff: polyglot.DiffRelation,
                    tests: polyglot.TestRelation, coupling: .72, cache: .76, diversity: "polyglot-dependency-graph");
            }

            string relevant = await relevantTask.ConfigureAwait(false);
            AddCandidate(candidates, "task-evidence", "Task-relevant evidence", relevant,
                lexical: 1.0, symbol: .62, graph: .24, diff: .16, diversity: "task-lexical");

            string history = await historyTask.ConfigureAwait(false);
            AddCandidate(candidates, "git-history", "Recent Git history / change coupling", history,
                lexical: .42, recency: .94, coupling: 1.0, project: .42, diversity: "git-history");

            AddCandidate(candidates, "prior-agent-evidence", "Prior agent observations",
                BuildPriorObservationEvidence(priorObservations),
                lexical: .24, diff: .38, prior: 1.0, recency: .58, diversity: "agent-history");

            int rankTokenBudget = Math.Max(256, (budget - 750) / 4);
            ContextRankResult ranked = PaRuXContextRanker.Rank(
                candidates,
                PaRuXContextWorkload.Coding,
                rankTokenBudget,
                maxItems: budget >= 32_000 ? 20 : 14);

            var output = new StringBuilder(Math.Min(budget, MaximumMaxChars));
            output.AppendLine("Compiled repository context (PaRuX ranked + bounded; prefer grounded evidence before guessing):");
            output.Append("Context selection: ").Append(ranked.Items.Count).Append('/')
                .Append(ranked.CandidatesConsidered).Append(" evidence blocks, ~")
                .Append(ranked.TokensUsed).Append('/').Append(ranked.TokenBudget)
                .Append(" ranked tokens; character budget ").Append(budget).AppendLine(".");

            foreach (ContextRankedItem item in ranked.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int remaining = budget - output.Length;
                if (remaining < 180) break;
                string heading = "\n## " + item.Candidate.SourceLabel + "\n";
                string rationale = "[rank " + item.Explanation + "]\n";
                if (heading.Length + rationale.Length >= remaining) break;
                output.Append(heading).Append(rationale);
                remaining = budget - output.Length;
                output.Append(Limit(item.Candidate.Content, remaining));
            }

            if (output.Length > budget) output.Length = budget;
            return output.ToString().TrimEnd();
        }

        private static void AddCandidate(
            ICollection<ContextEvidenceCandidate> target,
            string id,
            string title,
            string? body,
            double semantic = 0,
            double lexical = 0,
            double symbol = 0,
            double graph = 0,
            double diff = 0,
            double tests = 0,
            double recency = 0,
            double coupling = 0,
            double project = 0,
            double prior = 0,
            double cache = 0,
            string? diversity = null)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            string content = body.Trim();
            target.Add(new ContextEvidenceCandidate(
                Id: id,
                SourceLabel: title,
                Content: content,
                EstimatedTokens: EstimateTokens(content),
                SemanticRelevance: semantic,
                LexicalRelevance: lexical,
                SymbolRelevance: symbol,
                GraphProximity: graph,
                CurrentDiffRelation: diff,
                TestRelation: tests,
                GitRecency: recency,
                ChangeCoupling: coupling,
                ProjectKnowledge: project,
                PriorTaskEvidence: prior,
                CacheStability: cache,
                DiversityKey: diversity));
        }

        private static int EstimateTokens(string value) => Math.Max(1, (value.Length + 3) / 4);

        private static async Task<SemanticGraphEvidence?> BuildSemanticGraphEvidenceAsync(
            string root,
            string task,
            IReadOnlyList<string> changedPaths,
            CancellationToken cancellationToken)
        {
            try
            {
                CSharpCodeIntelligenceGraph? graph = await GetCodeGraphAsync(root, cancellationToken).ConfigureAwait(false);
                if (graph == null || graph.Nodes.Count == 0) return null;

                List<string> terms = ExtractTaskTerms(task);
                var changed = new HashSet<string>(changedPaths.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
                var nodeScores = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (CodeIntelligenceNode node in graph.Nodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double score = changed.Contains(NormalizePath(node.RelativePath)) ? 1.6 : 0;
                    foreach (string term in terms.Take(14))
                    {
                        if (string.Equals(node.Name, term, StringComparison.OrdinalIgnoreCase)) score += 2.8;
                        else if (node.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 1.6;
                        else if (node.FullyQualifiedName.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 1.0;
                        else if (NormalizePath(node.RelativePath).Contains(term, StringComparison.OrdinalIgnoreCase)) score += .6;
                    }
                    if (score > 0) nodeScores[node.Id] = score;
                }

                List<CodeIntelligenceNode> seeds = graph.Nodes
                    .Where(node => nodeScores.ContainsKey(node.Id))
                    .OrderByDescending(node => nodeScores[node.Id])
                    .ThenBy(node => node.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(node => node.StartLine)
                    .Take(28)
                    .ToList();
                if (seeds.Count == 0) return null;

                var selectedIds = new HashSet<string>(seeds.Select(node => node.Id), StringComparer.Ordinal);
                foreach (CodeIntelligenceEdge edge in graph.Edges
                    .Where(edge => selectedIds.Contains(edge.FromId) || selectedIds.Contains(edge.ToId))
                    .Take(MaxGraphEdges))
                {
                    selectedIds.Add(edge.FromId);
                    selectedIds.Add(edge.ToId);
                    if (selectedIds.Count >= MaxGraphNodes) break;
                }

                Dictionary<string, CodeIntelligenceNode> byId = graph.Nodes
                    .Where(node => selectedIds.Contains(node.Id))
                    .ToDictionary(node => node.Id, StringComparer.Ordinal);
                var output = new StringBuilder();
                output.Append("Roslyn graph: ").Append(graph.Nodes.Count).Append(" nodes, ")
                    .Append(graph.Edges.Count).Append(" edges; diagnostics errors=")
                    .Append(graph.ErrorDiagnosticCount).Append(", warnings=")
                    .Append(graph.WarningDiagnosticCount).AppendLine(".");

                foreach (CodeIntelligenceNode node in byId.Values
                    .OrderByDescending(node => nodeScores.TryGetValue(node.Id, out double score) ? score : 0)
                    .ThenBy(node => node.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(node => node.StartLine)
                    .Take(MaxGraphNodes))
                {
                    output.Append("- ").Append(node.RelativePath).Append(':').Append(node.StartLine)
                        .Append(" [").Append(node.Kind).Append("] ").AppendLine(node.FullyQualifiedName);
                }

                int renderedEdges = 0;
                bool hasTestRelation = false;
                foreach (CodeIntelligenceEdge edge in graph.Edges.Where(edge =>
                    byId.ContainsKey(edge.FromId) && byId.ContainsKey(edge.ToId)
                    && edge.Kind != CodeIntelligenceEdgeKind.Contains))
                {
                    if (renderedEdges++ >= MaxGraphEdges) break;
                    CodeIntelligenceNode from = byId[edge.FromId];
                    CodeIntelligenceNode to = byId[edge.ToId];
                    output.Append("  ").Append(from.Name).Append(" --").Append(edge.Kind).Append("--> ")
                        .Append(to.Name).Append(" (").Append(to.RelativePath).Append(':').Append(to.StartLine).AppendLine(")");
                    hasTestRelation |= edge.Kind == CodeIntelligenceEdgeKind.Tests;
                }

                double diffRelation = seeds.Any(node => changed.Contains(NormalizePath(node.RelativePath))) ? 1.0 : 0.0;
                return new SemanticGraphEvidence(Limit(output.ToString(), 8_000), diffRelation, hasTestRelation ? 1.0 : 0.0);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private static async Task<CSharpCodeIntelligenceGraph?> GetCodeGraphAsync(
            string root,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<string> files = CSharpCodeIntelligenceService.EnumerateSourceFiles(root);
            if (files.Count == 0) return null;
            string fingerprint = BuildCodeGraphFingerprint(root, files, cancellationToken);
            if (CodeGraphCache.TryGetValue(root, out CodeGraphCacheEntry? cached)
                && string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
                return cached.Graph;

            SemaphoreSlim gate = CodeGraphLocks.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (CodeGraphCache.TryGetValue(root, out cached)
                    && string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return cached.Graph;

                var service = new CSharpCodeIntelligenceService();
                CSharpCodeIntelligenceGraph graph = await service.IndexRepositoryAsync(root, cancellationToken).ConfigureAwait(false);
                CodeGraphCache[root] = new CodeGraphCacheEntry(fingerprint, graph);
                return graph;
            }
            finally { gate.Release(); }
        }

        private static string BuildCodeGraphFingerprint(
            string root,
            IReadOnlyList<string> files,
            CancellationToken cancellationToken)
        {
            var builder = new StringBuilder(files.Count * 48);
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    builder.Append(NormalizePath(Path.GetRelativePath(root, file))).Append('|')
                        .Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                }
                catch { }
            }
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
            return Convert.ToHexString(hash);
        }

        private static string BuildPriorObservationEvidence(IReadOnlyList<string>? priorObservations)
        {
            if (priorObservations == null || priorObservations.Count == 0) return string.Empty;
            var result = new StringBuilder();
            foreach (string observation in priorObservations
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .TakeLast(20))
            {
                string line = Regex.Replace(observation.Trim(), @"\s+", " ");
                result.Append("- ").AppendLine(Limit(line, 760));
                if (result.Length >= MaxPriorObservationChars) break;
            }
            return Limit(result.ToString(), MaxPriorObservationChars);
        }

        private static string BuildTree(string root, CancellationToken cancellationToken)
        {
            var result = new List<string>();
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0 && result.Count < MaxTreeEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string directory, int depth) = queue.Dequeue();
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(directory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase); }
                catch { continue; }

                foreach (string entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result.Count >= MaxTreeEntries) break;
                    string name = Path.GetFileName(entry);
                    bool isDirectory = Directory.Exists(entry);
                    if (isDirectory && NoiseDirectories.Contains(name)) continue;
                    string relative = NormalizePath(Path.GetRelativePath(root, entry));
                    result.Add(relative + (isDirectory ? "/" : string.Empty));
                    if (isDirectory && depth < 3) queue.Enqueue((entry, depth + 1));
                }
            }
            return string.Join("\n", result);
        }

        private static string ReadManifestHeaders(string root, CancellationToken cancellationToken)
        {
            var result = new StringBuilder();
            var stack = new Stack<(string Directory, int Depth)>();
            stack.Push((root, 0));
            int filesRead = 0;

            while (stack.Count > 0 && filesRead < 24 && result.Length < 9_000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string directory, int depth) = stack.Pop();
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(directory)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(); }
                catch { files = Array.Empty<string>(); }

                foreach (string file in files)
                {
                    string name = Path.GetFileName(file);
                    // Instruction files have an ordered, scoped candidate
                    // above. Do not duplicate them in arbitrary manifest reads.
                    if (name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!ManifestNames.Contains(name)
                        && !name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        var manifestFile = new FileInfo(file);
                        if ((manifestFile.Attributes & FileAttributes.ReparsePoint) != 0
                            || manifestFile.Length > 128 * 1024)
                            continue;
                        // Avoid reading megabyte-scale lockfiles/manifests only to
                        // discard almost all the contents in the prompt bound.
                        string text = File.ReadAllText(file);
                        text = Limit(text, 2_200);
                        result.Append("### ").Append(NormalizePath(Path.GetRelativePath(root, file))).Append('\n')
                            .Append(text.Trim()).Append("\n\n");
                        filesRead++;
                        if (filesRead >= 24 || result.Length >= 9_000) break;
                    }
                    catch { }
                }

                if (depth >= 2) continue;
                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(directory); }
                catch { continue; }
                foreach (string subdir in subdirs.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (NoiseDirectories.Contains(Path.GetFileName(subdir))) continue;
                    try
                    {
                        if ((File.GetAttributes(subdir) & FileAttributes.ReparsePoint) != 0)
                            continue;
                        stack.Push((subdir, depth + 1));
                    }
                    catch { /* unreadable directory must never fail context */ }
                }
            }
            return Limit(result.ToString(), 9_000);
        }

        private static async Task<ChangedFileEvidence> BuildChangedFileSummaryAsync(
            string root,
            CancellationToken cancellationToken)
        {
            (int exit, string output) = await CodingAgentTools.RunGitAsync(
                root, cancellationToken, "diff", "--name-status", "--no-ext-diff").ConfigureAwait(false);
            if (exit != 0 || string.IsNullOrWhiteSpace(output))
                return new ChangedFileEvidence(string.Empty, Array.Empty<string>());

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Take(MaxChangedFiles)
                .ToArray();
            List<string> paths = lines.Select(ParseChangedPath)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new ChangedFileEvidence(string.Join("\n", lines), paths);
        }

        private static string ParseChangedPath(string line)
        {
            string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return string.Empty;
            return NormalizePath(parts[^1].Trim());
        }

        private static string BuildTaskRelevantEvidence(string root, string task, CancellationToken cancellationToken)
        {
            List<string> terms = ExtractTaskTerms(task);
            if (terms.Count == 0) return string.Empty;

            List<string> queried = terms.Take(10).ToList();
            Dictionary<string, List<CodingAgentTools.GrepMatch>> matchesByTerm;
            try { matchesByTerm = CodingAgentTools.GrepMultiple(root, queried, cancellationToken, 16); }
            catch { return string.Empty; }

            var lines = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string term in queried)
            {
                if (!matchesByTerm.TryGetValue(term, out List<CodingAgentTools.GrepMatch>? matches)) continue;
                foreach (CodingAgentTools.GrepMatch match in matches)
                {
                    string key = match.RelativePath + ":" + match.LineNumber;
                    if (!seen.Add(key)) continue;
                    string line = match.Line.Length > MaxSnippetLineChars
                        ? match.Line.Substring(0, MaxSnippetLineChars) + "…"
                        : match.Line;
                    lines.Add($"{match.RelativePath}:{match.LineNumber}: {line}");
                    if (lines.Count >= MaxRelevantMatches) return string.Join("\n", lines);
                }
            }
            return string.Join("\n", lines);
        }

        private static async Task<string> BuildGitHistoryEvidenceAsync(
            string root,
            string task,
            CancellationToken cancellationToken)
        {
            try
            {
                (int exit, string output) = await CodingAgentTools.RunGitAsync(
                    root, cancellationToken,
                    "log", "-n", "35", "--date=short", "--pretty=format:@@ %h %ad %s", "--name-only")
                    .ConfigureAwait(false);
                if (exit != 0 || string.IsNullOrWhiteSpace(output)) return string.Empty;

                List<string> terms = ExtractTaskTerms(task).Take(10).ToList();
                string[] blocks = output.Split("@@ ", StringSplitOptions.RemoveEmptyEntries);
                var scored = new List<(int Score, string Block)>();
                foreach (string raw in blocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string block = raw.Trim();
                    int score = 0;
                    foreach (string term in terms)
                        if (block.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 5;
                    // Even without a lexical match, recent commits are useful project evidence, but
                    // they rank below a task-related historical change.
                    if (score == 0) score = 1;
                    scored.Add((score, "@@ " + block));
                }

                var sb = new StringBuilder();
                foreach ((int _, string block) in scored
                    .OrderByDescending(item => item.Score)
                    .Take(12))
                {
                    sb.AppendLine(block).AppendLine();
                    if (sb.Length >= MaxGitHistoryChars) break;
                }
                return Limit(sb.ToString(), MaxGitHistoryChars);
            }
            catch (OperationCanceledException) { throw; }
            catch { return string.Empty; }
        }

        internal static List<string> ExtractTaskTerms(string? task)
        {
            if (string.IsNullOrWhiteSpace(task)) return new List<string>();
            return Regex.Matches(task, @"[A-Za-z_][A-Za-z0-9_.-]{2,}")
                .Select(match => match.Value)
                .Where(value => value.Length >= 4 && !StopWords.Contains(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(value => value.Any(char.IsUpper) || value.Contains('.') || value.Contains('_'))
                .ThenByDescending(value => value.Length)
                .Take(20)
                .ToList();
        }

        private static string NormalizePath(string value) => value.Replace('\\', '/');

        private static string Limit(string? value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || maxChars <= 0) return string.Empty;
            if (value.Length <= maxChars) return value;
            if (maxChars <= 18) return value.Substring(0, maxChars);
            return value.Substring(0, maxChars - 18) + "\n… (context cut)";
        }

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "this", "that", "with", "from", "into", "then", "than", "when", "where", "what",
            "have", "will", "should", "could", "would", "there", "their", "these", "those", "after",
            "before", "make", "made", "using", "used", "user", "task", "code", "file", "files",
            "implement", "implementation", "change", "changes", "update", "updated", "please",
            "questo", "questa", "quello", "quella", "della", "delle", "degli", "sono", "come",
            "fare", "fatto", "anche", "tutto", "tutti", "dopo", "prima", "senza", "ancora"
        };
    }
}
