using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class ProjectsWorkspacePage
{
    private readonly UiTestApplication _app;
    private readonly Window _window;
    private readonly MainWindowPage _main;

    internal ProjectsWorkspacePage(UiTestApplication app)
    {
        _app = app;
        _window = app.MainWindow;
        _main = new MainWindowPage(app.MainWindow);
    }

    internal void Open()
    {
        _main.OpenProjects();
        WaitForViewLoaded();
    }

    internal void WaitForViewLoaded(TimeSpan? timeout = null)
    {
        UiWait.For(() =>
        {
            var title = _app.FindRawDescendant(_window, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.Text
                && el.Properties.Name.ValueOrDefault == "Projects & Artifacts");
            return title != null;
        }, "Projects & Artifacts view loaded", timeout ?? TimeSpan.FromSeconds(30));

        UiWait.For(() =>
        {
            var initial = _app.FindRawDescendant(_window, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.Text
                && (el.Properties.Name.ValueOrDefault?.StartsWith("Project workspace", StringComparison.Ordinal) == true
                    || el.Properties.Name.ValueOrDefault == "No durable Projects yet."));
            return initial != null;
        }, "Projects workspace initial data loaded", TimeSpan.FromSeconds(20));
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
                    && el.Properties.Name.ValueOrDefault == "Projects & Artifacts");
                return element != null;
            }, "Projects & Artifacts title text");
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
                    && (el.Properties.Name.ValueOrDefault?.StartsWith("Select a durable Project as the active workspace", StringComparison.Ordinal) == true));
                return element != null;
            }, "Projects workspace description text");
            return element!;
        }
    }

    internal AutomationElement ActiveProjectText
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && (el.Properties.Name.ValueOrDefault?.StartsWith("Active project:", StringComparison.Ordinal) == true));
                return element != null;
            }, "Active project text");
            return element!;
        }
    }

    internal AutomationElement RepositoryText
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && ((el.Properties.Name.ValueOrDefault?.StartsWith("Select a Project before assigning a repository", StringComparison.Ordinal) == true)
                        || (el.Properties.Name.ValueOrDefault?.StartsWith("Session repository:", StringComparison.Ordinal) == true)));
                return element != null;
            }, "Repository guidance or status text");
            return element!;
        }
    }

    internal AutomationElement ChooseRepositoryButton
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && (el.Properties.Name.ValueOrDefault?.StartsWith("Choose Project repository", StringComparison.Ordinal) == true));
                return element != null;
            }, "Choose Project repository button");
            return element!;
        }
    }

    internal AutomationElement ClearRepositoryButton
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && el.Properties.Name.ValueOrDefault == "Clear session repository");
                return element != null;
            }, "Clear session repository button");
            return element!;
        }
    }

    internal AutomationElement ChooseNewProjectRepositoryButton
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && (el.Properties.Name.ValueOrDefault?.StartsWith("Choose repository for new Project", StringComparison.Ordinal) == true));
                return element != null;
            }, "Choose repository for new Project button");
            return element!;
        }
    }

    internal AutomationElement ClearNewProjectRepositoryButton
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && el.Properties.Name.ValueOrDefault == "Clear");
                return element != null;
            }, "Clear new project repository button");
            return element!;
        }
    }

    internal AutomationElement CreateProjectButton
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && el.Properties.Name.ValueOrDefault == "Create Project");
                return element != null;
            }, "Create Project button");
            return element!;
        }
    }

    internal AutomationElement GoalTextBox
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                var createBtn = CreateProjectButton;
                var walker = _app.Automation.TreeWalkerFactory.GetRawViewWalker();
                var parent = walker.GetParent(createBtn);
                if (parent != null)
                {
                    element = parent.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
                    if (element == null)
                    {
                        var prev = walker.GetPreviousSibling(createBtn);
                        if (prev != null)
                        {
                            element = prev.Properties.ControlType.ValueOrDefault == ControlType.Edit
                                ? prev
                                : prev.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
                        }
                    }
                }
                if (element == null)
                {
                    element = _app.FindRawDescendant(_window, el =>
                        el.Properties.ControlType.ValueOrDefault == ControlType.Edit
                        && el.Properties.AutomationId.ValueOrDefault != "MainWindow_ChatSearch_TextBox"
                        && el.Properties.AutomationId.ValueOrDefault != "Chat_Prompt_TextBox"
                        && (el.Properties.Name.ValueOrDefault?.Contains("Goal", StringComparison.OrdinalIgnoreCase) == true
                            || el.Properties.HelpText.ValueOrDefault?.Contains("Goal", StringComparison.OrdinalIgnoreCase) == true));
                }
                return element != null;
            }, "Goal input TextBox");
            return element!;
        }
    }

    internal AutomationElement EmptyProjectsText
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && el.Properties.Name.ValueOrDefault == "No durable Projects yet.");
                return element != null;
            }, "Empty projects text");
            return element!;
        }
    }

    internal AutomationElement? FindEmptyProjectsText() =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && el.Properties.Name.ValueOrDefault == "No durable Projects yet.");

    internal AutomationElement ScopedArtifactsText
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && (el.Properties.Name.ValueOrDefault == "Select a Project to see scoped artifacts."
                        || el.Properties.Name.ValueOrDefault == "No artifact is linked to this Project yet."));
                return element != null;
            }, "Scoped artifacts status text");
            return element!;
        }
    }

    internal AutomationElement AllArtifactsText
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && el.Properties.Name.ValueOrDefault == "Artifact Workspace is empty.");
                return element != null;
            }, "All artifacts empty status text");
            return element!;
        }
    }

    internal AutomationElement StatusText
    {
        get
        {
            AutomationElement? element = null;
            UiWait.For(() =>
            {
                element = _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && (el.Properties.Name.ValueOrDefault?.StartsWith("Project workspace", StringComparison.Ordinal) == true
                        || el.Properties.Name.ValueOrDefault?.StartsWith("Created and selected Project", StringComparison.Ordinal) == true
                        || el.Properties.Name.ValueOrDefault?.StartsWith("Project creation failed", StringComparison.Ordinal) == true));
                return element != null;
            }, "Project workspace status text");
            return element!;
        }
    }

    internal AutomationElement FindSectionHeader(string headerText)
    {
        AutomationElement? element = null;
        UiWait.For(() =>
        {
            element = _app.FindRawDescendant(_window, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.Text
                && el.Properties.Name.ValueOrDefault == headerText);
            return element != null;
        }, "Section header " + headerText);
        return element!;
    }

    internal void SetGoal(string goal)
    {
        _window.SetForeground();
        var box = GoalTextBox;
        box.Focus();
        if (box.Patterns.Value.PatternOrDefault != null)
        {
            try { box.Patterns.Value.Pattern.SetValue(goal); }
            catch { }
        }
        string currentText = string.Empty;
        try { currentText = box.AsTextBox().Text; } catch { }
        if (!string.Equals(currentText, goal, StringComparison.Ordinal))
        {
            try
            {
                box.Click(moveMouse: false);
                Keyboard.Press(VirtualKeyShort.CONTROL);
                Keyboard.Press(VirtualKeyShort.KEY_A);
                Keyboard.Release(VirtualKeyShort.KEY_A);
                Keyboard.Release(VirtualKeyShort.CONTROL);
                Keyboard.Press(VirtualKeyShort.BACK);
                if (!string.IsNullOrEmpty(goal))
                {
                    Keyboard.Type(goal);
                }
            }
            catch { }
        }
    }

    internal void ClickCreateProject()
    {
        _window.SetForeground();
        ClickElement(CreateProjectButton);
    }

    internal void CreateProject(string goal)
    {
        SetGoal(goal);
        ClickCreateProject();
    }

    internal AutomationElement? FindProjectButton(string goal)
    {
        var textElement = _app.FindRawDescendant(_window, el =>
            el.Properties.ControlType.ValueOrDefault == ControlType.Text
            && el.Properties.Name.ValueOrDefault?.StartsWith(goal, StringComparison.Ordinal) == true);
        if (textElement == null) return null;

        var parent = _app.Automation.TreeWalkerFactory.GetRawViewWalker().GetParent(textElement);
        if (parent == null) return null;
        // WinUI grid rows are not exposed as UIA containers, so the row label and its
        // action button are flat siblings: take the first Button that follows the label.
        var walker = _app.Automation.TreeWalkerFactory.GetRawViewWalker();
        for (var sibling = walker.GetNextSibling(textElement); sibling != null; sibling = walker.GetNextSibling(sibling))
        {
            if (sibling.Properties.ControlType.ValueOrDefault == ControlType.Button) return sibling;
            if (sibling.Properties.ControlType.ValueOrDefault == ControlType.Text
                && sibling.Properties.Name.ValueOrDefault?.StartsWith(goal, StringComparison.Ordinal) == true) return null;
        }
        return null;
    }

    internal AutomationElement WaitForProjectButton(string goal, TimeSpan? timeout = null)
    {
        AutomationElement? button = null;
        UiWait.For(() =>
        {
            button = FindProjectButton(goal);
            return button != null;
        }, "Project button for " + goal, timeout ?? TimeSpan.FromSeconds(25));
        return button!;
    }

    internal void SelectProject(string goal)
    {
        var button = WaitForProjectButton(goal);
        ClickElement(button);
    }

    internal void WaitForActiveProject(string goalSubstring, TimeSpan? timeout = null)
    {
        UiWait.For(() =>
        {
            var el = _app.FindRawDescendant(_window, e =>
                e.Properties.ControlType.ValueOrDefault == ControlType.Text
                && (e.Properties.Name.ValueOrDefault?.StartsWith("Active project:", StringComparison.Ordinal) == true)
                && (e.Properties.Name.ValueOrDefault?.Contains(goalSubstring, StringComparison.Ordinal) == true));
            return el != null;
        }, "Active project containing " + goalSubstring, timeout ?? TimeSpan.FromSeconds(25));
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
