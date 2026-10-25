using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Model Hub offers "make recommended" and "go back" only when Core says they are legitimately available,
/// and always behind a confirmation. Uses only DEBUG view fixtures: no Core call, model load or routing change.</summary>
[Collection(UiTestCollection.Name)]
public sealed class RecommendationActionsTests
{
    private const string CandidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PromoteId = "ModelHub_CandidatePromote_" + CandidateKey;
    private const string RollbackId = "ModelHub_CandidateRollback_" + CandidateKey;

    private static void Prepare(UiTestApplication app)
    {
        UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
            .Patterns.Invoke.Pattern.Invoke();
        UiWait.ById(app.MainWindow, "ModelHub_TestCandidateFitEligibleHistoryFixture_Button", ControlType.Button)
            .Patterns.Invoke.Pattern.Invoke();
        UiWait.ById(app.MainWindow, "ModelHub_CandidateCompare_" + CandidateKey, ControlType.Button);
    }

    private static AutomationElement? Find(UiTestApplication app, string id) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Promote_is_hidden_until_available_hidden_when_blocked_and_always_asks_first()
    {
        string testName = nameof(Promote_is_hidden_until_available_hidden_when_blocked_and_always_asks_first);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            Prepare(app);
            Assert.Null(Find(app, PromoteId));
            Assert.Null(Find(app, RollbackId));

            // A pin (or Gaming, or a pending change) blocks the offer even for a candidate that met the margin.
            UiWait.ById(app.MainWindow, "ModelHub_TestRecommendationBlockedFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            Assert.Null(Find(app, PromoteId));

            UiWait.ById(app.MainWindow, "ModelHub_TestRecommendationPromotableFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            AutomationElement promote = UiWait.ById(app.MainWindow, PromoteId, ControlType.Button);
            Assert.True(promote.IsEnabled);
            promote.Patterns.Invoke.Pattern.Invoke();

            AutomationElement? notNow = null;
            UiWait.For(() => (notNow = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Not now")))) != null, "confirmation dialog");
            Assert.NotNull(app.MainWindow.FindFirstDescendant(cf => cf.ByName("Make this the recommended model for Chat?")));
            // A second click while the dialog is open must not stack a second dialog.
            try { promote.Patterns.Invoke.Pattern.Invoke(); } catch (Exception) { /* a blocked/modal target is fine */ }
            Thread.Sleep(300);
            Assert.Single(app.MainWindow.FindAllDescendants(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Not now"))));
            notNow!.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByName("Not now")) == null, "dialog closed");
            Assert.NotNull(Find(app, PromoteId)); // declining changed nothing; the offer remains
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Rollback_is_offered_only_on_the_current_winner_when_a_previous_one_exists_and_asks_first()
    {
        string testName = nameof(Rollback_is_offered_only_on_the_current_winner_when_a_previous_one_exists_and_asks_first);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            Prepare(app);
            Assert.Null(Find(app, RollbackId));
            UiWait.ById(app.MainWindow, "ModelHub_TestRecommendationRollbackFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            AutomationElement rollback = UiWait.ById(app.MainWindow, RollbackId, ControlType.Button);
            Assert.True(rollback.IsEnabled);
            Assert.Null(Find(app, PromoteId));
            rollback.Patterns.Invoke.Pattern.Invoke();

            AutomationElement? keep = null;
            UiWait.For(() => (keep = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Keep current")))) != null, "rollback confirmation");
            Assert.NotNull(app.MainWindow.FindFirstDescendant(cf => cf.ByName("Go back to the previous recommendation?")));
            keep!.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByName("Keep current")) == null, "dialog closed");
            Assert.NotNull(Find(app, RollbackId));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }
}
