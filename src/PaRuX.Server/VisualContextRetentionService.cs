using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Privacy/storage policy for automatically captured desktop evidence. Explicit/user-provided
/// Visual Context snapshots are deliberately outside this retention policy and are never removed
/// automatically. Computer Use and UI Verification captures are transient operational evidence:
/// keep enough history for debugging/before-after review, but do not turn the workstation screen
/// into an unbounded archive.
/// </summary>
public sealed class VisualContextRetentionService
{
    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromDays(14);
    public const int DefaultMaximumPerScope = 64;
    public const int DefaultMaximumGeneratedSnapshots = 1_000;
    public const long DefaultMaximumGeneratedBytes = 256L * 1024 * 1024;

    private static readonly object Gate = new();
    private readonly string _databasePath;
    private readonly TimeSpan _maximumAge;
    private readonly int _maximumPerScope;
    private readonly int _maximumGeneratedSnapshots;
    private readonly long _maximumGeneratedBytes;

    public VisualContextRetentionService()
        : this(ServerState.StateDirectory) { }

    internal VisualContextRetentionService(
        string stateDirectory,
        TimeSpan? maximumAge = null,
        int maximumPerScope = DefaultMaximumPerScope,
        int maximumGeneratedSnapshots = DefaultMaximumGeneratedSnapshots,
        long maximumGeneratedBytes = DefaultMaximumGeneratedBytes)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory)) throw new ArgumentException("State directory is required.", nameof(stateDirectory));
        _databasePath = Path.Combine(Path.GetFullPath(stateDirectory), "visual-context-v2.db");
        _maximumAge = maximumAge ?? DefaultMaximumAge;
        if (_maximumAge <= TimeSpan.Zero || _maximumAge > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        _maximumPerScope = Math.Clamp(maximumPerScope, 1, 10_000);
        _maximumGeneratedSnapshots = Math.Clamp(maximumGeneratedSnapshots, 1, 100_000);
        _maximumGeneratedBytes = Math.Clamp(maximumGeneratedBytes, 1L * 1024 * 1024, 8L * 1024 * 1024 * 1024);
    }

    public VisualContextRetentionStatus GetStatus()
    {
        lock (Gate)
        {
            if (!File.Exists(_databasePath)) return EmptyStatus(0);
            using SqliteConnection connection = Open();
            if (!HasSnapshotTable(connection)) return EmptyStatus(0);
            (int count, long bytes) = CountGenerated(connection);
            return BuildStatus(0, count, bytes);
        }
    }

    public VisualContextRetentionStatus PruneGeneratedEvidence(DateTime? utcNow = null)
    {
        DateTime now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
        lock (Gate)
        {
            if (!File.Exists(_databasePath)) return EmptyStatus(0);
            using SqliteConnection connection = Open();
            if (!HasSnapshotTable(connection)) return EmptyStatus(0);

            int deleted = DeleteExpired(connection, now - _maximumAge);
            deleted += DeletePerScopeOverflow(connection);
            deleted += DeleteGlobalOverflow(connection);

            (int count, long bytes) = CountGenerated(connection);
            return BuildStatus(deleted, count, bytes);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=" + _databasePath + ";Mode=ReadWrite;Cache=Shared");
        connection.Open();
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static bool HasSnapshotTable(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='visual_snapshots';";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0) > 0;
    }

    private static int DeleteExpired(SqliteConnection connection, DateTime cutoffUtc)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"DELETE FROM visual_snapshots
WHERE lower(source) IN ('computer-use','ui-verification') AND created_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.ToString("O"));
        return command.ExecuteNonQuery();
    }

    private int DeletePerScopeOverflow(SqliteConnection connection)
    {
        var byScope = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = @"SELECT id,scope,scope_id FROM visual_snapshots
WHERE lower(source) IN ('computer-use','ui-verification')
ORDER BY scope ASC, scope_id ASC, created_utc DESC, id DESC;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(1) + "\0" + reader.GetString(2);
                if (!byScope.TryGetValue(key, out List<string>? ids))
                {
                    ids = new List<string>();
                    byScope[key] = ids;
                }
                ids.Add(reader.GetString(0));
            }
        }

        var delete = new List<string>();
        foreach (List<string> ids in byScope.Values)
            if (ids.Count > _maximumPerScope) delete.AddRange(ids.Skip(_maximumPerScope));
        return DeleteIds(connection, delete);
    }

    private int DeleteGlobalOverflow(SqliteConnection connection)
    {
        var rows = new List<(string Id, long Bytes)>();
        long totalBytes = 0;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = @"SELECT id,length(bytes) FROM visual_snapshots
WHERE lower(source) IN ('computer-use','ui-verification')
ORDER BY created_utc ASC, id ASC;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                long bytes = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                rows.Add((reader.GetString(0), bytes));
                totalBytes = checked(totalBytes + Math.Max(0, bytes));
            }
        }

        int removeCount = 0;
        long remainingBytes = totalBytes;
        while (removeCount < rows.Count
            && (rows.Count - removeCount > _maximumGeneratedSnapshots || remainingBytes > _maximumGeneratedBytes))
        {
            remainingBytes -= rows[removeCount].Bytes;
            removeCount++;
        }
        return removeCount == 0
            ? 0
            : DeleteIds(connection, rows.Take(removeCount).Select(row => row.Id));
    }

    private static int DeleteIds(SqliteConnection connection, IEnumerable<string> ids)
    {
        int deleted = 0;
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM visual_snapshots WHERE id=$id;";
        SqliteParameter parameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (string id in ids.Distinct(StringComparer.Ordinal))
        {
            parameter.Value = id;
            deleted += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return deleted;
    }

    private static (int Count, long Bytes) CountGenerated(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT COUNT(*),COALESCE(SUM(length(bytes)),0) FROM visual_snapshots
WHERE lower(source) IN ('computer-use','ui-verification');";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return (0, 0);
        return (reader.GetInt32(0), reader.GetInt64(1));
    }

    private VisualContextRetentionStatus EmptyStatus(int deleted) => BuildStatus(deleted, 0, 0);

    private VisualContextRetentionStatus BuildStatus(int deleted, int count, long bytes) => new()
    {
        GeneratedMaximumAgeDays = (int)Math.Ceiling(_maximumAge.TotalDays),
        MaximumGeneratedSnapshotsPerScope = _maximumPerScope,
        MaximumGeneratedSnapshots = _maximumGeneratedSnapshots,
        MaximumGeneratedBytes = _maximumGeneratedBytes,
        CurrentGeneratedSnapshots = count,
        CurrentGeneratedBytes = bytes,
        DeletedSnapshots = deleted,
        ExplicitSnapshotsAutoDeleted = false
    };
}
