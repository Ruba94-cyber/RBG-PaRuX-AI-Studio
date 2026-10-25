using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class ToolsAutomationPage
{
    private readonly UiTestApplication _app;
    private readonly Window _window;
    private readonly MainWindowPage _main;

    internal ToolsAutomationPage(UiTestApplication app)
    {
        _app = app;
        _window = app.MainWindow;
        _main = new MainWindowPage(app.MainWindow);
    }

    internal void Open()
    {
        AutomationElement? toolsItem = _app.FindRawDescendant(_window, el =>
            el.Properties.AutomationId.ValueOrDefault == "Navigation_Tools_Item"
            && !el.Properties.IsOffscreen.ValueOrDefault);
        if (toolsItem == null)
        {
            _main.EnableAdvanced();
        }
        _main.OpenTools();
        WaitForViewLoaded();
    }

    internal void WaitForViewLoaded(TimeSpan? timeout = null)
    {
        UiWait.For(() =>
        {
            var title = _app.FindRawDescendant(_window, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.Text
                && el.Properties.Name.ValueOrDefault == "Tools & Automations");
            return title != null;
        }, "Tools & Automations view loaded", timeout ?? TimeSpan.FromSeconds(30));

        UiWait.For(() =>
        {
            var status = StatusText;
            if (status == null) return false;
            string text = status.Properties.Name.ValueOrDefault ?? string.Empty;
            return !text.StartsWith("Refreshing", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(text);
        }, "Tools & Automations initial refresh resolved", timeout ?? TimeSpan.FromSeconds(30));
    }

    internal AutomationElement TitleText
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && el.Properties.Name.ValueOrDefault == "Tools & Automations");
                return element != null;
            }, "Tools & Automations title text");
            return element!;
        }
    }

    internal AutomationElement DescriptionText
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && el.Properties.Name.ValueOrDefault != null
                    && el.Properties.Name.ValueOrDefault.StartsWith("Inspect skill permissions", StringComparison.Ordinal));
                return element != null;
            }, "Tools & Automations description text");
            return element!;
        }
    }

    internal AutomationElement RefreshAllButton
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && el.Properties.Name.ValueOrDefault == "Refresh all");
                return element != null;
            }, "Refresh all button");
            return element!;
        }
    }

    internal AutomationElement? StatusText =>
        _app.FindRawDescendant(_window, el =>
        {
            if (el.Properties.ControlType.ValueOrDefault != ControlType.Text) return false;
            string? text = el.Properties.Name.ValueOrDefault;
            if (string.IsNullOrEmpty(text)) return false;
            return text.StartsWith("Refreshing", StringComparison.Ordinal)
                || text.StartsWith("Tools and automation state", StringComparison.Ordinal)
                || text.StartsWith("Refresh failed:", StringComparison.Ordinal)
                || text.Contains("required", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Core validation", StringComparison.OrdinalIgnoreCase)
                || text.Contains("created", StringComparison.OrdinalIgnoreCase)
                || text.Contains("failed", StringComparison.OrdinalIgnoreCase);
        });

    internal AutomationElement? FindSectionHeader(string title) =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && el.Properties.Name.ValueOrDefault == title);

    internal AutomationElement? FindEmptySkillsText() =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && el.Properties.Name.ValueOrDefault == "No skills registered.");

    internal AutomationElement? FindEmptyMcpText() =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && el.Properties.Name.ValueOrDefault == "No outbound MCP servers configured.");

    internal AutomationElement? FindEmptyAutomationsText() =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && el.Properties.Name.ValueOrDefault == "No automations yet.");

    internal AutomationElement? FindEmptyWorkflowsText() =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && el.Properties.Name.ValueOrDefault == "No workflow templates yet.");

    internal AutomationElement AddMcpButton
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && el.Properties.Name.ValueOrDefault == "Add MCP server");
                return element != null;
            }, "Add MCP server button");
            return element!;
        }
    }

    internal AutomationElement CreateAutomationButton
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && el.Properties.Name.ValueOrDefault == "Create automation");
                return element != null;
            }, "Create automation button");
            return element!;
        }
    }

    internal AutomationElement CreateWorkflowButton
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && el.Properties.Name.ValueOrDefault == "Create workflow template");
                return element != null;
            }, "Create workflow template button");
            return element!;
        }
    }

    internal AutomationElement? FindMcpPresetComboBox()
    {
        var addBtn = AddMcpButton;
        var walker = _app.Automation.TreeWalkerFactory.GetRawViewWalker();
        var row = walker.GetParent(addBtn);
        if (row != null)
        {
            var panel = walker.GetParent(row);
            if (panel != null)
            {
                var combo = panel.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox));
                if (combo != null) return combo;
            }
        }
        return _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.ComboBox
            && (el.Properties.Name.ValueOrDefault == "Preset (optional)"
                || el.Properties.HelpText.ValueOrDefault == "Preset (optional)"));
    }

    internal AutomationElement? FindAutomationTriggerComboBox()
    {
        var createBtn = CreateAutomationButton;
        var walker = _app.Automation.TreeWalkerFactory.GetRawViewWalker();
        var panel = walker.GetParent(createBtn);
        if (panel != null)
        {
            var combo = panel.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox));
            if (combo != null) return combo;
        }
        return _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.ComboBox
            && el.Properties.AutomationId.ValueOrDefault != "Settings_Theme_ComboBox"
            && el.Properties.AutomationId.ValueOrDefault != "Create_EditMode_ComboBox");
    }

    internal AutomationElement? FindWorkflowStepsTextBox()
    {
        return _app.FindRawDescendant(_window, el =>
        {
            if (el.Properties.ControlType.ValueOrDefault != ControlType.Edit) return false;
            string id = el.Properties.AutomationId.ValueOrDefault;
            if (id == "MainWindow_ChatSearch_TextBox" || id == "Chat_Prompt_TextBox") return false;
            string text = el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault
                ?? (el.Patterns.Value.PatternOrDefault != null ? el.AsTextBox().Text : string.Empty);
            return text.Contains("Plan", StringComparison.Ordinal)
                || text.Contains("Execute", StringComparison.Ordinal);
        });
    }

    internal void ClickRefreshAll()
    {
        _window.SetForeground();
        ClickElement(RefreshAllButton);
    }

    internal void ClickAddMcp()
    {
        _window.SetForeground();
        ClickElement(AddMcpButton);
    }

    internal void ClickCreateAutomation()
    {
        _window.SetForeground();
        ClickElement(CreateAutomationButton);
    }

    internal void ClickCreateWorkflow()
    {
        _window.SetForeground();
        ClickElement(CreateWorkflowButton);
    }

    internal static void ClickElement(AutomationElement element)
    {
        if (element.Patterns.ScrollItem.PatternOrDefault != null)
        {
            try { element.Patterns.ScrollItem.Pattern.ScrollIntoView(); }
            catch { }
        }
        if (element.Patterns.Invoke.PatternOrDefault != null)
        {
            try { element.Patterns.Invoke.Pattern.Invoke(); return; }
            catch { }
        }
        if (element.Patterns.SelectionItem.PatternOrDefault != null)
        {
            try { element.Patterns.SelectionItem.Pattern.Select(); return; }
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
