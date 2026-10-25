using System.Text;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WindowsCodingSandboxPolicyTests
{
    [Fact]
    public async Task Overlong_sandbox_state_path_fails_fast_with_an_explanation()
    {
        if (!OperatingSystem.IsWindows()) return;

        string workspace = Path.Combine(Path.GetTempPath(), "parux-longstate-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string previous = PaRuX.Contracts.PaRuXDataRoot.Path;
        try
        {
            PaRuX.Contracts.PaRuXDataRoot.UseForTests(Path.Combine(Path.GetTempPath(), new string('d', 150)));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            WindowsCodingSandbox.Result result = await WindowsCodingSandbox.RunAsync(
                workspace, "echo never-runs", CancellationToken.None,
                timeout: TimeSpan.FromSeconds(30), networkMode: WindowsCodingSandbox.NetworkMode.None);

            Assert.Equal(-1, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.Contains("PARUX_DATA_ROOT", result.Error, StringComparison.Ordinal);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "The guard must reject before any setup or launch.");
        }
        finally
        {
            PaRuX.Contracts.PaRuXDataRoot.UseForTests(previous);
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Host_interpreter_directories_are_resolved_from_path_but_never_windowsapps_aliases()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-tooldirs-" + Guid.NewGuid().ToString("N"));
        string alias = Path.Combine(root, "Microsoft", "WindowsApps");
        string python = Path.Combine(root, "Programs", "Python", "Python313");
        Directory.CreateDirectory(alias);
        Directory.CreateDirectory(python);
        File.WriteAllText(Path.Combine(alias, "python.exe"), string.Empty);
        File.WriteAllText(Path.Combine(python, "python.exe"), string.Empty);
        // A folder that only happens to hold python.exe is not an interpreter install (review finding).
        string loose = Path.Combine(root, "tools");
        Directory.CreateDirectory(loose);
        File.WriteAllText(Path.Combine(loose, "python.exe"), string.Empty);
        try
        {
            Assert.Empty(WindowsCodingSandbox.ResolveHostToolDirectories(new[] { "python.exe" }, python + ";" + loose));

            Directory.CreateDirectory(Path.Combine(python, "Lib"));
            File.WriteAllText(Path.Combine(python, "python313.dll"), string.Empty);
            IReadOnlyList<string> resolved = WindowsCodingSandbox.ResolveHostToolDirectories(
                new[] { "python.exe", "node.exe" }, alias + ";" + python + "\\;.;relative;" + loose);

            Assert.Equal(new[] { python }, resolved);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Node_skips_drive_root_realpath_walk_except_in_pnpm_workspaces()
    {
        Assert.Equal("--preserve-symlinks --preserve-symlinks-main", WindowsCodingSandbox.NodeOptionsForSandbox(null, null));
        Assert.Equal("--max-old-space-size=4096 --preserve-symlinks --preserve-symlinks-main",
            WindowsCodingSandbox.NodeOptionsForSandbox("--max-old-space-size=4096 --preserve-symlinks", null));

        string root = Path.Combine(Path.GetTempPath(), "parux-pnpm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", ".pnpm"));
        try { Assert.Null(WindowsCodingSandbox.NodeOptionsForSandbox(null, root)); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Existing_equivalent_ace_is_recognised_so_restart_skips_icacls_propagation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-ace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const string appPackages = "S-1-15-2-1"; // ALL APPLICATION PACKAGES
        try
        {
            Assert.False(WindowsCodingSandbox.HasEquivalentAllowRule(dir, appPackages, "(OI)(CI)(RX)"));
            var info = new DirectoryInfo(dir);
            var security = info.GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(appPackages),
                System.Security.AccessControl.FileSystemRights.ReadAndExecute,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(security);

            Assert.True(WindowsCodingSandbox.HasEquivalentAllowRule(dir, appPackages, "(OI)(CI)(RX)"));
            Assert.True(WindowsCodingSandbox.HasEquivalentAllowRule(dir, appPackages, "(RX)"));
            Assert.False(WindowsCodingSandbox.HasEquivalentAllowRule(dir, appPackages, "(OI)(CI)(M)"));
            Assert.False(WindowsCodingSandbox.HasEquivalentAllowRule(dir, "S-1-15-2-2", "(RX)"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData("dotnet test -m:1", WindowsCodingSandbox.ToolingEcosystem.DotNet)]
    [InlineData("node --test", WindowsCodingSandbox.ToolingEcosystem.Node)]
    [InlineData("python verify.py x", WindowsCodingSandbox.ToolingEcosystem.Python)]
    [InlineData(".\\gradlew.bat --offline test", WindowsCodingSandbox.ToolingEcosystem.Java)]
    [InlineData("cargo test && go test ./...", WindowsCodingSandbox.ToolingEcosystem.Rust | WindowsCodingSandbox.ToolingEcosystem.Go)]
    [InlineData("\"C:\\tools\\node\\node.exe\" web/verify.mjs", WindowsCodingSandbox.ToolingEcosystem.Node)]
    [InlineData("type result.json", WindowsCodingSandbox.ToolingEcosystem.None)]
    [InlineData("make all", WindowsCodingSandbox.ToolingEcosystem.All)]
    public void Tooling_grants_follow_the_programs_a_command_starts(string command, WindowsCodingSandbox.ToolingEcosystem expected)
    {
        // Live Chat run: `npm test` granted every toolchain tree to a fresh identity (72 s).
        Assert.Equal(expected, WindowsCodingSandbox.ToolingEcosystemsFor(command));
    }

    [Fact]
    public void Npm_script_tooling_includes_what_the_package_script_starts()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-eco-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), "{\"scripts\":{\"test\":\"node --test && python check.py\"}}");
        try
        {
            Assert.Equal(WindowsCodingSandbox.ToolingEcosystem.Node | WindowsCodingSandbox.ToolingEcosystem.Python,
                WindowsCodingSandbox.ToolingEcosystemsFor("npm test", root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Sandbox_path_keeps_only_absolute_entries()
    {
        // Relative/empty entries resolve against the agent-writable working directory.
        string sanitized = WindowsCodingSandbox.SanitizeSearchPath(@"C:\Windows\system32;;.;bin;.\tools;""C:\Program Files\dotnet"";\\server\share\bin;C:rel");

        Assert.Equal(@"C:\Windows\system32;C:\Program Files\dotnet;\\server\share\bin", sanitized);
    }

    [Fact]
    public async Task Acl_circuit_reopens_only_after_the_stuck_operation_has_ended()
    {
        // Live benchmark r4: one optional icacls grant stuck under memory pressure disabled every
        // later sandboxed command until the App restarted.
        var stuck = new TaskCompletionSource();

        Assert.False(WindowsCodingSandbox.ShouldReopenAclCircuit(null));
        Assert.False(WindowsCodingSandbox.ShouldReopenAclCircuit(stuck.Task));
        stuck.SetResult();
        await stuck.Task;
        Assert.True(WindowsCodingSandbox.ShouldReopenAclCircuit(stuck.Task));
    }

    [Theory]
    // Live benchmark r2: every multi-project `dotnet test` hung ~5 min on MSB1025 (node pipe denied).
    [InlineData("dotnet test --no-restore", "dotnet test --no-restore -m:1")]
    [InlineData("dotnet build", "dotnet build -m:1")]
    [InlineData("dotnet test --filter \"Sum_IncludesBothEnds\"", "dotnet test --filter \"Sum_IncludesBothEnds\" -m:1")]
    [InlineData("dotnet test --no-restore -m:1", "dotnet test --no-restore -m:1")]
    [InlineData("dotnet build /maxcpucount:4", "dotnet build /maxcpucount:4")]
    [InlineData("dotnet test -- RunConfiguration.MaxCpuCount=1", "dotnet test -- RunConfiguration.MaxCpuCount=1")]
    [InlineData("dotnet run --project App.csproj", "dotnet run --project App.csproj")]
    [InlineData("dotnet --info", "dotnet --info")]
    [InlineData("dotnet test Tests.dll", "dotnet test Tests.dll")]
    [InlineData("dotnet test & echo ok", "dotnet test & echo ok")]
    [InlineData("npm test", "npm test")]
    public void MSBuild_driven_dotnet_commands_run_in_process_inside_the_sandbox(string command, string expected)
    {
        Assert.Equal(expected, WindowsCodingSandbox.ApplyInProcessMSBuild(command));
    }

    [Fact]
    public void Failed_optional_grant_is_skipped_until_its_backoff_expires()
    {
        var key = (@"c:\parux-backoff-" + Guid.NewGuid().ToString("N"), "S-1-15-2-1", "(OI)(CI)(RX)");
        var now = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(WindowsCodingSandbox.IsOptionalGrantBackedOff(key, now));

        WindowsCodingSandbox.RecordOptionalGrantFailure(key, now);

        Assert.True(WindowsCodingSandbox.IsOptionalGrantBackedOff(key, now.AddMinutes(1)));
        Assert.True(WindowsCodingSandbox.IsOptionalGrantBackedOff(key, now + WindowsCodingSandbox.OptionalGrantFailureBackoff - TimeSpan.FromSeconds(1)));
        Assert.False(WindowsCodingSandbox.IsOptionalGrantBackedOff(key, now + WindowsCodingSandbox.OptionalGrantFailureBackoff));
        Assert.False(WindowsCodingSandbox.IsOptionalGrantBackedOff(key, now.AddMinutes(1)));
    }

    [Fact]
    public void Setup_failure_stops_retries_but_command_failure_and_runtime_timeout_remain_repairable()
    {
        Assert.True(WindowsCodingSandbox.IsInfrastructureUnavailable(
            new WindowsCodingSandbox.Result(-1, "", Error: "ACL updates are disabled after a previous Windows security update timed out.")));
        Assert.False(WindowsCodingSandbox.IsInfrastructureUnavailable(
            new WindowsCodingSandbox.Result(1, "Compiler error")));
        Assert.False(WindowsCodingSandbox.IsInfrastructureUnavailable(
            new WindowsCodingSandbox.Result(-1, "slow test", TimedOut: true, Error: "Command timed out inside AppContainer.")));
    }

    [Fact]
    public async Task Multi_line_command_is_a_repairable_input_error_not_sandbox_failure()
    {
        // Live parity run: a multi-line `python -c` stopped the whole task as "Coding sandbox unavailable".
        WindowsCodingSandbox.Result result = await WindowsCodingSandbox.RunAsync(
            Path.GetTempPath(), "python -c \"\nimport sys\nprint(1)\"", CancellationToken.None);

        Assert.True(result.RejectedInput);
        Assert.False(WindowsCodingSandbox.IsInfrastructureUnavailable(result));
        Assert.Contains("write_file", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet run --project CoreTaskSmoke.csproj --no-restore", true)]
    [InlineData("dotnet.exe --info", true)]
    [InlineData("dotnet build && npm test", false)]
    [InlineData("npm test", false)]
    public void Only_a_single_dotnet_command_uses_the_narrow_tooling_grants(string command, bool expected)
    {
        Assert.Equal(expected, WindowsCodingSandbox.IsSingleDotNetCommand(command));
    }

    [Fact]
    public void Workspace_profile_name_is_stable_and_scoped_per_workspace()
    {
        string rootA = Path.Combine(Path.GetTempPath(), "sandbox-a");
        string rootB = Path.Combine(Path.GetTempPath(), "sandbox-b");

        string first = WindowsCodingSandbox.BuildProfileName(rootA);
        string again = WindowsCodingSandbox.BuildProfileName(rootA);
        string other = WindowsCodingSandbox.BuildProfileName(rootB);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.StartsWith("PaRuX.Coding.", first, StringComparison.Ordinal);
        Assert.True(first.Length <= 64);
    }

    [Fact]
    public void Network_enabled_profile_is_distinct_but_shares_only_repository_sandbox_state()
    {
        string root = Path.Combine(Path.GetTempPath(), "sandbox-network-profile");

        string offline = WindowsCodingSandbox.BuildProfileName(root, WindowsCodingSandbox.NetworkMode.None);
        string online = WindowsCodingSandbox.BuildProfileName(root, WindowsCodingSandbox.NetworkMode.InternetClient);
        string state = WindowsCodingSandbox.BuildSandboxStateName(root);

        Assert.NotEqual(offline, online);
        Assert.EndsWith(".Net", online, StringComparison.Ordinal);
        Assert.DoesNotContain(".Net", offline, StringComparison.Ordinal);
        Assert.Equal(offline, state);
        Assert.Equal("S-1-15-3-1", WindowsCodingSandbox.InternetClientCapabilitySid);
        Assert.True(online.Length <= 64);
    }

    [Theory]
    [InlineData("echo sandbox-ok>probe.txt")]
    [InlineData("type \"%USERPROFILE%\\restore-seed.txt\"")]
    [InlineData("@echo off & echo hello")]
    [InlineData("set RESULT=ok && echo %RESULT%")]
    public void Shell_builtins_do_not_request_shared_tooling_roots(string command)
    {
        Assert.False(WindowsCodingSandbox.CommandMayNeedReadOnlyToolingRoots(command));
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("echo preparing & dotnet build")]
    [InlineData("\"C:\\Program Files\\Git\\bin\\git.exe\" status")]
    [InlineData("for /f %i in ('dotnet --version') do echo %i")]
    [InlineData("unknown-tool --version")]
    public void External_or_compound_commands_request_read_only_tooling_roots(string command)
    {
        Assert.True(WindowsCodingSandbox.CommandMayNeedReadOnlyToolingRoots(command));
    }

    [Theory]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("my_private_key")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("NPM_TOKEN")]
    [InlineData("SSH_AUTH_SOCK")]
    [InlineData("SERVICE_CREDENTIAL")]
    [InlineData("SESSION_COOKIE")]
    [InlineData("DATABASE_CONNECTION_STRING")]
    public void Credential_shaped_environment_names_are_removed(string name)
    {
        Assert.True(WindowsCodingSandbox.IsSecretEnvironmentName(name));
    }

    [Theory]
    [InlineData("GIT_ASKPASS")]
    [InlineData("SSH_ASKPASS")]
    [InlineData("GIT_CONFIG_GLOBAL")]
    [InlineData("NPM_CONFIG_USERCONFIG")]
    [InlineData("PIP_CONFIG_FILE")]
    [InlineData("PIP_INDEX_URL")]
    [InlineData("NUGET_CONFIG_FILE")]
    [InlineData("HTTP_PROXY")]
    [InlineData("https_proxy")]
    [InlineData("NO_PROXY")]
    public void User_config_credential_helpers_and_proxy_routes_are_not_inherited(string name)
    {
        Assert.True(WindowsCodingSandbox.IsBlockedEnvironmentName(name));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("DOTNET_ROOT")]
    [InlineData("JAVA_HOME")]
    [InlineData("NUMBER_OF_PROCESSORS")]
    public void Ordinary_tooling_environment_names_are_not_mistaken_for_secrets_or_blocked_config(string name)
    {
        Assert.False(WindowsCodingSandbox.IsSecretEnvironmentName(name));
        Assert.False(WindowsCodingSandbox.IsBlockedEnvironmentName(name));
    }

    [Fact]
    public void Real_gradle_maven_and_cargo_homes_are_never_tooling_root_candidates()
    {
        // CF-02: these folders hold repository and registry tokens. The sandbox redirects their tools to its own home,
        // so no command, unknown programs included, may receive a grant on the real folders.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] credentialHomes = new[] { ".gradle", ".m2", ".cargo" }
            .Select(name => Path.Combine(profile, name))
            .ToArray();
        foreach (string command in new[] { "gradlew build", "mvn -q test", "cargo test", "powershell -NoProfile -Command dir", "make all" })
        {
            foreach (string candidate in WindowsCodingSandbox.ReadOnlyToolingRootCandidates(command))
            {
                Assert.False(WindowsCodingSandbox.IsCredentialStorePath(candidate), $"{command} granted {candidate}");
                foreach (string home in credentialHomes)
                    Assert.False(
                        candidate.Equals(home, StringComparison.OrdinalIgnoreCase)
                            || candidate.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                        $"{command} granted {candidate}");
            }
        }
    }

    [Theory]
    [InlineData(@"C:\Users\me\.ssh\keys", true)]
    [InlineData(@"C:\Users\me\.gradle\caches", true)]
    [InlineData(@"C:\Users\me\AppData\Roaming\Microsoft\Credentials", true)]
    [InlineData(@"C:\Users\me\.nuget\packages", false)]
    [InlineData(@"C:\Program Files\nodejs", false)]
    public void Credential_store_folders_are_recognised_at_any_depth(string path, bool expected)
    {
        Assert.Equal(expected, WindowsCodingSandbox.IsCredentialStorePath(path));
    }

    [Fact]
    public void Literal_interpreter_paths_are_granted_only_from_trusted_installs_that_exist()
    {
        // SC-05: the command text is model output. Only a real interpreter inside a trusted install root qualifies.
        string root = Path.Combine(Path.GetTempPath(), "parux-explicit-" + Guid.NewGuid().ToString("N"));
        string trusted = Path.Combine(root, "Programs");
        string install = Path.Combine(trusted, "Python313");
        string credentialInstall = Path.Combine(trusted, ".ssh", "Python313");
        string outsideInstall = Path.Combine(root, "Elsewhere", "Python313");
        try
        {
            foreach (string dir in new[] { install, credentialInstall, outsideInstall })
            {
                Directory.CreateDirectory(Path.Combine(dir, "Lib"));
                File.WriteAllText(Path.Combine(dir, "python.exe"), string.Empty);
                File.WriteAllText(Path.Combine(dir, "python313.dll"), string.Empty);
            }
            var roots = new[] { trusted };

            Assert.Equal(install, WindowsCodingSandbox.ResolveExplicitExecutableDirectory(
                "\"" + Path.Combine(install, "python.exe") + "\" -V", "python.exe", roots));
            Assert.Null(WindowsCodingSandbox.ResolveExplicitExecutableDirectory(
                "\"" + Path.Combine(credentialInstall, "python.exe") + "\" -V", "python.exe", roots));
            Assert.Null(WindowsCodingSandbox.ResolveExplicitExecutableDirectory(
                "\"" + Path.Combine(outsideInstall, "python.exe") + "\" -V", "python.exe", roots));
            Assert.Null(WindowsCodingSandbox.ResolveExplicitExecutableDirectory(
                "\"" + Path.Combine(trusted, "Missing", "python.exe") + "\" -V", "python.exe", roots));
            // A UNC literal is rejected on its string form, before any filesystem or network access.
            Assert.Null(WindowsCodingSandbox.ResolveExplicitExecutableDirectory(
                @"\\attacker\share\python.exe -V", "python.exe", roots));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Explicit_interpreter_under_a_credential_folder_is_not_granted_with_the_default_roots()
    {
        // The real .ssh folder may exist (the old resolver granted any existing folder); only its path is used here.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Null(WindowsCodingSandbox.ResolveExplicitExecutableDirectory(
            "\"" + Path.Combine(profile, ".ssh", "python.exe") + "\" -V", "python.exe"));
        Assert.Null(WindowsCodingSandbox.ResolveExplicitExecutableDirectory(
            "\"C:\\Users\\x\\.ssh\\node.exe\" -v", "node.exe"));
    }

    [Fact]
    public void Junction_into_a_credential_store_adds_no_grant_under_a_trusted_looking_name()
    {
        // SC-05: a grant on a junction reaches the folder it points to, so a junction into .gradle adds nothing, not even
        // its own path. mklink /J needs no elevation, so the junction is real on this host.
        string root = Path.Combine(Path.GetTempPath(), "parux-junction-" + Guid.NewGuid().ToString("N"));
        string store = Path.Combine(root, "home", ".gradle");
        string link = Path.Combine(root, "Programs", "tooling-link");
        try
        {
            Directory.CreateDirectory(store);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            CreateJunction(link, store);
            var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            WindowsCodingSandbox.AddDirectory(granted, link);

            Assert.Empty(granted);
            Assert.True(WindowsCodingSandbox.ReachesCredentialStore(link));
        }
        finally { RemoveTestTree(root, link); }
    }

    [Fact]
    public void Folder_reached_through_a_link_into_a_credential_store_is_not_granted()
    {
        // A grant on "...\Tools\mid\keys" is applied through the junction "mid". Its lexical path names no credential store.
        string root = Path.Combine(Path.GetTempPath(), "parux-junction-" + Guid.NewGuid().ToString("N"));
        string store = Path.Combine(root, "home", ".ssh");
        string link = Path.Combine(root, "Tools", "mid");
        string reached = Path.Combine(link, "keys");
        try
        {
            Directory.CreateDirectory(Path.Combine(store, "keys"));
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            CreateJunction(link, store);
            Assert.True(Directory.Exists(reached));
            var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            WindowsCodingSandbox.AddDirectory(granted, reached);

            Assert.Empty(granted);
        }
        finally { RemoveTestTree(root, link); }
    }

    [Fact]
    public void Junction_to_an_ordinary_folder_is_still_granted_at_its_link_and_its_target()
    {
        // Control for the two tests above: the credential check must not refuse an ordinary tooling junction.
        string root = Path.Combine(Path.GetTempPath(), "parux-junction-" + Guid.NewGuid().ToString("N"));
        string tools = Path.Combine(root, "node-tools");
        string link = Path.Combine(root, "Programs", "node-current");
        try
        {
            Directory.CreateDirectory(tools);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            CreateJunction(link, tools);
            var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            WindowsCodingSandbox.AddDirectory(granted, link);

            Assert.Contains(link, granted);
            Assert.Contains(tools, granted);
        }
        finally { RemoveTestTree(root, link); }
    }

    [Fact]
    public void User_profile_caches_and_global_npm_config_stay_out_of_network_runs_and_unknown_programs()
    {
        // CF-02 residual decision. The npm global prefix (etc\npmrc can carry a registry token) is granted only to a local
        // Node command. A network run executes package install scripts with Internet access, and an unknown program is not
        // an npm command, so neither receives the user's npm prefix, NuGet cache or Go folder. Each folder is checked only
        // where it exists on this host, so the positive controls cannot pass vacuously.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string npmPrefix = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        string nugetPackages = Path.Combine(profile, ".nuget", "packages");
        string goFolder = Path.Combine(profile, "go");

        Assert.DoesNotContain(npmPrefix, WindowsCodingSandbox.ReadOnlyToolingRootCandidates("make all"));
        Assert.DoesNotContain(npmPrefix, WindowsCodingSandbox.ReadOnlyToolingRootCandidates("npm ci", null, networkEnabled: true));
        Assert.DoesNotContain(nugetPackages, WindowsCodingSandbox.ReadOnlyToolingRootCandidates("dotnet restore", null, networkEnabled: true));
        Assert.DoesNotContain(goFolder, WindowsCodingSandbox.ReadOnlyToolingRootCandidates("go mod download", null, networkEnabled: true));

        // Local commands keep the grants they need, so the restriction is not a blanket removal.
        if (Directory.Exists(npmPrefix))
            Assert.Contains(npmPrefix, WindowsCodingSandbox.ReadOnlyToolingRootCandidates("npm test"));
        if (Directory.Exists(nugetPackages))
            Assert.Contains(nugetPackages, WindowsCodingSandbox.ReadOnlyToolingRootCandidates("dotnet test"));
        if (Directory.Exists(goFolder))
            Assert.Contains(goFolder, WindowsCodingSandbox.ReadOnlyToolingRootCandidates("go test ./..."));
    }

    private static void CreateJunction(string link, string target)
    {
        // mklink /J needs no elevation, unlike a directory symbolic link.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\" >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("cmd.exe did not start.");
        Assert.True(process.WaitForExit(30_000), "mklink /J did not finish.");
        Assert.Equal(0, process.ExitCode);
    }

    private static void RemoveTestTree(string root, params string[] junctions)
    {
        // Junctions are removed as links first, so deleting the tree never walks through into a target.
        foreach (string junction in junctions)
            if (Directory.Exists(junction)) Directory.Delete(junction);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [Fact]
    public void Bounded_output_reader_keeps_the_head_and_the_final_error_of_a_large_log()
    {
        // CF-03: a command log is never loaded whole. The head and the final error line survive; the middle does not.
        string path = Path.Combine(Path.GetTempPath(), "parux-output-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using (FileStream file = File.Create(path))
            {
                file.Write(Encoding.ASCII.GetBytes("HEAD-MARKER\r\n"));
                file.Write(new byte[6 * 1024 * 1024]);
                file.Write(Encoding.ASCII.GetBytes("\r\nFAILED: TAIL-MARKER"));
            }

            WindowsCodingSandbox.BoundedOutput bounded = WindowsCodingSandbox.ReadBoundedOutput(path, headBytes: 1024, tailBytes: 1024);

            Assert.True(bounded.Truncated);
            Assert.StartsWith("HEAD-MARKER", bounded.Text, StringComparison.Ordinal);
            Assert.EndsWith("FAILED: TAIL-MARKER", bounded.Text, StringComparison.Ordinal);
            Assert.Contains("bytes of command output omitted", bounded.Text, StringComparison.Ordinal);
            Assert.True(bounded.Text.Length < 4096, "Retained " + bounded.Text.Length + " characters of a 6 MiB log.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Small_log_is_returned_whole_and_not_marked_truncated()
    {
        string path = Path.Combine(Path.GetTempPath(), "parux-output-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(path, "ok\r\nall passed");

            WindowsCodingSandbox.BoundedOutput bounded = WindowsCodingSandbox.ReadBoundedOutput(path);

            Assert.False(bounded.Truncated);
            Assert.Equal("ok\r\nall passed", bounded.Text);
        }
        finally { File.Delete(path); }
    }
}
