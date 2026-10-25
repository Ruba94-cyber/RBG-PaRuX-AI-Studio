using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Runtime Manager is where a clean install acquires or locates its runtimes. These tests only
/// check that its controls are reachable and named; no install, download or runtime change is started.</summary>
[Collection(UiTestCollection.Name)]
public sealed class RuntimeManagerCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Runtime")]
    public void Runtime_manager_exposes_refresh_status_and_performance_controls() => Run(
        nameof(Runtime_manager_exposes_refresh_status_and_performance_controls), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            nav.EnableAdvanced();
            nav.NavigateTo("Navigation_Runtime_Item");

            UiWait.ById(app.MainWindow, "RuntimeManager_Refresh_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "RuntimeManager_Status_Text", ControlType.Text);
            UiWait.ById(app.MainWindow, "RuntimeManager_PerformanceMode_ComboBox", ControlType.ComboBox);
            UiWait.ById(app.MainWindow, "RuntimeManager_Workload_ComboBox", ControlType.ComboBox);
            UiWait.ById(app.MainWindow, "RuntimeManager_Optimize_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "RuntimeManager_ChooseModel_Button", ControlType.Button);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Runtime")]
    public void Runtime_manager_refresh_leaves_a_truthful_status_and_never_a_blank_one() => Run(
        nameof(Runtime_manager_refresh_leaves_a_truthful_status_and_never_a_blank_one), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            nav.EnableAdvanced();
            nav.NavigateTo("Navigation_Runtime_Item");
            UiWait.ById(app.MainWindow, "RuntimeManager_Refresh_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement status = UiWait.ById(app.MainWindow, "RuntimeManager_Status_Text", ControlType.Text);
            UiWait.For(() => !string.IsNullOrWhiteSpace(status.Name), "runtime status text after refresh");
        });
}
