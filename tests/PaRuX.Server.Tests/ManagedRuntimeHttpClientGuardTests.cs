using System.Diagnostics;
using System.Text.RegularExpressions;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source HEURISTIC (a line-based text scan, not a proof): any HttpClient created in a file that talks to the managed runtime endpoint must
/// use the attested handler, so a new prompt-carrying path cannot silently bypass connected-peer
/// attestation. Exceptions are explicit and justified.
/// </summary>
public sealed class ManagedRuntimeHttpClientGuardTests
{
    private static readonly string[] RuntimeTokens =
    {
        "ProviderEndpoint", "TextEndpoint", "TextPort", "CurrentManagedProviderEndpoint", "ManagedProviderEndpoint",
    };

    // file (relative to src/PaRuX.Server) + creation-line fragment -> why it is exempt.
    private static readonly (string File, string Fragment, string Why)[] Allowlist =
    {
        ("LocalAiProcessManager.cs", "private static readonly HttpClient Http = new()",
            "Health/system_stats GETs only (no prompt content); also shared with the ComfyUI media runtime, which has no recorded launch identity."),
        ("TextInferenceQuiescence.cs", "private static readonly HttpClient Http = new()",
            "Prompt-free /slots and /v1/models probes, including llama.cpp router child ports owned by descendants of the launched runtime."),
    };

