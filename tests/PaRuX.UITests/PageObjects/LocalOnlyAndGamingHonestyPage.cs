using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class LocalOnlyAndGamingHonestyPage
{
    private readonly Window _window;
    private readonly UiTestApplication _app;

    internal LocalOnlyAndGamingHonestyPage(Window window, UiTestApplication app)
    {
        _window = window;
        _app = app;
    }

    internal void OpenSettings()
    {
        new MainWindowPage(_window).OpenSettings();
    }

    internal void OpenModelHub()
    {
        new MainWindowPage(_window).OpenModelHub();
    }

    internal void SelectCategory(string categoryId)
    {
        _window.SetForeground();
        var button = UiWait.ById(_window, categoryId, ControlType.Button);
        try
        {
            button.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        }
        catch { }

        if (button.Patterns.Invoke.PatternOrDefault != null)
        {
            try
            {
                button.Patterns.Invoke.Pattern.Invoke();
                return;
            }
            catch { }
        }

        try
        {
            button.Focus();
            Keyboard.Press(VirtualKeyShort.SPACE);
            return;
        }
        catch { }

        button.Click(moveMouse: false);
    }

    internal AutomationElement? FindTextElement(string textSubstring)
    {
        var found = _window.FindFirstDescendant(cf =>
            cf.ByControlType(ControlType.Text).And(cf.ByName(textSubstring)));
        if (found != null) return found;

        return _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && el.Properties.Name.ValueOrDefault != null
            && el.Properties.Name.ValueOrDefault.Contains(textSubstring, StringComparison.Ordinal));
    }

    internal void ScrollSettingsContentUntilOnScreen(AutomationElement target, string description)
    {
        bool IsOnScreen() => !target.Properties.IsOffscreen.ValueOrDefault;
        if (IsOnScreen()) return;

        AutomationElement scrollHost = UiWait.ById(_window, "SettingsContentScroll", ControlType.Pane);
        FlaUI.Core.Patterns.IScrollPattern scroll = scrollHost.Patterns.Scroll.PatternOrDefault
            ?? throw new InvalidOperationException("SettingsContentScroll does not expose UIA ScrollPattern.");
        for (int percent = 0; percent <= 100; percent += 5)
        {
            scroll.SetScrollPercent(-1, percent);
            try
            {
                UiWait.For(IsOnScreen, description, TimeSpan.FromMilliseconds(400));
                return;
            }
            catch (TimeoutException)
            {
                // Search the actual settings content scroll host; child controls can expose unrelated inner scrollers.
            }
        }

        UiWait.For(IsOnScreen, description);
    }

    internal AutomationElement? FindControlByName(string name, ControlType controlType)
    {
        var found = _window.FindFirstDescendant(cf =>
            cf.ByName(name).And(cf.ByControlType(controlType)));
        if (found != null) return found;

        return _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == controlType
            && el.Properties.Name.ValueOrDefault != null
            && (string.Equals(el.Properties.Name.ValueOrDefault, name, StringComparison.Ordinal)
                || el.Properties.Name.ValueOrDefault.Contains(name, StringComparison.Ordinal)));
    }

    internal AutomationElement GetCloudFallbackToggle()
    {
        AutomationElement? toggle = null;
        UiWait.For(() =>
        {
            toggle = _window.FindFirstDescendant(cf =>
                cf.ByAutomationId("CloudFallbackToggle").And(cf.ByControlType(ControlType.Button)))
                ?? _window.FindFirstDescendant(cf => cf.ByAutomationId("CloudFallbackToggle"))
                ?? FindControlByName("Allow a cloud fallback", ControlType.Button);
            return toggle != null;
        }, "CloudFallbackToggle switch");
        return toggle!;
    }

    internal AutomationElement? GetSaveCloudFallbackButton()
    {
        return _window.FindFirstDescendant(cf =>
            cf.ByAutomationId("SaveCloudFallbackButton").And(cf.ByControlType(ControlType.Button)))
            ?? FindControlByName("Save", ControlType.Button);
    }

    internal AutomationElement? GetTestCloudFallbackButton()
    {
        return _window.FindFirstDescendant(cf =>
            cf.ByAutomationId("TestCloudFallbackButton").And(cf.ByControlType(ControlType.Button)))
            ?? FindControlByName("Test connection", ControlType.Button);
    }

    internal AutomationElement? GetCloudFallbackApiKeyBox()
    {
        return _window.FindFirstDescendant(cf =>
            cf.ByAutomationId("CloudFallbackApiKeyBox"))
            ?? FindControlByName("API key", ControlType.Edit)
            ?? _app.FindRawDescendant(_window, el =>
                (el.Properties.ControlType.ValueOrDefault == ControlType.Edit || el.Properties.ControlType.ValueOrDefault == ControlType.Custom)
                && ((el.Properties.AutomationId.ValueOrDefault != null && el.Properties.AutomationId.ValueOrDefault.Contains("ApiKey", StringComparison.OrdinalIgnoreCase))
                    || (el.Properties.Name.ValueOrDefault != null && el.Properties.Name.ValueOrDefault.Contains("API key", StringComparison.OrdinalIgnoreCase))));
    }

    internal void ToggleSwitch(AutomationElement toggle)
    {
        _window.SetForeground();
        try
        {
            toggle.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        }
        catch { }

        if (toggle.Patterns.Toggle.PatternOrDefault != null)
        {
            toggle.Patterns.Toggle.Pattern.Toggle();
            return;
        }

        toggle.Click(moveMouse: false);
    }

    internal AutomationElement GetGamingStatusText()
    {
        AutomationElement? element = null;
        UiWait.For(() =>
        {
            element = _app.FindRawDescendant(_window, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.Text
                && el.Properties.Name.ValueOrDefault != null
                && (el.Properties.Name.ValueOrDefault.Contains("Ready — heavy AI", StringComparison.Ordinal)
                    || el.Properties.Name.ValueOrDefault.Contains("ACTIVE — AI jobs", StringComparison.Ordinal)
                    || el.Properties.Name.ValueOrDefault.Contains("Checking gaming state", StringComparison.Ordinal)));
            return element != null;
        }, "GamingStatusText element");
        return element!;
    }

    internal AutomationElement GetEnterGamingButton()
    {
        AutomationElement? button = null;
        UiWait.For(() =>
        {
            button = FindControlByName("Enter Gaming Mode", ControlType.Button);
            return button != null;
        }, "Enter Gaming Mode button");
        return button!;
    }

    internal AutomationElement GetExitGamingButton()
    {
        AutomationElement? button = null;
        UiWait.For(() =>
        {
            button = FindControlByName("Exit Gaming Mode", ControlType.Button);
            return button != null;
        }, "Exit Gaming Mode button");
        return button!;
    }

    internal AutomationElement GetGamingStatusBadge()
    {
        AutomationElement? badge = null;
        UiWait.For(() =>
        {
            badge = _window.FindFirstDescendant(cf =>
                cf.ByAutomationId("GamingStatusBadge").And(cf.ByControlType(ControlType.Text)))
                ?? _window.FindFirstDescendant(cf => cf.ByAutomationId("GamingStatusBadge"))
                ?? _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && ((el.Properties.AutomationId.ValueOrDefault != null && string.Equals(el.Properties.AutomationId.ValueOrDefault, "GamingStatusBadge", StringComparison.Ordinal))
                        || (!string.IsNullOrWhiteSpace(el.Properties.Name.ValueOrDefault)
                            && (string.Equals(el.Properties.Name.ValueOrDefault, "AIREADY", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(el.Properties.Name.ValueOrDefault, "CHECKING", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(el.Properties.Name.ValueOrDefault, "IDLE", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(el.Properties.Name.ValueOrDefault, "ACTIVE", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(el.Properties.Name.ValueOrDefault, "OFF", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(el.Properties.Name.ValueOrDefault, "READY", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(el.Properties.Name.ValueOrDefault, "RUNNING", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(el.Properties.Name.ValueOrDefault, "STANDBY", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(el.Properties.Name.ValueOrDefault, "GAMINGLOCAL", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(el.Properties.Name.ValueOrDefault, "GAMINGREMOTE", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(el.Properties.Name.ValueOrDefault, "GAMESUSPENDED", StringComparison.OrdinalIgnoreCase)))));
            return badge != null;
        }, "GamingStatusBadge element");
        return badge!;
    }
}
