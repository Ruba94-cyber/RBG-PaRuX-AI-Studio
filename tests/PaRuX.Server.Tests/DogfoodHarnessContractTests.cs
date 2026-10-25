using System.Diagnostics;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DogfoodHarnessContractTests
{
    [DogfoodPowerShellFact]
    public async Task Acceptance_script_selftest_exercises_the_harness_refusal_and_verdict_contracts()
    {
        string? powerShell = FindPowerShellOnPath();
        Assert.True(powerShell is not null, "PowerShell is not on PATH; the dogfood harness contract test requires pwsh or powershell.");

        string script = Path.Combine(RepositoryRoot(), "tools", "dogfood", "Test-DogfoodAcceptance.ps1");
        var startInfo = new ProcessStartInfo(powerShell!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryRoot()
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-SelfTest");

        using Process process = Process.Start(startInfo)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Assert.True(false, "The acceptance script self-test exceeded three minutes.");
        }

        string output = await stdout;
        string errors = await stderr;
        Assert.True(process.ExitCode == 0, "The acceptance script self-test failed. stdout: " + output + " stderr: " + errors);
        Assert.Contains("PASS", output);
    }

    internal static string? FindPowerShellOnPath()
    {
        string[] executableNames = ["pwsh.exe", "pwsh", "powershell.exe", "powershell"];
        string[] directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string name in executableNames)
        {
            foreach (string directory in directories)
            {
                string candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string RepositoryRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "tools", "dogfood", "Test-DogfoodAcceptance.ps1"))) return directory;
        }
        throw new DirectoryNotFoundException("The repository root with the dogfood acceptance script was not found.");
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class DogfoodPowerShellFactAttribute : FactAttribute
{
    public DogfoodPowerShellFactAttribute()
    {
        if (DogfoodHarnessContractTests.FindPowerShellOnPath() is null)
        {
            Skip = "Neither pwsh nor powershell was found on PATH; the dogfood acceptance self-test did not run.";
        }
    }
}
