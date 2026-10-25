using System.Diagnostics;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Review finding: a code edit made through run_command (e.g. PowerShell Set-Content) was not
/// treated as a mutation, so "verified by command" survived a real code change.
/// </summary>
public sealed class TestableChangeFingerprintTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "parux-fingerprint-" + Guid.NewGuid().ToString("N"));

    public TestableChangeFingerprintTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init");
        Git("config", "user.name", "PaRuX Tests");
        Git("config", "user.email", "tests@parux.invalid");
        File.WriteAllText(Path.Combine(_repo, "Calc.cs"), "class Calc { }\n");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "readme\n");
        Git("add", ".");
        Git("commit", "-m", "baseline");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, true); } catch { }
    }

    private static bool IsTestable(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Fingerprint_changes_for_testable_edits_and_new_files_but_not_for_other_files()
    {
        string? clean = await CodingAgentTools.TestableChangeFingerprintAsync(_repo, IsTestable, CancellationToken.None);
        Assert.Equal(string.Empty, clean);

        File.WriteAllText(Path.Combine(_repo, "README.md"), "docs only\n");
        Assert.Equal(clean, await CodingAgentTools.TestableChangeFingerprintAsync(_repo, IsTestable, CancellationToken.None));

        File.WriteAllText(Path.Combine(_repo, "Calc.cs"), "class Calc { int X; }\n");
        string? edited = await CodingAgentTools.TestableChangeFingerprintAsync(_repo, IsTestable, CancellationToken.None);
        Assert.NotEqual(clean, edited);

        // A second edit of an already-modified file (same Git status) must still be detected.
        File.WriteAllText(Path.Combine(_repo, "Calc.cs"), "class Calc { int X; int Y; }\n");
        File.SetLastWriteTimeUtc(Path.Combine(_repo, "Calc.cs"), DateTime.UtcNow.AddSeconds(5));
        Assert.NotEqual(edited, await CodingAgentTools.TestableChangeFingerprintAsync(_repo, IsTestable, CancellationToken.None));

        File.WriteAllText(Path.Combine(_repo, "New.cs"), "class New { }\n");
        Assert.Contains("New.cs", await CodingAgentTools.TestableChangeFingerprintAsync(_repo, IsTestable, CancellationToken.None));
    }

    [Fact]
    public async Task Fingerprint_detects_same_size_content_change_when_timestamp_is_restored()
    {
        string file = Path.Combine(_repo, "Calc.cs");
        File.WriteAllText(file, "class Calc { int A; }\n");
        DateTime originalTimestamp = File.GetLastWriteTimeUtc(file);
        string? before = await CodingAgentTools.TestableChangeFingerprintAsync(_repo, IsTestable, CancellationToken.None);

        File.WriteAllText(file, "class Calc { int B; }\n");
        Assert.Equal("class Calc { int A; }\n".Length, File.ReadAllText(file).Length);
        File.SetLastWriteTimeUtc(file, originalTimestamp);
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(file));

        string? after = await CodingAgentTools.TestableChangeFingerprintAsync(_repo, IsTestable, CancellationToken.None);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Non_repository_root_has_no_fingerprint()
    {
        string plain = Path.Combine(Path.GetTempPath(), "parux-no-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plain);
        try
        {
            Assert.Null(await CodingAgentTools.TestableChangeFingerprintAsync(plain, IsTestable, CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(plain, true); } catch { }
        }
    }

    [Fact]
    public async Task Verifier_diff_includes_new_files_without_touching_the_index()
    {
        // Live parity run: a new result.json was invisible to the verifier ("no working-tree changes").
        File.WriteAllText(Path.Combine(_repo, "Calc.cs"), "class Calc { int Y; }\n");
        Directory.CreateDirectory(Path.Combine(_repo, "out"));
        File.WriteAllText(Path.Combine(_repo, "out", "result.json"), "{\"tokens\":[\"ORBIT-7\"]}\n");

        string diff = await CodingAgentTools.WorkingTreeDiffWithUntrackedAsync(_repo, CancellationToken.None);

        Assert.Contains("int Y;", diff, StringComparison.Ordinal);
        Assert.Contains("out/result.json", diff, StringComparison.Ordinal);
        Assert.Contains("ORBIT-7", diff, StringComparison.Ordinal);
        Assert.Contains("new file mode", diff, StringComparison.Ordinal);
        // Read-only: the new file is still untracked afterwards.
        var status = new ProcessStartInfo { FileName = "git", WorkingDirectory = _repo, UseShellExecute = false,
            RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (string arg in new[] { "status", "--porcelain=v1" }) status.ArgumentList.Add(arg);
        using Process process = Process.Start(status)!;
        string porcelain = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Contains("?? out/", porcelain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verifier_diff_bounds_new_file_count_and_size()
    {
        for (int i = 0; i < 3; i++) File.WriteAllText(Path.Combine(_repo, "new" + i + ".txt"), "x");
        File.WriteAllText(Path.Combine(_repo, "big.bin"), new string('b', 5000));

        string diff = await CodingAgentTools.WorkingTreeDiffWithUntrackedAsync(_repo, CancellationToken.None,
            maxUntrackedFiles: 2, maxUntrackedBytes: 100);

        Assert.Contains("more new file(s) not shown", diff, StringComparison.Ordinal);
    }

    private void Git(params string[] args)
    {
        var start = new ProcessStartInfo { FileName = "git", WorkingDirectory = _repo, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git " + string.Join(' ', args) + ": " + error);
    }
}
