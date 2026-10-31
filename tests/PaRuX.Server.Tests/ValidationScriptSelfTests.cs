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
            string shims = await WriteShimsAsync(root, "dotnet");

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
            string shims = await WriteShimsAsync(root, "dotnet", "node");

            ScriptRun run = await RunScriptAsync("tools/test-agent-platform-v2.ps1",
                new[] { "-OutputRoot", outputRoot }, pathValue: ShimPath(shims));

            Assert.Equal(0, run.ExitCode);
            JsonElement status = ReadStatus(outputRoot);
            Assert.Equal("PASS", status.GetProperty("unit").GetString());
            Assert.Equal("PASS", status.GetProperty("browserDomJs").GetString());
            Assert.Contains("NATIVE_SHIM_STDERR node.exe",
                File.ReadAllText(Path.Combine(outputRoot, "browser-dom-js.log")), StringComparison.Ordinal);
            // Hardware gates stay NOT_RUN by design and are reported as such, never as PASS.
            Assert.Equal("NOT_RUN", status.GetProperty("physicalGpuAndLongContext").GetString());
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [WindowsFact(WindowsOnly)]
    public async Task Agent_platform_acceptance_with_node_nonzero_exit_records_browserDomJs_as_FAIL()
    {
        string root = NewScratchDirectory();
        try
        {
            string outputRoot = Path.Combine(root, "out");
            string shims = await WriteShimsAsync(root, "dotnet", "node");

            ScriptRun run = await RunScriptAsync("tools/test-agent-platform-v2.ps1",
                new[] { "-OutputRoot", outputRoot }, pathValue: ShimPath(shims),
                environment: new Dictionary<string, string> { ["PARUX_VALIDATION_NODE_EXIT"] = "23" });

            Assert.NotEqual(0, run.ExitCode);
            JsonElement status = ReadStatus(outputRoot);
            Assert.Equal("PASS", status.GetProperty("unit").GetString());
            Assert.Equal("FAIL", status.GetProperty("browserDomJs").GetString());
            Assert.Contains("NATIVE_SHIM_STDERR node.exe",
                File.ReadAllText(Path.Combine(outputRoot, "browser-dom-js.log")), StringComparison.Ordinal);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [WindowsFact(WindowsOnly)]
    public async Task Agent_platform_native_process_capture_preserves_stderr_and_exit_codes()
    {
        string root = NewScratchDirectory();
        try
        {
            string outputRoot = Path.Combine(root, "out");
            ScriptRun run = await RunScriptAsync("tools/test-agent-platform-v2.ps1",
                new[] { "-OutputRoot", outputRoot, "-SelfTestNativeProcessCapture" });

            Assert.Equal(0, run.ExitCode);
            Assert.Contains("SENTINEL_STDOUT", run.Output, StringComparison.Ordinal);
            Assert.Contains("SENTINEL_STDERR", run.Output, StringComparison.Ordinal);
            Assert.Contains("exit 0, native stderr with exit 23", run.Output, StringComparison.Ordinal);
            string exceptionLog = Path.Combine(outputRoot, "agent-process-capture-powershell-exception.log");
            Assert.Contains("SENTINEL_PS_EXCEPTION", File.ReadAllText(exceptionLog), StringComparison.Ordinal);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [WindowsFact(WindowsOnly)]
    public async Task Agent_validation_syntax_gate_recompiles_changed_modal_source_in_reused_powershell_host()
    {
        string root = NewScratchDirectory();
        try
        {
            string repository = RepositoryRoot();
            string[] requiredScripts =
            {
                "tools/test-agent-validation-syntax.ps1",
                "tools/test-agent-platform-v2.ps1",
                "tools/test-agent-native-ui-e2e.ps1",
                "tools/test-agent-native-modal-e2e.ps1",
                "tools/test-agent-live-computer-use.ps1",
                "tools/test-winapp-functional.ps1",
                "tools/test-winapp-ui.ps1",
                "tools/test-ui.ps1"
            };
            foreach (string relative in requiredScripts)
            {
                string destination = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(repository, relative), destination, overwrite: true);
            }

            string harness = Path.Combine(root, "revalidate-modal-source.ps1");
            File.WriteAllText(harness, @"
$ErrorActionPreference = 'Stop'
$syntaxGate = $env:PARUX_SYNTAX_GATE_PATH
$modalSourcePath = $env:PARUX_MODAL_SOURCE_PATH
& $syntaxGate
$source = [IO.File]::ReadAllText($modalSourcePath)
$validMethod = 'return IsWindowEnabled(new IntPtr(hwnd));'
if (-not $source.Contains($validMethod)) { throw 'Could not locate the modal method in the isolated source copy.' }
[IO.File]::WriteAllText($modalSourcePath, $source.Replace($validMethod, 'return ???;'))
$compileFailure = $null
try { & $syntaxGate } catch { $compileFailure = $_.Exception.ToString() }
if ($null -eq $compileFailure) { throw 'The syntax gate accepted modified invalid modal C# after an earlier successful compile.' }
if ($compileFailure -notmatch 'return \?\?\?;') {
    throw ('The second syntax-gate run failed for an unexpected reason: ' + $compileFailure)
}
Write-Output 'PASS: second gate run compiled the current malformed modal source and rejected it.'
");

            ScriptRun run = await RunScriptAsync(harness, Array.Empty<string>(),
                environment: new Dictionary<string, string>
                {
                    ["PARUX_SYNTAX_GATE_PATH"] = Path.Combine(root, "tools", "test-agent-validation-syntax.ps1"),
                    ["PARUX_MODAL_SOURCE_PATH"] = Path.Combine(root, "tools", "test-agent-native-modal-e2e.ps1")
                },
                hostOverride: WindowsPowerShellHost());

            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("PASS: second gate run compiled the current malformed modal source and rejected it.",
                run.Output, StringComparison.Ordinal);
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
        IReadOnlyDictionary<string, string>? environment = null,
        string? hostOverride = null)
    {
        string root = RepositoryRoot();
        string host = hostOverride ?? PowerShellHost();
        string scriptPath = Path.IsPathRooted(relativeScript)
            ? relativeScript
            : Path.Combine(root, relativeScript);
        var startInfo = new ProcessStartInfo
        {
            FileName = host,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath }
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

    // The shim directory is searched first. Windows system paths remain available, but real node or dotnet
    // installations elsewhere on PATH are not visible to the acceptance script.
    private static string ShimPath(string shimDirectory) =>
        string.Join(Path.PathSeparator, new[]
        {
            shimDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        });

    // Build fixed native-process shims so ProcessStartInfo exercises the same executable path as production.
    // They ignore arguments, emit deterministic stdout/stderr sentinels, and only return a configurable code
    // for the node shim; no shell or user-provided command text is interpreted.
    private static async Task<string> WriteShimsAsync(string root, params string[] names)
    {
        string directory = Path.Combine(root, "shims");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(root, "NativeGateShim.cs");
        string appHost = Path.Combine(directory, "NativeGateShim.exe");
        File.WriteAllText(source,
            "using System;\r\nusing System.IO;\r\ninternal static class Program\r\n{\r\n    private static int Main()\r\n    {\r\n        string exe = Path.GetFileName(Environment.GetCommandLineArgs()[0]);\r\n        Console.Out.WriteLine(\"NATIVE_SHIM_STDOUT \" + exe);\r\n        Console.Error.WriteLine(\"NATIVE_SHIM_STDERR \" + exe);\r\n        int code;\r\n        return String.Equals(exe, \"node.exe\", StringComparison.OrdinalIgnoreCase) && Int32.TryParse(Environment.GetEnvironmentVariable(\"PARUX_VALIDATION_NODE_EXIT\"), out code) ? code : 0;\r\n    }\r\n}\r\n");

        string compiler = FrameworkCSharpCompiler();
        var buildInfo = new ProcessStartInfo
        {
            FileName = compiler,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "/nologo", "/target:exe", "/out:" + appHost, source })
            buildInfo.ArgumentList.Add(argument);

        using Process build = Process.Start(buildInfo)
            ?? throw new InvalidOperationException("Could not start the .NET Framework C# compiler to build the test-only native shim.");
        Task<string> stdout = build.StandardOutput.ReadToEndAsync();
        Task<string> stderr = build.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(ProcessLimit);
        try
        {
            await build.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { build.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("The test-only native shim build did not finish within " + ProcessLimit.TotalMinutes + " minutes.");
        }
        string buildOutput = await stdout + Environment.NewLine + await stderr;
        if (build.ExitCode != 0)
            throw new InvalidOperationException("Could not compile the test-only native shim: " + buildOutput);

        Assert.True(File.Exists(appHost), "The .NET Framework compiler did not produce the expected native shim executable.");
        foreach (string name in names)
        {
            if (name is not ("dotnet" or "node"))
                throw new ArgumentException("Only the fixed dotnet and node test shim names are supported.", nameof(names));
            File.Copy(appHost, Path.Combine(directory, name + ".exe"), overwrite: true);
        }
        return directory;
    }

    private static string FrameworkCSharpCompiler()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string frameworkRoot = Path.Combine(windows, "Microsoft.NET");
        string[] candidates = Environment.Is64BitOperatingSystem
            ? new[]
            {
                Path.Combine(frameworkRoot, "Framework64", "v4.0.30319", "csc.exe"),
                Path.Combine(frameworkRoot, "Framework", "v4.0.30319", "csc.exe")
            }
            : new[] { Path.Combine(frameworkRoot, "Framework", "v4.0.30319", "csc.exe") };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("The .NET Framework C# compiler was not found; required for the Windows acceptance test's native process shims.");
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

    private static string WindowsPowerShellHost()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException("Windows PowerShell was not found for the legacy Add-Type regression.");
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
