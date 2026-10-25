using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Canonical, id-free view of the file/symbol/relation graph used to compare incremental and full state.</summary>
internal static class SemanticGraphFingerprint
{
    public static string[] Capture(ContextGraphStore graph)
    {
        ContextGraphExportDto export = graph.ExportSnapshot();
        var label = new Dictionary<string, string>();
        foreach (ContextEntityDto entity in export.Entities)
        {
            if (entity.EntityType is not ("file" or "symbol")) continue;
            string detail = string.Empty;
            if (entity.MetadataJson != null)
            {
                using JsonDocument doc = JsonDocument.Parse(entity.MetadataJson);
                detail = (doc.RootElement.TryGetProperty("relativePath", out JsonElement p) ? p.GetString() : "")
                    + "#" + (doc.RootElement.TryGetProperty("signature", out JsonElement s) ? s.GetString() : "")
                    + "#" + (doc.RootElement.TryGetProperty("line", out JsonElement l) ? l.ToString() : "");
            }
            label[entity.Id] = entity.EntityType + ":" + entity.Name + ":" + detail;
        }
        IEnumerable<string> entities = label.Values.Select(v => "E|" + v);
        IEnumerable<string> relations = export.Relations
            .Where(r => label.ContainsKey(r.FromEntityId) && label.ContainsKey(r.ToEntityId))
            .Select(r => "R|" + label[r.FromEntityId] + "|" + r.RelationType + "|" + label[r.ToEntityId]);
        return entities.Concat(relations).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }
}

