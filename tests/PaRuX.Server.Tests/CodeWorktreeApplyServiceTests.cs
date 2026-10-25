using System.Diagnostics;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodeWorktreeApplyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-apply-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _worktree;

    public CodeWorktreeApplyServiceTests()
    {
        Directory.CreateDirectory(_root);
        _repo = Path.Combine(_root, "repo");
        _worktree = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(_repo);
        Git(_repo, "init");
        Git(_repo, "config", "user.name", "PaRuX Tests");
        Git(_repo, "config", "user.email", "tests@parux.invalid");
        File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "before\n");
        File.WriteAllText(Path.Combine(_repo, "staged.txt"), "before-staged\n");
        File.WriteAllText(Path.Combine(_repo, "unstaged.txt"), "before-unstaged\n");
        Git(_repo, "add", "tracked.txt", "staged.txt", "unstaged.txt");
        Git(_repo, "commit", "-m", "baseline");
        Git(_repo, "worktree", "add", "-b", "parux-test", _worktree, "HEAD");
    }

    [Fact]
    public async Task Clean_target_receives_modified_and_new_files_without_commit()
    {
        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "after\n");
        File.WriteAllText(Path.Combine(_worktree, "new.txt"), "new-file\n");

        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(_repo, _worktree, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("after\n", ReadNormalized(Path.Combine(_repo, "tracked.txt")));
        Assert.Equal("new-file\n", ReadNormalized(Path.Combine(_repo, "new.txt")));
        Assert.False(string.IsNullOrWhiteSpace(Git(_repo, "status", "--porcelain")));
        Assert.Equal("baseline", Git(_repo, "log", "-1", "--pretty=%s").Trim());
    }

    [Fact]
    public async Task Verified_fingerprint_must_equal_the_reviewed_state_before_anything_is_applied()
    {
        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "verified\n");
        string? verified = await CodingWorkspaceVerificationFingerprint.CaptureAsync(_worktree, CancellationToken.None);
        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "changed after verification\n");
        string? reviewed = await CodingWorkspaceVerificationFingerprint.CaptureAsync(_worktree, CancellationToken.None);
        Assert.NotNull(verified);
        Assert.NotNull(reviewed);
        Assert.NotEqual(verified, reviewed);

        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(
            _repo, _worktree, CancellationToken.None, reviewed, verified);

        Assert.False(result.Success);
        Assert.Contains("verified", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("before\n", ReadNormalized(Path.Combine(_repo, "tracked.txt")));
    }

    [Fact]
    public async Task Verified_fingerprint_equal_to_the_reviewed_state_still_applies()
    {
        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "verified and reviewed\n");
        string? state = await CodingWorkspaceVerificationFingerprint.CaptureAsync(_worktree, CancellationToken.None);
        Assert.NotNull(state);

        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(
            _repo, _worktree, CancellationToken.None, state, state);

        Assert.True(result.Success, result.Message);
        Assert.Equal("verified and reviewed\n", ReadNormalized(Path.Combine(_repo, "tracked.txt")));
    }

    [Fact]
    public async Task Verified_fingerprint_without_a_reviewed_fingerprint_is_refused()
    {
        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "verified\n");
        string? verified = await CodingWorkspaceVerificationFingerprint.CaptureAsync(_worktree, CancellationToken.None);
        Assert.NotNull(verified);

        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(
            _repo, _worktree, CancellationToken.None, approvedWorkspaceFingerprint: null,
            verifiedWorkspaceFingerprint: verified);

        Assert.False(result.Success);
        Assert.Equal("before\n", ReadNormalized(Path.Combine(_repo, "tracked.txt")));
    }

    [Fact]
    public async Task Reviewed_fingerprint_rejects_changed_agent_worktree_without_modifying_target()
    {
        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "reviewed\n");
        string? accepted = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            _worktree, CancellationToken.None);
        Assert.NotNull(accepted);

        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "changed after review\n");
        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(
            _repo, _worktree, CancellationToken.None, accepted);

        Assert.False(result.Success);
        Assert.Contains("review", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("before\n", ReadNormalized(Path.Combine(_repo, "tracked.txt")));
    }

    [Fact]
    public async Task Reviewed_fingerprint_allows_exact_tracked_and_new_files()
    {
        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "approved\n");
        File.WriteAllText(Path.Combine(_worktree, "new-approved.txt"), "new-file\n");
        string? accepted = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            _worktree, CancellationToken.None);
        Assert.NotNull(accepted);

        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(
            _repo, _worktree, CancellationToken.None, accepted);

        Assert.True(result.Success, result.Message);
        Assert.Equal("approved\n", ReadNormalized(Path.Combine(_repo, "tracked.txt")));
        Assert.Equal("new-file\n", ReadNormalized(Path.Combine(_repo, "new-approved.txt")));
    }

    [Fact]
    public async Task Committing_the_reviewed_edit_invalidates_its_apply_fingerprint()
    {
        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "before commit\n");
        string? accepted = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            _worktree, CancellationToken.None);
        Assert.NotNull(accepted);
        Git(_worktree, "add", "--", "tracked.txt");
        Git(_worktree, "commit", "-m", "changed post review");

        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(
            _repo, _worktree, CancellationToken.None, accepted);
        Assert.False(result.Success);
        Assert.Equal("before\n", ReadNormalized(Path.Combine(_repo, "tracked.txt")));
    }

    [Fact]
    public async Task Unrelated_staged_and_unstaged_target_changes_are_preserved_when_agent_patch_is_applied()
    {
        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "agent-change\n");
        File.WriteAllText(Path.Combine(_worktree, "agent-new.txt"), "new-agent-file\n");
        File.WriteAllText(Path.Combine(_repo, "staged.txt"), "human-staged\n");
        Git(_repo, "add", "staged.txt");
        File.WriteAllText(Path.Combine(_repo, "unstaged.txt"), "human-unstaged\n");
        File.WriteAllText(Path.Combine(_repo, "human-untracked.txt"), "human-untracked\n");

        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(_repo, _worktree, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("agent-change\n", ReadNormalized(Path.Combine(_repo, "tracked.txt")));
        Assert.Equal("new-agent-file\n", ReadNormalized(Path.Combine(_repo, "agent-new.txt")));
        Assert.Equal("human-staged\n", ReadNormalized(Path.Combine(_repo, "staged.txt")));
        Assert.Equal("human-unstaged\n", ReadNormalized(Path.Combine(_repo, "unstaged.txt")));
        Assert.Equal("human-untracked\n", ReadNormalized(Path.Combine(_repo, "human-untracked.txt")));
        Assert.Equal("staged.txt", Git(_repo, "diff", "--cached", "--name-only").Trim());
        Assert.Contains("unstaged.txt", Git(_repo, "diff", "--name-only"), StringComparison.Ordinal);
        Assert.Contains("tracked.txt", Git(_repo, "diff", "--name-only"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conflicting_target_edit_rejects_the_entire_agent_patch_without_changes()
    {
        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "agent-change\n");
        File.WriteAllText(Path.Combine(_worktree, "agent-new.txt"), "new-agent-file\n");
        File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "human-change\n");

        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(_repo, _worktree, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("does not apply cleanly", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("human-change\n", ReadNormalized(Path.Combine(_repo, "tracked.txt")));
        Assert.False(File.Exists(Path.Combine(_repo, "agent-new.txt")));
    }

    [Fact]
    public async Task Applying_the_same_reviewed_changes_twice_reports_already_applied_and_changes_nothing()
    {
        // Live Chat check: the second "Apply" click reported "patch does not apply".
        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "after\n");

        CodeApplyResult first = await CodeWorktreeApplyService.ApplyAsync(_repo, _worktree, CancellationToken.None);
        string afterFirst = Git(_repo, "diff", "HEAD", "--");
        CodeApplyResult second = await CodeWorktreeApplyService.ApplyAsync(_repo, _worktree, CancellationToken.None);

        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        Assert.Contains("already present", second.Message, StringComparison.Ordinal);
        Assert.Equal(afterFirst, Git(_repo, "diff", "HEAD", "--"));
    }

    [Fact]
    public async Task Post_apply_verification_requires_the_exact_patch_even_when_the_target_is_already_dirty()
    {
        File.WriteAllText(Path.Combine(_worktree, "tracked.txt"), "agent-change\n");
        string patch = Path.Combine(_root, "agent.patch");
        File.WriteAllText(patch, Git(_worktree, "diff", "--binary", "HEAD", "--"));
        File.WriteAllText(Path.Combine(_repo, "unstaged.txt"), "human-unstaged\n");
        Assert.False(string.IsNullOrWhiteSpace(Git(_repo, "status", "--porcelain")));

        Assert.False(await CodeWorktreeApplyService.IsPatchAppliedAsync(_repo, patch));

        Git(_repo, "apply", "--binary", patch);
        Assert.True(await CodeWorktreeApplyService.IsPatchAppliedAsync(_repo, patch));
    }

    [Fact]
    public async Task Same_directory_is_rejected()
    {
        CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(_repo, _repo, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("same directory", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Timed_out_git_process_tree_is_stopped_and_exited_before_return()
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("ping 127.0.0.1 -n 15 > nul");

        CodeWorktreeApplyService.GitProcessRunResult result = await CodeWorktreeApplyService.RunProcessAsync(
            start, TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(result.ProcessId > 0);
        Assert.False(ProcessExists(result.ProcessId), "Timed out Git process remained active after the apply service returned.");
        Assert.Contains("process tree was stopped and exited", result.Error, StringComparison.Ordinal);
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

    private static string ReadNormalized(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static bool ProcessExists(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    public void Dispose()
    {
        try { Git(_repo, "worktree", "remove", "--force", _worktree); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
