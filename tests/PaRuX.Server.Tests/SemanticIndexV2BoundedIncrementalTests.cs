using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ContextGraph;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-10 hermetic bound check on a generated 5,000-file repository (temp dir, no git, no network):
/// the cost of a normal sync must track the number of changed files, not the repository size, and
/// retrieval must follow edit / rename / delete. Measured numbers are written to the test output.
/// Thresholds are loose structural bounds (changed-file proportional work), not performance claims.
/// </summary>
public sealed class SemanticIndexV2BoundedIncrementalTests
{
    private const int FileCount = 5_000;
    private readonly ITestOutputHelper _output;

    public SemanticIndexV2BoundedIncrementalTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Normal_changes_on_5k_file_repo_do_bounded_work_and_keep_retrieval_correct()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string stateDir = Path.Combine(Path.GetTempPath(), "parux-bic10-state-" + suffix);
        string repoDir = Path.Combine(Path.GetTempPath(), "parux-bic10-repo-" + suffix);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(repoDir);
        try
        {
            for (int i = 0; i < FileCount; i++) Write(repoDir, i, $"Plain{i:D5}");

            var graph = new ContextGraphStore(stateDir);
            var index = new RepoIndexService(graph);
            var service = new SemanticIndexV2Service(stateDir, index);
            service.Register(new RegisterSemanticRepositoryRequest { Alias = "b5k", RepositoryRoot = repoDir });
            var req = new SemanticIndexSyncRequest { Aliases = new List<string> { "b5k" } };

            var sw = Stopwatch.StartNew();
            long a0 = GC.GetTotalAllocatedBytes(false);
            SemanticRepositorySyncResult initial = service.Sync(req).Repositories.Single();
            sw.Stop();
            long initialMs = sw.ElapsedMilliseconds;
            long initialAlloc = GC.GetTotalAllocatedBytes(false) - a0;
            Assert.True(initial.FullRebuild);
            Assert.Equal(FileCount, initial.IndexedFiles);

            // No-op sync: no projection, no git-history probe.
            int probesBefore = index.GitHistoryProbeCountForTest;
            sw.Restart();
            SemanticRepositorySyncResult noop = service.Sync(req).Repositories.Single();
            long noopMs = sw.ElapsedMilliseconds;
            Assert.False(noop.FullRebuild);
            Assert.Equal(0, noop.AddedFiles + noop.ChangedFiles + noop.RemovedFiles);
            Assert.Equal(probesBefore, index.GitHistoryProbeCountForTest);

            // Edit one, rename one, delete one, add one (4 changed files of 5,000).
            Write(repoDir, 10, "EditedSymbolZeta");
            string newPath = Path.Combine(repoDir, "src", "renamed", "MovedFile.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.Move(SourcePath(repoDir, 20), newPath);
            File.Delete(SourcePath(repoDir, 30));
            Write(repoDir, FileCount, "AddedSymbolOmega");

            probesBefore = index.GitHistoryProbeCountForTest;
            sw.Restart();
            long b0 = GC.GetTotalAllocatedBytes(false);
            SemanticRepositorySyncResult inc = service.Sync(req).Repositories.Single();
            sw.Stop();
            long incMs = sw.ElapsedMilliseconds;
            long incAlloc = GC.GetTotalAllocatedBytes(false) - b0;
            int probes = index.GitHistoryProbeCountForTest - probesBefore;
            int fileRecords = index.IndexedFileRecordsLoadedForTest;
            int topoRows = index.TopologyInputRowsLoadedForTest;

            _output.WriteLine($"BIC10 files={FileCount} initialMs={initialMs} initialAllocMB={initialAlloc / 1048576} "
                + $"noopMs={noopMs} incMs={incMs} incAllocMB={incAlloc / 1048576} "
                + $"gitProbes={probes} fileRecordsLoaded={fileRecords} topologyRowsLoaded={topoRows} "
                + $"incToInitialTime={(double)incMs / Math.Max(1, initialMs):F3}");

            Assert.False(inc.FullRebuild);
            Assert.Equal(1, inc.ChangedFiles);
            Assert.Equal(2, inc.AddedFiles);
            Assert.Equal(2, inc.RemovedFiles);
            Assert.Equal(FileCount, inc.IndexedFiles);

            // Work is proportional to the changed files (plus direct neighbours), not to 5,000.
            Assert.True(probes <= 1, "git history must be probed at most once per changed sync, was " + probes);
            Assert.InRange(fileRecords, 0, 600);
            Assert.InRange(topoRows, 0, 600);
            Assert.True(incMs * 4 < initialMs, $"incremental {incMs}ms vs initial {initialMs}ms");
            Assert.True(incAlloc * 4 < initialAlloc, $"incremental alloc {incAlloc} vs initial {initialAlloc}");

            // Retrieval after edit / rename / delete / add.
            Assert.Contains(Symbols(service, "EditedSymbolZeta"), s => s.Contains("EditedSymbolZeta", StringComparison.Ordinal));
            Assert.DoesNotContain(Symbols(service, "Plain00010"), s => s.Contains("Plain00010", StringComparison.Ordinal));
            Assert.Contains(Symbols(service, "AddedSymbolOmega"), s => s.Contains("AddedSymbolOmega", StringComparison.Ordinal));
            Assert.DoesNotContain(Symbols(service, "Plain00030"), s => s.Contains("Plain00030", StringComparison.Ordinal));
            SemanticIndexSearchResponse moved = service.Search(new SemanticIndexSearchRequest
            { Query = "Plain00020", Aliases = new List<string> { "b5k" }, MaxFiles = 20, MaxSymbols = 20 });
            Assert.Contains(moved.RelevantSymbols, s => s.Contains("Plain00020", StringComparison.Ordinal)
                && s.Contains("renamed/MovedFile.cs", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(moved.RelevantSymbols, s => s.Contains("ScaleBucketType00020", StringComparison.Ordinal));
            Assert.Contains(service.Search(new SemanticIndexSearchRequest
            { Query = "MovedFile", Aliases = new List<string> { "b5k" }, MaxFiles = 5, MaxSymbols = 5 }).RelevantFiles,
                f => f.Contains("renamed/MovedFile.cs", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (string db in new[] { "context-graph.db", "semantic-index-v2.db" })
                try { SqlitePoolIsolation.ClearDatabasePools(Path.Combine(stateDir, db)); } catch { }
            try { Directory.Delete(stateDir, true); } catch { }
            try { Directory.Delete(repoDir, true); } catch { }
        }
    }

    [Fact]
    public void Ambiguous_common_method_name_in_every_file_does_not_load_the_repository()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string stateDir = Path.Combine(Path.GetTempPath(), "parux-bic10a-state-" + suffix);
        string repoDir = Path.Combine(Path.GetTempPath(), "parux-bic10a-repo-" + suffix);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(repoDir);
        try
        {
            for (int i = 0; i < FileCount; i++) WriteCommon(repoDir, i, $"Common{i:D5}");
            var graph = new ContextGraphStore(stateDir);
            var index = new RepoIndexService(graph);
            var service = new SemanticIndexV2Service(stateDir, index);
            service.Register(new RegisterSemanticRepositoryRequest { Alias = "b5k", RepositoryRoot = repoDir });
            var req = new SemanticIndexSyncRequest { Aliases = new List<string> { "b5k" } };

            var sw = Stopwatch.StartNew();
            service.Sync(req);
            long initialMs = sw.ElapsedMilliseconds;

            WriteCommon(repoDir, 10, "EditedCommonZeta");
            WriteCommon(repoDir, 11, "EditedCommonEta");
            File.Delete(SourcePath(repoDir, 30));
            WriteCommon(repoDir, FileCount, "AddedCommonOmega");

            sw.Restart();
            SemanticRepositorySyncResult inc = service.Sync(req).Repositories.Single();
            long incMs = sw.ElapsedMilliseconds;
            int fileRecords = index.IndexedFileRecordsLoadedForTest;
            int topoRows = index.TopologyInputRowsLoadedForTest;
            _output.WriteLine($"BIC10-ambiguous files={FileCount} initialMs={initialMs} incMs={incMs} "
                + $"fileRecordsLoaded={fileRecords} topologyRowsLoaded={topoRows}");

            Assert.False(inc.FullRebuild);
            Assert.InRange(fileRecords, 0, FileCount / 20 - 1);
            Assert.InRange(topoRows, 0, FileCount / 20 - 1);
            Assert.True(incMs * 4 < initialMs, $"incremental {incMs}ms vs initial {initialMs}ms");
            Assert.Contains(Symbols(service, "EditedCommonZeta"), s => s.Contains("EditedCommonZeta", StringComparison.Ordinal));
            Assert.Contains(Symbols(service, "AddedCommonOmega"), s => s.Contains("AddedCommonOmega", StringComparison.Ordinal));
            Assert.DoesNotContain(Symbols(service, "Common00030"), s => s.Contains("Common00030", StringComparison.Ordinal));
        }
        finally
        {
            foreach (string db in new[] { "context-graph.db", "semantic-index-v2.db" })
                try { SqlitePoolIsolation.ClearDatabasePools(Path.Combine(stateDir, db)); } catch { }
            try { Directory.Delete(stateDir, true); } catch { }
            try { Directory.Delete(repoDir, true); } catch { }
        }
    }

    private static void WriteCommon(string root, int i, string symbol)
    {
        string path = SourcePath(root, i);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"namespace Bic10.B{i % 64:D2};\npublic sealed class {symbol}\n{{\n    public void Execute() {{ Execute(); }}\n}}\n");
    }

    private static List<string> Symbols(SemanticIndexV2Service service, string query) =>
        service.Search(new SemanticIndexSearchRequest
        { Query = query, Aliases = new List<string> { "b5k" }, MaxFiles = 20, MaxSymbols = 20 }).RelevantSymbols;

    private static string SourcePath(string root, int i) =>
        Path.Combine(root, "src", $"Bucket{i % 64:D2}", $"ScaleBucketType{i:D5}.cs");

    private static void Write(string root, int i, string symbol)
    {
        string path = SourcePath(root, i);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"namespace Bic10.B{i % 64:D2};\npublic sealed class {symbol}\n{{\n    public int Value{i:D5}() => {i};\n}}\n");
    }
}
