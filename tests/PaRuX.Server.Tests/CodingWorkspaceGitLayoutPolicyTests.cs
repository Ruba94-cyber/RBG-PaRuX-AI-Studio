using System;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingWorkspaceGitLayoutPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-git-layout-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void An_ordinary_directory_is_not_a_git_repository_just_because_host_git_could_trust_it()
    {
        Directory.CreateDirectory(_root);
        Assert.True(CodingAgentTools.IsTrustedGitWorkingDirectory(_root, out _));
        Assert.False(CodingWorkspaceGitLayoutPolicy.TrySelect(
            _root, 16, out var repositories));
        Assert.Empty(repositories);
    }

    [Fact]
    public void Direct_git_marker_is_required_and_resolves_one_worktree()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Assert.True(CodingWorkspaceGitLayoutPolicy.TrySelect(
            _root, 16, out var repositories));
        var repo = Assert.Single(repositories);
        Assert.Equal(".", repo.Alias);
        Assert.Equal(Path.GetFullPath(_root), repo.Root);
    }

    [Fact]
    public void Aggregate_requires_every_direct_child_git_marker_and_safe_alias()
    {
        Directory.CreateDirectory(_root);
        foreach (string alias in new[] { "backend", "frontend" })
        {
            string child = Path.Combine(_root, alias);
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(Path.Combine(child, ".git"));
        }

        Assert.True(CodingWorkspaceGitLayoutPolicy.TrySelect(
            _root, 16, out var repositories));
        Assert.Equal(new[] { "backend", "frontend" },
            repositories.Select(item => item.Alias));

        Directory.CreateDirectory(Path.Combine(_root, "untrusted"));
        Assert.False(CodingWorkspaceGitLayoutPolicy.TrySelect(
            _root, 16, out _));
    }

    [Fact]
    public void Repository_count_budget_and_shell_unsafe_name_fail_closed()
    {
        Directory.CreateDirectory(_root);
        foreach (string alias in new[] { "repo-1", "repo-2" })
        {
            string child = Path.Combine(_root, alias);
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(Path.Combine(child, ".git"));
        }
        Assert.False(CodingWorkspaceGitLayoutPolicy.TrySelect(_root, 1, out _));
        Assert.True(CodingWorkspaceGitLayoutPolicy.TrySelect(_root, 2, out _));

        string unsafeChild = Path.Combine(_root, "bad & injected");
        Directory.CreateDirectory(unsafeChild);
        Directory.CreateDirectory(Path.Combine(unsafeChild, ".git"));
        Assert.False(CodingWorkspaceGitLayoutPolicy.TrySelect(_root, 16, out _));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
