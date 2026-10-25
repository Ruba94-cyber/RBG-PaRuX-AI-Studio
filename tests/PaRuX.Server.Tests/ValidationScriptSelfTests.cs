using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Runs the Windows acceptance scripts as real PowerShell processes and pins their honesty: a required gate
/// that cannot run must read FAIL or NOT_RUN and exit non-zero, never exit 0. A PowerShell shim stands in for
/// dotnet or node only where the orchestration decision is the thing under test. Nothing here starts the WinUI
/// app, FlaUI, or a hardware lane.
/// </summary>
public sealed class ValidationScriptSelfTests
{
    private const string WindowsOnly = "The acceptance scripts are Windows-only; they did not run on this host.";
    private static readonly TimeSpan ProcessLimit = TimeSpan.FromMinutes(3);

    [WindowsFact(WindowsOnly)]
    public async Task Agent_platform_acceptance_without_node_records_browserDomJs_as_FAIL_and_exits_non_zero()
    {
        string root = NewScratchDirectory();
        try
        {
            string outputRoot = Path.Combine(root, "out");
            // dotnet stands in for the unit gate. node is deliberately absent from the child PATH.
            string shims = WriteShims(root, "dotnet");

            ScriptRun run = await RunScriptAsync("tools/test-agent-platform-v2.ps1",
                new[] { "-OutputRoot", outputRoot }, pathValue: ShimPath(shims));

            Assert.NotEqual(0, run.ExitCode);
            JsonElement status = ReadStatus(outputRoot);
            Assert.Equal("PASS", status.GetProperty("unit").GetString());
            Assert.Equal("FAIL", status.GetProperty("browserDomJs").GetString());
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [WindowsFact(WindowsOnly)]
    public async Task Agent_platform_acceptance_with_node_records_browserDomJs_as_PASS_and_exits_zero()
    {
        string root = NewScratchDirectory();
        try
        {
            string outputRoot = Path.Combine(root, "out");
            string shims = WriteShims(root, "dotnet", "node");

            ScriptRun run = await RunScriptAsync("tools/test-agent-platform-v2.ps1",
                new[] { "-OutputRoot", outputRoot }, pathValue: ShimPath(shims));

            Assert.Equal(0, run.ExitCode);
            JsonElement status = ReadStatus(outputRoot);
            Assert.Equal("PASS", status.GetProperty("unit").GetString());
            Assert.Equal("PASS", status.GetProperty("browserDomJs").GetString());
            // Hardware gates stay NOT_RUN by design and are reported as such, never as PASS.
            Assert.Equal("NOT_RUN", status.GetProperty("physicalGpuAndLongContext").GetString());
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [WindowsFact(WindowsOnly)]
    public async Task UI_HardwareE2E_suite_exits_non_zero_as_NOT_RUN_instead_of_reporting_success()
    {
        ScriptRun run = await RunScriptAsync("tools/test-ui.ps1", new[] { "-Suite", "HardwareE2E" });

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("NOT_RUN", run.Output, StringComparison.Ordinal);
    }

    [WindowsFact(WindowsOnly)]
    public async Task Release_gate_on_a_non_Windows_host_fails_instead_of_reporting_PASS_without_the_asset_harness()
    {
        ScriptRun run = await RunScriptAsync("tools/release-gate.ps1", Array.Empty<string>(),
            environment: new Dictionary<string, string> { ["PARUX_RELEASE_GATE_ASSUME_NON_WINDOWS"] = "1" });

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("cannot run on this host", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS - executable repository/software checks completed", run.Output, StringComparison.Ordinal);
    }

    private sealed record ScriptRun(int ExitCode, string Output);

    private static async Task<ScriptRun> RunScriptAsync(
        string relativeScript,
        IReadOnlyList<string> arguments,
        string? pathValue = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        string root = RepositoryRoot();
        string host = PowerShellHost();
        var startInfo = new ProcessStartInfo
        {
            FileName = host,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, relativeScript) }
                     .Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (pathValue != null) startInfo.Environment["PATH"] = pathValue;
        if (environment != null)
        {
            foreach (KeyValuePair<string, string> pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start " + host + ".");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(ProcessLimit);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(relativeScript + " did not finish within " + ProcessLimit.TotalMinutes + " minutes.");
        }

        return new ScriptRun(process.ExitCode, await stdout + Environment.NewLine + await stderr);
    }

    // The shim directory is searched first. System32 stays reachable so cmd.exe and the PowerShell runtime
    // resolve, but a real node or dotnet installed elsewhere on PATH is not visible to the script.
    private static string ShimPath(string shimDirectory) =>
        string.Join(Path.PathSeparator, new[]
        {
            shimDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        });

    // PowerShell shims rather than .cmd files. cmd.exe would parse the unquoted '|' in the unit filter as a
    // pipeline and report the shim itself as failed, which would be a property of cmd and not of the script.
    private static string WriteShims(string root, params string[] names)
    {
        string directory = Path.Combine(root, "shims");
        Directory.CreateDirectory(directory);
        foreach (string name in names)
            File.WriteAllText(Path.Combine(directory, name + ".ps1"), "exit 0\r\n");
        return directory;
    }

    private static JsonElement ReadStatus(string outputRoot)
    {
        string path = Path.Combine(outputRoot, "validation-status.json");
        Assert.True(File.Exists(path), "The acceptance script did not write its status report: " + path);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string PowerShellHost()
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory, "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }

        string windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(windowsPowerShell)) return windowsPowerShell;
        throw new InvalidOperationException("No PowerShell host was found, so the acceptance-script self-tests could not run.");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test process.");
    }

    private static string NewScratchDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-acceptance-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteQuietly(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
