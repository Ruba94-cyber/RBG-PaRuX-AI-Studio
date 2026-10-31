using System.Diagnostics;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PaRuXCliAgentHelpTests
{
    [Fact]
    public async Task Agent_help_flags_print_guidance_without_creating_task_state_or_contacting_core()
    {
        string repositoryRoot = FindRepositoryRoot();
        string isolatedDataRoot = Path.Combine(Path.GetTempPath(), "parux-cli-help-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedDataRoot);

        try
        {
            foreach (string[] arguments in new[]
            {
                new[] { "agent", "--help" },
                new[] { "agent", "-h" },
                new[] { "agent", "help" },
                new[] { "agent", "run", "--help" },
                new[] { "agent", "run", "-h" }
            })
            {
                (int exitCode, string stdout, string stderr) = await RunCliAsync(repositoryRoot, isolatedDataRoot, arguments);
                Assert.Equal(0, exitCode);
                Assert.Contains("parux agent - create and manage", stdout, StringComparison.Ordinal);
                Assert.DoesNotContain("PaRuX Core isn't running", stderr, StringComparison.Ordinal);
                Assert.Empty(Directory.EnumerateFileSystemEntries(isolatedDataRoot));
            }
        }
        finally
        {
            Directory.Delete(isolatedDataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Help_text_inside_an_agent_goal_is_preserved_as_goal_text()
    {
        string repositoryRoot = FindRepositoryRoot();
        string isolatedDataRoot = Path.Combine(Path.GetTempPath(), "parux-cli-goal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedDataRoot);

        try
        {
            (int exitCode, string stdout, string stderr) = await RunCliAsync(
                repositoryRoot,
                isolatedDataRoot,
                "agent", "run", "implement feature", "--help", "documentation");

            Assert.Equal(1, exitCode);
            Assert.DoesNotContain("parux agent - create and manage", stdout, StringComparison.Ordinal);
            Assert.Contains("PaRuX Core isn't running", stderr, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(isolatedDataRoot));
        }
        finally
        {
            Directory.Delete(isolatedDataRoot, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string repositoryRoot,
        string dataRoot,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--no-restore");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "src", "PaRuX.Cli", "PaRuX.Cli.csproj"));
        process.StartInfo.ArgumentList.Add("--");
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["PARUX_DATA_ROOT"] = dataRoot;

        Assert.True(process.Start(), "Could not start the PaRuX CLI process.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PaRuX.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test output directory.");
    }
}
