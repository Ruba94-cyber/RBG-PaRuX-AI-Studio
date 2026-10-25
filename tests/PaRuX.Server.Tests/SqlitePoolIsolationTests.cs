using Microsoft.Data.Sqlite;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SqlitePoolIsolationTests
{
    [Fact]
    public void Releasing_one_backup_and_clearing_one_database_preserves_unrelated_pooled_store()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-sqlite-pool-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string targetPath = Path.Combine(root, "target.db");
        string backupPath = Path.Combine(root, "backup.db");
        string unrelatedPath = Path.Combine(root, "unrelated.db");
        string unrelatedConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = unrelatedPath,
            Pooling = true
        }.ToString();

        try
        {
            // A TEMP table belongs to the physical SQLite connection rather than the database
            // file. Reopening must reuse the unrelated pooled handle; process-wide ClearAllPools
            // would discard it and make the table disappear.
            using (var unrelated = new SqliteConnection(unrelatedConnectionString))
            {
                unrelated.Open();
                using var create = unrelated.CreateCommand();
                create.CommandText = "CREATE TEMP TABLE preserved(value TEXT NOT NULL); INSERT INTO preserved VALUES ('still-here');";
                create.ExecuteNonQuery();
            }

            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = targetPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString()))
            {
                target.Open();
                using var create = target.CreateCommand();
                create.CommandText = "CREATE TABLE target_probe(value INTEGER);";
                create.ExecuteNonQuery();
            }

            using (var backupDestination = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
            {
                backupDestination.Open();
                using var create = backupDestination.CreateCommand();
                create.CommandText = "CREATE TABLE backup_probe(value INTEGER);";
                create.ExecuteNonQuery();

                SqlitePoolIsolation.ReleaseBackupDestination(backupDestination);

                Assert.Equal(System.Data.ConnectionState.Closed, backupDestination.State);
            }

            AssertUnrelatedPoolRetained(unrelatedConnectionString);

            // The database-file replacement path must clear the target's connection-string
            // variants only; the unrelated pooled file connection must remain reusable.
            SqlitePoolIsolation.ClearDatabasePools(targetPath);
            File.Delete(targetPath);
            Assert.False(File.Exists(targetPath));
            AssertUnrelatedPoolRetained(unrelatedConnectionString);

            File.Delete(backupPath);
            Assert.False(File.Exists(backupPath));
        }
        finally
        {
            using var unrelatedPoolKey = new SqliteConnection(unrelatedConnectionString);
            SqliteConnection.ClearPool(unrelatedPoolKey);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ConnectionStringsFor_includes_all_store_modes_including_ReadWrite_shared_cache()
    {
        string path = @"C:\data\test.db";
        IReadOnlyList<string> variants = SqlitePoolIsolation.ConnectionStringsFor(path);
        Assert.Contains("Data Source=" + path + ";Mode=ReadWrite;Cache=Shared", variants);
        Assert.Contains("Data Source=" + path + ";Mode=ReadWrite", variants);
        Assert.Contains("Data Source=" + path + ";Mode=ReadWriteCreate;Cache=Shared", variants);
        Assert.Contains("Data Source=" + path + ";Mode=ReadOnly;Cache=Shared", variants);
    }

    private static void AssertUnrelatedPoolRetained(string connectionString)
    {
        using var unrelated = new SqliteConnection(connectionString);
        unrelated.Open();
        using var verify = unrelated.CreateCommand();
        verify.CommandText = "SELECT value FROM preserved;";
        Assert.Equal("still-here", verify.ExecuteScalar());
    }
}
