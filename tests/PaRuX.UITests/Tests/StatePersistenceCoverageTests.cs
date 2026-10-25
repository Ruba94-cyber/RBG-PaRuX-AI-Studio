using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.Data.Sqlite;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using System.Text.Json;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class StatePersistenceCoverageTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Persistence"), Trait("Category", "Navigation")]
    public void Restart_resets_workspace_navigation_to_chat_home()
    {
        const string testName = nameof(Restart_resets_workspace_navigation_to_chat_home);
        using var initial = new UiTestApplication(testName);
        var main = new MainWindowPage(initial.MainWindow);
        main.OpenProjects();
        var projectsItem = UiWait.ById(initial.MainWindow, "Navigation_Projects_Item", ControlType.ListItem);
        UiWait.For(() => projectsItem.Patterns.SelectionItem.Pattern.IsSelected, "Projects workspace selected before restart");

        using var restarted = initial.Restart("workspace-reset-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);
            var prompt = UiWait.ById(restarted.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
            Assert.False(prompt.Properties.IsOffscreen.ValueOrDefault);

            var send = UiWait.ById(restarted.MainWindow, "Chat_Send_Button", ControlType.Button);
            Assert.False(send.Properties.IsOffscreen.ValueOrDefault);

            var projectsItemAfter = UiWait.ById(restarted.MainWindow, "Navigation_Projects_Item", ControlType.ListItem);
            Assert.False(projectsItemAfter.Patterns.SelectionItem.Pattern.IsSelected,
                "Projects workspace should not remain selected after restart; PaRuX returns to Chat.");
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("workspace-reset-restart", ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Persistence"), Trait("Category", "Settings")]
    public void Restart_preserves_theme_choice_in_settings_general()
    {
        const string testName = nameof(Restart_preserves_theme_choice_in_settings_general);
        using var initial = new UiTestApplication(testName);
        var main = new MainWindowPage(initial.MainWindow);
        main.OpenSettings();

        var themeCombo = UiWait.ById(initial.MainWindow, "Settings_Theme_ComboBox", ControlType.ComboBox);
        SelectThemeOption(initial, themeCombo, "Dark");

        UiWait.For(() =>
        {
            if (!File.Exists(initial.SettingsPath)) return false;
            using var settings = JsonDocument.Parse(File.ReadAllText(initial.SettingsPath));
            return settings.RootElement.TryGetProperty("Theme", out var theme)
                && string.Equals(theme.GetString(), "Dark", StringComparison.Ordinal);
        }, "Theme setting Dark persisted to settings.json before restart");

        using var restarted = initial.Restart("theme-persistence-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);
            using (var settings = JsonDocument.Parse(File.ReadAllText(restarted.SettingsPath)))
            {
                Assert.Equal("Dark", settings.RootElement.GetProperty("Theme").GetString());
            }

            var restartedMain = new MainWindowPage(restarted.MainWindow);
            restartedMain.OpenSettings();

            var restartedThemeCombo = UiWait.ById(restarted.MainWindow, "Settings_Theme_ComboBox", ControlType.ComboBox);
            restartedThemeCombo.Patterns.ExpandCollapse.Pattern.Expand();
            AutomationElement? darkItem = null;
            UiWait.For(() =>
            {
                darkItem = restarted.FindRawDescendant(restarted.MainWindow, element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.ListItem
                    && string.Equals(element.Properties.Name.ValueOrDefault, "Dark", StringComparison.Ordinal));
                return darkItem != null;
            }, "Dark option in restarted Theme combo");

            Assert.True(darkItem!.Patterns.SelectionItem.Pattern.IsSelected,
                "The persisted 'Dark' theme item should be selected in the Settings Theme ComboBox.");
            restartedThemeCombo.Patterns.ExpandCollapse.Pattern.Collapse();
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("theme-persistence-restart", ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Persistence"), Trait("Category", "Chat")]
    public void Restart_does_not_persist_uncommitted_chat_composer_draft()
    {
        const string testName = nameof(Restart_does_not_persist_uncommitted_chat_composer_draft);
        using var initial = new UiTestApplication(testName);
        var chat = new ChatPage(initial.MainWindow);
        string draftText = "Uncommitted draft prompt " + Guid.NewGuid().ToString("N");
        chat.EnterPrompt(draftText);
        UiWait.For(() => string.Equals(chat.Prompt.Text, draftText, StringComparison.Ordinal),
            "composer draft text entered before restart");

        using var restarted = initial.Restart("composer-draft-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);
            var prompt = UiWait.ById(restarted.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit).AsTextBox();
            Assert.False(prompt.Properties.IsOffscreen.ValueOrDefault);
            Assert.True(prompt.Properties.IsEnabled.ValueOrDefault);
            Assert.True(string.IsNullOrEmpty(prompt.Text),
                "Uncommitted chat composer draft must not persist across app restart; the composer starts clean.");
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("composer-draft-restart", ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Persistence"), Trait("Category", "Window")]
    public void Restart_does_not_persist_custom_window_size_and_restarts_maximized()
    {
        const string testName = nameof(Restart_does_not_persist_custom_window_size_and_restarts_maximized);
        using var initial = new UiTestApplication(testName);
        initial.ResizeMainWindow(850, 600);
        UiWait.For(() =>
        {
            var visualState = initial.MainWindow.Patterns.Window.PatternOrDefault?.WindowVisualState.ValueOrDefault;
            return visualState != FlaUI.Core.Definitions.WindowVisualState.Maximized;
        }, "window restored from maximized state before restart");

        using var restarted = initial.Restart("window-size-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);
            var visualState = restarted.MainWindow.Patterns.Window.PatternOrDefault?.WindowVisualState.ValueOrDefault;
            if (visualState != null)
            {
                Assert.Equal(FlaUI.Core.Definitions.WindowVisualState.Maximized, visualState);
            }

            var bounds = restarted.MainWindow.BoundingRectangle;
            Assert.True(bounds.Width > 850, "Restarted window width should not stay locked to the pre-restart 850px custom size.");
            Assert.True(bounds.Height > 600, "Restarted window height should not stay locked to the pre-restart 600px custom size.");
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("window-size-restart", ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Persistence"), Trait("Category", "Chat")]
    public void Restart_starts_with_clean_transient_and_busy_state()
    {
        const string testName = nameof(Restart_starts_with_clean_transient_and_busy_state);
        using var initial = new UiTestApplication(testName);
        var sendButton = UiWait.ById(initial.MainWindow, "Chat_Send_Button", ControlType.Button);
        Assert.False(sendButton.Properties.IsOffscreen.ValueOrDefault);

        using var restarted = initial.Restart("clean-transient-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);

            var prompt = UiWait.ById(restarted.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
            Assert.False(prompt.Properties.IsOffscreen.ValueOrDefault);
            Assert.True(prompt.Properties.IsEnabled.ValueOrDefault,
                "Chat prompt input should be enabled and not locked by any busy state after restart.");

            var send = UiWait.ById(restarted.MainWindow, "Chat_Send_Button", ControlType.Button);
            Assert.False(send.Properties.IsOffscreen.ValueOrDefault);
            Assert.True(send.Properties.IsEnabled.ValueOrDefault,
                "Send button should be visible and enabled after restart.");

            // Stop generating button is only displayed during active inference generation; after restart it must not be visible.
            var stopButton = restarted.MainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("Chat_Stop_Button").And(cf.ByControlType(ControlType.Button)));
            if (stopButton != null)
            {
                Assert.True(stopButton.Properties.IsOffscreen.ValueOrDefault,
                    "Chat stop button should be offscreen/collapsed after restart.");
            }
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("clean-transient-restart", ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Persistence"), Trait("Category", "Navigation")]
    public void Restart_preserves_studio_mode_toggle_and_navigation_items()
    {
        const string testName = nameof(Restart_preserves_studio_mode_toggle_and_navigation_items);
        using var initial = new UiTestApplication(testName);
        var main = new MainWindowPage(initial.MainWindow);
        main.EnableAdvanced();

        UiWait.For(() =>
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(initial.SettingsPath));
            return settings.RootElement.GetProperty("StudioModeEnabled").GetBoolean();
        }, "StudioModeEnabled persisted before restart");

        using var restarted = initial.Restart("studio-mode-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);
            using (var settings = JsonDocument.Parse(File.ReadAllText(restarted.SettingsPath)))
            {
                Assert.True(settings.RootElement.GetProperty("StudioModeEnabled").GetBoolean());
            }

            var studioToggle = UiWait.ById(restarted.MainWindow, "MainWindow_StudioMode_Toggle", ControlType.Button);
            var toggleState = studioToggle.Patterns.Toggle.PatternOrDefault?.ToggleState.ValueOrDefault;
            if (toggleState != null)
            {
                Assert.Equal(FlaUI.Core.Definitions.ToggleState.On, toggleState.Value);
            }

            var runtime = UiWait.ById(restarted.MainWindow, "Navigation_Runtime_Item", ControlType.ListItem);
            runtime.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            UiWait.For(() => !runtime.Properties.IsOffscreen.ValueOrDefault, "Advanced Runtime navigation item after restart");
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("studio-mode-restart", ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Persistence"), Trait("Category", "History")]
    public void Restart_preserves_seeded_conversation_history_in_database()
    {
        const string testName = nameof(Restart_preserves_seeded_conversation_history_in_database);
        using var initial = new UiTestApplication(testName, seedConversationHistoryCount: 5);

        using var restarted = initial.Restart("conversation-history-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);
            string dbPath = Path.Combine(restarted.DataRoot, "parux.db");
            Assert.True(File.Exists(dbPath), "parux.db database file should persist across restart");

            using var connection = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM conversations WHERE id LIKE 'ui-history-%'";
            long count = (long)cmd.ExecuteScalar()!;
            Assert.Equal(5L, count);

            var prompt = UiWait.ById(restarted.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
            Assert.False(prompt.Properties.IsOffscreen.ValueOrDefault);
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("conversation-history-restart", ex);
            throw;
        }
    }

    private static void SelectThemeOption(UiTestApplication app, AutomationElement themeCombo, string themeName)
    {
        themeCombo.Patterns.ExpandCollapse.Pattern.Expand();
        AutomationElement? item = null;
        UiWait.For(() =>
        {
            item = app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.ControlType.ValueOrDefault == ControlType.ListItem
                && string.Equals(element.Properties.Name.ValueOrDefault, themeName, StringComparison.Ordinal));
            return item != null;
        }, themeName + " option in Theme ComboBox");
        item!.Patterns.SelectionItem.Pattern.Select();
    }
}
