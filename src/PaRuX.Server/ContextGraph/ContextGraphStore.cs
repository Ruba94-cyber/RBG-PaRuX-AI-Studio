using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.ContextGraph;

/// <summary>
/// vNext Phase 21/22 - the Local Context Graph's storage. SQLite, same convention as every other
/// vNext store (WAL, lazy schema, test-injectable directory) - deliberately not a dedicated graph
/// database (directive section 41: "Do NOT add a heavyweight database without justification").
/// Two tables (entities, relations) are enough to represent the directive's example relations
/// ("project USES model", "task MODIFIES repository", ...) with plain SQL joins; a real graph
/// engine would only earn its complexity at a scale/traversal-depth this local, single-user store
/// has no reason to expect.
///
/// Entirely local, per directive section 42 - nothing here ever leaves this machine on its own.
/// Explicit deletion, a versioned inspectable export, one-hop neighborhood retrieval and bounded
/// multi-hop traversal are all first-class store operations; callers cannot request an unbounded
/// whole-graph traversal through the retrieval API.
/// </summary>
public sealed class ContextGraphStore
{
    private static readonly object Gate = new();
    private const int MaximumTraversalDepth = 4;
    private const int MaximumTraversalEntities = 200;
    private bool _initialized;

    private readonly string _databaseDirectory;

    public ContextGraphStore() : this(ServerState.StateDirectory) { }
    public ContextGraphStore(string databaseDirectory) => _databaseDirectory = databaseDirectory;

