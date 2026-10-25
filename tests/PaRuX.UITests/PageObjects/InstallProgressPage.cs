using System;
using System.Collections.Generic;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class InstallProgressPage
{
    private readonly UiTestApplication _app;
    private readonly Window _window;
    private readonly MainWindowPage _main;

    internal InstallProgressPage(UiTestApplication app)
    {
        _app = app;
        _window = app.MainWindow;
        _main = new MainWindowPage(app.MainWindow);
    }

    internal UiTestApplication App => _app;
    internal Window Window => _window;
    internal MainWindowPage MainWindow => _main;

    internal void EnableAdvancedMode()
    {
        var runtimeItem = _window.FindFirstDescendant(cf => cf.ByAutomationId("Navigation_Runtime_Item"));
        if (runtimeItem != null && !runtimeItem.Properties.IsOffscreen.ValueOrDefault)
        {
            return;
        }

        var toggle = UiWait.ById(_window, "MainWindow_StudioMode_Toggle", ControlType.Button);
        Invoke(toggle);
        UiWait.For(() =>
        {
            var item = _window.FindFirstDescendant(cf => cf.ByAutomationId("Navigation_Runtime_Item"));
            return item != null && !item.Properties.IsOffscreen.ValueOrDefault;
        }, "Advanced mode runtime navigation item enabled");
    }

    internal void OpenRuntimeManager()
    {
        EnableAdvancedMode();
        var item = UiWait.ById(_window, "Navigation_Runtime_Item", ControlType.ListItem);
        try { item.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch { }
        UiWait.For(() => !item.Properties.IsOffscreen.ValueOrDefault, "Runtime nav item visible");
        Invoke(item);
        UiWait.For(() => RuntimeManagerTitle != null, "Runtime Manager view active");
    }

    internal void OpenSettingsModelsCategory()
    {
        _main.OpenSettings();
        UiWait.For(() => _window.FindFirstDescendant(cf => cf.ByName("General").And(cf.ByControlType(ControlType.Button))) != null,
            "Settings category rail visible");

        AutomationElement? modelsBtn = null;
        UiWait.For(() =>
        {
            modelsBtn = _app.FindRawDescendant(_window, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.Button
                && string.Equals(el.Properties.Name.ValueOrDefault, "Models", StringComparison.Ordinal));
            return modelsBtn != null;
        }, "Settings Models category button");

        Invoke(modelsBtn!);
        UiWait.For(() => _window.FindFirstDescendant(cf => cf.ByAutomationId("Settings_RefreshComponents_Button")) != null,
            "Settings Runtime Components section visible");
    }

    internal AutomationElement? RuntimeManagerTitle =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && string.Equals(el.Properties.Name.ValueOrDefault, "Runtime Manager", StringComparison.Ordinal));

    internal AutomationElement? RuntimeManagerRefreshButton =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Button
            && string.Equals(el.Properties.Name.ValueOrDefault, "Refresh", StringComparison.Ordinal));

    internal AutomationElement? RuntimeManagerCancelInstallButton =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Button
            && string.Equals(el.Properties.Name.ValueOrDefault, "Cancel active install", StringComparison.Ordinal));

    internal AutomationElement? RuntimeManagerStatus =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && ((el.Properties.Name.ValueOrDefault ?? string.Empty).Contains("component(s) detected or configured", StringComparison.Ordinal)
                || (el.Properties.Name.ValueOrDefault ?? string.Empty).Contains("No Runtime Manager install is active in this view.", StringComparison.Ordinal)
                || (el.Properties.Name.ValueOrDefault ?? string.Empty).Contains("Runtime discovery failed", StringComparison.Ordinal)));

    internal AutomationElement? RuntimeComponentsHeader =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && string.Equals(el.Properties.Name.ValueOrDefault, "Runtime components", StringComparison.Ordinal));

    internal AutomationElement? OnboardingDownloadHeader =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("FirstModelDownloadProgress_Header"));

    internal AutomationElement? OnboardingDownloadStatus =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("FirstModelDownloadProgress_Status"));

    internal AutomationElement? OnboardingDownloadEmptyRow =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && string.Equals(el.Properties.Name.ValueOrDefault, "No downloads yet.", StringComparison.Ordinal));

    internal AutomationElement? FindOnboardingComponentsStatus() =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && ((el.Properties.Name.ValueOrDefault ?? string.Empty).Contains("component(s) checked. Detection never installs or changes software.", StringComparison.Ordinal)
                || string.Equals(el.Properties.Name.ValueOrDefault, "Component state updated.", StringComparison.Ordinal)
                || (el.Properties.Name.ValueOrDefault ?? string.Empty).Contains("Component discovery failed", StringComparison.Ordinal)));

    internal AutomationElement? SettingsRefreshComponentsButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("Settings_RefreshComponents_Button"));

    internal AutomationElement? SettingsComponentsStatus =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("Settings_Components_StatusText"));

    internal AutomationElement? SettingsComponentsProgress =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("Settings_Components_Progress"));

    internal AutomationElement? SettingsComponentsList =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("Settings_Components_List"));

    internal void Invoke(AutomationElement element)
    {
        _window.SetForeground();
        try { element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); }
        catch { }

        if (element.Patterns.Invoke.PatternOrDefault != null)
        {
            try
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return;
            }
            catch { }
        }

        if (element.Patterns.Toggle.PatternOrDefault != null)
        {
            try
            {
                element.Patterns.Toggle.Pattern.Toggle();
                return;
            }
            catch { }
        }

        if (element.Patterns.SelectionItem.PatternOrDefault != null)
        {
            try
            {
                element.Patterns.SelectionItem.Pattern.Select();
                return;
            }
            catch { }
        }

        try
        {
            element.Focus();
            Keyboard.Press(VirtualKeyShort.SPACE);
            return;
        }
        catch { }

        element.Click(moveMouse: false);
    }
}
