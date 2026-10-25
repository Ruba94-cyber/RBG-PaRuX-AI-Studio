using System;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingTaskApplyAdmissionPolicyTests
{
    private static AgentTaskDto Task(
        string id = "task-1",
        string role = "coding",
        string? status = null,
        string verificationStatus = "pass",
        bool verified = true,
        bool pending = false,
        string checkpointId = "task-1") => new()
    {
        Id = id,
        Goal = "Review and apply coding change",
        Role = role,
        Status = status ?? AgentTaskStatus.Completed,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
        VerificationStatus = verificationStatus,
        ResultJson = JsonSerializer.Serialize(new
        {
            goalReported = true,
            verifiedByCommand = verified,
            verificationPending = pending,
            browserPostconditionPending = false,
            checkpointSessionId = checkpointId,
            verifiedWorkspaceFingerprint = new string('a', 64)
        })
    };

    [Fact]
    public void Only_latest_accepted_command_and_independent_Core_pass_enable_adoption()
    {
        Assert.True(CodingTaskApplyAdmissionPolicy.CanApply(Task(), "task-1"));
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(Task(), "task-other"));
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(Task(role:"research"), "task-1"));
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(Task(status:AgentTaskStatus.Running), "task-1"));
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(Task(status:AgentTaskStatus.Verifying), "task-1"));
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(Task(verificationStatus:"fail"), "task-1"));
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(Task(verificationStatus:"inconclusive"), "task-1"));
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(Task(verified:false), "task-1"));
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(Task(pending:true), "task-1"));
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(Task(checkpointId:"task-other"), "task-1"));
        var goalNotReported = Task();
        goalNotReported.ResultJson = JsonSerializer.Serialize(new
        {
            goalReported = false,
            verifiedByCommand = true, verificationPending = false,
            browserPostconditionPending = false,
            checkpointSessionId = "task-1",
            verifiedWorkspaceFingerprint = new string('a', 64)
        });
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(goalNotReported, "task-1"));
        var browserPending = Task();
        browserPending.ResultJson = JsonSerializer.Serialize(new
        {
            goalReported = true,
            verifiedByCommand = true, verificationPending = false,
            browserPostconditionPending = true,
            checkpointSessionId = "task-1",
            verifiedWorkspaceFingerprint = new string('a', 64)
        });
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(browserPending, "task-1"));
        var invalidDigest = Task();
        invalidDigest.ResultJson = JsonSerializer.Serialize(new
        {
            goalReported = true,
            verifiedByCommand = true, verificationPending = false,
            browserPostconditionPending = false,
            checkpointSessionId = "task-1",
            verifiedWorkspaceFingerprint = "not-verified"
        });
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(invalidDigest, "task-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not valid JSON")]
    [InlineData("{\"verifiedByCommand\":\"true\",\"verificationPending\":false,\"checkpointSessionId\":\"task-1\"}")]
    [InlineData("{\"verifiedByCommand\":true,\"verificationPending\":\"false\",\"checkpointSessionId\":\"task-1\"}")]
    [InlineData("{\"verifiedByCommand\":true,\"verificationPending\":false}")]
    public void Missing_or_untrusted_metadata_cannot_authorize_user_repository_changes(string json)
    {
        var task = Task();
        task.ResultJson = json;
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(task, "task-1"));
    }

    [Fact]
    public void Verified_fingerprint_is_exposed_only_for_an_admissible_task()
    {
        Assert.Equal(new string('a', 64), CodingTaskApplyAdmissionPolicy.TryGetVerifiedWorkspaceFingerprint(Task(), "task-1"));
        Assert.Null(CodingTaskApplyAdmissionPolicy.TryGetVerifiedWorkspaceFingerprint(Task(), "task-other"));
        Assert.Null(CodingTaskApplyAdmissionPolicy.TryGetVerifiedWorkspaceFingerprint(Task(verified: false), "task-1"));
        Assert.Null(CodingTaskApplyAdmissionPolicy.TryGetVerifiedWorkspaceFingerprint(null, "task-1"));
    }

    [Fact]
    public void An_unbounded_result_cannot_authorize_apply()
    {
        var task = Task();
        task.ResultJson = new string('x', 1_000_001);
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(task, "task-1"));
        Assert.False(CodingTaskApplyAdmissionPolicy.CanApply(null, "task-1"));
    }
}
