using PaRuX.Contracts;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RepoIndexScopedLoadTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "parux-scoped-state-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "parux-scoped-repo-" + Guid.NewGuid().ToString("N"));
    private readonly ContextGraphStore _graph;
    private readonly RepoIndexService _index;

    public RepoIndexScopedLoadTests()
    {
        Directory.CreateDirectory(_repo);
        _graph = new ContextGraphStore(_state);
        _index = new RepoIndexService(_graph);
    }

    public void Dispose()
    {
        try { SqlitePoolIsolation.ClearDatabasePools(Path.Combine(_state, "context-graph.db")); } catch { }
        foreach (string directory in new[] { _state, _repo })
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
    }

    [Fact]
    public void Incremental_sync_loads_only_the_changed_file_and_its_direct_topology_neighbor()
    {
        for (int i = 0; i < 1_996; i++)
            Write($"src/Unrelated{i:D4}.cs", $"public sealed class Unrelated{i:D4} {{ public void Stable{i:D4}() {{ }} }}\n");
        Write("src/Target0000.cs", "public sealed class Target0000\n{\n    public void Anchor() { }\n}\n");
        Write("src/Caller.cs", "public sealed class Caller\n{\n    public void Run() { Anchor(); }\n}\n");
        Write("src/Other.ts", "export const otherValue = 1;\n");
        Write("outside/Importer.ts", "import '../src/Other';\nexport function stableImport() { return otherValue; }\n");
        Write("src/Target0000Extra.ts", "export const extraValue = 1;\n");
        Write("outside/PrefixImporter.ts", "import '../src/Target0000Extra';\nexport function stablePrefixImport() { return extraValue; }\n");
        _index.IndexRepository(_repo);

        var history = Enumerable.Range(0, 1_996)
            .ToDictionary(i => $"src/Unrelated{i:D4}.cs", _ => (0, 0L), StringComparer.OrdinalIgnoreCase);
        history["src/Target0000.cs"] = (1, 1_700_000_000L);
        history["src/Caller.cs"] = (0, 0L);
        history["src/Other.ts"] = (0, 0L);
        history["outside/Importer.ts"] = (0, 0L);
        history["src/Target0000Extra.ts"] = (0, 0L);
        history["outside/PrefixImporter.ts"] = (0, 0L);
        _index.GitHistoryEvidenceProviderForTest = _ => history;

        Write("src/Target0000.cs", "public sealed class Target0000\n{\n    public void Anchor() { int value = 1; }\n}\n");
        _index.ReindexFile(_repo, "src/Target0000.cs");

        // The counter deduplicates old and replacement rows by relative path. Only the changed file
        // and its one direct caller enter the incremental working set.
        // No count depends on elapsed time.
        Assert.Equal(2, _index.IndexedFileRecordsLoadedForTest);
        Assert.Equal(2, _index.TopologyInputRowsLoadedForTest);
        Assert.Contains(SemanticGraphFingerprint.Capture(_graph), item => item.Contains("|CALLS|"));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("change")]
    [InlineData("delete")]
    [InlineData("rename")]
    public void Scoped_incremental_topology_and_search_match_a_full_rebuild(string operation)
    {
        Write("src/helper.ts", "export function helperValue() { return 1; }\n");
        Write("src/main.ts", "import { helperValue } from './helper';\nexport function runMain() { return helperValue(); }\n");
        Write("src/steady.ts", "export function steadyValue() { return 7; }\n");
        _index.IndexRepository(_repo);

        switch (operation)
        {
            case "add":
                Write("src/helper.test.ts", "import { helperValue } from './helper';\nexport function checkHelper() { return helperValue(); }\n");
                _index.ReindexFiles(_repo, new[] { "src/helper.test.ts" });
                break;
            case "change":
                Write("src/helper.ts", "export function helperChanged() { return 2; }\n");
                _index.ReindexFiles(_repo, new[] { "src/helper.ts" });
                break;
            case "delete":
                File.Delete(Path.Combine(_repo, "src", "helper.ts"));
                _index.ReindexFiles(_repo, Array.Empty<string>(), new[] { "src/helper.ts" });
                break;
            case "rename":
                File.Move(Path.Combine(_repo, "src", "helper.ts"), Path.Combine(_repo, "src", "renamed.ts"));
                _index.ReindexFiles(_repo, new[] { "src/renamed.ts" }, new[] { "src/helper.ts" });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        AssertIncrementalMatchesFullRebuild();
    }

    [Fact]
    public void Adding_a_duplicate_symbol_updates_the_untouched_call_source()
    {
        Write("src/Caller.ts", "export function runCaller() { return computeValue(); }\n");
        Write("src/First.ts", "export function computeValue() { return 1; }\n");
        _index.IndexRepository(_repo);
        Assert.Contains(SemanticGraphFingerprint.Capture(_graph), item => item.Contains("|CALLS|"));

        Write("src/Second.ts", "export function computeValue() { return 2; }\n");
        _index.ReindexFiles(_repo, new[] { "src/Second.ts" });

        Assert.DoesNotContain(SemanticGraphFingerprint.Capture(_graph), item => item.Contains("|CALLS|"));
        AssertIncrementalMatchesFullRebuild();
    }

    [Fact]
    public void Removing_a_duplicate_symbol_restores_the_untouched_call_edge()
    {
        Write("src/Caller.ts", "export function runCaller() { return computeValue(); }\n");
        Write("src/First.ts", "export function computeValue() { return 1; }\n");
        Write("src/Second.ts", "export function computeValue() { return 2; }\n");
        _index.IndexRepository(_repo);
        Assert.DoesNotContain(SemanticGraphFingerprint.Capture(_graph), item => item.Contains("|CALLS|"));

        File.Delete(Path.Combine(_repo, "src", "Second.ts"));
        _index.ReindexFiles(_repo, Array.Empty<string>(), new[] { "src/Second.ts" });

        Assert.Contains(SemanticGraphFingerprint.Capture(_graph), item => item.Contains("|CALLS|"));
        AssertIncrementalMatchesFullRebuild();
    }

    [Fact]
    public void Adding_a_preferred_import_target_updates_the_untouched_importer()
    {
        Write("web/app.js", "import { item } from './util';\nexport function runApp() { return item; }\n");
        Write("web/util/index.js", "export const item = 1;\n");
        _index.IndexRepository(_repo);

        Write("web/util.js", "export const item = 2;\n");
        _index.ReindexFiles(_repo, new[] { "web/util.js" });

        ContextEntityDto importer = _graph.ListEntities("file").Single(entity => entity.Name == "web/app.js");
        ContextEntityDto preferred = _graph.ListEntities("file").Single(entity => entity.Name == "web/util.js");
        Assert.Contains(_graph.GetNeighborhood(importer.Id)!.OutgoingRelations,
            relation => relation.RelationType == "DEPENDS_ON" && relation.ToEntityId == preferred.Id);
        AssertIncrementalMatchesFullRebuild();
    }

    [Fact]
    public void Adding_a_directory_index_target_updates_its_untouched_importer()
    {
        Write("web/app.js", "import { item } from './util';\nexport function runApp() { return item; }\n");
        _index.IndexRepository(_repo);

        Write("web/util/index.js", "export const item = 1;\n");
        _index.ReindexFiles(_repo, new[] { "web/util/index.js" });

        ContextEntityDto importer = _graph.ListEntities("file").Single(entity => entity.Name == "web/app.js");
        ContextEntityDto target = _graph.ListEntities("file").Single(entity => entity.Name == "web/util/index.js");
        Assert.Contains(_graph.GetNeighborhood(importer.Id)!.OutgoingRelations,
            relation => relation.RelationType == "DEPENDS_ON" && relation.ToEntityId == target.Id);
        AssertIncrementalMatchesFullRebuild();
    }

    [Fact]
    public void Adding_a_rust_module_directory_updates_its_untouched_importer()
    {
        Write("src/lib.rs", "mod utilities;\npub fn run() { utilities::value(); }\n");
        _index.IndexRepository(_repo);

        Write("src/utilities/mod.rs", "pub fn value() { }\n");
        _index.ReindexFiles(_repo, new[] { "src/utilities/mod.rs" });

        ContextEntityDto importer = _graph.ListEntities("file").Single(entity => entity.Name == "src/lib.rs");
        ContextEntityDto target = _graph.ListEntities("file").Single(entity => entity.Name == "src/utilities/mod.rs");
        Assert.Contains(_graph.GetNeighborhood(importer.Id)!.OutgoingRelations,
            relation => relation.RelationType == "DEPENDS_ON" && relation.ToEntityId == target.Id);
        AssertIncrementalMatchesFullRebuild();
    }

    [Fact]
    public void Adding_an_importer_of_an_untouched_module_creates_its_dependency_edge()
    {
        Write("web/util.js", "export const item = 1;\n");
        Write("web/other.js", "export const other = 2;\n");
        _index.IndexRepository(_repo);

        Write("web/app.js", "import { item } from './util';\nexport function runApp() { return item; }\n");
        _index.ReindexFiles(_repo, new[] { "web/app.js" });

        ContextEntityDto importer = _graph.ListEntities("file").Single(entity => entity.Name == "web/app.js");
        ContextEntityDto target = _graph.ListEntities("file").Single(entity => entity.Name == "web/util.js");
        Assert.Contains(_graph.GetNeighborhood(importer.Id)!.OutgoingRelations,
            relation => relation.RelationType == "DEPENDS_ON" && relation.ToEntityId == target.Id);
        AssertIncrementalMatchesFullRebuild();
    }

    [Fact]
    public void Changing_a_file_to_import_an_untouched_module_creates_its_dependency_edge()
    {
        Write("web/util.js", "export const item = 1;\n");
        Write("web/app.js", "export function runApp() { return 1; }\n");
        _index.IndexRepository(_repo);

        Write("web/app.js", "import { item } from './util';\nexport function runApp() { return item; }\n");
        _index.ReindexFiles(_repo, new[] { "web/app.js" });

        ContextEntityDto importer = _graph.ListEntities("file").Single(entity => entity.Name == "web/app.js");
        ContextEntityDto target = _graph.ListEntities("file").Single(entity => entity.Name == "web/util.js");
        Assert.Contains(_graph.GetNeighborhood(importer.Id)!.OutgoingRelations,
            relation => relation.RelationType == "DEPENDS_ON" && relation.ToEntityId == target.Id);
        AssertIncrementalMatchesFullRebuild();
    }

    [Fact]
    public void Adding_a_single_letter_module_updates_its_untouched_importer()
    {
        Write("web/app.js", "import { item } from './x';\nexport function runApp() { return item; }\n");
        _index.IndexRepository(_repo);

        Write("web/x.js", "export const item = 1;\n");
        _index.ReindexFiles(_repo, new[] { "web/x.js" });

        ContextEntityDto importer = _graph.ListEntities("file").Single(entity => entity.Name == "web/app.js");
        ContextEntityDto target = _graph.ListEntities("file").Single(entity => entity.Name == "web/x.js");
        Assert.Contains(_graph.GetNeighborhood(importer.Id)!.OutgoingRelations,
            relation => relation.RelationType == "DEPENDS_ON" && relation.ToEntityId == target.Id);
        AssertIncrementalMatchesFullRebuild();
    }

    private void AssertIncrementalMatchesFullRebuild()
    {
        string[] incrementalGraph = SemanticGraphFingerprint.Capture(_graph);
        RepoContextPack incrementalSearch = _index.SearchContext(_repo, "helper value main", maxFiles: 8, maxSymbols: 15);
        _index.RemoveRepositoryIndex(_repo);
        _index.IndexRepository(_repo);
        Assert.Equal(SemanticGraphFingerprint.Capture(_graph), incrementalGraph);
        RepoContextPack rebuiltSearch = _index.SearchContext(_repo, "helper value main", maxFiles: 8, maxSymbols: 15);
        Assert.Equal(rebuiltSearch.RelevantFiles, incrementalSearch.RelevantFiles);
        Assert.Equal(rebuiltSearch.RelevantSymbols, incrementalSearch.RelevantSymbols);
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
