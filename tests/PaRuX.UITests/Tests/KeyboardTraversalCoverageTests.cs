using System;
using System.Collections.Generic;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class KeyboardTraversalCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Navigation"), Trait("Category", "Accessibility")]
    public void Arrow_keys_navigate_between_workspace_navigation_items_with_focus_and_on_screen_bounds() => Run(
        nameof(Arrow_keys_navigate_between_workspace_navigation_items_with_focus_and_on_screen_bounds), app =>
        {
            var page = new KeyboardTraversalPage(app.MainWindow, app);
            string[] navItemIds =
            {
                "Navigation_Create_Item",
                "Navigation_Projects_Item",
                "Navigation_ModelHub_Item",
                "Navigation_Settings_Item",
                "Navigation_Help_Item"
            };

            var windowBounds = app.MainWindow.Properties.BoundingRectangle.ValueOrDefault;

            // Focus the initial workspace item (Create)
            AutomationElement initialItem = UiWait.ById(app.MainWindow, navItemIds[0], ControlType.ListItem);
            initialItem.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            app.MainWindow.SetForeground();
            initialItem.Focus();
            UiWait.For(() => initialItem.Properties.HasKeyboardFocus.ValueOrDefault, navItemIds[0] + " focused");

            // Forward traversal using Down arrow
            for (int i = 1; i < navItemIds.Length; i++)
            {
                string targetId = navItemIds[i];
                AutomationElement targetItem = UiWait.ById(app.MainWindow, targetId, ControlType.ListItem);
                page.Tap(VirtualKeyShort.DOWN, targetItem);

                UiWait.For(() =>
                {
                    var el = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(targetId));
                    return el != null && el.Properties.HasKeyboardFocus.ValueOrDefault;
                }, targetId + " receives keyboard focus by Down arrow");

                Assert.False(targetItem.Properties.IsOffscreen.ValueOrDefault, targetId + " must be on-screen when focused.");
                var bounds = targetItem.Properties.BoundingRectangle.ValueOrDefault;
                Assert.True(bounds.Width > 0 && bounds.Height > 0, targetId + " must have valid bounds.");
                Assert.InRange(bounds.Left, windowBounds.Left, windowBounds.Right);
                Assert.InRange(bounds.Top, windowBounds.Top, windowBounds.Bottom);
            }

            // Backward traversal using Up arrow
            for (int i = navItemIds.Length - 2; i >= 0; i--)
            {
                string targetId = navItemIds[i];
                AutomationElement targetItem = UiWait.ById(app.MainWindow, targetId, ControlType.ListItem);
                page.Tap(VirtualKeyShort.UP, targetItem);

                UiWait.For(() =>
                {
                    var el = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(targetId));
                    return el != null && el.Properties.HasKeyboardFocus.ValueOrDefault;
                }, targetId + " receives keyboard focus by Up arrow");

                Assert.False(targetItem.Properties.IsOffscreen.ValueOrDefault, targetId + " must be on-screen when focused.");
            }
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Navigation"), Trait("Category", "Accessibility")]
    public void Space_key_activates_focused_navigation_item_and_focused_settings_category_button() => Run(
        nameof(Space_key_activates_focused_navigation_item_and_focused_settings_category_button), app =>
        {
            var page = new KeyboardTraversalPage(app.MainWindow, app);

            // Focus Navigation_Settings_Item and activate with Space
            AutomationElement settingsNav = UiWait.ById(app.MainWindow, "Navigation_Settings_Item", ControlType.ListItem);
            settingsNav.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            app.MainWindow.SetForeground();
            settingsNav.Focus();
            UiWait.For(() => settingsNav.Properties.HasKeyboardFocus.ValueOrDefault, "Settings nav item receives focus");

            page.Tap(VirtualKeyShort.SPACE, settingsNav, () => settingsNav.Patterns.SelectionItem.PatternOrDefault?.Select());
            UiWait.For(() => settingsNav.Patterns.SelectionItem.PatternOrDefault?.IsSelected == true,
                "Settings nav item selected via Space key");

            // Verify Settings view loaded with default General category (Theme combo visible)
            AutomationElement themeCombo = UiWait.ById(app.MainWindow, "Settings_Theme_ComboBox", ControlType.ComboBox);
            Assert.False(themeCombo.Properties.IsOffscreen.ValueOrDefault, "Theme combo must be visible in General category.");

            // Focus Models category button and activate with Space
            AutomationElement modelsCatBtn = UiWait.ById(app.MainWindow, "Settings_Category_Models_Button", ControlType.Button);
            modelsCatBtn.Focus();
            UiWait.For(() => modelsCatBtn.Properties.HasKeyboardFocus.ValueOrDefault, "Models category button receives focus");

            page.Tap(VirtualKeyShort.SPACE, modelsCatBtn);

            // Verify state change: Models section controls are now visible, General controls hidden
            AutomationElement discoveryCombo = UiWait.ById(app.MainWindow, "Settings_DiscoveryPolicy_Promotion_ComboBox", ControlType.ComboBox);
            UiWait.For(() => !discoveryCombo.Properties.IsOffscreen.ValueOrDefault,
                "Discovery promotion combo visible after activating Models category via Space");

            Assert.True(themeCombo.Properties.IsOffscreen.ValueOrDefault,
                "Theme combo must be collapsed after switching to Models category.");
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Navigation"), Trait("Category", "Accessibility")]
    public void Enter_key_activates_focused_settings_category_button_restoring_general_category() => Run(
        nameof(Enter_key_activates_focused_settings_category_button_restoring_general_category), app =>
        {
            var page = new KeyboardTraversalPage(app.MainWindow, app);
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);

            nav.NavigateTo(WorkspaceNavigationPage.SettingsDescriptor.Id);
            nav.WaitForWorkspace(WorkspaceNavigationPage.SettingsDescriptor);

            // Switch to Models category first
            AutomationElement modelsCatBtn = UiWait.ById(app.MainWindow, "Settings_Category_Models_Button", ControlType.Button);
            modelsCatBtn.Focus();
            page.Tap(VirtualKeyShort.SPACE, modelsCatBtn);
            AutomationElement discoveryCombo = UiWait.ById(app.MainWindow, "Settings_DiscoveryPolicy_Promotion_ComboBox", ControlType.ComboBox);
            UiWait.For(() => !discoveryCombo.Properties.IsOffscreen.ValueOrDefault, "Models category loaded");

            // Now focus General category button and activate using ENTER (Return)
            AutomationElement generalCatBtn = UiWait.ById(app.MainWindow, "Settings_Category_General_Button", ControlType.Button);
            generalCatBtn.Focus();
            UiWait.For(() => generalCatBtn.Properties.HasKeyboardFocus.ValueOrDefault, "General category button receives focus");

            page.Tap(VirtualKeyShort.RETURN, generalCatBtn);

            // Verify state change: General section restored, Theme combo visible again
            AutomationElement themeCombo = UiWait.ById(app.MainWindow, "Settings_Theme_ComboBox", ControlType.ComboBox);
            UiWait.For(() => !themeCombo.Properties.IsOffscreen.ValueOrDefault,
                "Theme combo restored after activating General category via Enter");
            Assert.True(discoveryCombo.Properties.IsOffscreen.ValueOrDefault,
                "Discovery combo must be collapsed after activating General category.");

            // Also test Enter key on New Chat button in nav
            AutomationElement newChatBtn = UiWait.ById(app.MainWindow, "MainWindow_NewChat_Button", ControlType.Button);
            newChatBtn.Focus();
            UiWait.For(() => newChatBtn.Properties.HasKeyboardFocus.ValueOrDefault, "NewChat button receives focus");

            page.Tap(VirtualKeyShort.RETURN, newChatBtn);

            // Verify Chat home workspace restored
            AutomationElement chatPrompt = UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
            UiWait.For(() => !chatPrompt.Properties.IsOffscreen.ValueOrDefault,
                "Chat prompt visible after activating NewChat via Enter key");
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Navigation"), Trait("Category", "Accessibility"), Trait("Category", "Chat")]
    public void Tab_from_navigation_into_chat_reaches_first_interactive_control_and_shift_tab_returns() => Run(
        nameof(Tab_from_navigation_into_chat_reaches_first_interactive_control_and_shift_tab_returns), app =>
        {
            var page = new KeyboardTraversalPage(app.MainWindow, app);

            UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);

            // Start from a navigation pane control
            AutomationElement navElement = UiWait.ById(app.MainWindow, "MainWindow_NewChat_Button", ControlType.Button);
            navElement.Focus();
            UiWait.For(() => navElement.Properties.HasKeyboardFocus.ValueOrDefault, "NewChat button focused in navigation");

            // Tab forward until focus enters the Chat page content
            AutomationElement firstContentControl = page.TabUntil(
                KeyboardTraversalPage.IsInChat,
                "Tab from navigation into Chat content");

            Assert.NotNull(firstContentControl);
            Assert.True(KeyboardTraversalPage.IsInChat(firstContentControl),
                "First interactive control reached must belong to Chat content.");
            Assert.False(firstContentControl.Properties.IsOffscreen.ValueOrDefault,
                "First interactive control in Chat must be on-screen.");
            var bounds = firstContentControl.Properties.BoundingRectangle.ValueOrDefault;
            Assert.True(bounds.Width > 0 && bounds.Height > 0, "Focused Chat control must have valid bounds.");

            // Shift+Tab back from Chat content into navigation
            AutomationElement returnNavElement = page.ShiftTabUntil(
                KeyboardTraversalPage.IsInNavigation,
                "Shift+Tab from Chat content back into navigation");

            Assert.NotNull(returnNavElement);
            Assert.True(KeyboardTraversalPage.IsInNavigation(returnNavElement),
                "Shift+Tab must return focus to a navigation control.");
            Assert.False(returnNavElement.Properties.IsOffscreen.ValueOrDefault,
                "Returned navigation control must be on-screen.");
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Navigation"), Trait("Category", "Accessibility")]
    public void Tab_from_navigation_into_settings_reaches_first_interactive_control_and_shift_tab_returns() => Run(
        nameof(Tab_from_navigation_into_settings_reaches_first_interactive_control_and_shift_tab_returns), app =>
        {
            var page = new KeyboardTraversalPage(app.MainWindow, app);
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);

            nav.NavigateTo(WorkspaceNavigationPage.SettingsDescriptor.Id);
            nav.WaitForWorkspace(WorkspaceNavigationPage.SettingsDescriptor);

            AutomationElement navItem = UiWait.ById(app.MainWindow, "Navigation_Settings_Item", ControlType.ListItem);
            navItem.Focus();
            UiWait.For(() => navItem.Properties.HasKeyboardFocus.ValueOrDefault, "Settings navigation item focused");

            // Tab forward until focus enters Settings page content
            AutomationElement firstContentControl = page.TabUntil(
                KeyboardTraversalPage.IsInSettings,
                "Tab from navigation into Settings content");

            Assert.NotNull(firstContentControl);
            Assert.True(KeyboardTraversalPage.IsInSettings(firstContentControl),
                "First interactive control reached must belong to Settings content.");
            Assert.False(firstContentControl.Properties.IsOffscreen.ValueOrDefault,
                "First interactive control in Settings must be on-screen.");

            // Shift+Tab back from Settings content into navigation
            AutomationElement returnNavElement = page.ShiftTabUntil(
                KeyboardTraversalPage.IsInNavigation,
                "Shift+Tab from Settings content back into navigation");

            Assert.NotNull(returnNavElement);
            Assert.True(KeyboardTraversalPage.IsInNavigation(returnNavElement),
                "Shift+Tab must return focus to a navigation control.");
            Assert.False(returnNavElement.Properties.IsOffscreen.ValueOrDefault,
                "Returned navigation control must be on-screen.");
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Navigation"), Trait("Category", "Accessibility")]
    public void Tab_from_navigation_into_model_hub_reaches_first_interactive_control_and_shift_tab_returns() => Run(
        nameof(Tab_from_navigation_into_model_hub_reaches_first_interactive_control_and_shift_tab_returns), app =>
        {
            var page = new KeyboardTraversalPage(app.MainWindow, app);
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);

            nav.NavigateTo(WorkspaceNavigationPage.ModelHubDescriptor.Id);
            nav.WaitForWorkspace(WorkspaceNavigationPage.ModelHubDescriptor);

            AutomationElement navItem = UiWait.ById(app.MainWindow, "Navigation_ModelHub_Item", ControlType.ListItem);
            navItem.Focus();
            UiWait.For(() => navItem.Properties.HasKeyboardFocus.ValueOrDefault, "Model Hub navigation item focused");

            // Tab forward until focus enters Model Hub page content
            AutomationElement firstContentControl = page.TabUntil(
                KeyboardTraversalPage.IsInModelHub,
                "Tab from navigation into Model Hub content");

            Assert.NotNull(firstContentControl);
            Assert.True(KeyboardTraversalPage.IsInModelHub(firstContentControl),
                "First interactive control reached must belong to Model Hub content.");
            Assert.False(firstContentControl.Properties.IsOffscreen.ValueOrDefault,
                "First interactive control in Model Hub must be on-screen.");

            // Shift+Tab back from Model Hub content into navigation
            AutomationElement returnNavElement = page.ShiftTabUntil(
                KeyboardTraversalPage.IsInNavigation,
                "Shift+Tab from Model Hub content back into navigation");

            Assert.NotNull(returnNavElement);
            Assert.True(KeyboardTraversalPage.IsInNavigation(returnNavElement),
                "Shift+Tab must return focus to a navigation control.");
            Assert.False(returnNavElement.Properties.IsOffscreen.ValueOrDefault,
                "Returned navigation control must be on-screen.");
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Navigation"), Trait("Category", "Accessibility")]
    public void Escape_key_closes_open_in_app_help_dialog_and_restores_focus_to_invoking_control() => Run(
        nameof(Escape_key_closes_open_in_app_help_dialog_and_restores_focus_to_invoking_control), app =>
        {
            var page = new KeyboardTraversalPage(app.MainWindow, app);

            // Focus Navigation_Help_Item in the navigation pane
            AutomationElement helpItem = UiWait.ById(app.MainWindow, "Navigation_Help_Item", ControlType.ListItem);
            helpItem.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            app.MainWindow.SetForeground();
            helpItem.Focus();
            UiWait.For(() => helpItem.Properties.HasKeyboardFocus.ValueOrDefault, "Navigation_Help_Item focused");

            // Activate Help using Space key to show the modal In-App Help dialog
            page.Tap(VirtualKeyShort.SPACE, helpItem, () => helpItem.Patterns.SelectionItem.PatternOrDefault?.Select());

            // Wait for MainWindow_InAppHelp_Dialog to be displayed
            UiWait.For(() => page.FindDialog("MainWindow_InAppHelp_Dialog") != null,
                "MainWindow_InAppHelp_Dialog displayed after activating Help");

            AutomationElement? dialog = page.FindDialog("MainWindow_InAppHelp_Dialog");
            Assert.NotNull(dialog);
            Assert.False(dialog.Properties.IsOffscreen.ValueOrDefault, "Help dialog must be visible on-screen.");

            // Press Escape to dismiss the dialog
            page.Tap(VirtualKeyShort.ESCAPE);

            // Verify the dialog is dismissed and no longer exists in UIA raw tree
            UiWait.For(() => page.FindDialog("MainWindow_InAppHelp_Dialog") == null,
                "MainWindow_InAppHelp_Dialog dismissed after Escape key press");

            // Verify focus is restored to the invoking control (Navigation_Help_Item)
            UiWait.For(() => helpItem.Properties.HasKeyboardFocus.ValueOrDefault,
                "Keyboard focus restored to Navigation_Help_Item after dialog dismissal");
            Assert.True(helpItem.Properties.HasKeyboardFocus.ValueOrDefault,
                "Invoking navigation control must regain keyboard focus after Escape.");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Accessibility"), Trait("Category", "Chat")]
    public void Cycling_through_sixty_tabs_never_traps_visits_distinct_controls_and_keeps_focus_on_screen() => Run(
        nameof(Cycling_through_sixty_tabs_never_traps_visits_distinct_controls_and_keeps_focus_on_screen), app =>
        {
            var page = new KeyboardTraversalPage(app.MainWindow, app);

            AutomationElement prompt = UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
            prompt.Focus();
            UiWait.For(() => prompt.Properties.HasKeyboardFocus.ValueOrDefault, "Chat prompt initial focus");

            var visitedDescriptors = new HashSet<string>();
            var visitedAutomationIds = new HashSet<string>();
            int consecutiveRepeats = 0;
            string? previousId = null;

            for (int step = 0; step < 60; step++)
            {
                page.Tab();

                AutomationElement? current = null;
                UiWait.For(() =>
                {
                    current = page.GetFocusedElementSafe();
                    return current != null;
                }, $"focused element at Tab step {step + 1}", TimeSpan.FromSeconds(3));

                Assert.NotNull(current);

                // Verify Requirement 6: every focused element is always on-screen
                Assert.False(current.Properties.IsOffscreen.ValueOrDefault,
                    $"Focused element at step {step + 1} must be on-screen.");

                var bounds = current.Properties.BoundingRectangle.ValueOrDefault;
                Assert.True(bounds.Width > 0 && bounds.Height > 0,
                    $"Focused element at step {step + 1} must have non-empty bounding rectangle.");

                string? id = current.Properties.AutomationId.ValueOrDefault;
                string? name = current.Properties.Name.ValueOrDefault;
                ControlType type = current.Properties.ControlType.ValueOrDefault;
                string descriptor = $"{type}:{id ?? name ?? "unnamed"}";

                visitedDescriptors.Add(descriptor);
                if (!string.IsNullOrEmpty(id))
                {
                    visitedAutomationIds.Add(id);
                }

                // Keyboard trap check: focus must not remain stuck on the same element indefinitely
                if (id != null && id == previousId)
                {
                    consecutiveRepeats++;
                    Assert.True(consecutiveRepeats < 4,
                        $"Keyboard focus trapped on '{id}' after multiple consecutive Tab presses.");
                }
                else
                {
                    consecutiveRepeats = 0;
                }
                previousId = id;
            }

            // Verify Requirement 2: visited at least N distinct controls
            Assert.True(visitedDescriptors.Count >= 6,
                $"Expected at least 6 distinct controls visited during 60 Tab presses, but visited {visitedDescriptors.Count}: {string.Join(", ", visitedDescriptors)}");

            // Verify focus cycled out of Chat into navigation or wrapped around
            bool visitedNavigation = visitedAutomationIds.Any(KeyboardTraversalPage.IsNavigationAutomationId);
            Assert.True(visitedNavigation,
                "Cycling 60 Tab presses must exit the Chat page content and traverse navigation or wrap around.");
        });
}
