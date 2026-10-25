using System;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class KeyboardTraversalPage
{
    private readonly Window _window;
    private readonly UiTestApplication _app;

    internal KeyboardTraversalPage(Window window, UiTestApplication app)
    {
        _window = window;
        _app = app;
    }

    internal void Tap(VirtualKeyShort key, AutomationElement? fallbackTarget = null, Action? fallbackAction = null)
    {
        try
        {
            _window.SetForeground();
            Keyboard.Pressing(key);
            Thread.Sleep(120);
            Keyboard.Release(key);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (fallbackTarget != null)
            {
                try { fallbackTarget.Focus(); } catch { }
            }
            fallbackAction?.Invoke();
        }
    }

    internal void ShiftTab(AutomationElement? fallbackTarget = null, Action? fallbackAction = null)
    {
        try
        {
            _window.SetForeground();
            Keyboard.Pressing(VirtualKeyShort.SHIFT);
            Keyboard.Pressing(VirtualKeyShort.TAB);
            Thread.Sleep(120);
            Keyboard.Release(VirtualKeyShort.TAB);
            Keyboard.Release(VirtualKeyShort.SHIFT);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (fallbackTarget != null)
            {
                try { fallbackTarget.Focus(); } catch { }
            }
            fallbackAction?.Invoke();
        }
    }

    internal void Tab() => Tap(VirtualKeyShort.TAB);

    internal AutomationElement? GetFocusedElementSafe()
    {
        try
        {
            return _app.Automation.FocusedElement();
        }
        catch
        {
            return null;
        }
    }

    internal AutomationElement TabUntil(Func<AutomationElement, bool> predicate, string description, int maxTabs = 15)
    {
        AutomationElement? target = null;
        UiWait.For(() =>
        {
            Tab();
            AutomationElement? current = GetFocusedElementSafe();
            if (current != null && predicate(current))
            {
                target = current;
                return true;
            }
            return false;
        }, description, TimeSpan.FromSeconds(15));

        return target ?? throw new TimeoutException("Timed out waiting to tab until: " + description);
    }

    internal AutomationElement ShiftTabUntil(Func<AutomationElement, bool> predicate, string description, int maxTabs = 15)
    {
        AutomationElement? target = null;
        UiWait.For(() =>
        {
            ShiftTab();
            AutomationElement? current = GetFocusedElementSafe();
            if (current != null && predicate(current))
            {
                target = current;
                return true;
            }
            return false;
        }, description, TimeSpan.FromSeconds(15));

        return target ?? throw new TimeoutException("Timed out waiting to shift+tab until: " + description);
    }

    internal AutomationElement? FindDialog(string automationId) =>
        _app.FindRawDescendant(_window, candidate => candidate.Properties.AutomationId.ValueOrDefault == automationId);

    internal static bool IsNavigationAutomationId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        return id.StartsWith("Navigation_", StringComparison.Ordinal)
            || id.StartsWith("MainWindow_", StringComparison.Ordinal)
            || string.Equals(id, "TogglePaneButton", StringComparison.Ordinal);
    }

    internal static bool IsInNavigation(AutomationElement element) =>
        IsNavigationAutomationId(element.Properties.AutomationId.ValueOrDefault);

    internal static bool IsInChat(AutomationElement element)
    {
        string? id = element.Properties.AutomationId.ValueOrDefault;
        return !string.IsNullOrEmpty(id) && id.StartsWith("Chat_", StringComparison.Ordinal);
    }

    internal static bool IsInSettings(AutomationElement element)
    {
        string? id = element.Properties.AutomationId.ValueOrDefault;
        return !string.IsNullOrEmpty(id) && id.StartsWith("Settings_", StringComparison.Ordinal);
    }

    internal static bool IsInModelHub(AutomationElement element)
    {
        string? id = element.Properties.AutomationId.ValueOrDefault;
        return !string.IsNullOrEmpty(id) && id.StartsWith("ModelHub_", StringComparison.Ordinal);
    }
}
