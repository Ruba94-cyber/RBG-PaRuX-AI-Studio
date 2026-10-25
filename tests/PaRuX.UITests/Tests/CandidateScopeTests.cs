using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class CandidateScopeTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Production_criteria_scope_choice_explains_cost_and_can_be_cancelled()
    {
        string testName = nameof(Production_criteria_scope_choice_explains_cost_and_can_be_cancelled);
        using var app = new UiTestApplication(testName, enableCandidateUtilityPolicyFixture: true);
        try
        {
            new MainWindowPage(app.MainWindow).OpenModelHub();
            // Seed only the previous policy status. The normal production Configure handler
            // opens the scope chooser; its fixture-only editor bypass is not used here.
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateUtilityPolicyReady_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            var configure = UiWait.ById(app.MainWindow, "ModelHub_CandidateUtilityConfigure_Button", ControlType.Button);
            UiWait.For(() => configure.IsEnabled, "criteria configuration enabled");
            configure.Patterns.Invoke.Pattern.Invoke();
            AutomationElement? dialog = null;
            UiWait.For(() =>
            {
                dialog = app.FindRawDescendant(app.MainWindow, e =>
                    e.Properties.AutomationId.ValueOrDefault == "ModelHub_CandidateUtilityScope_Dialog");
                return dialog != null;
            }, "production evidence scope chooser");
            string text = string.Join(" ", dialog!.FindAllDescendants().Select(e => e.Properties.Name.ValueOrDefault));
            Assert.Contains("General chat qualification (30 cases)", text, StringComparison.Ordinal);
            Assert.Contains("Quick checks (4-case smoke)", text, StringComparison.Ordinal);
            Assert.Contains("5 distinct runs", text, StringComparison.Ordinal);
            Assert.Contains("does not start a model", text, StringComparison.Ordinal);
            app.CaptureSnapshot("explicit-candidate-scope-choice");
            AutomationElement cancel = dialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Cancel")));
            Assert.NotNull(cancel);
            cancel.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => app.FindRawDescendant(app.MainWindow, e =>
                e.Properties.AutomationId.ValueOrDefault == "ModelHub_CandidateUtilityScope_Dialog") == null,
                "scope cancellation");
            Assert.Null(app.FindRawDescendant(app.MainWindow, e =>
                e.Properties.AutomationId.ValueOrDefault == "ModelHub_CandidateUtilityConfigure_Dialog"));
        }
        catch (Exception error) { app.CaptureFailure(testName, error); throw; }
    }
}
