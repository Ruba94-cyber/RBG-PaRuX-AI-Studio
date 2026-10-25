using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MultiRepositoryWorktreeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-multi-wt-" + Guid.NewGuid().ToString("N"));
    private readonly string _state;
    private readonly string _repoA;
    private readonly string _repoB;

    public MultiRepositoryWorktreeServiceTests()
    {
        Directory.CreateDirectory(_root);
        _state = Path.Combine(_root, "state");
        _repoA = CreateRepo("repo-a", "a.txt", "A0\n");
        _repoB = CreateRepo("repo-b", "b.txt", "B0\n");
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("bad\\child")]
    [InlineData("%APPDATA%")]
    public async Task Invalid_task_ids_never_create_aggregate_directories(string taskId)
    {
        MultiRepositoryWorktreeResult result =
            await MultiRepositoryWorktreeService.PrepareInStateDirectoryAsync(
                Set(), taskId, null, _state, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Empty(result.Repositories);
        Assert.False(Directory.Exists(Path.Combine(_state, "multi-worktrees")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".git")]
    [InlineData("CON")]
    [InlineData("bad & alias")]
    [InlineData("trailing.")]
    public async Task Invalid_repository_alias_is_rejected_before_creating_any_worktree(string alias)
    {
        AgentRepositorySetDto set = Set();
        set.Repositories[1].Alias = alias;
        string taskId = "invalid" + Guid.NewGuid().ToString("N")[..12];
        MultiRepositoryWorktreeResult result =
            await MultiRepositoryWorktreeService.PrepareInStateDirectoryAsync(
                set, taskId, null, _state, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Empty(result.Repositories);
        Assert.False(Directory.Exists(Path.Combine(_state, "multi-worktrees", taskId)));
    }

    [Fact]
    public async Task Duplicate_aliases_are_blocked_before_Git_worktree_creation()
    {
        AgentRepositorySetDto set = Set();
        set.Repositories[1].Alias = "PRIMARY";
        string taskId = "duplicate" + Guid.NewGuid().ToString("N")[..12];
        MultiRepositoryWorktreeResult result =
            await MultiRepositoryWorktreeService.PrepareInStateDirectoryAsync(
                set, taskId, null, _state, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Empty(result.Repositories);
        Assert.False(Directory.Exists(Path.Combine(_state, "multi-worktrees", taskId)));
    }

    [Fact]
    public async Task Two_repositories_get_real_independent_worktrees_under_one_plain_workspace()
    {
        AgentRepositorySetDto authorities = Set();
        string taskId = "task" + Guid.NewGuid().ToString("N")[..12];

        MultiRepositoryWorktreeResult result = await MultiRepositoryWorktreeService.PrepareInStateDirectoryAsync(
            authorities, taskId, parentWorkspaceRoot: null, _state, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Repositories.Count);
        Assert.Equal(Path.Combine(_state, "multi-worktrees", taskId), result.WorkspaceRoot);
        Assert.All(result.Repositories, entry =>
        {
            Assert.True(Directory.Exists(entry.WorktreePath));
            Assert.StartsWith(result.WorkspaceRoot, entry.WorktreePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(entry.Branch, Git(entry.WorktreePath, "branch", "--show-current").Trim());
            Assert.False(new DirectoryInfo(entry.WorktreePath).Attributes.HasFlag(FileAttributes.ReparsePoint));
        });

        // This is a real git worktree layout beneath "multi-worktrees",
        // unlike test folders containing fake .git markers. Both host-side
        // proof gates must recognize the correct child gitfiles.
        string? initialProof = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            result.WorkspaceRoot, CancellationToken.None);
        Assert.NotNull(initialProof);
        Assert.Matches("^[0-9a-f]{64}$", initialProof!);

        File.WriteAllText(Path.Combine(result.Repositories.Single(x => x.Alias == "primary").WorktreePath, "a.txt"), "A1\n");
        File.WriteAllText(Path.Combine(result.Repositories.Single(x => x.Alias == "backend").WorktreePath, "b.txt"), "B1\n");

        string? changedProof = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            result.WorkspaceRoot, CancellationToken.None);
        Assert.NotNull(changedProof);
        Assert.NotEqual(initialProof, changedProof);

        Assert.Equal("A0\n", File.ReadAllText(Path.Combine(_repoA, "a.txt")));
        Assert.Equal("B0\n", File.ReadAllText(Path.Combine(_repoB, "b.txt")));
        string combined = await MultiRepositoryWorktreeService.BuildCombinedDiffAsync(result.Repositories, CancellationToken.None);
        Assert.Contains("repository: primary", combined);
        Assert.Contains("repository: backend", combined);
        Assert.Contains("A1", combined);
        Assert.Contains("B1", combined);

        await MultiRepositoryWorktreeService.CleanupAsync(result.Repositories, deleteBranches: true, CancellationToken.None);
    }

    [Fact]
    public async Task Same_task_id_resumes_the_exact_multi_repo_worktrees()
    {
        AgentRepositorySetDto authorities = Set();
        string taskId = "resume" + Guid.NewGuid().ToString("N")[..10];
        MultiRepositoryWorktreeResult first = await MultiRepositoryWorktreeService.PrepareInStateDirectoryAsync(
            authorities, taskId, null, _state, CancellationToken.None);
        Assert.True(first.Success, first.Error);
        File.WriteAllText(Path.Combine(first.Repositories[0].WorktreePath, "resume-marker.txt"), "keep me");

        MultiRepositoryWorktreeResult second = await MultiRepositoryWorktreeService.PrepareInStateDirectoryAsync(
            authorities, taskId, null, _state, CancellationToken.None);

        Assert.True(second.Success, second.Error);
        Assert.All(second.Repositories, entry => Assert.True(entry.Resumed));
        Assert.True(File.Exists(Path.Combine(second.Repositories[0].WorktreePath, "resume-marker.txt")));
        await MultiRepositoryWorktreeService.CleanupAsync(second.Repositories, deleteBranches: true, CancellationToken.None);
    }

    [Fact]
    public async Task Child_continuation_copies_tracked_and_untracked_state_per_repository_without_touching_parent()
    {
        AgentRepositorySetDto authorities = Set();
        string parentId = "parent" + Guid.NewGuid().ToString("N")[..10];
        string childId = "child" + Guid.NewGuid().ToString("N")[..11];
        MultiRepositoryWorktreeResult parent = await MultiRepositoryWorktreeService.PrepareInStateDirectoryAsync(
            authorities, parentId, null, _state, CancellationToken.None);
        Assert.True(parent.Success, parent.Error);

        string parentPrimary = parent.Repositories.Single(x => x.Alias == "primary").WorktreePath;
        string parentBackend = parent.Repositories.Single(x => x.Alias == "backend").WorktreePath;
        File.WriteAllText(Path.Combine(parentPrimary, "a.txt"), "A-parent\n");
        File.WriteAllText(Path.Combine(parentPrimary, "new-a.txt"), "new A\n");
        File.WriteAllText(Path.Combine(parentBackend, "b.txt"), "B-parent\n");
        File.WriteAllText(Path.Combine(parentBackend, "new-b.txt"), "new B\n");

        MultiRepositoryWorktreeResult child = await MultiRepositoryWorktreeService.PrepareInStateDirectoryAsync(
            authorities, childId, parent.WorkspaceRoot, _state, CancellationToken.None);

        Assert.True(child.Success, child.Error);
        string childPrimary = child.Repositories.Single(x => x.Alias == "primary").WorktreePath;
        string childBackend = child.Repositories.Single(x => x.Alias == "backend").WorktreePath;
        Assert.Equal("A-parent\n", File.ReadAllText(Path.Combine(childPrimary, "a.txt")));
        Assert.Equal("new A\n", File.ReadAllText(Path.Combine(childPrimary, "new-a.txt")));
        Assert.Equal("B-parent\n", File.ReadAllText(Path.Combine(childBackend, "b.txt")));
        Assert.Equal("new B\n", File.ReadAllText(Path.Combine(childBackend, "new-b.txt")));
        Assert.Equal("A-parent\n", File.ReadAllText(Path.Combine(parentPrimary, "a.txt")));
        Assert.Equal("B-parent\n", File.ReadAllText(Path.Combine(parentBackend, "b.txt")));

        await MultiRepositoryWorktreeService.CleanupAsync(child.Repositories, deleteBranches: true, CancellationToken.None);
        await MultiRepositoryWorktreeService.CleanupAsync(parent.Repositories, deleteBranches: true, CancellationToken.None);
    }

    private AgentRepositorySetDto Set() => new()
    {
        TaskId = "fixture",
        Repositories = new List<AgentRepositoryAuthorityDto>
        {
            new() { Alias = "primary", WorkspaceRoot = _repoA, IsPrimary = true, AssignedUtc = DateTime.UtcNow },
            new() { Alias = "backend", WorkspaceRoot = _repoB, IsPrimary = false, AssignedUtc = DateTime.UtcNow }
        }
    };

    private string CreateRepo(string name, string file, string content)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        Git(path, "init");
        Git(path, "config", "user.name", "PaRuX Tests");
        Git(path, "config", "user.email", "tests@parux.invalid");
        File.WriteAllText(Path.Combine(path, file), content);
        Git(path, "add", file);
        Git(path, "commit", "-m", "baseline");
        return path;
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git " + string.Join(' ', args) + " failed: " + error);
        return output;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
