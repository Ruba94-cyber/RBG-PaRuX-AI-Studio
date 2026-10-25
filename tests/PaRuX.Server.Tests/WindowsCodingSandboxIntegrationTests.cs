using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// These tests deliberately become real integration tests on the Windows development/release host.
/// They are no-ops on non-Windows hosts because AppContainer is a Windows security primitive. The
/// local software-complete gate runs this project on Windows, so P/Invoke/ACL/environment failures
/// cannot be turned into a documentation-only pass.
/// </summary>
public sealed class WindowsCodingSandboxIntegrationTests : IDisposable
{
    // Deliberately NOT under Path.GetTempPath(): granting AppContainer access walks every
    // ancestor directory, and confirmed live on this host, a fresh, single, non-concurrent
    // ACL updates against %TEMP% or %LOCALAPPDATA% (heavily watched by AV/indexer/sync clients)
    // can block for 90+ seconds even without contention. Keep both the selected workspace and
    // PaRuX's test-only sandbox state under this fresh user-profile directory so the real tests
    // never mutate shared temp/app-data ancestors or caches.
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".parux-sandbox-tests",
        "it-" + Guid.NewGuid().ToString("N"));

    private readonly string _previousDataRoot;

    public WindowsCodingSandboxIntegrationTests()
    {
        Directory.CreateDirectory(_root);
        _previousDataRoot = PaRuXDataRoot.Path;
        PaRuXDataRoot.UseForTests(Path.Combine(_root, "parux-data"));
    }

    [Fact]
    public async Task Real_no_network_appcontainer_executes_in_workspace_and_scrubs_parent_secret()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string secretName = "PARUX_SANDBOX_TEST_SECRET_TOKEN";
        const string secretValue = "must-not-cross-appcontainer-boundary";
        string? previous = Environment.GetEnvironmentVariable(secretName);
        try
        {
            Environment.SetEnvironmentVariable(secretName, secretValue);
            WindowsCodingSandbox.Result result = await WindowsCodingSandbox.RunAsync(
                _root,
                "echo sandbox-ok>probe.txt & echo %" + secretName + "%",
                CancellationToken.None,
                timeout: TimeSpan.FromSeconds(30),
                networkMode: WindowsCodingSandbox.NetworkMode.None);

            Assert.Null(result.Error);
            Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Output}");
            Assert.True(File.Exists(Path.Combine(_root, "probe.txt")));
            Assert.Equal("sandbox-ok", File.ReadAllText(Path.Combine(_root, "probe.txt")).Trim());
            Assert.DoesNotContain(secretValue, result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, previous);
        }
    }

    [Fact]
    public async Task Bare_program_names_do_not_resolve_from_the_agent_writable_workspace()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Review P1: an agent-written dotnet.cmd in the working directory stood in for the real tool.
        File.WriteAllText(Path.Combine(_root, "parux-probe-tool.cmd"), "@echo HIJACKED\r\n@exit /b 0\r\n");
        WindowsCodingSandbox.Result bare = await WindowsCodingSandbox.RunAsync(
            _root, "parux-probe-tool", CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30), networkMode: WindowsCodingSandbox.NetworkMode.None);
        WindowsCodingSandbox.Result explicitPath = await WindowsCodingSandbox.RunAsync(
            _root, ".\\parux-probe-tool.cmd", CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30), networkMode: WindowsCodingSandbox.NetworkMode.None);

        Assert.DoesNotContain("HIJACKED", bare.Output, StringComparison.Ordinal);
        Assert.NotEqual(0, bare.ExitCode);
        Assert.True(explicitPath.ExitCode == 0, $"Exit {explicitPath.ExitCode}: {explicitPath.Output} {explicitPath.Error}");
        Assert.Contains("HIJACKED", explicitPath.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Node_es_module_verifier_runs_without_drive_root_access()
    {
        if (!OperatingSystem.IsWindows()) return;
        // PaRuX-managed Node (readable by app packages); skip where it is not provisioned.
        string node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaRuX", "tooling", "node", "node.exe");
        if (!File.Exists(node)) return;

        // Live parity run: "EPERM: lstat 'C:\'" from Node's real-path walk failed every Node verifier.
        Directory.CreateDirectory(Path.Combine(_root, "web"));
        File.WriteAllText(Path.Combine(_root, "web", "app.mjs"), "export const answer = () => 42;\n");
        File.WriteAllText(Path.Combine(_root, "web", "verify.mjs"),
            "import { answer } from './app.mjs';\nif (answer() !== 42) process.exit(3);\nconsole.log('ESM-OK');\n");
        WindowsCodingSandbox.Result result = await WindowsCodingSandbox.RunAsync(
            _root, "\"" + node + "\" web/verify.mjs", CancellationToken.None,
            timeout: TimeSpan.FromSeconds(120), networkMode: WindowsCodingSandbox.NetworkMode.None);

        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Output} {result.Error}");
        Assert.Contains("ESM-OK", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Npm_test_runs_the_package_script_inside_the_offline_sandbox()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Live UI check: `npm test` hung for 8+ minutes; the script itself must run and report its exit code.
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"name\":\"d\",\"private\":true,\"scripts\":{\"pretest\":\"echo PRE-RAN\",\"test\":\"echo SCRIPT-RAN %npm_lifecycle_event% && exit /b 3\"}}");
        // The run timeout covers the process only (first-use ACL setup is excluded), so a hang fails here.
        WindowsCodingSandbox.Result result = await WindowsCodingSandbox.RunAsync(
            _root, "npm test", CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30), networkMode: WindowsCodingSandbox.NetworkMode.None);

        Assert.False(result.TimedOut, result.Output);
        Assert.Contains("PRE-RAN", result.Output, StringComparison.Ordinal);
        Assert.Contains("SCRIPT-RAN test", result.Output, StringComparison.Ordinal);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task Npm_test_with_node_test_runner_completes_inside_the_offline_sandbox()
    {
        if (!OperatingSystem.IsWindows()) return;
        string managedNode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaRuX", "tooling", "node");
        if (!File.Exists(Path.Combine(managedNode, "node.exe"))) return;

        // Live UI check: `npm test` (node --test) hung for 10 minutes per attempt in the Chat demo.
        // Provision the PaRuX-managed Node into this test's data root exactly like an installed PaRuX.
        string tooling = Path.Combine(PaRuXDataRoot.Path, "tooling");
        Directory.CreateDirectory(tooling);
        using (var link = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                   "/c mklink /J \"" + Path.Combine(tooling, "node") + "\" \"" + managedNode + "\"") { CreateNoWindow = true, UseShellExecute = false })!)
            link.WaitForExit();
        Directory.CreateDirectory(Path.Combine(_root, "test"));
        File.WriteAllText(Path.Combine(_root, "package.json"), "{\"name\":\"d\",\"private\":true,\"type\":\"module\",\"scripts\":{\"test\":\"node --test\"}}");
        File.WriteAllText(Path.Combine(_root, "test", "a.test.mjs"),
            "import test from 'node:test';\nimport assert from 'node:assert/strict';\ntest('adds', () => assert.equal(1 + 1, 2));\n");

        WindowsCodingSandbox.Result result = await WindowsCodingSandbox.RunAsync(
            _root, "npm test", CancellationToken.None,
            timeout: TimeSpan.FromSeconds(60), networkMode: WindowsCodingSandbox.NetworkMode.None);

        Assert.False(result.TimedOut, result.Output);
        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Output} {result.Error}");
        Assert.Contains("pass 1", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Controlled_restore_identity_seeds_state_visible_to_later_offline_identity()
    {
        if (!OperatingSystem.IsWindows()) return;

        WindowsCodingSandbox.Result restoreIdentity = await WindowsCodingSandbox.RunAsync(
            _root,
            "echo seeded-by-controlled-restore>\"%USERPROFILE%\\restore-seed.txt\"",
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30),
            networkMode: WindowsCodingSandbox.NetworkMode.InternetClient);

        Assert.Null(restoreIdentity.Error);
        Assert.True(restoreIdentity.ExitCode == 0, $"Exit {restoreIdentity.ExitCode}: {restoreIdentity.Output}");

        WindowsCodingSandbox.Result offlineIdentity = await WindowsCodingSandbox.RunAsync(
            _root,
            "type \"%USERPROFILE%\\restore-seed.txt\"",
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30),
            networkMode: WindowsCodingSandbox.NetworkMode.None);

        Assert.Null(offlineIdentity.Error);
        Assert.True(offlineIdentity.ExitCode == 0, $"Exit {offlineIdentity.ExitCode}: {offlineIdentity.Output}");
        Assert.Contains("seeded-by-controlled-restore", offlineIdentity.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Suspended_child_is_terminated_when_job_assignment_fails_before_resume()
    {
        if (!OperatingSystem.IsWindows()) return;

        // CF-04: the child is created suspended. A failed Job Object assignment used to leave it suspended for good.
        uint? childProcessId = null;
        using (WindowsCodingSandbox.UseFaultInjectionForTests(new WindowsCodingSandbox.SandboxFaultInjection
               {
                   FailJobAssignment = true,
                   OnProcessCreated = processId => childProcessId = processId
               }))
        {
            WindowsCodingSandbox.Result result = await WindowsCodingSandbox.RunAsync(
                _root,
                "echo should-not-run>probe.txt",
                CancellationToken.None,
                timeout: TimeSpan.FromSeconds(30),
                networkMode: WindowsCodingSandbox.NetworkMode.None);

            Assert.NotNull(result.Error);
        }

        Assert.NotNull(childProcessId);
        Assert.False(File.Exists(Path.Combine(_root, "probe.txt")));
        try
        {
            using var child = System.Diagnostics.Process.GetProcessById((int)childProcessId!.Value);
            Assert.True(child.WaitForExit(10_000), "The suspended sandbox child is still running.");
        }
        catch (ArgumentException)
        {
            // The process is already gone, which is the outcome the fix guarantees.
        }
    }

    [Fact]
    public async Task Output_past_the_hard_cap_stops_the_sandboxed_command_and_returns_a_bounded_result()
    {
        if (!OperatingSystem.IsWindows()) return;

        // CF-03: 80 MiB of output is more than the 64 MiB cap. The command must be stopped while it is still writing,
        // and the caller must get a bounded, explicitly truncated result rather than the whole log.
        string bigFile = Path.Combine(_root, "big-output.bin");
        try
        {
            using (FileStream big = File.Create(bigFile))
                big.SetLength(80L * 1024 * 1024);

            WindowsCodingSandbox.Result result = await WindowsCodingSandbox.RunAsync(
                _root,
                "type big-output.bin",
                CancellationToken.None,
                timeout: TimeSpan.FromMinutes(2),
                networkMode: WindowsCodingSandbox.NetworkMode.None);

            Assert.True(result.OutputTruncated, result.Output);
            Assert.False(result.TimedOut);
            Assert.Null(result.Error);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("passed the 64 MiB cap", result.Output, StringComparison.Ordinal);
            Assert.True(result.Output.Length < 8 * 1024 * 1024, "Returned " + result.Output.Length + " characters.");
        }
        finally
        {
            // The 80 MiB probe must not outlive the test even when the per-test ACL cleanup is skipped.
            try { File.Delete(bigFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        // Each test uses a fresh isolated data/workspace root. Remove only its per-profile ACEs;
        // DeleteProfileForTests bounds cleanup because Windows ACL persistence is non-cancellable.
        bool cleanupComplete = true;
        try
        {
            cleanupComplete &= WindowsCodingSandbox.DeleteProfileForTests(_root, WindowsCodingSandbox.NetworkMode.None);
            cleanupComplete &= WindowsCodingSandbox.DeleteProfileForTests(_root, WindowsCodingSandbox.NetworkMode.InternetClient);
            if (cleanupComplete && Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { }
        finally
        {
            PaRuXDataRoot.UseForTests(_previousDataRoot);
        }
    }
}
