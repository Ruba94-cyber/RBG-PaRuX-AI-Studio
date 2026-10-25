using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Windows AppContainer execution boundary for model-requested coding commands.
    ///
    /// Every repository receives distinct no-network and controlled-network AppContainer SIDs. Both
    /// identities share only the PaRuX-owned repository sandbox state/cache directory so an explicit
    /// dependency restore can seed a later no-network build/test. The selected workspace plus this
    /// scratch directory are writable; a small set of already-installed SDK/cache roots is read-only.
    /// Ordinary coding commands receive zero network capabilities. A separate InternetClient mode
    /// exists only for PaRuX-controlled operations such as deterministic package restore; callers
    /// cannot turn arbitrary run_command into a networked shell. Credential-shaped environment
    /// variables are stripped in both modes, user credential/config/proxy helpers are not inherited,
    /// and HOME/AppData/package-manager state is redirected under the PaRuX sandbox. The process is
    /// placed in a kill-on-close Job Object so timeout/cancellation terminates descendants too.
    ///
    /// Security rule: failure to establish AppContainer is a command failure. There is deliberately
    /// no unsandboxed fallback for unattended coding.
    /// </summary>
    public static class WindowsCodingSandbox
    {
        private static readonly ConcurrentDictionary<(string Path, string Sid, string Rights), bool> _grantedAccessCache = new();
        // Optional grants that failed or timed out. Each icacls attempt can cost its full 15s
        // timeout, and commands succeed without these grants, so retrying them on every command
        // of a task adds minutes of overhead. Entries expire so a transient failure is retried.
        private static readonly ConcurrentDictionary<(string Path, string Sid, string Rights), DateTime> _failedOptionalGrantUntilUtc = new();
        internal static readonly TimeSpan OptionalGrantFailureBackoff = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _pathGrantLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim _aclMutationGate = new(1, 1);
        private static readonly HashSet<string> CmdBuiltinsWithoutToolingAccess = new(StringComparer.OrdinalIgnoreCase)
        {
            "assoc", "cd", "chdir", "cls", "color", "date", "dir", "echo", "exit", "ftype",
            "md", "mkdir", "mklink", "path", "pause", "popd", "prompt", "pushd", "rd", "rem",
            "ren", "rename", "rmdir", "set", "time", "title", "type", "ver", "verify", "vol"
        };
        private static int _aclMutationTimedOut;
        private static string? _aclMutationTimedOutPath;
        // The stuck ACL operation that tripped the circuit. When it finally ends, later grants may
        // safely run again; without this a single slow optional grant disabled every future
        // sandboxed command until the App restarted.
        private static Task? _aclBlockingOperation;
        private static Task? _lastManagedAclTimeout;
        public enum NetworkMode
        {
            None = 0,
            InternetClient = 1
        }

        internal const string InternetClientCapabilitySid = "S-1-15-3-1";

        // A command log is read as a bounded head and tail, never whole. A log that passes the hard cap while the
        // command still runs stops the whole sandboxed process tree before it can fill the disk (CF-03).
        internal const long MaxSandboxOutputBytes = 64L * 1024 * 1024;
        internal const int SandboxOutputHeadBytes = 2 * 1024 * 1024;
        internal const int SandboxOutputTailBytes = 2 * 1024 * 1024;

        internal sealed record BoundedOutput(string Text, bool Truncated);

        private const uint SeGroupEnabled = 0x00000004;
        private const int ErrorAlreadyExistsHResult = unchecked((int)0x800700B7);
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint CreateSuspended = 0x00000004;
        private const uint CreateNoWindow = 0x08000000;
        private const uint StartfUseShowWindow = 0x00000001;
        private const ushort SwHide = 0;
        private const uint WaitObject0 = 0x00000000;
        private const uint WaitTimeout = 0x00000102;
        private const uint JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private static readonly IntPtr ProcThreadAttributeSecurityCapabilities = new(0x00020009);

        private static readonly string[] SecretEnvironmentMarkers =
        {
            "TOKEN", "SECRET", "PASSWORD", "PASSWD", "API_KEY", "APIKEY", "PRIVATE_KEY",
            "OPENAI", "ANTHROPIC", "GITHUB_", "GITLAB_", "AZURE_", "AWS_", "HF_TOKEN",
            "HUGGINGFACE", "NPM_TOKEN", "NUGET_AUTH", "DOCKER_AUTH", "SSH_AUTH_SOCK",
            "CREDENTIAL", "COOKIE", "CONNECTION_STRING", "BEARER"
        };

        private static readonly HashSet<string> BlockedEnvironmentNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "GIT_ASKPASS", "SSH_ASKPASS", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM",
            "NPM_CONFIG_USERCONFIG", "npm_config_userconfig", "PIP_CONFIG_FILE",
            "PIP_INDEX_URL", "PIP_EXTRA_INDEX_URL", "NUGET_CONFIG_FILE",
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
            "http_proxy", "https_proxy", "all_proxy", "no_proxy"
        };

        public sealed record Result(int ExitCode, string Output, bool TimedOut = false, string? Error = null)
        {
            /// <summary>Per-phase sandbox setup time (grants), for performance diagnosis.</summary>
            public string? SetupTimings { get; init; }

            /// <summary>The command itself was not acceptable (empty or multi-line); nothing about the sandbox failed.</summary>
            public bool RejectedInput { get; init; }

            /// <summary>The command's log was longer than the retained head and tail, or the process was stopped at the cap.</summary>
            public bool OutputTruncated { get; init; }
        }

        // A nonzero process exit is a command result the agent may repair. A setup error means
        // the command never ran, so repeating it cannot validate a code change. A command that
        // actually started but timed out remains eligible for a narrower retry.
        // A rejected command payload (live parity run: a multi-line `python -c`) is the agent's input
        // error; treating it as sandbox failure stopped a task that one rewritten command would fix.
        internal static bool IsInfrastructureUnavailable(Result result) =>
            result.Error != null && !result.TimedOut && !result.RejectedInput;

        // Test seam for the sandbox's failure paths. AsyncLocal scopes it to the calling test's async flow, so
        // parallel tests cannot see each other's injected faults.
        private static readonly AsyncLocal<SandboxFaultInjection?> _faultInjection = new();

        internal sealed class SandboxFaultInjection
        {
            /// <summary>Treats the Job Object assignment as failed, before the child is resumed.</summary>
            public bool FailJobAssignment { get; init; }

            /// <summary>Receives the id of the suspended child as soon as it exists.</summary>
            public Action<uint>? OnProcessCreated { get; init; }
        }

        internal static IDisposable UseFaultInjectionForTests(SandboxFaultInjection injection)
        {
            SandboxFaultInjection? previous = _faultInjection.Value;
            _faultInjection.Value = injection;
            return new FaultInjectionScope(previous);
        }

        private sealed class FaultInjectionScope : IDisposable
        {
            private readonly SandboxFaultInjection? _previous;
            private bool _disposed;

            public FaultInjectionScope(SandboxFaultInjection? previous) => _previous = previous;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _faultInjection.Value = _previous;
            }
        }

        public static async Task<Result> RunAsync(
            string workspaceRoot,
            string command,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null,
            NetworkMode networkMode = NetworkMode.None)
        {
            if (!OperatingSystem.IsWindows())
                return new Result(-1, string.Empty, Error: "The desktop coding sandbox is Windows-only on this build.");
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return new Result(-1, string.Empty, Error: "The coding workspace does not exist.");
            if (string.IsNullOrWhiteSpace(command))
                return new Result(-1, string.Empty, Error: "The command is empty.") { RejectedInput = true };
            if (command.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                return new Result(-1, string.Empty,
                    Error: "Multi-line/NUL command payloads are not accepted by the coding sandbox. Put multi-line code in a script file "
                        + "with write_file (outside the files you must not edit) and run that file, or pass a single-line command.")
                { RejectedInput = true };
            if (networkMode is not (NetworkMode.None or NetworkMode.InternetClient))
                return new Result(-1, string.Empty, Error: "Unsupported coding sandbox network mode.");

            string root = Path.GetFullPath(workspaceRoot);
            string profileName = BuildProfileName(root, networkMode);
            string sandboxStateName = BuildSandboxStateName(root);
            AppPaths.EnsureCreated();
            string sandboxDir = Path.Combine(AppPaths.CodingAgentStateDir, "sandbox", sandboxStateName);
            // Measured on Windows 11: once the sandbox state directory exceeds roughly 190 characters (185 worked, 191 hung) every
            // child process started by cmd.exe inside the AppContainer hangs (or is denied) while cmd builtins
            // still work, so the command would burn its whole timeout with no output. Fail fast and say why.
            if (sandboxDir.Length > MaxSandboxDirectoryLength)
                return new Result(-1, string.Empty, Error:
                    "The coding sandbox state path is " + sandboxDir.Length + " characters (limit " + MaxSandboxDirectoryLength
                    + "); child processes cannot start reliably above it. Use a shorter PaRuX data root (PARUX_DATA_ROOT).");
            string runId = Guid.NewGuid().ToString("N");
            string scriptPath = Path.Combine(sandboxDir, "run-" + runId + ".cmd");
            string outputPath = Path.Combine(sandboxDir, "out-" + runId + ".log");
            var npmScriptFiles = new List<string>();

            IntPtr appContainerSid = IntPtr.Zero;
            IntPtr internetCapabilitySid = IntPtr.Zero;
            IntPtr capabilityArray = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr securityCapabilitiesPtr = IntPtr.Zero;
            IntPtr environmentPtr = IntPtr.Zero;
            IntPtr job = IntPtr.Zero;
            PROCESS_INFORMATION processInfo = default;
            bool childResumed = false;

            try
            {
                string sandboxHome = Path.Combine(sandboxDir, "home");
                string sandboxTemp = Path.Combine(sandboxDir, "temp");
                Directory.CreateDirectory(sandboxDir);
                Directory.CreateDirectory(sandboxHome);
                Directory.CreateDirectory(sandboxTemp);

                int capabilityCount = 0;
                if (networkMode == NetworkMode.InternetClient)
                {
                    if (!ConvertStringSidToSidW(InternetClientCapabilitySid, out internetCapabilitySid)
                        || internetCapabilitySid == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve Windows InternetClient capability SID.");
                    var internet = new SID_AND_ATTRIBUTES
                    {
                        Sid = internetCapabilitySid,
                        Attributes = SeGroupEnabled
                    };
                    capabilityArray = Marshal.AllocHGlobal(Marshal.SizeOf<SID_AND_ATTRIBUTES>());
                    Marshal.StructureToPtr(internet, capabilityArray, false);
                    capabilityCount = 1;
                }

                appContainerSid = EnsureProfile(profileName, capabilityArray, capabilityCount);
                string sid = new SecurityIdentifier(appContainerSid).Value;
                EnsureWindowStationAndDesktopAppContainerAccess();

                var setupClock = Stopwatch.StartNew();
                await GrantWorkspaceAccessAsync(root, sid, cancellationToken).ConfigureAwait(false);
                long workspaceMs = setupClock.ElapsedMilliseconds;
                await GrantPathAccessAsync(sandboxDir, sid, "(OI)(CI)(M)", required: true, cancellationToken).ConfigureAwait(false);
                await GrantPathAccessAsync(sandboxHome, sid, "(OI)(CI)(M)", required: true, cancellationToken).ConfigureAwait(false);
                await GrantPathAccessAsync(sandboxTemp, sid, "(OI)(CI)(M)", required: true, cancellationToken).ConfigureAwait(false);

                long sandboxDirsMs = setupClock.ElapsedMilliseconds - workspaceMs;
                await GrantPathsAccessAsync(EnumerateAncestors(sandboxDir), sid, "(RX)", required: false, cancellationToken).ConfigureAwait(false);
                long ancestorsMs = setupClock.ElapsedMilliseconds - workspaceMs - sandboxDirsMs;
                if (CommandMayNeedReadOnlyToolingRoots(command))
                    await GrantReadOnlyToolingRootsAsync(sid, command, cancellationToken, root, networkMode == NetworkMode.InternetClient).ConfigureAwait(false);
                string setupTimings = "workspace=" + workspaceMs + "ms sandboxDirs=" + sandboxDirsMs + "ms sandboxAncestors="
                    + ancestorsMs + "ms tooling=" + (setupClock.ElapsedMilliseconds - workspaceMs - sandboxDirsMs - ancestorsMs) + "ms";

                File.WriteAllText(outputPath, string.Empty);

                // npm cannot start inside the AppContainer on a default host; run its script directly.
                CodingNpmScriptRunner.Rewrite? npmRewrite = CodingNpmScriptRunner.TryRewrite(root, command, sandboxDir, "run-" + runId);
                string effectiveCommand = npmRewrite?.CommandLine ?? command;
                if (npmRewrite != null)
                {
                    npmScriptFiles.AddRange(npmRewrite.ScriptFiles);
                    foreach (string file in npmRewrite.ScriptFiles)
                        await GrantPathAccessAsync(file, sid, "(RX)", required: false, cancellationToken).ConfigureAwait(false);
                }
                string script = "@echo off\r\n"
                    + "set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1\r\n"
                    + "set DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1\r\n"
                    + "set DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1\r\n"
                    + "set DOTNET_MULTILEVEL_LOOKUP=0\r\n"
                    + "set MSBUILDDISABLENODEREUSE=1\r\n"
                    + "set MSBUILD_NO_SERVER=1\r\n"
                    + "set MSBUILDNOOUTOFPROCNODES=1\r\n"
                    + "set UseSharedCompilation=false\r\n"
                    + "set VBCSCOMPILER_DISABLED=1\r\n"
                    + "set DOTNET_NOLOGO=1\r\n"
                    + "set NUGET_CERT_REVOCATION_MODE=offline\r\n"
                    + "set POWERSHELL_TELEMETRY_OPTOUT=1\r\n"
                    + "set CI=1\r\n"
                    + "set NONINTERACTIVE=1\r\n"
                    + ApplyInProcessMSBuild(effectiveCommand) + "\r\n"
                    + "exit /b %errorlevel%\r\n";
                await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

                await GrantPathAccessAsync(scriptPath, sid, "(RX)", required: false, cancellationToken).ConfigureAwait(false);
                await GrantPathAccessAsync(outputPath, sid, "(M)", required: false, cancellationToken).ConfigureAwait(false);

                STARTUPINFOEX startup = new();
                startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                startup.StartupInfo.dwFlags = StartfUseShowWindow;
                startup.StartupInfo.wShowWindow = SwHide;

                IntPtr attributeSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
                if (attributeSize == IntPtr.Zero)
                    return new Result(-1, string.Empty, Error: "Windows did not provide an AppContainer attribute-list size.");

                attributeList = Marshal.AllocHGlobal(attributeSize);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not initialize AppContainer process attributes.");

                SECURITY_CAPABILITIES capabilities = new()
                {
                    AppContainerSid = appContainerSid,
                    Capabilities = capabilityArray,
                    CapabilityCount = capabilityCount,
                    Reserved = 0
                };
                securityCapabilitiesPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
                Marshal.StructureToPtr(capabilities, securityCapabilitiesPtr, false);

                if (!UpdateProcThreadAttribute(
                    attributeList, 0, ProcThreadAttributeSecurityCapabilities,
                    securityCapabilitiesPtr, (IntPtr)Marshal.SizeOf<SECURITY_CAPABILITIES>(),
                    IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not apply AppContainer security capabilities.");
                startup.lpAttributeList = attributeList;

                environmentPtr = BuildSanitizedEnvironment(
                    sandboxHome, sandboxTemp,
                    preferPrivatePackageCaches: networkMode == NetworkMode.InternetClient,
                    workspaceRoot: workspaceRoot,
                    command: effectiveCommand);

                string cmd = Environment.GetEnvironmentVariable("COMSPEC")
                    ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
                string commandLine = "\"" + cmd + "\" /s /c \"\"" + scriptPath + "\" < NUL > \"" + outputPath + "\" 2>&1\"";
                var mutableCommand = new StringBuilder(commandLine);

                uint creationFlags = ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended | CreateNoWindow;
                if (!CreateProcessW(
                    cmd, mutableCommand, IntPtr.Zero, IntPtr.Zero, false, creationFlags,
                    environmentPtr, root, ref startup, out processInfo))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not launch the coding command inside AppContainer.");
                _faultInjection.Value?.OnProcessCreated?.Invoke(processInfo.dwProcessId);

                job = CreateKillOnCloseJob();
                bool jobAssigned = job != IntPtr.Zero
                    && _faultInjection.Value?.FailJobAssignment != true
                    && AssignProcessToJobObject(job, processInfo.hProcess);
                if (!jobAssigned)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the sandboxed command to its Job Object.");

                if (ResumeThread(processInfo.hThread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume the sandboxed command.");
                childResumed = true;

                TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromMinutes(10);
                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(effectiveTimeout);

                bool timedOut = false;
                bool outputCapExceeded = false;
                try
                {
                    bool exited = await WaitForProcessAsync(processInfo.hProcess, timeoutCts.Token, outputPath).ConfigureAwait(false);
                    if (!exited)
                    {
                        // The log passed the hard cap while the command still runs: stop the whole tree now (CF-03).
                        outputCapExceeded = true;
                        TerminateJobObject(job, 1450);
                        await WaitForTerminatedAsync(processInfo.hProcess).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    timedOut = true;
                    TerminateJobObject(job, 1460);
                    await WaitForTerminatedAsync(processInfo.hProcess).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TerminateJobObject(job, 1223);
                    throw;
                }

                if (timedOut)
                {
                    string partial = SafeRead(outputPath);
                    return new Result(-1, partial, TimedOut: true,
                        Error: "Command timed out inside AppContainer and the full process tree was terminated.");
                }

                if (outputCapExceeded)
                {
                    // A nonzero result the agent can repair. It is not a sandbox failure, so Error stays null.
                    string capped = SafeRead(outputPath);
                    return new Result(-1, capped + "\r\n[sandbox output passed the " + (MaxSandboxOutputBytes >> 20)
                        + " MiB cap; the sandboxed process tree was terminated]")
                    { OutputTruncated = true, SetupTimings = setupTimings };
                }

                if (!GetExitCodeProcess(processInfo.hProcess, out uint exitCode))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the sandboxed command exit code.");

                BoundedOutput output = TryReadBoundedOutput(outputPath);
                return new Result(unchecked((int)exitCode), output.Text) { OutputTruncated = output.Truncated, SetupTimings = setupTimings };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new Result(-1, SafeRead(outputPath), Error: ex.Message);
            }
            finally
            {
                // A child that was created but never resumed is stopped by no job (job setup or assignment
                // failed), and closing its handles would leave it suspended for good. End it first (CF-04).
                if (!childResumed && processInfo.hProcess != IntPtr.Zero) TerminateProcess(processInfo.hProcess, 1);
                if (processInfo.hThread != IntPtr.Zero) CloseHandle(processInfo.hThread);
                if (processInfo.hProcess != IntPtr.Zero) CloseHandle(processInfo.hProcess);
                if (job != IntPtr.Zero) CloseHandle(job);
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
                if (securityCapabilitiesPtr != IntPtr.Zero) Marshal.FreeHGlobal(securityCapabilitiesPtr);
                if (capabilityArray != IntPtr.Zero) Marshal.FreeHGlobal(capabilityArray);
                if (internetCapabilitySid != IntPtr.Zero) LocalFree(internetCapabilitySid);
                if (environmentPtr != IntPtr.Zero) Marshal.FreeHGlobal(environmentPtr);
                if (appContainerSid != IntPtr.Zero) FreeSid(appContainerSid);
                try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                foreach (string file in npmScriptFiles)
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
        }

        internal const int MaxSandboxDirectoryLength = 185;

        internal static string BuildProfileName(string root) => BuildProfileName(root, NetworkMode.None);

        internal static string BuildProfileName(string root, NetworkMode networkMode)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()));
            string suffix = networkMode == NetworkMode.InternetClient ? ".Net" : string.Empty;
            return "PaRuX.Coding." + Convert.ToHexString(hash).ToLowerInvariant()[..24] + suffix;
        }

        internal static string BuildSandboxStateName(string root) => BuildProfileName(root, NetworkMode.None);

        /// <summary>
        /// Test-only teardown: reverses what <see cref="RunAsync"/> leaves behind for one workspace
        /// identity. Production usage deliberately keeps the AppContainer profile and its granted
        /// ACEs alive across repeated calls against the same repository (that is the point of
        /// <see cref="_grantedAccessCache"/> - avoiding a re-grant on every coding-agent turn), so
        /// RunAsync itself must never delete them. Tests are different: each one manufactures a
        /// disposable one-off workspace under a fresh GUID, so without an explicit teardown every
        /// test run permanently leaks one AppContainer profile/package folder plus its granted ACEs
        /// on shared paths such as the .nuget package cache. Confirmed on this host: 335 stale ACEs
        /// on .nuget\packages and 418 orphaned AppContainer package folders had accumulated from
        /// prior runs before this method existed.
        /// </summary>
        internal static bool DeleteProfileForTests(string root, NetworkMode networkMode = NetworkMode.None)
        {
            if (!OperatingSystem.IsWindows()) return true;
            string profileName = BuildProfileName(root, networkMode);
            IntPtr sidPtr = IntPtr.Zero;
            bool cleanupComplete = true;
            try
            {
                int hr = DeriveAppContainerSidFromAppContainerName(profileName, out sidPtr);
                if (hr >= 0 && sidPtr != IntPtr.Zero)
                {
                    string sid = new SecurityIdentifier(sidPtr).Value;
                    foreach (var key in _grantedAccessCache.Keys.Where(k => string.Equals(k.Sid, sid, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        if (RevokeSinglePathAccess(key.Path, sid))
                            _grantedAccessCache.TryRemove(key, out _);
                        else
                            cleanupComplete = false;
                    }
                }
                else
                {
                    cleanupComplete = false;
                }
            }
            catch { cleanupComplete = false; }
            finally
            {
                if (sidPtr != IntPtr.Zero) FreeSid(sidPtr);
            }

            if (cleanupComplete)
            {
                try
                {
                    int deleteResult = DeleteAppContainerProfile(profileName);
                    if (deleteResult < 0) cleanupComplete = false;
                }
                catch { cleanupComplete = false; }
            }
            return cleanupComplete;
        }

        private static bool RevokeSinglePathAccess(string path, string sid)
        {
            if (IsAclCircuitOpen()) return false;
            if (!_aclMutationGate.Wait(TimeSpan.FromSeconds(1))) return false;

            try
            {
                if (IsAclCircuitOpen()) return false;
                if (!Directory.Exists(path) && !File.Exists(path)) return true;
                var info = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                info.ArgumentList.Add(path);
                info.ArgumentList.Add("/remove:g");
                info.ArgumentList.Add("*" + sid);
                info.ArgumentList.Add("/Q");

                using var process = Process.Start(info);
                if (process == null) return false;
                if (!process.WaitForExit(5000))
                {
                    KillAclChildAndTripCircuitIfNeeded(process, path);
                    return false;
                }
                return process.ExitCode == 0;
            }
            catch { return false; }
            finally { _aclMutationGate.Release(); }
        }

        private static IntPtr EnsureProfile(string profileName, IntPtr capabilities, int capabilityCount)
        {
            // The profile must be registered before its deterministic SID can be used to
            // launch a process. Deriving first returns a SID for a valid name even when
            // the profile does not yet exist, which drops InternetClient capabilities for
            // a first-time controlled restore.
            int hr = CreateAppContainerProfile(
                profileName, "PaRuX Coding Sandbox", "PaRuX isolated coding workspace",
                capabilities, (uint)Math.Max(0, capabilityCount), out _);
            if (hr < 0 && hr != ErrorAlreadyExistsHResult)
                Marshal.ThrowExceptionForHR(hr);

            hr = DeriveAppContainerSidFromAppContainerName(profileName, out IntPtr sid);
            if (hr < 0 || sid == IntPtr.Zero)
                Marshal.ThrowExceptionForHR(hr);
            return sid;
        }

        private static async Task GrantWorkspaceAccessAsync(string root, string sid, CancellationToken ct)
        {
            // 1. Grant modify access to the workspace root itself.
            await GrantPathAccessAsync(root, sid, "(OI)(CI)(M)", required: true, ct).ConfigureAwait(false);

            // 2. Git metadata (the worktree's gitdir and the repository's common .git) is READ-ONLY.
            // Git itself is denied inside the sandbox and build tools only read it. Write access
            // let a sandboxed command rewrite the user's repository config (core.fsmonitor, filter
            // drivers, hooks), which the next host-side git call would execute outside the
            // AppContainer. "/grant:r" also replaces the Modify ACE earlier versions left behind.
            foreach (string gitDir in ResolveGitDirectories(root))
            {
                // Required: if the downgrade cannot be applied the command must not run with the
                // Modify right an earlier version may have left on the user's repository.
                await GrantPathAccessAsync(gitDir, sid, GitMetadataRights, required: true, ct, replaceExisting: true).ConfigureAwait(false);
            }

            // 3. Grant traverse permission (RX) on all ancestor directories up to the drive root.
            // AppContainers lack SeChangeNotifyPrivilege (Bypass Traverse Checking), so getcwd()
            // or GetCurrentDirectory fails with EACCES / Permission Denied if any ancestor cannot be traversed.
            // Note: Stop at the drive root (e.g. C:\) which is already world-traversable on Windows.
            await GrantPathsAccessAsync(EnumerateAncestors(root), sid, "(RX)", required: false, ct).ConfigureAwait(false);

            // The .NET SDK walks parent directories looking for global.json even when the
            // project itself is inside an isolated worktree. Traverse permission on a parent
            // directory does not grant read access to its existing files. Grant only this
            // well-known SDK selector, not the surrounding repository or user config files.
            foreach (string ancestor in EnumerateAncestors(root))
            {
                string sdkSelector = Path.Combine(ancestor, "global.json");
                if (File.Exists(sdkSelector))
                    await GrantPathAccessAsync(sdkSelector, sid, "(RX)", required: false, ct).ConfigureAwait(false);
            }
        }

        private static IEnumerable<string> EnumerateAncestors(string path)
        {
            DirectoryInfo? parent = Directory.GetParent(path);
            while (parent != null && parent.Parent != null)
            {
                yield return parent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                parent = parent.Parent;
            }
        }

        /// <summary>
        /// Git metadata directories the sandbox may read: only those of an agent worktree whose
        /// ".git" link validates against the exact shape Git creates (see AgentWorktreeGitTrust).
        /// The link file itself is sandbox-writable, so an unvalidated link could otherwise choose
        /// which host directory (for example ~/.ssh) receives the grant. A plain ".git" directory
        /// inside the root is already covered by the root's own grant.
        /// </summary>
        internal static List<string> ResolveGitDirectories(string root)
        {
            var directories = new List<string>();
            string gitPath = Path.Combine(root, ".git");
            if (File.Exists(gitPath)
                && PaRuX.Contracts.AgentWorktreeGitTrust.TryResolveWorktreeMetadata(root, out string gitDir, out string commonDir, out _))
            {
                directories.Add(gitDir);
                directories.Add(commonDir);
            }
            return directories;
        }

        internal const string GitMetadataRights = "(OI)(CI)(RX)";

        private static async Task GrantPathAccessAsync(
            string path,
            string sid,
            string rights,
            bool required,
            CancellationToken ct,
            bool replaceExisting = false)
        {
            await GrantPathsAccessAsync(new[] { path }, sid, rights, required, ct, replaceExisting).ConfigureAwait(false);
        }

        private static async Task GrantPathsAccessAsync(
            IEnumerable<string> paths,
            string sid,
            string rights,
            bool required,
            CancellationToken ct,
            bool replaceExisting = false)
        {
            if (paths == null || string.IsNullOrWhiteSpace(sid)) return;
            var ungranted = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !_grantedAccessCache.ContainsKey((p.ToLowerInvariant(), sid, rights)))
                .ToList();

            // A path lock alone does not protect nested paths. Serialize writes because Windows
            // ACL updates against ancestor/child directories can contend in filesystem filters.
            foreach (string path in ungranted)
                await GrantSinglePathAccessAsync(path, sid, rights, required, ct, replaceExisting).ConfigureAwait(false);
        }

        private static bool IsWellKnownSystemAncestor(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            try
            {
                string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.Length <= 3) return true; // Drive root e.g. "C:" or "C:\"

                string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string users = Directory.GetParent(userProfile)?.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;

                if (string.Equals(full, progFiles, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(full, progFilesX86, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(full, windows, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(full, userProfile, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(users) && string.Equals(full, users, StringComparison.OrdinalIgnoreCase)) ||
                    full.StartsWith(progFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(progFilesX86 + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static readonly TimeSpan ManagedAclGrantTimeout = TimeSpan.FromSeconds(15);

        private enum ManagedAclGrantResult
        {
            Granted,
            Failed,
            TimedOut
        }

        /// <summary>
        /// Removes explicit Allow rules that give any AppContainer identity (S-1-15-2-*) write
        /// access. Used on Git metadata: earlier versions granted Modify on the user's repository
        /// .git to every per-workspace sandbox profile, and those rules outlive the profile.
        /// </summary>
        internal static int RemoveAppContainerWriteRules(DirectorySecurity security)
        {
            const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData
                | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete
                | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            int removed = 0;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToList())
            {
                if (rule.AccessControlType != AccessControlType.Allow || rule.IsInherited) continue;
                if (!rule.IdentityReference.Value.StartsWith("S-1-15-2-", StringComparison.OrdinalIgnoreCase)) continue;
                if ((rule.FileSystemRights & WriteRights) == 0) continue;
                security.RemoveAccessRuleSpecific(rule);
                removed++;
            }
            return removed;
        }

        private const uint DaclSecurityInformation = 0x00000004;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetFileSecurityW")]
        private static extern bool SetFileSecurity(string fileName, uint securityInformation, byte[] securityDescriptor);

        /// <summary>
        /// Adds a non-inheritable Allow RX entry for <paramref name="sid"/> to one directory's DACL
        /// with SetFileSecurity, which (unlike SetNamedSecurityInfo/icacls) does not re-propagate
        /// inheritance to children - correct here because the new entry is not inheritable.
        /// </summary>
        internal static bool TryGrantTraverseWithoutPropagation(string directory, string sid)
        {
            try
            {
                var security = new DirectorySecurity(directory, AccessControlSections.Access);
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid),
                    FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                return SetFileSecurity(directory, DaclSecurityInformation, security.GetSecurityDescriptorBinaryForm());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or PrivilegeNotHeldException or SystemException)
            {
                return false;
            }
        }

        private static void TripAclCircuit(string path, Task? blockingOperation)
        {
            Volatile.Write(ref _aclBlockingOperation, blockingOperation);
            Interlocked.CompareExchange(ref _aclMutationTimedOutPath, path, null);
            Interlocked.Exchange(ref _aclMutationTimedOut, 1);
        }

        private static bool IsAclCircuitOpen()
        {
            if (Volatile.Read(ref _aclMutationTimedOut) == 0) return false;
            if (!ShouldReopenAclCircuit(Volatile.Read(ref _aclBlockingOperation))) return true;
            Volatile.Write(ref _aclBlockingOperation, null);
            Volatile.Write(ref _aclMutationTimedOutPath, null);
            Interlocked.Exchange(ref _aclMutationTimedOut, 0);
            return false;
        }

        /// <summary>An open ACL circuit may close only once the stuck operation is known to have ended.</summary>
        internal static bool ShouldReopenAclCircuit(Task? blockingOperation) =>
            blockingOperation != null && blockingOperation.IsCompleted;

        private static void KillAclChildAndTripCircuitIfNeeded(Process? process, string path)
        {
            if (process == null) return;
            bool exited;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                exited = process.WaitForExit(1000);
            }
            catch (InvalidOperationException)
            {
                return; // The process never started.
            }
            catch
            {
                try { exited = process.HasExited; }
                catch { exited = false; }
            }

            if (!exited)
            {
                // Keep later ACL writes from overlapping a child still stuck in a native ACL call.
                int stuckId = 0;
                try { stuckId = process.Id; } catch (InvalidOperationException) { }
                TripAclCircuit(path, stuckId == 0 ? null : Task.Run(() =>
                {
                    try { using Process stuck = Process.GetProcessById(stuckId); stuck.WaitForExit(); }
                    catch (ArgumentException) { } // already gone
                    catch (InvalidOperationException) { }
                }));
            }
        }

        internal static bool IsOptionalGrantBackedOff((string Path, string Sid, string Rights) key, DateTime utcNow)
        {
            if (!_failedOptionalGrantUntilUtc.TryGetValue(key, out DateTime untilUtc)) return false;
            if (utcNow < untilUtc) return true;
            _failedOptionalGrantUntilUtc.TryRemove(new KeyValuePair<(string Path, string Sid, string Rights), DateTime>(key, untilUtc));
            return false;
        }

        internal static void RecordOptionalGrantFailure((string Path, string Sid, string Rights) key, DateTime utcNow) =>
            _failedOptionalGrantUntilUtc[key] = utcNow + OptionalGrantFailureBackoff;

        /// <summary>
        /// True when the path's DACL already allows <paramref name="sid"/> at least the requested icacls-style
        /// rights ("(RX)", "(M)", "(F)") with at least the requested inheritance ("(OI)", "(CI)"), either as an
        /// explicit or an inherited ACE. Any error means "unknown", so the normal grant path runs.
        /// </summary>
        internal static bool HasEquivalentAllowRule(string path, string sid, string rights)
        {
            if (string.IsNullOrWhiteSpace(rights) || rights.Contains("(IO)", StringComparison.OrdinalIgnoreCase)) return false;
            FileSystemRights required = rights.Contains("(F)", StringComparison.OrdinalIgnoreCase) ? FileSystemRights.FullControl
                : rights.Contains("(M)", StringComparison.OrdinalIgnoreCase) ? FileSystemRights.Modify
                : rights.Contains("(RX)", StringComparison.OrdinalIgnoreCase) ? FileSystemRights.ReadAndExecute
                : 0;
            if (required == 0) return false;
            InheritanceFlags inheritance = InheritanceFlags.None;
            if (rights.Contains("(OI)", StringComparison.OrdinalIgnoreCase)) inheritance |= InheritanceFlags.ObjectInherit;
            if (rights.Contains("(CI)", StringComparison.OrdinalIgnoreCase)) inheritance |= InheritanceFlags.ContainerInherit;
            try
            {
                var identity = new SecurityIdentifier(sid);
                FileSystemSecurity security = Directory.Exists(path)
                    ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                    : File.Exists(path) ? new FileInfo(path).GetAccessControl(AccessControlSections.Access) : null!;
                if (security == null) return false;
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (rule.AccessControlType != AccessControlType.Allow || !identity.Equals(rule.IdentityReference)) continue;
                    if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                    if ((rule.FileSystemRights & required) != required) continue;
                    if ((rule.InheritanceFlags & inheritance) != inheritance) continue;
                    return true;
                }
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or SystemException)
            {
                return false;
            }
        }

        private static async Task GrantSinglePathAccessAsync(
            string cleanPath,
            string sid,
            string rights,
            bool required,
            CancellationToken ct,
            bool replaceExisting = false)
        {
            string keyPath = cleanPath.ToLowerInvariant();
            var cacheKey = (keyPath, sid, rights);
            if (_grantedAccessCache.ContainsKey(cacheKey)) return;
            // A grant that times out does so because of the path (subtree size), not the identity.
            var backoffKey = (keyPath, "*", rights);
            if (!required && IsOptionalGrantBackedOff(backoffKey, DateTime.UtcNow)) return;

            if (IsWellKnownSystemAncestor(cleanPath))
            {
                _grantedAccessCache[cacheKey] = true;
                return;
            }

            // Grants persist on disk but the in-memory cache starts empty with every App process, so the
            // first command after each start re-ran icacls propagation over whole tooling trees (72 s in
            // the live Chat run). An ACE already present for this identity is a no-op; reading the DACL
            // takes milliseconds.
            if (!replaceExisting && HasEquivalentAllowRule(cleanPath, sid, rights))
            {
                _grantedAccessCache[cacheKey] = true;
                return;
            }

            SemaphoreSlim pathLock = _pathGrantLocks.GetOrAdd(keyPath, _ => new SemaphoreSlim(1, 1));
            await pathLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_grantedAccessCache.ContainsKey(cacheKey)) return;
                await _aclMutationGate.WaitAsync(ct).ConfigureAwait(false);
                // Traverse-only grants (non-inheritable RX on a directory) are written without
                // re-propagating the directory's existing inheritable ACEs to its whole subtree.
                // icacls/SetNamedSecurityInfo propagation over Documents, LocalAI or a large
                // repository took >15 s per ancestor and dominated the first command of every task.
                if (!replaceExisting && rights == "(RX)" && Directory.Exists(cleanPath) && !IsAclCircuitOpen()
                    && TryGrantTraverseWithoutPropagation(cleanPath, sid))
                {
                    _grantedAccessCache[cacheKey] = true;
                    _aclMutationGate.Release();
                    return;
                }
                try
                {
                    if (required && IsAclCircuitOpen() && Volatile.Read(ref _aclBlockingOperation) is Task blocking)
                    {
                        // A required grant waits briefly for the stuck operation to finish instead
                        // of failing the command outright.
                        await Task.WhenAny(blocking, Task.Delay(TimeSpan.FromSeconds(30), ct)).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                    }
                    if (IsAclCircuitOpen())
                    {
                        if (required)
                            throw new TimeoutException("ACL updates are disabled after a previous Windows security update timed out at "
                                + (Volatile.Read(ref _aclMutationTimedOutPath) ?? "an unknown path") + ".");
                        return;
                    }

                    // Optional grants include workspace ancestors and user tooling caches. Their
                    // native SetAccessControl call can hang on a watched parent directory; a
                    // timeout would trip the process-wide circuit and prevent the later required
                    // sandbox grants. Use the bounded, killable icacls path for these grants.
                    ManagedAclGrantResult managedResult = required
                        ? await TryGrantViaManagedAclAsync(cleanPath, sid, rights, replaceExisting).ConfigureAwait(false)
                        : ManagedAclGrantResult.Failed;
                    if (managedResult == ManagedAclGrantResult.Granted)
                    {
                        _grantedAccessCache[cacheKey] = true;
                        return;
                    }

                    if (managedResult == ManagedAclGrantResult.TimedOut)
                    {
                        // SetAccessControl is a non-cancellable native call. Do not start icacls
                        // against the same object while that abandoned call may still be running,
                        // and prevent later grants from accumulating more blocked native threads.
                        TripAclCircuit(cleanPath, Volatile.Read(ref _lastManagedAclTimeout));
                        if (required)
                            throw new TimeoutException("Timed out while granting AppContainer access to " + cleanPath);
                        return;
                    }

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                    var info = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    info.ArgumentList.Add(cleanPath);
                    info.ArgumentList.Add(replaceExisting ? "/grant:r" : "/grant");
                    info.ArgumentList.Add("*" + sid + ":" + rights);
                    info.ArgumentList.Add("/Q");

                    Process? process = null;
                    try
                    {
                        process = new Process { StartInfo = info };
                        process.Start();
                        process.StandardInput.Close();
                        using var cancelReg = timeoutCts.Token.Register(() => { try { process?.Kill(true); } catch { } });
                        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                        Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(timeoutCts.Token)).ConfigureAwait(false);
                        string stdout = await stdoutTask.ConfigureAwait(false);
                        string stderr = await stderrTask.ConfigureAwait(false);
                        if (process.ExitCode == 0)
                        {
                            _grantedAccessCache[cacheKey] = true;
                        }
                        else if (required)
                        {
                            throw new InvalidOperationException("Could not grant AppContainer access to " + cleanPath + ": " + (stderr + stdout).Trim());
                        }
                        else
                        {
                            RecordOptionalGrantFailure(backoffKey, DateTime.UtcNow);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        KillAclChildAndTripCircuitIfNeeded(process, cleanPath);
                        if (required)
                            throw new TimeoutException("Timed out while granting AppContainer access to " + cleanPath);
                        RecordOptionalGrantFailure(backoffKey, DateTime.UtcNow);
                    }
                    catch (Exception) when (!required && !ct.IsCancellationRequested)
                    {
                        KillAclChildAndTripCircuitIfNeeded(process, cleanPath);
                        RecordOptionalGrantFailure(backoffKey, DateTime.UtcNow);
                    }
                    catch
                    {
                        KillAclChildAndTripCircuitIfNeeded(process, cleanPath);
                        if (required) throw;
                    }
                    finally
                    {
                        process?.Dispose();
                    }
                }
                finally
                {
                    _aclMutationGate.Release();
                }
            }
            finally
            {
                pathLock.Release();
            }
        }

        /// <summary>
        /// Runs the managed ACL grant on a background thread and abandons the wait after
        /// <see cref="ManagedAclGrantTimeout"/>. SetAccessControl/SetSecurityInfoByName is a
        /// synchronous, non-cancellable Win32 call. On timeout, the caller must not launch an
        /// icacls fallback against the same object because the abandoned native call may continue.
        /// </summary>
        private static async Task<ManagedAclGrantResult> TryGrantViaManagedAclAsync(string cleanPath, string sid, string rights, bool replaceExisting = false)
        {
            // Use a dedicated background thread rather than Task.Run: if the native call blocks,
            // the abandoned work must not consume a shared ThreadPool worker. The global mutation
            // circuit breaker prevents subsequent calls from accumulating more blocked threads.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                bool result;
                try
                {
                    if (Directory.Exists(cleanPath))
                    {
                        var directoryInfo = new DirectoryInfo(cleanPath);
                        var directorySecurity = directoryInfo.GetAccessControl();
                        var securityIdentifier = new SecurityIdentifier(sid);

                        FileSystemRights fsRights;
                        if (rights.Contains("(M)", StringComparison.OrdinalIgnoreCase))
                        {
                            fsRights = FileSystemRights.Modify | FileSystemRights.Synchronize;
                        }
                        else
                        {
                            fsRights = FileSystemRights.ReadAndExecute | FileSystemRights.Traverse;
                        }

                        InheritanceFlags inheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                        if (!rights.Contains("(OI)", StringComparison.OrdinalIgnoreCase) && !rights.Contains("(CI)", StringComparison.OrdinalIgnoreCase))
                        {
                            inheritanceFlags = InheritanceFlags.None;
                        }

                        var rule = new FileSystemAccessRule(
                            securityIdentifier,
                            fsRights,
                            inheritanceFlags,
                            PropagationFlags.None,
                            AccessControlType.Allow);

                        if (replaceExisting)
                        {
                            RemoveAppContainerWriteRules(directorySecurity);
                            directorySecurity.SetAccessRule(rule);
                        }
                        else
                        {
                            directorySecurity.AddAccessRule(rule);
                        }
                        directoryInfo.SetAccessControl(directorySecurity);
                        result = true;
                    }
                    else if (File.Exists(cleanPath))
                    {
                        var fileInfo = new FileInfo(cleanPath);
                        var fileSecurity = fileInfo.GetAccessControl();
                        var securityIdentifier = new SecurityIdentifier(sid);

                        FileSystemRights fsRights = rights.Contains("(M)", StringComparison.OrdinalIgnoreCase)
                            ? (FileSystemRights.Modify | FileSystemRights.Synchronize)
                            : (FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);

                        var rule = new FileSystemAccessRule(
                            securityIdentifier,
                            fsRights,
                            InheritanceFlags.None,
                            PropagationFlags.None,
                            AccessControlType.Allow);

                        fileSecurity.AddAccessRule(rule);
                        fileInfo.SetAccessControl(fileSecurity);
                        result = true;
                    }
                    else
                    {
                        result = false;
                    }
                }
                catch
                {
                    result = false;
                }
                tcs.TrySetResult(result);
            })
            {
                IsBackground = true,
                Name = "PaRuX ACL grant"
            };
            worker.Start();

            Task first = await Task.WhenAny(tcs.Task, Task.Delay(ManagedAclGrantTimeout)).ConfigureAwait(false);
            if (first != tcs.Task)
            {
                Volatile.Write(ref _lastManagedAclTimeout, tcs.Task);
                return ManagedAclGrantResult.TimedOut;
            }
            return await tcs.Task.ConfigureAwait(false)
                ? ManagedAclGrantResult.Granted
                : ManagedAclGrantResult.Failed;
        }

        /// <summary>First host PATH directory containing each executable, excluding WindowsApps execution aliases.</summary>
        internal static IReadOnlyList<string> ResolveHostToolDirectories(IEnumerable<string> executables, string? hostPath = null)
        {
            var result = new List<string>();
            string[] entries = SanitizeSearchPath(hostPath ?? Environment.GetEnvironmentVariable("PATH")).Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (string executable in executables)
            {
                foreach (string entry in entries)
                {
                    if (entry.Contains(@"\Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                    string directory = entry.TrimEnd('\\', '/');
                    bool exists;
                    try { exists = File.Exists(Path.Combine(directory, executable)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { exists = false; }
                    if (!exists) continue;
                    // Review finding: an inheritable read grant must never land on a broad folder that
                    // merely happens to hold an interpreter. Only a recognisable install layout counts.
                    if (IsBroadUserFolder(directory) || !LooksLikeInterpreterInstall(directory, executable)) break;
                    if (!result.Contains(directory, StringComparer.OrdinalIgnoreCase)) result.Add(directory);
                    break;
                }
            }
            return result;
        }

        private static bool IsBroadUserFolder(string directory)
        {
            string full;
            try { full = Path.GetFullPath(directory).TrimEnd('\\', '/'); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
            if (Path.GetPathRoot(full)?.TrimEnd('\\', '/').Equals(full, StringComparison.OrdinalIgnoreCase) == true) return true;
            foreach (Environment.SpecialFolder folder in new[]
                     {
                         Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop,
                         Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
                         Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows
                     })
            {
                string special = Environment.GetFolderPath(folder).TrimEnd('\\', '/');
                if (special.Length > 0 && special.Equals(full, StringComparison.OrdinalIgnoreCase)) return true;
            }
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return downloads.Equals(full, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool LooksLikeInterpreterInstall(string directory, string executable)
        {
            try
            {
                if (executable.StartsWith("node", StringComparison.OrdinalIgnoreCase))
                    return File.Exists(Path.Combine(directory, "node_modules", "npm", "package.json"));
                // CPython on Windows: pythonXY.dll next to the executable plus the Lib standard library.
                return Directory.Exists(Path.Combine(directory, "Lib"))
                    && Directory.EnumerateFiles(directory, "python3*.dll").Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        [Flags]
        public enum ToolingEcosystem
        {
            None = 0, DotNet = 1, Node = 2, Python = 4, Java = 8, Rust = 16, Go = 32,
            All = DotNet | Node | Python | Java | Rust | Go
        }

        private static readonly HashSet<string> ShellBuiltins = new(StringComparer.OrdinalIgnoreCase)
        {
            "echo", "dir", "type", "cd", "chdir", "set", "where", "findstr", "more", "copy", "move", "del", "erase",
            "mkdir", "md", "rmdir", "rd", "ren", "rename", "exit", "ver", "vol", "cls", "call", "pushd", "popd", "sort", "find"
        };

        /// <summary>
        /// Which toolchains a command can start, from the first program of each shell segment. Live Chat run:
        /// every non-dotnet command granted every toolchain tree (NuGet cache, Gradle, Cargo, Go, uv, ...) to the
        /// task's fresh AppContainer identity, which cost 72 s of icacls propagation on the first `npm test`.
        /// Unknown programs keep the previous behaviour (all toolchains).
        /// </summary>
        internal static ToolingEcosystem ToolingEcosystemsFor(string command, string? workspaceRoot = null)
        {
            if (string.IsNullOrWhiteSpace(command)) return ToolingEcosystem.All;
            var result = ToolingEcosystem.None;
            var texts = new List<string> { command };
            string? npmScript = CodingNpmScriptRunner.ScriptName(command);
            if (npmScript != null && !string.IsNullOrWhiteSpace(workspaceRoot))
            {
                // The package.json lifecycle bodies decide what `npm test` really starts.
                string? package = null;
                try { package = File.ReadAllText(Path.Combine(workspaceRoot, "package.json")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                string? lifecycle = CodingNpmScriptRunner.LifecycleFingerprint(package, npmScript);
                if (lifecycle != null) texts.AddRange(lifecycle.Split('\u0001').Where(part => part != "\u0000"));
            }
            foreach (string text in texts)
            {
                foreach (string segment in Regex.Split(text, @"&&|\|\||[&|;]"))
                {
                    string trimmed = segment.Trim().TrimStart('@', '(').Trim();
                    if (trimmed.Length == 0) continue;
                    string first;
                    if (trimmed.StartsWith('"'))
                    {
                        int close = trimmed.IndexOf('"', 1);
                        first = close > 1 ? trimmed.Substring(1, close - 1) : trimmed.Trim('"');
                    }
                    else first = trimmed.Split(new[] { ' ', '\t' }, 2)[0];
                    string name = Path.GetFileNameWithoutExtension(first).ToLowerInvariant();
                    result |= name switch
                    {
                        "dotnet" or "msbuild" or "csc" => ToolingEcosystem.DotNet,
                        "node" or "npm" or "npx" or "pnpm" or "yarn" or "corepack" or "tsc" or "vitest" or "jest" or "mocha" or "eslint" => ToolingEcosystem.Node,
                        "python" or "python3" or "py" or "pip" or "pip3" or "uv" or "pytest" or "ruff" => ToolingEcosystem.Python,
                        "java" or "javac" or "gradle" or "gradlew" or "mvn" or "mvnw" or "kotlinc" => ToolingEcosystem.Java,
                        "cargo" or "rustc" => ToolingEcosystem.Rust,
                        "go" => ToolingEcosystem.Go,
                        _ when ShellBuiltins.Contains(name) => ToolingEcosystem.None,
                        _ => ToolingEcosystem.All
                    };
                }
            }
            return result;
        }

        /// <summary>
        /// Read-only toolchain roots a command may need. Credential stores are never candidates: the sandbox
        /// redirects USERPROFILE, GRADLE_USER_HOME and CARGO_HOME to its own home, so a grant on the real
        /// .gradle, .m2 or .cargo folder only exposed their settings and tokens to the AppContainer (CF-02).
        /// </summary>
        internal static HashSet<string> ReadOnlyToolingRootCandidates(string command, string? workspaceRoot = null, bool networkEnabled = false)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ToolingEcosystem ecosystems = ToolingEcosystemsFor(command, workspaceRoot);
            // ToolingEcosystemsFor returns All for a program it does not recognise, which inherits every toolchain.
            bool unknownProgram = ecosystems == ToolingEcosystem.All;
            // A network run executes package install scripts with Internet access. For such a run the sandbox already
            // points the NuGet, npm and Go caches at private folders, so no user-profile cache or global config is granted.
            bool userProfileRoots = !networkEnabled;
            if (ecosystems.HasFlag(ToolingEcosystem.DotNet))
            {
                AddDirectory(candidates, Environment.GetEnvironmentVariable("DOTNET_ROOT"));
                AddDirectory(candidates, Environment.GetEnvironmentVariable("DOTNET_ROOT_X86"));
                AddDirectory(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
                AddDirectory(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"));
                if (userProfileRoots)
                {
                    AddDirectory(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
                    AddDirectory(candidates, Environment.GetEnvironmentVariable("NUGET_PACKAGES"));
                }
            }
            if (ecosystems.HasFlag(ToolingEcosystem.Java))
            {
                AddDirectory(candidates, Environment.GetEnvironmentVariable("JAVA_HOME"));
                AddDirectory(candidates, Environment.GetEnvironmentVariable("ANDROID_HOME"));
                AddDirectory(candidates, Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"));
                // ~/.gradle and ~/.m2 hold repository credentials. Gradle and Maven resolve the sandbox home instead.
            }
            if (ecosystems.HasFlag(ToolingEcosystem.Node))
            {
                AddDirectory(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Volta"));
                AddDirectory(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Volta"));
                AddDirectory(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));
                AddDirectory(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs"));
                // npm's global prefix holds etc\npmrc, which can carry a registry token. Only a local Node command, which may
                // start globally installed tools, receives it. An unknown program and a network run do not.
                if (userProfileRoots && !unknownProgram)
                    AddDirectory(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
                AddDirectory(candidates, Path.Combine(AppPaths.PaRuXRoot, "tooling", "node"));
                AddDirectory(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaRuX", "tooling", "node"));
                string? explicitNodeDir = ResolveExplicitExecutableDirectory(command, "node.exe");
                if (!string.IsNullOrEmpty(explicitNodeDir))
                    AddDirectory(candidates, explicitNodeDir);
                foreach (string toolDirectory in ResolveHostToolDirectories(new[] { "node.exe" }))
                    AddDirectory(candidates, toolDirectory);
            }
            if (ecosystems.HasFlag(ToolingEcosystem.Python))
            {
                AddDirectory(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "uv"));
                AddDirectory(candidates, Path.Combine(AppPaths.PaRuXRoot, "tooling", "python"));
                AddDirectory(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaRuX", "tooling", "python"));
                string? explicitPythonDir = ResolveExplicitExecutableDirectory(command, "python.exe") ?? ResolveExplicitExecutableDirectory(command, "python3.exe");
                if (!string.IsNullOrEmpty(explicitPythonDir))
                    AddDirectory(candidates, explicitPythonDir);
                // Live parity run: a per-user Python (%LOCALAPPDATA%\Programs\Python\PythonXYZ) has no
                // AppContainer ACE, so every Python verifier failed with "Access denied"/9009 and the
                // agent spent the whole task budget hunting for an interpreter. Grant the install
                // directory of interpreters actually on the host PATH (never the WindowsApps aliases).
                foreach (string toolDirectory in ResolveHostToolDirectories(new[] { "python.exe", "python3.exe" }))
                    AddDirectory(candidates, toolDirectory);
            }
            // Rust: ~/.cargo holds registry tokens and is never granted. CARGO_HOME points at the sandbox home.
            if (ecosystems.HasFlag(ToolingEcosystem.Go) && userProfileRoots)
                AddDirectory(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "go"));

            return candidates;
        }

        private static async Task GrantReadOnlyToolingRootsAsync(string sid, string command, CancellationToken ct, string? workspaceRoot = null, bool networkEnabled = false)
        {
            HashSet<string> candidates = ReadOnlyToolingRootCandidates(command, workspaceRoot, networkEnabled);
            var ancestorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in candidates)
            {
                DirectoryInfo? toolingParent = Directory.GetParent(path);
                while (toolingParent != null && toolingParent.Parent != null)
                {
                    string safeParent = toolingParent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    ancestorPaths.Add(safeParent);
                    toolingParent = toolingParent.Parent;
                }
            }

            // Read-only tooling roots and their ancestors contain user caches. Grant only this
            // workspace's AppContainer rather than exposing them to ALL_APPLICATION_PACKAGES.
            await GrantPathsAccessAsync(candidates, sid, "(OI)(CI)(RX)", required: false, ct).ConfigureAwait(false);
            await GrantPathsAccessAsync(ancestorPaths, sid, "(RX)", required: false, ct).ConfigureAwait(false);
        }

        private static readonly HashSet<string> MSBuildDotNetVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            "build", "test", "publish", "pack", "msbuild", "clean", "restore"
        };

        /// <summary>
        /// MSBuild's out-of-process worker nodes cannot create their named pipes inside the
        /// AppContainer (UnauthorizedAccessException, MSB1025); the parent then waits for them until
        /// the command times out. MSBUILDNOOUTOFPROCNODES is not honoured by the dotnet CLI, which
        /// passes -maxcpucount itself, so a single MSBuild-driven dotnet command without an explicit
        /// node count runs in-process with -m:1. Commands with "--" pass-through arguments, an
        /// explicit -m/-maxcpucount or any shell composition are left unchanged.
        /// </summary>
        internal static string ApplyInProcessMSBuild(string command)
        {
            if (!IsSingleDotNetCommand(command)) return command;
            string[] tokens = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || !MSBuildDotNetVerbs.Contains(tokens[1])) return command;
            // `dotnet test Tests.dll` runs VSTest directly, which rejects MSBuild switches.
            if (tokens.Length > 2 && (tokens[2].TrimEnd('"').EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || tokens[2].TrimEnd('"').EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                return command;
            foreach (string token in tokens.Skip(2))
            {
                string lower = token.ToLowerInvariant();
                if (lower == "--" || lower is "-m" or "/m" || lower.StartsWith("-m:", StringComparison.Ordinal)
                    || lower.StartsWith("/m:", StringComparison.Ordinal) || lower.StartsWith("-maxcpucount", StringComparison.Ordinal)
                    || lower.StartsWith("/maxcpucount", StringComparison.Ordinal) || lower.StartsWith("--maxcpucount", StringComparison.Ordinal))
                    return command;
            }
            return command.TrimEnd() + " -m:1";
        }

        internal static bool IsSingleDotNetCommand(string command)
        {
            string text = command?.TrimStart() ?? string.Empty;
            bool startsWithDotNet = text.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("dotnet ", StringComparison.OrdinalIgnoreCase)
                || text.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("dotnet.exe ", StringComparison.OrdinalIgnoreCase);
            return startsWithDotNet && text.IndexOfAny(new[] { '&', '|', '<', '>', '\r', '\n' }) < 0;
        }

        internal static bool CommandMayNeedReadOnlyToolingRoots(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return true;

            bool inQuotes = false;
            bool escaped = false;
            bool sawCommand = false;
            var segment = new StringBuilder();

            bool IsExternalCommand(string rawSegment)
            {
                string value = rawSegment.Trim();
                if (value.Length == 0) return false;

                int start = 0;
                while (start < value.Length && value[start] == '@') start++;
                while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
                if (start < value.Length && (value[start] is '<' or '>' or '(' or ')')) return true;

                int end;
                if (start < value.Length && value[start] == '"')
                {
                    end = value.IndexOf('"', start + 1);
                    if (end < 0) return true;
                    start++;
                }
                else
                {
                    end = start;
                    while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] is not ('<' or '>')) end++;
                }

                string commandName = value[start..end];
                return commandName.Length == 0 || !CmdBuiltinsWithoutToolingAccess.Contains(commandName);
            }

            for (int i = 0; i < command.Length; i++)
            {
                char current = command[i];
                if (escaped)
                {
                    segment.Append(current);
                    escaped = false;
                    continue;
                }
                if (current == '^')
                {
                    segment.Append(current);
                    escaped = true;
                    continue;
                }
                if (current == '"') inQuotes = !inQuotes;
                if (!inQuotes && (current is '(' or ')')) return true;
                if (!inQuotes && (current is '&' or '|'))
                {
                    string part = segment.ToString();
                    if (IsExternalCommand(part)) return true;
                    if (!string.IsNullOrWhiteSpace(part)) sawCommand = true;
                    segment.Clear();
                    continue;
                }
                segment.Append(current);
            }

            if (escaped || inQuotes) return true;
            string lastPart = segment.ToString();
            if (IsExternalCommand(lastPart)) return true;
            if (!string.IsNullOrWhiteSpace(lastPart)) sawCommand = true;
            return !sawCommand;
        }

        /// <summary>
        /// The directory of an interpreter that the command names by a literal path. The command text comes from
        /// the model, so the path is a grant candidate only when it is a real interpreter layout inside a trusted
        /// install root and outside every credential store. A literal such as "C:\Users\me\.ssh\python.exe" never
        /// becomes a persistent ACE on that folder (SC-05). The string checks run before any filesystem access, so a
        /// UNC path cannot make the sandbox contact a remote host.
        /// </summary>
        internal static string? ResolveExplicitExecutableDirectory(string command, string expectedExeName, IEnumerable<string>? trustedRoots = null)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string trimmed = command.Trim().TrimStart('@', '(').Trim();
            if (trimmed.Length == 0) return null;
            string first;
            if (trimmed.StartsWith('"'))
            {
                int close = trimmed.IndexOf('"', 1);
                first = close > 1 ? trimmed.Substring(1, close - 1) : trimmed.Trim('"');
            }
            else
            {
                first = trimmed.Split(new[] { ' ', '\t' }, 2)[0];
            }
            if (!string.IsNullOrWhiteSpace(first) && Path.IsPathRooted(first))
            {
                string fileName = Path.GetFileName(first);
                if (string.Equals(fileName, expectedExeName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileNameWithoutExtension(fileName), Path.GetFileNameWithoutExtension(expectedExeName), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string executable = Path.GetFullPath(first);
                        string? dir = Path.GetDirectoryName(executable);
                        if (string.IsNullOrEmpty(dir) || IsBroadUserFolder(dir) || IsCredentialStorePath(dir)) return null;
                        string directory = dir;
                        if (!(trustedRoots ?? TrustedExplicitToolRoots()).Any(root => IsStrictlyUnder(directory, root))) return null;
                        if (!File.Exists(executable)) return null;
                        return LooksLikeInterpreterInstall(directory, expectedExeName) ? directory : null;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        return null;
                    }
                }
            }
            return null;
        }

        // Folder names of credential and token stores. Nothing at or below them is granted to the sandbox,
        // whether the path came from an environment variable, a junction or a command literal.
        private static readonly HashSet<string> CredentialStoreFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".ssh", ".gnupg", ".aws", ".azure", ".docker", ".kube", ".gradle", ".m2", ".cargo", "Credentials"
        };

        internal static bool IsCredentialStorePath(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
            return full.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => CredentialStoreFolderNames.Contains(segment));
        }

        /// <summary>
        /// Install roots a user or PaRuX installs toolchains into. An interpreter named by a command literal is
        /// granted only from inside one of these roots; PATH-discovered interpreters are handled by
        /// <see cref="ResolveHostToolDirectories"/>.
        /// </summary>
        internal static IReadOnlyList<string> TrustedExplicitToolRoots()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(local, "Programs"),
                Path.Combine(local, "Volta"),
                Path.Combine(local, "PaRuX", "tooling"),
                Path.Combine(AppPaths.PaRuXRoot, "tooling"),
            }.Where(root => Path.IsPathFullyQualified(root)).ToArray();
        }

        /// <summary>True when <paramref name="directory"/> is a folder strictly inside <paramref name="root"/> (the root itself does not count).</summary>
        internal static bool IsStrictlyUnder(string directory, string root)
        {
            string child = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return child.Length > parent.Length + 1
                && child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when <paramref name="fullPath"/> is a credential store or reaches one through a link. A grant on
        /// "C:\tools\link\sub" is applied through the junction "link", so every ancestor that is a reparse point is
        /// resolved and its final target is checked the same way. A link that cannot be resolved fails closed.
        /// </summary>
        internal static bool ReachesCredentialStore(string fullPath, int depth = 0)
        {
            if (IsCredentialStorePath(fullPath)) return true;
            if (depth > MaxLinkResolutionDepth) return true;
            try
            {
                for (string? current = fullPath; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0) continue;
                    var linkTarget = Directory.ResolveLinkTarget(current, returnFinalTarget: true);
                    if (linkTarget == null || ReachesCredentialStore(linkTarget.FullName, depth + 1)) return true;
                }
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                return true;
            }
        }

        private const int MaxLinkResolutionDepth = 8;

        /// <summary>
        /// Adds a folder to a grant set. Links are resolved before anything is added: a junction or symlink into a
        /// credential store adds nothing, not even its own path, because a grant on a link reaches the folder it points to (SC-05).
        /// </summary>
        internal static void AddDirectory(ISet<string> target, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                string full = Path.GetFullPath(value);
                if (!Directory.Exists(full) || ReachesCredentialStore(full)) return;
                string? linkedTarget = Directory.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName;
                target.Add(full);
                if (linkedTarget != null) target.Add(linkedTarget);
            }
            catch { }
        }

        internal static bool IsSecretEnvironmentName(string key) =>
            SecretEnvironmentMarkers.Any(marker => key.Contains(marker, StringComparison.OrdinalIgnoreCase));

        internal static bool IsBlockedEnvironmentName(string key) =>
            BlockedEnvironmentNames.Contains(key);

        /// <summary>
        /// Keeps only absolute PATH entries. An empty, "." or relative entry is resolved against the
        /// agent-writable working directory and would undo NoDefaultCurrentDirectoryInExePath.
        /// </summary>
        internal static string SanitizeSearchPath(string? searchPath) =>
            string.Join(";", (searchPath ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim().Trim('"'))
                .Where(entry => entry.Length > 2 && Path.IsPathFullyQualified(entry)));

        /// <summary>
        /// Live parity run: every Node verifier failed with "EPERM: lstat 'C:\'" because Node's module
        /// loader resolves real paths by lstat-ing each ancestor up to the drive root, and a default
        /// Windows drive root grants the AppContainer no read-attributes right (changing that needs
        /// administrator rights on a system object). --preserve-symlinks(-main) skips that walk. pnpm's
        /// symlinked node_modules layout relies on real-path resolution, so a pnpm workspace keeps the
        /// default behaviour. Existing NODE_OPTIONS are preserved.
        /// </summary>
        internal static string? NodeOptionsForSandbox(string? existing, string? workspaceRoot, string? command = null)
        {
            if (!string.IsNullOrWhiteSpace(workspaceRoot)
                && Directory.Exists(Path.Combine(workspaceRoot, "node_modules", ".pnpm")))
                return existing;
            // npm/npx/pnpm/yarn run the real-path walk themselves: with these flags they hung until the
            // command timeout instead of failing fast with EPERM, so package managers keep the default.
            if (CodingNpmScriptRunner.StartsPackageManager(command)) return existing;
            string current = existing?.Trim() ?? string.Empty;
            var flags = new List<string> { "--preserve-symlinks", "--preserve-symlinks-main" };
            // Live UI check: `node --test` hung until the 10-minute timeout because its per-file child
            // isolation never completes inside the AppContainer; in-process isolation works. Only the
            // PaRuX-managed Node (first on the sandbox PATH, v24) is known to accept this option: an
            // older Node would refuse to start with an unknown NODE_OPTIONS flag.
            if (File.Exists(Path.Combine(AppPaths.PaRuXRoot, "tooling", "node", "node.exe")))
                flags.Add("--test-isolation=none");
            foreach (string flag in flags)
            {
                bool present = current.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(part => string.Equals(part, flag, StringComparison.Ordinal));
                if (!present) current = (current + " " + flag).Trim();
            }
            return current;
        }

        private static IntPtr BuildSanitizedEnvironment(
            string sandboxHome,
            string sandboxTemp,
            bool preferPrivatePackageCaches,
            string? workspaceRoot = null,
            string? command = null)
        {
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
            {
                string key = item.Key?.ToString() ?? string.Empty;
                string value = item.Value?.ToString() ?? string.Empty;
                if (key.Length == 0 || IsSecretEnvironmentName(key) || IsBlockedEnvironmentName(key)) continue;
                values[key] = value;
            }

            string sandboxAppData = Path.Combine(sandboxHome, "AppData", "Roaming");
            string sandboxLocalAppData = Path.Combine(sandboxHome, "AppData", "Local");
            string sandboxConfig = Path.Combine(sandboxHome, ".config");
            string sandboxCache = Path.Combine(sandboxHome, ".cache");
            Directory.CreateDirectory(sandboxAppData);
            Directory.CreateDirectory(sandboxLocalAppData);
            Directory.CreateDirectory(sandboxConfig);
            Directory.CreateDirectory(sandboxCache);

            values["HOME"] = sandboxHome;
            values["USERPROFILE"] = sandboxHome;
            values["APPDATA"] = sandboxAppData;
            values["LOCALAPPDATA"] = sandboxLocalAppData;
            values["XDG_CONFIG_HOME"] = sandboxConfig;
            values["XDG_CACHE_HOME"] = sandboxCache;
            values["TEMP"] = sandboxTemp;
            values["TMP"] = sandboxTemp;
            values["DOTNET_CLI_HOME"] = sandboxHome;
            values["npm_config_cache"] = Path.Combine(sandboxHome, ".npm");
            values["NPM_CONFIG_USERCONFIG"] = Path.Combine(sandboxHome, ".npmrc");
            string toolingNode = Path.Combine(AppPaths.PaRuXRoot, "tooling", "node");
            if (Directory.Exists(toolingNode))
            {
                string existingPath = values.TryGetValue("PATH", out string? p) ? p : string.Empty;
                values["PATH"] = toolingNode + ";" + existingPath;
            }
            string voltaHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Volta");
            if (Directory.Exists(voltaHome)) values["VOLTA_HOME"] = voltaHome;
            values["GRADLE_USER_HOME"] = Path.Combine(sandboxHome, ".gradle");
            values["CARGO_HOME"] = Path.Combine(sandboxHome, ".cargo");
            values["GOMODCACHE"] = Path.Combine(sandboxHome, "go", "pkg", "mod");
            values["COMPOSER_HOME"] = Path.Combine(sandboxHome, ".composer");
            values["ANDROID_USER_HOME"] = Path.Combine(sandboxHome, ".android");
            values["PIP_CONFIG_FILE"] = "NUL";
            values["GIT_CONFIG_NOSYSTEM"] = "1";
            values["GIT_TERMINAL_PROMPT"] = "0";
            values["GCM_INTERACTIVE"] = "Never";

            string privateNuget = Path.Combine(sandboxHome, ".nuget", "packages");
            bool privateNugetExists = Directory.Exists(privateNuget)
                && SafeDirectoryHasEntries(privateNuget);
            if (preferPrivatePackageCaches || privateNugetExists)
            {
                Directory.CreateDirectory(privateNuget);
                values["NUGET_PACKAGES"] = privateNuget;
            }
            else
            {
                string? nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
                if (string.IsNullOrWhiteSpace(nuget))
                    nuget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
                values["NUGET_PACKAGES"] = nuget;
            }

            string dotnetProg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            if (Directory.Exists(dotnetProg) && !values.ContainsKey("DOTNET_ROOT"))
            {
                values["DOTNET_ROOT"] = dotnetProg;
            }

            // Make package-manager behavior quieter, deterministic and non-hanging. These do not grant
            // credentials and do not weaken the AppContainer boundary.
            values["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            values["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
            values["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            values["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
            values["MSBUILDDISABLENODEREUSE"] = "1";
            values["MSBUILD_NO_SERVER"] = "1";
            values["MSBUILDNOOUTOFPROCNODES"] = "1";
            values["UseSharedCompilation"] = "false";
            values["VBCSCOMPILER_DISABLED"] = "1";
            values["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            // cmd.exe must not resolve bare program names from the agent-writable working directory:
            // a planted dotnet.cmd would otherwise stand in for the real tool during verification.
            values["NoDefaultCurrentDirectoryInExePath"] = "1";
            values["DOTNET_EnableDiagnostics"] = "0";
            values["COMPlus_EnableDiagnostics"] = "0";
            values["DOTNET_NOLOGO"] = "1";
            values["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
            values["NUGET_CERT_REVOCATION_MODE"] = "offline";
            values["npm_config_audit"] = "false";
            values["npm_config_fund"] = "false";
            values["CI"] = "1";
            values["NONINTERACTIVE"] = "1";
            if (values.TryGetValue("PATH", out string? searchPath)) values["PATH"] = SanitizeSearchPath(searchPath);
            string? nodeOptions = NodeOptionsForSandbox(values.TryGetValue("NODE_OPTIONS", out string? existingNodeOptions) ? existingNodeOptions : null, workspaceRoot, command);
            if (nodeOptions != null) values["NODE_OPTIONS"] = nodeOptions;

            string block = string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
            return Marshal.StringToHGlobalUni(block);
        }

        private static bool SafeDirectoryHasEntries(string path)
        {
            try { return Directory.EnumerateFileSystemEntries(path).Take(1).Any(); }
            catch { return false; }
        }

        private static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
                return job;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        /// <summary>Returns true when the process exited, or false when its log passed the hard cap first (CF-03).</summary>
        private static async Task<bool> WaitForProcessAsync(IntPtr process, CancellationToken ct, string outputPath)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                uint result = WaitForSingleObject(process, 100);
                if (result == WaitObject0) return true;
                if (result != WaitTimeout)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Waiting for the sandboxed process failed.");
                if (OutputExceedsCap(outputPath)) return false;
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }

        private static bool OutputExceedsCap(string path)
        {
            try { return new FileInfo(path).Length > MaxSandboxOutputBytes; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        /// <summary>Bounded wait for a process just terminated, so its output handle is released before the log is read.</summary>
        private static async Task WaitForTerminatedAsync(IntPtr process)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (WaitForSingleObject(process, 0) == WaitObject0) return;
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reads a log as a bounded head and tail. Memory use is capped whatever the command wrote, and the middle is
        /// replaced by a notice that says how many bytes were left out.
        /// </summary>
        internal static BoundedOutput ReadBoundedOutput(string path, int headBytes = SandboxOutputHeadBytes, int tailBytes = SandboxOutputTailBytes)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            if (length <= (long)headBytes + tailBytes)
                return new BoundedOutput(DecodeOutput(ReadRange(stream, 0, (int)length)), Truncated: false);
            byte[] head = ReadRange(stream, 0, headBytes);
            byte[] tail = ReadRange(stream, length - tailBytes, tailBytes);
            long omitted = length - head.Length - tail.Length;
            return new BoundedOutput(
                DecodeOutput(head) + "\r\n[... " + omitted + " bytes of command output omitted ...]\r\n" + DecodeOutput(tail),
                Truncated: true);
        }

        private static byte[] ReadRange(FileStream stream, long offset, int count)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[count];
            int filled = 0;
            while (filled < count)
            {
                int read = stream.Read(buffer, filled, count - filled);
                if (read == 0) break;
                filled += read;
            }
            return filled == count ? buffer : buffer[..filled];
        }

        private static string DecodeOutput(byte[] bytes)
        {
            string text = Encoding.UTF8.GetString(bytes);
            return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        }

        private static BoundedOutput TryReadBoundedOutput(string path)
        {
            try { return File.Exists(path) ? ReadBoundedOutput(path) : new BoundedOutput(string.Empty, Truncated: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The failure text is the evidence the repair loop needs, so say that it could not be read.
                return new BoundedOutput("[sandbox output could not be read: " + ex.Message + "]", Truncated: false);
            }
        }

        private static string SafeRead(string path) => TryReadBoundedOutput(path).Text;

        private static string EscapeBatch(string value) => value.Replace("%", "%%");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public ushort wShowWindow;
            public ushort cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_CAPABILITIES
        {
            public IntPtr AppContainerSid;
            public IntPtr Capabilities;
            public int CapabilityCount;
            public int Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SID_AND_ATTRIBUTES
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int CreateAppContainerProfile(
            string pszAppContainerName, string pszDisplayName, string pszDescription,
            IntPtr pCapabilities, uint dwCapabilityCount, out IntPtr ppSidAppContainerSid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int DeriveAppContainerSidFromAppContainerName(
            string pszAppContainerName, out IntPtr ppsidAppContainerSid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int DeleteAppContainerProfile(string pszAppContainerName);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSidToSidW(string stringSid, out IntPtr sid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr FreeSid(IntPtr pSid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
            IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(
            string? lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
            uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob, uint JobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        private static int _windowStationAndDesktopGranted = 0;

        /// <summary>
        /// AppContainer processes that load user32.dll (such as Node.js or Python C-extensions) connect to
        /// the current session's WindowStation and Desktop during user32 DllMain. If the WindowStation or
        /// Desktop object does not grant access to ALL APPLICATION PACKAGES, NtUserProcessConnect fails with
        /// Access Denied and process initialization terminates with 0xC0000142 (STATUS_DLL_INIT_FAILED).
        /// </summary>
        private static void EnsureWindowStationAndDesktopAppContainerAccess()
        {
            if (!OperatingSystem.IsWindows()) return;
            if (Interlocked.CompareExchange(ref _windowStationAndDesktopGranted, 1, 0) != 0) return;
            try
            {
                GrantUserObjectAppContainerAccess(GetProcessWindowStation());
                GrantUserObjectAppContainerAccess(GetThreadDesktop(GetCurrentThreadId()));
            }
            catch { }
        }

        private static void GrantUserObjectAppContainerAccess(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try
            {
                int si = 4; // DACL_SECURITY_INFORMATION
                GetUserObjectSecurity(handle, ref si, null, 0, out int needed);
                if (needed <= 0) return;
                byte[] sd = new byte[needed];
                if (!GetUserObjectSecurity(handle, ref si, sd, needed, out _)) return;
                var raw = new RawSecurityDescriptor(sd, 0);
                if (raw.DiscretionaryAcl == null) return;
                var sid = new SecurityIdentifier("S-1-15-2-1"); // ALL APPLICATION PACKAGES
                foreach (GenericAce ace in raw.DiscretionaryAcl)
                {
                    if (ace is CommonAce common && common.SecurityIdentifier == sid && (common.AccessMask & 0x10000000) != 0)
                        return;
                }
                raw.DiscretionaryAcl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, unchecked((int)0x10000000), sid, false, null));
                byte[] bin = new byte[raw.BinaryLength];
                raw.GetBinaryForm(bin, 0);
                SetUserObjectSecurity(handle, ref si, bin);
            }
            catch { }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetProcessWindowStation();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetThreadDesktop(int dwThreadId);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetUserObjectSecurity(IntPtr hObj, ref int pSIRequested, byte[]? pSD, int nLength, out int lpnLengthNeeded);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetUserObjectSecurity(IntPtr hObj, ref int pSIRequested, byte[] pSD);
    }
}