    private string DatabasePath => Path.Combine(_databaseDirectory, "context-graph.db");

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_databaseDirectory);
        var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        // WAL + synchronous=NORMAL: still consistent after any crash; only an OS/power failure can
        // drop the last commits. Repository indexing writes thousands of entities, and a full
        // fsync per commit made the per-task "Context Engine" step take ~39 s on the PaRuX repo.
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
        EnsureSchema(connection);
        return connection;
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        lock (Gate)
        {
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "context_entities", "context_relations",
                    "repo_topology_inputs", "repo_topology_import_refs", "repo_topology_call_refs", "repo_topology_ref_migrations")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS context_entities (
    id TEXT PRIMARY KEY,
    entity_type TEXT NOT NULL,
    name TEXT NOT NULL,
    metadata_json TEXT,
    source TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_context_entities_type ON context_entities(entity_type);
CREATE INDEX IF NOT EXISTS ix_context_entities_type_name ON context_entities(entity_type, name COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS ix_context_entities_source ON context_entities(source);
CREATE TABLE IF NOT EXISTS context_relations (
    id TEXT PRIMARY KEY,
    from_entity_id TEXT NOT NULL,
    relation_type TEXT NOT NULL,
    to_entity_id TEXT NOT NULL,
    metadata_json TEXT,
    source TEXT,
    created_utc TEXT NOT NULL,
    FOREIGN KEY (from_entity_id) REFERENCES context_entities(id) ON DELETE CASCADE,
    FOREIGN KEY (to_entity_id) REFERENCES context_entities(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_context_relations_from ON context_relations(from_entity_id);
CREATE INDEX IF NOT EXISTS ix_context_relations_to ON context_relations(to_entity_id);
CREATE TABLE IF NOT EXISTS repo_topology_inputs (
    source TEXT PRIMARY KEY,
    entity_id TEXT NOT NULL,
    imports_json TEXT NOT NULL,
    calls_json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS repo_topology_import_refs (
    source TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    import_spec TEXT NOT NULL,
    PRIMARY KEY(source, import_spec)
);
CREATE INDEX IF NOT EXISTS ix_repo_topology_import_refs_spec ON repo_topology_import_refs(import_spec COLLATE NOCASE, source);
CREATE TABLE IF NOT EXISTS repo_topology_call_refs (
    source TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    call_name TEXT NOT NULL,
    PRIMARY KEY(source, call_name)
);
CREATE INDEX IF NOT EXISTS ix_repo_topology_call_refs_name ON repo_topology_call_refs(call_name COLLATE NOCASE, source);
CREATE TABLE IF NOT EXISTS repo_topology_ref_migrations (
    id INTEGER PRIMARY KEY CHECK (id = 1)
);
CREATE TRIGGER IF NOT EXISTS trg_repo_topology_inputs_insert AFTER INSERT ON repo_topology_inputs
BEGIN
    DELETE FROM repo_topology_import_refs WHERE source = NEW.source;
    DELETE FROM repo_topology_call_refs WHERE source = NEW.source;
    INSERT OR IGNORE INTO repo_topology_import_refs(source, entity_id, import_spec)
        SELECT NEW.source, NEW.entity_id, value FROM json_each(CASE WHEN json_valid(NEW.imports_json) THEN NEW.imports_json ELSE '[]' END);
    INSERT OR IGNORE INTO repo_topology_call_refs(source, entity_id, call_name)
        SELECT NEW.source, NEW.entity_id, value FROM json_each(CASE WHEN json_valid(NEW.calls_json) THEN NEW.calls_json ELSE '[]' END);
END;
CREATE TRIGGER IF NOT EXISTS trg_repo_topology_inputs_update AFTER UPDATE ON repo_topology_inputs
BEGIN
    DELETE FROM repo_topology_import_refs WHERE source = OLD.source;
    DELETE FROM repo_topology_call_refs WHERE source = OLD.source;
    INSERT OR IGNORE INTO repo_topology_import_refs(source, entity_id, import_spec)
        SELECT NEW.source, NEW.entity_id, value FROM json_each(CASE WHEN json_valid(NEW.imports_json) THEN NEW.imports_json ELSE '[]' END);
    INSERT OR IGNORE INTO repo_topology_call_refs(source, entity_id, call_name)
        SELECT NEW.source, NEW.entity_id, value FROM json_each(CASE WHEN json_valid(NEW.calls_json) THEN NEW.calls_json ELSE '[]' END);
END;
CREATE TRIGGER IF NOT EXISTS trg_repo_topology_inputs_delete AFTER DELETE ON repo_topology_inputs
BEGIN
    DELETE FROM repo_topology_import_refs WHERE source = OLD.source;
    DELETE FROM repo_topology_call_refs WHERE source = OLD.source;
END;";
            command.ExecuteNonQuery();
            EnsureTopologyRefIndexes(connection);
            _initialized = true;
        }
    }

    private static void EnsureTopologyRefIndexes(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand migrated = connection.CreateCommand();
        migrated.Transaction = transaction;
        migrated.CommandText = "SELECT 1 FROM repo_topology_ref_migrations WHERE id = 1;";
        if (migrated.ExecuteScalar() == null)
        {
            using SqliteCommand backfill = connection.CreateCommand();
            backfill.Transaction = transaction;
            backfill.CommandText = @"
INSERT OR IGNORE INTO repo_topology_import_refs(source, entity_id, import_spec)
    SELECT t.source, t.entity_id, j.value FROM repo_topology_inputs t, json_each(CASE WHEN json_valid(t.imports_json) THEN t.imports_json ELSE '[]' END) j;
INSERT OR IGNORE INTO repo_topology_call_refs(source, entity_id, call_name)
    SELECT t.source, t.entity_id, j.value FROM repo_topology_inputs t, json_each(CASE WHEN json_valid(t.calls_json) THEN t.calls_json ELSE '[]' END) j;
INSERT INTO repo_topology_ref_migrations(id) VALUES (1);";
            backfill.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public ContextEntityDto CreateEntity(CreateContextEntityRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EntityType)) throw new InvalidDataException("An entity type is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidDataException("An entity name is required.");

        DateTime now = DateTime.UtcNow;
        var dto = new ContextEntityDto
        {
            Id = Guid.NewGuid().ToString("N"),
            EntityType = request.EntityType,
            Name = request.Name,
            MetadataJson = request.MetadataJson,
            Source = request.Source,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO context_entities
(id, entity_type, name, metadata_json, source, created_utc, updated_utc)
VALUES ($id, $type, $name, $metadata, $source, $created, $updated);";
            command.Parameters.AddWithValue("$id", dto.Id);
            command.Parameters.AddWithValue("$type", dto.EntityType);
            command.Parameters.AddWithValue("$name", dto.Name);
            command.Parameters.AddWithValue("$metadata", (object?)dto.MetadataJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", (object?)dto.Source ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", dto.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updated", dto.UpdatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
        return dto;
    }

    /// <summary>
    /// Atomically replaces one source's file/symbol entities and CONTAINS relations. Removing the
    /// old source and publishing the new projection share a transaction, so readers cannot observe
    /// a half-indexed file and relations from other sources cannot retain deleted IDs.
    /// </summary>
    internal IReadOnlyList<ContextEntityDto> ReplaceSourceEntitiesWithContainment(
        string source,
        IReadOnlyList<CreateContextEntityRequest> requests,
        TopologyInputs? topologyInputs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) throw new ArgumentException("At least one file entity is required.", nameof(requests));
        if (!string.Equals(requests[0].EntityType, "file", StringComparison.Ordinal)
            || requests.Skip(1).Any(request => !string.Equals(request.EntityType, "symbol", StringComparison.Ordinal)))
            throw new InvalidDataException("A source projection must contain one file entity followed by zero or more symbol entities.");

        DateTime now = DateTime.UtcNow;
        var entities = new List<ContextEntityDto>(requests.Count);
        foreach (CreateContextEntityRequest request in requests)
        {
            if (string.IsNullOrWhiteSpace(request.EntityType)) throw new InvalidDataException("An entity type is required.");
            if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidDataException("An entity name is required.");
            entities.Add(new ContextEntityDto
            {
                Id = Guid.NewGuid().ToString("N"),
                EntityType = request.EntityType,
                Name = request.Name,
                MetadataJson = request.MetadataJson,
                Source = source,
                CreatedUtc = now,
                UpdatedUtc = now
            });
        }

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            DeleteSourceEntitiesInternal(connection, transaction, source);

            for (int i = 0; i < entities.Count; i++)
            {
                ContextEntityDto entity = entities[i];
                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = @"INSERT INTO context_entities
(id, entity_type, name, metadata_json, source, created_utc, updated_utc)
VALUES ($id, $type, $name, $metadata, $source, $created, $updated);";
                insert.Parameters.AddWithValue("$id", entity.Id);
                insert.Parameters.AddWithValue("$type", entity.EntityType);
                insert.Parameters.AddWithValue("$name", entity.Name);
                insert.Parameters.AddWithValue("$metadata", (object?)entity.MetadataJson ?? DBNull.Value);
                insert.Parameters.AddWithValue("$source", source);
                insert.Parameters.AddWithValue("$created", now.ToString("O"));
                insert.Parameters.AddWithValue("$updated", now.ToString("O"));
                insert.ExecuteNonQuery();

                if (i == 0) continue;
                using SqliteCommand relation = connection.CreateCommand();
                relation.Transaction = transaction;
                relation.CommandText = @"INSERT INTO context_relations
(id, from_entity_id, relation_type, to_entity_id, metadata_json, source, created_utc)
VALUES ($id, $from, 'CONTAINS', $to, NULL, $source, $created);";
                relation.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                relation.Parameters.AddWithValue("$from", entities[0].Id);
                relation.Parameters.AddWithValue("$to", entity.Id);
                relation.Parameters.AddWithValue("$source", source);
                relation.Parameters.AddWithValue("$created", now.ToString("O"));
                relation.ExecuteNonQuery();
            }

            if (topologyInputs != null)
                UpsertTopologyInputRow(connection, transaction, source, entities[0].Id, topologyInputs);

            transaction.Commit();
        }

        return entities;
    }

    internal void DeleteSourceEntities(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            DeleteSourceEntitiesInternal(connection, transaction, source);
            transaction.Commit();
        }
    }

    // Live Chat run: the context graph had grown to 1.4 GB (every coding task's worktree index) and
    // substr()/full-type scans made a 3-file repository take 35 s to index and 19 s to search. A
    // prefix range on the indexed `source` column touches only the requested repository scope.
    private const string ScopeRangeSql = "source >= $lo AND source < $hi AND substr(source, 1, length($lo)) = $lo";

    private static void AddScopeRange(SqliteCommand command, string sourcePrefix)
    {
        command.Parameters.AddWithValue("$lo", sourcePrefix);
        // U+10FFFF (UTF-8 F4 8F BF BF) is the largest code point, so paths with supplementary characters
        // (e.g. emoji, UTF-8 F0..F4) still fall inside the range; "\uffff" (EF BF BF) excluded them.
        command.Parameters.AddWithValue("$hi", sourcePrefix + "\U0010FFFF\U0010FFFF");
    }

    internal void DeleteSourcePrefix(string sourcePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrefix);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand deleteRelations = connection.CreateCommand())
            {
                deleteRelations.Transaction = transaction;
                // Two statements, one per indexed column: "from IN (..) OR to IN (..)" made SQLite scan
                // the whole relations table (live: minutes on a 1.4 GB graph).
                deleteRelations.CommandText = @"DELETE FROM context_relations
WHERE from_entity_id IN (SELECT id FROM context_entities WHERE +entity_type IN ('file','symbol') AND " + ScopeRangeSql + @");
DELETE FROM context_relations
WHERE to_entity_id IN (SELECT id FROM context_entities WHERE +entity_type IN ('file','symbol') AND " + ScopeRangeSql + ");";
                AddScopeRange(deleteRelations, sourcePrefix);
                deleteRelations.ExecuteNonQuery();
            }

            using (SqliteCommand deleteInputs = connection.CreateCommand())
            {
                deleteInputs.Transaction = transaction;
                deleteInputs.CommandText = "DELETE FROM repo_topology_inputs WHERE " + ScopeRangeSql + ";";
                AddScopeRange(deleteInputs, sourcePrefix);
                deleteInputs.ExecuteNonQuery();
            }

            using (SqliteCommand deleteEntities = connection.CreateCommand())
            {
                deleteEntities.Transaction = transaction;
                deleteEntities.CommandText = "DELETE FROM context_entities WHERE +entity_type IN ('file','symbol') AND " + ScopeRangeSql + ";";
                AddScopeRange(deleteEntities, sourcePrefix);
                deleteEntities.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Entities of one type whose source starts with <paramref name="sourcePrefix"/>, via the source index.</summary>
    internal List<ContextEntityDto> ListEntitiesBySourcePrefix(string entityType, string sourcePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrefix);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, entity_type, name, metadata_json, source, created_utc, updated_utc
FROM context_entities WHERE +entity_type = $type AND " + ScopeRangeSql + " ORDER BY created_utc DESC;";
            command.Parameters.AddWithValue("$type", entityType);
            AddScopeRange(command, sourcePrefix);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<ContextEntityDto>();
            while (reader.Read())
            {
                result.Add(new ContextEntityDto
                {
                    Id = reader.GetString(0),
                    EntityType = reader.GetString(1),
                    Name = reader.GetString(2),
                    MetadataJson = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Source = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedUtc = DateTime.Parse(reader.GetString(5)).ToUniversalTime(),
                    UpdatedUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime()
                });
            }
            return result;
        }
    }

    /// <summary>Gets one entity by its exact indexed source without loading the rest of its repository scope.</summary>
    internal ContextEntityDto? GetEntityBySource(string entityType, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, entity_type, name, metadata_json, source, created_utc, updated_utc
FROM context_entities WHERE +entity_type = $type AND source = $source ORDER BY created_utc DESC LIMIT 1;";
            command.Parameters.AddWithValue("$type", entityType);
            command.Parameters.AddWithValue("$source", source);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new ContextEntityDto
            {
                Id = reader.GetString(0),
                EntityType = reader.GetString(1),
                Name = reader.GetString(2),
                MetadataJson = reader.IsDBNull(3) ? null : reader.GetString(3),
                Source = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedUtc = DateTime.Parse(reader.GetString(5)).ToUniversalTime(),
                UpdatedUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime()
            };
        }
    }

    internal List<ContextEntityDto> ListEntitiesBySource(string entityType, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, entity_type, name, metadata_json, source, created_utc, updated_utc
FROM context_entities WHERE +entity_type = $type AND source = $source ORDER BY created_utc DESC;";
            command.Parameters.AddWithValue("$type", entityType);
            command.Parameters.AddWithValue("$source", source);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<ContextEntityDto>();
            while (reader.Read()) result.Add(ReadEntity(reader));
            return result;
        }
    }

    /// <summary>Returns only file records whose stored history differs from the supplied current Git evidence.</summary>
    internal List<ContextEntityDto> ListFilesWithHistoryMismatch(
        string sourcePrefix,
        IEnumerable<(string RelativePath, int CommitCount, long LastCommitUnix)> history)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrefix);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using (SqliteCommand prepare = connection.CreateCommand())
            {
                prepare.CommandText = @"CREATE TEMP TABLE IF NOT EXISTS current_repo_git_history (
    source TEXT PRIMARY KEY,
    commit_count INTEGER NOT NULL,
    last_commit_unix INTEGER NOT NULL
);
DELETE FROM current_repo_git_history;";
                prepare.ExecuteNonQuery();
            }

            using (SqliteTransaction transaction = connection.BeginTransaction())
            using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = @"INSERT OR REPLACE INTO current_repo_git_history(source, commit_count, last_commit_unix)
