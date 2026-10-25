using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class UpdateManagerCoverageTests
{
    private const string CheckingText = "Checking update state…";
    private const string OfflineResultPrefix = "Release check unavailable or unverifiable:";
    private const string AuthenticatedNoUpgradeText = "The authenticated/public release metadata confirms this PaRuX build is not older than the latest published release.";
    private const string SecureInstallerOfflineMessage = "Secure installer check failed before any installation action.";

    private static void Run(string testName, Action<UiTestApplication, OfflineUpdateProxy> action,
        bool seedUserManagedComfyUi = false)
    {
        using var proxy = new OfflineUpdateProxy();
        string? dataRoot = seedUserManagedComfyUi ? CreateUserManagedComfyUiDataRoot() : null;
        try
        {
            using var app = LaunchWithOfflineFeed(testName, proxy, dataRoot);
            try { action(app, proxy); }
            catch (Exception ex) { app.CaptureFailure(testName, ex); throw; }
        }
        finally
        {
            if (dataRoot != null && Directory.Exists(dataRoot))
            {
                try { Directory.Delete(dataRoot, recursive: true); } catch { }
            }
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Updates"), Trait("Category", "FlaUI")]
    public void Opening_updates_checks_state_without_applying_an_update()
    {
        Run(nameof(Opening_updates_checks_state_without_applying_an_update), (app, proxy) =>
        {
            OpenUpdates(app, proxy);
            WaitForOfflineResult(app, proxy, proxy.RequestCount, "automatic offline release check after opening Update Manager");

            AutomationElement result = FindText(app, name => name.StartsWith(OfflineResultPrefix, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Opening Update Manager did not report the unverified release state.");
            Assert.StartsWith(OfflineResultPrefix, result.Name, StringComparison.Ordinal);
            Assert.NotNull(FindText(app,
                name => name == "Update state refreshed. No update is applied without an explicit action."));
            Assert.Null(FindText(app, name => name == "Downloading verified PaRuX MSI…"));

            AutomationElement? install = FindButton(app, "Download & install verified MSI");
            Assert.True(install == null || !install.Properties.IsEnabled.ValueOrDefault,
                "An unverified release must not make an application installer actionable during automatic refresh.");
            Assert.Equal("RBG PaRuX AI Studio", app.MainWindow.Title);
        });
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Updates"), Trait("Category", "FlaUI")]
    public void Check_again_shows_busy_until_the_offline_result_is_known()
    {
        Run(nameof(Check_again_shows_busy_until_the_offline_result_is_known), (app, proxy) =>
        {
            OpenUpdates(app, proxy);
            WaitForOfflineResult(app, proxy, proxy.RequestCount, "initial offline release check");

            int expectedRequestCount = checked((int)proxy.RequestCount + 1);
            proxy.HoldNextRequest();
            AutomationElement checkAgain = FindButton(app, "Check again")
                ?? throw new InvalidOperationException("The Check again button was not found.");
            app.MainWindow.SetForeground();
            Invoke(checkAgain);

            try
            {
                UiWait.For(() => proxy.HeldRequestArrived, "manual update check reached the loopback-only offline feed", TimeSpan.FromSeconds(10));
                UiWait.For(() => FindText(app, name => name == CheckingText) != null,
                    "busy update-check status while the feed response is pending", TimeSpan.FromSeconds(5));
                Assert.Equal(CheckingText, FindText(app, name => name == CheckingText)!.Name);
                Assert.Null(FindText(app, name => name == AuthenticatedNoUpgradeText));
            }
            finally
            {
                proxy.ReleaseHeldRequest();
            }

            WaitForOfflineResult(app, proxy, expectedRequestCount, "manual offline release check result");
            AutomationElement result = FindText(app, name => name.StartsWith(OfflineResultPrefix, StringComparison.Ordinal))!;
            Assert.StartsWith(OfflineResultPrefix, result.Name, StringComparison.Ordinal);
        });
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Updates"), Trait("Category", "FlaUI")]
    public void Unverified_release_does_not_enable_install_or_apply_automatically()
    {
        Run(nameof(Unverified_release_does_not_enable_install_or_apply_automatically), (app, proxy) =>
        {
            OpenUpdates(app, proxy);
            WaitForOfflineResult(app, proxy, proxy.RequestCount, "offline release state and update-manager refresh");

            AutomationElement installedVersion = FindText(app, name => name.StartsWith("Installed: ", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The installed application version was not shown.");
            string version = installedVersion.Name["Installed: ".Length..].Split(" · ", 2, StringSplitOptions.None)[0].Trim();
            Assert.False(string.IsNullOrWhiteSpace(version));

            AutomationElement installerReason = FindText(app, name => name == SecureInstallerOfflineMessage)
                ?? throw new InvalidOperationException("The UI did not explain why secure installation is unavailable.");
            Assert.Equal(SecureInstallerOfflineMessage, installerReason.Name);

        });
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Updates"), Trait("Category", "FlaUI")]
    public void User_managed_component_is_listed_as_outside_ParuX_update_ownership()
    {
        Run(nameof(User_managed_component_is_listed_as_outside_ParuX_update_ownership), (app, proxy) =>
        {
            OpenUpdates(app, proxy);
            WaitForOfflineResult(app, proxy, proxy.RequestCount, "offline update check before component ownership assertion");

            const string ownershipExplanation = "Managed by you. PaRuX can use/validate this location but Update Manager will not modify it.";
            AutomationElement explanation = FindText(app, name => name == ownershipExplanation)
                ?? throw new InvalidOperationException("The configured user-managed component was not listed with its ownership explanation.");
            AutomationElement componentCard = FindNearestAncestorWithUserManagedTitle(app, explanation);
            AutomationElement title = app.FindRawDescendant(componentCard, IsUserManagedTitle)
                ?? throw new InvalidOperationException("The user-managed component card did not identify its ownership.");
            Assert.EndsWith(" · User managed", title.Name, StringComparison.Ordinal);
        }, seedUserManagedComfyUi: true);
    }

    private static UiTestApplication LaunchWithOfflineFeed(string testName, OfflineUpdateProxy proxy, string? dataRoot)
    {
        using var environment = new ProxyEnvironmentScope(proxy.Address);
        return dataRoot == null
            ? new UiTestApplication(testName)
            : new UiTestApplication(testName, restartDataRoot: dataRoot);
    }

    private static void OpenUpdates(UiTestApplication app, OfflineUpdateProxy proxy)
    {
        var main = new MainWindowPage(app.MainWindow);
        main.EnableAdvanced();
        AutomationElement updatesNavigation = UiWait.ById(app.MainWindow, "Navigation_Updates_Item", ControlType.ListItem);
        Assert.Equal("Updates", updatesNavigation.Name);

        long requestsBeforeNavigation = proxy.RequestCount;
        main.Click("Navigation_Updates_Item", ControlType.ListItem);
        UiWait.For(() => FindText(app, name => name == "Update Manager") != null,
            "Update Manager heading after opening its workspace");
        UiWait.For(() => proxy.RequestCount > requestsBeforeNavigation,
            "Update Manager release check sent through the loopback-only offline feed", TimeSpan.FromSeconds(10));
    }

    private static void WaitForOfflineResult(UiTestApplication app, OfflineUpdateProxy proxy,
        long minimumRequestCount, string description)
    {
        UiWait.For(() =>
        {
            bool requestFinished = proxy.RequestCount >= minimumRequestCount && proxy.InFlightCount == 0;
            bool statusResolved = FindText(app, name => name == CheckingText) == null;
            bool releaseResultShown = FindText(app, name => name.StartsWith(OfflineResultPrefix, StringComparison.Ordinal)) != null;
            bool installedVersionShown = FindText(app, name => name.StartsWith("Installed: ", StringComparison.Ordinal)) != null;
            bool installerReasonShown = FindText(app, name => name == SecureInstallerOfflineMessage) != null;
            return requestFinished && statusResolved && releaseResultShown && installedVersionShown && installerReasonShown;
        }, description, TimeSpan.FromSeconds(25));
    }

    private static AutomationElement? FindText(UiTestApplication app, Func<string, bool> nameMatches) =>
        app.FindRawDescendant(app.MainWindow, element =>
            element.Properties.ControlType.ValueOrDefault == ControlType.Text
            && nameMatches(element.Properties.Name.ValueOrDefault ?? string.Empty));

    private static AutomationElement? FindButton(UiTestApplication app, string name) =>
        app.FindRawDescendant(app.MainWindow, element =>
            element.Properties.ControlType.ValueOrDefault == ControlType.Button
            && element.Properties.Name.ValueOrDefault == name);

    private static bool IsUserManagedTitle(AutomationElement element) =>
        element.Properties.ControlType.ValueOrDefault == ControlType.Text
        && (element.Properties.Name.ValueOrDefault ?? string.Empty).EndsWith(" · User managed", StringComparison.Ordinal);

    private static AutomationElement FindNearestAncestorWithUserManagedTitle(UiTestApplication app, AutomationElement element)
    {
        var walker = app.Automation.TreeWalkerFactory.GetRawViewWalker();
        AutomationElement? ancestor = walker.GetParent(element);
        while (ancestor != null && ancestor.Properties.ControlType.ValueOrDefault != ControlType.Window)
        {
            if (app.FindRawDescendant(ancestor, IsUserManagedTitle) != null)
                return ancestor;
            ancestor = walker.GetParent(ancestor);
        }
        throw new InvalidOperationException("The user-managed component title and explanation did not share a UIA card.");
    }

    private static void Invoke(AutomationElement element)
    {
        if (element.Patterns.Invoke.PatternOrDefault != null)
            element.Patterns.Invoke.Pattern.Invoke();
        else
            element.Click(moveMouse: false);
    }

    private static string CreateUserManagedComfyUiDataRoot()
    {
        string isolatedRoot = Path.Combine(Repository.Root, ".ai", "artifacts", "ui-data");
        string dataRoot = Path.Combine(isolatedRoot, Guid.NewGuid().ToString("N")[..16]);
        Directory.CreateDirectory(dataRoot);

        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["OnboardingCompleted"] = true,
            ["StudioModeEnabled"] = false,
            ["BubbleMode"] = "Disabled"
        }));

        string mainPy = Path.Combine(dataRoot, "fixtures", "ComfyUI", "main.py");
        Directory.CreateDirectory(Path.GetDirectoryName(mainPy)!);
        File.WriteAllText(mainPy, "# disposable UI fixture\n");
        File.WriteAllText(Path.Combine(dataRoot, "component-locations.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["comfyui"] = mainPy }));
        return dataRoot;
    }

    private sealed class ProxyEnvironmentScope : IDisposable
    {
        private static readonly string[] ProxyVariables =
        {
            "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy"
        };
        private static readonly string[] BypassVariables = { "NO_PROXY", "no_proxy" };
        private readonly Dictionary<string, string?> _previous = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        internal ProxyEnvironmentScope(string proxyAddress)
        {
            foreach (string name in ProxyVariables.Concat(BypassVariables))
                _previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
            foreach (string name in ProxyVariables)
                Environment.SetEnvironmentVariable(name, proxyAddress);
            foreach (string name in BypassVariables)
                Environment.SetEnvironmentVariable(name, "localhost,127.0.0.1,::1");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach ((string name, string? value) in _previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class OfflineUpdateProxy : IDisposable
    {
        private sealed class HeldRequest
        {
            internal TaskCompletionSource<bool> Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ConcurrentBag<Task> _connections = new();
        private readonly Task _acceptLoop;
        private HeldRequest? _nextHeldRequest;
        private HeldRequest? _activeHeldRequest;
        private long _requestCount;
        private int _inFlightCount;
        private bool _disposed;

        internal OfflineUpdateProxy()
        {
            _listener.Start();
            Address = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync();
        }

        internal string Address { get; }
        internal long RequestCount => Interlocked.Read(ref _requestCount);
        internal int InFlightCount => Volatile.Read(ref _inFlightCount);
        internal bool HeldRequestArrived => _activeHeldRequest?.Arrived.Task.IsCompletedSuccessfully == true;

        internal void HoldNextRequest()
        {
            var held = new HeldRequest();
            if (Interlocked.CompareExchange(ref _nextHeldRequest, held, null) != null)
                throw new InvalidOperationException("An update-feed request is already held.");
            _activeHeldRequest = held;
        }

        internal void ReleaseHeldRequest() => _activeHeldRequest?.Release.TrySetResult(true);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                    _connections.Add(RejectRequestAsync(client));
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
        }

        private async Task RejectRequestAsync(TcpClient client)
        {
            Interlocked.Increment(ref _inFlightCount);
            try
            {
                using (client)
                {
                    NetworkStream stream = client.GetStream();
                    if (!await ReadHeadersAsync(stream, _shutdown.Token)) return;
                    Interlocked.Increment(ref _requestCount);

                    HeldRequest? held = Interlocked.Exchange(ref _nextHeldRequest, null);
                    if (held != null)
                    {
                        held.Arrived.TrySetResult(true);
                        await held.Release.Task.WaitAsync(_shutdown.Token);
                    }

                    byte[] response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response.AsMemory(), _shutdown.Token);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (IOException) when (_shutdown.IsCancellationRequested) { }
            finally
            {
                Interlocked.Decrement(ref _inFlightCount);
            }
        }

        private static async Task<bool> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024, leaveOpen: true);
            string? request = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(request)) return false;
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken))) { }
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseHeldRequest();
            _shutdown.Cancel();
            _listener.Stop();
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            foreach (Task connection in _connections)
                try { connection.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _shutdown.Dispose();
        }
    }
}
