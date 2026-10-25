using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Workflows is the version-aware workflow builder. These tests only check that its primary
/// controls are reachable by stable id and that refreshing leaves a truthful status; no workflow is
/// created, run or deleted.</summary>
[Collection(UiTestCollection.Name)]
public sealed class WorkflowsCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    private static void Open(UiTestApplication app)
    {
        var nav = new WorkspaceNavigationPage(app.MainWindow, app);
        nav.EnableAdvanced();
        nav.NavigateTo("Navigation_Workflows_Item");
        UiWait.ById(app.MainWindow, "Workflows_Refresh_Button", ControlType.Button);
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Workflows"), Trait("Category", "FlaUI")]
    public void Workflows_workspace_exposes_title_refresh_new_and_status_controls() => Run(
        nameof(Workflows_workspace_exposes_title_refresh_new_and_status_controls), app =>
        {
            Open(app);

            AutomationElement title = UiWait.ById(app.MainWindow, "Workflows_Title_Text", ControlType.Text);
            Assert.Equal("Workflows", title.Name);
            AutomationElement create = UiWait.ById(app.MainWindow, "Workflows_New_Button", ControlType.Button);
            Assert.True(create.Properties.IsEnabled.ValueOrDefault);
            UiWait.ById(app.MainWindow, "Workflows_Status_Text", ControlType.Text);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Workflows"), Trait("Category", "FlaUI")]
    public void Workflows_refresh_leaves_a_truthful_status_and_never_a_blank_one() => Run(
        nameof(Workflows_refresh_leaves_a_truthful_status_and_never_a_blank_one), app =>
        {
            Open(app);
            UiWait.ById(app.MainWindow, "Workflows_Refresh_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement status = UiWait.ById(app.MainWindow, "Workflows_Status_Text", ControlType.Text);
            UiWait.For(() => !string.IsNullOrWhiteSpace(status.Name), "workflows status text after refresh");
        });
}