VALUES ($source, $commits, $last);";
                SqliteParameter sourceParameter = insert.Parameters.Add("$source", SqliteType.Text);
                SqliteParameter commitsParameter = insert.Parameters.Add("$commits", SqliteType.Integer);
                SqliteParameter lastParameter = insert.Parameters.Add("$last", SqliteType.Integer);
                foreach ((string relativePath, int commitCount, long lastCommitUnix) in history)
                {
                    sourceParameter.Value = sourcePrefix + relativePath.Replace('\\', '/');
                    commitsParameter.Value = Math.Max(0, commitCount);
                    lastParameter.Value = Math.Max(0L, lastCommitUnix);
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }

            using SqliteCommand query = connection.CreateCommand();
            string storedCommitCount = "CASE WHEN json_valid(f.metadata_json) THEN CASE WHEN json_type(f.metadata_json, '$.gitCommitCount') = 'integer' THEN json_extract(f.metadata_json, '$.gitCommitCount') ELSE 0 END ELSE 0 END";
            string storedLastCommit = "CASE WHEN json_valid(f.metadata_json) THEN CASE WHEN json_type(f.metadata_json, '$.gitLastCommitUnix') = 'integer' THEN json_extract(f.metadata_json, '$.gitLastCommitUnix') ELSE 0 END ELSE 0 END";
            query.CommandText = @"SELECT f.id, f.entity_type, f.name, f.metadata_json, f.source, f.created_utc, f.updated_utc
