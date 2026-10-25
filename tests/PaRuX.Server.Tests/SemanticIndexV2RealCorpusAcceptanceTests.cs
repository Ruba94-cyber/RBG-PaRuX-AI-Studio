using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Opt-in BIC-10 acceptance over real, pinned, permissively licensed multi-language repositories.
/// Set PARUX_BIC10_CORPUS_ROOT to a directory holding clones named like the corpus entries (each must
/// contain both pinned commits); PARUX_BIC10_EVIDENCE receives result JSON. Each repository is cloned
/// into a disposable temp directory, indexed at the base commit, moved to the head commit (a real
/// add/delete/rename/edit history), synced incrementally, and compared with a forced full rebuild at
/// the same commit: graph state (entities and relations) and ranked retrieval must be identical, and
/// retrieval quality against a fixed expected-file query set is reported for both.
/// </summary>
public sealed class SemanticIndexV2RealCorpusAcceptanceTests
{
    private sealed record Query(string Text, string[] Expected);
    private sealed record Repo(string Name, string Url, string License, string BaseSha, string HeadSha, Query[] Queries);

    private static readonly Repo[] Corpus =
    {
        new("ripgrep", "https://github.com/BurntSushi/ripgrep", "MIT OR Unlicense (Rust)",
            "9802945e6342ec284633924cb7d8d3ce67204995", "3fce3b5bb0236da2df6d99672afb8a719642eca7", new Query[]
            {
                new("globset glob matcher", new[] { "crates/globset/src/glob.rs" }),
                new("gitignore matching", new[] { "crates/ignore/src/gitignore.rs" }),
                new("decompress reader", new[] { "crates/cli/src/decompress.rs" }),
                new("hostname", new[] { "crates/cli/src/hostname.rs" }),
                new("index literal", new[] { "crates/index/src/literal.rs" }),
                new("incremental ignore", new[] { "crates/ignore/src/incremental.rs" }),
                new("powershell completion", new[] { "crates/core/flags/complete/powershell.rs" }),
                new("walk", new[] { "crates/ignore/src/walk.rs" }),
            }),
        new("fmt", "https://github.com/fmtlib/fmt", "MIT (C++/CMake)",
            "840a5b4190d157c26a30f44dd5a06612f3c66090", "9197f51593dab5f637454a8889fb9638e737711a", new Query[]
            {
                new("chrono formatting", new[] { "include/fmt/chrono.h" }),
                new("enum formatting", new[] { "include/fmt/enum.h", "test/enum-test.cc" }),
                new("core test", new[] { "test/core-test.cc" }),
                new("color terminal", new[] { "include/fmt/color.h" }),
                new("ostream", new[] { "include/fmt/ostream.h", "test/ostream-test.cc" }),
                new("printf", new[] { "include/fmt/printf.h" }),
                new("fuzzing named arg", new[] { "test/fuzzing/named-arg.cc" }),
                new("os file", new[] { "include/fmt/os.h", "src/os.cc" }),
            }),
        new("requests", "https://github.com/psf/requests", "Apache-2.0 (Python)",
            "f12ccbef6d6b95564da8d22e280d28c39d53f0e9", "611c6162cbc4ac2020a2f91c7cfa4f3abf9bbb60", new Query[]
            {
                new("session cookies", new[] { "src/requests/sessions.py", "src/requests/cookies.py" }),
                new("http adapter", new[] { "src/requests/adapters.py" }),
                new("status codes", new[] { "src/requests/status_codes.py" }),
                new("authentication", new[] { "src/requests/auth.py" }),
                new("exceptions", new[] { "src/requests/exceptions.py" }),
                new("types", new[] { "src/requests/_types.py" }),
                new("test server", new[] { "tests/testserver/server.py", "tests/test_testserver.py" }),
                new("structures", new[] { "src/requests/structures.py" }),
            }),
        new("TaoQuick", "https://github.com/jaredtao/TaoQuick", "MIT (QML/C++/qmake)",
            "1a578ba7a9d6b567e66cac805c70e705b1ed934c", "d1e8f51756bacd23eaaf96dcb54465cd61deaf92", new Query[]
            {
                new("thread pool", new[] { "3rdparty/TaoCommon/src/TaoCommon/Thread/ThreadPool.cpp", "3rdparty/TaoCommon/src/TaoCommon/Thread/ThreadPool.h" }),
                new("logger", new[] { "3rdparty/TaoCommon/src/TaoCommon/Logger/Logger.h", "3rdparty/TaoCommon/src/TaoCommon/Logger/Logger.cpp" }),
                new("quick list model", new[] { "3rdparty/TaoCommon/src/TaoCommon/QuickModel/QuickListModel.h", "3rdparty/TaoCommon/src/TaoCommon/QuickModel/QuickListModel.cpp" }),
                new("frameless view", new[] { "3rdparty/TaoCommon/src/TaoCommon/Frameless/TaoFrameLessView.h", "3rdparty/TaoCommon/src/TaoCommon/Frameless/TaoFrameLessView.cpp" }),
                new("quick tree model", new[] { "3rdparty/TaoCommon/src/TaoCommon/QuickTree/Model/QuickTreeModel.h", "3rdparty/TaoCommon/src/TaoCommon/QuickTree/Model/QuickTreeModel.cpp" }),
                new("ring page effect", new[] { "examples/TaoQuickShow/Contents/Effect/RingPage.qml", "examples/TaoQuickShow/Contents/Effect/Effects/TRingPage.qml" }),
                new("dissolve animation", new[] { "examples/TaoQuickShow/Contents/Animation/Dissolve.qml" }),
                new("taocommon qmake project", new[] { "3rdparty/TaoCommon/src/TaoCommon/TaoCommon.pro", "3rdparty/TaoCommon/TaoCommon.pro" }),
            }),
    };

