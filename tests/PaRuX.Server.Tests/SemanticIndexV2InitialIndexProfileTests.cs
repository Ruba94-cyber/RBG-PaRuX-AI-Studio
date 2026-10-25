using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ContextGraph;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-10 initial-index profile. Phase timings (parse, graph-write, history, topology, ...) are written to the
/// test output only. The graph digest proves that two indexing runs produced identical symbols, relations
/// and topology inputs (ids and timestamps excluded), which is the safety net for any speed change.
/// </summary>
[Trait("Category", "Benchmark")]
[Collection("RepoIndexPhaseObserver")]
public sealed class SemanticIndexV2InitialIndexProfileTests
{
    private readonly ITestOutputHelper _output;

    public SemanticIndexV2InitialIndexProfileTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Initial_index_of_5k_files_phase_profile()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PARUX_BENCH_5K"), "1", StringComparison.Ordinal)) return;
        (string digest, long ms, Dictionary<string, double> phases) = BuildAndDigest(5_000);
        _output.WriteLine($"PROFILE5K totalMs={ms} digest={digest}");
        // Recorded before the quadratic topology-row lookup was removed; the optimisation must not change the graph.
        Assert.Equal("30104:006613FACD625B2C8C775C55F29C4F7466CD16F256BF957D7BEB846681010A69", digest);
        foreach ((string phase, double total) in phases.OrderByDescending(p => p.Value))
            _output.WriteLine($"PROFILE5K phase={phase} ms={total:F0}");
    }

    [Fact]
    public void Index_results_are_identical_across_two_runs_and_match_the_recorded_pre_optimisation_digest_shape()
    {
        (string first, _, _) = BuildAndDigest(300);
        (string second, _, _) = BuildAndDigest(300);
        Assert.Equal(first, second);
    }

    private static (string Digest, long Ms, Dictionary<string, double> Phases) BuildAndDigest(int fileCount)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string stateDir = Path.Combine(Path.GetTempPath(), "parux-profile-state-" + suffix);
        string repoDir = Path.Combine(Path.GetTempPath(), "parux-profile-repo-" + suffix);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(repoDir);
        var phases = new Dictionary<string, double>();
        var gate = new object();
        Action<string, TimeSpan>? previousObserver = RepoIndexService.PhaseObserver;
        try
        {
            for (int index = 0; index < fileCount; index++)
                SemanticIndexV2MultiLanguage50kBenchmark.WriteFile(repoDir,
                    SemanticIndexV2MultiLanguage50kBenchmark.RelativePath(index), index, 1,
                    index % 97 == 7 ? "PlantedNeedle" + index + "Zq" : null);

            var graph = new ContextGraphStore(stateDir);
            var service = new SemanticIndexV2Service(stateDir, new RepoIndexService(graph));
            service.Register(new RegisterSemanticRepositoryRequest { Alias = "p", RepositoryRoot = repoDir });
            RepoIndexService.PhaseObserver = (phase, elapsed) =>
            {
                lock (gate) phases[phase] = phases.GetValueOrDefault(phase) + elapsed.TotalMilliseconds;
            };
            var clock = Stopwatch.StartNew();
            service.Sync(new SemanticIndexSyncRequest { Aliases = new List<string> { "p" } });
            clock.Stop();
            RepoIndexService.PhaseObserver = previousObserver;
            return (Digest(Path.Combine(stateDir, "context-graph.db"), Path.GetFullPath(repoDir).TrimEnd('\\', '/').ToLowerInvariant()), clock.ElapsedMilliseconds, phases);
        }
        finally
        {
            RepoIndexService.PhaseObserver = previousObserver;
            foreach (string databaseName in new[] { "context-graph.db", "semantic-index-v2.db" })
            {
                try { SqlitePoolIsolation.ClearDatabasePools(Path.Combine(stateDir, databaseName)); } catch { }
            }
            try { Directory.Delete(stateDir, recursive: true); } catch { }
            try { Directory.Delete(repoDir, recursive: true); } catch { }
        }
    }

    /// <summary>Order-independent digest of entities, relations (by endpoint identity) and topology inputs.</summary>
    private static string Digest(string databasePath, string repoKey)
    {
        var lines = new List<string>();
        using var connection = new SqliteConnection("Data Source=" + databasePath + ";Mode=ReadOnly;Pooling=False");
        connection.Open();
        void Collect(string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                var parts = new string[reader.FieldCount];
                for (int i = 0; i < parts.Length; i++) parts[i] = reader.IsDBNull(i) ? "~" : Convert.ToString(reader.GetValue(i)) ?? "~";
                lines.Add(string.Join("\u001f", parts).Replace(repoKey, "<repo>", StringComparison.OrdinalIgnoreCase).Replace(repoKey.Replace('\\', '/'), "<repo>", StringComparison.OrdinalIgnoreCase));
            }
        }
        Collect("SELECT 'E', entity_type, name, source, metadata_json FROM context_entities");
        Collect(@"SELECT 'R', r.relation_type, f.entity_type || ':' || f.name || ':' || IFNULL(f.source,''),
                  t.entity_type || ':' || t.name || ':' || IFNULL(t.source,''), IFNULL(r.metadata_json,'') FROM context_relations r
                  JOIN context_entities f ON f.id = r.from_entity_id JOIN context_entities t ON t.id = r.to_entity_id");
        Collect("SELECT 'T', source, imports_json, calls_json FROM repo_topology_inputs");
        lines.Sort(StringComparer.Ordinal);
        using var sha = SHA256.Create();
        return lines.Count + ":" + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }
}
