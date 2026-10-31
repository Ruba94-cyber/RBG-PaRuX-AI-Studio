using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseGuiRepairBudgetTests
{
    [Theory]
    [InlineData((int)WindowsUiAssertionPolicy.Verdict.DifferentValue)]
    [InlineData((int)WindowsUiAssertionPolicy.Verdict.MissingTarget)]
    [InlineData((int)WindowsUiAssertionPolicy.Verdict.MissingProperty)]
    public void Real_gui_mismatches_may_request_one_bounded_repair(
        int verdictValue)
    {
        var verdict = (WindowsUiAssertionPolicy.Verdict)verdictValue;
        var budget = new ComputerUseGuiRepairBudget();
        Assert.True(budget.TryReserve(verdict, 3, 15, true));
        Assert.Equal(1, budget.RecoveriesUsed);
        // Waiting or observing cannot unlock a second retry.
        budget.RecordDeliveredAction("wait");
        budget.RecordDeliveredAction("move");
        budget.RecordDeliveredAction("ui_assert");
        Assert.False(budget.TryReserve(verdict, 4, 15, true));
        budget.RecordDeliveredAction("ui_set_value");
        Assert.True(budget.TryReserve(verdict, 6, 15, true));
        Assert.Equal(2, budget.RecoveriesUsed);
        budget.RecordDeliveredAction("ui_invoke");
        Assert.False(budget.TryReserve(verdict, 7, 15, true));
    }

    [Theory]
    [InlineData((int)WindowsUiAssertionPolicy.Verdict.AmbiguousTarget)]
    [InlineData((int)WindowsUiAssertionPolicy.Verdict.InvalidObservation)]
    [InlineData((int)WindowsUiAssertionPolicy.Verdict.InvalidRequest)]
    [InlineData((int)WindowsUiAssertionPolicy.Verdict.Verified)]
    public void Invalid_ambiguous_or_already_verified_ui_evidence_never_gets_repair_permission(
        int verdictValue)
    {
        var verdict = (WindowsUiAssertionPolicy.Verdict)verdictValue;
        var budget = new ComputerUseGuiRepairBudget();
        Assert.False(budget.TryReserve(verdict, 1, 15, true));
    }

    [Fact]
    public void No_retries_without_gui_test_intent_or_remaining_step_budget()
    {
        var budget = new ComputerUseGuiRepairBudget();
        Assert.False(budget.TryReserve(
            WindowsUiAssertionPolicy.Verdict.DifferentValue, 1, 15, false));
        Assert.False(budget.TryReserve(
            WindowsUiAssertionPolicy.Verdict.DifferentValue, 15, 15, true));
        Assert.False(budget.TryReserve(
            WindowsUiAssertionPolicy.Verdict.DifferentValue, 0, 15, true));
        Assert.True(budget.TryReserve(
            WindowsUiAssertionPolicy.Verdict.DifferentValue, 14, 15, true));
    }

    [Fact]
    public void Another_task_has_an_independent_repair_budget()
    {
        var first = new ComputerUseGuiRepairBudget();
        var second = new ComputerUseGuiRepairBudget();
        Assert.True(first.TryReserve(
            WindowsUiAssertionPolicy.Verdict.MissingProperty, 1, 15, true));
        Assert.Equal(0, second.RecoveriesUsed);
        Assert.True(second.TryReserve(
            WindowsUiAssertionPolicy.Verdict.MissingProperty, 1, 15, true));
    }
}