    private const int TopK = 10;

    [Fact]
    public void Real_repository_history_incremental_equals_full_rebuild_with_measured_retrieval_quality()
    {
        string? corpusRoot = Environment.GetEnvironmentVariable("PARUX_BIC10_CORPUS_ROOT");
        if (string.IsNullOrWhiteSpace(corpusRoot)) return;
        string? evidenceDir = Environment.GetEnvironmentVariable("PARUX_BIC10_EVIDENCE");
        var results = new List<object>();

        foreach (Repo repo in Corpus)
        {
            string source = Path.Combine(corpusRoot, repo.Name);
            Assert.True(Directory.Exists(source), "Missing corpus clone: " + source);
            string work = Path.Combine(Path.GetTempPath(), "parux-bic10-real-" + repo.Name + "-" + Guid.NewGuid().ToString("N"));
            string state = work + "-state";
            try
            {
                Git(Path.GetTempPath(), "clone", "--quiet", "--no-checkout", "--no-hardlinks", source, work);
                Git(work, "checkout", "--quiet", "--force", repo.BaseSha);

                var graph = new ContextGraphStore(state);
                var index = new RepoIndexService(graph);
                var service = new SemanticIndexV2Service(state, index);
                // Real histories churn 30-70% of these small repositories; disable the cost-based full-rebuild
                // fallback so this acceptance exercises the incremental path itself.
                service.FullRebuildChangeRatio = 2.0;
                service.Register(new RegisterSemanticRepositoryRequest { Alias = "c", RepositoryRoot = work });

                Stopwatch clock = Stopwatch.StartNew();
                SemanticRepositorySyncResult initial = Sync(service, false);
                long initialMs = clock.ElapsedMilliseconds;
                Assert.True(initial.FullRebuild);

                Git(work, "checkout", "--quiet", "--force", repo.HeadSha);
                clock.Restart();
                SemanticRepositorySyncResult incremental = Sync(service, false);
                long incrementalMs = clock.ElapsedMilliseconds;
                Assert.False(incremental.FullRebuild);
                Assert.True(incremental.AddedFiles + incremental.RemovedFiles + incremental.ChangedFiles > 0);

                string[] incrementalState = SemanticGraphFingerprint.Capture(graph);
                List<string[]> incrementalRanked = repo.Queries.Select(q => Rank(service, q.Text)).ToList();

                var reopened = new SemanticIndexV2Service(state, index);
                SemanticRepositorySyncResult noop = Sync(reopened, false);
                Assert.Equal(0, noop.AddedFiles + noop.ChangedFiles + noop.RemovedFiles);

                clock.Restart();
                SemanticRepositorySyncResult full = Sync(reopened, true);
                long fullMs = clock.ElapsedMilliseconds;
                Assert.True(full.FullRebuild);

                string[] fullState = SemanticGraphFingerprint.Capture(graph);
                List<string[]> fullRanked = repo.Queries.Select(q => Rank(reopened, q.Text)).ToList();
                Assert.Equal(fullState, incrementalState);
                for (int i = 0; i < repo.Queries.Length; i++)
                    Assert.Equal(fullRanked[i], incrementalRanked[i]);

                string[] indexedPaths = graph.ListEntities("file").Select(e => e.Name).ToArray();
                foreach (Query q in repo.Queries)
                    foreach (string expected in q.Expected)
                        Assert.Contains(expected, indexedPaths, StringComparer.OrdinalIgnoreCase);

                var perQuery = repo.Queries.Select((q, i) => Score(q, incrementalRanked[i])).ToList();
                results.Add(new
                {
                    repo = repo.Name, repo.Url, repo.License, repo.BaseSha, repo.HeadSha,
                    indexedFiles = full.IndexedFiles,
                    churn = new { incremental.AddedFiles, incremental.ChangedFiles, incremental.RemovedFiles },
                    initialMs, incrementalMs, fullRebuildMs = fullMs,
                    graphItems = fullState.Length,
                    graphIdentical = true,
                    rankingIdenticalForAllQueries = true,
                    meanPrecisionAtK = perQuery.Average(x => x.Precision),
                    meanRecallAtK = perQuery.Average(x => x.Recall),
                    meanReciprocalRank = perQuery.Average(x => x.Rr),
                    topK = TopK,
                    queries = repo.Queries.Select((q, i) => new
                    {
                        q.Text, q.Expected, firstHitRank = perQuery[i].FirstRank,
                        recall = perQuery[i].Recall, precision = perQuery[i].Precision,
                        topPaths = incrementalRanked[i].Take(5)
                    }).ToList()
                });
            }
            finally
            {
                foreach (string db in new[] { "context-graph.db", "semantic-index-v2.db" })
                    try { SqlitePoolIsolation.ClearDatabasePools(Path.Combine(state, db)); } catch { }
                foreach (string dir in new[] { work, state })
                    try { ForceDelete(dir); } catch { }
            }
        }

        if (!string.IsNullOrWhiteSpace(evidenceDir))
        {
            Directory.CreateDirectory(evidenceDir);
            File.WriteAllText(Path.Combine(evidenceDir, "real-corpus-precision.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    method = "lexical path/symbol retrieval; ranked distinct file paths (file hits first, then symbol-owning files); hit = expected path in top-" + TopK,
                    utc = DateTime.UtcNow, results
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static SemanticRepositorySyncResult Sync(SemanticIndexV2Service service, bool force) =>
        service.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "c" }, VerifyAllHashes = true, ForceFull = force
        }).Repositories.Single();

    private static string[] Rank(SemanticIndexV2Service service, string query)
    {
        SemanticIndexSearchResponse response = service.Search(new SemanticIndexSearchRequest
        {
            Query = query, Aliases = new List<string> { "c" }, MaxFiles = 50, MaxSymbols = 100
        });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranked = new List<string>();
        foreach (string file in response.RelevantFiles)
            if (seen.Add(StripAlias(file))) ranked.Add(StripAlias(file));
        foreach (string symbol in response.RelevantSymbols)
        {
            string path = StripAlias(symbol);
            int colon = path.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0) path = path[..colon];
            if (seen.Add(path)) ranked.Add(path);
        }
        return ranked.ToArray();
    }

    private static string StripAlias(string value) => value.StartsWith("c:", StringComparison.Ordinal) ? value[2..] : value;

    private static (double Precision, double Recall, double Rr, int? FirstRank) Score(Query query, string[] ranked)
    {
        string[] top = ranked.Take(TopK).ToArray();
        int hits = top.Count(p => query.Expected.Contains(p, StringComparer.OrdinalIgnoreCase));
        int? first = null;
        for (int i = 0; i < ranked.Length; i++)
            if (query.Expected.Contains(ranked[i], StringComparer.OrdinalIgnoreCase)) { first = i + 1; break; }
        return (top.Length == 0 ? 0 : (double)hits / top.Length, (double)hits / query.Expected.Length,
            first == null ? 0 : 1.0 / first.Value, first);
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)!;
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git " + string.Join(' ', args) + " failed: " + error);
    }

    private static void ForceDelete(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, true);
    }
}
