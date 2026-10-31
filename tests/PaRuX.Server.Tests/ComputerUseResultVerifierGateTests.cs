using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseResultVerifierGateTests
{
    private const string GuiGoal = "Testa questa applicazione e verifica la sua interfaccia";

    private static string Fixture(bool gui = true)
    {
        int uiChecks = gui ? 1 : 0;
        int finalChecks = gui ? 1 : 0;
        return JsonSerializer.Serialize(new
        {
            response = "the model says PASS",
            goalReported = true,
            guiTestProofRequired = gui,
            guiTestProofSatisfied = gui,
            guiFinalReadbackVerified = gui,
            uiAssertionsPassed = uiChecks,
            stepsTaken = 4,
            guiRunReceipt = new
            {
                schemaVersion = 1,
                outcome = "ready_for_independent_verifier",
                reasonCode = "handoff_only",
                assurance = "executor-reported-not-independently-attested",
                guiTestRequired = gui,
                goalReported = true,
                readyForIndependentVerifier = true,
                finalControlReadbackVerified = gui,
                actionsAttempted = 4,
                actionsDelivered = 3,
                uiAssertionsPassed = uiChecks,
                uiAssertionsFailed = 0,
                finalReadbackChecks = finalChecks,
                finalReadbackFailures = 0,
                finalDistinctControlsVerified = gui ? 1 : 0,
                stepsTaken = 4
            }
        });
    }

    private static string Alter(string baseJson, string member, JsonNode? value, bool nested = true)
    {
        var json = JsonNode.Parse(baseJson)!.AsObject();
        JsonObject target = nested ? json["guiRunReceipt"]!.AsObject() : json;
        target[member] = value;
        return json.ToJsonString();
    }

    [Fact]
    public void Real_desktop_gui_test_receipt_is_only_admitted_for_independent_review()
    {
        var verdict = ComputerUseResultVerifierGate.Evaluate("computer-use", GuiGoal, Fixture());
        Assert.True(verdict.Allowed, verdict.Reason);
        Assert.Contains("independent goal verification", verdict.Reason);
    }

    [Fact]
    public void Ordinary_task_without_gui_assertions_is_unaffected()
    {
        var verdict = ComputerUseResultVerifierGate.Evaluate(
            "computer-use", "Open Notepad", Fixture(gui: false));
        Assert.True(verdict.Allowed, verdict.Reason);
        Assert.False(ComputerUseGuiIntentPolicy.RequiresAssertion("Open Notepad"));
        Assert.True(ComputerUseGuiIntentPolicy.RequiresAssertion(GuiGoal));
    }

    [Fact]
    public void Model_text_cannot_forge_missing_structured_receipt()
    {
        var verdict = ComputerUseResultVerifierGate.Evaluate("computer-use", GuiGoal,
            """{"response":"PASS, tested UIA, everything works!"}""");
        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void A_gui_task_cannot_opt_out_of_gui_proof_on_its_worker()
    {
        var verdict = ComputerUseResultVerifierGate.Evaluate(
            "computer-use", GuiGoal, Fixture(gui: false));
        Assert.False(verdict.Allowed);
        Assert.Contains("original user goal", verdict.Reason);
    }

    [Fact]
    public void A_non_gui_task_cannot_claim_gui_only_evidence()
    {
        var verdict = ComputerUseResultVerifierGate.Evaluate(
            "computer-use", "Open Notepad", Fixture(gui: true));
        Assert.False(verdict.Allowed);
    }

    [Theory]
    [InlineData("readyForIndependentVerifier", "false")]
    [InlineData("outcome", "\"passed\"")]
    [InlineData("reasonCode", "\"some-model-reason\"")]
    [InlineData("assurance", "\"signed-proof\"")]
    [InlineData("schemaVersion", "2")]
    [InlineData("uiAssertionsPassed", "0")]
    [InlineData("finalReadbackChecks", "0")]
    [InlineData("actionsDelivered", "5")]
    [InlineData("actionsAttempted", "20")]
    [InlineData("stepsTaken", "3")]
    public void Forged_inconsistent_or_too_large_receipts_are_refused(
        string field, string rawValue)
    {
        string modified = Alter(Fixture(), field, JsonNode.Parse(rawValue));
        Assert.False(ComputerUseResultVerifierGate.Evaluate("computer-use", GuiGoal, modified).Allowed);
    }

    [Theory]
    [InlineData("guiFinalReadbackVerified", "false")]
    [InlineData("guiTestProofRequired", "false")]
    [InlineData("guiTestProofSatisfied", "false")]
    [InlineData("uiAssertionsPassed", "0")]
    [InlineData("goalReported", "false")]
    public void Worker_root_metadata_must_agree_with_original_goal_and_receipt(
        string field, string rawValue)
    {
        string modified = Alter(Fixture(), field, JsonNode.Parse(rawValue), nested: false);
        Assert.False(ComputerUseResultVerifierGate.Evaluate("computer-use", GuiGoal, modified).Allowed);
    }

    [Fact]
    public void Missing_bad_or_unbounded_receipts_fail_before_model_is_invoked()
    {
        Assert.False(ComputerUseResultVerifierGate.Evaluate("computer-use", GuiGoal, null).Allowed);
        Assert.False(ComputerUseResultVerifierGate.Evaluate("computer-use", GuiGoal, "not-json").Allowed);
        Assert.False(ComputerUseResultVerifierGate.Evaluate(
            "computer-use", GuiGoal, new string('x', 128001)).Allowed);
    }

    [Fact]
    public void Other_roles_remain_governed_by_their_existing_independent_gates()
    {
        Assert.True(ComputerUseResultVerifierGate.Evaluate("coding", GuiGoal, "not json").Allowed);
        Assert.True(ComputerUseResultVerifierGate.Evaluate("ui-verification", GuiGoal, null).Allowed);
    }
    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("9")]
    [InlineData("-1")]
    public void Distinct_final_control_count_must_be_bounded_and_agree_with_assertions(string raw)
    {
        string modified = Alter(Fixture(), "finalDistinctControlsVerified", JsonNode.Parse(raw));
        Assert.False(ComputerUseResultVerifierGate.Evaluate(
            "computer-use", GuiGoal, modified).Allowed);
    }

    [Fact]
    public void Legacy_v1_receipt_without_additive_control_count_is_still_parseable()
    {
        var json = JsonNode.Parse(Fixture())!.AsObject();
        json["guiRunReceipt"]!.AsObject().Remove("finalDistinctControlsVerified");
        Assert.True(ComputerUseResultVerifierGate.Evaluate(
            "computer-use", GuiGoal, json.ToJsonString()).Allowed);
    }

}
