using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class MainWindowPage
{
    private readonly Window _window;
    internal MainWindowPage(Window window) => _window = window;
    internal AutomationElement Element(string id, ControlType type) => UiWait.ById(_window, id, type);
    internal void Click(string id, ControlType type)
    {
        _window.SetForeground();
        var element = Element(id, type);
        if (type != ControlType.ListItem)
        {
            try { element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); }
            catch { /* Items in an already-visible pane may not expose ScrollItem. */ }
        }
        if (element.Patterns.Invoke.PatternOrDefault != null)
        {
            try { element.Patterns.Invoke.Pattern.Invoke(); return; }
            catch { /* Some WinUI controls expose Invoke but reject it; fall back to their UIA element click. */ }
        }
        if (element.Patterns.Toggle.PatternOrDefault != null)
        {
            try { element.Patterns.Toggle.Pattern.Toggle(); return; }
            catch { /* Fall through to the stable AutomationId click. */ }
        }
        if (element.Patterns.SelectionItem.PatternOrDefault != null)
        {
            try { element.Patterns.SelectionItem.Pattern.Select(); return; }
            catch { /* Fall back to keyboard/UIA activation if the provider rejects selection. */ }
        }
        try
        {
            // WinUI NavigationViewItem does not reliably provide a clickable point to UIA.
            // Keyboard activation is deterministic and keeps this test independent of screen coordinates.
            element.Focus();
            Keyboard.Press(VirtualKeyShort.SPACE);
            return;
        }
        catch { /* Fall through only for controls that cannot be keyboard focused. */ }
        element.Click(moveMouse: false);
    }
    internal void EnableAdvanced() => Click("MainWindow_StudioMode_Toggle", ControlType.Button);
    internal void OpenSettings() => Click("Navigation_Settings_Item", ControlType.ListItem);
    internal void OpenProjects() => Click("Navigation_Projects_Item", ControlType.ListItem);
    internal void OpenModelHub() => Click("Navigation_ModelHub_Item", ControlType.ListItem);
    internal void OpenTools() => Click("Navigation_Tools_Item", ControlType.ListItem);
    internal void OpenSettingsCategory(string categoryId) => Click(categoryId, ControlType.Button);

}
