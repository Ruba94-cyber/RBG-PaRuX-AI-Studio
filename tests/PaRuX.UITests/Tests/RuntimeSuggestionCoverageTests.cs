using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Controls for the runtime "Suggested for this PC" card and the Settings "Automatic checks" section must exist
/// and be named. Nothing is installed, dismissed or changed by these tests.</summary>
[Collection(UiTestCollection.Name)]
public sealed class RuntimeSuggestionCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Runtime")]
    public void Runtime_manager_shows_the_suggested_for_this_pc_card_with_a_truthful_status() => Run(
        nameof(Runtime_manager_shows_the_suggested_for_this_pc_card_with_a_truthful_status), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            nav.EnableAdvanced();
            nav.NavigateTo("Navigation_Runtime_Item");

            UiWait.ById(app.MainWindow, "RuntimeManager_Suggestion_Card", ControlType.Group);
            // Either an explicit "no suggestions / unavailable" status or at least one suggestion item must be present.
            UiWait.For(() =>
                app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("RuntimeManager_Suggestion_Status_Text")) != null
                || app.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Dismiss"))) != null,
                "suggestion status text or a suggestion with a Dismiss action");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings")]
    public void Settings_models_category_exposes_automatic_check_controls() => Run(
        nameof(Settings_models_category_exposes_automatic_check_controls), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            nav.NavigateTo(WorkspaceNavigationPage.SettingsDescriptor.Id);
            nav.WaitForWorkspace(WorkspaceNavigationPage.SettingsDescriptor);

            UiWait.ById(app.MainWindow, "Settings_Category_Models_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            UiWait.ById(app.MainWindow, "Settings_AutoCheck_Enabled_Toggle", ControlType.Button);
            UiWait.ById(app.MainWindow, "Settings_AutoCheck_Interval_Box", ControlType.Spinner);
            UiWait.ById(app.MainWindow, "Settings_AutoCheck_CheckNow_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "Settings_AutoCheck_LastRun_Text", ControlType.Text);
            UiWait.ById(app.MainWindow, "Settings_AutoCheck_RuntimeUpdate_Toggle", ControlType.Button);
            UiWait.ById(app.MainWindow, "Settings_AutoCheck_RuntimeUpdate_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "Settings_AutoCheck_RuntimeUpdate_Text", ControlType.Text);
        });
}
