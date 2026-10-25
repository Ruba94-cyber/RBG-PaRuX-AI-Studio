using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Verifies Git-history refresh is limited to re-indexed files with changed content.</summary>
public sealed class RepoIndexHistorySkipTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "parux-history-skip-state-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "parux-history-skip-repo-" + Guid.NewGuid().ToString("N"));

    public RepoIndexHistorySkipTests()
    {
        Directory.CreateDirectory(_state);
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_state)) Directory.Delete(_state, recursive: true); } catch { }
        try { if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true); } catch { }
    }

    [Fact]
    public async Task Unchanged_content_reindex_skips_history_probe_and_keeps_stored_evidence()
    {
        Write("src/Stable.cs", "public sealed class Stable { public void Stay() {} }\n");
        Write("src/Other.cs", "public sealed class Other { public void Before() {} }\n");
        await InitializeRepository();
        await Commit("baseline");

        var graph = new ContextGraphStore(_state);
        var index = new RepoIndexService(graph);
        index.IndexRepository(_repo);

        ContextEntityDto before = GetFile(graph, "src/Stable.cs");
        int probesBefore = index.GitHistoryProbeCountForTest;
        Assert.Equal(1, probesBefore);
        Assert.Equal(1, GetNumber(before, "gitCommitCount"));

        // A new repository commit does not require a history refresh for a re-indexed file whose
        // bytes still match its stored content hash.
        Write("src/Other.cs", "public sealed class Other { public void After() {} }\n");
        await Git("add", "src/Other.cs");
        await Commit("update other file");
        index.ReindexFiles(_repo, new[] { "src/Stable.cs" });

        ContextEntityDto after = GetFile(graph, "src/Stable.cs");
        Assert.Equal(probesBefore, index.GitHistoryProbeCountForTest);
        Assert.Equal(before.MetadataJson, after.MetadataJson);
    }

    [Fact]
    public async Task Changed_content_reindex_probes_history_once_for_the_batch()
    {
        Write("src/Alpha.cs", "public sealed class Alpha { public void Before() {} }\n");
        Write("src/Beta.cs", "public sealed class Beta { public void Before() {} }\n");
        await InitializeRepository();
        await Commit("baseline");

        var graph = new ContextGraphStore(_state);
        var index = new RepoIndexService(graph);
        index.IndexRepository(_repo);

        string alphaHashBefore = GetString(GetFile(graph, "src/Alpha.cs"), "contentHash");
        string betaHashBefore = GetString(GetFile(graph, "src/Beta.cs"), "contentHash");
        int probesBefore = index.GitHistoryProbeCountForTest;

        Write("src/Alpha.cs", "public sealed class Alpha { public void After() {} }\n");
        Write("src/Beta.cs", "public sealed class Beta { public void After() {} }\n");
        await Commit("update indexed files");
        index.ReindexFiles(_repo, new[] { "src/Alpha.cs", "src/Beta.cs" });

        Assert.Equal(probesBefore + 1, index.GitHistoryProbeCountForTest);
        ContextEntityDto alpha = GetFile(graph, "src/Alpha.cs");
        ContextEntityDto beta = GetFile(graph, "src/Beta.cs");
        Assert.Equal(2, GetNumber(alpha, "gitCommitCount"));
        Assert.Equal(2, GetNumber(beta, "gitCommitCount"));
        Assert.NotEqual(alphaHashBefore, GetString(alpha, "contentHash"));
        Assert.NotEqual(betaHashBefore, GetString(beta, "contentHash"));
    }

    [Fact]
    public async Task Unchanged_content_retry_finishes_pending_topology_without_another_history_probe()
    {
        Write("src/util.ts", "export function oldParser() { return 1; }\n");
        Write("src/main.ts", "import { oldParser } from './util';\nexport function runMain() { return oldParser(); }\n");
        await InitializeRepository();
        await Commit("baseline");

        var graph = new ContextGraphStore(_state);
        var index = new RepoIndexService(graph);
        index.IndexRepository(_repo);

        Write("src/util.ts", "export function newParser() { return 2; }\n");
        Write("src/main.ts", "import { newParser } from './util';\nexport function runMain() { return newParser(); }\n");
        await Commit("update parser usage");
        string[] changedPaths = { "src/util.ts", "src/main.ts" };
        int projectedFiles = 0;
        index.AfterFileProjectedForTest = _ =>
        {
            if (Interlocked.Increment(ref projectedFiles) == changedPaths.Length)
                throw new IOException("simulated interruption before topology reconciliation");
        };

        Assert.Throws<IOException>(() => index.ReindexFiles(_repo, changedPaths));
        index.AfterFileProjectedForTest = null;
        Assert.Equal(2, index.GitHistoryProbeCountForTest);

        ContextEntityDto mainBeforeRetry = GetFile(graph, "src/main.ts");
        ContextEntityDto parserBeforeRetry = graph.ListEntities("symbol").Single(entity => entity.Name == "newParser");
        Assert.DoesNotContain(graph.GetNeighborhood(mainBeforeRetry.Id)!.OutgoingRelations,
            relation => relation.RelationType == "CALLS" && relation.ToEntityId == parserBeforeRetry.Id);

        index.ReindexFiles(_repo, changedPaths);

        Assert.Equal(2, index.GitHistoryProbeCountForTest);
        ContextEntityDto mainAfterRetry = GetFile(graph, "src/main.ts");
        ContextEntityDto parserAfterRetry = graph.ListEntities("symbol").Single(entity => entity.Name == "newParser");
        Assert.Contains(graph.GetNeighborhood(mainAfterRetry.Id)!.OutgoingRelations,
            relation => relation.RelationType == "CALLS" && relation.ToEntityId == parserAfterRetry.Id);
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task InitializeRepository()
    {
        await Git("init", "-b", "main");
        await Git("config", "user.email", "tests@example.com");
        await Git("config", "user.name", "Example Test");
    }

    private async Task Commit(string message)
    {
        await Git("add", "--all");
        await Git("commit", "--quiet", "-m", message);
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
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(_state, "gitconfig");
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("git " + string.Join(' ', arguments) + " failed: " + output + error);
    }

    private static ContextEntityDto GetFile(ContextGraphStore graph, string name) =>
        graph.ListEntities("file").Single(entity => entity.Name == name);

    private static string GetString(ContextEntityDto entity, string property)
    {
        using JsonDocument document = JsonDocument.Parse(entity.MetadataJson!);
        return document.RootElement.GetProperty(property).GetString()!;
    }

    private static int GetNumber(ContextEntityDto entity, string property)
    {
        using JsonDocument document = JsonDocument.Parse(entity.MetadataJson!);
        return document.RootElement.GetProperty(property).GetInt32();
    }
}
