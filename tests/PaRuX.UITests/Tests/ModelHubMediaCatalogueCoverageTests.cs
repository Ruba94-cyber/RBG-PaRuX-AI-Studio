using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Model Hub media section: Standard / Uncensored lane tabs for the media catalogue. Observes controls only;
/// no download or install is invoked. Compile-checked; run on a desktop session when UI coverage is exercised.</summary>
[Collection(UiTestCollection.Name)]
public sealed class ModelHubMediaCatalogueCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Media_section_offers_standard_and_uncensored_lanes() => Run(
        nameof(Media_section_offers_standard_and_uncensored_lanes), app =>
        {
            new MainWindowPage(app.MainWindow).OpenModelHub();
            // The media catalogue lives on the Media tab of the Model Hub selector bar.
            AutomationElement? mediaTab = null;
            UiWait.For(() => (mediaTab = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("ModelHub_Tab_Media"))) != null,
                "Model Hub Media tab");
            if (mediaTab!.Patterns.SelectionItem.PatternOrDefault != null) mediaTab.Patterns.SelectionItem.Pattern.Select();
            else mediaTab.Click();
            UiWait.ById(app.MainWindow, "ModelHub_MediaLane_Standard", ControlType.Button);
            UiWait.ById(app.MainWindow, "ModelHub_MediaLane_Uncensored", ControlType.Button);
        });
}
