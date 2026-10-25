using PaRuX.Server.ContextGraph;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Repository Intelligence (roadmap P2/C2) - RepoIndexService against real temp repositories with
/// real source files, not fixtures pretending to be code. No fake local model is needed here since
/// the embedding engine is never installed in CI/dev by default (same reasoning EmbeddingService's
/// own tests use) - these tests exercise the always-available keyword/token-overlap path, and the
/// hybrid embedding blend is exercised implicitly (TryEmbed returns null, ScoreSymbol correctly
/// falls back to keyword-only, matching MemoryStore.Retrieve's own precedent).
/// </summary>
public sealed class RepoIndexServiceTests : IDisposable
{
    private readonly string _dbDir = Path.Combine(Path.GetTempPath(), "parux-repoindex-db-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoDir = Path.Combine(Path.GetTempPath(), "parux-repoindex-repo-" + Guid.NewGuid().ToString("N"));

    public RepoIndexServiceTests() => Directory.CreateDirectory(_repoDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, recursive: true); } catch { /* best effort */ }
        try { if (Directory.Exists(_repoDir)) Directory.Delete(_repoDir, recursive: true); } catch { /* best effort */ }
    }

    private ContextGraphStore Graph() => new(_dbDir);
    private RepoIndexService Build() => new(Graph());

    private void WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(_repoDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ---- Symbol extraction ----

    [Fact]
    public void ExtractSymbols_finds_CSharp_classes_and_methods()
    {
        string[] lines =
        {
            "namespace Foo;",
            "public sealed class TaskStore",
            "{",
            "    public AgentTaskDto Create(CreateAgentTaskRequest request)",
            "    {",
            "        return null;",
            "    }",
            "    private void Helper() { }",
            "}"
        };

        List<(string Name, string Signature, int LineNumber)> symbols = RepoIndexService.ExtractSymbols("TaskStore.cs", lines);

        Assert.Contains(symbols, s => s.Name == "TaskStore" && s.LineNumber == 2);
        Assert.Contains(symbols, s => s.Name == "Create" && s.LineNumber == 4);
        // Private members are indexed too - a coding agent navigating a file it's editing needs
        // to find a private helper just as much as a public one.
        Assert.Contains(symbols, s => s.Name == "Helper");
    }

    [Fact]
    public void ExtractSymbols_finds_TypeScript_functions_and_classes()
    {
        string[] lines =
        {
            "export class Widget {",
            "  render() {}",
            "}",
            "export function buildWidget() {",
            "  return new Widget();",
            "}"
        };

        List<(string Name, string Signature, int LineNumber)> symbols = RepoIndexService.ExtractSymbols("widget.ts", lines);

        Assert.Contains(symbols, s => s.Name == "Widget");
        Assert.Contains(symbols, s => s.Name == "buildWidget");
    }

    [Fact]
    public void ExtractSymbols_finds_Python_defs_and_classes()
    {
        string[] lines = { "class Repository:", "    def load(self):", "        pass" };

        List<(string Name, string Signature, int LineNumber)> symbols = RepoIndexService.ExtractSymbols("repo.py", lines);

        Assert.Contains(symbols, s => s.Name == "Repository");
        Assert.Contains(symbols, s => s.Name == "load");
    }

    [Fact]
    public void ExtractSymbols_returns_nothing_for_an_unrecognized_extension()
    {
        Assert.Empty(RepoIndexService.ExtractSymbols("readme.md", new[] { "# Title", "public class Foo" }));
    }

    // ---- Test-to-code linking heuristic ----

    [Theory]
    [InlineData("TaskStoreTests.cs", "TaskStore.cs")]
    [InlineData("test_repository.py", "repository.py")]
    [InlineData("widget.test.ts", "widget.ts")]
    public void TryResolveCodeFileForTest_matches_common_naming_conventions(string testFile, string codeFile)
    {
        string? resolved = RepoIndexService.TryResolveCodeFileForTest(testFile, new[] { codeFile, "unrelated.cs" });
        Assert.Equal(codeFile, resolved);
    }

    [Fact]
    public void TryResolveCodeFileForTest_returns_null_when_no_convention_matches()
    {
        Assert.Null(RepoIndexService.TryResolveCodeFileForTest("Program.cs", new[] { "Other.cs" }));
    }

    // ---- Tokenization ----

    [Fact]
    public void Tokenize_splits_camelCase_and_pascalCase_identifiers()
    {
        HashSet<string> tokens = RepoIndexService.Tokenize("TryBeginRepair");
        Assert.Contains("try", tokens);
        Assert.Contains("begin", tokens);
        Assert.Contains("repair", tokens);
    }

    [Fact]
    public void Tokenize_drops_stop_words_and_short_tokens()
    {
        HashSet<string> tokens = RepoIndexService.Tokenize("fix the bug in a store");
        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("in", tokens);
        Assert.DoesNotContain("a", tokens);
        Assert.Contains("fix", tokens);
        Assert.Contains("bug", tokens);
        Assert.Contains("store", tokens);
    }

    // ---- Full indexing + retrieval, against a real temp repository ----

    [Fact]
    public void IndexRepository_indexes_files_and_symbols_and_excludes_build_output()
    {
        WriteFile("src/TaskStore.cs", "public sealed class TaskStore\n{\n    public void Create() {}\n}");
        WriteFile("src/VerifierService.cs", "public sealed class VerifierService\n{\n    public void ReviewAsync() {}\n}");
        WriteFile("bin/Generated.cs", "public sealed class ShouldBeExcluded {}");
        WriteFile(".git/HEAD", "ref: refs/heads/main");

        RepoIndexService index = Build();
        PaRuX.Contracts.RepoIndexResult result = index.IndexRepository(_repoDir);

        Assert.Equal(2, result.FilesIndexed);
        Assert.True(result.SymbolsIndexed >= 2);
    }

    [Fact]
    public void IndexRepository_is_idempotent_and_does_not_accumulate_duplicates()
    {
        WriteFile("src/Foo.cs", "public sealed class Foo\n{\n    public void Bar() {}\n}");
        ContextGraphStore graph = Graph();
        RepoIndexService index = new(graph);

        index.IndexRepository(_repoDir);
        index.IndexRepository(_repoDir);
        index.IndexRepository(_repoDir);

        // Asserts against the actual store, not the returned DTO's own loop counters - a
        // regression where RemoveRepositoryIndex silently became a no-op would still report
        // identical counts every call while the real row count underneath kept doubling.
        // "Foo" (the class) and "Bar" (the method) are 2 genuinely distinct symbols in this file.
        Assert.Single(graph.ListEntities("file"));
        Assert.Equal(2, graph.ListEntities("symbol").Count);
    }

    [Fact]
    public void IndexRepository_links_a_test_file_to_its_code_file()
    {
        WriteFile("src/TaskStore.cs", "public sealed class TaskStore\n{\n    public void Create() {}\n}");
        WriteFile("tests/TaskStoreTests.cs", "public sealed class TaskStoreTests\n{\n    public void Create_works() {}\n}");

        RepoIndexService index = Build();
        PaRuX.Contracts.RepoIndexResult result = index.IndexRepository(_repoDir);

        Assert.Equal(1, result.TestRelationsFound);
    }

    [Fact]
    public void SearchContext_ranks_a_matching_symbol_above_an_unrelated_one()
    {
        WriteFile("src/BackupService.cs", "public sealed class BackupService\n{\n    public void ExportAsync() {}\n}");
        WriteFile("src/UnrelatedThing.cs", "public sealed class UnrelatedThing\n{\n    public void DoStuff() {}\n}");

        RepoIndexService index = Build();
        index.IndexRepository(_repoDir);

        PaRuX.Contracts.RepoContextPack pack = index.SearchContext(_repoDir, "fix a bug in the backup export logic");

        Assert.Contains(pack.RelevantFiles, f => f.Contains("BackupService"));
        Assert.DoesNotContain(pack.RelevantFiles, f => f.Contains("UnrelatedThing"));
        Assert.Contains(pack.RelevantSymbols, s => s.Contains("ExportAsync"));
    }

    [Fact]
    public void SearchContext_respects_maxFiles_and_maxSymbols_bounds()
    {
        for (int i = 0; i < 10; i++)
            WriteFile($"src/BackupHelper{i}.cs", $"public sealed class BackupHelper{i}\n{{\n    public void BackupThing{i}() {{}}\n}}");

        RepoIndexService index = Build();
        index.IndexRepository(_repoDir);

        PaRuX.Contracts.RepoContextPack pack = index.SearchContext(_repoDir, "backup", maxFiles: 3, maxSymbols: 4);

        Assert.True(pack.RelevantFiles.Count <= 3);
        Assert.True(pack.RelevantSymbols.Count <= 4);
    }

    [Fact]
    public void SearchContext_returns_empty_for_a_blank_goal()
    {
        WriteFile("src/Foo.cs", "public sealed class Foo {}");
        RepoIndexService index = Build();
        index.IndexRepository(_repoDir);

        PaRuX.Contracts.RepoContextPack pack = index.SearchContext(_repoDir, "   ");

        Assert.Empty(pack.RelevantFiles);
        Assert.Empty(pack.RelevantSymbols);
    }

    // ---- Incremental invalidation ----

    [Fact]
    public void ReindexFile_reflects_a_renamed_symbol_without_a_full_repository_walk()
    {
        WriteFile("src/Widget.cs", "public sealed class Widget\n{\n    public void OldName() {}\n}");
        RepoIndexService index = Build();
        index.IndexRepository(_repoDir);

        WriteFile("src/Widget.cs", "public sealed class Widget\n{\n    public void NewName() {}\n}");
        index.ReindexFile(_repoDir, "src/Widget.cs");

        PaRuX.Contracts.RepoContextPack oldPack = index.SearchContext(_repoDir, "old name");
        PaRuX.Contracts.RepoContextPack newPack = index.SearchContext(_repoDir, "new name");

        Assert.DoesNotContain(oldPack.RelevantSymbols, s => s.Contains("OldName"));
        Assert.Contains(newPack.RelevantSymbols, s => s.Contains("NewName"));
    }

    [Fact]
    public void RemoveFileIndex_removes_both_the_files_own_entity_and_its_symbols()
    {
        WriteFile("src/ToDelete.cs", "public sealed class ToDelete\n{\n    public void DoomedMethod() {}\n}");
        ContextGraphStore graph = Graph();
        RepoIndexService index = new(graph);
        index.IndexRepository(_repoDir);
        Assert.Single(graph.ListEntities("file")); // sanity check before removal

        index.RemoveFileIndex(_repoDir, "src/ToDelete.cs");

        // Checks the store directly, not just SearchContext's symbol results - a bug that left
        // the file entity itself orphaned (as an earlier version of this method did) would still
        // pass a symbols-only assertion.
        Assert.Empty(graph.ListEntities("file"));
        Assert.Empty(graph.ListEntities("symbol"));
        Assert.Empty(index.SearchContext(_repoDir, "doomed method").RelevantSymbols);
    }

    [Fact]
    public void File_and_repository_cleanup_preserve_non_index_entities_that_share_the_source()
    {
        WriteFile("src/ToDelete.cs", "public sealed class ToDelete\n{\n    public void DoomedMethod() {}\n}");
        ContextGraphStore graph = Graph();
        string source = "repo-index:" + Path.GetFullPath(_repoDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant()
            + "::src/ToDelete.cs";
        ContextEntityDto artifact = graph.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "artifact",
            Name = "retained artifact",
            Source = source
        });
        RepoIndexService index = new(graph);
        index.IndexRepository(_repoDir);

        index.RemoveFileIndex(_repoDir, "src/ToDelete.cs");

        Assert.Contains(graph.ListEntities("artifact"), entity => entity.Id == artifact.Id);
        Assert.Empty(graph.ListEntities("file"));
        Assert.Empty(graph.ListEntities("symbol"));

        index.IndexRepository(_repoDir);
        index.RemoveRepositoryIndex(_repoDir);

        Assert.Contains(graph.ListEntities("artifact"), entity => entity.Id == artifact.Id);
    }

    [Fact]
    public void Reindexing_a_deleted_file_removes_its_old_projection()
    {
        WriteFile("src/Widget.cs", "public sealed class Widget\n{\n    public void LastGoodName() {}\n}");
        ContextGraphStore graph = Graph();
        RepoIndexService index = new(graph);
        index.IndexRepository(_repoDir);
        string filePath = Path.Combine(_repoDir, "src", "Widget.cs");
        File.Delete(filePath);

        index.ReindexFile(_repoDir, "src/Widget.cs");

        Assert.Empty(graph.ListEntities("file"));
        Assert.Empty(graph.ListEntities("symbol"));
    }

    [Fact]
    public void Failed_read_of_an_existing_file_throws_and_keeps_the_last_good_projection()
    {
        WriteFile("src/Widget.cs", "public sealed class Widget\n{\n    public void LastGoodName() {}\n}");
        ContextGraphStore graph = Graph();
        RepoIndexService index = new(graph);
        index.IndexRepository(_repoDir);
        string filePath = Path.Combine(_repoDir, "src", "Widget.cs");

        using (new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => index.ReindexFile(_repoDir, "src/Widget.cs"));
        }

        Assert.Contains(graph.ListEntities("symbol"), entity => entity.Name == "LastGoodName");
    }

    [Fact]
    public void ReindexFile_does_not_leave_a_stale_duplicate_file_entity_behind()
    {
        WriteFile("src/Widget.cs", "public sealed class Widget\n{\n    public void OldName() {}\n}");
        ContextGraphStore graph = Graph();
        RepoIndexService index = new(graph);
        index.IndexRepository(_repoDir);

        // Re-index the same file several times, as a real editing session would.
        for (int i = 0; i < 3; i++)
            index.ReindexFile(_repoDir, "src/Widget.cs");

        Assert.Single(graph.ListEntities("file"));
    }

    [Fact]
    public void Recently_used_agent_worktree_scopes_are_never_pruned()
    {
        // Review: a running task whose folder write time looks old must keep its index.
        string agents = Path.Combine(_repoDir, ".parux-agents");
        var worktrees = Enumerable.Range(0, 8).Select(i => Path.Combine(agents, "task" + i)).ToList();
        RepoIndexService index = Build();
        foreach (string worktree in worktrees)
        {
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, "Task.cs"), "public sealed class Task {}");
            index.IndexRepository(worktree);
            Directory.SetLastWriteTimeUtc(worktree, DateTime.UtcNow.AddDays(-5));
        }

        Assert.Equal(0, index.PruneStaleAgentWorktreeScopes(_repoDir, DateTime.UtcNow.AddMinutes(30)));
        Assert.NotEmpty(index.SearchContext(worktrees[0], "task").RelevantFiles);
    }

    [Fact]
    public void Scope_queries_include_paths_with_supplementary_characters()
    {
        WriteFile("src/😀emoji.cs", "public sealed class Smile {}");
        RepoIndexService index = Build();
        index.IndexRepository(_repoDir);

        Assert.Contains(index.SearchContext(_repoDir, "emoji").RelevantFiles, f => f.Contains("emoji", StringComparison.Ordinal));
        index.RemoveRepositoryIndex(_repoDir);
        Assert.Empty(index.SearchContext(_repoDir, "emoji").RelevantFiles);
    }

    [Fact]
    public void Es_module_files_are_indexed()
    {
        // Live Chat run: a Node repository of .mjs files indexed 0 files, so the Context Engine had nothing.
        WriteFile("src/cart.mjs", "export function subtotal(items) {\n  return 0;\n}\n");
        WriteFile("lib/util.cjs", "function helper() {}\nmodule.exports = { helper };\n");

        RepoIndexResult result = Build().IndexRepository(_repoDir);

        Assert.Equal(2, result.FilesIndexed);
        Assert.Contains(RepoIndexService.ExtractSymbols("src/cart.mjs", new[] { "export function subtotal(items) {" }), s => s.Name == "subtotal");
    }

    [Fact]
    public void Stale_and_surplus_agent_worktree_scopes_are_pruned_but_user_repositories_are_kept()
    {
        // Live Chat run: every task's worktree index accumulated (1.4 GB) and slowed every index/search.
        string agents = Path.Combine(_repoDir, ".parux-agents");
        var worktrees = Enumerable.Range(0, 9).Select(i => Path.Combine(agents, "task" + i)).ToList();
        string deletedRepo = Path.Combine(Path.GetTempPath(), "parux-repoindex-gone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(deletedRepo);
        File.WriteAllText(Path.Combine(deletedRepo, "Gone.cs"), "public sealed class Gone {}");
        WriteFile("src/Keep.cs", "public sealed class Keep {}");
        for (int i = 0; i < worktrees.Count; i++)
        {
            Directory.CreateDirectory(worktrees[i]);
            File.WriteAllText(Path.Combine(worktrees[i], "Task.cs"), "public sealed class Task" + i + " {}");
        }

        RepoIndexService index = Build();
        index.IndexRepository(_repoDir);
        index.IndexRepository(deletedRepo);
        for (int i = 0; i < worktrees.Count; i++)
        {
            index.IndexRepository(worktrees[i]);
            Directory.SetLastWriteTimeUtc(worktrees[i], DateTime.UtcNow.AddMinutes(i - 100));
        }
        Directory.Delete(deletedRepo, recursive: true);

        int removed = index.PruneStaleAgentWorktreeScopes(_repoDir, DateTime.UtcNow.AddHours(3));

        Assert.Equal(1 + 3, removed); // the deleted repository plus the 3 oldest of 9 agent worktrees
        Assert.NotEmpty(index.SearchContext(_repoDir, "keep").RelevantFiles);
        Assert.Empty(index.SearchContext(worktrees[0], "task").RelevantFiles);
        Assert.NotEmpty(index.SearchContext(worktrees[8], "task").RelevantFiles);
        // Within the interval a second call does no work.
        Assert.Equal(0, index.PruneStaleAgentWorktreeScopes(_repoDir, DateTime.UtcNow.AddHours(3).AddMinutes(1)));
    }

    [Fact]
    public void RemoveRepositoryIndex_does_not_affect_a_different_repository()
    {
        string otherRepoDir = Path.Combine(Path.GetTempPath(), "parux-repoindex-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherRepoDir);
        try
        {
            WriteFile("src/Shared.cs", "public sealed class Shared\n{\n    public void DoWork() {}\n}");
            File.WriteAllText(Path.Combine(otherRepoDir, "Shared.cs"), "public sealed class Shared\n{\n    public void DoWork() {}\n}");

            RepoIndexService index = Build();
            index.IndexRepository(_repoDir);
            index.IndexRepository(otherRepoDir);

            index.RemoveRepositoryIndex(_repoDir);

            // "shared" matches the file name itself (RelevantFiles scores by file-name tokens,
            // not contained symbols) - a goal has to actually mention something the file is named
            // for FileScore to be nonzero.
            Assert.Empty(index.SearchContext(_repoDir, "shared").RelevantFiles);
            Assert.NotEmpty(index.SearchContext(otherRepoDir, "shared").RelevantFiles);
        }
        finally
        {
            try { Directory.Delete(otherRepoDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The exact shape review caught as a structurally-guaranteed collision, not a
    /// theoretical one: repoRoot "...\repo" is a literal string-prefix of a sibling
    /// "...\repo-extra" - and WorktreeService.CreateAsync places every real agent worktree at
    /// "&lt;repoRoot&gt;\.parux-agents\&lt;taskId&gt;", a genuine subdirectory of repoRoot, which is the
    /// same collision shape. Without a delimiter after the repo key, RemoveRepositoryIndex(repo)
    /// would also wipe repo-extra's entities, and SearchContext(repo, ...) would leak
    /// repo-extra's symbols into results.</summary>
    [Fact]
    public void Source_scoping_does_not_collide_with_a_sibling_repo_whose_path_is_a_string_prefix_extension()
    {
        string siblingRepoDir = _repoDir + "-extra";
        Directory.CreateDirectory(siblingRepoDir);
        try
        {
            WriteFile("Original.cs", "public sealed class Original\n{\n    public void OnlyInOriginal() {}\n}");
            File.WriteAllText(Path.Combine(siblingRepoDir, "Sibling.cs"), "public sealed class Sibling\n{\n    public void OnlyInSibling() {}\n}");

            ContextGraphStore graph = Graph();
            RepoIndexService index = new(graph);
            index.IndexRepository(_repoDir);
            index.IndexRepository(siblingRepoDir);

            // Re-indexing (which clears-then-rebuilds) the shorter path must not touch the
            // longer sibling path's entities.
            index.IndexRepository(_repoDir);

            Assert.Contains(graph.ListEntities("symbol"), e => e.Name == "OnlyInSibling");
            Assert.Contains(index.SearchContext(siblingRepoDir, "only in sibling").RelevantSymbols, s => s.Contains("OnlyInSibling"));
            Assert.DoesNotContain(index.SearchContext(_repoDir, "only in sibling").RelevantSymbols, s => s.Contains("OnlyInSibling"));
        }
        finally
        {
            try { Directory.Delete(siblingRepoDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- Multi-line signatures ----

    [Fact]
    public void ExtractSymbols_finds_a_CSharp_method_whose_signature_wraps_across_multiple_lines()
    {
        string[] lines =
        {
            "public sealed class Service",
            "{",
            "    public async Task<Foo> DoSomethingAsync(",
            "        Bar request,",
            "        CancellationToken cancellationToken)",
            "    {",
            "        return null;",
            "    }",
            "}"
        };

        List<(string Name, string Signature, int LineNumber)> symbols = RepoIndexService.ExtractSymbols("Service.cs", lines);

        Assert.Contains(symbols, s => s.Name == "DoSomethingAsync" && s.LineNumber == 3);
    }
}
