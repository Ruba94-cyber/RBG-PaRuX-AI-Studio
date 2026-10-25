using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.ContextGraph;

/// <summary>
/// Persistent repository intelligence over ContextGraphStore. Files and declarations become graph
/// entities; TESTS, DEPENDS_ON and CALLS relations add bounded repository topology; retrieval blends
/// camel/path-aware lexical overlap with local embeddings when available and bounded Git-history
/// evidence as a relevance boost. Topology is intentionally heuristic navigation evidence rather
/// than a claimed AST/LSP replacement: dependency edges require a resolvable repository-local
/// target, and call edges require exactly one symbol with that name in another indexed file.
/// </summary>
public sealed class RepoIndexService
{
    private const string EntityTypeFile = "file";
    private const string EntityTypeSymbol = "symbol";
    private const string RelationTests = "TESTS";
    private const string RelationDependsOn = "DEPENDS_ON";
    private const string RelationCalls = "CALLS";
    private const int MaximumCallRelationsPerFile = 96;
    private const int MaximumDependencyRelationsPerFile = 96;
    private const int MaximumGitCommitsToInspect = 512;
    private static readonly TimeSpan GitProbeTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] ExcludedDirectoryNames =
    {
        ".git", "bin", "obj", "node_modules", ".parux-agents", ".vs", "packages", "dist", "build"
    };

    private static readonly HashSet<string> IndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts", ".py", ".java", ".kt", ".go", ".rb",
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hh", ".hpp", ".hxx", ".rs"
    };

    private sealed record FileHistoryEvidence(int CommitCount, long LastCommitUnix);
    private sealed record IndexedFile(string RelativePath, string FullPath, string EntityId);

    /// <summary>Files above this size are never projected (data dumps, bundles): bounded read cost per file.</summary>
    internal const long MaximumIndexedFileBytes = 4L * 1024 * 1024;

    /// <summary>Optional phase observer (name, elapsed) for benchmarks; null in production.</summary>
    internal static Action<string, TimeSpan>? PhaseObserver { get; set; }

    /// <summary>Number of distinct indexed file paths materialized by the most recent incremental reindex.</summary>
    internal int IndexedFileRecordsLoadedForTest
    {
        get { lock (_indexedFileLoadCounterGate) return _indexedFilePathsLoadedForTest.Count; }
    }

    /// <summary>Number of per-file topology input rows materialized by the most recent incremental reindex.</summary>
    internal int TopologyInputRowsLoadedForTest => Volatile.Read(ref _topologyInputRowsLoadedForTest);

    private static T Timed<T>(string phase, Func<T> work)
    {
        Action<string, TimeSpan>? observer = PhaseObserver;
        if (observer == null) return work();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return work(); }
        finally { observer(phase, System.Diagnostics.Stopwatch.GetElapsedTime(start)); }
    }

    /// <summary>Test seam: invoked after each file projection is committed during an incremental reindex.</summary>
    internal Action<string>? AfterFileProjectedForTest { get; set; }

    /// <summary>Test-only count of Git-history probe attempts made by this index service instance.</summary>
    internal int GitHistoryProbeCountForTest => Volatile.Read(ref _gitHistoryProbeCountForTest);

    /// <summary>Optional deterministic history evidence for tests; the production Git probe remains unchanged.</summary>
    internal Func<string, IReadOnlyDictionary<string, (int CommitCount, long LastCommitUnix)>>? GitHistoryEvidenceProviderForTest { get; set; }

    private readonly ContextGraphStore _graph;
    private readonly EmbeddingService? _embeddings;
    private int _gitHistoryProbeCountForTest;
    private readonly object _indexedFileLoadCounterGate = new();
    private readonly HashSet<string> _indexedFilePathsLoadedForTest = new(StringComparer.OrdinalIgnoreCase);
    private int _topologyInputRowsLoadedForTest;

    public RepoIndexService(ContextGraphStore graph, EmbeddingService? embeddings = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _embeddings = embeddings;
    }

    private static string RepoKey(string repoRoot) =>
        Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
    private static string RepoScope(string repoRoot) => "repo-index:" + RepoKey(repoRoot) + "::";
    private static string FileSource(string repoRoot, string relativePath) =>
        RepoScope(repoRoot) + relativePath.Replace('\\', '/');
    private static string SymbolSource(string repoRoot, string relativePath) => FileSource(repoRoot, relativePath);

    public RepoIndexResult IndexRepository(string repoRoot)
    {
        string root = Path.GetFullPath(repoRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        MarkUsed(repoRoot);
        SchedulePrune(root);
        RemoveRepositoryIndex(repoRoot);

        Dictionary<string, FileHistoryEvidence> history = ProbeGitHistory(root);
        int filesIndexed = 0;
        int symbolsIndexed = 0;
        int filesWithGitHistory = 0;
        var indexedFiles = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in EnumerateIndexableFiles(root))
        {
            string relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            history.TryGetValue(relativePath, out FileHistoryEvidence? fileHistory);
            IndexedFile? indexed = IndexFileInternal(repoRoot, root, relativePath, fileHistory, out int symbolCount);
            if (indexed == null) continue;
            filesIndexed++;
            symbolsIndexed += symbolCount;
            if (fileHistory != null) filesWithGitHistory++;
            indexedFiles[relativePath] = indexed;
        }

        (int tests, int dependencies, int calls) = Timed("topology", () => LinkTopology(repoRoot, root, indexedFiles, replaceExisting: false));
        return new RepoIndexResult
        {
            FilesIndexed = filesIndexed,
            SymbolsIndexed = symbolsIndexed,
            TestRelationsFound = tests,
            DependencyRelationsFound = dependencies,
            CallRelationsFound = calls,
            FilesWithGitHistory = filesWithGitHistory
        };
    }

    private const int KeptAgentWorktreeScopes = 6;
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(10);
    private DateTime _lastPruneUtc = DateTime.UtcNow; // first background prune 10 min after start

    /// <summary>
    /// The repository index is a cache. Every coding task indexes a fresh agent worktree, and those
    /// scopes were never removed: the live graph reached 1.4 GB. Scopes whose folder no longer exists
    /// are dropped, and agent worktrees (".parux-agents") beyond the most recently used few are
    /// dropped too; they are re-indexed on demand if a task resumes. User repositories are kept.
    /// </summary>
    private int _pruneRunning;
    private static readonly TimeSpan RecentlyUsedProtection = TimeSpan.FromHours(2);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastUsedUtc = new(StringComparer.Ordinal);

    private void MarkUsed(string repoRoot) => _lastUsedUtc[RepoKey(repoRoot)] = DateTime.UtcNow;

    /// <summary>Pruning runs off the request path: a first pass over a large legacy graph must not
    /// delay the coding task that triggered it. Each scope is deleted in its own short transaction.</summary>
    private void SchedulePrune(string currentRoot)
    {
        if (DateTime.UtcNow - _lastPruneUtc < PruneInterval) return;
        if (Interlocked.CompareExchange(ref _pruneRunning, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { PruneStaleAgentWorktreeScopes(currentRoot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or Microsoft.Data.Sqlite.SqliteException)
            {
                // Best effort: the index is a cache and pruning is retried after the interval.
            }
            finally { Interlocked.Exchange(ref _pruneRunning, 0); }
        });
    }

    internal int PruneStaleAgentWorktreeScopes(string currentRoot, DateTime? utcNow = null)
    {
        DateTime now = utcNow ?? DateTime.UtcNow;
        if (now - _lastPruneUtc < PruneInterval) return 0;
        _lastPruneUtc = now;
        string currentKey = RepoKey(currentRoot);
        var agentScopes = new List<(string Scope, string Root, DateTime LastUsedUtc)>();
        int removed = 0;
        foreach (string scope in _graph.ListSourceScopes("repo-index:", "::"))
        {
            string key = scope["repo-index:".Length..^2];
            if (string.Equals(key, currentKey, StringComparison.Ordinal)) continue;
            bool exists;
            try { exists = Directory.Exists(key); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { exists = true; }
            if (!exists)
            {
                _graph.DeleteSourcePrefix(scope);
                removed++;
                continue;
            }
            if (key.Contains(@"\.parux-agents\", StringComparison.OrdinalIgnoreCase)
                || key.Contains("/.parux-agents/", StringComparison.OrdinalIgnoreCase))
            {
                // Review: a long task that only edits subfolders leaves the worktree folder's write time
                // old. Index/search use in this process is the stronger signal, and a recently used scope
                // (a task that may still be running) is never pruned.
                bool recentlyUsed = _lastUsedUtc.TryGetValue(key, out DateTime usedUtc) && now - usedUtc < RecentlyUsedProtection;
                if (recentlyUsed) continue;
                DateTime lastUsed;
                try { lastUsed = Directory.GetLastWriteTimeUtc(key); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { lastUsed = DateTime.MinValue; }
                if (_lastUsedUtc.TryGetValue(key, out DateTime known) && known > lastUsed) lastUsed = known;
                agentScopes.Add((scope, key, lastUsed));
            }
        }
        foreach ((string scope, _, _) in agentScopes.OrderByDescending(item => item.LastUsedUtc).Skip(KeptAgentWorktreeScopes))
        {
            _graph.DeleteSourcePrefix(scope);
            removed++;
        }
        return removed;
    }

    public void ReindexFile(string repoRoot, string relativePath) =>
        ReindexFiles(repoRoot, new[] { relativePath });

    /// <summary>
    /// Re-index several changed files with one Git-history probe and a topology reconciliation
    /// limited to the changed paths and their direct neighbors.
    /// </summary>
    public void ReindexFiles(
        string repoRoot,
        IEnumerable<string> relativePaths,
        IEnumerable<string>? removedRelativePaths = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_indexedFileLoadCounterGate) _indexedFilePathsLoadedForTest.Clear();
        Interlocked.Exchange(ref _topologyInputRowsLoadedForTest, 0);
        string root = Path.GetFullPath(repoRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var requested = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = (removedRelativePaths ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        removed.ExceptWith(requested);
        if (requested.Count == 0 && removed.Count == 0) return;

        string scope = RepoScope(repoRoot);
        var seedPaths = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        seedPaths.UnionWith(removed);
        bool fullTopologyFallback = _graph.CountFilesMissingTopologyInputs(scope) > 0;
        var affectedSources = new HashSet<string>(seedPaths, StringComparer.OrdinalIgnoreCase);
        var priorEntityIds = new HashSet<string>(StringComparer.Ordinal);
        var priorSymbolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seedFileEntities = new Dictionary<string, ContextEntityDto>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in seedPaths)
        {
            ContextEntityDto? oldFile = _graph.GetEntityBySource(EntityTypeFile, FileSource(repoRoot, path));
            if (oldFile == null) continue;
            seedFileEntities[path] = oldFile;
            RecordIndexedFilePathLoadedForTest(path);
        }
        if (!fullTopologyFallback)
        {
            foreach (string path in seedPaths)
            {
                if (seedFileEntities.TryGetValue(path, out ContextEntityDto? oldFile))
                    priorEntityIds.Add(oldFile.Id);
                List<ContextEntityDto> oldSymbols = _graph.ListEntitiesBySource(EntityTypeSymbol, FileSource(repoRoot, path));
                priorEntityIds.UnionWith(oldSymbols.Select(entity => entity.Id));
                priorSymbolNames.UnionWith(oldSymbols.Select(entity => entity.Name));
            }

            AddSourcePaths(affectedSources, scope, _graph.ListTopologyNeighborSources(scope, priorEntityIds, TopologyRelationTypes));
            AddSourcePaths(affectedSources, scope, _graph.FindTopologyCallReferrers(scope, _graph.FilterNamesStillResolvable(scope, priorSymbolNames, SeedSources(repoRoot, seedPaths))));
            AddImportReferrerPaths(affectedSources, root, scope, seedPaths);
            foreach (string seed in seedPaths)
                affectedSources.UnionWith(BuildTestCounterpartPaths(seed));
        }

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relativePath in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            seedFileEntities.TryGetValue(relativePath, out ContextEntityDto? fileEntity);
            string? storedContentHash = fileEntity == null ? null : ParseContentHash(fileEntity.MetadataJson);
            if (storedContentHash == null)
            {
                changed.Add(relativePath);
                continue;
            }

            string? currentContentHash = ComputeContentHash(root, relativePath);
            if (currentContentHash == null || !string.Equals(storedContentHash, currentContentHash, StringComparison.Ordinal))
                changed.Add(relativePath);
        }
        cancellationToken.ThrowIfCancellationRequested();

        // A retry can reach this point after file projections committed but topology reconciliation
        // did not. Matching content hashes skip Git history and projections, while the final topology
        // pass remains necessary to complete that pending incremental update.

        Dictionary<string, FileHistoryEvidence> history = changed.Count == 0
            ? new Dictionary<string, FileHistoryEvidence>(StringComparer.OrdinalIgnoreCase)
            : GitHistoryEvidenceProviderForTest is { } historyProvider
                ? historyProvider(root).ToDictionary(
                    pair => pair.Key,
                    pair => new FileHistoryEvidence(Math.Max(0, pair.Value.CommitCount), Math.Max(0L, pair.Value.LastCommitUnix)),
                    StringComparer.OrdinalIgnoreCase)
                : Timed("history", () => ProbeGitHistory(root));

        // Git-history ranking evidence is stored per file/symbol. New commits can move it for files whose
        // content hash did not change (touched and reverted, mode/merge-only), which would leave ranking
        // different from a full rebuild. The probe above already has current history for every path, so
        // files whose stored evidence drifted are re-projected with the changed set. Skipped when git
        // produced no history at all (no repository / probe failure) so a failed probe never rewrites everything.
        if (history.Count > 0)
        {
            List<ContextEntityDto> historyFiles = _graph.ListFilesWithHistoryMismatch(
                scope,
                history.Select(pair => (pair.Key, pair.Value.CommitCount, pair.Value.LastCommitUnix)));
            foreach (ContextEntityDto fileEntity in historyFiles)
            {
                string path = fileEntity.Name.Replace('\\', '/');
                if (changed.Contains(path) || removed.Contains(path)) continue;
                RecordIndexedFilePathLoadedForTest(path);
                changed.Add(path);
                seedFileEntities[path] = fileEntity;
            }
        }
        if (!fullTopologyFallback)
        {
            string[] historyChangedPaths = changed.Where(path => !seedPaths.Contains(path)).ToArray();
            foreach (string path in historyChangedPaths)
            {
                seedPaths.Add(path);
                if (seedFileEntities.TryGetValue(path, out ContextEntityDto? oldFile)) priorEntityIds.Add(oldFile.Id);
                List<ContextEntityDto> oldSymbols = _graph.ListEntitiesBySource(EntityTypeSymbol, FileSource(repoRoot, path));
                priorEntityIds.UnionWith(oldSymbols.Select(entity => entity.Id));
                priorSymbolNames.UnionWith(oldSymbols.Select(entity => entity.Name));
            }
            AddSourcePaths(affectedSources, scope, _graph.ListTopologyNeighborSources(scope, priorEntityIds, TopologyRelationTypes));
            AddSourcePaths(affectedSources, scope, _graph.FindTopologyCallReferrers(scope, _graph.FilterNamesStillResolvable(scope, priorSymbolNames, SeedSources(repoRoot, seedPaths))));
            AddImportReferrerPaths(affectedSources, root, scope, historyChangedPaths);
            foreach (string path in historyChangedPaths) affectedSources.UnionWith(BuildTestCounterpartPaths(path));
        }
        long projectStart = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (string relativePath in changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            history.TryGetValue(relativePath, out FileHistoryEvidence? fileHistory);
            if (IndexFileInternal(repoRoot, root, relativePath, fileHistory, out _) == null)
                RemoveFileIndex(repoRoot, relativePath);
            AfterFileProjectedForTest?.Invoke(relativePath);
        }
        PhaseObserver?.Invoke("project", System.Diagnostics.Stopwatch.GetElapsedTime(projectStart));
        foreach (string relativePath in removed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveFileIndex(repoRoot, relativePath);
        }

        if (!fullTopologyFallback)
        {
            var changedSymbolNames = new HashSet<string>(priorSymbolNames, StringComparer.OrdinalIgnoreCase);
            foreach (string relativePath in changed)
                changedSymbolNames.UnionWith(_graph.ListEntitiesBySource(EntityTypeSymbol, FileSource(repoRoot, relativePath)).Select(entity => entity.Name));
            AddSourcePaths(affectedSources, scope, _graph.FindTopologyCallReferrers(scope, _graph.FilterNamesStillResolvable(scope, changedSymbolNames, SeedSources(repoRoot, seedPaths))));
            AddImportReferrerPaths(affectedSources, root, scope, seedPaths);
        }

        _ = Timed("topology", () =>
        {
            if (fullTopologyFallback)
            {
                Dictionary<string, IndexedFile> allFiles = LoadIndexedFiles(repoRoot, root);
                return LinkTopology(repoRoot, root, allFiles, replaceExisting: true, cancellationToken);
            }
            return LinkTopologyIncremental(repoRoot, root, affectedSources, seedPaths, cancellationToken);
        });
    }

    private static IReadOnlySet<string> SeedSources(string repoRoot, IEnumerable<string> seedPaths) =>
        seedPaths.Select(path => FileSource(repoRoot, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void RemoveFileIndex(string repoRoot, string relativePath)
    {
        string source = SymbolSource(repoRoot, relativePath.Replace('\\', '/'));
        _graph.DeleteSourceEntities(source);
    }

    public void RemoveRepositoryIndex(string repoRoot)
    {
        string prefix = RepoScope(repoRoot);
        _graph.DeleteSourcePrefix(prefix);
    }

    private IndexedFile? IndexFileInternal(
        string repoRoot,
        string root,
        string relativePath,
        FileHistoryEvidence? history,
        out int symbolCount)
    {
        symbolCount = 0;
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        byte[] content;
        string[] lines;
        try
        {
            if (new FileInfo(fullPath).Length > MaximumIndexedFileBytes) return null;
            content = File.ReadAllBytes(fullPath);
            if (content.LongLength > MaximumIndexedFileBytes) return null;
            lines = DecodeLines(content);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new IOException("Could not read repository file for indexing: " + relativePath, ex);
        }

        string contentHash = Convert.ToHexString(SHA256.HashData(content));
        string historyMetadata = HistoryMetadata(history);
        long parseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var requests = new List<CreateContextEntityRequest>
        {
            new()
            {
                EntityType = EntityTypeFile,
                Name = relativePath,
                MetadataJson = "{\"relativePath\":" + JsonQuote(relativePath)
                    + ",\"contentHash\":" + JsonQuote(contentHash) + historyMetadata + "}",
                Source = FileSource(repoRoot, relativePath)
            }
        };

        foreach ((string name, string signature, int lineNumber) in ExtractSymbols(relativePath, lines))
        {
            string? embeddingBase64 = _embeddings != null
                ? EncodeBase64OrNull(_embeddings.TryEmbed(signature))
                : null;
            string metadata = "{\"relativePath\":" + JsonQuote(relativePath)
                + ",\"signature\":" + JsonQuote(signature)
                + ",\"line\":" + lineNumber
                + historyMetadata
                + (embeddingBase64 == null ? string.Empty : ",\"embeddingBase64\":" + JsonQuote(embeddingBase64))
                + "}";

            requests.Add(new CreateContextEntityRequest
            {
                EntityType = EntityTypeSymbol,
                Name = name,
                MetadataJson = metadata,
                Source = SymbolSource(repoRoot, relativePath)
            });
            symbolCount++;
        }

        ContextGraphStore.TopologyInputs topologyInputs = BuildTopologyInputs(relativePath, lines);
        PhaseObserver?.Invoke("parse", System.Diagnostics.Stopwatch.GetElapsedTime(parseStart));
        IReadOnlyList<ContextEntityDto> entities = Timed("graph-write", () => _graph.ReplaceSourceEntitiesWithContainment(
            FileSource(repoRoot, relativePath), requests, topologyInputs));
        ContextEntityDto fileEntity = entities[0];
        return new IndexedFile(relativePath, fullPath, fileEntity.Id);
    }

    private static string HistoryMetadata(FileHistoryEvidence? history) => history == null
        ? string.Empty
        : ",\"gitCommitCount\":" + history.CommitCount
          + ",\"gitLastCommitUnix\":" + history.LastCommitUnix;

    private static string[] DecodeLines(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines.ToArray();
    }

    private static string? ParseContentHash(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(metadataJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("contentHash", out JsonElement hash)
                   && hash.ValueKind == JsonValueKind.String
                ? hash.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ComputeContentHash(string root, string relativePath)
    {
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (new FileInfo(fullPath).Length > MaximumIndexedFileBytes) return null;
            using FileStream stream = File.OpenRead(fullPath);
            if (stream.Length > MaximumIndexedFileBytes) return null;
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new IOException("Could not read repository file for indexing: " + relativePath, ex);
        }
    }

    private Dictionary<string, IndexedFile> LoadIndexedFiles(string repoRoot, string root, IEnumerable<string>? relativePaths = null)
    {
        string prefix = RepoScope(repoRoot);
        var result = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        List<ContextEntityDto> records = relativePaths == null
            ? _graph.ListEntitiesBySourcePrefix(EntityTypeFile, prefix)
            : _graph.ListFileEntitiesByRelativePaths(prefix, relativePaths);
        foreach (ContextEntityDto entity in records)
        {
            string relative = entity.Name.Replace('\\', '/');
            RecordIndexedFilePathLoadedForTest(relative);
            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(full)) result[relative] = new IndexedFile(relative, full, entity.Id);
            }
            catch { }
        }
        return result;
    }

    private static readonly string[] TestSuffixes = { "tests", "test", ".test", ".spec", "_test", "_tests" };

    private static readonly string[] TopologyRelationTypes = { RelationTests, RelationDependsOn, RelationCalls };

    /// <summary>
    /// Derives the complete TESTS / DEPENDS_ON / CALLS edge set from the persisted per-file topology
    /// inputs (raw import specifiers and call identifiers, written atomically with each file projection)
    /// and the current path/symbol universe, then applies only the difference to the stored edges.
    /// Resolution depends on global facts (path existence, unique symbol names, per-file edge budgets), so
    /// an add/remove elsewhere can create or invalidate edges of files whose own content is unchanged;
    /// recomputing against the whole universe keeps incremental state identical to a full rebuild. Only
    /// files with no valid stored inputs (pre-existing graphs, or an entity id that no longer matches) are
    /// re-read from disk, and their inputs are backfilled. Rows are streamed; nothing is kept per file.
    /// </summary>
    private (int Tests, int Dependencies, int Calls) LinkTopology(
        string repoRoot,
        string root,
        Dictionary<string, IndexedFile> files,
        bool replaceExisting,
        CancellationToken cancellationToken = default)
    {
        int tests = 0, dependencies = 0, calls = 0;
        HashSet<string> allPaths = files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string scope = RepoScope(repoRoot);

        long topoStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Dictionary<string, ContextEntityDto> uniqueSymbols = _graph.ListEntitiesBySourcePrefix(EntityTypeSymbol, scope)
            .GroupBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var symbolPath = uniqueSymbols.ToDictionary(
            pair => pair.Key,
            pair => ParsePathAndSignature(pair.Value.MetadataJson).RelativePath ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);

        PhaseObserver?.Invoke("topology-symbols", System.Diagnostics.Stopwatch.GetElapsedTime(topoStart));
        topoStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var desired = new Dictionary<(string From, string Type, string To), string>();
        foreach ((string sourcePath, IndexedFile source) in files)
        {
            string? codePath = TryResolveCodeFileForTest(sourcePath, allPaths);
            if (codePath != null && files.TryGetValue(codePath, out IndexedFile? code)
                && desired.TryAdd((source.EntityId, RelationTests, code.EntityId),
                    "{\"heuristic\":\"test-name-convention\"}"))
                tests++;
        }

        void Process(string sourcePath, IndexedFile source, IEnumerable<string> specs, IReadOnlyList<string> callNames)
        {
            int dependencyBudget = MaximumDependencyRelationsPerFile;
            foreach (string targetPath in ResolveImportSpecs(root, sourcePath, specs, allPaths))
            {
                if (dependencyBudget-- <= 0) break;
                if (string.Equals(targetPath, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!files.TryGetValue(targetPath, out IndexedFile? target)) continue;
                if (desired.TryAdd((source.EntityId, RelationDependsOn, target.EntityId),
                        "{\"heuristic\":\"resolved-local-import\"}"))
                    dependencies++;
            }

            int callBudget = MaximumCallRelationsPerFile;
            foreach (string callName in callNames)
            {
                if (callBudget <= 0) break;
                if (!uniqueSymbols.TryGetValue(callName, out ContextEntityDto? targetSymbol)) continue;
                string targetPath = symbolPath[callName];
                if (string.IsNullOrWhiteSpace(targetPath)
                    || string.Equals(targetPath, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (desired.TryAdd((source.EntityId, RelationCalls, targetSymbol.Id),
                        "{\"heuristic\":\"unique-call-identifier\",\"name\":" + JsonQuote(callName) + "}"))
                {
                    callBudget--;
                    calls++;
                }
            }
        }

        PhaseObserver?.Invoke("topology-tests", System.Diagnostics.Stopwatch.GetElapsedTime(topoStart));
        topoStart = System.Diagnostics.Stopwatch.GetTimestamp();
        // Pass 1: stored inputs, streamed. A row is only trusted when it belongs to the current file entity.
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ContextGraphStore.TopologyInputRow row in _graph.EnumerateTopologyInputs(scope))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = row.Source.Substring(scope.Length);
            if (!files.TryGetValue(path, out IndexedFile? file) || !string.Equals(file.EntityId, row.EntityId, StringComparison.Ordinal))
                continue;
            covered.Add(path);
            Process(path, file, ParseStringArray(row.ImportsJson), ParseStringArray(row.CallsJson));
        }

        PhaseObserver?.Invoke("topology-pass1", System.Diagnostics.Stopwatch.GetElapsedTime(topoStart));
        // Pass 2: files without valid stored inputs are read once and backfilled.
        var backfill = new List<(string Source, string EntityId, ContextGraphStore.TopologyInputs Inputs)>();
        foreach ((string sourcePath, IndexedFile source) in files)
        {
            if (covered.Contains(sourcePath)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            string[] lines;
            try { lines = File.ReadAllLines(source.FullPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            ContextGraphStore.TopologyInputs inputs = BuildTopologyInputs(sourcePath, lines);
            backfill.Add((FileSource(repoRoot, sourcePath), source.EntityId, inputs));
            Process(sourcePath, source, ParseStringArray(inputs.ImportsJson), ParseStringArray(inputs.CallsJson));
        }
        if (backfill.Count > 0) _graph.UpsertTopologyInputs(backfill);

        cancellationToken.ThrowIfCancellationRequested();
        return Timed("topology-apply", () =>
        {
            var staleIds = new List<string>();
            if (replaceExisting)
            {
                foreach (ContextGraphStore.ScopedRelation existing in _graph.ListFileRelationsByScope(scope, TopologyRelationTypes))
                {
                    if (!desired.Remove((existing.FromEntityId, existing.RelationType, existing.ToEntityId)))
                        staleIds.Add(existing.Id);
                }
            }

            var creates = desired.Select(pair => new CreateContextRelationRequest
            {
                FromEntityId = pair.Key.From,
                RelationType = pair.Key.Type,
                ToEntityId = pair.Key.To,
                Source = scope,
                MetadataJson = pair.Value
            }).ToList();
            _graph.ApplyRelationDelta(staleIds, creates);
            return (tests, dependencies, calls);
        });
    }

    private (int Tests, int Dependencies, int Calls) LinkTopologyIncremental(
        string repoRoot,
        string root,
        IReadOnlyCollection<string> affectedSourcePaths,
        IReadOnlyCollection<string> changedPaths,
        CancellationToken cancellationToken)
    {
        string scope = RepoScope(repoRoot);
        var sourcePaths = affectedSourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidatePaths = new HashSet<string>(sourcePaths, StringComparer.OrdinalIgnoreCase);
        candidatePaths.UnionWith(changedPaths);

        List<ContextGraphStore.TopologyInputRow> inputRows = _graph.ListTopologyInputsByRelativePaths(scope, sourcePaths);
        Interlocked.Add(ref _topologyInputRowsLoadedForTest, inputRows.Count);
        var inputsByPath = new Dictionary<string, ContextGraphStore.TopologyInputs>(StringComparer.OrdinalIgnoreCase);
        foreach (ContextGraphStore.TopologyInputRow row in inputRows)
        {
            if (!row.Source.StartsWith(scope, StringComparison.Ordinal)) continue;
            inputsByPath[row.Source[scope.Length..]] = new ContextGraphStore.TopologyInputs(row.ImportsJson, row.CallsJson);
        }

        foreach ((string sourcePath, ContextGraphStore.TopologyInputs inputs) in inputsByPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddImportResolutionCandidates(root, sourcePath, ParseStringArray(inputs.ImportsJson), scope, candidatePaths);
            candidatePaths.UnionWith(GetTestCodeCandidatePaths(sourcePath));
        }

        Dictionary<string, IndexedFile> files = LoadIndexedFiles(repoRoot, root, candidatePaths);
        var allPaths = files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = sourcePaths
            .Where(path => files.ContainsKey(path))
            .ToDictionary(path => path, path => files[path], StringComparer.OrdinalIgnoreCase);

        var backfill = new List<(string Source, string EntityId, ContextGraphStore.TopologyInputs Inputs)>();
        // Indexed once: a linear scan of the rows per source file made this loop quadratic in repository size.
        var rowBySource = new Dictionary<string, ContextGraphStore.TopologyInputRow>(StringComparer.Ordinal);
        foreach (ContextGraphStore.TopologyInputRow item in inputRows) rowBySource.TryAdd(item.Source, item);
        foreach ((string sourcePath, IndexedFile source) in sourceFiles)
        {
            if (inputsByPath.TryGetValue(sourcePath, out ContextGraphStore.TopologyInputs? stored))
            {
                rowBySource.TryGetValue(FileSource(repoRoot, sourcePath), out ContextGraphStore.TopologyInputRow? row);
                if (row != null && string.Equals(row.EntityId, source.EntityId, StringComparison.Ordinal)) continue;
                inputsByPath.Remove(sourcePath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string[] lines;
            try { lines = File.ReadAllLines(source.FullPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            ContextGraphStore.TopologyInputs rebuilt = BuildTopologyInputs(sourcePath, lines);
            inputsByPath[sourcePath] = rebuilt;
            backfill.Add((FileSource(repoRoot, sourcePath), source.EntityId, rebuilt));
        }
        if (backfill.Count > 0) _graph.UpsertTopologyInputs(backfill);

        int tests = 0, dependencies = 0, calls = 0;
        var desired = new Dictionary<(string From, string Type, string To), string>();
        var uniqueSymbolCache = new Dictionary<string, ContextEntityDto?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string sourcePath, IndexedFile source) in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inputsByPath.TryGetValue(sourcePath, out ContextGraphStore.TopologyInputs? inputs)) continue;

            string? codePath = TryResolveCodeFileForTest(sourcePath, allPaths);
            if (codePath != null && files.TryGetValue(codePath, out IndexedFile? code)
                && desired.TryAdd((source.EntityId, RelationTests, code.EntityId),
                    "{\"heuristic\":\"test-name-convention\"}"))
                tests++;

            int dependencyBudget = MaximumDependencyRelationsPerFile;
            foreach (string targetPath in ResolveImportSpecs(root, sourcePath, ParseStringArray(inputs.ImportsJson), allPaths))
            {
                if (dependencyBudget-- <= 0) break;
                if (string.Equals(targetPath, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!files.TryGetValue(targetPath, out IndexedFile? target)) continue;
                if (desired.TryAdd((source.EntityId, RelationDependsOn, target.EntityId),
                        "{\"heuristic\":\"resolved-local-import\"}"))
                    dependencies++;
            }

            int callBudget = MaximumCallRelationsPerFile;
            foreach (string callName in ParseStringArray(inputs.CallsJson))
            {
                if (callBudget <= 0) break;
                if (!uniqueSymbolCache.TryGetValue(callName, out ContextEntityDto? targetSymbol))
                {
                    List<ContextEntityDto> candidates = _graph.ListSymbolCandidatesByName(scope, callName);
                    targetSymbol = candidates.Count == 1 ? candidates[0] : null;
                    uniqueSymbolCache[callName] = targetSymbol;
                }
                if (targetSymbol == null) continue;
                string targetPath = ParsePathAndSignature(targetSymbol.MetadataJson).RelativePath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(targetPath)
                    || string.Equals(targetPath, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (desired.TryAdd((source.EntityId, RelationCalls, targetSymbol.Id),
                        "{\"heuristic\":\"unique-call-identifier\",\"name\":" + JsonQuote(callName) + "}"))
                {
                    callBudget--;
                    calls++;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Timed("topology-apply", () =>
        {
            string[] sourceIds = sourceFiles.Values.Select(file => file.EntityId).ToArray();
            var staleIds = new List<string>();
            foreach (ContextGraphStore.ScopedRelation existing in _graph.ListFileRelationsByFromEntityIds(scope, sourceIds, TopologyRelationTypes))
                if (!desired.Remove((existing.FromEntityId, existing.RelationType, existing.ToEntityId)))
                    staleIds.Add(existing.Id);

            var creates = desired.Select(pair => new CreateContextRelationRequest
            {
                FromEntityId = pair.Key.From,
                RelationType = pair.Key.Type,
                ToEntityId = pair.Key.To,
                Source = scope,
                MetadataJson = pair.Value
            }).ToList();
            _graph.ApplyRelationDelta(staleIds, creates);
            return (tests, dependencies, calls);
        });
    }

    private static void AddSourcePaths(HashSet<string> destination, string scope, IEnumerable<string> sources)
    {
        foreach (string source in sources)
            if (source.StartsWith(scope, StringComparison.Ordinal))
                destination.Add(source[scope.Length..].Replace('\\', '/'));
    }

    private void AddImportReferrerPaths(HashSet<string> destination, string root, string scope, IEnumerable<string> changedPaths)
    {
        string[] targets = changedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targets.Length == 0) return;

        foreach (ContextGraphStore.TopologyImportReferrer reference in
                 _graph.FindTopologyImportReferrers(scope, BuildImportPathTokens(targets)))
        {
            if (!reference.Source.StartsWith(scope, StringComparison.Ordinal)) continue;
            string importerPath = reference.Source[scope.Length..].Replace('\\', '/');
            if (ImportSpecTargetsAnyPath(root, scope, importerPath, reference.ImportSpec, targets))
                destination.Add(importerPath);
        }
    }

    private bool ImportSpecTargetsAnyPath(string root, string scope, string sourcePath, string spec, IReadOnlyCollection<string> targetPaths)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddImportResolutionCandidates(root, sourcePath, new[] { spec }, scope, candidates);
        if (!candidates.Overlaps(targetPaths)) return false;

        List<string> existingPaths = _graph.ListFilePathsByRelativePaths(scope, candidates);
        foreach (string path in existingPaths) RecordIndexedFilePathLoadedForTest(path);
        var existingSet = existingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ResolveImportSpecs(root, sourcePath, new[] { spec }, existingSet)
            .Any(path => targetPaths.Contains(path, StringComparer.OrdinalIgnoreCase));
    }

    private void RecordIndexedFilePathLoadedForTest(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        lock (_indexedFileLoadCounterGate)
            _indexedFilePathsLoadedForTest.Add(relativePath.Replace('\\', '/'));
    }

    private static IReadOnlyList<string> BuildImportPathTokens(IEnumerable<string> relativePaths)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in relativePaths)
        {
            string normalized = path.Replace('\\', '/').Trim('/');
            string fileName = Path.GetFileName(normalized);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            if (fileName.Length >= 2) tokens.Add(fileName);
            if (stem.Length >= 1) tokens.Add(stem);
            string parentName = Path.GetFileName(Path.GetDirectoryName(normalized)) ?? string.Empty;
            if (fileName.Equals("__init__.py", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("index", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("mod.rs", StringComparison.OrdinalIgnoreCase))
            {
                if (parentName.Length >= 1) tokens.Add(parentName);
                if (fileName.Equals("__init__.py", StringComparison.OrdinalIgnoreCase)) tokens.Add(".");
            }
        }
        return tokens.ToArray();
    }

    private static IReadOnlyCollection<string> BuildTestCounterpartPaths(string relativePath)
    {
        var candidates = new HashSet<string>(GetTestCodeCandidatePaths(relativePath), StringComparer.OrdinalIgnoreCase);
        string fileName = Path.GetFileNameWithoutExtension(relativePath);
        string extension = Path.GetExtension(relativePath);
        string directory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
        string[] directories = new[] { directory }.Concat(GetMirroredTestDirectories(directory)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] extensions = IsCppExtension(extension)
            ? new[] { ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx" }
            : new[] { extension };

        foreach (string dir in directories)
        foreach (string ext in extensions)
        foreach (string suffix in TestSuffixes)
        {
            string testPrefix = suffix.TrimStart('.', '_') + "_";
            candidates.Add(CombineRelative(dir, fileName + suffix + ext));
            candidates.Add(CombineRelative(dir, testPrefix + fileName + ext));
        }

        foreach (string testDirectory in new[] { "tests", "test", "__tests__", "Tests" })
        foreach (string ext in extensions)
        foreach (string suffix in TestSuffixes)
            candidates.Add(CombineRelative(testDirectory, fileName + suffix + ext));

        return candidates.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
    }

    private static IReadOnlyCollection<string> GetMirroredTestDirectories(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return Array.Empty<string>();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string testDir in new[] { "tests", "test", "__tests__", "Tests" })
            if (Regex.IsMatch(directory, "(^|/)src(/|$)", RegexOptions.IgnoreCase))
                result.Add(Regex.Replace(directory, "(^|/)src(?=/|$)", "$1" + testDir, RegexOptions.IgnoreCase));
        return result;
    }

    private static IReadOnlyCollection<string> GetTestCodeCandidatePaths(string testRelativePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(testRelativePath);
        string extension = Path.GetExtension(testRelativePath);
        string directory = Path.GetDirectoryName(testRelativePath)?.Replace('\\', '/') ?? string.Empty;
        string? candidateBaseName = null;
        foreach (string suffix in TestSuffixes.OrderByDescending(value => value.Length))
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && fileName.Length > suffix.Length)
            {
                candidateBaseName = fileName[..^suffix.Length];
                break;
            }
            string prefix = suffix.TrimStart('.', '_') + "_";
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && fileName.Length > prefix.Length)
            {
                candidateBaseName = fileName[prefix.Length..];
                break;
            }
        }
        candidateBaseName = candidateBaseName?.TrimEnd('.', '_');
        if (string.IsNullOrWhiteSpace(candidateBaseName)) return Array.Empty<string>();

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] extensions = IsCppExtension(extension)
            ? new[] { ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx" }
            : new[] { extension };
        string[] directories = new[] { directory }.Concat(GetMirroredSourceDirectories(directory)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string dir in directories)
        foreach (string ext in extensions)
            candidates.Add(CombineRelative(dir, candidateBaseName + ext));
        return candidates;
    }

    private static IReadOnlyCollection<string> GetMirroredSourceDirectories(string directory)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string testDir in new[] { "tests", "test", "__tests__", "Tests" })
            if (directory.Contains(testDir, StringComparison.OrdinalIgnoreCase))
                result.Add(Regex.Replace(directory, Regex.Escape(testDir), "src", RegexOptions.IgnoreCase));
        return result;
    }

    private static bool IsCppExtension(string extension) => extension is ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" or ".hxx";

    private void AddImportResolutionCandidates(
        string root,
        string sourcePath,
        IEnumerable<string> specs,
        string scope,
        HashSet<string> candidates)
    {
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        bool moduleName = extension is ".rs" or ".java" or ".kt";
        string sourceDirectory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
        foreach (string spec in specs)
        {
            if (moduleName && extension == ".rs")
            {
                string stem = spec.Replace("::", "/").Replace('.', '/');
                candidates.Add(CombineRelative(sourceDirectory, stem + ".rs"));
                candidates.Add(CombineRelative(sourceDirectory, stem + "/mod.rs"));
                candidates.Add(stem + ".rs");
                candidates.Add(stem + "/mod.rs");
                continue;
            }
            if (moduleName)
            {
                string simpleName = spec.Split('.').LastOrDefault() ?? string.Empty;
                string expectedExtension = extension == ".kt" ? ".kt" : ".java";
                if (simpleName.Length > 0 && simpleName != "*")
                {
                    List<string> namedPaths = _graph.ListFilePathsByName(scope, simpleName + expectedExtension);
                    foreach (string path in namedPaths) RecordIndexedFilePathLoadedForTest(path);
                    candidates.UnionWith(namedPaths);
                }
                continue;
            }

            string normalizedSpec = spec.Replace('\\', '/');
            if (extension == ".py" && normalizedSpec.StartsWith(".", StringComparison.Ordinal))
            {
                int dots = normalizedSpec.TakeWhile(ch => ch == '.').Count();
                string remainder = normalizedSpec[dots..].Replace('.', '/');
                string dir = sourceDirectory;
                for (int index = 1; index < dots; index++) dir = ParentRelative(dir);
                normalizedSpec = CombineRelative(dir, remainder);
            }
            else normalizedSpec = CombineRelative(sourceDirectory, normalizedSpec);

            string? canonical = NormalizeWithinRoot(root, normalizedSpec);
            if (canonical != null)
            {
                candidates.Add(canonical);
                if (string.IsNullOrEmpty(Path.GetExtension(canonical)))
                {
                    if (!string.IsNullOrEmpty(extension)) candidates.Add(canonical + extension);
                    foreach (string ext in IndexableExtensions) candidates.Add(canonical + ext);
                    foreach (string ext in IndexableExtensions) candidates.Add(canonical.TrimEnd('/') + "/index" + ext);
                    if (extension == ".py") candidates.Add(canonical.TrimEnd('/') + "/__init__.py");
                }
            }

            if (IsCppExtension(extension))
            {
                string? rootRelative = NormalizeWithinRoot(root, spec.Replace('\\', '/'));
                if (rootRelative != null) candidates.Add(rootRelative);
            }
        }
    }

    private static readonly Regex CppInclude = new(
        "^\\s*#\\s*include\\s*\"([^\"]+)\"", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex JsRelativeImport = new(
        "(?:\\bfrom\\s*|\\bimport\\s*\\(\\s*|\\brequire\\s*\\(\\s*)['\"](\\.[^'\"]+)['\"]",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex PythonRelativeImport = new(
        @"^\s*from\s+(\.+[A-Za-z0-9_.]*)\s+import\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex RubyRelativeRequire = new(
        "^\\s*require_relative\\s+['\"]([^'\"]+)['\"]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex RustModule = new(
        @"^\s*(?:pub\s+)?mod\s+([A-Za-z_]\w*)\s*;", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex JavaKotlinImport = new(
        @"^\s*import\s+([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex CallIdentifier = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>\r\n]{0,120}>)?\s*\(",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private const int MaximumImportSpecsPerFile = 2048;

    /// <summary>Raw local-import specifiers of a file in first-occurrence order (resolution-independent).</summary>
    internal static IReadOnlyList<string> ExtractImportSpecs(string sourcePath, string[] lines)
    {
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var specs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines.Take(20_000))
        {
            Match match = Match.Empty;
            try
            {
                if (extension is ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".hh" or ".hpp" or ".hxx")
                    match = CppInclude.Match(line);
                else if (extension is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".mts" or ".cts")
                    match = JsRelativeImport.Match(line);
                else if (extension == ".py")
                    match = PythonRelativeImport.Match(line);
                else if (extension == ".rb")
                    match = RubyRelativeRequire.Match(line);
                else if (extension == ".rs")
                    match = RustModule.Match(line);
                else if (extension is ".java" or ".kt")
                    match = JavaKotlinImport.Match(line);
            }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success || match.Groups.Count < 2) continue;
            string spec = match.Groups[1].Value.Trim();
            if (seen.Add(spec)) specs.Add(spec);
            if (specs.Count >= MaximumImportSpecsPerFile) break;
        }
        return specs;
    }

    /// <summary>Resolves stored specifiers against the current path universe (the part that depends on other files).</summary>
    internal static IReadOnlyList<string> ResolveImportSpecs(
        string root,
        string sourcePath,
        IEnumerable<string> specs,
        IReadOnlySet<string> allPaths)
    {
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        bool moduleName = extension is ".rs" or ".java" or ".kt";
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string spec in specs)
        {
            string? target = moduleName
                ? ResolveModuleName(sourcePath, spec, extension, allPaths)
                : ResolvePathImport(root, sourcePath, spec, extension, allPaths);
            if (target != null) resolved.Add(target);
            if (resolved.Count >= MaximumDependencyRelationsPerFile) break;
        }
        return resolved.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static IReadOnlyList<string> ExtractLocalDependencies(
        string root,
        string sourcePath,
        string[] lines,
        IReadOnlySet<string> allPaths) =>
        ResolveImportSpecs(root, sourcePath, ExtractImportSpecs(sourcePath, lines), allPaths);

    private static ContextGraphStore.TopologyInputs BuildTopologyInputs(string relativePath, string[] lines) =>
        new(JsonSerializer.Serialize(ExtractImportSpecs(relativePath, lines)),
            JsonSerializer.Serialize(ExtractCallIdentifiers(lines)));

    private static string[] ParseStringArray(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    private static string? ResolveModuleName(
        string sourcePath,
        string spec,
        string extension,
        IReadOnlySet<string> allPaths)
    {
        string sourceDirectory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
        if (extension == ".rs")
        {
            string stem = spec.Replace("::", "/").Replace('.', '/');
            foreach (string candidate in new[]
                     {
                         CombineRelative(sourceDirectory, stem + ".rs"),
                         CombineRelative(sourceDirectory, stem + "/mod.rs"),
                         stem + ".rs",
                         stem + "/mod.rs"
                     })
                if (allPaths.Contains(candidate)) return candidate;
            return null;
        }

        string simpleName = spec.Split('.').LastOrDefault() ?? string.Empty;
        if (simpleName.Length == 0 || simpleName == "*") return null;
        string expectedExtension = extension == ".kt" ? ".kt" : ".java";
        List<string> matches = allPaths.Where(path =>
                string.Equals(Path.GetExtension(path), expectedExtension, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileNameWithoutExtension(path), simpleName, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string? ResolvePathImport(
        string root,
        string sourcePath,
        string spec,
        string sourceExtension,
        IReadOnlySet<string> allPaths)
    {
        string sourceDirectory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
        string normalizedSpec = spec.Replace('\\', '/');
        if (sourceExtension == ".py" && normalizedSpec.StartsWith(".", StringComparison.Ordinal))
        {
            int dots = normalizedSpec.TakeWhile(ch => ch == '.').Count();
            string remainder = normalizedSpec[dots..].Replace('.', '/');
            string dir = sourceDirectory;
            for (int i = 1; i < dots; i++) dir = ParentRelative(dir);
            normalizedSpec = CombineRelative(dir, remainder);
        }
        else
        {
            normalizedSpec = CombineRelative(sourceDirectory, normalizedSpec);
        }

        string? canonicalRelative = NormalizeWithinRoot(root, normalizedSpec);
        if (canonicalRelative == null) return null;

        var candidates = new List<string> { canonicalRelative };
        if (string.IsNullOrEmpty(Path.GetExtension(canonicalRelative)))
        {
            if (!string.IsNullOrEmpty(sourceExtension)) candidates.Add(canonicalRelative + sourceExtension);
            foreach (string ext in IndexableExtensions) candidates.Add(canonicalRelative + ext);
            foreach (string ext in IndexableExtensions) candidates.Add(canonicalRelative.TrimEnd('/') + "/index" + ext);
            if (sourceExtension == ".py") candidates.Add(canonicalRelative.TrimEnd('/') + "/__init__.py");
        }
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (allPaths.Contains(candidate)) return candidate;

        // Quoted C/C++ includes are often repository-root relative rather than source-file relative.
        if (sourceExtension is ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".hh" or ".hpp" or ".hxx")
        {
            string? rootRelative = NormalizeWithinRoot(root, spec.Replace('\\', '/'));
            if (rootRelative != null && allPaths.Contains(rootRelative)) return rootRelative;
        }
        return null;
    }

    private static string? NormalizeWithinRoot(string root, string relative)
    {
        try
        {
            string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(canonicalRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            string prefix = canonicalRoot + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetRelativePath(canonicalRoot, full).Replace('\\', '/');
        }
        catch { return null; }
    }

    private static string ParentRelative(string value)
    {
        string normalized = value.Replace('\\', '/').TrimEnd('/');
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    private static string CombineRelative(string left, string right)
    {
        string l = left.Replace('\\', '/').Trim('/');
        string r = right.Replace('\\', '/').TrimStart('/');
        return l.Length == 0 ? r : r.Length == 0 ? l : l + "/" + r;
    }

    internal static IReadOnlyList<string> ExtractCallIdentifiers(string[] lines)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Take(30_000))
        {
            MatchCollection matches;
            try { matches = CallIdentifier.Matches(line); }
            catch (RegexMatchTimeoutException) { continue; }
            foreach (Match match in matches)
            {
                string name = match.Groups[1].Value;
                if (name.Length < 3 || ControlFlowCallWords.Contains(name)) continue;
                result.Add(name);
                if (result.Count >= MaximumCallRelationsPerFile * 4) break;
            }
            if (result.Count >= MaximumCallRelationsPerFile * 4) break;
        }
        return result.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly HashSet<string> ControlFlowCallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "nameof", "typeof", "sizeof",
        "return", "new", "match", "when", "with", "assert"
    };

    internal static string? TryResolveCodeFileForTest(string testRelativePath, IEnumerable<string> allRelativePaths)
    {
        string fileName = Path.GetFileNameWithoutExtension(testRelativePath);
        string extension = Path.GetExtension(testRelativePath);
        string directory = Path.GetDirectoryName(testRelativePath)?.Replace('\\', '/') ?? string.Empty;
        string? candidateBaseName = null;

        foreach (string suffix in TestSuffixes.OrderByDescending(value => value.Length))
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && fileName.Length > suffix.Length)
            {
                candidateBaseName = fileName[..^suffix.Length];
                break;
            }
            string prefix = suffix.TrimStart('.', '_') + "_";
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && fileName.Length > prefix.Length)
            {
                candidateBaseName = fileName[prefix.Length..];
                break;
            }
        }
        candidateBaseName = candidateBaseName?.TrimEnd('.', '_');
        if (string.IsNullOrWhiteSpace(candidateBaseName)) return null;

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddCandidate(string dir, string name, string ext) =>
            candidates.Add(string.IsNullOrEmpty(dir) ? name + ext : dir + "/" + name + ext);
        AddCandidate(directory, candidateBaseName, extension);
        foreach (string testDirName in new[] { "tests", "test", "__tests__", "Tests" })
        {
            if (!directory.Contains(testDirName, StringComparison.OrdinalIgnoreCase)) continue;
            string mirrored = Regex.Replace(directory, Regex.Escape(testDirName), "src", RegexOptions.IgnoreCase);
            AddCandidate(mirrored, candidateBaseName, extension);
        }

        if (extension is ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" or ".hxx")
        {
            foreach (string ext in new[] { ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx" })
            {
                AddCandidate(directory, candidateBaseName, ext);
                foreach (string testDirName in new[] { "tests", "test", "Tests" })
                {
                    if (!directory.Contains(testDirName, StringComparison.OrdinalIgnoreCase)) continue;
                    string mirrored = Regex.Replace(directory, Regex.Escape(testDirName), "src", RegexOptions.IgnoreCase);
                    AddCandidate(mirrored, candidateBaseName, ext);
                }
            }
        }
        return allRelativePaths.FirstOrDefault(path => candidates.Contains(path));
    }

    private static readonly TimeSpan SymbolMatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex CSharpTypeDeclaration = new(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected)\s+)?(?:static\s+|sealed\s+|abstract\s+|partial\s+)*(?:class|interface|struct|record|enum)\s+(\w+)",
        RegexOptions.Compiled, SymbolMatchTimeout);
    private static readonly Regex CSharpMemberDeclaration = new(
        @"^\s*(?:public|internal|private|protected)\s+(?:static\s+|sealed\s+|virtual\s+|override\s+|async\s+|readonly\s+)*[\w<>\[\],\.\? ]+?\s(\w+)\s*\([^;{=]*\)\s*(?:=>|\{|$)",
        RegexOptions.Compiled, SymbolMatchTimeout);
    private static readonly Regex JavaScriptDeclaration = new(
        @"^\s*(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(?:async\s+)?(?:function\*?\s+(\w+)|class\s+(\w+)|interface\s+(\w+)|(?:const\s+)?enum\s+(\w+)|type\s+(\w+)\s*(?:<[^=]*>)?\s*=|(?:const|let|var)\s+(\w+)\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*(?::[^=]+)?=>|\w+\s*=>)|(?:(?:public|private|protected|static|async|readonly|override|abstract|get|set)\s+)*\*?(\w+)\s*(?:<[^>]*>)?\s*\([^)]*\)\s*(?::\s*[^{;=]+?)?\s*\{)",
        RegexOptions.Compiled, SymbolMatchTimeout);
    private static readonly Regex PythonDeclaration = new(
        @"^\s*(?:async\s+)?def\s+(\w+)|^\s*class\s+(\w+)",
        RegexOptions.Compiled, SymbolMatchTimeout);
    private static readonly Regex JavaDeclaration = new(
        @"^\s*(?:(?:public|protected|private|static|final|abstract|sealed|non-sealed|synchronized|native|strictfp)\s+)*(?:(?:class|interface|enum|record)\s+(\w+)|(?:<[^>]+>\s*)?[\w<>,.?\[\]]+\s+(\w+)\s*\([^;{}]*\)\s*(?:throws\s+[\w.,\s]+)?(?:\{|$))",
        RegexOptions.Compiled, SymbolMatchTimeout);
    private static readonly Regex KotlinDeclaration = new(
        @"^\s*(?:(?:public|private|protected|internal|open|final|abstract|sealed|data|enum|annotation|value|expect|actual|suspend|inline|tailrec|operator|infix|external|override)\s+)*(?:(?:class|interface|object)\s+(\w+)|fun\s+(?:<[^>]+>\s*)?(?:[\w<>,.?]+\.)?(\w+)\s*\()",
        RegexOptions.Compiled, SymbolMatchTimeout);
    private static readonly Regex GoDeclaration = new(
        @"^\s*(?:func\s+(?:\([^)]*\)\s*)?(\w+)\s*\(|type\s+(\w+)\s+(?:struct|interface)\b)",
        RegexOptions.Compiled, SymbolMatchTimeout);
    private static readonly Regex RubyDeclaration = new(
        @"^\s*(?:(?:class|module)\s+([A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)|def\s+(?:self\.)?([A-Za-z_]\w*[!?=]?))",
        RegexOptions.Compiled, SymbolMatchTimeout);
    private static readonly Regex CppTypeDeclaration = new(
        @"^\s*(?:(?:template\s*<[^;{}]+>\s*)?)(?:class|struct|union|enum(?:\s+class)?)\s+([A-Za-z_]\w*)",
        RegexOptions.Compiled, SymbolMatchTimeout);
    private static readonly Regex CppFunctionDeclaration = new(
        @"^\s*(?:(?:inline|static|virtual|constexpr|consteval|constinit|extern|friend|explicit)\s+)*(?!if\b|for\b|while\b|switch\b|catch\b)(?:[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*(?:\s*[*&]+)?(?:\s*<[^;{}()]+>)?\s+)+(?:(?:[A-Za-z_]\w*::)*~?([A-Za-z_]\w*)|operator\s*([^\s(]+))\s*\([^;{}]*\)\s*(?:const\s*)?(?:noexcept(?:\([^)]*\))?\s*)?(?:->\s*[^;{]+)?(?:\{|;|$)",
        RegexOptions.Compiled, SymbolMatchTimeout);
    private static readonly Regex RustDeclaration = new(
        @"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:(?:async|unsafe|const|extern(?:\s+""[^""]+"")?)\s+)*(?:fn\s+([A-Za-z_]\w*)|(?:struct|enum|trait|union|type|mod)\s+([A-Za-z_]\w*))",
        RegexOptions.Compiled, SymbolMatchTimeout);

    internal static List<(string Name, string Signature, int LineNumber)> ExtractSymbols(string relativePath, string[] lines)
    {
        string extension = Path.GetExtension(relativePath).ToLowerInvariant();
        Func<string, Match> matcher = extension switch
        {
            ".cs" => SafeMatchCSharp,
            ".py" => line => SafeMatch(PythonDeclaration, line),
            ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".mts" or ".cts" => SafeMatchJavaScript,
            ".java" => line => SafeMatch(JavaDeclaration, line),
            ".kt" => line => SafeMatch(KotlinDeclaration, line),
            ".go" => line => SafeMatch(GoDeclaration, line),
            ".rb" => line => SafeMatch(RubyDeclaration, line),
            ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".hh" or ".hpp" or ".hxx" => SafeMatchCpp,
            ".rs" => line => SafeMatch(RustDeclaration, line),
            _ => _ => Match.Empty
        };

        var results = new List<(string Name, string Signature, int LineNumber)>();
        foreach ((string text, int lineNumber) in JoinContinuationLines(lines))
        {
            Match match = matcher(text);
            if (!match.Success) continue;
            string? name = match.Groups.Cast<Group>().Skip(1).FirstOrDefault(group => group.Success)?.Value;
            if (string.IsNullOrWhiteSpace(name)) continue;
            results.Add((name, Truncate(text.Trim(), 300), lineNumber));
        }
        return results;
    }

    private static Match SafeMatchCSharp(string line)
    {
        Match typeMatch = SafeMatch(CSharpTypeDeclaration, line);
        return typeMatch.Success ? typeMatch : SafeMatch(CSharpMemberDeclaration, line);
    }

    private static readonly HashSet<string> JavaScriptNonSymbolWords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "with", "function", "return", "super", "do", "else", "constructor"
    };

    private static Match SafeMatchJavaScript(string line)
    {
        Match match = SafeMatch(JavaScriptDeclaration, line);
        if (!match.Success) return match;
        string? name = match.Groups.Cast<Group>().Skip(1).FirstOrDefault(group => group.Success)?.Value;
        return name != null && JavaScriptNonSymbolWords.Contains(name) ? Match.Empty : match;
    }

    private static Match SafeMatchCpp(string line)
    {
        Match typeMatch = SafeMatch(CppTypeDeclaration, line);
        return typeMatch.Success ? typeMatch : SafeMatch(CppFunctionDeclaration, line);
    }

    private static Match SafeMatch(Regex regex, string text)
    {
        try { return regex.Match(text); }
        catch (RegexMatchTimeoutException) { return Match.Empty; }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private static List<(string Text, int LineNumber)> JoinContinuationLines(string[] lines)
    {
        const int maxContinuationLines = 8;
        var merged = new List<(string, int)>();
        int i = 0;
        while (i < lines.Length)
        {
            string text = lines[i];
            int lineNumber = i + 1;
            int balance = CountChar(text, '(') - CountChar(text, ')');
            int continuations = 0;
            while (balance > 0 && i + 1 < lines.Length && continuations < maxContinuationLines)
            {
                i++;
                text += " " + lines[i].Trim();
                balance += CountChar(lines[i], '(') - CountChar(lines[i], ')');
                continuations++;
            }
            merged.Add((text, lineNumber));
            i++;
        }
        return merged;
    }

    private static int CountChar(string text, char value)
    {
        int count = 0;
        foreach (char character in text) if (character == value) count++;
        return count;
    }

    public RepoContextPack SearchContext(string repoRoot, string goalText, int maxFiles = 8, int maxSymbols = 15)
    {
        if (string.IsNullOrWhiteSpace(goalText))
            return new RepoContextPack { RelevantFiles = new List<string>(), RelevantSymbols = new List<string>() };

        maxFiles = Math.Clamp(maxFiles, 1, 100);
        maxSymbols = Math.Clamp(maxSymbols, 1, 200);
        string prefix = RepoScope(repoRoot);
        MarkUsed(repoRoot);
        HashSet<string> goalTokens = Tokenize(goalText);
        float[]? goalEmbedding = _embeddings?.TryEmbed(goalText);

        List<string> files = _graph.ListEntitiesBySourcePrefix(EntityTypeFile, prefix)
            .Select(entity => (Entity: entity, Lexical: Overlap(goalTokens, Tokenize(entity.Name))))
            .Where(item => item.Lexical > 0)
            .Select(item => (Path: item.Entity.Name, Score: item.Lexical + HistoryBoost(item.Entity.MetadataJson)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Take(maxFiles)
            .Select(item => item.Path)
            .ToList();

        List<string> symbols = _graph.ListEntitiesBySourcePrefix(EntityTypeSymbol, prefix)
            .Select(entity => (Entity: entity, Score: ScoreSymbol(entity, goalTokens, goalEmbedding)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Entity.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxSymbols)
            .Select(item => DescribeSymbol(item.Entity))
            .ToList();

        return new RepoContextPack { RelevantFiles = files, RelevantSymbols = symbols };
    }

    private double ScoreSymbol(ContextEntityDto entity, HashSet<string> goalTokens, float[]? goalEmbedding)
    {
        double keywordScore = Overlap(goalTokens, Tokenize(entity.Name));
        double relevance = keywordScore;
        if (goalEmbedding != null && entity.MetadataJson != null)
        {
            float[]? symbolEmbedding = ExtractEmbeddingFromMetadata(entity.MetadataJson);
            if (symbolEmbedding != null)
            {
                float similarity = EmbeddingService.CosineSimilarity(goalEmbedding, symbolEmbedding);
                if (similarity >= EmbeddingService.RelevanceThreshold)
                    relevance = similarity + keywordScore * 0.1;
            }
        }
        return relevance <= 0 ? 0 : relevance + HistoryBoost(entity.MetadataJson);
    }

    private static double HistoryBoost(string? metadataJson)
    {
        (int commits, long lastUnix) = ParseHistory(metadataJson);
        if (commits <= 0 || lastUnix <= 0) return 0;
        double frequency = Math.Min(Math.Log2(commits + 1) * 0.12, 0.60);
        double ageDays = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastUnix) / 86_400d);
        double recency = Math.Exp(-ageDays / 90d) * 0.45;
        return frequency + recency;
    }

    private static (int CommitCount, long LastCommitUnix) ParseHistory(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return (0, 0);
        try
        {
            using JsonDocument document = JsonDocument.Parse(metadataJson);
            int commits = document.RootElement.TryGetProperty("gitCommitCount", out JsonElement count) && count.TryGetInt32(out int c) ? c : 0;
            long last = document.RootElement.TryGetProperty("gitLastCommitUnix", out JsonElement timestamp) && timestamp.TryGetInt64(out long t) ? t : 0;
            return (Math.Max(0, commits), Math.Max(0, last));
        }
        catch (JsonException) { return (0, 0); }
    }

    private static string DescribeSymbol(ContextEntityDto entity)
    {
        (string? relativePath, string? signature) = ParsePathAndSignature(entity.MetadataJson);
        return string.IsNullOrEmpty(relativePath) ? entity.Name : relativePath + ": " + (signature ?? entity.Name);
    }

    private static (string? RelativePath, string? Signature) ParsePathAndSignature(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return (null, null);
        try
        {
            using JsonDocument document = JsonDocument.Parse(metadataJson);
            string? path = document.RootElement.TryGetProperty("relativePath", out JsonElement p) ? p.GetString() : null;
            string? signature = document.RootElement.TryGetProperty("signature", out JsonElement s) ? s.GetString() : null;
            return (path, signature);
        }
        catch (JsonException) { return (null, null); }
    }

    private static float[]? ExtractEmbeddingFromMetadata(string metadataJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(metadataJson);
            if (!document.RootElement.TryGetProperty("embeddingBase64", out JsonElement element)) return null;
            string? base64 = element.GetString();
            return base64 == null ? null : EmbeddingService.FromBytes(Convert.FromBase64String(base64));
        }
        catch { return null; }
    }

    private static string? EncodeBase64OrNull(float[]? embedding) =>
        embedding == null ? null : Convert.ToBase64String(EmbeddingService.ToBytes(embedding));
    private static string JsonQuote(string value) => JsonSerializer.Serialize(value);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "with", "is", "are", "this", "that", "it"
    };

    internal static HashSet<string> Tokenize(string text)
    {
        var words = new List<string>();
        foreach (string raw in Regex.Split(text, @"[^A-Za-z0-9]+"))
        {
            if (raw.Length == 0) continue;
            words.AddRange(SplitCamelCase(raw));
        }
        return words.Select(word => word.ToLowerInvariant())
            .Where(word => word.Length > 1 && !StopWords.Contains(word))
            .ToHashSet();
    }

    private static IEnumerable<string> SplitCamelCase(string word)
    {
        var builder = new StringBuilder();
        var parts = new List<string>();
        foreach (char character in word)
        {
            if (builder.Length > 0 && char.IsUpper(character) && !char.IsUpper(builder[^1]))
            {
                parts.Add(builder.ToString());
                builder.Clear();
            }
            builder.Append(character);
        }
        if (builder.Length > 0) parts.Add(builder.ToString());
        parts.Add(word);
        return parts;
    }

    private static int Overlap(HashSet<string> goalTokens, HashSet<string> candidateTokens) =>
        goalTokens.Count(candidateTokens.Contains);

    private Dictionary<string, FileHistoryEvidence> ProbeGitHistory(string root)
    {
        Interlocked.Increment(ref _gitHistoryProbeCountForTest);
        var result = new Dictionary<string, FileHistoryEvidence>(StringComparer.OrdinalIgnoreCase);
        if (!PaRuX.Contracts.AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(root, out _)) return result;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[]
                     {
                         "log", "-n", MaximumGitCommitsToInspect.ToString(), "--format=@@PARUX:%ct",
                         "--name-only", "--no-renames", "--", "."
                     }) start.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = start };
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)GitProbeTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return result;
            }
            string output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0) return result;

            long currentTimestamp = 0;
            var seenInCommit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in output.Replace("\r\n", "\n").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("@@PARUX:", StringComparison.Ordinal))
                {
                    _ = long.TryParse(line[8..], out currentTimestamp);
                    seenInCommit.Clear();
                    continue;
                }
                if (line.Length == 0 || currentTimestamp <= 0) continue;
                string path = line.Replace('\\', '/');
                if (!seenInCommit.Add(path) || !IndexableExtensions.Contains(Path.GetExtension(path))) continue;
                if (result.TryGetValue(path, out FileHistoryEvidence? old))
                    result[path] = new FileHistoryEvidence(old.CommitCount + 1, Math.Max(old.LastCommitUnix, currentTimestamp));
                else
                    result[path] = new FileHistoryEvidence(1, currentTimestamp);
            }
        }
        catch { }
        return result;
    }

    private static IEnumerable<string> EnumerateIndexableFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string directory = stack.Pop();
            IEnumerable<string> subdirectories;
            try { subdirectories = Directory.EnumerateDirectories(directory); }
            catch { continue; }
            foreach (string subdirectory in subdirectories)
            {
                if (ExcludedDirectoryNames.Contains(Path.GetFileName(subdirectory))) continue;
                stack.Push(subdirectory);
            }

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory); }
            catch { continue; }
            foreach (string file in files)
                if (IndexableExtensions.Contains(Path.GetExtension(file))) yield return file;
        }
    }
}
