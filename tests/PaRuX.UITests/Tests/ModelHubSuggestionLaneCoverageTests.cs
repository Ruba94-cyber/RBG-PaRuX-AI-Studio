using System;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Suggested-for-this-PC lanes: Standard and Uncensored tabs exist and stay selectable. Observation only;
/// no download, defer or ignore is invoked.</summary>
[Collection(UiTestCollection.Name)]
public sealed class ModelHubSuggestionLaneCoverageTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Suggestions_expose_standard_and_uncensored_lanes_with_the_content_filter_note()
    {
        using var app = new UiTestApplication(nameof(Suggestions_expose_standard_and_uncensored_lanes_with_the_content_filter_note));
        try
        {
            new MainWindowPage(app.MainWindow).OpenModelHub();
            UiWait.ById(app.MainWindow, "ModelHub_Suggestion_Lane_Standard", ControlType.Button);
            var uncensored = UiWait.ById(app.MainWindow, "ModelHub_Suggestion_Lane_Uncensored", ControlType.Button);
            uncensored.Click();
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByText("Uncensored models have no content filter.", PropertyConditionFlags.MatchSubstring)) != null,
                "uncensored lane note");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Suggestions_expose_standard_and_uncensored_lanes_with_the_content_filter_note), ex); throw; }
    }
}
