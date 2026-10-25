using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Model Hub shows hardware-driven suggestions (as unvalidated candidates) and discovery-feed
/// filters. These checks only observe controls and honest text; no download, ignore or defer is invoked.</summary>
[Collection(UiTestCollection.Name)]
public sealed class ModelHubSuggestionsCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Suggestions_section_names_the_hardware_basis_and_never_stays_blank() => Run(
        nameof(Suggestions_section_names_the_hardware_basis_and_never_stays_blank), app =>
        {
            new MainWindowPage(app.MainWindow).OpenModelHub();

            AutomationElement hardware = UiWait.ById(app.MainWindow, "ModelHub_Suggestion_Hardware_Text", ControlType.Text);
            UiWait.For(() => !string.IsNullOrWhiteSpace(hardware.Name), "suggestion hardware basis text");

            // With an empty maintenance report the honest empty state is shown; otherwise a
            // suggestion card renders its own review action. Either way the section is not blank.
            UiWait.For(() =>
                app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("ModelHub_Suggestion_Empty_Text")) != null
                || app.MainWindow.FindFirstDescendant(cf => cf.ByText("Review & download")) != null,
                "suggestion empty state or a suggestion card");

            var empty = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("ModelHub_Suggestion_Empty_Text"));
            if (empty != null)
                Assert.Contains("suggestions", empty.Name, StringComparison.OrdinalIgnoreCase);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Advanced_candidates_expose_discovery_cohort_filters() => Run(
        nameof(Advanced_candidates_expose_discovery_cohort_filters), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            nav.EnableAdvanced();
            new MainWindowPage(app.MainWindow).OpenModelHub();

            foreach (string id in new[]
            {
                "ModelHub_CohortFilter_All", "ModelHub_CohortFilter_Popular",
                "ModelHub_CohortFilter_New", "ModelHub_CohortFilter_Updated"
            })
                UiWait.ById(app.MainWindow, id, ControlType.Button);
        });
}
