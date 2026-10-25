using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Opt-in acceptance harness for BIC-10. This deliberately does not run the 1k/10k/50k repository
/// matrix during every unit-test invocation: set PARUX_RUN_SEMANTIC_SCALE=1 on a validation machine.
/// The harness proves that a real incremental churn pass converges to the same file/search state as
/// an explicit full rebuild, including a service restart between the two observations, and records
/// timings as JSON when PARUX_SEMANTIC_SCALE_EVIDENCE is set.
/// </summary>
public sealed class SemanticIndexV2ScaleAcceptanceTests
{
    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public void Incremental_churn_matches_full_rebuild_after_restart_at_scale(int fileCount)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PARUX_RUN_SEMANTIC_SCALE"), "1", StringComparison.Ordinal))
            return;

        string suffix = fileCount + "-" + Guid.NewGuid().ToString("N");
        string stateDir = Path.Combine(Path.GetTempPath(), "parux-semantic-scale-state-" + suffix);
        string repoDir = Path.Combine(Path.GetTempPath(), "parux-semantic-scale-repo-" + suffix);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(repoDir);

        try
        {
            for (int index = 0; index < fileCount; index++)
                WriteSource(repoDir, index, version: 1, hot: index % 997 == 0);

            ContextGraphStore graph = new(stateDir);
            RepoIndexService indexService = new(graph);
            SemanticIndexV2Service service = new(stateDir, indexService);
            service.Register(new RegisterSemanticRepositoryRequest { Alias = "scale", RepositoryRoot = repoDir });

            Stopwatch clock = Stopwatch.StartNew();
            PhaseMemory initialMemory = PhaseMemory.Begin();
            SemanticRepositorySyncResult initial = service.Sync(new SemanticIndexSyncRequest
            {
                Aliases = new List<string> { "scale" },
                VerifyAllHashes = true
            }).Repositories.Single();
            clock.Stop();
            long initialMs = clock.ElapsedMilliseconds;
            PhaseMemoryResult initialMem = initialMemory.End();

            Assert.True(initial.FullRebuild);
            Assert.Equal(fileCount, initial.IndexedFiles);

            int churn = Math.Max(10, fileCount / 100);
            int modified = churn;
            int removed = churn;
            int renamed = churn;
            int added = churn;

            for (int index = 0; index < modified; index++)
                WriteSource(repoDir, index, version: 2, hot: index % 997 == 0);

            for (int index = modified; index < modified + removed; index++)
                File.Delete(SourcePath(repoDir, index));

            for (int index = modified + removed; index < modified + removed + renamed; index++)
            {
                string source = SourcePath(repoDir, index);
                string destination = Path.Combine(repoDir, "src", "renamed", $"Renamed{index:D6}.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination);
            }

            for (int offset = 0; offset < added; offset++)
            {
                int index = fileCount + offset;
                WriteSource(repoDir, index, version: 1, hot: false);
            }

            clock.Restart();
            PhaseMemory incrementalMemory = PhaseMemory.Begin();
            SemanticRepositorySyncResult incremental = service.Sync(new SemanticIndexSyncRequest
            {
                Aliases = new List<string> { "scale" },
                VerifyAllHashes = true
            }).Repositories.Single();
            clock.Stop();
            long incrementalMs = clock.ElapsedMilliseconds;
            PhaseMemoryResult incrementalMem = incrementalMemory.End();

            Assert.False(incremental.FullRebuild);
            Assert.Equal(fileCount, incremental.IndexedFiles); // removed and added balance; rename is add+remove.
            Assert.Equal(added + renamed, incremental.AddedFiles);
            Assert.Equal(removed + renamed, incremental.RemovedFiles);
            Assert.Equal(modified, incremental.ChangedFiles);

            string[] incrementalFiles = graph.ListEntities("file")
                .Select(entity => entity.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SemanticIndexSearchResponse incrementalSearch = service.Search(new SemanticIndexSearchRequest
            {
                Query = "ScaleMarkerHot",
                Aliases = new List<string> { "scale" },
                MaxFiles = 100,
                MaxSymbols = 100
            });
            Assert.Contains(incrementalSearch.RelevantSymbols, symbol => symbol.Contains("ScaleMarkerHot", StringComparison.Ordinal));

            // Re-open the durable coordinator before the reference full rebuild. A restart must not
            // manufacture a rebuild or forget the manifest written by the incremental pass.
            SemanticIndexV2Service reopened = new(stateDir, indexService);
            SemanticRepositorySyncResult noOpAfterRestart = reopened.Sync(new SemanticIndexSyncRequest
            {
                Aliases = new List<string> { "scale" },
                VerifyAllHashes = true
            }).Repositories.Single();
            Assert.False(noOpAfterRestart.FullRebuild);
            Assert.Equal(0, noOpAfterRestart.AddedFiles);
            Assert.Equal(0, noOpAfterRestart.RemovedFiles);
            Assert.Equal(0, noOpAfterRestart.ChangedFiles);

            clock.Restart();
            PhaseMemory fullMemory = PhaseMemory.Begin();
            SemanticRepositorySyncResult forced = reopened.Sync(new SemanticIndexSyncRequest
            {
                Aliases = new List<string> { "scale" },
                VerifyAllHashes = true,
                ForceFull = true
            }).Repositories.Single();
            clock.Stop();
            long fullRebuildMs = clock.ElapsedMilliseconds;
            PhaseMemoryResult fullMem = fullMemory.End();

            Assert.True(forced.FullRebuild);
            Assert.Equal(fileCount, forced.IndexedFiles);

            string[] rebuiltFiles = graph.ListEntities("file")
                .Select(entity => entity.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.Equal(incrementalFiles, rebuiltFiles);

            SemanticIndexSearchResponse rebuiltSearch = reopened.Search(new SemanticIndexSearchRequest
            {
                Query = "ScaleMarkerHot",
                Aliases = new List<string> { "scale" },
                MaxFiles = 100,
                MaxSymbols = 100
            });
            Assert.Equal(
                incrementalSearch.RelevantFiles.OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
                rebuiltSearch.RelevantFiles.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            Assert.Equal(
                incrementalSearch.RelevantSymbols.OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
                rebuiltSearch.RelevantSymbols.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

            AppendEvidence(new
            {
                schemaVersion = 2,
                memoryMethod = "50ms sampled process working set; managed allocation delta; native peaks between samples may be missed",
                initialMemory = initialMem,
                incrementalMemory = incrementalMem,
                fullRebuildMemory = fullMem,
                fileCount,
                churnFilesPerClass = churn,
                initialMs,
                incrementalMs,
                fullRebuildMs,
                incrementalToFullRatio = fullRebuildMs == 0 ? (double?)null : (double)incrementalMs / fullRebuildMs,
                indexedFiles = forced.IndexedFiles,
                utc = DateTime.UtcNow
            });
        }
        finally
        {
            ClearStatePools(stateDir);
            DeleteBestEffort(stateDir);
            DeleteBestEffort(repoDir);
        }
    }

    private sealed record PhaseMemoryResult(
        long WorkingSetBeforeBytes, long SampledPeakWorkingSetBytes, long WorkingSetAfterBytes, long AllocatedManagedBytes);

    private sealed class PhaseMemory
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly long _before;
        private readonly long _allocatedBefore;
        private long _peak;
        private System.Threading.Timer? _timer;

        private PhaseMemory()
        {
            _process.Refresh();
            _before = _process.WorkingSet64;
            _peak = _before;
            _allocatedBefore = GC.GetTotalAllocatedBytes(false);
        }

        public static PhaseMemory Begin()
        {
            var phase = new PhaseMemory();
            phase._timer = new System.Threading.Timer(_ => phase.Sample(), null, 0, 50);
            return phase;
        }

        private void Sample()
        {
            try
            {
                _process.Refresh();
                long value = _process.WorkingSet64;
                long seen;
                do { seen = Interlocked.Read(ref _peak); if (value <= seen) return; }
                while (Interlocked.CompareExchange(ref _peak, value, seen) != seen);
            }
            catch { /* best-effort sampler */ }
        }

        public PhaseMemoryResult End()
        {
            _timer?.Dispose();
            Sample();
            _process.Refresh();
            return new PhaseMemoryResult(_before, Interlocked.Read(ref _peak), _process.WorkingSet64,
                GC.GetTotalAllocatedBytes(false) - _allocatedBefore);
        }
    }

    private static string SourcePath(string root, int index) =>
        Path.Combine(root, "src", $"Bucket{index % 128:D3}", $"ScaleType{index:D6}.cs");

    private static void WriteSource(string root, int index, int version, bool hot)
    {
        string path = SourcePath(root, index);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            $"namespace Scale.Bucket{index % 128:D3};\n"
            + $"public sealed class ScaleType{index:D6}\n{{\n    public int Version() => {version};\n"
            + (hot ? "    public string ScaleMarkerHot() => \"hot\";\n" : string.Empty)
            + "}\n");
    }

    private static void AppendEvidence(object sample)
    {
        string? path = Environment.GetEnvironmentVariable("PARUX_SEMANTIC_SCALE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(path)) return;
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.AppendAllText(full, JsonSerializer.Serialize(sample) + Environment.NewLine);
    }

    private static void ClearStatePools(string stateDir)
    {
        foreach (string databaseName in new[] { "context-graph.db", "semantic-index-v2.db" })
        {
            string databasePath = Path.Combine(stateDir, databaseName);
            try { SqlitePoolIsolation.ClearDatabasePools(databasePath); }
            catch { /* best-effort test cleanup; the paths are unique to this run */ }
        }
    }

    private static void DeleteBestEffort(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
