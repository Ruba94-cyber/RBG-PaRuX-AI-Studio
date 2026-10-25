using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Model Hub Simple vs Advanced disclosure: Simple shows the recommendation, search and
/// download sections only; Advanced adds the Candidate discovery controls. No network or download.</summary>
[Collection(UiTestCollection.Name)]
public sealed class ModelHubModesCoverageTests
{
    private static readonly string[] AdvancedOnlyIds =
    {
        "ModelHub_CandidateRefreshButton",
        "ModelHub_CandidateFilter_TextBox",
        "ModelHub_CandidateUtilityPolicyStatus_Text"
    };

    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    private static AutomationElement? Find(UiTestApplication app, string id) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Simple_mode_model_hub_keeps_search_and_hides_candidate_controls() => Run(
        nameof(Simple_mode_model_hub_keeps_search_and_hides_candidate_controls), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            nav.NavigateTo(WorkspaceNavigationPage.ModelHubDescriptor.Id);
            nav.WaitForWorkspace(WorkspaceNavigationPage.ModelHubDescriptor);

            UiWait.ById(app.MainWindow, "ModelHub_Search_TextBox", ControlType.Edit);
            UiWait.ById(app.MainWindow, "ModelHub_Search_Button", ControlType.Button);
            foreach (string id in AdvancedOnlyIds)
            {
                AutomationElement? element = Find(app, id);
                Assert.True(element == null || element.Properties.IsOffscreen.ValueOrDefault,
                    id + " must not be visible in Simple mode.");
            }
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Advanced_mode_model_hub_shows_candidate_controls_above_the_model_sections() => Run(
        nameof(Advanced_mode_model_hub_shows_candidate_controls_above_the_model_sections), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            nav.EnableAdvanced();
            nav.NavigateTo(WorkspaceNavigationPage.ModelHubDescriptor.Id);
            nav.WaitForWorkspace(WorkspaceNavigationPage.ModelHubDescriptor);

            foreach (string id in AdvancedOnlyIds)
                UiWait.For(() => Find(app, id) != null, id + " visible in Advanced mode");
            AutomationElement filter = UiWait.ById(app.MainWindow, "ModelHub_CandidateFilter_TextBox", ControlType.Edit);
            AutomationElement search = UiWait.ById(app.MainWindow, "ModelHub_Search_TextBox", ControlType.Edit);
            Assert.True(filter.BoundingRectangle.Top > search.BoundingRectangle.Top,
                "The search row stays at the top; Candidate controls follow it in Advanced mode.");
        });
}