public sealed class SemanticIndexV2IncrementalParityTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "parux-bic10-state-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "parux-bic10-repo-" + Guid.NewGuid().ToString("N"));
    private readonly ContextGraphStore _graph;
    private readonly RepoIndexService _index;
    private readonly SemanticIndexV2Service _service;

    public SemanticIndexV2IncrementalParityTests()
    {
        Directory.CreateDirectory(_state);
        Directory.CreateDirectory(_repo);
        _graph = new ContextGraphStore(_state);
        _index = new RepoIndexService(_graph);
        _service = new SemanticIndexV2Service(_state, _index);
        _service.Register(new RegisterSemanticRepositoryRequest { Alias = "r", RepositoryRoot = _repo });
    }

    public void Dispose()
    {
        foreach (string db in new[] { "context-graph.db", "semantic-index-v2.db" })
            try { SqlitePoolIsolation.ClearDatabasePools(Path.Combine(_state, db)); } catch { }
        foreach (string dir in new[] { _state, _repo })
            try { Directory.Delete(dir, true); } catch { }
    }

    private SemanticRepositorySyncResult Sync(bool full = false, CancellationToken ct = default) =>
        _service.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "r" }, VerifyAllHashes = true, ForceFull = full
        }, ct).Repositories.Single();

    private void Write(string relative, string content)
    {
        string full = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void AssertIncrementalEqualsFull()
    {
        string[] incremental = SemanticGraphFingerprint.Capture(_graph);
        SemanticIndexV2Service reopened = new(_state, _index);
        SemanticRepositorySyncResult full = reopened.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "r" }, VerifyAllHashes = true, ForceFull = true
        }).Repositories.Single();
        Assert.True(full.FullRebuild);
        Assert.Equal(SemanticGraphFingerprint.Capture(_graph), incremental);
    }

    private const string CallerSource = "public sealed class Caller\n{\n    public void Go() { Helper(); }\n}\n";
    private static string Definer(string cls) => "public sealed class " + cls + "\n{\n    public void Helper() {}\n}\n";

    [Fact]
    public void Adding_a_duplicate_symbol_removes_the_stale_unique_call_edge()
    {
        Write("src/Caller.cs", CallerSource);
        Write("src/DefOne.cs", Definer("DefOne"));
        Sync();
        Assert.Contains(SemanticGraphFingerprint.Capture(_graph), x => x.StartsWith("R|") && x.Contains("|CALLS|"));

        Write("src/DefTwo.cs", Definer("DefTwo"));
        Assert.False(Sync().FullRebuild);
        Assert.DoesNotContain(SemanticGraphFingerprint.Capture(_graph), x => x.Contains("|CALLS|"));
        AssertIncrementalEqualsFull();
    }

    [Fact]
    public void Removing_a_duplicate_symbol_creates_the_call_edge_for_an_untouched_caller()
    {
        Write("src/Caller.cs", CallerSource);
        Write("src/DefOne.cs", Definer("DefOne"));
        Write("src/DefTwo.cs", Definer("DefTwo"));
        Sync();
        Assert.DoesNotContain(SemanticGraphFingerprint.Capture(_graph), x => x.Contains("|CALLS|"));

        File.Delete(Path.Combine(_repo, "src", "DefTwo.cs"));
        Assert.False(Sync().FullRebuild);
        Assert.Contains(SemanticGraphFingerprint.Capture(_graph), x => x.Contains("|CALLS|") && x.Contains("DefOne"));
        AssertIncrementalEqualsFull();
    }

    [Fact]
    public void Import_resolution_changes_when_a_higher_priority_target_is_added_or_removed()
    {
        Write("web/app.js", "import { x } from './util';\nexport const a = 1;\n");
        Write("web/util/index.js", "export const x = 1;\n");
        Sync();
        Write("web/util.js", "export const x = 2;\n");
        Sync();
        AssertIncrementalEqualsFull();
        File.Delete(Path.Combine(_repo, "web", "util.js"));
        Sync();
        AssertIncrementalEqualsFull();
    }

    [Fact]
    public void Add_remove_rename_edit_across_languages_converges_to_full_rebuild_and_survives_restart()
    {
        Write("py/pkg/__init__.py", "");
        Write("py/pkg/core.py", "from . import helpers\n\ndef run_core():\n    return helpers.assist()\n");
        Write("py/pkg/helpers.py", "def assist():\n    return 1\n");
        Write("cpp/engine.h", "class Engine { public: void spin(); };\n");
        Write("cpp/engine.cpp", "#include \"engine.h\"\nvoid Engine::spin() {}\n");
        Write("rs/src/lib.rs", "pub mod util;\npub fn entry() {}\n");
        Write("rs/src/util.rs", "pub fn helper_fn() {}\n");
        Write("ui/Main.qml", "import QtQuick 2.15\nItem { id: root }\n");
        Write("ui/app.pro", "QT += quick\nSOURCES += main.cpp\n");
        Write("ui/res.qrc", "<RCC><qresource prefix=\"/\"><file>Main.qml</file></qresource></RCC>\n");
        Write("CMakeLists.txt", "project(demo)\nadd_library(engine cpp/engine.cpp)\n");
        Sync();

        Write("py/pkg/helpers.py", "def assist():\n    return 2\n\ndef extra():\n    return 3\n");
        File.Move(Path.Combine(_repo, "rs", "src", "util.rs"), Path.Combine(_repo, "rs", "src", "tools.rs"));
        Write("rs/src/lib.rs", "pub mod tools;\npub fn entry() {}\n");
        File.Delete(Path.Combine(_repo, "cpp", "engine.cpp"));
        Write("cpp/renderer.cpp", "#include \"engine.h\"\nvoid render_frame() {}\n");
        Write("ui/Second.qml", "import QtQuick 2.15\nRectangle { id: second }\n");
        SemanticRepositorySyncResult result = Sync();
        Assert.False(result.FullRebuild);
        Assert.True(result.AddedFiles >= 3 && result.RemovedFiles >= 2 && result.ChangedFiles >= 2);

        // Restart before the reference rebuild: the durable manifest must make a no-op sync a no-op.
        SemanticIndexV2Service reopened = new(_state, _index);
        SemanticRepositorySyncResult noop = reopened.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "r" }, VerifyAllHashes = true
        }).Repositories.Single();
        Assert.Equal(0, noop.AddedFiles + noop.ChangedFiles + noop.RemovedFiles);
        AssertIncrementalEqualsFull();
    }

    [Theory]
    [InlineData("ui/app.pro")]
    [InlineData("ui/shared.pri")]
    [InlineData("ui/res.qrc")]
    [InlineData("setup.cfg")]
    [InlineData("pom.xml")]
    [InlineData("requirements.txt")]
    [InlineData("go.mod")]
    [InlineData("meson.build")]
    public void Additional_project_descriptors_are_indexed_as_file_evidence(string relative)
    {
        Write(relative, "# descriptor\n");
        Sync();
        Assert.Contains(_graph.ListEntities("file"), e => string.Equals(e.Name, relative, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generated_lockfiles_and_oversized_files_are_not_indexed()
    {
        Write("web/package-lock.json", "{}");
        Write("web/package.json", "{}");
        string big = Path.Combine(_repo, "data", "huge.json");
        Directory.CreateDirectory(Path.GetDirectoryName(big)!);
        File.WriteAllBytes(big, new byte[(int)SemanticIndexV2Service.MaximumIndexedFileBytes + 1]);
        Sync();
        string[] files = _graph.ListEntities("file").Select(e => e.Name).ToArray();
        Assert.Contains("web/package.json", files);
        Assert.DoesNotContain("web/package-lock.json", files);
        Assert.DoesNotContain("data/huge.json", files);
    }

    [Fact]
    public void Cancelled_incremental_sync_keeps_manifest_and_a_later_sync_converges()
    {
        for (int i = 0; i < 6; i++) Write($"src/F{i}.cs", $"public sealed class F{i}\n{{\n    public void M{i}() {{}}\n}}\n");
        Sync();
        for (int i = 0; i < 6; i++) Write($"src/F{i}.cs", $"public sealed class F{i}\n{{\n    public void N{i}() {{}}\n}}\n");

        using var cts = new CancellationTokenSource();
        int seen = 0;
        _index.AfterFileProjectedForTest = _ => { if (++seen == 2) cts.Cancel(); };
        Assert.ThrowsAny<OperationCanceledException>(() => Sync(ct: cts.Token));
        _index.AfterFileProjectedForTest = null;

        SemanticRepositorySyncResult resumed = Sync();
        Assert.False(resumed.FullRebuild);
        Assert.Equal(6, resumed.ChangedFiles);
        AssertIncrementalEqualsFull();
        Assert.Contains(_graph.ListEntities("symbol"), s => s.Name == "N5");
        Assert.DoesNotContain(_graph.ListEntities("symbol"), s => s.Name.StartsWith("M", StringComparison.Ordinal));
    }

    [Fact]
    public void Interrupted_sync_followed_by_reverting_the_file_does_not_leave_a_stale_projection()
    {
        Write("src/Flip.cs", "public sealed class Flip\n{\n    public void Original() {}\n}\n");
        Write("src/Other.cs", "public sealed class Other { public void Stay() {} }");
        Sync();

        Write("src/Flip.cs", "public sealed class Flip\n{\n    public void Edited() {}\n}\n");
        _index.AfterFileProjectedForTest = _ => throw new IOException("simulated crash after projection commit");
        Assert.Throws<IOException>(() => Sync());
        _index.AfterFileProjectedForTest = null;
        Assert.Contains(_graph.ListEntities("symbol"), s => s.Name == "Edited");

        // The user reverts to the exact content the manifest still records; hash equality alone would hide
        // the half-applied edit. The pending journal forces the projection to be rebuilt.
        Write("src/Flip.cs", "public sealed class Flip\n{\n    public void Original() {}\n}\n");
        new SemanticIndexV2Service(_state, _index).Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "r" }, VerifyAllHashes = true
        });
        Assert.DoesNotContain(_graph.ListEntities("symbol"), s => s.Name == "Edited");
        Assert.Contains(_graph.ListEntities("symbol"), s => s.Name == "Original");
        AssertIncrementalEqualsFull();
    }

    [Fact]
    public void Interrupted_removal_does_not_resurrect_after_the_file_is_recreated_identically()
    {
        Write("src/Keep.cs", "public sealed class Keep { public void K() {} }");
        Write("src/Gone.cs", "public sealed class Gone { public void G() {} }");
        Sync();
        File.Delete(Path.Combine(_repo, "src", "Gone.cs"));
        using var cts = new CancellationTokenSource();
        _index.AfterFileProjectedForTest = _ => cts.Cancel();
        Write("src/Keep.cs", "public sealed class Keep { public void K2() {} }");
        Assert.ThrowsAny<OperationCanceledException>(() => Sync(ct: cts.Token));
        _index.AfterFileProjectedForTest = null;
        Sync();
        AssertIncrementalEqualsFull();
        Assert.DoesNotContain(_graph.ListEntities("file"), e => e.Name == "src/Gone.cs");
    }

    [Fact]
    public void Cancelled_first_sync_is_retried_as_full_rebuild()
    {
        for (int i = 0; i < 4; i++) Write($"src/G{i}.cs", $"public sealed class G{i} {{ }}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Sync(ct: cts.Token));
        SemanticRepositorySyncResult retry = Sync();
        Assert.True(retry.FullRebuild);
        Assert.Equal(4, retry.IndexedFiles);
    }

    [Fact]
    public void Unchanged_files_are_not_re_read_when_topology_is_recomputed()
    {
        Write("src/Caller.cs", CallerSource);
        Write("src/DefOne.cs", Definer("DefOne"));
        Sync();
        Write("src/DefOne.cs", Definer("DefOne") + "// edit\n");

        // Caller.cs is unchanged; an exclusive lock makes any re-read fail. Its CALLS edge must survive
        // because topology comes from the stored per-file inputs, not from re-reading the file.
        using (new FileStream(Path.Combine(_repo, "src", "Caller.cs"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            SemanticRepositorySyncResult result = _service.Sync(new SemanticIndexSyncRequest
            {
                Aliases = new List<string> { "r" }
            }).Repositories.Single();
            Assert.False(result.FullRebuild);
            Assert.Equal(1, result.ChangedFiles);
            Assert.Contains(SemanticGraphFingerprint.Capture(_graph), x => x.Contains("|CALLS|"));
        }
        AssertIncrementalEqualsFull();
    }

    [Fact]
    public void Missing_stored_topology_inputs_are_backfilled_from_disk_without_changing_the_graph()
    {
        Write("src/Caller.cs", CallerSource);
        Write("src/DefOne.cs", Definer("DefOne"));
        Sync();
        string[] before = SemanticGraphFingerprint.Capture(_graph);

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(_state, "context-graph.db")))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM repo_topology_inputs;";
            Assert.True(command.ExecuteNonQuery() >= 2);
        }

        Write("src/Other.cs", Definer("Other2").Replace("Helper", "Unrelated"));
        Sync();
        Assert.Contains(SemanticGraphFingerprint.Capture(_graph), x => x.Contains("|CALLS|"));
        AssertIncrementalEqualsFull();
        Assert.True(before.Length > 0);
    }

    [Fact]
    public void Large_change_sets_fall_back_to_full_rebuild_and_small_ones_stay_incremental()
    {
        int count = SemanticIndexV2Service.FullRebuildMinimumFiles + 20;
        for (int i = 0; i < count; i++) Write($"src/N{i:D3}.cs", $"public sealed class N{i:D3}\n{{\n    public void V{i:D3}() {{}}\n}}\n");
        Sync();

        // 10% edited: stays incremental.
        for (int i = 0; i < count / 10; i++) Write($"src/N{i:D3}.cs", $"public sealed class N{i:D3}\n{{\n    public void W{i:D3}() {{}}\n}}\n");
        Assert.False(Sync().FullRebuild);

        // 50% edited (above the 40% threshold): executed as a full rebuild, state still correct.
        for (int i = 0; i < count / 2; i++) Write($"src/N{i:D3}.cs", $"public sealed class N{i:D3}\n{{\n    public void X{i:D3}() {{}}\n}}\n");
        SemanticRepositorySyncResult big = Sync();
        Assert.True(big.FullRebuild);
        Assert.Equal(count, big.IndexedFiles);
        Assert.Contains(_graph.ListEntities("symbol"), e => e.Name == "X000");
        Assert.DoesNotContain(_graph.ListEntities("symbol"), e => e.Name == "W000");

        // The threshold itself is configurable and the same edit stays incremental when raised.
        _service.FullRebuildChangeRatio = 2.0;
        for (int i = 0; i < count / 2; i++) Write($"src/N{i:D3}.cs", $"public sealed class N{i:D3}\n{{\n    public void Y{i:D3}() {{}}\n}}\n");
        Assert.False(Sync().FullRebuild);
        AssertIncrementalEqualsFull();
    }

    [Fact]
    public void Reindex_enforces_the_file_size_cap_even_when_called_directly()
    {
        Write("src/Small.cs", Definer("Small"));
        string big = Path.Combine(_repo, "src", "Big.cs");
        File.WriteAllText(big, "public sealed class Big" + Environment.NewLine + "{" + Environment.NewLine
            + "    public void Huge() {}" + Environment.NewLine + "}" + Environment.NewLine
            + new string('/', (int)RepoIndexService.MaximumIndexedFileBytes));
        _index.ReindexFiles(_repo, new[] { "src/Small.cs", "src/Big.cs" });
        string[] files = _graph.ListEntities("file").Select(e => e.Name).ToArray();
        Assert.Contains("src/Small.cs", files);
        Assert.DoesNotContain("src/Big.cs", files);
        Assert.DoesNotContain(_graph.ListEntities("symbol"), e => e.Name == "Huge");
    }
}
