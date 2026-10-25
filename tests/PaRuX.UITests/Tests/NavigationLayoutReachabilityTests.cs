using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// BIC-06: every workspace in the navigation model must be reachable by real mouse input and by
/// keyboard at minimum, compact and wide window sizes, with no clipped or off-window items.
/// Window sizes only; OS display scaling is intentionally not changed by this suite.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class NavigationLayoutReachabilityTests
{
    private static readonly string[] SimpleWorkspaces =
    {
        "Navigation_Create_Item", "Navigation_Projects_Item", "Navigation_ModelHub_Item", "Navigation_Settings_Item"
    };

    private static readonly string[] AdvancedWorkspaces =
    {
        "Navigation_Create_Item", "Navigation_Projects_Item", "Navigation_ModelHub_Item",
        "Navigation_ModelRouting_Item", "Navigation_Lifecycle_Item", "Navigation_Workflows_Item",
        "Navigation_Tools_Item", "Navigation_Observability_Item", "Navigation_Updates_Item",
        "Navigation_Runtime_Item", "Navigation_Settings_Item"
    };

    public static IEnumerable<object[]> Sizes() => new[]
    {
        new object[] { "narrow", 560, 560 },
        new object[] { "compact", 800, 720 },
        new object[] { "wide", 1400, 900 }
    };

    [Theory, MemberData(nameof(Sizes)), Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "Accessibility")]
    public void Every_workspace_is_reachable_by_mouse_and_keyboard_at_each_window_size(string label, int width, int height)
    {
        string testName = "Bic06_nav_reachability_" + label;
        using var app = new UiTestApplication(testName, seedConversationHistoryCount: 60);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            app.ResizeMainWindow(width, height);
            UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);

            AssertAllReachable(app, SimpleWorkspaces, "simple/" + label);
            EnableAdvancedFromNavigation(app, main, width);
            foreach (string id in AdvancedWorkspaces) UiWait.ById(app.MainWindow, id, ControlType.ListItem);
            app.ResizeMainWindow(width, height);
            AssertAllReachable(app, AdvancedWorkspaces, "advanced/" + label);
            AssertKeyboardTraversal(app, AdvancedWorkspaces, label);
            app.CaptureSnapshot("bic06-nav-" + label);
        }
        catch (Exception ex) { app.CaptureFailure(testName, ex); throw; }
    }

    [Theory, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "Accessibility")]
    [InlineData("tiny", 420, 480)]
    [InlineData("compact", 800, 720)]
    public void Collapsed_navigation_pane_keeps_every_workspace_reachable_and_can_be_reopened(string label, int width, int height)
    {
        string testName = "Bic06_nav_collapsed_pane_" + label;
        using var app = new UiTestApplication(testName, seedConversationHistoryCount: 60);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();
            app.ResizeMainWindow(width, height);
            UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
            AutomationElement toggle = UiWait.ById(app.MainWindow, "TogglePaneButton", ControlType.Button);
            app.MainWindow.SetForeground();
            // Up to 640 px (WindowResponsivenessPolicy.BreakpointCompactMax) the window already auto-collapses the
            // pane to the rail, so the toggle would open it; only wider windows need to be collapsed by the user.
            if (width > 640)
            {
                toggle.Patterns.Invoke.Pattern.Invoke();
            }
            // Collapsed (compact rail) pane: the search box is replaced by an icon, items stay icon-only.
            UiWait.For(() =>
            {
                var search = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("MainWindow_ChatSearch_TextBox"));
                return search == null || search.Properties.IsOffscreen.ValueOrDefault;
            }, "navigation pane collapsed to the compact rail (chat search hidden)");
            AssertAllReachable(app, AdvancedWorkspaces, "collapsed-pane");
            toggle = UiWait.ById(app.MainWindow, "TogglePaneButton", ControlType.Button);
            toggle.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() =>
            {
                var search = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("MainWindow_ChatSearch_TextBox"));
                return search != null && !search.Properties.IsOffscreen.ValueOrDefault;
            }, "navigation pane reopened (chat search visible)");
            AssertAllReachable(app, AdvancedWorkspaces, "reopened-pane", requireChatHome: width >= 800);
            app.CaptureSnapshot("bic06-nav-reopened");
        }
        catch (Exception ex) { app.CaptureFailure(testName, ex); throw; }
    }

    /// <summary>
    /// At widths up to 640 px the window auto-collapses the navigation pane to the icon rail (WindowResponsivenessPolicy).
    /// The Interface toggle lives in the pane header, which is not realized while the pane is collapsed, so a narrow-window
    /// user opens the pane with its navigation button first; this helper takes the same path before toggling Advanced.
    /// </summary>
    private static void EnableAdvancedFromNavigation(UiTestApplication app, MainWindowPage main, int windowWidth)
    {
        if (windowWidth > 640)
        {
            main.EnableAdvanced();
            return;
        }

        AutomationElement paneToggle = UiWait.ById(app.MainWindow, "TogglePaneButton", ControlType.Button);
        app.MainWindow.SetForeground();
        paneToggle.Patterns.Invoke.Pattern.Invoke();
        UiWait.ById(app.MainWindow, "MainWindow_StudioMode_Toggle", ControlType.Button);
        main.EnableAdvanced();

        // Hand the window back in the collapsed rail that this width produces; the Advanced workspaces are checked there.
        paneToggle = UiWait.ById(app.MainWindow, "TogglePaneButton", ControlType.Button);
        app.MainWindow.SetForeground();
        paneToggle.Patterns.Invoke.Pattern.Invoke();
        UiWait.For(() =>
        {
            var search = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("MainWindow_ChatSearch_TextBox"));
            return search == null || search.Properties.IsOffscreen.ValueOrDefault;
        }, "navigation pane collapsed to the rail after enabling Advanced");
    }

    private static void AssertAllReachable(UiTestApplication app, string[] ids, string context, bool requireChatHome = true)
    {
        foreach (string id in ids)
        {
            AutomationElement item = UiWait.ById(app.MainWindow, id, ControlType.ListItem);
            item.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            UiWait.For(() => !item.Properties.IsOffscreen.ValueOrDefault, id + " visible (" + context + ")");
            var window = app.MainWindow.Properties.BoundingRectangle.ValueOrDefault;
            var bounds = item.Properties.BoundingRectangle.ValueOrDefault;
            Assert.True(bounds.Width > 0 && bounds.Height > 0, id + " has empty bounds (" + context + ")");
            Assert.True(bounds.Left >= window.Left && bounds.Right <= window.Right
                && bounds.Top >= window.Top && bounds.Bottom <= window.Bottom,
                id + " is clipped outside the window (" + context + "): " + bounds + " vs " + window);

            WaitUntilLayoutIsStable(item, id + " layout settled (" + context + ")");
            // Real mouse activation at the element's clickable point.
            app.MainWindow.SetForeground();
            UiWait.InputUntil(() =>
            {
                try
                {
                    item.Click();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    if (item.Patterns.SelectionItem.PatternOrDefault != null)
                    {
                        item.Patterns.SelectionItem.Pattern.Select();
                    }
                    else
                    {
                        item.Click(moveMouse: false);
                    }
                }
            }, () => item.Patterns.SelectionItem.PatternOrDefault?.IsSelected == true,
                id + " selected by mouse click (" + context + ")");
            UiWait.For(() =>
            {
                var prompt = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Chat_Prompt_TextBox"));
                return prompt == null || prompt.Properties.IsOffscreen.ValueOrDefault;
            }, "workspace content replaced Chat after clicking " + id + " (" + context + ")");
        }

        if (!requireChatHome) return;
        app.MainWindow.SetForeground();
        new MainWindowPage(app.MainWindow).Click("MainWindow_NewChat_Button", ControlType.Button);
        AutomationElement chat = UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
        UiWait.For(() => !chat.Properties.IsOffscreen.ValueOrDefault, "Chat home restored (" + context + ")");
    }

    // Pane open/close and scroll animations move items for a few frames; clicking a moving target is not a
    // product defect, so wait (bounded) for two identical consecutive bounds before the real mouse click.
    private static void WaitUntilLayoutIsStable(AutomationElement element, string description)
    {
        var previous = element.Properties.BoundingRectangle.ValueOrDefault;
        UiWait.For(() =>
        {
            Thread.Sleep(150);
            var current = element.Properties.BoundingRectangle.ValueOrDefault;
            bool stable = current == previous;
            previous = current;
            return stable;
        }, description, TimeSpan.FromSeconds(5));
    }

    // A human key press holds the key for tens of milliseconds. FlaUI's zero-duration Press can lose the
    // WinUI NavigationViewItem KeyUp, after which the next Space/Enter activation is ignored (verified:
    // 1 of N activations with Press, N of N with a 120 ms hold). Tap models a real key press.
    private static void Tap(VirtualKeyShort key, AutomationElement? fallbackTarget = null, Action? fallbackAction = null)
    {
        try
        {
            Keyboard.Pressing(key);
            Thread.Sleep(120);
            Keyboard.Release(key);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (fallbackTarget != null) fallbackTarget.Focus();
            fallbackAction?.Invoke();
        }
    }

    private static void AssertKeyboardTraversal(UiTestApplication app, string[] ids, string context)
    {
        AutomationElement first = UiWait.ById(app.MainWindow, ids[0], ControlType.ListItem);
        first.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        app.MainWindow.SetForeground();
        first.Focus();
        UiWait.For(() => first.Properties.HasKeyboardFocus.ValueOrDefault, ids[0] + " focused (" + context + ")");
        for (int index = 1; index < ids.Length; index++)
        {
            string id = ids[index];
            AutomationElement current = UiWait.ById(app.MainWindow, id, ControlType.ListItem);
            // The first key after a UIA Focus() can be delivered to the host window before the XAML island owns
            // keyboard focus (the app never sees it), so an effect-less press is repeated, bounded.
            UiWait.InputUntil(() => Tap(VirtualKeyShort.DOWN, current),
                () => app.MainWindow.FindFirstDescendant(cf =>
                    cf.ByAutomationId(id).And(cf.ByControlType(ControlType.ListItem)))
                ?.Properties.HasKeyboardFocus.ValueOrDefault == true, id + " focused by Down (" + context + ")");
            Tap(VirtualKeyShort.SPACE, current, () => current.Patterns.SelectionItem.PatternOrDefault?.Select());
            UiWait.For(() => current.Patterns.SelectionItem.PatternOrDefault?.IsSelected == true,
                id + " activated by Space (" + context + ")");
            current.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            UiWait.For(() => !current.Properties.IsOffscreen.ValueOrDefault,
                id + " visible after keyboard activation (" + context + ")", TimeSpan.FromSeconds(5));
        }
    }
}
