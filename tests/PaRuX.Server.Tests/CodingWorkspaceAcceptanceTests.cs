using System.Diagnostics;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Non-model acceptance journeys for the Codex-class coding substrate. They use real temporary
/// Git repositories and the exact linked desktop helpers the coding agent calls: isolated
/// worktrees, stale-content-checked edit, verification discovery, git review, restart/resume and
/// parent->child continuation snapshots.
/// </summary>
public sealed class CodingWorkspaceAcceptanceTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "parux-coding-acceptance-" + Guid.NewGuid().ToString("N"));

    public CodingWorkspaceAcceptanceTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { RunGit(_repo, "worktree", "prune"); } catch { }
        try { if (Directory.Exists(_repo)) Directory.Delete(_repo, true); } catch { }
    }

    [Fact]
    public async Task Project_task_can_edit_verify_review_cancel_and_resume_without_touching_main_checkout()
    {
        InitRepository();
        const string taskId = "acceptance-task";
        WorktreeResult? active = null;

        try
        {
            active = await WorktreeService.CreateOrResumeAsync(_repo, taskId, CancellationToken.None);
            Assert.True(active.Success, active.Error);
            Assert.False(active.Resumed);
            Assert.NotEqual(Path.GetFullPath(_repo), Path.GetFullPath(active.Path));

            string programPath = Path.Combine(active.Path, "Program.cs");
            string original = await File.ReadAllTextAsync(programPath);
            (bool success, string updated, string? error) = CodingAgentTools.TryReplaceRange(
                original,
                "public static string Message => \"before\";",
                "public static string Message => \"after\";");
            Assert.True(success, error);
            await File.WriteAllTextAsync(programPath, updated);

            IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> verification =
                CodingVerificationDiscovery.Discover(active.Path);
            Assert.Contains(verification, v => v.Kind == "build" && v.Command == "dotnet build");
            Assert.Contains(verification, v => v.Kind == "test" && v.Command == "dotnet test");

            (int checkExit, string checkOutput) = await CodingAgentTools.RunGitAsync(
                active.Path, CancellationToken.None, "diff", "--check");
            Assert.Equal(0, checkExit);
            Assert.True(string.IsNullOrWhiteSpace(checkOutput), checkOutput);

            (int diffExit, string diff) = await CodingAgentTools.RunGitAsync(
                active.Path, CancellationToken.None, "diff", "--", "Program.cs");
            Assert.Equal(0, diffExit);
            Assert.Contains("-    public static string Message => \"before\";", diff);
            Assert.Contains("+    public static string Message => \"after\";", diff);

            string checkpoint = Path.Combine(active.Path, "agent-checkpoint.txt");
            await File.WriteAllTextAsync(checkpoint, "review pending\n");

            WorktreeResult resumed = await WorktreeService.CreateOrResumeAsync(_repo, taskId, CancellationToken.None);
            Assert.True(resumed.Success, resumed.Error);
            Assert.True(resumed.Resumed);
            Assert.Equal(Path.GetFullPath(active.Path), Path.GetFullPath(resumed.Path));
            Assert.Contains("=> \"after\";", await File.ReadAllTextAsync(Path.Combine(resumed.Path, "Program.cs")));
            Assert.Equal("review pending\n", await File.ReadAllTextAsync(Path.Combine(resumed.Path, "agent-checkpoint.txt")));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                CodingAgentTools.Grep(resumed.Path, "after", useRegex: false, cancelled.Token));

            await WorktreeService.CleanupAsync(_repo, resumed.Path, resumed.Branch, deleteBranch: true, CancellationToken.None);
            active = null;

            string mainProgram = await File.ReadAllTextAsync(Path.Combine(_repo, "Program.cs"));
            Assert.Contains("=> \"before\";", mainProgram);
            Assert.False(File.Exists(Path.Combine(_repo, "agent-checkpoint.txt")));
        }
        finally
        {
            if (active is { Success: true })
            {
                try { await WorktreeService.CleanupAsync(_repo, active.Path, active.Branch, deleteBranch: true, CancellationToken.None); }
                catch { }
            }
        }
    }

    [Fact]
    public async Task Android_style_child_continuation_inherits_parent_working_snapshot_without_mutating_parent_or_main()
    {
        InitRepository();
        const string parentId = "phone-parent";
        const string childId = "phone-child";
        WorktreeResult? parent = null;
        WorktreeResult? child = null;

        try
        {
            parent = await WorktreeService.CreateOrResumeAsync(_repo, parentId, CancellationToken.None);
            Assert.True(parent.Success, parent.Error);

            string parentProgram = Path.Combine(parent.Path, "Program.cs");
            string text = await File.ReadAllTextAsync(parentProgram);
            await File.WriteAllTextAsync(parentProgram, text.Replace("\"before\"", "\"from-parent\""));

            // Binary tracked changes must survive too; ForkFromParentAsync uses git diff --binary.
            byte[] parentBinary = { 9, 8, 7, 6, 5, 4, 3, 2, 1 };
            await File.WriteAllBytesAsync(Path.Combine(parent.Path, "asset.bin"), parentBinary);

            // An intentional untracked checkpoint models agent-created source/config that has not
            // been committed yet. Ignored build output would not be included by the fork API.
            string parentUntracked = Path.Combine(parent.Path, "new-from-agent.txt");
            await File.WriteAllTextAsync(parentUntracked, "uncommitted parent state\n");

            child = await WorktreeService.ForkFromParentAsync(_repo, childId, parent.Path, CancellationToken.None);
            Assert.True(child.Success, child.Error);
            Assert.False(child.Resumed);
            Assert.NotEqual(Path.GetFullPath(parent.Path), Path.GetFullPath(child.Path));
            Assert.NotEqual(parent.Branch, child.Branch);

            Assert.Contains("\"from-parent\"", await File.ReadAllTextAsync(Path.Combine(child.Path, "Program.cs")));
            Assert.Equal(parentBinary, await File.ReadAllBytesAsync(Path.Combine(child.Path, "asset.bin")));
            Assert.Equal("uncommitted parent state\n", await File.ReadAllTextAsync(Path.Combine(child.Path, "new-from-agent.txt")));

            // Once forked, the child is independent. Continuing work from Android must not change
            // the reviewable parent or the user's main checkout.
            await File.AppendAllTextAsync(Path.Combine(child.Path, "Program.cs"), "// child-only\n");
            Assert.DoesNotContain("child-only", await File.ReadAllTextAsync(parentProgram));
            Assert.Contains("\"from-parent\"", await File.ReadAllTextAsync(parentProgram));

            string mainProgram = await File.ReadAllTextAsync(Path.Combine(_repo, "Program.cs"));
            Assert.Contains("\"before\"", mainProgram);
            Assert.DoesNotContain("from-parent", mainProgram);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(_repo, "asset.bin")));
            Assert.False(File.Exists(Path.Combine(_repo, "new-from-agent.txt")));

            // Reclaiming the child after a desktop restart must preserve its own post-fork edits.
            WorktreeResult resumedChild = await WorktreeService.CreateOrResumeAsync(_repo, childId, CancellationToken.None);
            Assert.True(resumedChild.Success, resumedChild.Error);
            Assert.True(resumedChild.Resumed);
            Assert.Contains("child-only", await File.ReadAllTextAsync(Path.Combine(resumedChild.Path, "Program.cs")));
        }
        finally
        {
            if (child is { Success: true })
            {
                try { await WorktreeService.CleanupAsync(_repo, child.Path, child.Branch, deleteBranch: true, CancellationToken.None); }
                catch { }
            }
            if (parent is { Success: true })
            {
                try { await WorktreeService.CleanupAsync(_repo, parent.Path, parent.Branch, deleteBranch: true, CancellationToken.None); }
                catch { }
            }
        }
    }

    private void InitRepository()
    {
        RunGit(_repo, "init", "-b", "main");
        RunGit(_repo, "config", "user.email", "test@parux.local");
        RunGit(_repo, "config", "user.name", "PaRuX Acceptance");
        File.WriteAllText(Path.Combine(_repo, "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>\n");
        File.WriteAllText(Path.Combine(_repo, "Program.cs"),
            "public static class Demo\n{\n    public static string Message => \"before\";\n}\n");
        File.WriteAllBytes(Path.Combine(_repo, "asset.bin"), new byte[] { 1, 2, 3, 4 });
        RunGit(_repo, "add", ".");
        RunGit(_repo, "commit", "-m", "seed");
    }

    private static void RunGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("git " + string.Join(' ', args) + " failed: " + stdout + "\n" + stderr);
    }
}
