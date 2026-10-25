using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class SettingsIntegrationsPage
{
    private readonly Window _window;
    private readonly MainWindowPage _main;

    internal SettingsIntegrationsPage(Window window)
    {
        _window = window;
        _main = new MainWindowPage(window);
    }

    internal void OpenIntegrationsCategory()
    {
        _main.OpenSettings();
        _main.OpenSettingsCategory("Settings_Category_Integrations_Button");
    }

    internal void OpenDesktopIntegrationsCategory()
    {
        _main.OpenSettings();
        _main.OpenSettingsCategory("Settings_Category_YourPCapps_Button");
    }

    internal AutomationElement GatewayStateText =>
        UiWait.ById(_window, "Settings_Gateway_State_Text", ControlType.Text);

    internal AutomationElement IntegrationsRefreshButton =>
        UiWait.ById(_window, "Settings_Integrations_Refresh_Button", ControlType.Button);

    internal AutomationElement WorkingFolderTextBox =>
        UiWait.ById(_window, "Settings_Integrations_Folder_Text", ControlType.Edit);

    internal AutomationElement ClientStatusText(string clientAutomationId) =>
        UiWait.ById(_window, "Settings_Integration_" + clientAutomationId + "_Status_Text", ControlType.Text);

    internal void WaitForGatewayResolved(TimeSpan? timeout = null)
    {
        var state = GatewayStateText;
        UiWait.For(() => !state.Name.StartsWith("Checking", StringComparison.Ordinal),
            "gateway state resolved", timeout ?? TimeSpan.FromSeconds(60));
    }

    internal AutomationElement? FindDescendant(ControlType type, string name)
    {
        return _window.FindFirstDescendant(cf => cf.ByControlType(type).And(cf.ByName(name)));
    }
}
