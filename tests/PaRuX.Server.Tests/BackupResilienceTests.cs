using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.Automation;
using PaRuX.Server.Autopilot;
using PaRuX.Server.AutoLab;
using PaRuX.Server.CapabilityProfiler;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.EvalLab;
using PaRuX.Server.Orchestration;
using PaRuX.Server.Workflow;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-15 S2: backup/restore under concurrency, cancellation, locked files and restart. Every
/// store, the client root and the model directory live in a private temp directory; nothing here
/// can touch the real %LOCALAPPDATA%\PaRuX. A "restart" builds brand-new store instances over the
/// same directories, which is what a process restart observes.
/// </summary>
public sealed class BackupResilienceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-backup-resilience-" + Guid.NewGuid().ToString("N"));
    private string ServerDir => Path.Combine(_root, "server");
    private string ClientDir => Path.Combine(_root, "client");
    private string ModelsDir => Path.Combine(_root, "models");

    public BackupResilienceTests()
    {
        Directory.CreateDirectory(ServerDir);
        Directory.CreateDirectory(ClientDir);
        Directory.CreateDirectory(ModelsDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    internal sealed class Instance : IDisposable
    {
        public required ConversationStore Conversations { get; init; }
        public required MemoryStore Memory { get; init; }
        public required BackupService Backups { get; init; }
        private readonly McpClientRegistry _mcp;
        public Instance(McpClientRegistry mcp) => _mcp = mcp;
        public void Dispose() => _mcp.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    internal sealed class FakeHost : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "PaRuX.Server.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private Instance Start() => Build(ServerDir, ClientDir, ModelsDir);

    internal static Instance Build(string serverDir, string clientDir, string modelsDir)
    {
        string s = serverDir;
        var embeddings = new EmbeddingService();
        var conversations = new ConversationStore(s);
        var memory = new MemoryStore(embeddings, s);
        var mcp = new McpClientRegistry(s);
        var backups = new BackupService(
            conversations, memory, new RagStore(embeddings, s),
            new TaskStore(s), new ContextGraphStore(s), new ArtifactStore(s), new AutomationStore(s),
            new AutopilotStore(s), new EvalStore(s), new AdapterStore(s), new AutoLabStore(s),
            new WorkflowStore(s), new CapabilityProfilerStore(s), new ModelBenchmarkStore(s),
            new PersonaService(s), mcp, new InternetSettingsService(s),
            new RemoteAccessService(Path.Combine(s, "remote-access.json")),
            new SkillRegistryService(s), new CustomWorkflowService(s),
            new AiProfileService(
                new LocalRuntimeService(new FakeHost(), new LocalAiProcessManager(), new ExternalTextProviderService()), s),
            clientDir, modelsDir);
        return new Instance(mcp) { Conversations = conversations, Memory = memory, Backups = backups };
    }

    private static void SaveConversation(ConversationStore store, string id, string title) =>
        store.Save(id, new ConversationUpsertRequest
        {
            Title = title, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, DocumentJson = "{}"
        });

    private static void AssertIntegrityOk(string dbPath)
    {
        using var c = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly;Pooling=False");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", (string?)cmd.ExecuteScalar());
    }

    private static List<string> ConversationTitles(string dbPath)
    {
        var titles = new List<string>();
        using var c = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly;Pooling=False");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT title FROM conversations ORDER BY title;";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read()) titles.Add(r.GetString(0));
        return titles;
    }

    private static void CreateClientHistory(string path, string id, string title)
    {
        using var c = new SqliteConnection("Data Source=" + path + ";Mode=ReadWriteCreate;Pooling=False");
        c.Open();
        using SqliteCommand create = c.CreateCommand();
        create.CommandText = @"CREATE TABLE conversations (
            id TEXT PRIMARY KEY, title TEXT NOT NULL, created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL, document_json TEXT NOT NULL);
            INSERT INTO conversations VALUES ('" + id + "','" + title + "','2026-01-01','2026-01-01','{}');";
        create.ExecuteNonQuery();
    }

    private string ExtractEntry(byte[] zip, string entry)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        string path = Path.Combine(_root, "extract-" + Guid.NewGuid().ToString("N") + ".db");
        archive.GetEntry(entry)!.ExtractToFile(path);
        return path;
    }

    [Fact]
    public async Task Export_cancelled_before_or_during_snapshots_throws_and_leaves_live_stores_untouched()
    {
        using Instance app = Start();
        SaveConversation(app.Conversations, "c1", "keep-me");

        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.Backups.ExportAsync(preCancelled.Token));

        // Cancel while the snapshot loop is running: repeat with growing delays so the token fires at
        // different points. Each attempt must either finish or cancel cleanly, never corrupt.
        for (int delayMs = 0; delayMs < 40; delayMs += 8)
        {
            using var cts = new CancellationTokenSource();
            Task<byte[]> export = Task.Run(() => app.Backups.ExportAsync(cts.Token));
            cts.CancelAfter(delayMs);
            try { await export; }
            catch (OperationCanceledException) { }
        }

        Assert.Equal("keep-me", app.Conversations.Get("c1")!.Title);
        byte[] fine = await app.Backups.ExportAsync(CancellationToken.None);
        Assert.Contains("keep-me", ConversationTitles(ExtractEntry(fine, "conversations.db")));
    }

    [Fact]
    public async Task Concurrent_writers_during_repeated_exports_still_produce_consistent_archives()
    {
        using Instance app = Start();
        using var stop = new CancellationTokenSource();
        int written = 0;
        Task writer = Task.Run(() =>
        {
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                SaveConversation(app.Conversations, "w" + i, "chat-" + i);
                Interlocked.Increment(ref written);
            }
        });
        SpinWait.SpinUntil(() => Volatile.Read(ref written) > 5, TimeSpan.FromSeconds(10));

        var counts = new List<int>();
        for (int run = 0; run < 5; run++)
        {
            byte[] zip = await app.Backups.ExportAsync(CancellationToken.None);
            string db = ExtractEntry(zip, "conversations.db");
            AssertIntegrityOk(db);
            List<string> titles = ConversationTitles(db);
            counts.Add(titles.Count);
            Assert.Equal(titles.Count, titles.Distinct().Count());
        }

        stop.Cancel();
        await writer;
        Assert.True(counts[^1] >= counts[0], "later exports must not lose rows written before earlier ones: " + string.Join(",", counts));
        Assert.Equal(Volatile.Read(ref written), app.Conversations.List().Count);
    }

    [Fact]
    public async Task Import_cancelled_before_commit_changes_nothing()
    {
        using Instance app = Start();
        SaveConversation(app.Conversations, "a", "in-archive");
        byte[] zip = await app.Backups.ExportAsync(CancellationToken.None);
        SaveConversation(app.Conversations, "b", "added-after-backup");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => app.Backups.ImportAsync(new MemoryStream(zip), cts.Token));

        Assert.NotNull(app.Conversations.Get("b"));
        Assert.Empty(Directory.GetFiles(ServerDir, "*" + SqliteDatabaseFileReplacer.StagingSuffix));
    }

    [Fact]
    public async Task Cancellation_requested_after_the_commit_boundary_is_ignored_so_no_partial_restore_results()
    {
        using Instance app = Start();
        SaveConversation(app.Conversations, "a", "in-archive");
        app.Memory.Add(new MemoryAddRequest { Scope = "User", Content = "memory-in-archive" });
        byte[] zip = await app.Backups.ExportAsync(CancellationToken.None);
        app.Conversations.Delete("a");
        foreach (MemoryEntryDto m in app.Memory.List()) app.Memory.Delete(m.Id);

        using var cts = new CancellationTokenSource();
        var steps = new List<string>();
        app.Backups.CommitStepObserver = label =>
        {
            steps.Add(label);
            cts.Cancel(); // client disconnects as soon as the first replace step finishes
        };

        BackupImportResult result = await app.Backups.ImportAsync(new MemoryStream(zip), cts.Token);

        Assert.True(result.Success);
        Assert.True(steps.Count > 5, "every restore step must still run after cancellation: " + steps.Count);
        Assert.Equal("in-archive", app.Conversations.Get("a")!.Title);
        Assert.Contains(app.Memory.List(), m => m.Content == "memory-in-archive");
    }

    [Fact]
    public async Task Locked_client_history_fails_closed_before_any_store_is_replaced()
    {
        using Instance app = Start();
        SaveConversation(app.Conversations, "a", "in-archive");
        byte[] zip = await app.Backups.ExportAsync(CancellationToken.None);
        SaveConversation(app.Conversations, "b", "added-after-backup");

        string clientDb = Path.Combine(ClientDir, "parux.db");
        CreateClientHistory(clientDb, "client-1", "client-original");

        using var locker = new SqliteConnection("Data Source=" + clientDb + ";Mode=ReadWrite;Pooling=False");
        locker.Open();
        using (SqliteCommand lockCmd = locker.CreateCommand())
        {
            lockCmd.CommandText = "BEGIN EXCLUSIVE;";
            lockCmd.ExecuteNonQuery();
        }

        var sw = Stopwatch.StartNew();
        IOException error = await Assert.ThrowsAsync<IOException>(
            () => app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None));
        sw.Stop();

        Assert.Contains("nothing was changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "the lock probe must use a short bounded wait: " + sw.Elapsed);
        Assert.NotNull(app.Conversations.Get("b")); // server store untouched
        using (SqliteCommand rollback = locker.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }
        Assert.Equal(new[] { "client-original" }, ConversationTitles(clientDb));
        Assert.Empty(Directory.GetFiles(ServerDir, "*" + SqliteDatabaseFileReplacer.StagingSuffix));
        Assert.Empty(Directory.GetFiles(ClientDir, "*" + SqliteDatabaseFileReplacer.StagingSuffix));

        // After the lock clears the same archive restores cleanly.
        BackupImportResult retry = await app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None);
        Assert.True(retry.Success);
        Assert.Null(app.Conversations.Get("b"));
    }

    [Fact]
    public async Task A_database_file_held_open_by_a_real_child_process_fails_the_replace_and_keeps_the_original()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows share-mode locking semantics.

        using Instance app = Start();
        SaveConversation(app.Conversations, "a", "in-archive");
        byte[] zip = await app.Backups.ExportAsync(CancellationToken.None);
        SaveConversation(app.Conversations, "b", "added-after-backup");
        SqliteConnection.ClearAllPools();

        string dbPath = Path.Combine(ServerDir, "conversations.db");
        Assert.True(File.Exists(dbPath), "precondition: conversations.db is the live server store file");
        string readyFile = Path.Combine(_root, "child-ready");
        string script =
            "$f=[System.IO.File]::Open('" + dbPath.Replace("'", "''") + "','Open','Read','Read');" +
            "Set-Content -Path '" + readyFile.Replace("'", "''") + "' -Value 1;" +
            "Start-Sleep -Seconds 60";
        using Process child = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        try
        {
            Assert.True(SpinWait.SpinUntil(() => File.Exists(readyFile), TimeSpan.FromSeconds(20)), "child never took the lock");

            IOException error = await Assert.ThrowsAsync<IOException>(
                () => app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None));
            Assert.Contains("failed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(ServerDir, "*" + SqliteDatabaseFileReplacer.StagingSuffix));
        }
        finally
        {
            try { child.Kill(entireProcessTree: true); child.WaitForExit(5000); } catch { }
        }

        // The failed replace left the pre-restore database intact and still writable.
        SqliteConnection.ClearAllPools();
        AssertIntegrityOk(dbPath);
        Assert.Contains("added-after-backup", ConversationTitles(dbPath));
    }

    [Fact]
    public async Task Restore_then_restart_yields_consistent_recoverable_data_and_no_stranded_staging_files()
    {
        List<string> expected;
        byte[] zip;
        using (Instance first = Start())
        {
            for (int i = 0; i < 20; i++) SaveConversation(first.Conversations, "c" + i, "chat-" + i);
            first.Memory.Add(new MemoryAddRequest { Scope = "User", Content = "restart-marker" });
            File.WriteAllText(Path.Combine(ClientDir, "settings.json"), "{\"theme\":\"Dark\"}");
            expected = first.Conversations.List().Select(c => c.Title).OrderBy(t => t).ToList();
            zip = await first.Backups.ExportAsync(CancellationToken.None);

            // Diverge the live state, then restore over it.
            first.Conversations.Delete("c0");
            SaveConversation(first.Conversations, "extra", "post-backup");
            File.WriteAllText(Path.Combine(ClientDir, "settings.json"), "{\"theme\":\"Light\"}");
            BackupImportResult result = await first.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None);
            Assert.True(result.Success);
        }

        SqliteConnection.ClearAllPools();

        // Restart: new instances over the same directories.
        using Instance second = Start();
        List<string> after = second.Conversations.List().Select(c => c.Title).OrderBy(t => t).ToList();
        Assert.Equal(expected, after);
        Assert.Contains(second.Memory.List(), m => m.Content == "restart-marker");
        Assert.Contains("Dark", File.ReadAllText(Path.Combine(ClientDir, "settings.json")));
        // The desktop client's own history file is recreated from the same archive.
        Assert.Equal(expected, ConversationTitles(Path.Combine(ClientDir, "parux.db")));

        SqliteConnection.ClearAllPools();
        foreach (string db in Directory.GetFiles(_root, "*.db", SearchOption.AllDirectories))
            AssertIntegrityOk(db);
        Assert.Empty(Directory.GetFiles(_root, "*" + SqliteDatabaseFileReplacer.StagingSuffix, SearchOption.AllDirectories));

        // The recovered instance is writable and a second backup round-trips.
        SaveConversation(second.Conversations, "post-restart", "post-restart");
        byte[] again = await second.Backups.ExportAsync(CancellationToken.None);
        Assert.Equal("post-restart", second.Conversations.Get("post-restart")!.Title);
        // The archive prefers the desktop client's authoritative history file (restored above),
        // so it holds the restored conversations; the new server-only row syncs later.
        string again2 = ExtractEntry(again, "conversations.db");
        AssertIntegrityOk(again2);
        Assert.Equal(expected, ConversationTitles(again2));
    }

    [Fact]
    public async Task A_mid_commit_failure_names_what_was_already_restored()
    {
        using Instance app = Start();
        SaveConversation(app.Conversations, "a", "in-archive");
        string settingsTarget = Path.Combine(ClientDir, "settings.json");
        File.WriteAllText(settingsTarget, "{\"theme\":\"Dark\"}");
        byte[] withSettings = await app.Backups.ExportAsync(CancellationToken.None);
        // A directory squatting where the settings file must be written makes that step fail for real.
        File.Delete(settingsTarget);
        Directory.CreateDirectory(settingsTarget);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => app.Backups.ImportAsync(new MemoryStream(withSettings), CancellationToken.None));
        Assert.Contains("app settings", error.Message);
        Assert.Contains("Already restored before the failure", error.Message);
        Assert.Contains("conversation history", error.Message);
    }

    private async Task<(Instance app, byte[] zip)> PrepareDivergedAsync()
    {
        Instance app = Start();
        SaveConversation(app.Conversations, "a", "in-archive");
        app.Memory.Add(new MemoryAddRequest { Scope = "User", Content = "mem-archive" });
        byte[] zip = await app.Backups.ExportAsync(CancellationToken.None);
        app.Conversations.Delete("a");
        foreach (MemoryEntryDto m in app.Memory.List()) app.Memory.Delete(m.Id);
        SaveConversation(app.Conversations, "live", "live-chat");
        app.Memory.Add(new MemoryAddRequest { Scope = "User", Content = "mem-live" });
        return (app, zip);
    }

    private static void AssertPreRestoreState(Instance app)
    {
        Assert.Equal(new[] { "live-chat" }, app.Conversations.List().Select(c => c.Title).ToArray());
        Assert.Equal(new[] { "mem-live" }, app.Memory.List().Select(m => m.Content).ToArray());
    }

    [Fact]
    public async Task Failure_at_a_later_step_rolls_every_store_back_to_the_pre_restore_content()
    {
        (Instance app, byte[] zip) = await PrepareDivergedAsync();
        using (app)
        {
            int seen = 0;
            app.Backups.CommitStepObserver = _ => { if (++seen == 3) throw new InvalidOperationException("injected"); };

            IOException error = await Assert.ThrowsAsync<IOException>(
                () => app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None));

            Assert.True(seen >= 3);
            Assert.Contains("Rolled back automatically", error.Message);
            string archive = Directory.GetFiles(Path.Combine(ClientDir, "backups"), "pre-restore-*.zip").Single();
            Assert.Contains(archive, error.Message);
            AssertPreRestoreState(app);
        }
    }

    [Fact]
    public async Task Snapshot_failure_aborts_before_any_store_is_replaced()
    {
        (Instance app, byte[] zip) = await PrepareDivergedAsync();
        using (app)
        {
            File.WriteAllText(Path.Combine(ClientDir, "backups"), "a file where the snapshot directory must be");
            int steps = 0;
            app.Backups.CommitStepObserver = _ => steps++;

            IOException error = await Assert.ThrowsAsync<IOException>(
                () => app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None));

            Assert.Contains("nothing was changed", error.Message);
            Assert.Equal(0, steps);
            AssertPreRestoreState(app);
        }
    }

    [Fact]
    public async Task Double_failure_reports_the_rollback_failure_and_the_archive_path()
    {
        (Instance app, byte[] zip) = await PrepareDivergedAsync();
        using (app)
        {
            int seen = 0;
            app.Backups.CommitStepObserver = _ =>
            {
                if (++seen != 2) return;
                // Corrupt rather than delete: the archive must still exist for manual recovery.
                foreach (string f in Directory.GetFiles(Path.Combine(ClientDir, "backups"), "pre-restore-*.zip"))
                    File.WriteAllText(f, "not a zip");
                throw new InvalidOperationException("injected");
            };

            IOException error = await Assert.ThrowsAsync<IOException>(
                () => app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None));

            Assert.Contains("AUTOMATIC ROLLBACK ALSO FAILED", error.Message);
            string archive = Directory.GetFiles(Path.Combine(ClientDir, "backups"), "pre-restore-*.zip").Single();
            Assert.Contains(archive, error.Message);
        }
    }

    [Fact]
    public async Task Pre_restore_snapshots_are_retained_up_to_the_limit()
    {
        (Instance app, byte[] zip) = await PrepareDivergedAsync();
        using (app)
        {
            for (int i = 0; i < BackupService.PreRestoreSnapshotsToKeep + 2; i++)
            {
                await app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None);
                await Task.Delay(5);
            }
            Assert.Equal(BackupService.PreRestoreSnapshotsToKeep,
                Directory.GetFiles(Path.Combine(ClientDir, "backups"), "pre-restore-*.zip").Length);
        }
    }

    [Fact]
    public void An_entry_declaring_more_uncompressed_bytes_than_the_cap_is_refused_before_extraction()
    {
        string zipPath = Path.Combine(_root, "cap.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("big.db");
            using Stream stream = entry.Open();
            stream.Write(new byte[4096]);
        }
        using ZipArchive read = ZipFile.OpenRead(zipPath);
        string dest = Path.Combine(_root, "out.db");

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => BackupService.ExtractEntryOrThrow(read, "big.db", dest, maxBytes: 1024));
        Assert.Contains("refusing to extract", error.Message);
        Assert.False(File.Exists(dest));

        BackupService.ExtractEntryOrThrow(read, "big.db", dest, maxBytes: 4096); // exactly at the cap is allowed
        Assert.True(File.Exists(dest));
    }

    /// <summary>Deletes a live store's database files from underneath it, making it unexportable.</summary>
    private void BreakStore(string dbName)
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string path = Path.Combine(ServerDir, dbName + suffix);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task A_store_that_cannot_be_snapshotted_makes_restore_refuse_by_default_with_nothing_changed()
    {
        (Instance app, byte[] zip) = await PrepareDivergedAsync();
        using (app)
        {
            BreakStore("model-benchmarks.db");
            int steps = 0;
            app.Backups.CommitStepObserver = _ => steps++;

            var refusal = await Assert.ThrowsAsync<BackupService.IncompleteRollbackSnapshotException>(
                () => app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None));

            Assert.Contains("model-benchmarks.db", refusal.UnsnapshottableStores);
            Assert.Contains("nothing was changed", refusal.Message);
            Assert.Equal(0, steps);
            AssertPreRestoreState(app);
            string backups = Path.Combine(ClientDir, "backups");
            Assert.True(!Directory.Exists(backups) || Directory.GetFiles(backups).Length == 0,
                "a refused restore must not leave a snapshot behind");
        }
    }

    [Fact]
    public async Task Opt_in_restores_over_a_broken_store_and_reports_the_partial_snapshot()
    {
        (Instance app, byte[] zip) = await PrepareDivergedAsync();
        using (app)
        {
            BreakStore("model-benchmarks.db");

            BackupImportResult result = await app.Backups.ImportAsync(
                new MemoryStream(zip), CancellationToken.None, allowIncompleteRollbackSnapshot: true);

            Assert.True(result.Success);
            Assert.Contains("model-benchmarks.db", result.UnsnapshottableStores);
            Assert.True(File.Exists(result.RollbackSnapshotPath));
            Assert.Equal("in-archive", app.Conversations.Get("a")!.Title);
        }
    }

    [Fact]
    public async Task Opt_in_failure_rolls_back_what_was_snapshotted_and_names_what_could_not_be()
    {
        (Instance app, byte[] zip) = await PrepareDivergedAsync();
        using (app)
        {
            BreakStore("model-benchmarks.db");
            int seen = 0;
            app.Backups.CommitStepObserver = _ => { if (++seen == 3) throw new InvalidOperationException("injected"); };

            IOException error = await Assert.ThrowsAsync<IOException>(
                () => app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None, allowIncompleteRollbackSnapshot: true));

            Assert.Contains("PARTIAL snapshot", error.Message);
            Assert.Contains("model-benchmarks.db", error.Message);
            Assert.Contains("CANNOT be rolled back", error.Message);
            AssertPreRestoreState(app);
        }
    }

    [Fact]
    public async Task A_corrupt_store_file_is_refused_by_default_and_its_bytes_are_never_rewritten()
    {
        (Instance app, byte[] zip) = await PrepareDivergedAsync();
        using (app)
        {
            // Stale WAL/SHM sidecars could let SQLite read a valid page 1 from the log, so they go with the damaged file.
            SqliteConnection.ClearAllPools();
            string database = Path.Combine(ServerDir, "model-benchmarks.db");
            foreach (string suffix in new[] { "-wal", "-shm" })
                if (File.Exists(database + suffix)) File.Delete(database + suffix);
            byte[] damaged = "damaged by hand, not an SQLite database"u8.ToArray();
            File.WriteAllBytes(database, damaged);

            var refusal = await Assert.ThrowsAsync<BackupService.IncompleteRollbackSnapshotException>(
                () => app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None));

            Assert.Contains("model-benchmarks.db", refusal.UnsnapshottableStores);
            SqliteConnection.ClearAllPools(); // the refused backup left pooled handles open; release them before reading
            Assert.Equal(damaged, File.ReadAllBytes(database));
            AssertPreRestoreState(app);
        }
    }

    [Fact]
    public async Task Rollback_removes_a_settings_file_that_did_not_exist_before_the_restore()
    {
        using Instance app = Start();
        string settings = Path.Combine(ClientDir, "settings.json");
        File.WriteAllText(settings, "{\"theme\":\"Dark\"}");
        byte[] zip = await app.Backups.ExportAsync(CancellationToken.None);
        File.Delete(settings); // pre-restore state: no settings file

        app.Backups.CommitStepObserver = label =>
        {
            if (label == "app settings") throw new InvalidOperationException("injected after settings were written");
        };

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None));

        Assert.Contains("Rolled back automatically", error.Message);
        Assert.False(File.Exists(settings), "settings.json created by the failed restore must be removed by the rollback");
    }

    [Fact]
    public async Task Concurrent_restores_are_serialized()
    {
        (Instance app, byte[] zip) = await PrepareDivergedAsync();
        using (app)
        {
            using var firstStepReached = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int stepsSeen = 0;
            app.Backups.CommitStepObserver = _ =>
            {
                if (Interlocked.Increment(ref stepsSeen) == 1)
                {
                    firstStepReached.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                }
            };

            Task<BackupImportResult> first = Task.Run(() => app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None));
            Assert.True(firstStepReached.Wait(TimeSpan.FromSeconds(30)));
            Task<BackupImportResult> second = Task.Run(() => app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None));

            await Task.Delay(1500); // plenty of time for an unserialized second restore to start replacing stores
            Assert.Equal(1, Volatile.Read(ref stepsSeen));
            Assert.False(second.IsCompleted);

            release.Set();
            Assert.True((await first).Success);
            Assert.True((await second).Success);
            Assert.True(stepsSeen > 2);
            Assert.Equal("in-archive", app.Conversations.Get("a")!.Title);
        }
    }

    [Fact]
    public async Task Retention_orders_by_write_time_and_never_prunes_the_snapshot_just_created()
    {
        (Instance app, byte[] zip) = await PrepareDivergedAsync();
        using (app)
        {
            string dir = Path.Combine(ClientDir, "backups");
            Directory.CreateDirectory(dir);
            // Name order and write-time order deliberately disagree.
            string oldButNamedNewest = Path.Combine(dir, "pre-restore-99990101T000000000Z.zip");
            string[] recent = new[] { "b", "c", "d" }.Select(n => Path.Combine(dir, "pre-restore-20000101T00000000" + n + "Z.zip")).ToArray();
            foreach (string f in recent.Append(oldButNamedNewest)) File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(oldButNamedNewest, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            for (int i = 0; i < recent.Length; i++)
                File.SetLastWriteTimeUtc(recent[i], DateTime.UtcNow.AddMinutes(-(i + 1)));

            BackupImportResult result = await app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None);

            Assert.True(File.Exists(result.RollbackSnapshotPath), "the just-created snapshot must survive pruning");
            Assert.False(File.Exists(oldButNamedNewest), "oldest by write time is pruned even though its name sorts newest");
            Assert.False(File.Exists(recent[2]));
            Assert.True(File.Exists(recent[0]));
            Assert.True(File.Exists(recent[1]));
            Assert.Equal(BackupService.PreRestoreSnapshotsToKeep, Directory.GetFiles(dir, "pre-restore-*.zip").Length);
        }
    }

    [Fact]
    public async Task Snapshot_directory_is_restricted_to_the_current_user_and_system()
    {
        if (!OperatingSystem.IsWindows()) return;
        (Instance app, byte[] zip) = await PrepareDivergedAsync();
        using (app)
        {
            await app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None);

            var security = new DirectoryInfo(Path.Combine(ClientDir, "backups")).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected, "inheritance must be disabled");
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
            Assert.All(rules, r =>
            {
                Assert.Equal(System.Security.AccessControl.AccessControlType.Allow, r.AccessControlType);
                Assert.True(r.IdentityReference.Equals(me) || r.IdentityReference.Equals(system),
                    "unexpected principal on the safety snapshot folder: " + r.IdentityReference.Value);
            });
            Assert.Contains(rules, r => r.IdentityReference.Equals(me));
            Assert.Contains(rules, r => r.IdentityReference.Equals(system));
        }
    }

    [Fact]
    public async Task Clearing_safety_snapshots_deletes_them()
    {
        (Instance app, byte[] zip) = await PrepareDivergedAsync();
        using (app)
        {
            await app.Backups.ImportAsync(new MemoryStream(zip), CancellationToken.None);
            string dir = Path.Combine(ClientDir, "backups");
            Assert.NotEmpty(Directory.GetFiles(dir, "pre-restore-*.zip"));

            int removed = await app.Backups.ClearPreRestoreSnapshotsAsync(CancellationToken.None);

            Assert.True(removed >= 1);
            Assert.Empty(Directory.GetFiles(dir, "pre-restore-*"));
        }
    }
}
