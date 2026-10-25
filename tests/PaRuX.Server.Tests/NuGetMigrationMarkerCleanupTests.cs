using System.Diagnostics;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Live runs: sandboxed dotnet left an empty "NuGet/Migrations/1" in the task worktree (at the
/// root and in project directories), which failed scope audits and was applied to user repos.
/// </summary>
public sealed class NuGetMigrationMarkerCleanupTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "parux-nuget-marker-" + Guid.NewGuid().ToString("N"));

    public NuGetMigrationMarkerCleanupTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init");
        Git("config", "user.name", "PaRuX Tests");
        Git("config", "user.email", "tests@parux.invalid");
        Directory.CreateDirectory(Path.Combine(_repo, "docs", "NuGet", "Migrations"));
        File.WriteAllText(Path.Combine(_repo, "docs", "NuGet", "Migrations", "7"), "");
        File.WriteAllText(Path.Combine(_repo, "A.cs"), "class A { }\n");
        Git("add", ".");
        Git("commit", "-m", "baseline");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, true); } catch { }
    }

    [Fact]
    public async Task Only_untracked_empty_migration_markers_are_removed()
    {
        string rootMarker = Path.Combine(_repo, "NuGet", "Migrations", "1");
        string projectMarker = Path.Combine(_repo, "src", "App", "NuGet", "Migrations", "1");
        string userData = Path.Combine(_repo, "data", "NuGet", "Migrations", "2");
        foreach (string file in new[] { rootMarker, projectMarker, userData })
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(rootMarker, "");
        File.WriteAllText(projectMarker, "");
        File.WriteAllText(userData, "not a marker");
        File.WriteAllText(Path.Combine(_repo, "src", "App", "B.cs"), "class B { }\n");

        int removed = await CodingAgentTools.RemoveNuGetMigrationMarkersAsync(_repo, CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(Path.Combine(_repo, "NuGet")));
        Assert.False(Directory.Exists(Path.Combine(_repo, "src", "App", "NuGet")));
        Assert.True(File.Exists(Path.Combine(_repo, "src", "App", "B.cs")));
        Assert.True(File.Exists(userData));
        Assert.True(File.Exists(Path.Combine(_repo, "docs", "NuGet", "Migrations", "7"))); // tracked
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
