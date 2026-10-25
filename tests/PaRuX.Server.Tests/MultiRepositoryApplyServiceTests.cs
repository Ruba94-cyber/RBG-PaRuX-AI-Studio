using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MultiRepositoryApplyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-multi-apply-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoA;
    private readonly string _repoB;
    private readonly string _aggregate;
    private readonly string _worktreeA;
    private readonly string _worktreeB;
    private readonly AgentRepositorySetDto _authorities;

    public MultiRepositoryApplyServiceTests()
    {
        Directory.CreateDirectory(_root);
        _repoA = CreateRepo("repo-a", "a.txt", "A0\n");
        _repoB = CreateRepo("repo-b", "b.txt", "B0\n");
        _aggregate = Path.Combine(_root, "aggregate");
        Directory.CreateDirectory(_aggregate);
        _worktreeA = Path.Combine(_aggregate, "primary");
        _worktreeB = Path.Combine(_aggregate, "backend");
        Git(_repoA, "worktree", "add", "-b", "parux-test-a", _worktreeA, "HEAD");
        Git(_repoB, "worktree", "add", "-b", "parux-test-b", _worktreeB, "HEAD");
        _authorities = new AgentRepositorySetDto
        {
            TaskId = "test",
            Repositories = new List<AgentRepositoryAuthorityDto>
            {
                new() { Alias = "primary", WorkspaceRoot = _repoA, IsPrimary = true, AssignedUtc = DateTime.UtcNow },
                new() { Alias = "backend", WorkspaceRoot = _repoB, IsPrimary = false, AssignedUtc = DateTime.UtcNow }
            }
        };
    }

    [Fact]
    public async Task Review_and_apply_cover_modified_and_new_files_across_all_repositories()
    {
        File.WriteAllText(Path.Combine(_worktreeA, "a.txt"), "A1\n");
        File.WriteAllText(Path.Combine(_worktreeB, "b.txt"), "B1\n");
        File.WriteAllText(Path.Combine(_worktreeB, "new.txt"), "NEW\n");

        MultiRepositoryReviewResult review = await MultiRepositoryApplyService.ReviewAsync(
            _authorities, _aggregate, CancellationToken.None);
        Assert.True(review.Success, review.Error);
        Assert.True(review.HasChanges);
        Assert.Equal(2, review.Repositories.Count(item => item.HasChanges));

        MultiRepositoryApplyResult apply = await MultiRepositoryApplyService.ApplyAsync(
            _authorities, _aggregate, CancellationToken.None);

        Assert.True(apply.Success, apply.Message);
        Assert.Equal("A1\n", ReadNormalized(Path.Combine(_repoA, "a.txt")));
        Assert.Equal("B1\n", ReadNormalized(Path.Combine(_repoB, "b.txt")));
        Assert.Equal("NEW\n", ReadNormalized(Path.Combine(_repoB, "new.txt")));
        Assert.NotEmpty(Git(_repoA, "status", "--porcelain"));
        Assert.NotEmpty(Git(_repoB, "status", "--porcelain"));
        Assert.Equal("baseline", Git(_repoA, "log", "-1", "--pretty=%s").Trim());
        Assert.Equal("baseline", Git(_repoB, "log", "-1", "--pretty=%s").Trim());
    }

    [Fact]
    public async Task Multi_repository_apply_rejects_a_changed_alias_after_review_without_touching_either_target()
    {
        File.WriteAllText(Path.Combine(_worktreeA, "a.txt"), "reviewed-A\n");
        File.WriteAllText(Path.Combine(_worktreeB, "b.txt"), "reviewed-B\n");
        string? accepted = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            _aggregate, CancellationToken.None);
        Assert.NotNull(accepted);

        File.WriteAllText(Path.Combine(_worktreeB, "b.txt"), "unreviewed-B\n");
        MultiRepositoryApplyResult result = await MultiRepositoryApplyService.ApplyAsync(
            _authorities, _aggregate, CancellationToken.None, accepted);

        Assert.False(result.Success);
        Assert.Equal("A0\n", ReadNormalized(Path.Combine(_repoA, "a.txt")));
        Assert.Equal("B0\n", ReadNormalized(Path.Combine(_repoB, "b.txt")));
    }

    [Fact]
    public async Task Multi_repository_apply_accepts_the_approved_git_revision_and_untracked_file()
    {
        File.WriteAllText(Path.Combine(_worktreeA, "a.txt"), "approved-A\n");
        File.WriteAllText(Path.Combine(_worktreeB, "b.txt"), "approved-B\n");
        File.WriteAllText(Path.Combine(_worktreeB, "new-approved.txt"), "NEW\n");
        string? accepted = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            _aggregate, CancellationToken.None);
        Assert.NotNull(accepted);

        MultiRepositoryApplyResult result = await MultiRepositoryApplyService.ApplyAsync(
            _authorities, _aggregate, CancellationToken.None, accepted);

        Assert.True(result.Success, result.Message);
        Assert.Equal("approved-A\n", ReadNormalized(Path.Combine(_repoA, "a.txt")));
        Assert.Equal("approved-B\n", ReadNormalized(Path.Combine(_repoB, "b.txt")));
        Assert.Equal("NEW\n", ReadNormalized(Path.Combine(_repoB, "new-approved.txt")));
    }

    [Fact]
    public async Task Unrelated_staged_unstaged_and_untracked_target_changes_are_preserved()
    {
        File.WriteAllText(Path.Combine(_worktreeA, "a.txt"), "AGENT-A\n");
        File.WriteAllText(Path.Combine(_worktreeB, "b.txt"), "AGENT-B\n");
        File.WriteAllText(Path.Combine(_worktreeB, "agent-new.txt"), "AGENT-NEW\n");
        File.WriteAllText(Path.Combine(_repoA, "user-staged.txt"), "USER-STAGED\n");
        Git(_repoA, "add", "user-staged.txt");
        File.WriteAllText(Path.Combine(_repoA, "user-unstaged.txt"), "USER-UNSTAGED\n");
        File.WriteAllText(Path.Combine(_repoB, "user-untracked.txt"), "USER-UNTRACKED\n");

        MultiRepositoryApplyResult apply = await MultiRepositoryApplyService.ApplyAsync(
            _authorities, _aggregate, CancellationToken.None);

        Assert.True(apply.Success, apply.Message);
        Assert.Equal("AGENT-A\n", ReadNormalized(Path.Combine(_repoA, "a.txt")));
        Assert.Equal("AGENT-B\n", ReadNormalized(Path.Combine(_repoB, "b.txt")));
        Assert.Equal("AGENT-NEW\n", ReadNormalized(Path.Combine(_repoB, "agent-new.txt")));
        Assert.Equal("USER-STAGED\n", File.ReadAllText(Path.Combine(_repoA, "user-staged.txt")));
        Assert.Equal("USER-UNSTAGED\n", File.ReadAllText(Path.Combine(_repoA, "user-unstaged.txt")));
        Assert.Equal("USER-UNTRACKED\n", File.ReadAllText(Path.Combine(_repoB, "user-untracked.txt")));
        Assert.Equal("user-staged.txt", Git(_repoA, "diff", "--cached", "--name-only").Trim());
        Assert.Contains("user-unstaged.txt", Git(_repoA, "diff", "--name-only"), StringComparison.Ordinal);
        Assert.Contains("a.txt", Git(_repoA, "diff", "--name-only"), StringComparison.Ordinal);
        Assert.Contains("user-untracked.txt", Git(_repoB, "status", "--porcelain"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conflicting_dirty_target_blocks_every_repository_without_losing_user_changes()
    {
        File.WriteAllText(Path.Combine(_worktreeA, "a.txt"), "AGENT-A\n");
        File.WriteAllText(Path.Combine(_worktreeA, "agent-new.txt"), "AGENT-NEW\n");
        File.WriteAllText(Path.Combine(_worktreeB, "b.txt"), "AGENT-B\n");
        File.WriteAllText(Path.Combine(_repoB, "b.txt"), "HUMAN-B\n");

        MultiRepositoryApplyResult apply = await MultiRepositoryApplyService.ApplyAsync(
            _authorities, _aggregate, CancellationToken.None);

        Assert.False(apply.Success);
        Assert.Equal("A0\n", File.ReadAllText(Path.Combine(_repoA, "a.txt")));
        Assert.False(File.Exists(Path.Combine(_repoA, "agent-new.txt")));
        Assert.Equal("HUMAN-B\n", File.ReadAllText(Path.Combine(_repoB, "b.txt")));
    }

    [Fact]
    public async Task Repository_autocrlf_setting_does_not_turn_clean_files_into_agent_changes()
    {
        Git(_repoA, "config", "core.autocrlf", "true");
        Git(_repoA, "worktree", "remove", "--force", _worktreeA);
        Git(_repoA, "worktree", "add", "--detach", _worktreeA, "HEAD");
        foreach (string path in Directory.EnumerateFiles(_worktreeA, "*.txt"))
        {
            string lf = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            File.WriteAllText(path, lf.Replace("\n", "\r\n", StringComparison.Ordinal));
        }
        File.WriteAllText(Path.Combine(_worktreeA, "a.txt"), "A1\r\n");

        MultiRepositoryReviewResult review = await MultiRepositoryApplyService.ReviewAsync(
            _authorities, _aggregate, CancellationToken.None);
        Assert.True(review.Success, review.Error);
        Assert.Equal(1, review.Repositories.Count(item => item.HasChanges));
        Assert.Equal(1, review.Repositories.Single(item => item.Alias == "primary").ChangedFiles);

        MultiRepositoryApplyResult apply = await MultiRepositoryApplyService.ApplyAsync(
            _authorities, _aggregate, CancellationToken.None);

        Assert.True(apply.Success, apply.Message);
        Assert.Equal("A1\n", File.ReadAllText(Path.Combine(_repoA, "a.txt")).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal("a.txt", Git(_repoA, "diff", "--name-only").Trim());
    }

    [Fact]
    public async Task Incompatible_patch_preflight_does_not_apply_other_repository()
    {
        File.WriteAllText(Path.Combine(_worktreeA, "a.txt"), "AGENT-A\n");
        File.WriteAllText(Path.Combine(_worktreeB, "b.txt"), "AGENT-B\n");

        // Move the original backend branch forward with a conflicting clean commit after the task
        // worktree was forked. `git apply --check` must fail before the already-valid primary patch
        // is applied.
        File.WriteAllText(Path.Combine(_repoB, "b.txt"), "HUMAN-B\n");
        Git(_repoB, "add", "b.txt");
        Git(_repoB, "commit", "-m", "human divergence");

        MultiRepositoryApplyResult apply = await MultiRepositoryApplyService.ApplyAsync(
            _authorities, _aggregate, CancellationToken.None);

        Assert.False(apply.Success);
        Assert.Equal("A0\n", File.ReadAllText(Path.Combine(_repoA, "a.txt")));
        Assert.Equal("HUMAN-B\n", File.ReadAllText(Path.Combine(_repoB, "b.txt")));
        Assert.Empty(Git(_repoA, "status", "--porcelain"));
    }

    [Fact]
    public async Task Applying_an_already_applied_set_again_reports_already_present_and_writes_nothing()
    {
        File.WriteAllText(Path.Combine(_worktreeA, "a.txt"), "A1\n");
        File.WriteAllText(Path.Combine(_worktreeB, "b.txt"), "B1\n");
        MultiRepositoryApplyResult first = await MultiRepositoryApplyService.ApplyAsync(
            _authorities, _aggregate, CancellationToken.None);
        Assert.True(first.Success, first.Message);
        string statusBefore = Git(_repoA, "status", "--porcelain") + Git(_repoB, "status", "--porcelain");
        string diffBefore = Git(_repoA, "diff") + Git(_repoB, "diff");

        MultiRepositoryApplyResult second = await MultiRepositoryApplyService.ApplyAsync(
            _authorities, _aggregate, CancellationToken.None);

        Assert.True(second.Success, second.Message);
        Assert.Contains("already present", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, second.AppliedCount);
        Assert.Equal(2, second.AlreadyPresentCount);
        Assert.Equal("A1\n", ReadNormalized(Path.Combine(_repoA, "a.txt")));
        Assert.Equal("B1\n", ReadNormalized(Path.Combine(_repoB, "b.txt")));
        Assert.Equal(statusBefore, Git(_repoA, "status", "--porcelain") + Git(_repoB, "status", "--porcelain"));
        Assert.Equal(diffBefore, Git(_repoA, "diff") + Git(_repoB, "diff"));
        Assert.Null(MultiRepositoryApplyService.FindUnresolvedJournal(_authorities));
    }

    [Fact]
    public async Task A_repository_that_already_holds_its_change_is_skipped_while_the_other_is_applied()
    {
        File.WriteAllText(Path.Combine(_worktreeA, "a.txt"), "A1\n");
        File.WriteAllText(Path.Combine(_worktreeB, "b.txt"), "B1\n");
        File.WriteAllText(Path.Combine(_repoA, "a.txt"), "A1\n"); // The primary already holds the reviewed change.

        MultiRepositoryApplyResult apply = await MultiRepositoryApplyService.ApplyAsync(
            _authorities, _aggregate, CancellationToken.None);

        Assert.True(apply.Success, apply.Message);
        Assert.Contains("already present", apply.Message, StringComparison.OrdinalIgnoreCase);
        // A partial set is reported with typed counts: one repository written, one left alone.
        Assert.Equal(1, apply.AppliedCount);
        Assert.Equal(1, apply.AlreadyPresentCount);
        Assert.Equal("A1\n", ReadNormalized(Path.Combine(_repoA, "a.txt")));
        Assert.Equal("B1\n", ReadNormalized(Path.Combine(_repoB, "b.txt")));
        Assert.Null(MultiRepositoryApplyService.FindUnresolvedJournal(_authorities));
    }

    [Fact]
    public async Task A_failure_before_the_second_repository_rolls_the_first_one_back_and_releases_the_journal()
    {
        File.WriteAllText(Path.Combine(_worktreeA, "a.txt"), "A1\n");
        File.WriteAllText(Path.Combine(_worktreeB, "b.txt"), "B1\n");

        MultiRepositoryApplyResult result;
        using (ShadowApplyFaults.Install((point, detail) =>
        {
            if (point == "multi.before-repository" && detail == "backend")
                throw new IOException("Injected failure before the second repository.");
        }))
        {
            result = await MultiRepositoryApplyService.ApplyAsync(_authorities, _aggregate, CancellationToken.None);
        }

        Assert.False(result.Success);
        Assert.True(result.RollbackAttempted);
        Assert.True(result.RollbackSucceeded, result.Message);
        Assert.Equal("A0\n", ReadNormalized(Path.Combine(_repoA, "a.txt")));
        Assert.Equal("B0\n", ReadNormalized(Path.Combine(_repoB, "b.txt")));
        // Compare content rather than the stat-based status: with core.autocrlf, a restored file can read as "M" there.
        Assert.Empty(Git(_repoA, "diff", "--name-only"));
        Assert.Null(MultiRepositoryApplyService.FindUnresolvedJournal(_authorities));
    }

    private string CreateRepo(string name, string file, string content)
    {
        string repo = Path.Combine(_root, name);
        Directory.CreateDirectory(repo);
        Git(repo, "init");
        Git(repo, "config", "user.name", "PaRuX Tests");
        Git(repo, "config", "user.email", "tests@parux.invalid");
        File.WriteAllText(Path.Combine(repo, file), content);
        File.WriteAllText(Path.Combine(repo, "user-staged.txt"), "STAGED0\n");
        File.WriteAllText(Path.Combine(repo, "user-unstaged.txt"), "UNSTAGED0\n");
        Git(repo, "add", file, "user-staged.txt", "user-unstaged.txt");
        Git(repo, "commit", "-m", "baseline");
        return repo;
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
        if (process.ExitCode != 0)
            throw new InvalidOperationException("git " + string.Join(' ', args) + " failed: " + error);
        return output;
    }

    private static string ReadNormalized(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

    public void Dispose()
    {
        try { Git(_repoA, "worktree", "remove", "--force", _worktreeA); } catch { }
        try { Git(_repoB, "worktree", "remove", "--force", _worktreeB); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
