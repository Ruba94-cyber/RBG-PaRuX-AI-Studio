using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingBrowserPostconditionPolicyTests
{
    [Theory]
    [InlineData("browser_click", true)]
    [InlineData("browser_type", true)]
    [InlineData("browser_eval", true)]
    [InlineData("browser_navigate", false)]
    [InlineData("browser_dom", false)]
    [InlineData("browser_screenshot", false)]
    [InlineData("browser_reset", false)]
    [InlineData("browser_assert", false)]
    [InlineData("run_command", false)]
    public void Only_potentially_mutating_browser_calls_require_a_new_typed_assertion(
        string action, bool expected)
    {
        Assert.Equal(expected,
            CodingBrowserPostconditionPolicy.IsMutationAction(action));
    }

    [Theory]
    [InlineData("browser_click", true)]
    [InlineData("browser_click", false)]
    [InlineData("browser_type", true)]
    [InlineData("browser_type", false)]
    [InlineData("browser_eval", true)]
    [InlineData("browser_eval", false)]
    public void Even_failed_or_partially_executed_UI_actions_leave_a_pending_postcondition(
        string action, bool delivered)
    {
        var checkpoint = new CodingAgentCheckpoint();
        CodingBrowserPostconditionPolicy.Apply(checkpoint, action, delivered);
        Assert.True(checkpoint.BrowserPostconditionPending);
        Assert.False(CodingBrowserPostconditionPolicy.MayReportDone(checkpoint));

        CodingBrowserPostconditionPolicy.Apply(checkpoint, "browser_dom", true);
        CodingBrowserPostconditionPolicy.Apply(checkpoint, "browser_screenshot", true);
        CodingBrowserPostconditionPolicy.Apply(checkpoint, "browser_assert", false);
        Assert.True(checkpoint.BrowserPostconditionPending);

        // A trivial assertion can be true even if the click did nothing.
        CodingBrowserPostconditionPolicy.Apply(checkpoint, "browser_assert", true,
            "exists", null);
        Assert.False(CodingBrowserPostconditionPolicy.MayReportDone(checkpoint));

        CodingBrowserPostconditionPolicy.Apply(checkpoint, "browser_assert", true,
            "text_equals", "Accepted");
        Assert.True(CodingBrowserPostconditionPolicy.MayReportDone(checkpoint));

        CodingBrowserPostconditionPolicy.Apply(checkpoint, "browser_type", true);
        Assert.False(CodingBrowserPostconditionPolicy.MayReportDone(checkpoint));
    }

    [Fact]
    public void Uncertain_browser_dispatch_is_recovered_with_pending_postcondition()
    {
        var checkpoint = new CodingAgentCheckpoint
        {
            SessionId = "browser-unknown",
            StepsTaken = 1,
            VerifiedByCommand = true,
            VerifiedWorkspaceFingerprint = new string('a', 64)
        };
        CodingAgentDispatchJournal.Begin(checkpoint, 2, "browser_click");
        string message = CodingAgentDispatchJournal.ReconcileInterruptedAction(checkpoint);
        Assert.Contains("INTERRUPTED ACTION RECOVERY", message);
        Assert.True(checkpoint.BrowserPostconditionPending);
        Assert.False(checkpoint.VerifiedByCommand);
        Assert.Null(checkpoint.VerifiedWorkspaceFingerprint);
        Assert.False(CodingBrowserPostconditionPolicy.MayReportDone(checkpoint));
    }

    [Fact]
    public void Nonbrowser_actions_never_fabricate_UI_observations()
    {
        var checkpoint = new CodingAgentCheckpoint();
        CodingBrowserPostconditionPolicy.Apply(checkpoint, "run_command", true);
        Assert.True(CodingBrowserPostconditionPolicy.MayReportDone(checkpoint));
    }

    [Theory]
    [InlineData("exists", "body", false)]
    [InlineData("visible", "button", false)]
    [InlineData("absent", null, false)]
    [InlineData("hidden", null, false)]
    [InlineData("text_equals", null, false)]
    [InlineData("text_equals", "", false)]
    [InlineData("value_equals", " ", false)]
    [InlineData("text_equals", "ACCEPTED-123", true)]
    [InlineData("value_equals", "saved", true)]
    public void Weak_or_empty_DOM_checks_cannot_certify_a_mutating_browser_action(
        string target, string? expected, bool accepted)
    {
        Assert.Equal(accepted,
            CodingBrowserPostconditionPolicy.IsStrongPostcondition(target, expected));
        var checkpoint = new CodingAgentCheckpoint();
        CodingBrowserPostconditionPolicy.Apply(checkpoint, "browser_click", true);
        CodingBrowserPostconditionPolicy.Apply(
            checkpoint, "browser_assert", true, target, expected);
        Assert.Equal(accepted,
            CodingBrowserPostconditionPolicy.MayReportDone(checkpoint));
    }
}
