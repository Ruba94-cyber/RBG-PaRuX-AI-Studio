using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.Data.Sqlite;

namespace PaRuX.UITests.Infrastructure;

internal sealed class UiTestApplication : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    private readonly string _testRoot;
    private readonly string _localAiStateRoot;
    private readonly string _artifacts;
    private readonly Application _application;
    private readonly BootstrapAuthStubServer? _bootstrapAuthFixture;
    private bool _preserveDataForRestart;
    private bool _disposed;
    internal UIA3Automation Automation { get; }
    internal Window MainWindow { get; }
    internal string DataRoot => _testRoot;
    internal string LocalAiStateRoot => _localAiStateRoot;
    internal string SettingsPath => Path.Combine(_testRoot, "settings.json");
    internal string LogPath => Path.Combine(_testRoot, "parux.log");
    internal int BootstrapRedirectedRequestCount => _bootstrapAuthFixture?.RedirectedRequestCount ?? 0;
    internal int BootstrapConversationPushRequestCount => _bootstrapAuthFixture?.ConversationPushRequestCount ?? 0;
    internal int BootstrapIdentityRequestCount => _bootstrapAuthFixture?.IdentityRequestCount ?? 0;
    internal string? BootstrapIdentityToken => _bootstrapAuthFixture?.IdentityToken;
    internal string? BootstrapRedirectedToken => _bootstrapAuthFixture?.RedirectedToken;
    internal int ExplicitDiscoveryPostCount => _bootstrapAuthFixture?.ExplicitDiscoveryPostCount ?? 0;
    internal string[] ExplicitDiscoveryRepositoryIds => _bootstrapAuthFixture?.ExplicitDiscoveryRepositoryIds
        ?? Array.Empty<string>();
    internal int DiscoveryCatalogGetCount => _bootstrapAuthFixture?.DiscoveryCatalogGetCount ?? 0;
    internal Task SpeechFirstAudio => (_bootstrapAuthFixture ?? throw new InvalidOperationException("No speech fixture is active.")).SpeechFirstAudio;
    internal Task SpeechFirstCancellation => (_bootstrapAuthFixture ?? throw new InvalidOperationException("No speech fixture is active.")).SpeechFirstCancellation;
    internal Task SpeechFollowupDone => (_bootstrapAuthFixture ?? throw new InvalidOperationException("No speech fixture is active.")).SpeechFollowupDone;
    internal int SpeechArtifactCreateCount => _bootstrapAuthFixture?.SpeechArtifactCreateCount ?? 0;
    internal int SpeechArtifactStatusCount => _bootstrapAuthFixture?.SpeechArtifactStatusCount ?? 0;
    internal bool SpeechFirstWasCancelled => _bootstrapAuthFixture?.SpeechFirstWasCancelled ?? false;
    internal bool SpeechFollowupWasCompleted => _bootstrapAuthFixture?.SpeechFollowupWasCompleted ?? false;
    internal string[] SpeechRequestIds => _bootstrapAuthFixture?.SpeechRequestIds ?? Array.Empty<string>();
    internal int[] SpeechSequences(string requestId) => _bootstrapAuthFixture?.SpeechSequences(requestId) ?? Array.Empty<int>();
    internal void SetDiscoveryCatalogFailure(bool fail) =>
        (_bootstrapAuthFixture ?? throw new InvalidOperationException("No HTTP fixture is active."))
            .SetDiscoveryCatalogFailure(fail);
    internal int? InitialBootstrapProcessId { get; private set; }
    internal string? CompletedCodingApplyRepositoryRoot { get; private set; }
    internal string? CompletedCodingApplyWorktreeRoot { get; private set; }
    internal string? CompletedCodingApplyBaselineCommit { get; private set; }

    internal UiTestApplication(string testName, string? initialAgentCodingWorkspaceRoot = null,
        bool enableModelLicenseConsentFixture = false, bool enableFirstModelStorageFixture = false,
        bool enableFirstModelZeroStorageFixture = false, bool enableCandidateEvaluationFixture = false,
        bool enableCandidateStagingFixture = false, bool enableFirstModelSelectionStorageFixture = false,
        int seedConversationHistoryCount = 0, bool enableCandidateIgnoreFixture = false,
        bool failCandidateIgnoreFixture = false, bool leaveCandidateIgnoreUnconfirmedFixture = false,
        bool enableCandidateUtilityPolicyFixture = false,
        bool enableCandidateLicensePreflightRejectionFixture = false,
        bool enableModelRoleAssignmentFixture = false,
        string? modelRoutingFixture = null,
        bool enableBootstrapAuthenticationFixture = false,
        bool enableBootstrapIdentityMismatchFixture = false,
        bool enableBootstrapRedirectFixture = false,
        bool enableStaleBootstrapRecoveryFixture = false,
        bool enableConversationSyncRedirectFixture = false,
        bool enableExplicitDiscoveryHttpFixture = false,
        int explicitDiscoveryHttpStatus = 200,
        int explicitDiscoveryHttpDelayMs = 0,
        bool enableCompletedCodingTaskCardFixture = false,
        bool enableCompletedCodingTaskApplyFixture = false,
        bool enableSpeechCancellationFixture = false,
        string? restartDataRoot = null)
    {
        string isolatedRoot = Path.Combine(Repository.Root, ".ai", "artifacts", "ui-data");
        // Keep Git object/worktree paths below the Windows legacy path limit; the artifact
        // directory retains the full test name for diagnostics.
        _testRoot = restartDataRoot ?? Path.Combine(isolatedRoot, Guid.NewGuid().ToString("N")[..16]);
        if (!Path.GetFullPath(_testRoot).StartsWith(Path.GetFullPath(isolatedRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("UI restart data must stay in the isolated project test root.", nameof(restartDataRoot));
        if (restartDataRoot != null && !File.Exists(SettingsPath))
            throw new ArgumentException("UI restart requires the previous session's persisted settings.", nameof(restartDataRoot));
        _localAiStateRoot = Path.Combine(_testRoot, "localai-state");
        // The app's state-file watcher only attaches to an existing directory; tests that write
        // state markers after launch (e.g. Gaming Mode) need it to exist before the app starts.
        Directory.CreateDirectory(_localAiStateRoot);
        _artifacts = Path.Combine(Repository.Root, "TestResults", "UIArtifacts", testName + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_testRoot);
        Directory.CreateDirectory(_localAiStateRoot);
        Directory.CreateDirectory(_artifacts);
        if (enableCompletedCodingTaskApplyFixture)
        {
            CompletedCodingApplyRepositoryRoot = Path.Combine(_testRoot, "coding-apply-target");
            CompletedCodingApplyWorktreeRoot = Path.Combine(_testRoot, "coding-apply-worktree");
            InitializeCodingApplyFixture(CompletedCodingApplyRepositoryRoot, CompletedCodingApplyWorktreeRoot);
            CompletedCodingApplyBaselineCommit = RunGit(CompletedCodingApplyRepositoryRoot, "rev-parse", "HEAD").Trim();
            enableCompletedCodingTaskCardFixture = true;
        }
        bool bootstrapUsesFixtureServerIdentity = enableBootstrapAuthenticationFixture
            || enableBootstrapRedirectFixture || enableConversationSyncRedirectFixture
            || enableExplicitDiscoveryHttpFixture || enableSpeechCancellationFixture;
        if (bootstrapUsesFixtureServerIdentity || enableBootstrapIdentityMismatchFixture
            || enableStaleBootstrapRecoveryFixture || enableConversationSyncRedirectFixture)
        {
            _bootstrapAuthFixture = new BootstrapAuthStubServer(
                acceptCredentials: enableBootstrapIdentityMismatchFixture || enableConversationSyncRedirectFixture
                    || enableExplicitDiscoveryHttpFixture || enableSpeechCancellationFixture,
                redirectBootstrapProbe: enableBootstrapRedirectFixture,
                redirectConversationPush: enableConversationSyncRedirectFixture,
                explicitDiscoveryFixture: enableExplicitDiscoveryHttpFixture,
                explicitDiscoveryStatus: explicitDiscoveryHttpStatus,
                explicitDiscoveryDelayMs: explicitDiscoveryHttpDelayMs,
                speechCancellationFixture: enableSpeechCancellationFixture);
            string bootstrapDirectory = Path.Combine(_testRoot, "server");
            Directory.CreateDirectory(bootstrapDirectory);
            int bootstrapProcessId = bootstrapUsesFixtureServerIdentity || enableBootstrapIdentityMismatchFixture
                ? Environment.ProcessId : int.MaxValue;
            InitialBootstrapProcessId = bootstrapProcessId;
            File.WriteAllText(Path.Combine(bootstrapDirectory, "bootstrap.json"), JsonSerializer.Serialize(new
            {
                endpoint = _bootstrapAuthFixture.Endpoint,
                token = "stale-test-token",
                processId = bootstrapProcessId,
                startedUtc = DateTime.UtcNow
            }));
        }
        var initialSettings = new Dictionary<string, object?>
        {
            ["OnboardingCompleted"] = true,
            ["StudioModeEnabled"] = false,
            ["BubbleMode"] = "Disabled"
        };
        if (!string.IsNullOrWhiteSpace(initialAgentCodingWorkspaceRoot))
            initialSettings["AgentCodingWorkspaceRoot"] = initialAgentCodingWorkspaceRoot;
        if (restartDataRoot == null)
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(initialSettings));
        if (restartDataRoot == null && seedConversationHistoryCount > 0)
            SeedConversationHistory(Path.Combine(_testRoot, "parux.db"), seedConversationHistoryCount);

        var existingPids = Process.GetProcessesByName("PaRuX").Select(process => process.Id).ToHashSet();
        var start = new ProcessStartInfo(Repository.AppExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Repository.AppExecutable)!
        };
        start.Environment["PARUX_DATA_ROOT"] = _testRoot;
        start.Environment["PARUX_LOCALAI_STATE_DIR"] = _localAiStateRoot;
        if (bootstrapUsesFixtureServerIdentity)
        {
            string expectedFixtureServerExecutable = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("The FlaUI test host executable could not be determined.");
            start.Environment["PARUX_SERVER_PATH"] = expectedFixtureServerExecutable;
        }
        if (enableStaleBootstrapRecoveryFixture)
        {
            string serverExecutable = Path.Combine(Path.GetDirectoryName(Repository.AppExecutable)!,
                "PaRuX.Server", "PaRuX.Server.exe");
            if (!File.Exists(serverExecutable))
                throw new FileNotFoundException("The UI test server publish was not found for stale-bootstrap recovery.", serverExecutable);
            start.Environment["PARUX_SERVER_PATH"] = serverExecutable;
        }
        bool enableUiFixtures = enableModelLicenseConsentFixture || enableFirstModelStorageFixture
            || enableFirstModelZeroStorageFixture || enableFirstModelSelectionStorageFixture
            || enableCandidateEvaluationFixture || enableCandidateStagingFixture || enableCandidateIgnoreFixture
            || enableCandidateUtilityPolicyFixture || enableCandidateLicensePreflightRejectionFixture;
        enableUiFixtures |= enableModelRoleAssignmentFixture;
        enableUiFixtures |= modelRoutingFixture != null;
        enableUiFixtures |= enableBootstrapAuthenticationFixture;
        enableUiFixtures |= enableBootstrapIdentityMismatchFixture;
        enableUiFixtures |= enableBootstrapRedirectFixture;
        enableUiFixtures |= enableStaleBootstrapRecoveryFixture;
        enableUiFixtures |= enableConversationSyncRedirectFixture;
        enableUiFixtures |= enableExplicitDiscoveryHttpFixture;
        enableUiFixtures |= enableCompletedCodingTaskCardFixture;
        enableUiFixtures |= enableCompletedCodingTaskApplyFixture;
        start.Environment["PARUX_UI_TEST_ISOLATED"] = "1";
        start.Environment["PARUX_UI_TESTS"] = enableUiFixtures ? "1" : "0";
        start.Environment["PARUX_UI_TEST_CODING_TASK_CARD"] = enableCompletedCodingTaskApplyFixture
            ? "completed-apply" : enableCompletedCodingTaskCardFixture ? "completed" : string.Empty;
        start.Environment["PARUX_UI_TEST_CODING_APPLY_REPOSITORY"] = CompletedCodingApplyRepositoryRoot ?? string.Empty;
        start.Environment["PARUX_UI_TEST_CODING_APPLY_WORKTREE"] = CompletedCodingApplyWorktreeRoot ?? string.Empty;
        start.Environment["PARUX_UI_TEST_MODEL_ROLE_ASSIGNMENT"] = enableModelRoleAssignmentFixture ? "1" : "0";
        start.Environment["PARUX_UI_TEST_MODEL_ROUTING"] = modelRoutingFixture ?? string.Empty;
        if (enableModelLicenseConsentFixture || enableCandidateEvaluationFixture || enableCandidateStagingFixture
            || enableCandidateIgnoreFixture || enableCandidateUtilityPolicyFixture
            || enableCandidateLicensePreflightRejectionFixture || enableExplicitDiscoveryHttpFixture)
            start.Environment["PARUX_UI_TEST_INITIAL_PAGE"] = "modelhub";
        start.Environment["PARUX_UI_TEST_CANDIDATE_EVALUATION"] =
            enableCandidateEvaluationFixture || enableCandidateIgnoreFixture || enableCandidateUtilityPolicyFixture
                || enableCandidateLicensePreflightRejectionFixture
                ? "1" : "0";
        start.Environment["PARUX_UI_TEST_CANDIDATE_LICENSE_REJECTION"] =
            enableCandidateLicensePreflightRejectionFixture ? "metadata_changed" : "0";
        start.Environment["PARUX_UI_TEST_CANDIDATE_IGNORE"] = enableCandidateIgnoreFixture ? "1" : "0";
        start.Environment["PARUX_UI_TEST_CANDIDATE_IGNORE_FAILURE"] = failCandidateIgnoreFixture ? "1" : "0";
        start.Environment["PARUX_UI_TEST_CANDIDATE_IGNORE_UNCONFIRMED"] =
            leaveCandidateIgnoreUnconfirmedFixture ? "1" : "0";
        start.Environment["PARUX_UI_TEST_CANDIDATE_UTILITY_POLICY"] =
            enableCandidateUtilityPolicyFixture ? "1" : "0";
        if (enableCandidateStagingFixture)
            start.Environment["PARUX_UI_TEST_CANDIDATE_STAGING"] = "1";
        if (enableFirstModelStorageFixture || enableFirstModelZeroStorageFixture || enableFirstModelSelectionStorageFixture)
        {
            start.Environment["PARUX_UI_TEST_INITIAL_PAGE"] = "firstmodel";
            start.Environment["PARUX_UI_TEST_FIRST_MODEL_STORAGE"] = enableFirstModelSelectionStorageFixture
                ? "selection"
                : enableFirstModelZeroStorageFixture ? "zero" : "1";
        }
        var launcher = Application.Launch(start);
        Application? application = null;
        UiWait.For(() =>
        {
            if (!launcher.HasExited && Process.GetProcessById(launcher.ProcessId).MainWindowHandle != IntPtr.Zero)
            {
                application = launcher;
                return true;
            }

            // WinUI's activation redirection can make the initial executable a short-lived
            // bootstrap process and create the actual desktop process separately.
            Process? desktopProcess = Process.GetProcessesByName("PaRuX")
                .FirstOrDefault(process => !existingPids.Contains(process.Id) && process.Id != launcher.ProcessId);
            if (desktopProcess == null)
                return false;

            application = Application.Attach(desktopProcess.Id);
            return true;
        }, "PaRuX desktop process", TimeSpan.FromSeconds(45));

        _application = application ?? throw new InvalidOperationException("PaRuX did not create a desktop process.");
        if (!ReferenceEquals(_application, launcher))
            launcher.Dispose();
        Automation = new UIA3Automation();
        Window? mainWindow = null;
        UiWait.For(() =>
        {
            mainWindow = _application.GetAllTopLevelWindows(Automation)
                .FirstOrDefault(window => string.Equals(window.Title, "PaRuX AI & Gaming OS", StringComparison.Ordinal));
            return mainWindow != null;
        }, "PaRuX main window", TimeSpan.FromSeconds(45));
        MainWindow = mainWindow ?? throw new InvalidOperationException("PaRuX main window was not found.");
        try
        {
            MainWindow.Patterns.Window.PatternOrDefault?.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
            IntPtr nativeHandle = MainWindow.Properties.NativeWindowHandle.ValueOrDefault;
            if (nativeHandle != IntPtr.Zero) ShowWindow(nativeHandle, 3); // SW_MAXIMIZE keeps the entire UIA surface on-screen.
        }
        catch { /* Some window managers do not expose the UIA window pattern; continue with the launched size. */ }
        MainWindow.SetForeground();
        MainWindow.Focus();
        // The Candidate discovery controls are Advanced-only; fixtures that open Model Hub directly drive them.
        if (string.Equals(start.Environment.TryGetValue("PARUX_UI_TEST_INITIAL_PAGE", out string? initialPage) ? initialPage : null, "modelhub", StringComparison.Ordinal))
            new PageObjects.WorkspaceNavigationPage(MainWindow, this).EnableAdvanced();
    }

    internal AutomationElement? FindRawDescendant(AutomationElement root, Func<AutomationElement, bool> predicate)
    {
        var walker = Automation.TreeWalkerFactory.GetRawViewWalker();
        var pending = new Stack<AutomationElement>();
        for (AutomationElement? child = walker.GetFirstChild(root); child != null; child = walker.GetNextSibling(child))
            pending.Push(child);

        int visited = 0;
        while (pending.Count > 0 && visited++ < 5000)
        {
            AutomationElement current = pending.Pop();
            try
            {
                if (predicate(current)) return current;
                var children = new List<AutomationElement>();
                for (AutomationElement? child = walker.GetFirstChild(current); child != null; child = walker.GetNextSibling(child))
                    children.Add(child);
                for (int index = children.Count - 1; index >= 0; index--)
                    pending.Push(children[index]);
            }
            catch
            {
                // Native modal windows can invalidate elements while their tree is still being created.
            }
        }
        return null;
    }

    internal void CaptureFailure(string step, Exception exception)
    {
        string report = Path.Combine(_artifacts, "failure.txt");
        string windowTitle;
        try { windowTitle = MainWindow.Title; }
        catch (Exception titleError) { windowTitle = "<UIA title unavailable: " + titleError.Message + ">"; }
        File.WriteAllText(report, $"UTC: {DateTime.UtcNow:O}\nPID: {_application.ProcessId}\nStep: {step}\nWindow: {windowTitle}\nException:\n{exception}\n");
        try
        {
            MainWindow.SetForeground();
            if (GetForegroundWindow() == new IntPtr(MainWindow.Properties.NativeWindowHandle.ValueOrDefault))
                MainWindow.Capture().Save(Path.Combine(_artifacts, "failure.png"), ImageFormat.Png);
            else
                File.AppendAllText(report, "\nScreenshot omitted: PaRuX was not the foreground window.\n");
        }
        catch { }
        try
        {
            var rows = DumpRawTree(MainWindow);
            File.WriteAllLines(Path.Combine(_artifacts, "uia-tree.txt"), rows);
        }
        catch { }
        // The isolated root is deleted on dispose; keep the app and Core logs that explain the failure.
        foreach (string log in Directory.Exists(_testRoot)
                     ? Directory.EnumerateFiles(_testRoot, "*.log", SearchOption.AllDirectories) : [])
        {
            try
            {
                using var source = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = File.Create(Path.Combine(_artifacts, "log-" + Path.GetRelativePath(_testRoot, log).Replace(Path.DirectorySeparatorChar, '_')));
                source.CopyTo(target);
            }
            catch { }
        }
    }

    internal string? CaptureSnapshot(string name)
    {
        MainWindow.SetForeground();
        if (GetForegroundWindow() != new IntPtr(MainWindow.Properties.NativeWindowHandle.ValueOrDefault))
        {
            File.WriteAllText(Path.Combine(_artifacts, name + "-screenshot-skipped.txt"),
                "Screenshot omitted because PaRuX could not be confirmed as the foreground window; "
                + "the test did not capture another application.");
            return null;
        }

        string path = Path.Combine(_artifacts, name + ".png");
        MainWindow.Capture().Save(path, ImageFormat.Png);
        File.WriteAllLines(Path.Combine(_artifacts, name + "-uia.txt"), DumpRawTree(MainWindow));
        return path;
    }

    internal void ResizeMainWindow(int width, int height)
    {
        IntPtr handle = new(MainWindow.Properties.NativeWindowHandle.ValueOrDefault);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("PaRuX main window has no native handle.");

        // Keep the whole test window on the current desktop so screenshots and visible-state
        // assertions are evidence of the rendered app, not only its off-screen UIA tree.
        int screenWidth = GetSystemMetrics(0); // SM_CXSCREEN
        int screenHeight = GetSystemMetrics(1); // SM_CYSCREEN
        if (screenWidth > 0) width = Math.Min(width, screenWidth);
        if (screenHeight > 0) height = Math.Min(height, screenHeight);

        ShowWindow(handle, 9); // SW_RESTORE
        const uint flags = 0x0004 /* SWP_NOZORDER */ | 0x0040 /* SWP_SHOWWINDOW */;
        if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, width, height, flags))
            throw new InvalidOperationException("Could not resize the PaRuX window for the layout test. Win32=" + Marshal.GetLastWin32Error());
        MainWindow.SetForeground();
    }

    internal UiTestApplication Restart(string testName)
    {
        // Window.Close intentionally parks PaRuX in its tray. Terminate only this isolated
        // test process to exercise persisted-state recovery after an abrupt desktop restart.
        _application.Kill();
        UiWait.For(() => _application.HasExited, "isolated desktop terminated before persisted-state restart", TimeSpan.FromSeconds(15));
        _preserveDataForRestart = true;
        Dispose();
        try { return new UiTestApplication(testName, restartDataRoot: _testRoot); }
        catch
        {
            try { Directory.Delete(_testRoot, recursive: true); } catch { }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { MainWindow.Close(); } catch { }
        int appProcessId = 0;
        try { appProcessId = _application.ProcessId; } catch { }
        // Kill() ends only the desktop process; its Core server child would otherwise outlive the test
        // and keep the Core port, breaking every later test. Terminate exactly the tree this test started
        // while the parent link still exists, then fall back to a plain kill.
        if (appProcessId > 0) KillProcessTree(appProcessId);
        try { if (!_application.HasExited) _application.Kill(); } catch { }
        Automation.Dispose();
        _application.Dispose();
        _bootstrapAuthFixture?.Dispose();
        if (!_preserveDataForRestart)
            try { Directory.Delete(_testRoot, recursive: true); } catch { }
    }

    private static void KillProcessTree(int rootProcessId)
    {
        try
        {
            using Process taskkill = Process.Start(new ProcessStartInfo("taskkill.exe",
                "/PID " + rootProcessId + " /T /F") { CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true })!;
            taskkill.WaitForExit(10000);
        }
        catch { /* Best effort: the process may already be gone. */ }
    }

    private IEnumerable<string> DumpRawTree(AutomationElement root)
    {
        var walker = Automation.TreeWalkerFactory.GetRawViewWalker();
        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((root, 0));
        int count = 0;
        while (stack.Count > 0 && count++ < 5000)
        {
            var current = stack.Pop();
            yield return $"{new string(' ', current.Depth * 2)}{current.Element.Properties.ControlType.ValueOrDefault}\tId={current.Element.Properties.AutomationId.ValueOrDefault}\tName={current.Element.Properties.Name.ValueOrDefault}\tClass={current.Element.Properties.ClassName.ValueOrDefault}";
            var children = new List<AutomationElement>();
            for (AutomationElement? child = walker.GetFirstChild(current.Element); child != null; child = walker.GetNextSibling(child))
                children.Add(child);
            for (int index = children.Count - 1; index >= 0; index--)
                stack.Push((children[index], current.Depth + 1));
        }
    }

    private static void SeedConversationHistory(string databasePath, int count)
    {
        using var connection = new SqliteConnection("Data Source=" + databasePath + ";Mode=ReadWriteCreate");
        connection.Open();
        using (SqliteCommand schema = connection.CreateCommand())
        {
            schema.CommandText = @"
CREATE TABLE conversations (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    document_json TEXT NOT NULL
);";
            schema.ExecuteNonQuery();
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = @"
INSERT INTO conversations(id, title, created_utc, updated_utc, document_json)
VALUES($id, $title, $created, $updated, $document);";
        SqliteParameter id = insert.Parameters.Add("$id", SqliteType.Text);
        SqliteParameter title = insert.Parameters.Add("$title", SqliteType.Text);
        SqliteParameter created = insert.Parameters.Add("$created", SqliteType.Text);
        SqliteParameter updated = insert.Parameters.Add("$updated", SqliteType.Text);
        SqliteParameter document = insert.Parameters.Add("$document", SqliteType.Text);
        DateTime now = DateTime.UtcNow;

        for (int index = 0; index < count; index++)
        {
            string conversationId = "ui-history-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            string conversationTitle = "Seeded conversation " + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            DateTime timestamp = now.AddMinutes(-index);
            id.Value = conversationId;
            title.Value = conversationTitle;
            created.Value = timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            updated.Value = timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            document.Value = JsonSerializer.Serialize(new
            {
                Id = conversationId,
                Title = conversationTitle,
                CreatedUtc = timestamp,
                UpdatedUtc = timestamp,
                IsFavorite = index % 20 == 0,
                Messages = index == 119
                    ? new object[]
                    {
                        new
                        {
                            Role = "user",
                            Content = "Unique history marker 0119",
                            TimestampUtc = timestamp
                        }
                    }
                    : new object[]
                    {
                        // The app never materializes a message-less conversation, so a realistic
                        // seeded history entry always carries at least one turn.
                        new
                        {
                            Role = "user",
                            Content = "Seeded history message " + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                            TimestampUtc = timestamp
                        }
                    },
                Branches = Array.Empty<object>()
            });
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void InitializeCodingApplyFixture(string repository, string worktree)
    {
        Directory.CreateDirectory(repository);
        RunGit(repository, "init");
        RunGit(repository, "config", "user.name", "PaRuX UI Tests");
        RunGit(repository, "config", "user.email", "ui-tests@parux.invalid");
        File.WriteAllText(Path.Combine(repository, "apply-target.txt"), "baseline from FlaUI\n");
        RunGit(repository, "add", "apply-target.txt");
        RunGit(repository, "commit", "-m", "FlaUI coding apply baseline");
        RunGit(repository, "worktree", "add", "-b", "parux-ui-test-task", worktree, "HEAD");
        File.WriteAllText(Path.Combine(worktree, "apply-target.txt"), "changed by the reviewed PaRuX patch\n");
        File.WriteAllText(Path.Combine(worktree, "created-by-agent.txt"), "new file from the reviewed patch\n");
        File.WriteAllBytes(Path.Combine(worktree, "created-binary-by-agent.bin"), new byte[] { 0x50, 0x61, 0x52, 0x00, 0x75, 0xFF });
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start for the FlaUI fixture.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Git timed out while preparing the FlaUI apply fixture.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Git " + string.Join(' ', arguments) + " failed: " + error);
        return output;
    }
}
