using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PaRuX.Server;

/// <summary>
/// Per-connection check that the tables a store needs still exist in the database it just opened.
/// A store that caches "schema initialized" on a long-lived instance must not trust that flag alone:
/// the database file can be deleted, replaced by a restore, or repaired by hand after the flag was
/// set, and the reopened file is then empty. Callers keep the flag only as a fast path together with
/// this check, and re-run their idempotent CREATE ... IF NOT EXISTS body when any table is missing.
/// </summary>
internal static class SqliteSchemaGuard
{
    /// <summary>True only when every named table exists in the main database of <paramref name="connection"/>.</summary>
    internal static bool HasAllTables(SqliteConnection connection, params string[] tableNames)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tableNames);

        var distinct = new HashSet<string>(tableNames, StringComparer.Ordinal);
        if (distinct.Count == 0) return true;

        using SqliteCommand command = connection.CreateCommand();
        var placeholders = new List<string>(distinct.Count);
        int index = 0;
        foreach (string name in distinct)
        {
            string placeholder = "$table" + index;
            index++;
            placeholders.Add(placeholder);
            command.Parameters.AddWithValue(placeholder, name);
        }
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ("
            + string.Join(", ", placeholders) + ");";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == distinct.Count;
    }

    /// <summary>
    /// Snapshot-path guard for a store that already initialized its schema in this process. Opening a missing
    /// database creates an empty file, and the next schema check recreates the tables in it, so a backup copied
    /// from that file would present an empty store as a complete snapshot. A removed file is therefore an error
    /// here, which makes BackupService record the store as unsnapshottable. A store that never initialized keeps
    /// the original behavior and may still create its file.
    /// </summary>
    internal static void ThrowIfInitializedDatabaseIsMissing(string databasePath, bool storeInitialized)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        if (storeInitialized && !File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                "The database file was removed after its store was initialized, so a snapshot would only capture a recreated empty store.",
                databasePath);
        }
    }
}