FROM context_entities f
LEFT JOIN current_repo_git_history h ON h.source = f.source
WHERE +f.entity_type = 'file' AND " + ScopeRangeSql.Replace("source", "f.source", StringComparison.Ordinal) + @"
AND ((" + storedCommitCount + @") <> COALESCE(h.commit_count, 0)
OR (" + storedLastCommit + @") <> COALESCE(h.last_commit_unix, 0));";
            AddScopeRange(query, sourcePrefix);
            using SqliteDataReader reader = query.ExecuteReader();
            var result = new List<ContextEntityDto>();
            while (reader.Read()) result.Add(ReadEntity(reader));
            return result;
        }
    }

    /// <summary>Loads only file entities for the requested paths in one repository scope.</summary>
    internal List<ContextEntityDto> ListFileEntitiesByRelativePaths(string sourcePrefix, IEnumerable<string> relativePaths)
    {
        string[] sources = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => sourcePrefix + path.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var result = new List<ContextEntityDto>();
        if (sources.Length == 0) return result;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            for (int offset = 0; offset < sources.Length; offset += 800)
            {
                string[] batch = sources.Skip(offset).Take(800).ToArray();
                using SqliteCommand command = connection.CreateCommand();
                string[] parameters = batch.Select((_, index) => "$s" + index).ToArray();
                command.CommandText = @"SELECT id, entity_type, name, metadata_json, source, created_utc, updated_utc
FROM context_entities WHERE +entity_type = 'file' AND source IN (" + string.Join(",", parameters) + ");";
                for (int index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(parameters[index], batch[index]);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) result.Add(ReadEntity(reader));
            }
        }
        return result;
    }

    internal List<string> ListFilePathsByRelativePaths(string sourcePrefix, IEnumerable<string> relativePaths)
    {
        string[] sources = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => sourcePrefix + path.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var result = new List<string>();
        if (sources.Length == 0) return result;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            for (int offset = 0; offset < sources.Length; offset += 800)
            {
                string[] batch = sources.Skip(offset).Take(800).ToArray();
                using SqliteCommand command = connection.CreateCommand();
                string[] parameters = batch.Select((_, index) => "$s" + index).ToArray();
                command.CommandText = @"SELECT name FROM context_entities WHERE +entity_type = 'file' AND source IN (" + string.Join(",", parameters) + ");";
                for (int index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(parameters[index], batch[index]);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) result.Add(reader.GetString(0));
            }
        }
        return result;
    }

    internal List<string> ListFilePathsByName(string sourcePrefix, string fileName, int limit = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT name FROM context_entities
WHERE +entity_type = 'file' AND " + ScopeRangeSql + @"
AND length(name) >= length($name)
AND lower(substr(name, length(name) - length($name) + 1)) = lower($name)
AND (length(name) = length($name) OR substr(name, length(name) - length($name), 1) = '/')
LIMIT $limit;";
            AddScopeRange(command, sourcePrefix);
            command.Parameters.AddWithValue("$name", fileName);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2));
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<string>();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }
    }

    internal List<ContextEntityDto> ListSymbolCandidatesByName(string sourcePrefix, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, entity_type, name, metadata_json, source, created_utc, updated_utc
FROM context_entities WHERE entity_type = 'symbol' AND " + ScopeRangeSql + @"
AND name = $name COLLATE NOCASE LIMIT 2;";
            AddScopeRange(command, sourcePrefix);
            command.Parameters.AddWithValue("$name", name);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<ContextEntityDto>();
            while (reader.Read()) result.Add(ReadEntity(reader));
            return result;
        }
    }

    /// <summary>
    /// Returns the names that can still change a CALLS edge when the files in <paramref name="excludedSources"/>
    /// change: a name has an edge only while it has exactly one symbol, so a name with two or more symbols
    /// outside the changed files is ambiguous before and after and is skipped. Each probe reads at most
    /// excludedSources.Count + 2 rows, independent of repository size.
    /// </summary>
    internal List<string> FilterNamesStillResolvable(string sourcePrefix, IEnumerable<string> names, IReadOnlySet<string> excludedSources)
    {
        var result = new List<string>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            foreach (string name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = @"SELECT source FROM context_entities WHERE entity_type = 'symbol' AND " + ScopeRangeSql + @"
AND name = $name COLLATE NOCASE LIMIT " + (excludedSources.Count + 2) + ";";
                AddScopeRange(command, sourcePrefix);
                command.Parameters.AddWithValue("$name", name);
                int outside = 0;
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) if (!excludedSources.Contains(reader.GetString(0))) outside++;
                if (outside <= 1) result.Add(name);
            }
        }
        return result;
    }

    internal int CountFilesMissingTopologyInputs(string sourcePrefix)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT COUNT(*) FROM context_entities f
