using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.ContextGraph;

/// <summary>
/// Persistent incremental coordinator over RepoIndexService's existing lexical + local-embedding
/// retrieval. V2 owns durable repository registration and file fingerprints. Normal add/update/
/// remove/rename syncs are applied as one explicit delta batch; only first sync or ForceFull clears
/// the repository graph. Project/config files are indexed as file evidence even when the heuristic
/// symbol extractor has no language-specific parser for them.
/// </summary>
public sealed class SemanticIndexV2Service
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> IndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".kt", ".go", ".rb",
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hh", ".hpp", ".hxx", ".rs",
        ".qml", ".xaml", ".cmake",
        ".json", ".jsonc", ".yaml", ".yml", ".toml", ".ini", ".config",
        ".props", ".targets", ".csproj", ".fsproj", ".vbproj", ".vcxproj",
        ".gradle", ".kts", ".mk",
        ".pro", ".pri", ".qrc", ".qbs", ".ui", ".sln", ".slnx", ".cfg"
    };
    private static readonly HashSet<string> IndexableFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CMakeLists.txt", "Makefile", "Dockerfile", ".editorconfig",
        "pom.xml", "meson.build", "BUILD", "BUILD.bazel", "WORKSPACE", "Gemfile", "go.mod",
        "requirements.txt", "Pipfile", "Justfile"
    };
    // Generated lockfiles are large, churn on every dependency change and carry no navigable symbols.
    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "pnpm-lock.yaml", "npm-shrinkwrap.json"
    };
    /// <summary>Files above this size are skipped (data dumps, bundles): bounded read cost per sync.</summary>
    internal const long MaximumIndexedFileBytes = RepoIndexService.MaximumIndexedFileBytes;

    /// <summary>
    /// Cost-based fallback: when more than this fraction of the indexed files must be re-projected, the
    /// sync is executed as a full rebuild (one clean pass, no per-file replacement or edge diff against
    /// stale rows). 0.40 is a conservative heuristic, not a measured crossover: on the synthetic 50k matrix
    /// the per-changed-file cost equals the per-file full-rebuild cost, so no crossover exists below 100%,
    /// while the real-history runs (ripgrep/fmt/requests/TaoQuick, 30-70% of 60-270 files changed) were
    /// 1.3-6x slower incrementally than a full rebuild. Only repositories with at least
    /// <see cref="FullRebuildMinimumFiles"/> files are subject to it so small repositories stay incremental.
    /// Settable for tests/benchmarks.
    /// </summary>
    internal double FullRebuildChangeRatio { get; set; } = 0.40;
    internal const int FullRebuildMinimumFiles = 200;
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".parux-agents", ".vs", "packages", "dist", "build"
    };

    private readonly RepoIndexService _index;
    private readonly EmbeddingService? _embeddings;
    private readonly string _databaseDirectory;
    private bool _initialized;
    private string DatabasePath => Path.Combine(_databaseDirectory, "semantic-index-v2.db");

    public SemanticIndexV2Service(RepoIndexService index, EmbeddingService? embeddings = null)
        : this(ServerState.StateDirectory, index, embeddings) { }

    public SemanticIndexV2Service(string databaseDirectory, RepoIndexService index, EmbeddingService? embeddings = null)
    {
        if (string.IsNullOrWhiteSpace(databaseDirectory))
            throw new ArgumentException("databaseDirectory is required.", nameof(databaseDirectory));
        _databaseDirectory = Path.GetFullPath(databaseDirectory);
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _embeddings = embeddings;
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_databaseDirectory);
        var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        EnsureSchema(connection);
        return connection;
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        lock (Gate)
        {
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "semantic_repositories", "semantic_files")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS semantic_repositories (
    alias TEXT PRIMARY KEY COLLATE NOCASE,
    repository_root TEXT NOT NULL,
    registered_utc TEXT NOT NULL,
    last_synced_utc TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_semantic_repositories_root ON semantic_repositories(repository_root);
CREATE TABLE IF NOT EXISTS semantic_files (
    alias TEXT NOT NULL COLLATE NOCASE,
    relative_path TEXT NOT NULL,
    length_bytes INTEGER NOT NULL,
    last_write_ticks INTEGER NOT NULL,
    sha256 TEXT NOT NULL,
    PRIMARY KEY(alias, relative_path)
);
CREATE INDEX IF NOT EXISTS ix_semantic_files_alias ON semantic_files(alias);
CREATE TABLE IF NOT EXISTS semantic_pending (
    alias TEXT NOT NULL COLLATE NOCASE,
    relative_path TEXT NOT NULL,
    PRIMARY KEY(alias, relative_path)
);";
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    public SemanticRepositoryRegistrationDto Register(RegisterSemanticRepositoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string alias = NormalizeAlias(request.Alias);
        string root = NormalizeRoot(request.RepositoryRoot);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            SemanticRepositoryRegistrationDto? duplicatePath = ListInternal(connection)
                .FirstOrDefault(x => !string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase)
                    && PathEquals(x.RepositoryRoot, root));
            if (duplicatePath != null)
                throw new InvalidDataException("Repository root is already registered as alias '" + duplicatePath.Alias + "'.");

            SemanticRepositoryRegistrationDto? existing = GetInternal(connection, alias);
            if (existing != null && !PathEquals(existing.RepositoryRoot, root))
            {
                _index.RemoveRepositoryIndex(existing.RepositoryRoot);
                using SqliteCommand clear = connection.CreateCommand();
                clear.CommandText = "DELETE FROM semantic_files WHERE alias=$alias; DELETE FROM semantic_pending WHERE alias=$alias;";
                clear.Parameters.AddWithValue("$alias", alias);
                clear.ExecuteNonQuery();
            }

            DateTime registered = existing?.RegisteredUtc ?? DateTime.UtcNow;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO semantic_repositories(alias,repository_root,registered_utc,last_synced_utc)
VALUES($alias,$root,$registered,NULL)
ON CONFLICT(alias) DO UPDATE SET
 repository_root=excluded.repository_root,
 last_synced_utc=CASE WHEN semantic_repositories.repository_root=excluded.repository_root
                      THEN semantic_repositories.last_synced_utc ELSE NULL END;";
            command.Parameters.AddWithValue("$alias", alias);
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$registered", registered.ToString("O"));
            command.ExecuteNonQuery();
            return GetInternal(connection, alias)!;
        }
    }

    public bool Unregister(string alias, bool removeIndex = true)
    {
        string normalized = NormalizeAlias(alias);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            SemanticRepositoryRegistrationDto? existing = GetInternal(connection, normalized);
            if (existing == null) return false;
            if (removeIndex) _index.RemoveRepositoryIndex(existing.RepositoryRoot);
            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand files = connection.CreateCommand())
            {
                files.Transaction = transaction;
                files.CommandText = "DELETE FROM semantic_files WHERE alias=$alias; DELETE FROM semantic_pending WHERE alias=$alias;";
                files.Parameters.AddWithValue("$alias", normalized);
                files.ExecuteNonQuery();
            }
            using (SqliteCommand repo = connection.CreateCommand())
            {
                repo.Transaction = transaction;
                repo.CommandText = "DELETE FROM semantic_repositories WHERE alias=$alias;";
                repo.Parameters.AddWithValue("$alias", normalized);
                repo.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        }
    }

    public IReadOnlyList<SemanticRepositoryRegistrationDto> ListRepositories()
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return ListInternal(connection);
        }
    }

    public SemanticIndexSyncResponse Sync(SemanticIndexSyncRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<SemanticRepositoryRegistrationDto> registrations = ResolveRepositories(request.Aliases);
        var response = new SemanticIndexSyncResponse();
        foreach (SemanticRepositoryRegistrationDto repository in registrations)
            response.Repositories.Add(SyncOne(repository, request.ForceFull, request.VerifyAllHashes, cancellationToken));
        return response;
    }

    public SemanticIndexSearchResponse Search(SemanticIndexSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string query = (request.Query ?? string.Empty).Trim();
        if (query.Length is < 1 or > 32_000)
            throw new InvalidDataException("Semantic search query must contain 1-32,000 characters.");

        int maxFiles = Math.Clamp(request.MaxFiles, 1, 100);
        int maxSymbols = Math.Clamp(request.MaxSymbols, 1, 200);
        List<SemanticRepositoryRegistrationDto> repositories = ResolveRepositories(request.Aliases);
        var perRepoFiles = new List<List<string>>();
        var perRepoSymbols = new List<List<string>>();
        foreach (SemanticRepositoryRegistrationDto repository in repositories)
        {
            RepoContextPack pack = _index.SearchContext(repository.RepositoryRoot, query, maxFiles, maxSymbols);
            perRepoFiles.Add(pack.RelevantFiles.Select(value => repository.Alias + ":" + value).ToList());
            perRepoSymbols.Add(pack.RelevantSymbols.Select(value => repository.Alias + ":" + value).ToList());
        }

        return new SemanticIndexSearchResponse
        {
            RepositoriesSearched = repositories.Select(x => x.Alias).ToList(),
            RelevantFiles = MergeRoundRobin(perRepoFiles, maxFiles),
            RelevantSymbols = MergeRoundRobin(perRepoSymbols, maxSymbols),
            EmbeddingCapable = _embeddings?.GetStatus().Loaded == true
        };
    }

    private SemanticRepositorySyncResult SyncOne(
        SemanticRepositoryRegistrationDto repository, bool forceFull, bool verifyAllHashes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = NormalizeRoot(repository.RepositoryRoot);
        Dictionary<string, FileFingerprint> previous;
        HashSet<string> pending;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            previous = ReadManifest(connection, repository.Alias);
            pending = ReadPending(connection, repository.Alias);
        }

        var current = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        int added = 0, changed = 0, unchanged = 0;
        foreach (string file in EnumerateIndexableFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            FileInfo info = new(file);
            if (info.Length > MaximumIndexedFileBytes) continue;
            if (!previous.TryGetValue(relative, out FileFingerprint? old))
            {
                current[relative] = Fingerprint(file, info);
                added++;
                continue;
            }

            bool metadataSame = old.LengthBytes == info.Length && old.LastWriteTicks == info.LastWriteTimeUtc.Ticks;
            if (metadataSame && !verifyAllHashes)
            {
                current[relative] = old;
                unchanged++;
                continue;
            }

            FileFingerprint fresh = Fingerprint(file, info);
            current[relative] = fresh;
            if (string.Equals(old.Sha256, fresh.Sha256, StringComparison.OrdinalIgnoreCase)) unchanged++;
            else changed++;
        }

        List<string> removed = previous.Keys.Where(path => !current.ContainsKey(path)).ToList();
        // A path left in the pending journal by an interrupted sync may already have a projection in the
        // graph that the manifest never recorded (it can even equal the old manifest hash after a revert),
        // so it is re-projected, or removed when it no longer exists, regardless of the hash comparison.
        List<string> graphRemoved = removed
            .Concat(pending.Where(path => !current.ContainsKey(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> graphChangedPaths = current
            .Where(pair => pending.Contains(pair.Key)
                || !previous.TryGetValue(pair.Key, out FileFingerprint? old)
                || !string.Equals(old.Sha256, pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .Concat(graphRemoved)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> manifestUpserts = current
            .Where(pair => !previous.TryGetValue(pair.Key, out FileFingerprint? old)
                || !Equals(old, pair.Value))
            .Select(pair => pair.Key)
            .ToList();

        bool full = forceFull || repository.LastSyncedUtc == null
            || (current.Count >= FullRebuildMinimumFiles
                && graphChangedPaths.Count > FullRebuildChangeRatio * current.Count);
        cancellationToken.ThrowIfCancellationRequested();
        if (full)
        {
            MarkSyncIncomplete(repository.Alias);
            _index.RemoveRepositoryIndex(root);
            _index.ReindexFiles(root, current.Keys, null, cancellationToken);
        }
        else if (graphChangedPaths.Count > 0)
        {
            // Journal the paths first: from here until the manifest commit the graph and the manifest
            // may disagree, and a crash/cancel must not let a later hash comparison hide that.
            RecordPending(repository.Alias, graphChangedPaths);
            _index.ReindexFiles(root, graphChangedPaths.Where(current.ContainsKey), graphRemoved, cancellationToken);
        }

        DateTime synced = DateTime.UtcNow;
        PersistManifest(repository.Alias, current, manifestUpserts, removed, synced, full);
        return new SemanticRepositorySyncResult
        {
            Alias = repository.Alias,
            RepositoryRoot = root,
            AddedFiles = added,
            ChangedFiles = changed,
            RemovedFiles = removed.Count,
            UnchangedFiles = unchanged,
            IndexedFiles = current.Count,
            IndexedSymbols = 0,
            FullRebuild = full,
            SyncedUtc = synced
        };
    }

    private static HashSet<string> ReadPending(SqliteConnection connection, string alias)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT relative_path FROM semantic_pending WHERE alias=$alias;";
        command.Parameters.AddWithValue("$alias", alias);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private void RecordPending(string alias, IReadOnlyCollection<string> paths)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO semantic_pending(alias,relative_path) VALUES($alias,$path);";
            insert.Parameters.AddWithValue("$alias", alias);
            SqliteParameter path = insert.Parameters.Add("$path", SqliteType.Text);
            foreach (string relative in paths)
            {
                path.Value = relative;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    private void MarkSyncIncomplete(string alias)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE semantic_repositories SET last_synced_utc=NULL WHERE alias=$alias;";
            update.Parameters.AddWithValue("$alias", alias);
            update.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private void PersistManifest(
        string alias,
        Dictionary<string, FileFingerprint> current,
        IReadOnlyCollection<string> upserts,
        IReadOnlyCollection<string> removed,
        DateTime synced,
        bool full)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();

            if (full)
            {
                using SqliteCommand clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM semantic_files WHERE alias=$alias;";
                clear.Parameters.AddWithValue("$alias", alias);
                clear.ExecuteNonQuery();
            }
            else
            {
                foreach (string relative in removed)
                {
                    using SqliteCommand delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM semantic_files WHERE alias=$alias AND relative_path=$path;";
                    delete.Parameters.AddWithValue("$alias", alias);
                    delete.Parameters.AddWithValue("$path", relative);
                    delete.ExecuteNonQuery();
                }
            }

            IEnumerable<string> pathsToWrite = full ? current.Keys : upserts;
            foreach (string relative in pathsToWrite)
            {
                if (!current.TryGetValue(relative, out FileFingerprint? fingerprint)) continue;
                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = @"INSERT INTO semantic_files(alias,relative_path,length_bytes,last_write_ticks,sha256)
VALUES($alias,$path,$length,$ticks,$sha)
ON CONFLICT(alias,relative_path) DO UPDATE SET
 length_bytes=excluded.length_bytes,
 last_write_ticks=excluded.last_write_ticks,
 sha256=excluded.sha256;";
                insert.Parameters.AddWithValue("$alias", alias);
                insert.Parameters.AddWithValue("$path", relative);
                insert.Parameters.AddWithValue("$length", fingerprint.LengthBytes);
                insert.Parameters.AddWithValue("$ticks", fingerprint.LastWriteTicks);
                insert.Parameters.AddWithValue("$sha", fingerprint.Sha256);
                insert.ExecuteNonQuery();
            }

            using (SqliteCommand clearPending = connection.CreateCommand())
            {
                clearPending.Transaction = transaction;
                clearPending.CommandText = "DELETE FROM semantic_pending WHERE alias=$alias;";
                clearPending.Parameters.AddWithValue("$alias", alias);
                clearPending.ExecuteNonQuery();
            }

            using (SqliteCommand update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE semantic_repositories SET last_synced_utc=$synced WHERE alias=$alias;";
                update.Parameters.AddWithValue("$synced", synced.ToString("O"));
                update.Parameters.AddWithValue("$alias", alias);
                update.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    private List<SemanticRepositoryRegistrationDto> ResolveRepositories(IEnumerable<string>? requestedAliases)
    {
        List<SemanticRepositoryRegistrationDto> all = ListRepositories().ToList();
        List<string> aliases = (requestedAliases ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeAlias)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (aliases.Count == 0) return all;
        return aliases.Select(alias => all.FirstOrDefault(x =>
                string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("Semantic repository alias is not registered: " + alias)).ToList();
    }

    private static List<SemanticRepositoryRegistrationDto> ListInternal(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT r.alias,r.repository_root,r.registered_utc,r.last_synced_utc,
(SELECT COUNT(*) FROM semantic_files f WHERE f.alias=r.alias)
FROM semantic_repositories r ORDER BY r.alias COLLATE NOCASE;";
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<SemanticRepositoryRegistrationDto>();
        while (reader.Read())
        {
            result.Add(new SemanticRepositoryRegistrationDto
            {
                Alias = reader.GetString(0),
                RepositoryRoot = reader.GetString(1),
                RegisteredUtc = DateTime.Parse(reader.GetString(2), null,
                    System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                LastSyncedUtc = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3), null,
                    System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                IndexedFiles = reader.GetInt32(4)
            });
        }
        return result;
    }

    private static SemanticRepositoryRegistrationDto? GetInternal(SqliteConnection connection, string alias) =>
        ListInternal(connection).FirstOrDefault(x => string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, FileFingerprint> ReadManifest(SqliteConnection connection, string alias)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT relative_path,length_bytes,last_write_ticks,sha256 FROM semantic_files WHERE alias=$alias;";
        command.Parameters.AddWithValue("$alias", alias);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            result[reader.GetString(0)] = new FileFingerprint(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
        return result;
    }

    private static FileFingerprint Fingerprint(string path, FileInfo info)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        string sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new FileFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, sha);
    }

    internal static bool IsIndexableFile(string path)
    {
        string name = Path.GetFileName(path);
        if (ExcludedFileNames.Contains(name)) return false;
        return IndexableFileNames.Contains(name) || IndexableExtensions.Contains(Path.GetExtension(name));
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
                if (!ExcludedDirectoryNames.Contains(Path.GetFileName(subdirectory))) stack.Push(subdirectory);

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory); }
            catch { continue; }
            foreach (string file in files)
                if (IsIndexableFile(file)) yield return file;
        }
    }

    private static List<string> MergeRoundRobin(IReadOnlyList<List<string>> lists, int max)
    {
        var result = new List<string>(max);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int rank = 0; result.Count < max && lists.Any(list => rank < list.Count); rank++)
        {
            foreach (List<string> list in lists)
            {
                if (rank >= list.Count) continue;
                if (seen.Add(list[rank])) result.Add(list[rank]);
                if (result.Count >= max) break;
            }
        }
        return result;
    }

    private static string NormalizeAlias(string? value)
    {
        string alias = (value ?? string.Empty).Trim();
        if (alias.Length is < 1 or > 64 || !char.IsLetter(alias[0])
            || alias.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new InvalidDataException("Repository alias must start with a letter and use only letters, numbers, '.', '_' or '-' (max 64).");
        return alias;
    }

    private static string NormalizeRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("repositoryRoot is required.");
        string root = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Semantic repository root does not exist: " + root);
        return root;
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record FileFingerprint(long LengthBytes, long LastWriteTicks, string Sha256);
}
