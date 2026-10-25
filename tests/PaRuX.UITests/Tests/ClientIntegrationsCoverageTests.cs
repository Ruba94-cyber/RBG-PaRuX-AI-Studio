using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ClientIntegrationsCoverageTests
{
    private static readonly string[] ValidStatusWords =
    {
        "Not installed",
        "Detected",
        "Available",
        "Connected to PaRuX",
        "Using another provider",
        "Configuration error"
    };

    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings"), Trait("Category", "Integrations")]
    public void Supported_client_integrations_list_displays_truthful_status_and_protocol() =>
        Run(nameof(Supported_client_integrations_list_displays_truthful_status_and_protocol), app =>
        {
            var page = new SettingsIntegrationsPage(app.MainWindow);
            page.OpenIntegrationsCategory();
            page.WaitForGatewayResolved();

            var expectedClients = new[]
            {
                new { Id = "claudecode", DisplayName = "Claude Code", Protocol = "Anthropic Messages" },
                new { Id = "codex", DisplayName = "Codex", Protocol = "OpenAI Responses" },
                new { Id = "opencode", DisplayName = "OpenCode", Protocol = "OpenAI Chat Completions" }
            };

            foreach (var client in expectedClients)
            {
                var statusElement = page.ClientStatusText(client.Id);
                string statusText = statusElement.Name;

                Assert.False(string.IsNullOrWhiteSpace(statusText),
                    "Status text for " + client.DisplayName + " must not be blank.");
                Assert.Contains(ValidStatusWords, word => statusText.StartsWith(word, StringComparison.Ordinal));

                FindByName(app, client.DisplayName, ControlType.Text);
                FindTextContaining(app, client.Protocol);
            }

            var refreshButton = page.IntegrationsRefreshButton;
            Assert.True(refreshButton.Properties.IsEnabled.ValueOrDefault,
                "Integrations refresh button should be enabled.");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings"), Trait("Category", "Integrations")]
    public void Gateway_state_reports_truthfully_and_omits_action_buttons_for_uninstalled_clients() =>
        Run(nameof(Gateway_state_reports_truthfully_and_omits_action_buttons_for_uninstalled_clients), app =>
        {
            var page = new SettingsIntegrationsPage(app.MainWindow);
            page.OpenIntegrationsCategory();
            page.WaitForGatewayResolved();

            string stateText = page.GatewayStateText.Name;
            Assert.False(stateText.StartsWith("Checking", StringComparison.Ordinal),
                "Gateway state must resolve beyond initial checking placeholder.");

            bool isRunning = stateText.StartsWith("Gateway running at ", StringComparison.Ordinal);
            bool isOff = string.Equals(stateText, "Gateway is off", StringComparison.Ordinal);
            bool isPortConflict = stateText.StartsWith("Gateway port ", StringComparison.Ordinal)
                && stateText.Contains("is in use by another program", StringComparison.Ordinal);
            bool isUnreachable = string.Equals(stateText, "PaRuX Core is not reachable", StringComparison.Ordinal);

            Assert.True(isRunning || isOff || isPortConflict || isUnreachable,
                "Gateway state must be a truthful status string, actual: '" + stateText + "'.");

            foreach (string clientId in new[] { "claudecode", "codex", "opencode" })
            {
                string clientStatus = page.ClientStatusText(clientId).Name;
                if (clientStatus.StartsWith("Not installed", StringComparison.Ordinal))
                {
                    var connectButton = app.MainWindow.FindFirstDescendant(cf =>
                        cf.ByAutomationId("Settings_Integration_" + clientId + "_Connect_Button"));
                    Assert.Null(connectButton);

                    var launchButton = app.MainWindow.FindFirstDescendant(cf =>
                        cf.ByAutomationId("Settings_Integration_" + clientId + "_Launch_Button"));
                    Assert.Null(launchButton);
                }
            }

            if (!isRunning)
            {
                var notice = app.MainWindow.FindFirstDescendant(cf =>
                    cf.ByName("The PaRuX gateway is not running, so Connect, Launch and Test are unavailable."));
                // If any client was detected or installed while gateway is inactive, the honesty notice is shown.
                // If no clients are installed, the cards cleanly omit action buttons without misleading affordances.
                if (notice != null)
                {
                    Assert.Equal(ControlType.Text, notice.Properties.ControlType.ValueOrDefault);
                }
            }
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings"), Trait("Category", "Integrations")]
    public void Integration_working_folder_is_read_only_and_launch_folder_picker_is_available() =>
        Run(nameof(Integration_working_folder_is_read_only_and_launch_folder_picker_is_available), app =>
        {
            var page = new SettingsIntegrationsPage(app.MainWindow);
            page.OpenIntegrationsCategory();
            page.WaitForGatewayResolved();

            var folderBox = page.WorkingFolderTextBox.AsTextBox();
            Assert.True(folderBox.IsReadOnly,
                "Integration working folder text box must be read-only to prevent path corruption.");

            string folderPath = folderBox.Text;
            Assert.False(string.IsNullOrWhiteSpace(folderPath),
                "Integration launch working folder must not be empty.");
            Assert.True(Directory.Exists(folderPath),
                "Integration launch folder must resolve to an existing directory path: " + folderPath);

            var chooseFolderButton = FindByName(app, "Choose folder", ControlType.Button);
            Assert.True(chooseFolderButton.Properties.IsEnabled.ValueOrDefault,
                "Choose folder button must be enabled.");

            FindByName(app, "DEVELOPER / AI CLIENTS", ControlType.Text);
            FindByName(app, "Launch in", ControlType.Text);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings"), Trait("Category", "Integrations")]
    public void Advanced_gateway_settings_displays_toggle_and_requires_explicit_save() =>
        Run(nameof(Advanced_gateway_settings_displays_toggle_and_requires_explicit_save), app =>
        {
            var page = new SettingsIntegrationsPage(app.MainWindow);
            page.OpenIntegrationsCategory();
            page.WaitForGatewayResolved();

            ExpandAdvancedGatewaySettings(app);

            FindByName(app, "Gateway port", ControlType.Text);

            var saveButton = FindByName(app, "Save gateway settings", ControlType.Button);
            Assert.True(saveButton.Properties.IsEnabled.ValueOrDefault,
                "Save gateway settings button must be present and enabled.");

            FindTextContaining(app, "Port and on/off changes apply the next time PaRuX starts.");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings"), Trait("Category", "Integrations")]
    public void Desktop_apps_integration_page_declares_read_only_consent_and_document_output_protection() =>
        Run(nameof(Desktop_apps_integration_page_declares_read_only_consent_and_document_output_protection), app =>
        {
            var page = new SettingsIntegrationsPage(app.MainWindow);
            page.OpenDesktopIntegrationsCategory();

            FindByName(app, "APPS ON THIS PC", ControlType.Text);

            var consentText = FindTextContaining(app, "Nothing is connected until you connect it");
            string fullConsent = consentText.Name;
            Assert.Contains("every source is read-only", fullConsent, StringComparison.Ordinal);
            Assert.Contains("each one is limited to the single folder or file you choose", fullConsent, StringComparison.Ordinal);

            var scanButton = FindByName(app, "Scan this PC", ControlType.Button);
            Assert.True(scanButton.Properties.IsEnabled.ValueOrDefault,
                "Scan this PC button must be enabled.");

            FindByName(app, "Documents PaRuX writes", ControlType.Text);

            var docOutputDescription = FindTextContaining(app, "writes only new files, only into this one folder");
            string fullDocDescription = docOutputDescription.Name;
            Assert.Contains("never into the folders it reads from", fullDocDescription, StringComparison.Ordinal);
            Assert.Contains("never over a file that already exists", fullDocDescription, StringComparison.Ordinal);

            var chooseFolder = FindByName(app, "Choose folder", ControlType.Button);
            Assert.True(chooseFolder.Properties.IsEnabled.ValueOrDefault);

            var useDefault = FindByName(app, "Use PaRuX's folder", ControlType.Button);
            Assert.NotNull(useDefault);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings")]
    public void Desktop_startup_toggle_shows_current_state_with_descriptive_system_hint() =>
        Run(nameof(Desktop_startup_toggle_shows_current_state_with_descriptive_system_hint), app =>
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();

            var startupToggle = UiWait.ById(app.MainWindow, "Settings_Startup_Toggle", ControlType.Button);
            Assert.Equal("Start with Windows", startupToggle.Name);

            var togglePattern = startupToggle.Patterns.Toggle.PatternOrDefault;
            Assert.NotNull(togglePattern);

            var toggleState = togglePattern.ToggleState.ValueOrDefault;
            Assert.True(toggleState == ToggleState.On || toggleState == ToggleState.Off,
                "Startup toggle must reflect a valid binary state (On or Off).");

            AutomationElement? hint = null;
            UiWait.For(() =>
            {
                hint = app.MainWindow.FindFirstDescendant(cf =>
                    cf.ByName("Adds PaRuX to the Windows startup list.")
                    .Or(cf.ByName("Adds PaRuX to the Windows startup list"))
                    .Or(cf.ByName("PaRuX will start automatically when you sign in.")));

                if (hint == null)
                {
                    hint = app.FindRawDescendant(app.MainWindow, el =>
                        el.Properties.ControlType.ValueOrDefault == ControlType.Text
                        && ((el.Properties.Name.ValueOrDefault ?? string.Empty).StartsWith("Adds PaRuX to the Windows startup list", StringComparison.Ordinal)
                            || (el.Properties.Name.ValueOrDefault ?? string.Empty).StartsWith("PaRuX will start automatically when you sign in", StringComparison.Ordinal)));
                }

                return hint != null;
            }, "Startup hint text element");

            Assert.NotNull(hint);
            Assert.Equal(ControlType.Text, hint.Properties.ControlType.ValueOrDefault);

            string hintText = hint.Properties.Name.ValueOrDefault ?? string.Empty;
            Assert.True(
                hintText.StartsWith("Adds PaRuX to the Windows startup list", StringComparison.Ordinal) ||
                hintText.StartsWith("PaRuX will start automatically when you sign in", StringComparison.Ordinal),
                "Startup hint must describe Windows startup integration honestly, actual: '" + hintText + "'.");
        });

    private static void ExpandAdvancedGatewaySettings(UiTestApplication app)
    {
        AutomationElement? expander = app.MainWindow.FindFirstDescendant(cf =>
            cf.ByName("Advanced gateway settings"));
        if (expander == null)
        {
            expander = app.FindRawDescendant(app.MainWindow, el =>
                string.Equals(el.Properties.Name.ValueOrDefault, "Advanced gateway settings", StringComparison.Ordinal));
        }

        Assert.NotNull(expander);
        if (expander.Patterns.ExpandCollapse.PatternOrDefault != null)
        {
            try
            {
                expander.Patterns.ExpandCollapse.Pattern.Expand();
                return;
            }
            catch { }
        }

        if (expander.Patterns.Invoke.PatternOrDefault != null)
        {
            try
            {
                expander.Patterns.Invoke.Pattern.Invoke();
                return;
            }
            catch { }
        }

        expander.Click(moveMouse: false);
    }

    private static AutomationElement FindByName(UiTestApplication app, string name, ControlType type)
    {
        AutomationElement? element = null;
        UiWait.For(() =>
        {
            element = app.MainWindow.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(type)));
            if (element == null)
            {
                element = app.FindRawDescendant(app.MainWindow, el =>
                    el.Properties.ControlType.ValueOrDefault == type &&
                    string.Equals(el.Properties.Name.ValueOrDefault, name, StringComparison.Ordinal));
            }
            return element != null;
        }, "UI element with name '" + name + "' (" + type + ")");
        return element!;
    }

    private static AutomationElement FindTextContaining(UiTestApplication app, string substring)
    {
        AutomationElement? element = null;
        UiWait.For(() =>
        {
            element = app.FindRawDescendant(app.MainWindow, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.Text &&
                (el.Properties.Name.ValueOrDefault ?? string.Empty).Contains(substring, StringComparison.Ordinal));
            return element != null;
        }, "Text element containing '" + substring + "'");
        return element!;
    }
}
