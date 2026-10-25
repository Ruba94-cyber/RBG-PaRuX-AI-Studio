using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentWorktreeGitTrustCharacterizationTests
{
    [Theory]
    [InlineData(@"C:\Users\me\repo\src")]
    [InlineData(@"C:\repo\work")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("worktree")]
    [InlineData(null)]
    [InlineData("")]
    public void IsAgentWorktreePath_returns_false_for_paths_without_marker_segments(string? path)
    {
        Assert.False(AgentWorktreeGitTrust.IsAgentWorktreePath(path));
    }

    [Theory]
    [InlineData(@"C:\repo\.parux-agents\task1")]
    [InlineData(@"C:\repo\.parux-agents\task1\src")]
    [InlineData(@"C:\multi-worktrees\shared\task2")]
    [InlineData(@"C:\a\b\multi-worktrees")]
    public void IsAgentWorktreePath_returns_true_when_marker_segment_is_present(string path)
    {
        Assert.True(AgentWorktreeGitTrust.IsAgentWorktreePath(path));
    }

    [Theory]
    [InlineData(@"C:\repo\.PARUX-AGENTS\t")]
    [InlineData(@"C:\repo\Multi-Worktrees")]
    [InlineData(@"C:\repo\.Parux-Agents\t")]
    public void IsAgentWorktreePath_marker_match_is_case_insensitive(string path)
    {
        Assert.True(AgentWorktreeGitTrust.IsAgentWorktreePath(path));
    }

    [Fact]
    public void IsAgentWorktreePath_alt_separator_is_recognized()
    {
        Assert.True(AgentWorktreeGitTrust.IsAgentWorktreePath(@"C:/repo/.parux-agents/t1"));
    }

    [Theory]
    [InlineData(@"C:\parux-parux\agents\t")]
    [InlineData(@"C:\x\not-parux-agents\t")]
    [InlineData(@"C:\x\multi-worktree\t")]
    [InlineData(@"C:\x\my-multi-worktrees2\t")]
    [InlineData(@"C:\x\parux-agents\t")]
    public void IsAgentWorktreePath_only_exact_segments_match(string path)
    {
        Assert.False(AgentWorktreeGitTrust.IsAgentWorktreePath(path));
    }

    [Theory]
    [InlineData(@"C:\Users\me\repo\src\")]
    [InlineData(@"C:\repo\main")]
    public void IsTrustedGitWorkingDirectory_trusts_non_agent_paths(string path)
    {
        Assert.True(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(path, out string reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_trusts_non_agent_paths_case_insensitively()
    {
        Assert.True(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(@"c:\users\me\repo", out string reason));
        Assert.Equal(string.Empty, reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@"C:\Users\me\repo\.parux-agents\t1")]
    [InlineData(@"C:\repo\multi-worktrees\shared\t2")]
    public void IsTrustedGitWorkingDirectory_never_trusts_agent_worktree_paths_without_validated_metadata(string? path)
    {
        Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(path, out string reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_null_path_is_refused_with_invalid_path_reason()
    {
        Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(null!, out string reason));
        Assert.Equal("invalid path", reason);
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_missing_git_link_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string worktree = Path.Combine(root, ".parux-agents", "t1");
        Directory.CreateDirectory(worktree);
        try
        {
            Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(worktree, out string reason));
            Assert.Equal("the agent worktree has no .git link", reason);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_dot_git_directory_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string worktree = Path.Combine(root, ".parux-agents", "t1");
        Directory.CreateDirectory(worktree);
        try
        {
            Directory.CreateDirectory(Path.Combine(worktree, ".git"));
            Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(worktree, out string reason));
            Assert.Equal("the agent worktree's .git was replaced by a directory", reason);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_malformed_git_link_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string worktree = Path.Combine(root, ".parux-agents", "t1");
        Directory.CreateDirectory(worktree);
        try
        {
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdirX C:\\repo\\.git\\worktrees\\t1\n");
            Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(worktree, out string reason));
            Assert.Equal("the agent worktree's .git link is malformed", reason);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_git_link_pointing_inside_worktree_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string worktree = Path.Combine(root, ".parux-agents", "t1");
        Directory.CreateDirectory(worktree);
        try
        {
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: .\n");
            Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(worktree, out string reason));
            Assert.Equal("the agent worktree's .git link points inside the worktree", reason);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_missing_git_metadata_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string worktree = Path.Combine(root, ".parux-agents", "t1");
        Directory.CreateDirectory(worktree);
        try
        {
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + Path.Combine(root, "missing") + "\n");
            Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(worktree, out string reason));
            Assert.Equal("the agent worktree's Git metadata is missing or redirected", reason);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_link_not_naming_worktrees_entry_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string worktree = Path.Combine(root, ".parux-agents", "t1");
        Directory.CreateDirectory(worktree);
        try
        {
            string gitDir = Path.Combine(root, "repo", ".git", "t1");
            Directory.CreateDirectory(gitDir);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + gitDir + "\n");
            Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(worktree, out string reason));
            Assert.Equal("the agent worktree's .git link does not name a repository's " + @".git\worktrees" + " entry", reason);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_metadata_under_sandbox_writable_area_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string worktree = Path.Combine(root, "multi-worktrees", "t1");
        string common = Path.Combine(root, "coding-agent-state", "sandbox", "repo", ".git");
        string gitDir = Path.Combine(common, "worktrees", "t1");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(gitDir);
        try
        {
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + gitDir + "\n");
            Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(worktree, out string reason));
            Assert.Equal("the agent worktree's Git metadata lives in a sandbox-writable location", reason);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_worktree_under_parux_agents_must_use_primary_repository_git()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(root, "repo");
        string worktree = Path.Combine(repo, ".parux-agents", "t1");
        string otherCommon = Path.Combine(root, "other", ".git");
        string otherGitDir = Path.Combine(otherCommon, "worktrees", "t1");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(otherGitDir);
        try
        {
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + otherGitDir + "\n");
            Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(worktree, out string reason));
            Assert.Equal("the agent worktree's .git link names a different repository", reason);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_missing_back_link_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(root, "repo");
        string worktree = Path.Combine(repo, ".parux-agents", "t1");
        string gitDir = Path.Combine(repo, ".git", "worktrees", "t1");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(gitDir);
        try
        {
            File.WriteAllText(Path.Combine(worktree, ".git"),
                "gitdir: " + gitDir + "\n");
            Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(worktree, out string reason));
            Assert.Equal("the worktree's Git metadata has no back-link", reason);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_back_link_naming_different_worktree_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(root, "repo");
        string worktree = Path.Combine(repo, ".parux-agents", "t1");
        string gitDir = Path.Combine(repo, ".git", "worktrees", "t1");
        string otherWorktree = Path.Combine(repo, ".parux-agents", "other");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(otherWorktree);
        try
        {
            File.WriteAllText(Path.Combine(worktree, ".git"),
                "gitdir: " + gitDir + "\n");
            File.WriteAllText(Path.Combine(gitDir, "gitdir"),
                Path.Combine(otherWorktree, ".git") + "\n");
            Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(worktree, out string reason));
            Assert.Equal("the worktree's Git metadata belongs to a different worktree", reason);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_commondir_declaring_different_common_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(root, "repo");
        string worktree = Path.Combine(repo, ".parux-agents", "t1");
        string gitDir = Path.Combine(repo, ".git", "worktrees", "t1");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(gitDir);
        try
        {
            File.WriteAllText(Path.Combine(worktree, ".git"),
                "gitdir: " + gitDir + "\n");
            File.WriteAllText(Path.Combine(gitDir, "gitdir"),
                Path.Combine(worktree, ".git") + "\n");
            File.WriteAllText(Path.Combine(gitDir, "commondir"),
                Path.Combine(root, "wrong", ".git") + "\n");
            Assert.False(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(worktree, out string reason));
            Assert.Equal("the worktree's Git metadata declares a different common directory", reason);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void IsTrustedGitWorkingDirectory_genuine_worktree_shape_is_trusted()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(root, "repo");
        string worktree = Path.Combine(repo, ".parux-agents", "t1");
        string gitDir = Path.Combine(repo, ".git", "worktrees", "t1");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(gitDir);
        try
        {
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + gitDir + "\n");
            File.WriteAllText(Path.Combine(gitDir, "gitdir"), Path.Combine(worktree, ".git") + "\n");
            File.WriteAllText(Path.Combine(gitDir, "commondir"), Path.Combine(repo, ".git") + "\n");

            Assert.True(AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(worktree, out string reason));
            Assert.Equal(string.Empty, reason);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void TryResolveWorktreeMetadata_genuine_shape_exposes_git_and_common_directories()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(root, "repo");
        string worktree = Path.Combine(repo, "multi-worktrees", "t1");
        string gitDir = Path.Combine(repo, ".git", "worktrees", "t1");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(gitDir);
        try
        {
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + gitDir + "\n");
            File.WriteAllText(Path.Combine(gitDir, "gitdir"), Path.Combine(worktree, ".git") + "\n");

            Assert.True(AgentWorktreeGitTrust.TryResolveWorktreeMetadata(worktree, out string gitDirectory, out string commonDirectory, out string reason));
            Assert.Equal(string.Empty, reason);
            Assert.Equal(Path.GetFullPath(gitDir), gitDirectory, ignoreCase: true);
            Assert.Equal(Path.GetFullPath(Path.Combine(repo, ".git")), commonDirectory, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
