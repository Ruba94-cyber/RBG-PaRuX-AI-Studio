using Microsoft.Data.Sqlite;
using PaRuX.Server.ContextGraph;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

public sealed class RepoIndexRetrievalFloorTests
{
    private readonly ITestOutputHelper _output;

    public RepoIndexRetrievalFloorTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Fixed_multilanguage_corpus_meets_retrieval_floors()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "parux-repo-index-floor", Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(testRoot, "repo");
        string graphRoot = Path.Combine(testRoot, "graph");

        try
        {
            IReadOnlyList<CorpusFile> corpus = BuildCorpus();
            Assert.Equal(48, corpus.Count);
            Assert.Equal(12, corpus.Count(file => Path.GetExtension(file.RelativePath) == ".cs"));
            Assert.Equal(12, corpus.Count(file => Path.GetExtension(file.RelativePath) == ".py"));
            Assert.Equal(12, corpus.Count(file => Path.GetExtension(file.RelativePath) == ".cpp"));
            Assert.Equal(12, corpus.Count(file => Path.GetExtension(file.RelativePath) == ".js"));

            foreach (CorpusFile file in corpus)
            {
                string fullPath = Path.Combine(repositoryRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, file.Content);
            }

            var index = new RepoIndexService(new ContextGraphStore(graphRoot));
            var indexed = index.IndexRepository(repositoryRoot);
            Assert.Equal(corpus.Count, indexed.FilesIndexed);

            QueryCase[] queries = BuildQueries();

            QueryResult[] results = queries.Select(query =>
            {
                List<string> topTen = index.SearchContext(repositoryRoot, query.Text, maxFiles: 10).RelevantFiles;
                int position = topTen.FindIndex(path => string.Equals(path, query.DefiningFile, StringComparison.OrdinalIgnoreCase));
                return new QueryResult(query, position < 0 ? 0 : position + 1);
            }).ToArray();

            double recallAtTen = results.Count(result => result.Rank is > 0 and <= 10) / (double)queries.Length;
            double meanReciprocalRank = results.Average(result => result.Rank == 0 ? 0.0 : 1.0 / result.Rank);
            _output.WriteLine($"Measured recall@10={recallAtTen:F3}; MRR={meanReciprocalRank:F3}; queries={queries.Length}; indexedFiles={indexed.FilesIndexed}.");

            Assert.True(recallAtTen >= RecallAtTenFloor,
                $"Recall@10 {recallAtTen:F3} is below floor {RecallAtTenFloor:F2}. Ranks: {FormatRanks(results)}");
            Assert.True(meanReciprocalRank >= MeanReciprocalRankFloor,
                $"MRR {meanReciprocalRank:F3} is below floor {MeanReciprocalRankFloor:F2}. Ranks: {FormatRanks(results)}");
            Assert.All(results, result => Assert.True(result.Rank is > 0 and <= 10,
                $"The defining file '{result.Query.DefiningFile}' was not in the top ten for {result.Query.Kind} query '{result.Query.Text}'."));
        }
        finally
        {
            ClearGraphConnectionPool(graphRoot);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void ClearGraphConnectionPool(string graphRoot)
    {
        using var poolKey = new SqliteConnection(
            $"Data Source={Path.Combine(graphRoot, "context-graph.db")};Mode=ReadWriteCreate;Cache=Shared");
        SqliteConnection.ClearPool(poolKey);
    }

    private static IReadOnlyList<CorpusFile> BuildCorpus()
    {
        var files = new List<CorpusFile>
        {
            CSharp("src/context/AdaptiveContextWindowPlanner.cs"),
            CSharp("src/verticalSlice/EntryPoint.cs"),
            CSharp("src/recovery/StaleSemanticIndexRestartRecovery.cs"),
            CSharp("src/context/ContextWindowBudget.cs"),
            CSharp("src/context/WindowSizeEstimator.cs"),
            CSharp("src/context/AdaptiveBatchPlanner.cs"),
            CSharp("src/context/TokenBudgetService.cs"),
            CSharp("src/cache/SemanticIndexCache.cs"),
            CSharp("src/recovery/StaleCacheReader.cs"),
            CSharp("src/recovery/RestartJournal.cs"),
            CSharp("src/verticalSlice/VerticalSliceTestHelper.cs"),
            CSharp("src/shared/EntryPointFixture.cs"),

            Python("tools/ingest/semantic_cache_rebuild.py"),
            Python("tools/batchImport/entry_point.py"),
            Python("tools/diagnostics/EmbeddingCacheEvictionMemoryPressure.py"),
            Python("tools/search/semantic_cache_reader.py"),
            Python("tools/search/token_window_query.py"),
            Python("tools/embeddings/cache_eviction_policy.py"),
            Python("tools/embeddings/rebuild_queue.py"),
            Python("tools/batchImport/BatchImportTest.py"),
            Python("tools/diagnostics/memory_pressure_report.py"),
            Python("tools/ingest/rebuild_manifest.py"),
            Python("tools/shared/entry_point_helper.py"),
            Python("tools/search/identifier_splitter.py"),

            Cpp("engine/token/TokenWindowScanner.cpp"),
            Cpp("engine/nativeBridge/Entry.cpp"),
            Cpp("engine/search/BlockingIncrementalTokenWindowReader.cpp"),
            Cpp("engine/token/TokenWindowReader.cpp"),
            Cpp("engine/nativeBridge/BridgeFixture.cpp"),
            Cpp("engine/search/IncrementalWindowScanner.cpp"),
            Cpp("engine/search/BlockingReader.cpp"),
            Cpp("engine/window/IncrementalTokenBuffer.cpp"),
            Cpp("engine/bridge/NativeBridgeRegistry.cpp"),
            Cpp("engine/token/ScannerFixture.cpp"),
            Cpp("engine/shared/EntryPointFixture.cpp"),
            Cpp("engine/window/WindowCursor.cpp"),

            QmlLike("ui/overlays/FocusRingOverlay.js"),
            QmlLike("ui/renderPanel/MainView.js"),
            QmlLike("ui/editor/KeyboardFocusRingForActiveEditor.js"),
            QmlLike("ui/panels/RenderPanelControls.js"),
            QmlLike("ui/renderPanel/RenderSettings.js"),
            QmlLike("ui/overlay/FocusRingHelpers.js"),
            QmlLike("ui/editor/ActiveEditorOutline.js"),
            QmlLike("ui/panels/PanelNavigation.js"),
            QmlLike("ui/widgets/FocusIndicator.js"),
            QmlLike("ui/keyboard/ActiveSelection.js"),
            QmlLike("ui/shared/EntryPointView.js"),
            QmlLike("ui/layout/PanelStack.js")
        };

        // These fixtures contain QML-style UI declarations in .js paths because .js is indexable here.
        return files;
    }

    // Measured on the targeted run: recall@10=1.000 and MRR=0.917 across 12 queries.
    private const double RecallAtTenFloor = 0.90;
    private const double MeanReciprocalRankFloor = 0.85;

    private static QueryCase[] BuildQueries() =>
    [
        new("identifier-only", "AdaptiveContextWindowPlanner", "src/context/AdaptiveContextWindowPlanner.cs"),
        new("identifier-only", "semantic_cache_rebuild", "tools/ingest/semantic_cache_rebuild.py"),
        new("identifier-only", "TokenWindowScanner", "engine/token/TokenWindowScanner.cpp"),
        new("identifier-only", "FocusRingOverlay", "ui/overlays/FocusRingOverlay.js"),
        new("path-only", "verticalSlice", "src/verticalSlice/EntryPoint.cs"),
        new("path-only", "batchImport", "tools/batchImport/entry_point.py"),
        new("path-only", "nativeBridge", "engine/nativeBridge/Entry.cpp"),
        new("path-only", "renderPanel", "ui/renderPanel/MainView.js"),
        new("natural-language", "recover a stale semantic index after restart", "src/recovery/StaleSemanticIndexRestartRecovery.cs"),
        new("natural-language", "explain memory pressure from cache eviction during embedding rebuild", "tools/diagnostics/EmbeddingCacheEvictionMemoryPressure.py"),
        new("natural-language", "why does the blocking incremental token window reader stall", "engine/search/BlockingIncrementalTokenWindowReader.cpp"),
        new("natural-language", "show the keyboard focus ring for the active editor", "ui/editor/KeyboardFocusRingForActiveEditor.js")
    ];

    private static CorpusFile CSharp(string path) => new(path,
        $"namespace Synthetic; public sealed class {Identifier(path)} {{ public void Execute() {{ }} }}");

    private static CorpusFile Python(string path) => new(path,
        $"class {Identifier(path)}:\n    def execute(self):\n        return None\n");

    private static CorpusFile Cpp(string path) => new(path,
        $"struct {Identifier(path)} {{ void execute() {{}} }};\n");

    private static CorpusFile QmlLike(string path) => new(path,
        $"import QtQuick\nItem {{ id: {Identifier(path)}; property int width: 640; property int height: 480 }}\n");

    private static string Identifier(string path) =>
        new(Path.GetFileNameWithoutExtension(path).Where(char.IsLetterOrDigit).ToArray());

    private static string FormatRanks(IEnumerable<QueryResult> results) =>
        string.Join(", ", results.Select(result => $"{result.Query.Kind}:{result.Rank}"));

    private sealed record CorpusFile(string RelativePath, string Content);
    private sealed record QueryCase(string Kind, string Text, string DefiningFile);
    private sealed record QueryResult(QueryCase Query, int Rank);
}
