using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace PaRuX.UITests.Infrastructure;

/// <summary>
/// Launches the REAL PaRuX desktop app (real Core, real coding worker, real llama.cpp model) on a
/// pre-provisioned isolated data root for the opt-in <c>RealCodingTask</c> acceptance test. It is a
/// separate class from <see cref="UiTestApplication"/> on purpose: that harness seeds fixtures and
/// stubs, this one must not. Nothing here touches an installed PaRuX: the data root, state dir and
/// model/runtime layout all come from the caller's isolated environment, and cleanup kills only the
/// process tree this class launched.
/// </summary>
internal sealed class RealCodingProduct : IDisposable
{
    internal const string AppVariable = "PARUX_REAL_CODING_APP";
    internal const string EnvRootVariable = "PARUX_REAL_CODING_ENV";
    internal const string ArtifactsVariable = "PARUX_REAL_CODING_ARTIFACTS";

    private readonly Application _application;
    private readonly Application _launcher;
    private readonly Process _launcherProcess;
    private readonly OwnedProcessIdentity _launcherIdentity;
    private readonly Dictionary<int, OwnedProcessIdentity> _ownedProcesses = new();
    private readonly Dictionary<int, Process> _ownedProcessHandles = new();
    private bool _disposed;

    internal UIA3Automation Automation { get; }
    internal Window MainWindow { get; }
    internal string DataRoot { get; }
    internal string ArtifactDirectory { get; }
    internal string LogPath => Path.Combine(DataRoot, "parux.log");