WHERE +f.entity_type = 'file' AND " + ScopeRangeSql + @"
AND NOT EXISTS (SELECT 1 FROM repo_topology_inputs t WHERE t.source = f.source AND t.entity_id = f.id);";
            AddScopeRange(command, sourcePrefix);
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    internal List<TopologyInputRow> ListTopologyInputsByRelativePaths(string sourcePrefix, IEnumerable<string> relativePaths)
    {
        string[] sources = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => sourcePrefix + path.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var result = new List<TopologyInputRow>();
        if (sources.Length == 0) return result;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            for (int offset = 0; offset < sources.Length; offset += 800)
            {
                string[] batch = sources.Skip(offset).Take(800).ToArray();
                using SqliteCommand command = connection.CreateCommand();
                string[] parameters = batch.Select((_, index) => "$s" + index).ToArray();
                command.CommandText = "SELECT source, entity_id, imports_json, calls_json FROM repo_topology_inputs WHERE source IN ("
                    + string.Join(",", parameters) + ");";
                for (int index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(parameters[index], batch[index]);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) result.Add(new TopologyInputRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }
        return result;
    }

    internal List<string> FindTopologyCallReferrers(string sourcePrefix, IEnumerable<string> callNames)
    {
        string[] names = callNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (names.Length == 0) return result.ToList();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            for (int offset = 0; offset < names.Length; offset += 800)
            {
                string[] batch = names.Skip(offset).Take(800).ToArray();
                using SqliteCommand command = connection.CreateCommand();
                string[] parameters = batch.Select((_, index) => "$n" + index).ToArray();
                command.CommandText = "SELECT DISTINCT source FROM repo_topology_call_refs WHERE " + ScopeRangeSql
                    + " AND call_name COLLATE NOCASE IN (" + string.Join(",", parameters) + ");";
                AddScopeRange(command, sourcePrefix);
                for (int index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(parameters[index], batch[index]);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) result.Add(reader.GetString(0));
            }
        }
        return result.ToList();
    }

    internal List<TopologyImportReferrer> FindTopologyImportReferrers(string sourcePrefix, IEnumerable<string> tokens)
    {
        string[] values = tokens.Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new HashSet<TopologyImportReferrer>();
        if (values.Length == 0) return result.ToList();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            for (int offset = 0; offset < values.Length; offset += 200)
            {
                string[] batch = values.Skip(offset).Take(200).ToArray();
                using SqliteCommand command = connection.CreateCommand();
                string[] parameters = batch.Select((_, index) => "$t" + index).ToArray();
                command.CommandText = "SELECT DISTINCT source, import_spec FROM repo_topology_import_refs WHERE " + ScopeRangeSql + " AND ("
                    + string.Join(" OR ", parameters.Select(parameter => "instr(lower(import_spec), lower(" + parameter + ")) > 0")) + ");";
                AddScopeRange(command, sourcePrefix);
                for (int index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(parameters[index], batch[index]);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) result.Add(new TopologyImportReferrer(reader.GetString(0), reader.GetString(1)));
            }
        }
        return result.ToList();
    }

    internal sealed record TopologyImportReferrer(string Source, string ImportSpec);

    internal List<string> ListTopologyNeighborSources(string sourcePrefix, IEnumerable<string> entityIds, IReadOnlyCollection<string> relationTypes)
    {
        string[] ids = entityIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (ids.Length == 0 || relationTypes.Count == 0) return result.ToList();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            for (int offset = 0; offset < ids.Length; offset += 400)
            {
                string[] batch = ids.Skip(offset).Take(400).ToArray();
                using SqliteCommand command = connection.CreateCommand();
                string[] entityParameters = batch.Select((_, index) => "$e" + index).ToArray();
                string[] typeParameters = relationTypes.Select((_, index) => "$r" + index).ToArray();
                string fromIn = string.Join(",", entityParameters);
                command.CommandText = @"SELECT DISTINCT CASE WHEN r.from_entity_id IN (" + fromIn + @") THEN t.source ELSE f.source END
FROM context_relations r
JOIN context_entities f ON f.id = r.from_entity_id
JOIN context_entities t ON t.id = r.to_entity_id
WHERE r.relation_type IN (" + string.Join(",", typeParameters) + @")
AND (r.from_entity_id IN (" + fromIn + ") OR r.to_entity_id IN (" + fromIn + @"))
AND ((r.from_entity_id IN (" + fromIn + ") AND " + ScopeRangeSql.Replace("source", "t.source", StringComparison.Ordinal)
                    + ") OR (r.to_entity_id IN (" + fromIn + ") AND " + ScopeRangeSql.Replace("source", "f.source", StringComparison.Ordinal) + "));";
                AddScopeRange(command, sourcePrefix);
                for (int index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(entityParameters[index], batch[index]);
                for (int index = 0; index < relationTypes.Count; index++) command.Parameters.AddWithValue(typeParameters[index], relationTypes.ElementAt(index));
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) if (!reader.IsDBNull(0)) result.Add(reader.GetString(0));
            }
        }
        return result.ToList();
    }

    internal List<ScopedRelation> ListFileRelationsByFromEntityIds(string sourcePrefix, IEnumerable<string> entityIds, IReadOnlyCollection<string> relationTypes)
    {
        string[] ids = entityIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        var result = new List<ScopedRelation>();
        if (ids.Length == 0 || relationTypes.Count == 0) return result;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            for (int offset = 0; offset < ids.Length; offset += 800)
            {
                string[] batch = ids.Skip(offset).Take(800).ToArray();
                using SqliteCommand command = connection.CreateCommand();
                string[] entityParameters = batch.Select((_, index) => "$e" + index).ToArray();
                string[] typeParameters = relationTypes.Select((_, index) => "$r" + index).ToArray();
                command.CommandText = "SELECT id, from_entity_id, relation_type, to_entity_id FROM context_relations WHERE source = $scope AND from_entity_id IN ("
                    + string.Join(",", entityParameters) + ") AND relation_type IN (" + string.Join(",", typeParameters) + ");";
                command.Parameters.AddWithValue("$scope", sourcePrefix);
                for (int index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(entityParameters[index], batch[index]);
                for (int index = 0; index < relationTypes.Count; index++) command.Parameters.AddWithValue(typeParameters[index], relationTypes.ElementAt(index));
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) result.Add(new ScopedRelation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }
        return result;
    }

    private static ContextEntityDto ReadEntity(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        EntityType = reader.GetString(1),
        Name = reader.GetString(2),
        MetadataJson = reader.IsDBNull(3) ? null : reader.GetString(3),
        Source = reader.IsDBNull(4) ? null : reader.GetString(4),
        CreatedUtc = DateTime.Parse(reader.GetString(5)).ToUniversalTime(),
        UpdatedUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime()
    };

    /// <summary>Distinct "&lt;prefix&gt;&lt;key&gt;::" scopes of file entities under a source prefix such as "repo-index:".</summary>
    internal List<string> ListSourceScopes(string scopePrefix, string separator)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT DISTINCT substr(source, 1, instr(substr(source, length($lo) + 1), $sep) + length($lo) + length($sep) - 1)
FROM context_entities WHERE +entity_type = 'file' AND " + ScopeRangeSql + ";";
            AddScopeRange(command, scopePrefix);
            command.Parameters.AddWithValue("$sep", separator);
            using SqliteDataReader reader = command.ExecuteReader();
            var scopes = new List<string>();
            while (reader.Read())
                if (!reader.IsDBNull(0)) scopes.Add(reader.GetString(0));
            return scopes.Where(scope => scope.EndsWith(separator, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Resolution-independent per-file topology inputs (raw import specifiers, call identifiers) as JSON arrays.</summary>
    internal sealed record TopologyInputs(string ImportsJson, string CallsJson);
    internal sealed record TopologyInputRow(string Source, string EntityId, string ImportsJson, string CallsJson);

    private static void UpsertTopologyInputRow(
        SqliteConnection connection, SqliteTransaction transaction, string source, string entityId, TopologyInputs inputs)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT OR REPLACE INTO repo_topology_inputs(source, entity_id, imports_json, calls_json)
VALUES ($source, $entity, $imports, $calls);";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$entity", entityId);
        command.Parameters.AddWithValue("$imports", inputs.ImportsJson);
        command.Parameters.AddWithValue("$calls", inputs.CallsJson);
        command.ExecuteNonQuery();
    }

    /// <summary>Streams stored topology inputs of one source scope via the primary-key range (no materialization).</summary>
    internal IEnumerable<TopologyInputRow> EnumerateTopologyInputs(string sourcePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrefix);
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT source, entity_id, imports_json, calls_json FROM repo_topology_inputs WHERE " + ScopeRangeSql + ";";
        AddScopeRange(command, sourcePrefix);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            yield return new TopologyInputRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    internal void UpsertTopologyInputs(IReadOnlyCollection<(string Source, string EntityId, TopologyInputs Inputs)> rows)
    {
        if (rows.Count == 0) return;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            foreach ((string source, string entityId, TopologyInputs inputs) in rows)
                UpsertTopologyInputRow(connection, transaction, source, entityId, inputs);
            transaction.Commit();
        }
    }

    internal sealed record ScopedRelation(string Id, string FromEntityId, string RelationType, string ToEntityId);

    /// <summary>
    /// Relations of the given types whose origin is a file entity in the source scope. Uses the
    /// indexed source range plus the from_entity_id index (never a full relation-table scan), so the
    /// incremental topology pass can diff desired edges against the stored ones for one repository.
    /// </summary>
    internal List<ScopedRelation> ListFileRelationsByScope(string sourcePrefix, IReadOnlyCollection<string> relationTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrefix);
        ArgumentNullException.ThrowIfNull(relationTypes);
        var result = new List<ScopedRelation>();
        if (relationTypes.Count == 0) return result;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            string[] names = relationTypes.Select((_, i) => "$t" + i).ToArray();
            command.CommandText = @"SELECT id, from_entity_id, relation_type, to_entity_id FROM context_relations
WHERE relation_type IN (" + string.Join(",", names) + @")
AND from_entity_id IN (SELECT id FROM context_entities WHERE +entity_type = 'file' AND " + ScopeRangeSql + ");";
            int index = 0;
            foreach (string type in relationTypes) command.Parameters.AddWithValue(names[index++], type);
            AddScopeRange(command, sourcePrefix);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(new ScopedRelation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return result;
    }

    /// <summary>
    /// Applies a relation delta atomically: stale relations are deleted and missing ones inserted in a
    /// single transaction, so a crash or cancellation leaves either the old or the new edge set.
    /// Endpoints that no longer exist are skipped rather than failing the whole batch.
    /// </summary>
    internal void ApplyRelationDelta(
        IReadOnlyCollection<string> deleteRelationIds,
        IReadOnlyCollection<CreateContextRelationRequest> creates)
    {
        ArgumentNullException.ThrowIfNull(deleteRelationIds);
        ArgumentNullException.ThrowIfNull(creates);
        if (deleteRelationIds.Count == 0 && creates.Count == 0) return;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM context_relations WHERE id = $id;";
                SqliteParameter id = delete.Parameters.Add("$id", SqliteType.Text);
                foreach (string relationId in deleteRelationIds)
                {
                    id.Value = relationId;
                    delete.ExecuteNonQuery();
                }
            }

            using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = @"INSERT INTO context_relations
(id, from_entity_id, relation_type, to_entity_id, metadata_json, source, created_utc)
SELECT $id, $from, $type, $to, $metadata, $source, $created
WHERE EXISTS (SELECT 1 FROM context_entities WHERE id = $from)
  AND EXISTS (SELECT 1 FROM context_entities WHERE id = $to);";
                SqliteParameter id = insert.Parameters.Add("$id", SqliteType.Text);
                SqliteParameter from = insert.Parameters.Add("$from", SqliteType.Text);
                SqliteParameter type = insert.Parameters.Add("$type", SqliteType.Text);
                SqliteParameter to = insert.Parameters.Add("$to", SqliteType.Text);
                SqliteParameter metadata = insert.Parameters.Add("$metadata", SqliteType.Text);
                SqliteParameter source = insert.Parameters.Add("$source", SqliteType.Text);
                SqliteParameter created = insert.Parameters.Add("$created", SqliteType.Text);
                string now = DateTime.UtcNow.ToString("O");
                foreach (CreateContextRelationRequest request in creates)
                {
                    id.Value = Guid.NewGuid().ToString("N");
                    from.Value = request.FromEntityId;
                    type.Value = request.RelationType;
                    to.Value = request.ToEntityId;
                    metadata.Value = (object?)request.MetadataJson ?? DBNull.Value;
                    source.Value = (object?)request.Source ?? DBNull.Value;
                    created.Value = now;
                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
    }

    private static void DeleteSourceEntitiesInternal(SqliteConnection connection, SqliteTransaction transaction, string source)
    {
        using (SqliteCommand deleteRelations = connection.CreateCommand())
        {
            deleteRelations.Transaction = transaction;
            deleteRelations.CommandText = @"DELETE FROM context_relations
WHERE from_entity_id IN (SELECT id FROM context_entities WHERE +entity_type IN ('file','symbol') AND source = $source);
DELETE FROM context_relations
WHERE to_entity_id IN (SELECT id FROM context_entities WHERE +entity_type IN ('file','symbol') AND source = $source);";
            deleteRelations.Parameters.AddWithValue("$source", source);
            deleteRelations.ExecuteNonQuery();
        }

        using (SqliteCommand deleteInputs = connection.CreateCommand())
        {
            deleteInputs.Transaction = transaction;
            deleteInputs.CommandText = "DELETE FROM repo_topology_inputs WHERE source = $source;";
            deleteInputs.Parameters.AddWithValue("$source", source);
            deleteInputs.ExecuteNonQuery();
        }

        using (SqliteCommand deleteEntities = connection.CreateCommand())
        {
            deleteEntities.Transaction = transaction;
            deleteEntities.CommandText = "DELETE FROM context_entities WHERE +entity_type IN ('file','symbol') AND source = $source;";
            deleteEntities.Parameters.AddWithValue("$source", source);
            deleteEntities.ExecuteNonQuery();
        }
    }

    public ContextEntityDto? GetEntity(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return GetEntityInternal(connection, id);
        }
    }

    public List<ContextEntityDto> ListEntities(string? entityType = null)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = entityType == null
                ? "SELECT id FROM context_entities ORDER BY created_utc DESC;"
                : "SELECT id FROM context_entities WHERE entity_type = $type ORDER BY created_utc DESC;";
            if (entityType != null) command.Parameters.AddWithValue("$type", entityType);
            using SqliteDataReader reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));
            reader.Close();
            return ids.Select(id => GetEntityInternal(connection, id)!).ToList();
        }
    }

    /// <summary>Finds entities whose name appears in free text (e.g. a chat message) - the
    /// primitive that resolves a user's message into candidate entity ids before
    /// <see cref="GetNeighborhood"/> can run. Deliberately simple, case-insensitive whole-name
    /// matching over what is expected to stay a small, local, single-user graph - scans
    /// in-memory rather than adding a new SQL shape, matching this store's "no heavyweight
    /// machinery" convention. Longer name matches are preferred first (more specific).</summary>
    public List<ContextEntityDto> FindEntitiesMentionedIn(string text, int maxResults = 3)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<ContextEntityDto>();
        return ListEntities()
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && ContainsWholeName(text, e.Name))
            .OrderByDescending(e => e.Name.Length)
            .Take(maxResults)
            .ToList();
    }

    private static bool ContainsWholeName(string text, string name)
    {
        for (int start = 0; start <= text.Length - name.Length; start++)
        {
            if (string.Compare(text, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;
            bool leftBoundary = start == 0 || !IsNameCharacter(text[start - 1]);
            int end = start + name.Length;
            bool rightBoundary = end == text.Length || !IsNameCharacter(text[end]);
            if (leftBoundary && rightBoundary) return true;
        }
        return false;
    }

    private static bool IsNameCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    /// <summary>Deletes an entity and every relation touching it (explicit cleanup, not relying
    /// solely on the FK cascade, since Microsoft.Data.Sqlite requires "PRAGMA foreign_keys=ON"
    /// per-connection to actually enforce cascade behavior and this store doesn't set that -
    /// simpler and more obviously correct to just delete relations directly).</summary>
    public bool DeleteEntity(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand deleteRelations = connection.CreateCommand())
            {
                deleteRelations.Transaction = transaction;
                deleteRelations.CommandText = "DELETE FROM context_relations WHERE from_entity_id = $id; DELETE FROM context_relations WHERE to_entity_id = $id;";
                deleteRelations.Parameters.AddWithValue("$id", id);
                deleteRelations.ExecuteNonQuery();
            }

            int affected;
            using (SqliteCommand deleteEntity = connection.CreateCommand())
            {
                deleteEntity.Transaction = transaction;
                deleteEntity.CommandText = "DELETE FROM context_entities WHERE id = $id;";
                deleteEntity.Parameters.AddWithValue("$id", id);
                affected = deleteEntity.ExecuteNonQuery();
            }

            transaction.Commit();
            return affected > 0;
        }
    }

    public ContextRelationDto CreateRelation(CreateContextRelationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RelationType)) throw new InvalidDataException("A relation type is required.");

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            if (GetEntityInternal(connection, request.FromEntityId) == null)
                throw new InvalidDataException("fromEntityId does not reference an existing entity.");
            if (GetEntityInternal(connection, request.ToEntityId) == null)
                throw new InvalidDataException("toEntityId does not reference an existing entity.");

            var dto = new ContextRelationDto
            {
                Id = Guid.NewGuid().ToString("N"),
                FromEntityId = request.FromEntityId,
                RelationType = request.RelationType,
                ToEntityId = request.ToEntityId,
                MetadataJson = request.MetadataJson,
                Source = request.Source,
                CreatedUtc = DateTime.UtcNow
            };

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO context_relations
(id, from_entity_id, relation_type, to_entity_id, metadata_json, source, created_utc)
VALUES ($id, $from, $type, $to, $metadata, $source, $created);";
            command.Parameters.AddWithValue("$id", dto.Id);
            command.Parameters.AddWithValue("$from", dto.FromEntityId);
            command.Parameters.AddWithValue("$type", dto.RelationType);
            command.Parameters.AddWithValue("$to", dto.ToEntityId);
            command.Parameters.AddWithValue("$metadata", (object?)dto.MetadataJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", (object?)dto.Source ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", dto.CreatedUtc.ToString("O"));
            command.ExecuteNonQuery();
            return dto;
        }
    }

    public bool DeleteRelation(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM context_relations WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Real retrieval primitive (directive section 43): a small, explainable slice
    /// around one entity - its direct relations both directions plus the entities on the other
    /// end of each - never the whole graph. Multi-hop callers use <see cref="Traverse"/>, which
    /// remains bounded independently by depth and entity count.</summary>
    public ContextNeighborhoodResponse? GetNeighborhood(string entityId)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            ContextEntityDto? center = GetEntityInternal(connection, entityId);
            if (center == null) return null;

            List<ContextRelationDto> outgoing = QueryRelations(connection, "from_entity_id", entityId);
            List<ContextRelationDto> incoming = QueryRelations(connection, "to_entity_id", entityId);

            var neighborIds = outgoing.Select(r => r.ToEntityId)
                .Concat(incoming.Select(r => r.FromEntityId))
                .Distinct()
                .ToList();
            List<ContextEntityDto> neighbors = neighborIds
                .Select(id => GetEntityInternal(connection, id))
                .Where(e => e != null)
                .Select(e => e!)
                .ToList();

            return new ContextNeighborhoodResponse
            {
                Center = center,
                OutgoingRelations = outgoing,
                IncomingRelations = incoming,
                NeighborEntities = neighbors
            };
        }
    }

    /// <summary>
    /// Breadth-first multi-hop retrieval with hard Core-owned limits. Distances are shortest-hop
    /// distances from the center. Once maxEntities is reached the traversal stops admitting unseen
    /// nodes and marks the response truncated; it never silently turns the cap into an unbounded
    /// relation dump.
    /// </summary>
    public ContextTraversalResponse? Traverse(string entityId, int maxDepth = 2, int maxEntities = 64)
    {
        if (string.IsNullOrWhiteSpace(entityId)) throw new InvalidDataException("entityId is required.");
        if (maxDepth is < 1 or > MaximumTraversalDepth)
            throw new InvalidDataException("Context Graph traversal depth must be between 1 and " + MaximumTraversalDepth + ".");
        if (maxEntities is < 2 or > MaximumTraversalEntities)
            throw new InvalidDataException("Context Graph traversal maxEntities must be between 2 and " + MaximumTraversalEntities + ".");

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            ContextEntityDto? center = GetEntityInternal(connection, entityId.Trim());
            if (center == null) return null;

            var entities = new Dictionary<string, ContextEntityDto>(StringComparer.Ordinal)
            {
                [center.Id] = center
            };
            var depths = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [center.Id] = 0
            };
            var relations = new Dictionary<string, ContextRelationDto>(StringComparer.Ordinal);
            var queue = new Queue<(string EntityId, int Depth)>();
            queue.Enqueue((center.Id, 0));
            bool truncated = false;

            while (queue.Count > 0)
            {
                (string currentId, int depth) = queue.Dequeue();
                if (depth >= maxDepth) continue;

                IEnumerable<ContextRelationDto> touching = QueryRelations(connection, "from_entity_id", currentId)
                    .Concat(QueryRelations(connection, "to_entity_id", currentId))
                    .OrderBy(relation => relation.CreatedUtc)
                    .ThenBy(relation => relation.Id, StringComparer.Ordinal);

                foreach (ContextRelationDto relation in touching)
                {
                    string neighborId = string.Equals(relation.FromEntityId, currentId, StringComparison.Ordinal)
                        ? relation.ToEntityId
                        : relation.FromEntityId;

                    if (!entities.ContainsKey(neighborId))
                    {
                        if (entities.Count >= maxEntities)
                        {
                            truncated = true;
                            continue;
                        }

                        ContextEntityDto? neighbor = GetEntityInternal(connection, neighborId);
                        if (neighbor == null) continue;
                        entities[neighbor.Id] = neighbor;
                        depths[neighbor.Id] = depth + 1;
                        queue.Enqueue((neighbor.Id, depth + 1));
                    }

                    if (entities.ContainsKey(relation.FromEntityId) && entities.ContainsKey(relation.ToEntityId))
                        relations.TryAdd(relation.Id, relation);
                }
            }

            return new ContextTraversalResponse
            {
                Center = center,
                MaxDepth = maxDepth,
                MaxEntities = maxEntities,
                Truncated = truncated,
                Entities = entities.Values
                    .OrderBy(entity => depths[entity.Id])
                    .ThenBy(entity => entity.CreatedUtc)
                    .ThenBy(entity => entity.Id, StringComparer.Ordinal)
                    .ToList(),
                Relations = relations.Values
                    .OrderBy(relation => relation.CreatedUtc)
                    .ThenBy(relation => relation.Id, StringComparer.Ordinal)
                    .ToList(),
                DepthByEntityId = depths
            };
        }
    }

    /// <summary>
    /// Lossless, inspectable export contract. This intentionally exports DTOs rather than the
    /// SQLite file so the format is stable across future schema migrations and can be consumed by
    /// external local tooling without SQLite implementation knowledge.
    /// </summary>
    public ContextGraphExportDto ExportSnapshot()
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand entityIdsCommand = connection.CreateCommand();
            entityIdsCommand.CommandText = "SELECT id FROM context_entities ORDER BY created_utc ASC, id ASC;";
            using SqliteDataReader reader = entityIdsCommand.ExecuteReader();
            var entityIds = new List<string>();
            while (reader.Read()) entityIds.Add(reader.GetString(0));
            reader.Close();

            List<ContextEntityDto> entities = entityIds
                .Select(id => GetEntityInternal(connection, id))
                .Where(entity => entity != null)
                .Select(entity => entity!)
                .ToList();
            List<ContextRelationDto> relations = QueryAllRelations(connection);

            return new ContextGraphExportDto
            {
                SchemaVersion = 1,
                ExportedUtc = DateTime.UtcNow,
                Entities = entities,
                Relations = relations
            };
        }
    }

    private static List<ContextRelationDto> QueryRelations(SqliteConnection connection, string column, string entityId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $@"SELECT id, from_entity_id, relation_type, to_entity_id, metadata_json, source, created_utc
FROM context_relations WHERE {column} = $id ORDER BY created_utc ASC;";
        command.Parameters.AddWithValue("$id", entityId);
        using SqliteDataReader reader = command.ExecuteReader();
        return ReadRelations(reader);
    }

    private static List<ContextRelationDto> QueryAllRelations(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT id, from_entity_id, relation_type, to_entity_id, metadata_json, source, created_utc
FROM context_relations ORDER BY created_utc ASC, id ASC;";
        using SqliteDataReader reader = command.ExecuteReader();
        return ReadRelations(reader);
    }

    private static List<ContextRelationDto> ReadRelations(SqliteDataReader reader)
    {
        var results = new List<ContextRelationDto>();
        while (reader.Read())
        {
            results.Add(new ContextRelationDto
            {
                Id = reader.GetString(0),
                FromEntityId = reader.GetString(1),
                RelationType = reader.GetString(2),
                ToEntityId = reader.GetString(3),
                MetadataJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                Source = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime()
            });
        }
        return results;
    }

    private static ContextEntityDto? GetEntityInternal(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT id, entity_type, name, metadata_json, source, created_utc, updated_utc
FROM context_entities WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return new ContextEntityDto
        {
            Id = reader.GetString(0),
            EntityType = reader.GetString(1),
            Name = reader.GetString(2),
            MetadataJson = reader.IsDBNull(3) ? null : reader.GetString(3),
            Source = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedUtc = DateTime.Parse(reader.GetString(5)).ToUniversalTime(),
            UpdatedUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime()
        };
    }

    /// <summary>Same backup-API convention as MemoryStore/ConversationStore - see BackupService
    /// for why the Local Context Graph (entities/relations learned from chat) is part of the
    /// Backup &amp; Restore archive rather than being silently dropped.</summary>
    public void BackupDatabaseTo(string destinationPath)
    {
        lock (Gate)
        {
            SqliteSchemaGuard.ThrowIfInitializedDatabaseIsMissing(DatabasePath, _initialized);
            using SqliteConnection source = Open();
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            using var destination = new SqliteConnection("Data Source=" + destinationPath + ";Mode=ReadWriteCreate");
            destination.Open();
            source.BackupDatabase(destination);
            destination.Close();
            SqliteConnection.ClearAllPools();
        }
    }

    public void ReplaceDatabaseFile(string sourceFilePath)
    {
        lock (Gate)
        {
            SqliteDatabaseFileReplacer.Replace(DatabasePath, _databaseDirectory, sourceFilePath);
            _initialized = false;
        }
    }
}
