using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingWorkspaceVerificationFingerprintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-state-fingerprint-" + Guid.NewGuid().ToString("N"));

    private static void Git(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repository,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.fsmonitor=false");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15000) && process.ExitCode == 0,
            string.Join(" ", arguments) + ": " + stdout + stderr);
    }

    private static string CreateRepository(string path)
    {
        Directory.CreateDirectory(path);
        Git(path, "init", "-q");
        File.WriteAllText(Path.Combine(path, "program.cs"), "public class Program {}");
        Git(path, "add", "--", "program.cs");
        Git(path, "-c", "user.name=PaRuX Test",
            "-c", "user.email=test@invalid.test",
            "-c", "commit.gpgsign=false", "commit", "-q", "-m", "baseline");
        return path;
    }

    [Fact]
    public async Task Single_git_receipt_changes_after_edit_and_even_after_committing_that_edit()
    {
        string root = CreateRepository(Path.Combine(_root, "primary"));
        string? baseline = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            root, CancellationToken.None);
        Assert.NotNull(baseline);
        Assert.Matches("^[0-9a-f]{64}$", baseline!);
        File.WriteAllText(Path.Combine(root, "program.cs"), "public class Program { public int Value = 7; }");
        string? edited = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            root, CancellationToken.None);
        Assert.NotNull(edited);
        Assert.NotEqual(baseline, edited);
        Git(root, "add", "--", "program.cs");
        Git(root, "-c", "user.name=PaRuX Test",
            "-c", "user.email=test@invalid.test", "-c", "commit.gpgsign=false",
            "commit", "-q", "-m", "edited");
        string? committed = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            root, CancellationToken.None);
        Assert.NotNull(committed);
        Assert.NotEqual(baseline, committed); // HEAD changed despite clean diff
        Assert.NotEqual(edited, committed);
    }

    [Fact]
    public async Task Aggregate_digest_includes_every_git_worktree_and_rejects_unknown_child()
    {
        Directory.CreateDirectory(_root);
        string backend = CreateRepository(Path.Combine(_root, "backend"));
        string frontend = CreateRepository(Path.Combine(_root, "frontend"));
        string? baseline = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            _root, CancellationToken.None);
        Assert.NotNull(baseline);

        File.WriteAllText(Path.Combine(frontend, "program.cs"), "public class Frontend { }");
        string? frontendChanged = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            _root, CancellationToken.None);
        Assert.NotNull(frontendChanged);
        Assert.NotEqual(baseline, frontendChanged);

        Directory.CreateDirectory(Path.Combine(_root, "unauthorized"));
        Assert.Null(await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            _root, CancellationToken.None));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("diff --git a/src/main.cs b/src/main.cs\n+fixed", true)]
    [InlineData("new file omitted: link or junction", false)]
    [InlineData("new file omitted: 2020001 bytes exceeds the diff budget", false)]
    [InlineData("… (201 more new file(s) not shown)", false)]
    public void Git_state_fingerprint_fails_closed_when_a_diff_is_truncated_or_linked(
        string diff, bool expected)
    {
        string? hash = CodingWorkspaceVerificationFingerprint.FingerprintDiff(diff);
        Assert.Equal(expected, hash != null);
        if (expected) Assert.Matches("^[0-9a-f]{64}$", hash!);
    }

    [Fact]
    public void Different_current_file_edits_cannot_reuse_the_previous_command_fingerprint()
    {
        const string previous = "diff --git a/src/main.cs b/src/main.cs\n+old";
        const string after = "diff --git a/src/main.cs b/src/main.cs\n+new";
        string? first = CodingWorkspaceVerificationFingerprint.FingerprintDiff(previous);
        string? second = CodingWorkspaceVerificationFingerprint.FingerprintDiff(after);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(first, CodingWorkspaceVerificationFingerprint.FingerprintDiff(previous));
        Assert.Null(CodingWorkspaceVerificationFingerprint.FingerprintDiff(
            new string('x', 4_000_001)));
        Assert.Null(CodingWorkspaceVerificationFingerprint.FingerprintDiff(null));
    }

    [Fact]
    public async Task Missing_workspace_never_yields_a_fictitious_verified_fingerprint()
    {
        string path = Path.Combine(Path.GetTempPath(), "parux-missing-workspace-"
            + Guid.NewGuid().ToString("N"));
        Assert.Null(await CodingWorkspaceVerificationFingerprint.CaptureAsync(
            path, CancellationToken.None));
    }
}
