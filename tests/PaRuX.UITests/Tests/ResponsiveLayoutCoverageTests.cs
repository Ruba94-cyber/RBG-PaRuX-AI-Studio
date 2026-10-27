using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ResponsiveLayoutCoverageTests
{
    private static readonly (int Width, int Height, string Label)[] TargetSizes =
    {
        (560, 560, "560x560"),
        (800, 600, "800x600"),
        (1280, 800, "1280x800"),
        (1920, 1080, "1920x1080")
    };

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "FlaUI")]
    public void Chat_header_and_primary_controls_adapt_responsively_across_window_sizes()
    {
        const string testName = nameof(Chat_header_and_primary_controls_adapt_responsively_across_window_sizes);
        using var app = new UiTestApplication(testName);
        var originalBounds = SaveWindowBounds(app);
        try
        {
            foreach (var (width, height, label) in TargetSizes)
            {
                ResizeWindow(app, width, height);

                var prompt = UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
                var send = UiWait.ById(app.MainWindow, "Chat_Send_Button", ControlType.Button);
                var title = UiWait.ById(app.MainWindow, "Chat_Header_Title_Text", ControlType.Text);
                var profileText = UiWait.ById(app.MainWindow, "Chat_Header_Profile_Text", ControlType.Text);
                var profileSelector = FindById(app.MainWindow, "Chat_ProfileSelector_ScrollViewer");

                AssertInsideWindow(app.MainWindow, prompt, $"Chat prompt at {label}");
                AssertInsideWindow(app.MainWindow, send, $"Chat send button at {label}");
                AssertInsideWindow(app.MainWindow, title, $"Chat header title at {label}");
                AssertInsideWindow(app.MainWindow, profileText, $"Chat header profile at {label}");
                AssertInsideWindow(app.MainWindow, profileSelector, $"Chat profile selector at {label}");

                AssertNoOverlapWithNav(app, prompt, $"Chat prompt at {label}");
                AssertNoOverlapWithNav(app, title, $"Chat header title at {label}");

                if (width <= 560)
                {
                    UiWait.For(() =>
                    {
                        var tBounds = title.Properties.BoundingRectangle.ValueOrDefault;
                        var pBounds = profileSelector.Properties.BoundingRectangle.ValueOrDefault;
                        return pBounds.Top >= tBounds.Bottom - 5;
                    }, $"Chat header profile selector stacked below title at {label}");
                }
                else if (width >= 1280)
                {
                    UiWait.For(() =>
                    {
                        var tBounds = title.Properties.BoundingRectangle.ValueOrDefault;
                        var pBounds = profileSelector.Properties.BoundingRectangle.ValueOrDefault;
                        return pBounds.Left >= tBounds.Right - 5 && Math.Abs(pBounds.Top - tBounds.Top) < 40;
                    }, $"Chat header profile selector inline with title at {label}");
                }
            }
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
        finally
        {
            RestoreWindow(app, originalBounds);
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "FlaUI")]
    public void Settings_layout_and_primary_controls_remain_bounded_across_window_sizes()
    {
        const string testName = nameof(Settings_layout_and_primary_controls_remain_bounded_across_window_sizes);
        using var app = new UiTestApplication(testName);
        var originalBounds = SaveWindowBounds(app);
        try
        {
            NavigateToWorkspace(app, "Navigation_Settings_Item");

            foreach (var (width, height, label) in TargetSizes)
            {
                ResizeWindow(app, width, height);

                var themeCombo = UiWait.ById(app.MainWindow, "Settings_Theme_ComboBox", ControlType.ComboBox);
                var startupToggle = UiWait.ById(app.MainWindow, "Settings_Startup_Toggle", ControlType.Button);

                AssertInsideWindow(app.MainWindow, themeCombo, $"Settings theme combo at {label}");
                AssertInsideWindow(app.MainWindow, startupToggle, $"Settings startup switch at {label}");

                AssertNoOverlapWithNav(app, themeCombo, $"Settings theme combo at {label}");
                AssertNoOverlapWithNav(app, startupToggle, $"Settings startup switch at {label}");

                Assert.True(themeCombo.IsEnabled, $"Theme combo must be enabled at {label}");
                Assert.True(startupToggle.IsEnabled, $"Startup switch must be enabled at {label}");
            }
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
        finally
        {
            RestoreWindow(app, originalBounds);
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "FlaUI")]
    public void ModelHub_layout_and_search_controls_remain_bounded_across_window_sizes()
    {
        const string testName = nameof(ModelHub_layout_and_search_controls_remain_bounded_across_window_sizes);
        using var app = new UiTestApplication(testName);
        var originalBounds = SaveWindowBounds(app);
        try
        {
            NavigateToWorkspace(app, "Navigation_ModelHub_Item");

            foreach (var (width, height, label) in TargetSizes)
            {
                ResizeWindow(app, width, height);

                var search = UiWait.ById(app.MainWindow, "ModelHub_Search_TextBox", ControlType.Edit);
                var searchBtn = UiWait.ById(app.MainWindow, "ModelHub_Search_Button", ControlType.Button);
                var refreshBtn = UiWait.ById(app.MainWindow, "ModelHub_Refresh_Button", ControlType.Button);
                var status = UiWait.ById(app.MainWindow, "ModelHub_Status_Text", ControlType.Text);
                var hardware = UiWait.ById(app.MainWindow, "ModelHub_HardwareSummary_Text", ControlType.Text);

                AssertInsideWindow(app.MainWindow, search, $"ModelHub search box at {label}");
                AssertInsideWindow(app.MainWindow, searchBtn, $"ModelHub search button at {label}");
                AssertInsideWindow(app.MainWindow, refreshBtn, $"ModelHub refresh button at {label}");
                AssertInsideWindow(app.MainWindow, status, $"ModelHub status text at {label}");
                AssertInsideWindow(app.MainWindow, hardware, $"ModelHub hardware summary at {label}");

                AssertNoOverlapWithNav(app, search, $"ModelHub search box at {label}");
                AssertNoOverlapWithNav(app, searchBtn, $"ModelHub search button at {label}");

                Assert.True(search.IsEnabled, $"ModelHub search box must be enabled at {label}");
                Assert.True(search.Properties.BoundingRectangle.ValueOrDefault.Width > 50,
                    $"ModelHub search box must maintain a usable width at {label}");
            }
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
        finally
        {
            RestoreWindow(app, originalBounds);
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "FlaUI")]
    public void Create_layout_and_operation_controls_remain_bounded_across_window_sizes()
    {
        const string testName = nameof(Create_layout_and_operation_controls_remain_bounded_across_window_sizes);
        using var app = new UiTestApplication(testName);
        var originalBounds = SaveWindowBounds(app);
        try
        {
            NavigateToWorkspace(app, "Navigation_Create_Item");

            foreach (var (width, height, label) in TargetSizes)
            {
                ResizeWindow(app, width, height);

                var cancelBtn = UiWait.ById(app.MainWindow, "Create_Cancel_Active_Job_Button", ControlType.Button);
                var status = UiWait.ById(app.MainWindow, "Create_Operation_Status_Text", ControlType.Text);

                // Status and cancel sit below the selected mode's panel, so on a small window they are reached by scrolling.
                // Bring them into view first; they must then be fully inside the window and clear of the navigation.
                cancelBtn.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
                status.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
                UiWait.For(() => cancelBtn.Properties.BoundingRectangle.ValueOrDefault.Height > 0
                    && status.Properties.BoundingRectangle.ValueOrDefault.Height > 0,
                    $"Create cancel button and status visible after scrolling at {label}");

                AssertInsideWindow(app.MainWindow, cancelBtn, $"Create cancel button at {label}");
                AssertInsideWindow(app.MainWindow, status, $"Create status text at {label}");

                AssertNoOverlapWithNav(app, cancelBtn, $"Create cancel button at {label}");
                AssertNoOverlapWithNav(app, status, $"Create status text at {label}");

                Assert.True(
                    status.Name.StartsWith("Create is ready", StringComparison.Ordinal)
                    || status.Name.StartsWith("Image generation needs a one-time setup", StringComparison.Ordinal)
                    || status.Name.StartsWith("Checking what image generation needs", StringComparison.Ordinal),
                    $"Create status at {label} must be one of the honest setup messages but was '{status.Name}'");
            }
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
        finally
        {
            RestoreWindow(app, originalBounds);
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "FlaUI")]
    public void Runtime_layout_and_engine_controls_remain_bounded_across_window_sizes()
    {
        const string testName = nameof(Runtime_layout_and_engine_controls_remain_bounded_across_window_sizes);
        using var app = new UiTestApplication(testName);
        var originalBounds = SaveWindowBounds(app);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();
            NavigateToWorkspace(app, "Navigation_Runtime_Item");

            foreach (var (width, height, label) in TargetSizes)
            {
                ResizeWindow(app, width, height);

                var title = FindByName(app, "Runtime Manager", ControlType.Text);
                var refresh = FindByName(app, "Refresh", ControlType.Button);
                var cancel = FindByName(app, "Cancel active install", ControlType.Button);
                var engine = FindByName(app, "Inference Performance Engine", ControlType.Text);

                AssertInsideWindow(app.MainWindow, title, $"Runtime Manager title at {label}");
                AssertInsideWindow(app.MainWindow, refresh, $"Runtime refresh button at {label}");
                AssertInsideWindow(app.MainWindow, cancel, $"Runtime cancel install button at {label}");
                AssertInsideWindow(app.MainWindow, engine, $"Inference Performance Engine heading at {label}");

                AssertNoOverlapWithNav(app, title, $"Runtime Manager title at {label}");
                AssertNoOverlapWithNav(app, refresh, $"Runtime refresh button at {label}");

                Assert.True(refresh.IsEnabled, $"Runtime refresh button must be enabled at {label}");
            }
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
        finally
        {
            RestoreWindow(app, originalBounds);
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "FlaUI")]
    public void Navigation_pane_toggle_maintains_usability_at_narrow_window_size()
    {
        const string testName = nameof(Navigation_pane_toggle_maintains_usability_at_narrow_window_size);
        using var app = new UiTestApplication(testName);
        var originalBounds = SaveWindowBounds(app);
        try
        {
            var main = new MainWindowPage(app.MainWindow);

            ResizeWindow(app, 560, 560, autoAdjustPane: false);

            var toggle = UiWait.ById(app.MainWindow, "TogglePaneButton", ControlType.Button);
            AssertInsideWindow(app.MainWindow, toggle, "TogglePaneButton at narrow width");

            app.MainWindow.SetForeground();
            toggle.Patterns.Invoke.Pattern.Invoke();

            NavigateToWorkspace(app, "Navigation_Settings_Item");
            var themeCombo = UiWait.ById(app.MainWindow, "Settings_Theme_ComboBox", ControlType.ComboBox);
            AssertInsideWindow(app.MainWindow, themeCombo, "Settings theme combo reached from narrow pane");

            var newChatBtn = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("MainWindow_NewChat_Button"));
            if (newChatBtn == null || newChatBtn.Properties.IsOffscreen.ValueOrDefault)
            {
                toggle = UiWait.ById(app.MainWindow, "TogglePaneButton", ControlType.Button);
                toggle.Patterns.Invoke.Pattern.Invoke();
            }

            main.Click("MainWindow_NewChat_Button", ControlType.Button);
            var prompt = UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
            AssertInsideWindow(app.MainWindow, prompt, "Chat prompt restored at narrow width");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
        finally
        {
            RestoreWindow(app, originalBounds);
        }
    }

    private static void NavigateToWorkspace(UiTestApplication app, string navItemId)
    {
        var item = UiWait.ById(app.MainWindow, navItemId, ControlType.ListItem);
        item.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        app.MainWindow.SetForeground();
        // Lower sidebar entries can sit below the fold of a short window; a real mouse click then lands
        // on whatever is drawn there. Select through UI Automation first, click only as a fallback.
        var selection = item.Patterns.SelectionItem.PatternOrDefault;
        if (selection != null)
        {
            selection.Select();
        }
        else
        {
            try
            {
                item.Click();
            }
            catch
            {
                item.Focus();
                Keyboard.Press(VirtualKeyShort.SPACE);
            }
        }

        UiWait.For(() => item.Patterns.SelectionItem.PatternOrDefault?.IsSelected == true,
            $"{navItemId} is selected in navigation", TimeSpan.FromSeconds(5));

        UiWait.For(() =>
        {
            var prompt = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Chat_Prompt_TextBox"));
            return prompt == null || prompt.Properties.IsOffscreen.ValueOrDefault;
        }, $"workspace content replaced Chat after selecting {navItemId}", TimeSpan.FromSeconds(5));
    }

    private static bool IsPaneOpen(UiTestApplication app)
    {
        var search = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("MainWindow_ChatSearch_TextBox"));
        return search != null && !search.Properties.IsOffscreen.ValueOrDefault;
    }

    private static void EnsurePaneForWidth(UiTestApplication app, int width)
    {
        bool shouldBeOpen = width > 560;
        if (IsPaneOpen(app) == shouldBeOpen) return;

        var toggle = UiWait.ById(app.MainWindow, "TogglePaneButton", ControlType.Button);
        app.MainWindow.SetForeground();
        toggle.Patterns.Invoke.Pattern.Invoke();

        UiWait.For(() => IsPaneOpen(app) == shouldBeOpen,
            shouldBeOpen ? "navigation pane reopened for wide layout" : "navigation pane collapsed for narrow layout",
            TimeSpan.FromSeconds(5));
    }

    private static void ResizeWindow(UiTestApplication app, int targetWidth, int targetHeight, bool autoAdjustPane = true)
    {
        try
        {
            var transform = app.MainWindow.Patterns.Transform.PatternOrDefault;
            if (transform != null && transform.CanResize)
            {
                transform.Resize(targetWidth, targetHeight);
            }
        }
        catch
        {
            // Fall back to native SetWindowPos in ResizeMainWindow
        }

        app.ResizeMainWindow(targetWidth, targetHeight);

        UiWait.For(() =>
        {
            var bounds = app.MainWindow.Properties.BoundingRectangle.ValueOrDefault;
            return bounds.Width > 0 && bounds.Height > 0;
        }, $"Window has non-empty bounds after resize to {targetWidth}x{targetHeight}", TimeSpan.FromSeconds(5));

        if (autoAdjustPane)
        {
            EnsurePaneForWidth(app, targetWidth);
        }

        Rectangle last = Rectangle.Empty;
        int stableCount = 0;
        UiWait.For(() =>
        {
            var current = app.MainWindow.Properties.BoundingRectangle.ValueOrDefault;
            if (current == last && current.Width > 0 && current.Height > 0)
            {
                stableCount++;
                if (stableCount >= 2) return true;
            }
            else
            {
                stableCount = 0;
                last = current;
            }
            return false;
        }, $"Layout settled after resize to {targetWidth}x{targetHeight}", TimeSpan.FromSeconds(5));
    }

    private static void AssertInsideWindow(Window window, AutomationElement element, string description)
    {
        var winBounds = window.Properties.BoundingRectangle.ValueOrDefault;
        var elemBounds = element.Properties.BoundingRectangle.ValueOrDefault;
        Assert.True(elemBounds.Width > 0 && elemBounds.Height > 0, $"{description} has empty bounds: {elemBounds}");
        Assert.False(element.Properties.IsOffscreen.ValueOrDefault, $"{description} is marked offscreen");
        Assert.True(
            elemBounds.Left >= winBounds.Left - 10 &&
            elemBounds.Right <= winBounds.Right + 10 &&
            elemBounds.Top >= winBounds.Top - 10 &&
            elemBounds.Bottom <= winBounds.Bottom + 10,
            $"{description} is outside window bounds: element {elemBounds} vs window {winBounds}");
    }

    private static void AssertNoOverlapWithNav(UiTestApplication app, AutomationElement contentElement, string description)
    {
        var toggle = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("TogglePaneButton"));
        if (toggle != null && !toggle.Properties.IsOffscreen.ValueOrDefault)
        {
            AssertNoOverlap(toggle, contentElement, description);
        }

        var navItem = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Navigation_Settings_Item"));
        if (navItem != null && !navItem.Properties.IsOffscreen.ValueOrDefault)
        {
            AssertNoOverlap(navItem, contentElement, description);
        }
    }

    private static void AssertNoOverlap(AutomationElement referenceElement, AutomationElement contentElement, string description)
    {
        var refBounds = referenceElement.Properties.BoundingRectangle.ValueOrDefault;
        var contentBounds = contentElement.Properties.BoundingRectangle.ValueOrDefault;
        if (refBounds.IsEmpty || contentBounds.IsEmpty) return;
        var overlap = Rectangle.Intersect(refBounds, contentBounds);
        Assert.True(overlap.IsEmpty, $"{description}: content element {ElementIdentifier(contentElement)} ({contentBounds}) overlaps reference {ElementIdentifier(referenceElement)} ({refBounds})");
    }

    private static string ElementIdentifier(AutomationElement element)
    {
        string id = element.Properties.AutomationId.ValueOrDefault;
        if (!string.IsNullOrWhiteSpace(id)) return id;
        string name = element.Properties.Name.ValueOrDefault;
        if (!string.IsNullOrWhiteSpace(name)) return $"'{name}'";
        return element.Properties.ControlType.ValueOrDefault.ToString();
    }

    private static AutomationElement FindById(AutomationElement root, string id)
    {
        AutomationElement? element = null;
        UiWait.For(() =>
        {
            element = root.FindFirstDescendant(cf => cf.ByAutomationId(id));
            return element != null;
        }, $"UI element with AutomationId '{id}'");
        return element!;
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
        }, $"UI element '{name}' ({type})");
        return element!;
    }

    private static Rectangle SaveWindowBounds(UiTestApplication app)
    {
        return app.MainWindow.Properties.BoundingRectangle.ValueOrDefault;
    }

    private static void RestoreWindow(UiTestApplication app, Rectangle originalBounds)
    {
        try
        {
            EnsurePaneForWidth(app, 1280);
            if (originalBounds.Width > 0 && originalBounds.Height > 0)
            {
                ResizeWindow(app, originalBounds.Width, originalBounds.Height, autoAdjustPane: false);
            }
            else
            {
                app.MainWindow.Patterns.Window.PatternOrDefault?.SetWindowVisualState(WindowVisualState.Maximized);
            }
        }
        catch
        {
            // Best effort window restore during cleanup
        }
    }
}
