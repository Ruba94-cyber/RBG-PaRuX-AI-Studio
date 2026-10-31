using System;
using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseGuiRunTraceTests
{
    [Fact]
    public void Verified_control_handoff_is_ready_for_independent_verification_not_a_goal_pass()
    {
        var trace = new ComputerUseGuiRunTrace();
        trace.RecordActionAttempt();
        trace.RecordActionDelivered();
        trace.RecordActionAttempt();
        trace.RecordAssertion(true);
        trace.RecordActionDelivered();
        trace.RecordFinalReadback(true);

        ComputerUseGuiRunReceipt receipt = trace.Build(
            guiTestRequired: true, notStarted: false, goalReported: true,
            cancelled: false, guiProofSatisfied: true, finalReadbackVerified: true,
            reportedUiAssertions: 1, stepsTaken: 4);

        Assert.Equal("ready_for_independent_verifier", receipt.Outcome);
        Assert.Equal("handoff_only", receipt.ReasonCode);
        Assert.True(receipt.ReadyForIndependentVerifier);
        Assert.Equal(2, receipt.ActionsAttempted);
        Assert.Equal(2, receipt.ActionsDelivered);
        Assert.Equal(1, receipt.UiAssertionsPassed);
        Assert.Equal(1, receipt.FinalReadbackChecks);
        Assert.Equal(1, receipt.FinalDistinctControlsVerified);
        Assert.Equal(1, receipt.FinalDistinctWindowsVerified);
        Assert.Equal(0, receipt.FinalReadbackFailures);
        Assert.Equal("executor-reported-not-independently-attested", receipt.Assurance);
    }

    [Theory]
    [InlineData(true, true, false, 1, "final_readback_unverified")]
    [InlineData(true, false, true, 1, "stale_assertion")]
    [InlineData(true, true, true, 0, "assertion_count_conflict")]
    public void Stale_missing_and_inconsistent_evidence_cannot_reach_verifier(
        bool lastReadback, bool proofSatisfied, bool finalReadback,
        int reportedAssertions, string expectedReason)
    {
        var trace = new ComputerUseGuiRunTrace();
        trace.RecordAssertion(true);
        trace.RecordFinalReadback(lastReadback);
        var receipt = trace.Build(true, false, true, false, proofSatisfied,
            finalReadback, reportedAssertions, 3);
        Assert.False(receipt.ReadyForIndependentVerifier);
        Assert.Equal("unverified", receipt.Outcome);
        Assert.Equal(expectedReason, receipt.ReasonCode);
    }

    [Fact]
    public void Failed_final_readback_then_new_assertion_and_pass_is_bounded_recovery()
    {
        var trace = new ComputerUseGuiRunTrace();
        trace.RecordAssertion(true);
        trace.RecordFinalReadback(false);
        trace.RecordAssertion(true);
        trace.RecordFinalReadback(true);
        var receipt = trace.Build(true, false, true, false, true, true, 2, 12);
        Assert.True(receipt.ReadyForIndependentVerifier);
        Assert.Equal(1, receipt.FinalReadbackFailures);
        Assert.Equal(2, receipt.FinalReadbackChecks);
        Assert.Equal(2, receipt.UiAssertionsPassed);
    }

    [Fact]
    public void Missing_final_readback_never_counts_as_gui_test_completion()
    {
        var trace = new ComputerUseGuiRunTrace();
        trace.RecordAssertion(true);
        var receipt = trace.Build(true, false, true, false, true, true, 1, 3);
        Assert.False(receipt.ReadyForIndependentVerifier);
        Assert.Equal("final_readback_unverified", receipt.ReasonCode);
    }

    [Fact]
    public void Failed_assertion_and_denied_action_are_not_counted_as_delivered()
    {
        var trace = new ComputerUseGuiRunTrace();
        trace.RecordActionAttempt();
        trace.RecordAssertion(false);
        var receipt = trace.Build(true, false, false, false, false, false, 0, 2);
        Assert.Equal("incomplete", receipt.Outcome);
        Assert.Equal(1, receipt.ActionsAttempted);
        Assert.Equal(0, receipt.ActionsDelivered);
        Assert.Equal(1, receipt.UiAssertionsFailed);
    }

    [Fact]
    public void Cancellation_and_no_model_completion_cannot_look_like_pass()
    {
        var trace = new ComputerUseGuiRunTrace();
        var cancelled = trace.Build(false, false, true, true, false, false, 0, 1);
        Assert.Equal("cancelled", cancelled.Outcome);
        Assert.False(cancelled.ReadyForIndependentVerifier);

        var incomplete = trace.Build(false, false, false, false, false, false, 0, 15);
        Assert.Equal("incomplete", incomplete.Outcome);
        Assert.False(incomplete.ReadyForIndependentVerifier);

        var ordinary = trace.Build(false, false, true, false, false, false, 0, 1);
        Assert.True(ordinary.ReadyForIndependentVerifier);
        Assert.False(ordinary.GuiTestRequired);
    }

    [Fact]
    public void Structured_receipt_contains_no_user_values_paths_titles_or_selectors()
    {
        var trace = new ComputerUseGuiRunTrace();
        trace.RecordActionAttempt();
        trace.RecordAssertion(true);
        trace.RecordFinalReadback(true);
        var receipt = trace.Build(true, false, true, false, true, true, 1, 4);
        string json = JsonSerializer.Serialize(receipt);
        Assert.DoesNotContain("selector", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expected", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("processId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("windowHandle", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExecutablePath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Summary", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Events_and_step_counts_are_bounded()
    {
        var trace = new ComputerUseGuiRunTrace();
        for (int i = 0; i < 150; i++)
        {
            trace.RecordActionAttempt();
            trace.RecordActionDelivered();
            trace.RecordAssertion(true);
            trace.RecordFinalReadback(true);
        }
        var receipt = trace.Build(true, false, true, false, true, true, 100, 200);
        Assert.Equal(100, receipt.ActionsAttempted);
        Assert.Equal(100, receipt.ActionsDelivered);
        Assert.Equal(100, receipt.UiAssertionsPassed);
        Assert.Equal(15, receipt.StepsTaken);
    }
    [Fact]
    public void Fresh_multi_control_count_is_redacted_and_cleared_after_readback_failure()
    {
        var trace = new ComputerUseGuiRunTrace();
        trace.RecordActionAttempt();
        trace.RecordAssertion(true);
        trace.RecordActionAttempt();
        trace.RecordAssertion(true);
        trace.RecordFinalReadback(true, 2);
        var receipt = trace.Build(true, false, true, false, true, true, 2, 3);
        Assert.True(receipt.ReadyForIndependentVerifier);
        Assert.Equal(2, receipt.FinalDistinctControlsVerified);

        trace.RecordFinalReadback(false, 2);
        var stale = trace.Build(true, false, true, false, true, true, 2, 3);
        Assert.Equal(0, stale.FinalDistinctControlsVerified);
        Assert.False(stale.ReadyForIndependentVerifier);
    }
    [Fact]
    public void Multiple_windows_are_distinct_from_multiple_controls_in_one_window()
    {
        var trace = new ComputerUseGuiRunTrace();
        trace.RecordActionAttempt();
        trace.RecordAssertion(true);
        trace.RecordActionAttempt();
        trace.RecordAssertion(true);
        trace.RecordFinalReadback(true, distinctControlsVerified: 2, distinctWindowsVerified: 2);
        var result = trace.Build(true, false, true, false, true, true, 2, 3);
        Assert.True(result.ReadyForIndependentVerifier);
        Assert.Equal(2, result.FinalDistinctControlsVerified);
        Assert.Equal(2, result.FinalDistinctWindowsVerified);
        trace.RecordFinalReadback(false);
        var invalid = trace.Build(true, false, true, false, true, true, 2, 3);
        Assert.Equal(0, invalid.FinalDistinctWindowsVerified);
        Assert.False(invalid.ReadyForIndependentVerifier);
    }


}