    private static string ServerSourceRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "PaRuX.Server");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/PaRuX.Server not found above the test output directory.");
    }

    private static readonly Regex Creation = new(
        @"new\s+HttpClient\s*(\(|\{|$)"
        + @"|HttpClient\??\s+\w+\s*(\{[^}]*\}\s*)?=\s*new\s*(\(|\{|$)",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("var c = new HttpClient();", true)]
    [InlineData("var c = new HttpClient { Timeout = T };", true)]
    [InlineData("var c = new HttpClient", true)]
    [InlineData("private static readonly HttpClient Http = new() { Timeout = T };", true)]
    [InlineData("private static HttpClient Http { get; } = new(handler);", true)]
    [InlineData("public HttpClient Client { get; set; } = new();", true)]
    [InlineData("private HttpClient? _http = new();", true)]
    [InlineData("HttpClient http = await factory.CreateAsync();", false)]
    [InlineData("private readonly HttpClient _http;", false)]
    public void The_creation_pattern_recognises_every_construction_form(string line, bool expected) =>
        Assert.Equal(expected, Creation.IsMatch(line));

    [Fact]
    public void Every_http_client_in_a_runtime_endpoint_file_uses_the_attested_handler_or_is_allowlisted()
    {
        string root = ServerSourceRoot();
        var violations = new List<string>();
        int inspected = 0;

        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string sep = Path.DirectorySeparatorChar.ToString();
            if (path.Contains(sep + "obj" + sep) || path.Contains(sep + "bin" + sep)) continue;
            string text = File.ReadAllText(path);
            if (!RuntimeTokens.Any(token => text.Contains(token, StringComparison.Ordinal))) continue;

            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!Creation.IsMatch(lines[i])) continue;
                inspected++;
                if (lines[i].Contains("ManagedRuntimePeers", StringComparison.Ordinal)
                    || lines[i].Contains("AttestedLoopbackHttp", StringComparison.Ordinal)) continue;
                if (Allowlist.Any(a => a.File == relative && lines[i].Contains(a.Fragment, StringComparison.Ordinal))) continue;
                violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(inspected >= 8, "guard did not find the expected HttpClient creations (" + inspected + ")");
        Assert.True(violations.Count == 0,
            "HttpClient used in a managed-runtime file without the attested handler:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Retrieval_server_pins_its_launched_identity_for_every_connection()
    {
        string text = File.ReadAllText(Path.Combine(ServerSourceRoot(), "LlamaCppRetrievalProviders.cs"));
        Assert.Contains("TryCaptureIdentity(process.Id)", text);
        Assert.Contains("AttestedLoopbackHttp.CreateHandler", text);
        Assert.DoesNotContain("new HttpClient {", text);
    }

    // ---- StopText never kills an unverified PID-file process

    private static string CopyLlamaStandIn(string directory)
    {
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "llama-server.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), target, overwrite: true);
        return target;
    }

    private static Process StartStandIn(string exe)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string arg in new[] { "-n", "120", "127.0.0.1" }) psi.ArgumentList.Add(arg);
        return Process.Start(psi)!;
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "parux-stop-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StopText_does_not_kill_a_pid_file_process_that_is_not_a_verified_parux_runtime()
    {
        if (!OperatingSystem.IsWindows()) return;
        string state = TempDir(), root = TempDir(), foreign = TempDir();
        using Process stranger = StartStandIn(CopyLlamaStandIn(foreign)); // named llama-server, outside the runtime root
        try
        {
            using IDisposable s = LocalAiPaths.UseStateDirForTests(state);
            using IDisposable r = LocalAiPaths.UseRootForTests(root);
            LocalAiProcessManager.ClearTextLaunchIdentity();
            Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.TextPidFile)!);
            File.WriteAllText(LocalAiPaths.TextPidFile, stranger.Id.ToString());

            Assert.True(new LocalAiProcessManager().StopText());

            Assert.False(stranger.HasExited, "an unverified llama-server must never be killed");
            Assert.False(File.Exists(LocalAiPaths.TextPidFile), "the stale record is dropped");
        }
        finally
        {
            try { if (!stranger.HasExited) stranger.Kill(true); } catch { }
            foreach (string d in new[] { state, root, foreign }) { try { Directory.Delete(d, true); } catch { } }
        }
    }

    [Fact]
    public void Final_path_resolution_sees_through_a_junction_and_fails_safe_for_a_missing_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        string real = TempDir(), link = TempDir();
        Directory.CreateDirectory(real);
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "/c", "mklink", "/J", link, real }) psi.ArgumentList.Add(arg);
            using (Process mk = Process.Start(psi)!) { mk.WaitForExit(10000); }
            if (!Directory.Exists(link)) return; // junction creation unavailable on this host

            string? viaLink = LocalAiProcessManager.ResolveFinalPath(link);
            string? direct = LocalAiProcessManager.ResolveFinalPath(real);
            Assert.NotNull(direct);
            Assert.Equal(direct, viaLink, ignoreCase: true);
            Assert.Null(LocalAiProcessManager.ResolveFinalPath(Path.Combine(real, "does-not-exist")));
        }
        finally
        {
            try { Directory.Delete(link); } catch { }   // removes the junction, not its target
            try { Directory.Delete(real, true); } catch { }
        }
    }

    [Fact]
    public void StopText_kills_the_verified_recorded_launch_and_a_runtime_under_the_runtime_root()
    {
        if (!OperatingSystem.IsWindows()) return;
        string state = TempDir(), root = TempDir(), outside = TempDir();
        try
        {
            using IDisposable s = LocalAiPaths.UseStateDirForTests(state);
            using IDisposable r = LocalAiPaths.UseRootForTests(root);
            Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.TextPidFile)!);

            // Verified recorded launch, executable outside the root.
            using (Process recorded = StartStandIn(CopyLlamaStandIn(outside)))
            {
                File.WriteAllText(LocalAiPaths.TextPidFile, recorded.Id.ToString());
                LocalAiProcessManager.RecordTextLaunchIdentity(recorded.Id);
                Assert.True(new LocalAiProcessManager().StopText());
                Assert.True(recorded.WaitForExit(5000), "the verified launch is stopped");
            }

            // Survivor of an earlier session: no record, but its image is PaRuX's own runtime.
            LocalAiProcessManager.ClearTextLaunchIdentity();
            using (Process survivor = StartStandIn(CopyLlamaStandIn(Path.Combine(LocalAiPaths.RuntimeRoot, "text-test"))))
            {
                File.WriteAllText(LocalAiPaths.TextPidFile, survivor.Id.ToString());
                Assert.True(new LocalAiProcessManager().StopText());
                Assert.True(survivor.WaitForExit(5000), "a PaRuX-installed runtime is stopped");
            }
        }
        finally
        {
            LocalAiProcessManager.ClearTextLaunchIdentity();
            foreach (string d in new[] { state, root, outside }) { try { Directory.Delete(d, true); } catch { } }
        }
    }
}
