using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

// Pins the per-file accept/reject contract of the apply services. Only accepted paths may reach the user's folder or
// repository, an empty selection must never be read as "apply everything", and the reported diff must describe only
// the accepted files.
public sealed class ShadowAcceptedFileSelectionTests : IDisposable
{
    private readonly string _tempRoot;

    public ShadowAcceptedFileSelectionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "parux-accepted-shadow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private async Task<(string Folder, string Shadow, string Worktree)> PrepareReviewedTaskAsync(string name)
    {
        string folder = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "keep.txt"), "keep original");
        await File.WriteAllTextAsync(Path.Combine(folder, "skip.txt"), "skip original");

        string shadow = await ShadowWorkspaceService.EnsureShadowRepositoryAsync(folder);
        WorktreeResult wt = await WorktreeService.CreateAsync(shadow, "task-" + name, CancellationToken.None);
        Assert.True(wt.Success, wt.Error);
        await File.WriteAllTextAsync(Path.Combine(wt.Path, "keep.txt"), "keep approved");
        await File.WriteAllTextAsync(Path.Combine(wt.Path, "skip.txt"), "skip approved");
        return (folder, shadow, wt.Path);
    }

    [Fact]
    public async Task Accepted_subset_applies_only_the_accepted_file_and_reports_only_it()
    {
        var (folder, shadow, worktree) = await PrepareReviewedTaskAsync("shadow-subset");
        string? approved = await CodingWorkspaceVerificationFingerprint.CaptureAsync(worktree, CancellationToken.None);
        Assert.NotNull(approved);

        CodeApplyResult result = await ShadowWorkspaceService.ApplyToOriginalAsync(
            shadow, worktree, CancellationToken.None, approved, acceptedFiles: new[] { "keep.txt" });

        Assert.True(result.Success, result.Message);
        Assert.Equal("keep approved", await File.ReadAllTextAsync(Path.Combine(folder, "keep.txt")));
        Assert.Equal("skip original", await File.ReadAllTextAsync(Path.Combine(folder, "skip.txt")));
        Assert.DoesNotContain("skip.txt", result.DiffStat ?? string.Empty);
    }

    [Fact]
    public async Task Empty_accepted_selection_is_refused_and_touches_no_user_file()
    {
        var (folder, shadow, worktree) = await PrepareReviewedTaskAsync("shadow-empty");
        string? approved = await CodingWorkspaceVerificationFingerprint.CaptureAsync(worktree, CancellationToken.None);
        Assert.NotNull(approved);

        CodeApplyResult result = await ShadowWorkspaceService.ApplyToOriginalAsync(
            shadow, worktree, CancellationToken.None, approved, acceptedFiles: Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Equal("keep original", await File.ReadAllTextAsync(Path.Combine(folder, "keep.txt")));
        Assert.Equal("skip original", await File.ReadAllTextAsync(Path.Combine(folder, "skip.txt")));
    }
}

public sealed class WorktreeAcceptedFileSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-accepted-worktree-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _worktree;

    public WorktreeAcceptedFileSelectionTests()
    {
        Directory.CreateDirectory(_root);
        _repo = Path.Combine(_root, "repo");
        _worktree = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(_repo);
        Git(_repo, "init");
        Git(_repo, "config", "user.name", "PaRuX Tests");
        Git(_repo, "config", "user.email", "tests@parux.invalid");
        File.WriteAllText(Path.Combine(_repo, "keep.txt"), "keep before\n");
        File.WriteAllText(Path.Combine(_repo, "skip.txt"), "skip before\n");
        Git(_repo, "add", "keep.txt", "skip.txt");
        Git(_repo, "commit", "-m", "baseline");
        Git(_repo, "worktree", "add", "-b", "parux-accepted-test", _worktree, "HEAD");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { }
    }

    [Fact]
    public async Task Accepted_subset_applies_only_the_accepted_file_and_reports_only_it()
    {
        File.WriteAllText(Path.Combine(_worktree, "keep.txt"), "keep after\n");
        File.WriteAllText(Path.Combine(_worktree, "skip.txt"), "skip after\n");

        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(
            _repo, _worktree, CancellationToken.None, acceptedFiles: new[] { "keep.txt" });

        Assert.True(result.Success, result.Message);
        Assert.Equal("keep after", File.ReadAllText(Path.Combine(_repo, "keep.txt")).Trim());
        Assert.Equal("skip before", File.ReadAllText(Path.Combine(_repo, "skip.txt")).Trim());
        Assert.DoesNotContain("skip.txt", result.DiffStat ?? string.Empty);
    }

    [Fact]
    public async Task Empty_accepted_selection_is_refused_and_touches_no_target_file()
    {
        File.WriteAllText(Path.Combine(_worktree, "keep.txt"), "keep after\n");

        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(
            _repo, _worktree, CancellationToken.None, acceptedFiles: Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Equal("keep before", File.ReadAllText(Path.Combine(_repo, "keep.txt")).Trim());
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("git could not start.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("git " + string.Join(' ', args) + " failed: " + error);
        return output;
    }
}
