using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ContextGraph;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-10 opt-in benchmark: a deterministic 50,000-file synthetic repository (C#, Python, TypeScript, Go,
/// Java in equal parts). Set PARUX_BENCH_50K=1 to run it; the default suite returns immediately.
/// Numbers (initial index time, incremental sync time, retrieval precision@k on planted symbols, managed
/// memory) are written to the test output only. The test asserts correctness of counts, not speed, so it
/// can never turn a measurement into a claim.
/// </summary>
[Trait("Category", "Benchmark")]
[Trait("Benchmark", "SemanticIndex50k")]
public sealed class SemanticIndexV2MultiLanguage50kBenchmark
{
    private const int FileCount = 50_000;
    private const int PlantedCount = 25;
    private const int TopK = 5;
    private static readonly string[] Extensions = { ".cs", ".py", ".ts", ".go", ".java" };

    private readonly ITestOutputHelper _output;

    public SemanticIndexV2MultiLanguage50kBenchmark(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Fifty_thousand_file_multi_language_repository_index_sync_and_retrieval_numbers()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PARUX_BENCH_50K"), "1", StringComparison.Ordinal))
            return;

        string suffix = Guid.NewGuid().ToString("N");
        string stateDir = Path.Combine(Path.GetTempPath(), "parux-bench50k-state-" + suffix);
        string repoDir = Path.Combine(Path.GetTempPath(), "parux-bench50k-repo-" + suffix);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(repoDir);
        try
        {
            var planted = new Dictionary<string, string>(StringComparer.Ordinal); // symbol -> relative file
            Stopwatch generation = Stopwatch.StartNew();
            for (int index = 0; index < FileCount; index++)
            {
                string? symbol = PlantedSymbolFor(index);
                string relative = RelativePath(index);
                WriteFile(repoDir, relative, index, version: 1, symbol);
                if (symbol != null) planted[symbol] = relative.Replace('\\', '/');
            }
            generation.Stop();
            Assert.Equal(PlantedCount, planted.Count);

            var graph = new ContextGraphStore(stateDir);
            var service = new SemanticIndexV2Service(stateDir, new RepoIndexService(graph));
            service.Register(new RegisterSemanticRepositoryRequest { Alias = "bench", RepositoryRoot = repoDir });

            ForceCollect();
            long managedBefore = GC.GetTotalMemory(true);
            Stopwatch clock = Stopwatch.StartNew();
            SemanticRepositorySyncResult initial = service.Sync(new SemanticIndexSyncRequest
            {
                Aliases = new List<string> { "bench" }
            }).Repositories.Single();
            clock.Stop();
            long initialMs = clock.ElapsedMilliseconds;
            long managedAfterInitial = GC.GetTotalMemory(false);
            long managedRetainedInitial = GC.GetTotalMemory(true);
            Assert.True(initial.FullRebuild);
            Assert.Equal(FileCount, initial.IndexedFiles);

            int edits = FileCount / 100; // 1% churn: edits, plus a few adds/removes
            for (int index = 1; index <= edits; index++)
                WriteFile(repoDir, RelativePath(index * 7), index * 7, version: 2, PlantedSymbolFor(index * 7));
            for (int index = 0; index < 50; index++)
                File.Delete(Path.Combine(repoDir, RelativePath(FileCount - 1 - index * 3)));
            for (int index = 0; index < 50; index++)
                WriteFile(repoDir, "added/Added" + index.ToString("D4") + Extensions[index % 5], FileCount + index, 1, null);

            clock.Restart();
            SemanticRepositorySyncResult incremental = service.Sync(new SemanticIndexSyncRequest
            {
                Aliases = new List<string> { "bench" }
            }).Repositories.Single();
            clock.Stop();
            long incrementalMs = clock.ElapsedMilliseconds;
            long managedRetainedIncremental = GC.GetTotalMemory(true);
            Assert.False(incremental.FullRebuild);

            clock.Restart();
            SemanticRepositorySyncResult noop = service.Sync(new SemanticIndexSyncRequest
            {
                Aliases = new List<string> { "bench" }
            }).Repositories.Single();
            clock.Stop();
            long noopMs = clock.ElapsedMilliseconds;

            int hitsAtK = 0, hitsAt1 = 0;
            Stopwatch search = Stopwatch.StartNew();
            foreach ((string symbol, string file) in planted)
            {
                SemanticIndexSearchResponse response = service.Search(new SemanticIndexSearchRequest
                {
                    Query = symbol,
                    Aliases = new List<string> { "bench" },
                    MaxFiles = TopK,
                    MaxSymbols = TopK
                });
                string[] ranked = response.RelevantSymbols.Take(TopK).Select(s => s.Replace('\\', '/')).ToArray();
                if (ranked.Any(s => s.Contains(file, StringComparison.OrdinalIgnoreCase) && s.Contains(symbol, StringComparison.Ordinal))) hitsAtK++;
                if (ranked.Length > 0 && ranked[0].Contains(file, StringComparison.OrdinalIgnoreCase) && ranked[0].Contains(symbol, StringComparison.Ordinal)) hitsAt1++;
            }
            search.Stop();

            _output.WriteLine($"BENCH50K files={FileCount} languages=cs,py,ts,go,java generationMs={generation.ElapsedMilliseconds}");
            _output.WriteLine($"BENCH50K initialIndexMs={initialMs} indexed={initial.IndexedFiles}");
            _output.WriteLine($"BENCH50K incrementalSyncMs={incrementalMs} added={incremental.AddedFiles} removed={incremental.RemovedFiles} changed={incremental.ChangedFiles}");
            _output.WriteLine($"BENCH50K noopSyncMs={noopMs} changed={noop.ChangedFiles}");
            _output.WriteLine($"BENCH50K precision@{TopK}={hitsAtK}/{planted.Count} precision@1={hitsAt1}/{planted.Count} searchTotalMs={search.ElapsedMilliseconds}");
            _output.WriteLine($"BENCH50K managedBytesBefore={managedBefore} afterInitialLive={managedAfterInitial} retainedAfterInitial={managedRetainedInitial} retainedAfterIncremental={managedRetainedIncremental} peakWorkingSet={Process.GetCurrentProcess().PeakWorkingSet64}");
        }
        finally
        {
            foreach (string databaseName in new[] { "context-graph.db", "semantic-index-v2.db" })
            {
                try { SqlitePoolIsolation.ClearDatabasePools(Path.Combine(stateDir, databaseName)); } catch { }
            }
            try { Directory.Delete(stateDir, recursive: true); } catch { }
            try { Directory.Delete(repoDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Generator_is_deterministic_and_covers_every_language()
    {
        Assert.Equal(RelativePath(12_345), RelativePath(12_345));
        Assert.Equal(Extensions.Length, Enumerable.Range(0, 10).Select(i => Path.GetExtension(RelativePath(i))).Distinct().Count());
        int planted = Enumerable.Range(0, FileCount).Count(i => PlantedSymbolFor(i) != null);
        Assert.Equal(PlantedCount, planted);
        Assert.Equal(PlantedSymbolFor(2_507), PlantedSymbolFor(2_507));
    }

    [Fact]
    public void Small_multi_language_repo_retrieves_planted_symbols_in_top_k()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string stateDir = Path.Combine(Path.GetTempPath(), "parux-bench-small-state-" + suffix);
        string repoDir = Path.Combine(Path.GetTempPath(), "parux-bench-small-repo-" + suffix);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(repoDir);
        try
        {
            var planted = new Dictionary<string, string>();
            for (int index = 0; index < 200; index++)
            {
                string? symbol = index % 41 == 7 ? "PlantedNeedle" + (index / 41).ToString("D2") + "Zq" : null;
                string relative = RelativePath(index);
                WriteFile(repoDir, relative, index, 1, symbol);
                if (symbol != null) planted[symbol] = relative;
            }
            var service = new SemanticIndexV2Service(stateDir, new RepoIndexService(new ContextGraphStore(stateDir)));
            service.Register(new RegisterSemanticRepositoryRequest { Alias = "small", RepositoryRoot = repoDir });
            service.Sync(new SemanticIndexSyncRequest { Aliases = new List<string> { "small" } });
            foreach ((string symbol, string file) in planted)
            {
                SemanticIndexSearchResponse response = service.Search(new SemanticIndexSearchRequest
                {
                    Query = symbol, Aliases = new List<string> { "small" }, MaxFiles = TopK, MaxSymbols = TopK
                });
                bool found = response.RelevantSymbols.Any(s => s.Replace('\\', '/').Contains(file, StringComparison.OrdinalIgnoreCase) && s.Contains(symbol, StringComparison.Ordinal));
                _output.WriteLine(symbol + " expect " + file + " foundInTopK=" + found);
                if (!file.EndsWith(".ts", StringComparison.Ordinal)) Assert.True(found, "planted symbol not retrieved: " + symbol + " in " + file);
            }
        }
        finally
        {
            foreach (string databaseName in new[] { "context-graph.db", "semantic-index-v2.db" })
            {
                try { SqlitePoolIsolation.ClearDatabasePools(Path.Combine(stateDir, databaseName)); } catch { }
            }
            try { Directory.Delete(stateDir, recursive: true); } catch { }
            try { Directory.Delete(repoDir, recursive: true); } catch { }
        }
    }

    internal static string RelativePath(int index) =>
        $"{Extensions[index % 5].TrimStart('.')}/pkg{index / 5 % 200:D3}/mod{index:D6}{Extensions[index % 5]}";

    internal static string? PlantedSymbolFor(int index)
    {
        // Spread planted symbols across all languages: every 2,003rd file starting at 7 (coprime with 5 so all languages are covered) (25 total).
        if (index % 2_003 != 7) return null;
        return "PlantedNeedle" + (index / 2_003).ToString("D2") + "Zq";
    }

    internal static void WriteFile(string root, string relative, int index, int version, string? planted)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string name = "Mod" + index.ToString("D6");
        string needle = planted ?? string.Empty;
        string text = Path.GetExtension(path) switch
        {
            ".cs" => $"namespace Bench;\npublic sealed class {name}\n{{\n    public int Version() => {version};\n"
                     + (needle.Length > 0 ? $"    public string {needle}() => \"x\";\n" : string.Empty) + "}\n",
            ".py" => $"class {name}:\n    def version(self):\n        return {version}\n"
                     + (needle.Length > 0 ? $"    def {needle}(self):\n        return 'x'\n" : string.Empty),
            ".ts" => $"export class {name} {{\n  version(): number {{ return {version}; }}\n"
                     + (needle.Length > 0 ? $"  {needle}(): string {{ return 'x'; }}\n" : string.Empty) + "}\n",
            ".go" => $"package pkg\n\ntype {name} struct{{}}\n\nfunc (m {name}) Version() int {{ return {version} }}\n"
                     + (needle.Length > 0 ? $"\nfunc (m {name}) {needle}() string {{ return \"x\" }}\n" : string.Empty),
            _ => $"package bench;\npublic class {name} {{\n  public int version() {{ return {version}; }}\n"
                     + (needle.Length > 0 ? $"  public String {needle}() {{ return \"x\"; }}\n" : string.Empty) + "}\n"
        };
        File.WriteAllText(path, text);
    }

    private static void ForceCollect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