    internal static string RequireVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                "This opt-in real-model test needs " + name + ". It never falls back to an installed PaRuX or the user's data.");

    internal RealCodingProduct(string artifactDirectory, string workspaceRoot)
    {
        string appPath = Path.GetFullPath(RequireVariable(AppVariable));
        string envRoot = Path.GetFullPath(RequireVariable(EnvRootVariable));
        DataRoot = Path.Combine(envRoot, "data");
        ArtifactDirectory = artifactDirectory;
        Directory.CreateDirectory(artifactDirectory);
        if (!File.Exists(appPath)) throw new FileNotFoundException("PaRuX layout under test was not found.", appPath);
        if (!File.Exists(Path.Combine(DataRoot, "localai-paths.json")))
            throw new InvalidOperationException("The isolated environment lacks data/localai-paths.json; provision it first.");

        // Fresh settings each run (onboarding done, quiet UI, the disposable repository as the code
        // folder). The persisted standard-role model assignment lives in the isolated LocalAI root.
        File.WriteAllText(Path.Combine(DataRoot, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["OnboardingCompleted"] = true,
            ["StudioModeEnabled"] = false,
            ["BubbleMode"] = "Disabled",
            ["AgentCodingWorkspaceRoot"] = workspaceRoot
        }));
        string bootstrap = Path.Combine(DataRoot, "server", "bootstrap.json");
        if (File.Exists(bootstrap)) File.Delete(bootstrap);

        var start = new ProcessStartInfo(appPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appPath)!
        };
        start.Environment["PARUX_DATA_ROOT"] = DataRoot;
        start.Environment["PARUX_LOCALAI_STATE_DIR"] = Path.Combine(envRoot, "state");
        start.Environment["PARUX_UI_TESTS"] = "0";
        Process launchedProcess = Process.Start(start) ?? throw new InvalidOperationException("Could not launch PaRuX.");
        _launcherProcess = launchedProcess;
        Application? launcher = null;
        OwnedProcessIdentity launchedIdentity;
        try
        {
            // Pin the OS process handle before asking for metadata. Early startup can briefly make
            // MainModule unavailable; retries stay on this handle and never reopen a PID.
            _ = launchedProcess.SafeHandle;
            launcher = new Application(launchedProcess);
            if (!OwnedProcessLineage.TryCaptureWhenReady(launchedProcess, TimeSpan.FromSeconds(3), out launchedIdentity))
                throw new InvalidOperationException("Cannot establish identity for the newly launched PaRuX process.");
        }
        catch
        {
            OwnedProcessLineage.TerminateExactProcess(launchedProcess, TimeSpan.FromSeconds(2));
            try { launcher?.Dispose(); } catch { }
            try { launchedProcess.Dispose(); } catch { }
            throw;
        }
        _launcher = launcher ?? throw new InvalidOperationException("PaRuX launcher handle was not created.");
        _launcherIdentity = launchedIdentity;
        Application? application = null;
        UIA3Automation? automation = null;
        try
        {
            _ownedProcesses.Add(_launcherIdentity.ProcessId, _launcherIdentity);
            _ownedProcessHandles.Add(_launcherIdentity.ProcessId, launchedProcess);
            if (!string.Equals(_launcherIdentity.ImagePath, appPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Launched PaRuX process image did not match the requested application path.");

            UiWait.For(() =>
            {
                CaptureOwnedDescendants();

                if (OwnedProcessLineage.TryCapture(_launcherProcess, out OwnedProcessIdentity liveLauncher) &&
                    OwnedProcessLineage.IsSameIdentity(_launcherIdentity, liveLauncher) &&
                    _launcher.MainWindowHandle != IntPtr.Zero)
                {
                    application = _launcher;
                    return true;
                }
                foreach (OwnedProcessIdentity descendant in _ownedProcesses.Values)
                {
                    if (descendant.ProcessId == _launcherIdentity.ProcessId ||
                        !string.Equals(descendant.ImagePath, appPath, StringComparison.OrdinalIgnoreCase)) continue;
                    OwnedProcessIdentity? verified = OwnedProcessLineage.CaptureDescendants(_launcherIdentity)
                        .Where(current => OwnedProcessLineage.IsSameIdentity(descendant, current))
                        .Select(current => (OwnedProcessIdentity?)current)
                        .FirstOrDefault();
                    if (!verified.HasValue) continue;
                    if (!TryOpenVerifiedProcess(verified.Value, out Process? attachedProcess)) continue;
                    application = Application.Attach(attachedProcess);
                    return true;
                }
                return false;
            }, "PaRuX desktop process", TimeSpan.FromSeconds(60));

            _application = application ?? throw new InvalidOperationException("PaRuX did not create a desktop process.");
            if (!_ownedProcessHandles.TryGetValue(_application.ProcessId, out Process? applicationProcess) ||
                !OwnedProcessLineage.TryCapture(applicationProcess, out OwnedProcessIdentity applicationIdentity))
                throw new InvalidOperationException("Could not verify the attached PaRuX process handle.");
            if (!_ownedProcesses.TryGetValue(applicationIdentity.ProcessId, out OwnedProcessIdentity observedIdentity) ||
                !OwnedProcessLineage.IsSameIdentity(observedIdentity, applicationIdentity))
                throw new InvalidOperationException("Attached desktop process is not part of the launched PaRuX process lineage.");
            automation = new UIA3Automation();
            Automation = automation;
            Window? mainWindow = null;
            UiWait.For(() =>
            {
                mainWindow = _application.GetAllTopLevelWindows(Automation)
                    .FirstOrDefault(window => string.Equals(window.Title, "PaRuX AI & Gaming OS", StringComparison.Ordinal));
                return mainWindow != null;
            }, "PaRuX main window", TimeSpan.FromSeconds(60));
            MainWindow = mainWindow ?? throw new InvalidOperationException("PaRuX main window was not found.");
            try { MainWindow.Patterns.Window.PatternOrDefault?.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized); }
            catch { /* continue with the launched size */ }
            MainWindow.SetForeground();
            MainWindow.Focus();
        }
        catch
        {
            try { automation?.Dispose(); } catch { }
            try { CleanupOwnedProcesses(); } catch { }
            try { application?.Dispose(); } catch { }
            try { _launcher.Dispose(); } catch { }
            DisposeOwnedProcessHandles();
            throw;
        }
    }

    /// <summary>Authenticated Core call through the app's own bootstrap (evidence reads only).</summary>
    internal string CoreGet(string path)
    {
        using JsonDocument boot = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataRoot, "server", "bootstrap.json")));
        string endpoint = boot.RootElement.GetProperty("endpoint").GetString()!;
        string token = boot.RootElement.GetProperty("token").GetString()!;
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.TrimEnd('/') + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = http.Send(request);
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    internal AutomationElement[] FindAllById(string automationId) =>
        MainWindow.FindAllDescendants(cf => cf.ByAutomationId(automationId));

    internal AutomationElement? TryFindById(string automationId) =>
        MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    internal string? Capture(string name)
    {
        try
        {
            MainWindow.SetForeground();
            string path = Path.Combine(ArtifactDirectory, name + ".png");
            MainWindow.Capture().Save(path, ImageFormat.Png);
            return path;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(ArtifactDirectory, name + "-screenshot-skipped.txt"), ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Automation.Dispose(); } catch { }
        try { CleanupOwnedProcesses(); } catch { }
        try { _application.Dispose(); } catch { }
        if (!ReferenceEquals(_application, _launcher))
            try { _launcher.Dispose(); } catch { }
        DisposeOwnedProcessHandles();
    }

    private void CleanupOwnedProcesses()
    {
        // Keep the verified launcher alive while repeatedly discovering and stopping descendants.
        // The deadline bounds teardown if an owned process cannot be inspected or stopped.
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(3))
        {
            CaptureOwnedDescendants();
            OwnedProcessIdentity[] descendants = _ownedProcesses.Values
                .Where(identity => identity.ProcessId != _launcherIdentity.ProcessId).ToArray();
            foreach (OwnedProcessIdentity descendant in descendants)
                KillIfIdentityMatches(descendant, waitTimeoutMs: 0);

            bool descendantsAlive = _ownedProcesses.Values.Any(identity =>
                identity.ProcessId != _launcherIdentity.ProcessId && IsLiveIdentity(identity));
            if (!descendantsAlive) break;
            Thread.Sleep(50);
        }

        // One last verified pass captures any child created as teardown began. Individual
        // identities only are killed; there is no name-based or recursive process-tree kill.
        CaptureOwnedDescendants();
        foreach (OwnedProcessIdentity descendant in _ownedProcesses.Values
                     .Where(identity => identity.ProcessId != _launcherIdentity.ProcessId).ToArray())
            KillIfIdentityMatches(descendant, waitTimeoutMs: 0);
        KillIfIdentityMatches(_launcherIdentity, waitTimeoutMs: 500);
    }

    private void CaptureOwnedDescendants()
    {
        foreach (OwnedProcessIdentity root in _ownedProcesses.Values.ToArray())
        {
            try
            {
                foreach (OwnedProcessIdentity descendant in OwnedProcessLineage.CaptureDescendants(root))
                    TryOpenVerifiedProcess(descendant, out _);
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private bool IsLiveIdentity(OwnedProcessIdentity expected) =>
        _ownedProcessHandles.TryGetValue(expected.ProcessId, out Process? process) &&
        OwnedProcessLineage.TryCapture(process, out OwnedProcessIdentity current) &&
        OwnedProcessLineage.IsSameIdentity(expected, current);

    private void KillIfIdentityMatches(OwnedProcessIdentity expected, int waitTimeoutMs)
    {
        try
        {
            if (!_ownedProcessHandles.TryGetValue(expected.ProcessId, out Process? process)) return;
            if (!OwnedProcessLineage.TryCapture(process, out OwnedProcessIdentity current) ||
                !OwnedProcessLineage.IsSameIdentity(expected, current)) return;
            process.Kill(entireProcessTree: false);
            process.WaitForExit(waitTimeoutMs);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                   System.ComponentModel.Win32Exception or NotSupportedException)
        { /* Exited, inaccessible, or identity changed: leave it alone. */ }
    }

    private bool TryOpenVerifiedProcess(OwnedProcessIdentity expected, out Process? process)
    {
        process = null;
        Process? candidate = null;
        try
        {
            candidate = Process.GetProcessById(expected.ProcessId);
            _ = candidate.SafeHandle;
            if (!OwnedProcessLineage.TryCapture(candidate, out OwnedProcessIdentity current) ||
                !OwnedProcessLineage.IsSameIdentity(expected, current) ||
                !OwnedProcessLineage.CaptureDescendants(_launcherIdentity)
                    .Any(identity => OwnedProcessLineage.IsSameIdentity(expected, identity)))
            { return false; }
            if (_ownedProcesses.TryGetValue(expected.ProcessId, out OwnedProcessIdentity oldIdentity))
            {
                if (!OwnedProcessLineage.IsSameIdentity(oldIdentity, expected) ||
                    !_ownedProcessHandles.TryGetValue(expected.ProcessId, out Process? oldHandle) ||
                    !OwnedProcessLineage.TryCapture(oldHandle, out OwnedProcessIdentity oldHandleIdentity) ||
                    !OwnedProcessLineage.IsSameIdentity(oldHandleIdentity, expected))
                { return false; }
                candidate.Dispose();
                candidate = null;
                process = oldHandle;
                return true;
            }
            _ownedProcessHandles.Add(expected.ProcessId, candidate);
            _ownedProcesses[expected.ProcessId] = expected;
            process = candidate;
            candidate = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                   System.ComponentModel.Win32Exception or NotSupportedException or
                                   UnauthorizedAccessException or IOException)
        { return false; }
        finally { candidate?.Dispose(); }
    }

    private void DisposeOwnedProcessHandles()
    {
        foreach (Process process in _ownedProcessHandles.Values.Distinct())
            try { process.Dispose(); } catch { }
        _ownedProcessHandles.Clear();
    }
}
