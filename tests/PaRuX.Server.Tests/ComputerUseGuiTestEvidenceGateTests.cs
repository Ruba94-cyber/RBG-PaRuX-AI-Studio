using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseGuiTestEvidenceGateTests
{
    [Theory]
    [InlineData("Testa questa applicazione e verifica la sua interfaccia")]
    [InlineData("Test the GUI in a disposable Windows app")]
    [InlineData("Verify the application UI opens and the text is correct")]
    [InlineData("Collauda la finestra di questa app")]
    [InlineData("Testing an app form before release")]
    public void Explicit_application_gui_test_requires_fresh_assertion(string goal)
    {
        var gate = new ComputerUseGuiTestEvidenceGate(goal);
        Assert.True(gate.RequiresAssertion);
        Assert.False(gate.CanReportDone);
    }

    [Theory]
    [InlineData("Apri Notepad e scrivi buongiorno")]
    [InlineData("Verifica il risultato della ricerca in Internet")]
    [InlineData("Create an Excel workbook with charts")]
    [InlineData("Explain how an app works")]
    [InlineData(null)]
    public void Ordinary_computer_use_keeps_existing_goal_verification_path(string? goal)
    {
        var gate = new ComputerUseGuiTestEvidenceGate(goal);
        Assert.False(gate.RequiresAssertion);
        Assert.True(gate.CanReportDone);
    }

    [Fact]
    public void A_verified_read_only_assertion_allows_a_gui_test_without_mutating_the_app()
    {
        var gate = new ComputerUseGuiTestEvidenceGate("Verify application UI");
        gate.RecordVerifiedAssertion();
        Assert.True(gate.CanReportDone);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("double_click")]
    [InlineData("right_click")]
    [InlineData("type")]
    [InlineData("key")]
    [InlineData("scroll")]
    [InlineData("ui_invoke")]
    [InlineData("ui_set_value")]
    [InlineData("focus_window")]
    [InlineData("open_app")]
    public void Any_later_effect_or_target_switch_invalidates_previous_ui_proof(string action)
    {
        var gate = new ComputerUseGuiTestEvidenceGate("Test the app interface");
        gate.RecordVerifiedAssertion();
        Assert.True(gate.CanReportDone);
        gate.RecordDeliveredAction(action);
        Assert.False(gate.CanReportDone);
        gate.RecordVerifiedAssertion();
        Assert.True(gate.CanReportDone);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("wait")]
    [InlineData("ui_wait_for")]
    [InlineData("ui_assert")]
    public void Read_only_actions_do_not_invalidate_a_confirmed_assertion(string action)
    {
        var gate = new ComputerUseGuiTestEvidenceGate("Test app UI");
        gate.RecordVerifiedAssertion();
        gate.RecordDeliveredAction(action);
        Assert.True(gate.CanReportDone);
    }

    [Fact]
    public void A_test_that_cannot_access_ui_automation_must_not_claim_success()
    {
        var gate = new ComputerUseGuiTestEvidenceGate("Test application GUI");
        gate.RecordDeliveredAction("click");
        gate.RecordDeliveredAction("ui_wait_for");
        Assert.False(gate.CanReportDone);
        Assert.Contains("ui_assert", ComputerUseGuiTestEvidenceGate.MissingProofReason);
    }

    [Fact]
    public void Ordinary_tasks_are_not_retroactively_blocked_by_the_gui_test_rule()
    {
        var gate = new ComputerUseGuiTestEvidenceGate("Open Notepad");
        gate.RecordDeliveredAction("ui_invoke");
        Assert.True(gate.CanReportDone);
    }

    [Fact]
    public void Ambiguous_or_excessive_goal_text_does_not_grant_test_success()
    {
        Assert.False(ComputerUseGuiTestEvidenceGate.IsGuiTestGoal(new string('x', 16385) + " Test app UI"));
        Assert.False(ComputerUseGuiTestEvidenceGate.IsGuiTestGoal("contest the application developer"));
    }
}
