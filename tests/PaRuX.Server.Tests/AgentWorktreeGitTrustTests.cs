using System.Diagnostics;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Security review 2026-09-29: the AppContainer had Modify rights on the repository's common .git,
/// and host-side git ran inside sandbox-writable agent worktrees. A sandboxed command could plant
/// Git configuration (core.fsmonitor, filter drivers) that host Git would execute outside the sandbox.
/// </summary>
public sealed class AgentWorktreeGitTrustTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-git-trust-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _worktree;

    public AgentWorktreeGitTrustTests()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        Git(_repo, "init");
        Git(_repo, "config", "user.name", "PaRuX Tests");
        Git(_repo, "config", "user.email", "tests@parux.invalid");
        File.WriteAllText(Path.Combine(_repo, "A.cs"), "class A { }\n");
        Git(_repo, "add", ".");
        Git(_repo, "commit", "-m", "baseline");
        _worktree = Path.Combine(_repo, ".parux-agents", "task1");
        Git(_repo, "worktree", "add", "-b", "parux-agent/task1", _worktree, "HEAD");
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch { }
    }

    [Fact]
    public async Task Genuine_agent_worktree_is_trusted_and_host_git_runs()
    {
        Assert.True(CodingAgentTools.IsTrustedGitWorkingDirectory(_worktree, out _));
        (int exit, _) = await CodingAgentTools.RunGitAsync(_worktree, CancellationToken.None, "status", "--porcelain=v1");
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Git_link_replaced_by_a_planted_directory_is_refused_before_git_runs()
    {
        File.Delete(Path.Combine(_worktree, ".git"));
        string planted = Path.Combine(_worktree, ".git");
        Directory.CreateDirectory(planted);
        string marker = Path.Combine(_root, "fsmonitor-ran.txt");
        File.WriteAllText(Path.Combine(planted, "config"),
            "[core]\n\trepositoryformatversion = 0\n\tfsmonitor = cmd /c echo pwned > \"" + marker + "\"\n");

        Assert.False(CodingAgentTools.IsTrustedGitWorkingDirectory(_worktree, out string reason));
        Assert.Contains("replaced by a directory", reason, StringComparison.Ordinal);
        (int exit, string output) = await CodingAgentTools.RunGitAsync(_worktree, CancellationToken.None, "status");
        Assert.Equal(-1, exit);
        Assert.StartsWith("(git refused", output, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void Git_link_pointing_inside_the_worktree_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_worktree, "evil"));
        File.Delete(Path.Combine(_worktree, ".git"));
        File.WriteAllText(Path.Combine(_worktree, ".git"), "gitdir: evil\n");

        Assert.False(CodingAgentTools.IsTrustedGitWorkingDirectory(_worktree, out string reason));
        Assert.Contains("inside the worktree", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Git_link_redirected_to_an_outside_directory_cannot_choose_the_sandbox_grant()
    {
        // e.g. "gitdir: C:\Users\me\.ssh" or a sandbox-writable fake repository elsewhere.
        string outside = Path.Combine(_root, "secrets");
        Directory.CreateDirectory(outside);
        File.Delete(Path.Combine(_worktree, ".git"));
        File.WriteAllText(Path.Combine(_worktree, ".git"), "gitdir: " + outside + "\n");

        Assert.False(CodingAgentTools.IsTrustedGitWorkingDirectory(_worktree, out _));
        Assert.Empty(WindowsCodingSandbox.ResolveGitDirectories(_worktree));
    }

    [Fact]
    public void Git_link_to_another_repositorys_worktree_metadata_is_refused()
    {
        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        Git(other, "init");
        Git(other, "config", "user.name", "PaRuX Tests");
        Git(other, "config", "user.email", "tests@parux.invalid");
        File.WriteAllText(Path.Combine(other, "B.cs"), "class B { }\n");
        Git(other, "add", ".");
        Git(other, "commit", "-m", "b");
        string otherWorktree = Path.Combine(_root, "other-wt");
        Git(other, "worktree", "add", otherWorktree, "HEAD");
        string otherGitDir = File.ReadAllText(Path.Combine(otherWorktree, ".git")).Trim()["gitdir:".Length..].Trim();
        File.Delete(Path.Combine(_worktree, ".git"));
        File.WriteAllText(Path.Combine(_worktree, ".git"), "gitdir: " + otherGitDir + "\n");

        Assert.False(CodingAgentTools.IsTrustedGitWorkingDirectory(_worktree, out string reason));
        Assert.Contains("different", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Genuine_worktree_grants_only_its_own_metadata_read_only()
    {
        List<string> dirs = WindowsCodingSandbox.ResolveGitDirectories(_worktree);
        Assert.Equal(2, dirs.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(_repo, ".git")), dirs[1], ignoreCase: true);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(_repo, ".git", "worktrees")), dirs[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_refuses_a_worktree_with_an_altered_git_link()
    {
        File.Delete(Path.Combine(_worktree, ".git"));
        Directory.CreateDirectory(Path.Combine(_worktree, ".git"));

        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(_repo, _worktree, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cannot be reviewed safely", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_repositories_are_not_treated_as_agent_worktrees()
    {
        Assert.True(CodingAgentTools.IsTrustedGitWorkingDirectory(_repo, out _));
        Assert.Equal("(OI)(CI)(RX)", WindowsCodingSandbox.GitMetadataRights);
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo { FileName = "git", WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git " + string.Join(' ', args) + ": " + error);
    }
}
