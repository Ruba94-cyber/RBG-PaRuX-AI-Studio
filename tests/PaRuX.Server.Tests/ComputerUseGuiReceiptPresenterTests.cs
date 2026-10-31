using System;
using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseGuiReceiptPresenterTests
{
    private static string Receipt(
        string outcome = "ready_for_independent_verifier",
        bool ready = true, bool guiTest = true)
    {
        return JsonSerializer.Serialize(new
        {
            response = "synthetic SECRET text / private/path",
            guiRunReceipt = new
            {
                schemaVersion = 1,
                outcome,
                assurance = "executor-reported-not-independently-attested",
                guiTestRequired = guiTest,
                finalControlReadbackVerified = guiTest,
                actionsAttempted = 5,
                actionsDelivered = 3,
                uiAssertionsPassed = 2,
                uiAssertionsFailed = 1,
                finalReadbackChecks = 2,
                finalReadbackFailures = 1,
                readyForIndependentVerifier = ready,
                selector = "synthetic SECRET selector"
            }
        });
    }

    [Fact]
    public void Ready_receipt_is_shown_as_handoff_not_as_pass_and_never_leaks_model_text()
    {
        string? line = ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", Receipt());
        Assert.NotNull(line);
        Assert.Contains("independent goal verification required", line);
        Assert.Contains("UIA 2 passed, 1 failed", line);
        Assert.Contains("delivered actions 3/5", line);
        Assert.DoesNotContain("SECRET", line, StringComparison.Ordinal);
        Assert.DoesNotContain("selector", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_authoritative_completed_verifier_status_can_be_shown_as_a_reported_pass()
    {
        string? pending = ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", Receipt(), "verifying", null);
        Assert.NotNull(pending);
        Assert.Contains("independent goal verification required", pending);

        string? passed = ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", Receipt(), "completed", "pass");
        Assert.NotNull(passed);
        Assert.Contains("goal verifier reports PASS", passed);

        string? failed = ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", Receipt(), "failed", "fail");
        Assert.NotNull(failed);
        Assert.Contains("goal verification did not pass", failed);

        string? unverified = ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", Receipt(), "completed", "unverified");
        Assert.NotNull(unverified);
        Assert.DoesNotContain("reports PASS", unverified);
    }

    [Theory]
    [InlineData("unverified", "GUI result unverified")]
    [InlineData("incomplete", "GUI task incomplete")]
    [InlineData("cancelled", "GUI task cancelled")]
    [InlineData("not_started", "GUI task not started")]
    public void Honest_nonpassing_outcomes_have_distinct_readable_labels(
        string status, string expected)
    {
        string? line = ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", Receipt(status, false));
        Assert.NotNull(line);
        Assert.Contains(expected, line);
        Assert.DoesNotContain("independent goal verification required", line);
    }

    [Fact]
    public void Ordinary_computer_use_is_not_misrepresented_as_a_gui_test()
    {
        string? line = ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", Receipt(guiTest: false));
        Assert.NotNull(line);
        Assert.Contains("Computer Use reported completion", line);
        Assert.DoesNotContain("GUI evidence captured", line);
    }

    [Theory]
    [InlineData("coding")]
    [InlineData("planner")]
    [InlineData("browser")]
    [InlineData("")]
    public void Other_worker_roles_do_not_read_computer_use_receipts(string role)
    {
        Assert.Null(ComputerUseGuiReceiptPresenter.BuildLine(role, Receipt()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"guiRunReceipt":{"outcome":"ready_for_independent_verifier"}}""")]
    public void Missing_or_malformed_receipts_are_ignored(string input)
    {
        Assert.Null(ComputerUseGuiReceiptPresenter.BuildLine("computer-use", input));
    }

    [Fact]
    public void Contradictory_ready_flag_is_not_displayed()
    {
        Assert.Null(ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", Receipt(ready: false)));
        Assert.Null(ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", Receipt("unverified", ready: true)));
    }

    [Fact]
    public void Untrusted_long_strings_and_forged_versions_are_refused()
    {
        string original = Receipt();
        Assert.Null(ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", original.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")));
        Assert.Null(ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", original.Replace("\"actionsDelivered\":3", "\"actionsDelivered\":999")));
        Assert.Null(ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", new string('x', 32769)));
    }

    [Fact]
    public void A_gui_ready_claim_without_fresh_final_control_readback_is_hidden()
    {
        string original = Receipt();
        Assert.Null(ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", original.Replace(
                "\"finalControlReadbackVerified\":true",
                "\"finalControlReadbackVerified\":false")));
    }

    [Fact]
    public void Unrealistic_action_or_check_counts_are_not_rendered()
    {
        string original = Receipt();
        Assert.Null(ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", original.Replace("\"actionsDelivered\":3", "\"actionsDelivered\":6")));
        Assert.Null(ComputerUseGuiReceiptPresenter.BuildLine(
            "computer-use", original.Replace("\"finalReadbackFailures\":1", "\"finalReadbackFailures\":3")));
    }
}
