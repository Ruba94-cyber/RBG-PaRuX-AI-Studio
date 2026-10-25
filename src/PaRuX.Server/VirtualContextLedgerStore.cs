using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Durable append-only ledger for Virtual Context V2. Constraints and decisions are facts with
/// provenance; supersession, contradiction and tombstone relations are explicit edges to earlier
/// facts. The ledger remains the audit authority while a rebuildable indexed projection tracks the
/// currently active facts, so long-running scopes do not have to replay a bounded history prefix.
/// </summary>
public sealed class VirtualContextLedgerStore
{
    private const string ProjectionVersion = "1";
    private const int ProjectionReplayPageSize = 256;
    private const int PromptConstraintLimit = 64;
    private const int PromptDecisionLimit = 64;
    private const int PromptContradictionLimit = 32;
    private const string PromptOpen = "[VIRTUAL_CONTEXT_V2_LEDGER]";
    private const string PromptClose = "[/VIRTUAL_CONTEXT_V2_LEDGER]";

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static VirtualContextLedgerStore Current { get; } = new(ServerState.StateDirectory);

    private readonly string _databaseDirectory;
    private readonly TimeProvider _timeProvider;
    private string DatabasePath => Path.Combine(_databaseDirectory, "virtual-context-v2.db");

    public VirtualContextLedgerStore(string databaseDirectory, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(databaseDirectory))
            throw new ArgumentException("databaseDirectory is required.", nameof(databaseDirectory));
        _databaseDirectory = Path.GetFullPath(databaseDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_databaseDirectory);
        var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        EnsureSchema(connection);
        return connection;
    }

    /// <summary>
    /// Verifies the schema on every connection instead of trusting a flag cached on this long-lived
    /// singleton. The database lives under a data root that can be swapped, restored or cleaned up
    /// after an earlier connection initialized it; the reopened file is then empty, and a cached
    /// "initialized" flag would skip table creation ("no such table: virtual_context_ledger").
    /// </summary>
    private void EnsureSchema(SqliteConnection connection)
    {
        lock (Gate)
        {
            if (SchemaIsCurrent(connection)) return;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS virtual_context_ledger (
    id TEXT PRIMARY KEY,
    scope TEXT NOT NULL,
    scope_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    text TEXT NOT NULL,
    source TEXT NOT NULL,
    supersedes_json TEXT NOT NULL,
    contradicts_json TEXT NOT NULL,
    content_sha256 TEXT NOT NULL UNIQUE,
    created_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_vc_ledger_scope ON virtual_context_ledger(scope, scope_id, created_utc);
CREATE INDEX IF NOT EXISTS ix_vc_ledger_kind ON virtual_context_ledger(kind);
CREATE TABLE IF NOT EXISTS virtual_context_active (
    id TEXT PRIMARY KEY,
    scope TEXT NOT NULL,
    scope_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    row_id INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_vc_active_scope_kind
    ON virtual_context_active(scope, scope_id, kind, row_id);
CREATE TABLE IF NOT EXISTS virtual_context_projection_meta (
    name TEXT PRIMARY KEY,
    value TEXT NOT NULL
);";
                command.ExecuteNonQuery();
            }

            string? version;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT value FROM virtual_context_projection_meta WHERE name='active_projection_version';";
                version = command.ExecuteScalar() as string;
            }

            if (!string.Equals(version, ProjectionVersion, StringComparison.Ordinal))
                RebuildProjection(connection);
        }
    }

    /// <summary>True only when every ledger table exists and the active projection version is current.</summary>
    private static bool SchemaIsCurrent(SqliteConnection connection)
    {
        using (SqliteCommand tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN "
                + "('virtual_context_ledger','virtual_context_active','virtual_context_projection_meta');";
            if (Convert.ToInt64(tables.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 3)
                return false;
        }
        using (SqliteCommand version = connection.CreateCommand())
        {
            version.CommandText = "SELECT value FROM virtual_context_projection_meta WHERE name='active_projection_version';";
            return string.Equals(version.ExecuteScalar() as string, ProjectionVersion, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Replays the append-only authority in bounded pages and atomically replaces the derived active
    /// projection. This runs only when the projection schema/version is absent or changes.
    /// </summary>
    private static void RebuildProjection(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM virtual_context_active;";
            clear.ExecuteNonQuery();
        }

        long afterRowId = 0;
        while (true)
        {
            var page = new List<(long RowId, string Id)>();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT rowid,id FROM virtual_context_ledger
WHERE rowid > $after ORDER BY rowid ASC LIMIT $limit;";
                command.Parameters.AddWithValue("$after", afterRowId);
                command.Parameters.AddWithValue("$limit", ProjectionReplayPageSize);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) page.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            if (page.Count == 0) break;
            foreach ((long rowId, string id) in page)
            {
                VirtualContextLedgerEntryDto entry = GetInternal(connection, id, transaction)
                    ?? throw new InvalidDataException("Virtual Context projection replay referenced a missing ledger entry.");
                ApplyProjectionMutation(connection, transaction, rowId, entry);
            }
            afterRowId = page[^1].RowId;
        }

        using (SqliteCommand marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = @"INSERT INTO virtual_context_projection_meta(name,value)
VALUES('active_projection_version',$version)
ON CONFLICT(name) DO UPDATE SET value=excluded.value;";
            marker.Parameters.AddWithValue("$version", ProjectionVersion);
            marker.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public VirtualContextLedgerEntryDto Append(AppendVirtualContextLedgerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string scope = NormalizeToken(request.Scope, 40, "scope").ToLowerInvariant();
        string scopeId = NormalizeToken(request.ScopeId, 256, "scopeId");
        string kind = NormalizeKind(request.Kind);
        string text = NormalizeText(request.Text);
        string source = NormalizeSource(request.Source);
        List<string> supersedes = NormalizeReferences(request.SupersedesIds, "supersedesIds");
        List<string> contradicts = NormalizeReferences(request.ContradictsIds, "contradictsIds");
        if ((kind == VirtualContextLedgerKind.Supersession || kind == VirtualContextLedgerKind.Tombstone)
            && supersedes.Count == 0)
            throw new InvalidDataException("A supersession or tombstone ledger entry must reference at least one earlier entry.");
        if (kind == VirtualContextLedgerKind.Contradiction && contradicts.Count == 0)
            throw new InvalidDataException("A contradiction ledger entry must reference at least one earlier entry.");

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            foreach (string reference in supersedes.Concat(contradicts).Distinct(StringComparer.Ordinal))
            {
                VirtualContextLedgerEntryDto prior = GetInternal(connection, reference)
                    ?? throw new InvalidDataException("Ledger relation references an unknown entry: " + reference);
                if (!string.Equals(prior.Scope, scope, StringComparison.Ordinal)
                    || !string.Equals(prior.ScopeId, scopeId, StringComparison.Ordinal))
                    throw new InvalidDataException("Virtual Context ledger relations cannot cross scope/scopeId boundaries.");
            }

            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            string id = Guid.NewGuid().ToString("N");
            string hash = Hash(id, scope, scopeId, kind, text, source, supersedes, contradicts, now);
            var dto = new VirtualContextLedgerEntryDto
            {
                Id = id,
                Scope = scope,
                ScopeId = scopeId,
                Kind = kind,
                Text = text,
                Source = source,
                SupersedesIds = supersedes,
                ContradictsIds = contradicts,
                ContentSha256 = hash,
                CreatedUtc = now
            };

            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO virtual_context_ledger
(id,scope,scope_id,kind,text,source,supersedes_json,contradicts_json,content_sha256,created_utc)
VALUES($id,$scope,$scopeId,$kind,$text,$source,$supersedes,$contradicts,$hash,$created);";
                command.Parameters.AddWithValue("$id", dto.Id);
                command.Parameters.AddWithValue("$scope", dto.Scope);
                command.Parameters.AddWithValue("$scopeId", dto.ScopeId);
                command.Parameters.AddWithValue("$kind", dto.Kind);
                command.Parameters.AddWithValue("$text", dto.Text);
                command.Parameters.AddWithValue("$source", dto.Source);
                command.Parameters.AddWithValue("$supersedes", JsonSerializer.Serialize(dto.SupersedesIds, JsonOptions));
                command.Parameters.AddWithValue("$contradicts", JsonSerializer.Serialize(dto.ContradictsIds, JsonOptions));
                command.Parameters.AddWithValue("$hash", dto.ContentSha256);
                command.Parameters.AddWithValue("$created", dto.CreatedUtc.ToString("O"));
                command.ExecuteNonQuery();
            }

            long rowId;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT rowid FROM virtual_context_ledger WHERE id=$id;";
                command.Parameters.AddWithValue("$id", dto.Id);
                rowId = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            ApplyProjectionMutation(connection, transaction, rowId, dto);
            transaction.Commit();
            return dto;
        }
    }

    private static void ApplyProjectionMutation(
        SqliteConnection connection, SqliteTransaction transaction, long rowId, VirtualContextLedgerEntryDto entry)
    {
        if (IsProjectedKind(entry.Kind))
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO virtual_context_active(id,scope,scope_id,kind,row_id)
VALUES($id,$scope,$scopeId,$kind,$rowId)
ON CONFLICT(id) DO UPDATE SET scope=excluded.scope,scope_id=excluded.scope_id,kind=excluded.kind,row_id=excluded.row_id;";
            insert.Parameters.AddWithValue("$id", entry.Id);
            insert.Parameters.AddWithValue("$scope", entry.Scope);
            insert.Parameters.AddWithValue("$scopeId", entry.ScopeId);
            insert.Parameters.AddWithValue("$kind", entry.Kind);
            insert.Parameters.AddWithValue("$rowId", rowId);
            insert.ExecuteNonQuery();
        }

        foreach (string supersededId in entry.SupersedesIds)
        {
            using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM virtual_context_active WHERE id=$id;";
            delete.Parameters.AddWithValue("$id", supersededId);
            delete.ExecuteNonQuery();
        }
    }

    private static bool IsProjectedKind(string kind) =>
        kind == VirtualContextLedgerKind.Constraint
        || kind == VirtualContextLedgerKind.Decision
        || kind == VirtualContextLedgerKind.Contradiction;

    public VirtualContextLedgerEntryDto? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return GetInternal(connection, id.Trim());
        }
    }

    /// <summary>
    /// Returns the most recent bounded audit window in Entries, but reconstructs active state from
    /// the complete durable projection. maxEntries therefore never changes supersession truth.
    /// </summary>
    public VirtualContextLedgerStateResponse GetState(string scope, string scopeId, int maxEntries = 1000)
    {
        string normalizedScope = NormalizeToken(scope, 40, "scope").ToLowerInvariant();
        string normalizedScopeId = NormalizeToken(scopeId, 256, "scopeId");
        int limit = Math.Clamp(maxEntries, 1, 10_000);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            List<VirtualContextLedgerEntryDto> entries = LoadHistoryWindow(connection, normalizedScope, normalizedScopeId, limit);
            List<VirtualContextLedgerEntryDto> activeConstraints = LoadActiveEntries(connection, normalizedScope, normalizedScopeId, VirtualContextLedgerKind.Constraint);
            List<VirtualContextLedgerEntryDto> activeDecisions = LoadActiveEntries(connection, normalizedScope, normalizedScopeId, VirtualContextLedgerKind.Decision);
            List<VirtualContextLedgerEntryDto> unresolvedContradictions = LoadActiveEntries(connection, normalizedScope, normalizedScopeId, VirtualContextLedgerKind.Contradiction);
            string promptText = BuildPromptFromProjection(connection, normalizedScope, normalizedScopeId, 16_000);

            return new VirtualContextLedgerStateResponse
            {
                Scope = normalizedScope,
                ScopeId = normalizedScopeId,
                Entries = entries,
                ActiveConstraints = activeConstraints,
                ActiveDecisions = activeDecisions,
                UnresolvedContradictions = unresolvedContradictions,
                PromptText = promptText
            };
        }
    }

    /// <summary>
    /// Hot-path prompt projection. It never materializes the full active state or replays ledger
    /// history; only bounded newest facts are read through the active-state index.
    /// </summary>
    public string BuildPromptText(string scope, string scopeId, int maxCharacters = 16_000)
    {
        string normalizedScope = NormalizeToken(scope, 40, "scope").ToLowerInvariant();
        string normalizedScopeId = NormalizeToken(scopeId, 256, "scopeId");
        int max = Math.Clamp(maxCharacters, 1_000, 64_000);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return BuildPromptFromProjection(connection, normalizedScope, normalizedScopeId, max);
        }
    }

    private static List<VirtualContextLedgerEntryDto> LoadHistoryWindow(
        SqliteConnection connection, string scope, string scopeId, int limit)
    {
        var ids = new List<string>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = @"SELECT id FROM virtual_context_ledger
WHERE scope=$scope AND scope_id=$scopeId ORDER BY rowid DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$scopeId", scopeId);
            command.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        ids.Reverse();
        return ids.Select(id => GetInternal(connection, id)!).ToList();
    }

    private static List<VirtualContextLedgerEntryDto> LoadActiveEntries(
        SqliteConnection connection, string scope, string scopeId, string kind)
    {
        var ids = new List<string>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = @"SELECT id FROM virtual_context_active
WHERE scope=$scope AND scope_id=$scopeId AND kind=$kind ORDER BY row_id ASC;";
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$scopeId", scopeId);
            command.Parameters.AddWithValue("$kind", kind);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        return ids.Select(id => GetInternal(connection, id)!).ToList();
    }

    private static string BuildPromptFromProjection(
        SqliteConnection connection, string scope, string scopeId, int maxCharacters)
    {
        (int contradictionCount, List<VirtualContextLedgerEntryDto> contradictions) =
            LoadNewestProjectedEntries(connection, scope, scopeId, VirtualContextLedgerKind.Contradiction, PromptContradictionLimit);
        (int constraintCount, List<VirtualContextLedgerEntryDto> constraints) =
            LoadNewestProjectedEntries(connection, scope, scopeId, VirtualContextLedgerKind.Constraint, PromptConstraintLimit);
        (int decisionCount, List<VirtualContextLedgerEntryDto> decisions) =
            LoadNewestProjectedEntries(connection, scope, scopeId, VirtualContextLedgerKind.Decision, PromptDecisionLimit);

        if (contradictionCount == 0 && constraintCount == 0 && decisionCount == 0) return string.Empty;

        var prompt = new StringBuilder();
        prompt.AppendLine(PromptOpen);
        AppendPromptSection(
            prompt,
            "UNRESOLVED CONTRADICTIONS - do not silently choose a side",
            contradictionCount,
            contradictions,
            item => "- " + item.Text + " contradicts " + string.Join(",", item.ContradictsIds));
        AppendPromptSection(
            prompt,
            "Active constraints",
            constraintCount,
            constraints,
            item => "- " + item.Text + " [" + item.Id + "]");
        AppendPromptSection(
            prompt,
            "Active decisions",
            decisionCount,
            decisions,
            item => "- " + item.Text + " [" + item.Id + "]");
        prompt.AppendLine(PromptClose);
        return BoundPrompt(prompt.ToString(), maxCharacters);
    }

    private static void AppendPromptSection(
        StringBuilder prompt,
        string title,
        int totalCount,
        IReadOnlyList<VirtualContextLedgerEntryDto> newestEntries,
        Func<VirtualContextLedgerEntryDto, string> formatter)
    {
        if (totalCount == 0) return;
        prompt.Append(title).Append(" (newest first; showing ")
            .Append(newestEntries.Count).Append(" of ").Append(totalCount).AppendLine("):");
        foreach (VirtualContextLedgerEntryDto item in newestEntries)
            prompt.AppendLine(formatter(item));
        int omitted = totalCount - newestEntries.Count;
        if (omitted > 0)
            prompt.Append("… ").Append(omitted).AppendLine(" older active facts omitted from the prompt projection; durable state remains retained.");
    }

    private static (int TotalCount, List<VirtualContextLedgerEntryDto> Entries) LoadNewestProjectedEntries(
        SqliteConnection connection, string scope, string scopeId, string kind, int limit)
    {
        int totalCount;
        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = @"SELECT COUNT(*) FROM virtual_context_active
WHERE scope=$scope AND scope_id=$scopeId AND kind=$kind;";
            count.Parameters.AddWithValue("$scope", scope);
            count.Parameters.AddWithValue("$scopeId", scopeId);
            count.Parameters.AddWithValue("$kind", kind);
            totalCount = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        var ids = new List<string>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = @"SELECT id FROM virtual_context_active
WHERE scope=$scope AND scope_id=$scopeId AND kind=$kind
ORDER BY row_id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$scopeId", scopeId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        return (totalCount, ids.Select(id => GetInternal(connection, id)!).ToList());
    }

    private static VirtualContextLedgerEntryDto? GetInternal(
        SqliteConnection connection, string id, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT scope,scope_id,kind,text,source,supersedes_json,contradicts_json,content_sha256,created_utc
FROM virtual_context_ledger WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var dto = new VirtualContextLedgerEntryDto
        {
            Id = id,
            Scope = reader.GetString(0),
            ScopeId = reader.GetString(1),
            Kind = reader.GetString(2),
            Text = reader.GetString(3),
            Source = reader.GetString(4),
            SupersedesIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(5), JsonOptions) ?? new(),
            ContradictsIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(6), JsonOptions) ?? new(),
            ContentSha256 = reader.GetString(7),
            CreatedUtc = DateTime.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()
        };
        string expected = Hash(dto.Id, dto.Scope, dto.ScopeId, dto.Kind, dto.Text, dto.Source,
            dto.SupersedesIds, dto.ContradictsIds, dto.CreatedUtc);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(dto.ContentSha256)))
            throw new InvalidDataException("Virtual Context ledger hash mismatch; append-only state may have been modified.");
        return dto;
    }

    private static string NormalizeKind(string value)
    {
        string kind = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (!VirtualContextLedgerKind.All.Contains(kind))
            throw new InvalidDataException("Unknown ledger kind. Use constraint, decision, supersession, contradiction, tombstone or note.");
        return kind;
    }

    private static string NormalizeToken(string? value, int max, string field)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is < 1 || normalized.Length > max || normalized.Any(char.IsControl))
            throw new InvalidDataException(field + " is required, contains no control characters and must be <= " + max + " characters.");
        return normalized;
    }

    private static string NormalizeText(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Length is < 1 or > 8_000)
            throw new InvalidDataException("Ledger text must contain 1-8,000 characters.");
        return text;
    }

    private static string NormalizeSource(string? value)
    {
        string source = string.IsNullOrWhiteSpace(value) ? "user" : value.Trim();
        if (source.Length > 256 || source.Any(char.IsControl))
            throw new InvalidDataException("source must be <= 256 characters and contain no control characters.");
        return source;
    }

    private static List<string> NormalizeReferences(IEnumerable<string>? values, string field)
    {
        List<string> result = (values ?? Array.Empty<string>()).Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (result.Count > 32) throw new InvalidDataException(field + " may contain at most 32 unique ids.");
        if (result.Any(x => x.Length > 128 || x.Any(char.IsControl)))
            throw new InvalidDataException(field + " contains an invalid id.");
        return result;
    }

    private static string Hash(string id, string scope, string scopeId, string kind, string text,
        string source, IReadOnlyList<string> supersedes, IReadOnlyList<string> contradicts, DateTime createdUtc)
    {
        string canonical = string.Join("\n", new[]
        {
            id, scope, scopeId, kind, text, source,
            string.Join(",", supersedes), string.Join(",", contradicts), createdUtc.ToUniversalTime().ToString("O")
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string BoundPrompt(string value, int max)
    {
        if (value.Length <= max) return value;
        string suffix = "\n… [Virtual Context V2 bounded; newest projected facts kept first]\n" + PromptClose + "\n";
        int take = Math.Max(0, max - suffix.Length);
        return value[..take].TrimEnd() + suffix;
    }
}