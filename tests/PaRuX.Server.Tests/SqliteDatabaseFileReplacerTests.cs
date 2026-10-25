using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Backup restore used to stage the incoming database with File.Copy and rely on the final
/// File.Move to remove it. Any failure in between - a locked destination, a full disk, the process
/// dying - stranded a full-size copy of the database next to it permanently, since nothing else
/// ever looked at that path again. Found for real: a 73 KB `parux.db.import-tmp` left in this
/// machine's data directory by an import that failed ten days earlier.
///
/// Sixteen stores each carried a byte-identical copy of that logic, so the leak was sixteen leaks.
/// </summary>
public sealed class SqliteDatabaseFileReplacerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-replacer-" + Guid.NewGuid().ToString("N"));

    public SqliteDatabaseFileReplacerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_successful_restore_replaces_the_database_and_leaves_no_staging_file()
    {
        string database = Write("store.db", "old");
        string source = Write("backup.db", "restored");

        SqliteDatabaseFileReplacer.Replace(database, _root, source);

        Assert.Equal("restored", File.ReadAllText(database));
        Assert.False(File.Exists(SqliteDatabaseFileReplacer.StagingPathFor(database)));
    }

    [Fact]
    public void The_stale_wal_and_shm_sidecars_of_the_previous_database_are_dropped()
    {
        // They describe the replaced database's page content; replaying them against the restored
        // file would corrupt it.
        string database = Write("store.db", "old");
        string wal = Write("store.db-wal", "stale wal");
        string shm = Write("store.db-shm", "stale shm");
        string source = Write("backup.db", "restored");

        SqliteDatabaseFileReplacer.Replace(database, _root, source);

        Assert.False(File.Exists(wal));
        Assert.False(File.Exists(shm));
    }

    [Fact]
    public void A_failed_restore_leaves_no_stranded_copy_of_the_database()
    {
        string database = Path.Combine(_root, "store.db");
        File.WriteAllText(database, "original");
        string source = Path.Combine(_root, "does-not-exist.db");

        Assert.ThrowsAny<Exception>(() => SqliteDatabaseFileReplacer.Replace(database, _root, source));

        Assert.False(File.Exists(SqliteDatabaseFileReplacer.StagingPathFor(database)));
        // The failure must also be non-destructive: the live database is untouched.
        Assert.Equal("original", File.ReadAllText(database));
    }

    [Fact]
    public void A_staging_file_left_by_an_earlier_crash_does_not_survive_the_next_restore()
    {
        string database = Write("store.db", "old");
        string staging = SqliteDatabaseFileReplacer.StagingPathFor(database);
        File.WriteAllText(staging, "orphan from a previous failed import");
        string source = Write("backup.db", "restored");

        SqliteDatabaseFileReplacer.Replace(database, _root, source);

        Assert.Equal("restored", File.ReadAllText(database));
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public void Restoring_into_a_directory_that_does_not_exist_yet_creates_it()
    {
        string directory = Path.Combine(_root, "nested", "state");
        string database = Path.Combine(directory, "store.db");
        string source = Write("backup.db", "restored");

        SqliteDatabaseFileReplacer.Replace(database, directory, source);

        Assert.Equal("restored", File.ReadAllText(database));
    }

    [Fact]
    public void A_failed_move_keeps_the_original_database_and_its_wal_and_shm_intact()
    {
        string database = Write("store.db", "old");
        string wal = Write("store.db-wal", "live wal");
        string shm = Write("store.db-shm", "live shm");
        string source = Write("backup.db", "restored");

        // Another handle without delete sharing makes the final move fail.
        using (new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => SqliteDatabaseFileReplacer.Replace(database, _root, source));
        }

        Assert.Equal("old", File.ReadAllText(database));
        Assert.Equal("live wal", File.ReadAllText(wal));
        Assert.Equal("live shm", File.ReadAllText(shm));
        Assert.False(File.Exists(SqliteDatabaseFileReplacer.StagingPathFor(database)));
    }
}
