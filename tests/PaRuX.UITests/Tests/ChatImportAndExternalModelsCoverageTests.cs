using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Settings > Data exposes the chat-import and other-app model finder actions. Nothing is imported or scanned.</summary>
[Collection(UiTestCollection.Name)]
public sealed class ChatImportAndExternalModelsCoverageTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings")]
    public void Settings_exposes_import_chats_and_find_models_actions()
    {
        string step = nameof(Settings_exposes_import_chats_and_find_models_actions);
        using var app = new UiTestApplication(step);
        try
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            nav.NavigateTo(WorkspaceNavigationPage.SettingsDescriptor.Id);
            nav.WaitForWorkspace(WorkspaceNavigationPage.SettingsDescriptor);
            UiWait.ById(app.MainWindow, "Settings_Category_Data_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            UiWait.ById(app.MainWindow, "Import_Chats_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "ExternalModels_Find_Button", ControlType.Button);
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Import_Chats_Status")) != null,
                "chat import status element");
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("ExternalModels_Status")) != null,
                "external models status element");
        }
        catch (System.Exception ex) { app.CaptureFailure(step, ex); throw; }
    }
}
