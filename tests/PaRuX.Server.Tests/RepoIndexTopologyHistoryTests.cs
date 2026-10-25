using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Regression coverage for repository-intelligence gaps that were still present after Semantic
/// Index V2: local dependency/call topology and bounded Git-history relevance. Tests use real files
/// and a real temporary Git repository; no model or external network is involved.
/// </summary>
public sealed class RepoIndexTopologyHistoryTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "parux-topology-state-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "parux-topology-repo-" + Guid.NewGuid().ToString("N"));

    public RepoIndexTopologyHistoryTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { if (Directory.Exists(_state)) Directory.Delete(_state, recursive: true); } catch { }
        try { if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true); } catch { }
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void IndexRepository_creates_resolved_dependency_and_unambiguous_call_edges()
    {
        Write("src/util.ts", "export function parsePayload() { return 7; }\n");
        Write("src/main.ts", "import { parsePayload } from './util';\nexport function runMain() { return parsePayload(); }\n");

        var graph = new ContextGraphStore(_state);
        var index = new RepoIndexService(graph);
        RepoIndexResult result = index.IndexRepository(_repo);

        Assert.True(result.DependencyRelationsFound >= 1);
        Assert.True(result.CallRelationsFound >= 1);

        ContextEntityDto main = graph.ListEntities("file").Single(entity => entity.Name == "src/main.ts");
        ContextNeighborhoodResponse neighborhood = graph.GetNeighborhood(main.Id)!;
        Assert.Contains(neighborhood.OutgoingRelations, relation => relation.RelationType == "DEPENDS_ON");
        Assert.Contains(neighborhood.OutgoingRelations, relation => relation.RelationType == "CALLS");
    }

    [Fact]
    public void Unresolved_call_names_do_not_consume_the_emitted_call_edge_budget()
    {
        Write("src/target.ts", "export function zzzzUniqueTarget() { return 7; }\n");
        string noise = string.Join("\n", Enumerable.Range(0, 120).Select(i => $"aaaNoise{i:000}();"));
        Write("src/main.ts", "export function runMain() {\n" + noise + "\nreturn zzzzUniqueTarget();\n}\n");

        var graph = new ContextGraphStore(_state);
        var index = new RepoIndexService(graph);
        RepoIndexResult result = index.IndexRepository(_repo);

        ContextEntityDto main = graph.ListEntities("file").Single(entity => entity.Name == "src/main.ts");
        ContextEntityDto target = graph.ListEntities("symbol").Single(entity => entity.Name == "zzzzUniqueTarget");
        ContextNeighborhoodResponse neighborhood = graph.GetNeighborhood(main.Id)!;

        Assert.True(result.CallRelationsFound >= 1);
        Assert.Contains(neighborhood.OutgoingRelations,
            relation => relation.RelationType == "CALLS" && relation.ToEntityId == target.Id);
    }

    [Fact]
    public void Ambiguous_symbol_name_does_not_create_a_fake_call_edge()
    {
        Write("src/a.ts", "export function processItem() { return 1; }\n");
        Write("src/b.ts", "export function processItem() { return 2; }\n");
        Write("src/main.ts", "export function runMain() { return processItem(); }\n");

        var graph = new ContextGraphStore(_state);
        var index = new RepoIndexService(graph);
        RepoIndexResult result = index.IndexRepository(_repo);

        ContextEntityDto main = graph.ListEntities("file").Single(entity => entity.Name == "src/main.ts");
        ContextNeighborhoodResponse neighborhood = graph.GetNeighborhood(main.Id)!;
        Assert.DoesNotContain(neighborhood.OutgoingRelations, relation => relation.RelationType == "CALLS");
        Assert.Equal(0, result.CallRelationsFound);
    }

    [Fact]
    public void ReindexFile_recreates_incoming_dependency_edges_to_the_replaced_file_entity()
    {
        Write("src/util.ts", "export function helperValue() { return 1; }\n");
        Write("src/main.ts", "import { helperValue } from './util';\nexport function runMain() { return helperValue(); }\n");

        var graph = new ContextGraphStore(_state);
        var index = new RepoIndexService(graph);
        index.IndexRepository(_repo);

        Write("src/util.ts", "export function helperValue() { return 2; }\n");
        index.ReindexFile(_repo, "src/util.ts");

        ContextEntityDto main = graph.ListEntities("file").Single(entity => entity.Name == "src/main.ts");
        ContextEntityDto util = graph.ListEntities("file").Single(entity => entity.Name == "src/util.ts");
        ContextNeighborhoodResponse neighborhood = graph.GetNeighborhood(main.Id)!;
        Assert.Contains(neighborhood.OutgoingRelations,
            relation => relation.RelationType == "DEPENDS_ON" && relation.ToEntityId == util.Id);
    }

    [Fact]
    public void ReindexFiles_updates_call_topology_for_a_multi_file_edit_without_full_rebuild()
    {
        Write("src/util.ts", "export function oldParser() { return 1; }\n");
        Write("src/main.ts", "import { oldParser } from './util';\nexport function runMain() { return oldParser(); }\n");

        var graph = new ContextGraphStore(_state);
        var index = new RepoIndexService(graph);
        index.IndexRepository(_repo);

        Write("src/util.ts", "export function newParser() { return 2; }\n");
        Write("src/main.ts", "import { newParser } from './util';\nexport function runMain() { return newParser(); }\n");
        index.ReindexFiles(_repo, new[] { "src/util.ts", "src/main.ts" });

        ContextEntityDto main = graph.ListEntities("file").Single(entity => entity.Name == "src/main.ts");
        ContextEntityDto newParser = graph.ListEntities("symbol").Single(entity => entity.Name == "newParser");
        ContextNeighborhoodResponse neighborhood = graph.GetNeighborhood(main.Id)!;
        Assert.Contains(neighborhood.OutgoingRelations,
            relation => relation.RelationType == "CALLS" && relation.ToEntityId == newParser.Id);
        Assert.DoesNotContain(graph.ListEntities("symbol"), entity => entity.Name == "oldParser");
    }

    [Fact]
    public async Task Git_history_reorders_only_already_relevant_files()
    {
        Write("src/AlphaCold.cs", "public sealed class AlphaCold { public void Work() {} }\n");
        Write("src/AlphaHot.cs", "public sealed class AlphaHot { public void Work() {} }\n");
        Write("src/CompletelyDifferent.cs", "public sealed class CompletelyDifferent {}\n");

        await Git("init", "-b", "main");
        await Git("config", "user.email", "tests@parux.local");
        await Git("config", "user.name", "PaRuX Tests");
        await Git("add", ".");
        await Git("commit", "-m", "initial");

        // A second real commit touches only AlphaHot: both alpha files remain equally relevant by
        // file-name tokens, so bounded history evidence should break the tie in favor of the hot file.
        File.AppendAllText(Path.Combine(_repo, "src", "AlphaHot.cs"), "// second revision\n");
        await Git("add", "src/AlphaHot.cs");
        await Git("commit", "-m", "touch hot alpha");

        var index = new RepoIndexService(new ContextGraphStore(_state));
        RepoIndexResult result = index.IndexRepository(_repo);
        RepoContextPack pack = index.SearchContext(_repo, "alpha", maxFiles: 3, maxSymbols: 3);

        Assert.True(result.FilesWithGitHistory >= 2);
        Assert.True(pack.RelevantFiles.Count >= 2);
        Assert.Equal("src/AlphaHot.cs", pack.RelevantFiles[0]);
        Assert.DoesNotContain(pack.RelevantFiles, path => path.Contains("CompletelyDifferent", StringComparison.Ordinal));
    }

    private async Task Git(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await stdout;
        string error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("git " + string.Join(' ', arguments) + " failed: " + output + error);
    }
}
