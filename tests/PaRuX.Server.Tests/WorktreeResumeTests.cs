using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WorktreeResumeTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "parux-worktree-resume-" + Guid.NewGuid().ToString("N"));

    public WorktreeResumeTests()
    {
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try
        {
            // Prune/remove worktrees before deleting the repository so Git does not leave locked
            // metadata on platforms where the temp directory cannot otherwise be removed cleanly.
            RunGit(_repo, "worktree", "prune");
            if (Directory.Exists(_repo)) Directory.Delete(_repo, true);
        }
        catch { }
    }

    private static void RunGit(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout}\n{stderr}");
    }

    private void InitRepository()
    {
        RunGit(_repo, "init", "-b", "main");
        RunGit(_repo, "config", "user.email", "test@parux.local");
        RunGit(_repo, "config", "user.name", "PaRuX Test");
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "seed\n");
        RunGit(_repo, "add", ".");
        RunGit(_repo, "commit", "-m", "seed");
    }

    [Fact]
    public async Task Existing_task_worktree_is_reused_and_keeps_uncommitted_checkpoint_files()
    {
        InitRepository();
        const string taskId = "task123";

        WorktreeResult first = await WorktreeService.CreateOrResumeAsync(_repo, taskId, CancellationToken.None);
        Assert.True(first.Success, first.Error);
        Assert.False(first.Resumed);

        string checkpointFile = Path.Combine(first.Path, "progress.txt");
        File.WriteAllText(checkpointFile, "work completed before restart\n");

        // Calling again models the desktop process coming back and claiming the same server task.
        WorktreeResult resumed = await WorktreeService.CreateOrResumeAsync(_repo, taskId, CancellationToken.None);

        Assert.True(resumed.Success, resumed.Error);
        Assert.True(resumed.Resumed);
        Assert.Equal(Path.GetFullPath(first.Path), Path.GetFullPath(resumed.Path));
        Assert.Equal(first.Branch, resumed.Branch);
        Assert.Equal("work completed before restart\n", File.ReadAllText(Path.Combine(resumed.Path, "progress.txt")));
    }

    [Fact]
    public async Task Task_worktrees_never_show_up_in_the_users_git_status()
    {
        // Live test: after a Chat coding task, "git status" in the user's repository listed
        // "?? .parux-agents/". The exclusion must be local (.git/info/exclude), never .gitignore.
        InitRepository();
        WorktreeResult first = await WorktreeService.CreateOrResumeAsync(_repo, "taskA", CancellationToken.None);
        Assert.True(first.Success, first.Error);
        WorktreeResult second = await WorktreeService.CreateOrResumeAsync(_repo, "taskB", CancellationToken.None);
        Assert.True(second.Success, second.Error);

        var status = new System.Diagnostics.ProcessStartInfo("git", "status --porcelain")
        { WorkingDirectory = _repo, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(status)!;
        string porcelain = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(string.Empty, porcelain.Trim());
        Assert.False(File.Exists(Path.Combine(_repo, ".gitignore")));
        string exclude = File.ReadAllText(Path.Combine(_repo, ".git", "info", "exclude"));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(exclude, "parux-agents").Count);   // written once
    }

    [Fact]
    public async Task Surviving_branch_can_be_reattached_when_the_worktree_directory_was_removed()
    {
        InitRepository();
        const string taskId = "task456";

        WorktreeResult first = await WorktreeService.CreateOrResumeAsync(_repo, taskId, CancellationToken.None);
        Assert.True(first.Success, first.Error);

        // Remove only the worktree, deliberately preserving the branch as a durable checkpoint.
        RunGit(_repo, "worktree", "remove", first.Path, "--force");
        Assert.False(Directory.Exists(first.Path));

        WorktreeResult resumed = await WorktreeService.CreateOrResumeAsync(_repo, taskId, CancellationToken.None);

        Assert.True(resumed.Success, resumed.Error);
        Assert.True(resumed.Resumed);
        Assert.True(Directory.Exists(resumed.Path));
        Assert.Equal(first.Branch, resumed.Branch);
    }

    [Fact]
    public async Task Unsafe_task_id_is_rejected_before_touching_git_branch_or_path()
    {
        InitRepository();

        WorktreeResult result = await WorktreeService.CreateOrResumeAsync(_repo, "../escape", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not safe", result.Error);
    }
}
