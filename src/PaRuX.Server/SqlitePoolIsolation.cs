using Microsoft.Data.Sqlite;

namespace PaRuX.Server;

/// <summary>
/// SQLite pool cleanup for backup/restore paths. These helpers deliberately never call
/// ClearAllPools: a backup of one PaRuX store must not invalidate pooled handles belonging to
/// conversations, memory, agents, or another database that happens to be active in the process.
/// </summary>
internal static class SqlitePoolIsolation
{
    /// <summary>
    /// Removes the pool associated with the exact destination connection used for an online
    /// backup. Call while the connection is still open; Microsoft.Data.Sqlite will then dispose
    /// that physical connection on Close instead of returning it to the pool.
    /// </summary>
    internal static void ReleaseBackupDestination(SqliteConnection destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        SqliteConnection.ClearPool(destination);
        destination.Close();
    }

    /// <summary>
    /// Clears only the connection-string variants PaRuX uses for one live database before a file
    /// replacement. The common store path uses shared cache; the non-shared/read-only variants
    /// cover migration/validation helpers without touching pools for any other database path.
    /// </summary>
    internal static void ClearDatabasePools(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        foreach (string connectionString in ConnectionStringsFor(databasePath))
        {
            using var key = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(key);
        }
    }

    internal static IReadOnlyList<string> ConnectionStringsFor(string databasePath) => new[]
    {
        "Data Source=" + databasePath + ";Mode=ReadWriteCreate;Cache=Shared",
        "Data Source=" + databasePath + ";Mode=ReadWriteCreate",
        "Data Source=" + databasePath + ";Mode=ReadWrite;Cache=Shared",
        "Data Source=" + databasePath + ";Mode=ReadWrite",
        "Data Source=" + databasePath + ";Mode=ReadOnly;Cache=Shared",
        "Data Source=" + databasePath + ";Mode=ReadOnly",
        "Data Source=" + databasePath + ";Cache=Shared",
        "Data Source=" + databasePath
    };
}
