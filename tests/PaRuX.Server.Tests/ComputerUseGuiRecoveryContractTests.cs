using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Pure, hermetic cross-policy contract test. It exercises the same decision
/// components as the Computer Use agent, but it neither starts a Windows app
/// nor calls the LLM/WinApp CLI. A PASS here is not physical agent acceptance.
/// </summary>
public sealed class ComputerUseGuiRecoveryContractTests
{
    private const int Pid = 101;
    private const long Hwnd = 202;
    private const string Selector = "status-result";
    private const string Initial = """
        {"windows":[{"hwnd":202,"elements":[{"selector":"status-result","value":"PENDING"}]}]}
        """;
    private const string Repaired = """
        {"windows":[{"hwnd":202,"elements":[{"selector":"status-result","value":"ACCEPTED"}]}]}
        """;

    [Fact]
    public void Actual_mismatch_then_delivered_repair_then_independent_readback_can_reach_verifier()
    {
        var gui = new ComputerUseGuiTestEvidenceGate("Test the application GUI");
        var budget = new ComputerUseGuiRepairBudget();
        var trace = new ComputerUseGuiRunTrace();

        trace.RecordActionAttempt();
        var mismatch = WindowsUiAssertionPolicy.AssertExact(
            Initial, Hwnd, Selector, "value", "ACCEPTED");
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.DifferentValue, mismatch);
        trace.RecordAssertion(false);
        gui.InvalidateExternalChange();
        Assert.True(budget.TryReserve(mismatch, 1, 15, gui.RequiresAssertion));
        Assert.False(gui.CanReportDone);

        trace.RecordActionAttempt();
        // Simulate only an executor-reported SUCCESSFUL delivery; no model
        // textual claim can stand in for this signal in the real worker.
        trace.RecordActionDelivered();
        gui.RecordDeliveredAction("ui_set_value");
        budget.RecordDeliveredAction("ui_set_value");

        trace.RecordActionAttempt();
        var verdict = WindowsUiAssertionPolicy.AssertExact(
            Repaired, Hwnd, Selector, "value", "ACCEPTED");
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.Verified, verdict);
        trace.RecordAssertion(true);
        trace.RecordActionDelivered();
        gui.RecordVerifiedAssertion();

        var proof = ComputerUseGuiFinalProof.FromVerifiedAssertion(
            Pid, Hwnd, Selector, "value", "ACCEPTED", verdict);
        Assert.NotNull(proof);
        Assert.True(gui.CanReportDone);
        // Before a fresh readback the worker must not allow completion.
        var premature = trace.Build(true, false, true, false,
            gui.CanReportDone, false, gui.VerifiedAssertions, 3);
        Assert.False(premature.ReadyForIndependentVerifier);

        trace.RecordActionAttempt(); // model-issued done
        var freshReadback = new WindowsUiTreeObservationService.Observation(
            "observed", Repaired, Pid, Hwnd);
        bool finalControlPass = proof!.Recheck(freshReadback);
        trace.RecordFinalReadback(finalControlPass);
        var receipt = trace.Build(true, false, true, false,
            gui.CanReportDone, finalControlPass, gui.VerifiedAssertions, 4);
        Assert.True(receipt.ReadyForIndependentVerifier);
        Assert.Equal("ready_for_independent_verifier", receipt.Outcome);
        Assert.Equal(1, receipt.UiAssertionsFailed);
        Assert.Equal(1, receipt.UiAssertionsPassed);
        Assert.Equal(1, receipt.FinalReadbackChecks);
        Assert.Equal(1, budget.RecoveriesUsed);
        Assert.Equal(4, receipt.ActionsAttempted);
        Assert.Equal(2, receipt.ActionsDelivered);
    }

    [Fact]
    public void External_reset_after_success_revokes_proof_and_requires_actual_repair()
    {
        var gui = new ComputerUseGuiTestEvidenceGate("Test the app GUI");
        var budget = new ComputerUseGuiRepairBudget();
        var trace = new ComputerUseGuiRunTrace();
        trace.RecordActionAttempt();
        var proof = ComputerUseGuiFinalProof.FromVerifiedAssertion(
            Pid, Hwnd, Selector, "value", "ACCEPTED",
            WindowsUiAssertionPolicy.Verdict.Verified);
        Assert.NotNull(proof);
        gui.RecordVerifiedAssertion();
        trace.RecordAssertion(true);

        // A change by the fixture is external: PaRuX delivered no mutation.
        bool stale = proof!.Recheck(new(
            "observed", Initial, Pid, Hwnd));
        Assert.False(stale);
        trace.RecordFinalReadback(stale);
        gui.InvalidateExternalChange();
        Assert.False(gui.CanReportDone);

        trace.RecordActionAttempt();
        var pending = WindowsUiAssertionPolicy.AssertExact(
            Initial, Hwnd, Selector, "value", "ACCEPTED");
        trace.RecordAssertion(false);
        Assert.True(budget.TryReserve(pending, 2, 15, true));
        Assert.False(budget.TryReserve(pending, 3, 15, true));
        var failed = trace.Build(true, false, true, false,
            gui.CanReportDone, false, gui.VerifiedAssertions, 3);
        Assert.False(failed.ReadyForIndependentVerifier);
        Assert.Equal("final_readback_unverified", failed.ReasonCode);
    }

    [Fact]
    public void Ambiguous_target_never_starts_repair_or_creates_successful_proof()
    {
        const string ambiguous = """
            {"windows":[{"hwnd":202,"elements":[
              {"selector":"status-result","value":"ACCEPTED"},
              {"selector":"status-result","value":"ACCEPTED"}
            ]}]}
            """;
        var budget = new ComputerUseGuiRepairBudget();
        var verdict = WindowsUiAssertionPolicy.AssertExact(
            ambiguous, Hwnd, Selector, "value", "ACCEPTED");
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.AmbiguousTarget, verdict);
        Assert.False(budget.TryReserve(verdict, 1, 15, true));
        Assert.Null(ComputerUseGuiFinalProof.FromVerifiedAssertion(
            Pid, Hwnd, Selector, "value", "ACCEPTED", verdict));
    }
}
