using System.Text.Json;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingResultVerifierGateTests
{
    [Theory]
    [InlineData("planner")]
    [InlineData("research")]
    [InlineData("ui-verification")]
    public void Noncoding_roles_use_their_existing_independent_verification_contract(string role)
    {
        Assert.True(CodingResultVerifierGate.Evaluate(role, "not even JSON").Allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("{\"verificationPending\":false,\"goalReported\":true}")]
    [InlineData("{\"verificationPending\":\"false\",\"verifiedByCommand\":false,\"goalReported\":true}")]
    [InlineData("{\"verificationPending\":true,\"verifiedByCommand\":true,\"goalReported\":true,\"checkpointSessionId\":\"task\"}")]
    [InlineData("{\"verificationPending\":false,\"verifiedByCommand\":true,\"goalReported\":true}")]
    [InlineData("{\"verificationPending\":false,\"verifiedByCommand\":true,\"goalReported\":true,\"checkpointSessionId\":\" \"}")]
    [InlineData("{\"verificationPending\":false,\"verifiedByCommand\":false}")]
    public void Coding_task_cannot_be_verified_from_missing_or_untrusted_execution_state(string? result)
    {
        CodingResultVerifierGate.Decision decision = CodingResultVerifierGate.Evaluate("coding", result);
        Assert.False(decision.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Theory]
    [InlineData("{\"verificationPending\":false,\"browserPostconditionPending\":false,\"verifiedByCommand\":false,\"goalReported\":true}")]
    [InlineData("{\"verificationPending\":false,\"browserPostconditionPending\":false,\"verifiedByCommand\":true,\"goalReported\":true,\"checkpointSessionId\":\"task-123\",\"verifiedWorkspaceFingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    public void Completed_without_pending_tests_may_reach_model_based_review(string result)
    {
        CodingResultVerifierGate.Decision decision = CodingResultVerifierGate.Evaluate("coding", result, "task-123");
        Assert.True(decision.Allowed, decision.Reason);
    }

    [Theory]
    [InlineData("{\"verificationPending\":false,\"browserPostconditionPending\":false,\"verifiedByCommand\":false,\"goalReported\":false}")]
    [InlineData("{\"verificationPending\":false,\"browserPostconditionPending\":false,\"verifiedByCommand\":true,\"goalReported\":false,\"checkpointSessionId\":\"task-a\"}")]
    public void Coding_attempt_without_explicit_done_can_never_reach_model_based_pass(string json)
    {
        CodingResultVerifierGate.Decision decision =
            CodingResultVerifierGate.Evaluate("coding", json, "task-a");
        Assert.False(decision.Allowed);
        Assert.Contains("did not explicitly complete", decision.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Browser_postcondition_must_be_present_and_false_before_independent_review(bool pending)
    {
        string json = JsonSerializer.Serialize(new
        {
            verificationPending = false,
            browserPostconditionPending = pending,
            verifiedByCommand = false,
            goalReported = true
        });
        Assert.Equal(!pending,
            CodingResultVerifierGate.Evaluate("coding", json, "task-a").Allowed);
    }

    [Theory]
    [InlineData("task-a", "task-a", true)]
    [InlineData("task-a", "task-b", false)]
    public void Successful_coding_proof_must_reference_this_orchestrator_task(
        string expectedTaskId, string claimedCheckpointId, bool allowed)
    {
        string json = JsonSerializer.Serialize(new
        {
            verificationPending = false,
            verifiedByCommand = true, browserPostconditionPending = false,
            goalReported = true,
            checkpointSessionId = claimedCheckpointId,
            verifiedWorkspaceFingerprint = new string('a', 64)
        });
        Assert.Equal(allowed,
            CodingResultVerifierGate.Evaluate("coding", json, expectedTaskId).Allowed);
    }

    [Fact]
    public void Accepted_command_claim_without_core_task_binding_is_rejected()
    {
        string json = JsonSerializer.Serialize(new
        {
            verificationPending = false,
            verifiedByCommand = true,
            browserPostconditionPending = false,
            goalReported = true,
            checkpointSessionId = "task-a",
            verifiedWorkspaceFingerprint = new string('a', 64)
        });

        CodingResultVerifierGate.Decision decision =
            CodingResultVerifierGate.Evaluate("coding", json);

        Assert.False(decision.Allowed);
        Assert.Contains("not bound to a Core task", decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-digest")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    public void Claimed_accepted_command_must_have_canonical_workspace_digest(string? digest)
    {
        string result = JsonSerializer.Serialize(new
        {
            verificationPending = false,
            verifiedByCommand = true, browserPostconditionPending = false,
            goalReported = true,
            checkpointSessionId = "task-a",
            verifiedWorkspaceFingerprint = digest
        });
        Assert.False(CodingResultVerifierGate.Evaluate("coding", result, "task-a").Allowed);
    }

    [Fact]
    public void Reject_oversized_result_before_parsing_or_model_inspection()
    {
        string longResult = new string('x', 1_000_001);
        Assert.False(CodingResultVerifierGate.Evaluate("coding", longResult).Allowed);
    }
}
