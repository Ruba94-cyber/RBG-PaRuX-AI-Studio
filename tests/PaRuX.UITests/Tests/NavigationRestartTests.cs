using FlaUI.Core.Definitions;
using Microsoft.Data.Sqlite;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using System.Text.Json;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class NavigationRestartTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation")]
    public void Desktop_restart_preserves_advanced_choice_and_history_and_returns_to_chat()
    {
        using var initial = new UiTestApplication(nameof(Desktop_restart_preserves_advanced_choice_and_history_and_returns_to_chat),
            seedConversationHistoryCount: 120);
        var main = new MainWindowPage(initial.MainWindow);
        main.EnableAdvanced();
        main.OpenProjects();
        UiWait.For(() =>
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(initial.SettingsPath));
            return settings.RootElement.GetProperty("StudioModeEnabled").GetBoolean();
        }, "Advanced choice persisted before restart");

        using var restarted = initial.Restart("navigation-persisted-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);
            var prompt = UiWait.ById(restarted.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
            Assert.False(prompt.Properties.IsOffscreen.ValueOrDefault);
            var runtime = UiWait.ById(restarted.MainWindow, "Navigation_Runtime_Item", ControlType.ListItem);
            runtime.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            UiWait.For(() => !runtime.Properties.IsOffscreen.ValueOrDefault, "Advanced Runtime navigation after restart");
            using var database = new SqliteConnection("Data Source=" + Path.Combine(restarted.DataRoot, "parux.db") + ";Mode=ReadOnly");
            database.Open();
            using var count = database.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM conversations WHERE id LIKE 'ui-history-%'";
            Assert.Equal(120L, (long)count.ExecuteScalar()!);
            restarted.CaptureSnapshot("persisted-restart-chat-advanced");
        }
        catch (Exception ex) { restarted.CaptureFailure("persisted-restart", ex); throw; }
    }
}
