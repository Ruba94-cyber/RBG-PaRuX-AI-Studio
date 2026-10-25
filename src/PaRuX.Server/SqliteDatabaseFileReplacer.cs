namespace PaRuX.Server;

/// <summary>
/// Swaps a SQLite database file for one restored from a backup.
///
/// Every store that can be restored had its own byte-identical copy of this, and each copy leaked:
/// the staging file was created with <c>File.Copy</c> and only removed by the final
/// <c>File.Move</c>, so any failure in between - a locked destination, a full disk, the process
/// dying - left a full-size copy of the database stranded next to it forever, with nothing that
/// would ever clean it up. Found as a real 73 KB `parux.db.import-tmp` sitting in this machine's
/// data directory from a failed import ten days earlier; on a large conversation or artifact store
/// the stranded copy is the size of the whole database.
///
/// Restore is a backup-safety path, so the failure behaviour matters as much as the success one:
/// the staging file is cleaned up on any failure and the original exception is preserved, a stale
/// staging file from an earlier crash is removed before starting rather than silently overwritten,
/// and the WAL/SHM sidecars are still dropped because they describe the previous database's page
/// content and must never be replayed against the new file. Pool cleanup is scoped to the database
/// being replaced so an unrelated active PaRuX store is never disrupted by this operation.
/// </summary>
public static class SqliteDatabaseFileReplacer
{
    public const string StagingSuffix = ".import-tmp";

    public static string StagingPathFor(string databasePath) => databasePath + StagingSuffix;

    public static void Replace(string databasePath, string databaseDirectory, string sourceFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);

        SqlitePoolIsolation.ClearDatabasePools(databasePath);
        Directory.CreateDirectory(databaseDirectory);

        string staging = StagingPathFor(databasePath);
        TryDelete(staging);
        try
        {
            File.Copy(sourceFilePath, staging, overwrite: true);
            // Move first: if it fails, the original database and its WAL/SHM stay intact and consistent.
            File.Move(staging, databasePath, overwrite: true);
            // Only now drop the old database's sidecars; they must never be replayed against the new file.
            // A sidecar that cannot be removed is an error (fail closed), not something to ignore.
            foreach (string sidecar in new[] { databasePath + "-wal", databasePath + "-shm" })
                if (File.Exists(sidecar)) File.Delete(sidecar);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
