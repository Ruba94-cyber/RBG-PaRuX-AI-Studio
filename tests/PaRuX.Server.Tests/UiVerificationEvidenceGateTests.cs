using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class UiVerificationEvidenceGateTests
{
    private const string VisualOnly =
        """{"response":"I can see an open desktop app.","foregroundWindowStable":true,"visualSnapshotId":"0123456789abcdef0123456789abcdef","uiaStatus":"winapp_not_configured","uiTreeMatchedCapture":false,"evidenceMode":"screen-plus-bounded-predecessors"}""";

    private const string WithUia =
        """{"response":"A button is visible in the target app.","foregroundWindowStable":true,"visualSnapshotId":"0123456789abcdef0123456789abcdef","uiaStatus":"observed","uiTreeMatchedCapture":true,"evidenceMode":"screen-plus-read-only-uia-plus-bounded-predecessors"}""";

    [Theory]
    [InlineData(VisualOnly)]
    [InlineData(WithUia)]
    public void Valid_persisted_desktop_observations_reach_independent_review(string json)
    {
        Assert.True(UiVerificationEvidenceGate.Evaluate("ui-verification", json).Allowed);
    }

    [Theory]
    [InlineData("""{"response":"test","foregroundWindowStable":false,"visualSnapshotId":"0123456789abcdef0123456789abcdef","uiaStatus":"observed","uiTreeMatchedCapture":true,"evidenceMode":"screen-plus-read-only-uia-plus-bounded-predecessors"}""")]
    [InlineData("""{"response":"test","foregroundWindowStable":true,"visualSnapshotId":null,"uiaStatus":"observed","uiTreeMatchedCapture":true,"evidenceMode":"screen-plus-read-only-uia-plus-bounded-predecessors"}""")]
    [InlineData("""{"response":"test","foregroundWindowStable":true,"visualSnapshotId":"0123456789abcdef0123456789abcdef","uiaStatus":"observed","uiTreeMatchedCapture":false,"evidenceMode":"screen-plus-read-only-uia-plus-bounded-predecessors"}""")]
    [InlineData("""{"response":"test","foregroundWindowStable":true,"visualSnapshotId":"0123456789abcdef0123456789abcdef","uiaStatus":"winapp_not_configured","uiTreeMatchedCapture":true,"evidenceMode":"screen-plus-read-only-uia-plus-bounded-predecessors"}""")]
    [InlineData("""{"response":"","foregroundWindowStable":true,"visualSnapshotId":"0123456789abcdef0123456789abcdef","uiaStatus":"observed","uiTreeMatchedCapture":true,"evidenceMode":"screen-plus-read-only-uia-plus-bounded-predecessors"}""")]
    [InlineData("""{"response":"test","foregroundWindowStable":true,"visualSnapshotId":"0123456789abcdef0123456789abcdef","uiaStatus":"observed","uiTreeMatchedCapture":"true","evidenceMode":"screen-plus-read-only-uia-plus-bounded-predecessors"}""")]
    [InlineData("""{"response":"test","foregroundWindowStable":true,"visualSnapshotId":"0123456789abcdef0123456789abcdef","uiaStatus":"observed","uiTreeMatchedCapture":true,"evidenceMode":"screen-plus-bounded-predecessors"}""")]
    [InlineData("{}")]
    [InlineData("not json")]
    public void Inconsistent_or_missing_UI_evidence_is_not_a_model_PASS(string json)
    {
        UiVerificationEvidenceGate.Decision decision =
            UiVerificationEvidenceGate.Evaluate("ui-verification", json);
        Assert.False(decision.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Persisted_evidence_must_match_task_scope_and_have_real_png_digest()
    {
        const string id = "0123456789abcdef0123456789abcdef";
        var snapshot = new VisualSnapshotDto
        {
            Id = id, Scope = "task", ScopeId = "task-17",
            Source = "ui-verification", MediaType = "image/png",
            ByteLength = 1024, Sha256 = new string('a', 64)
        };
        Assert.True(UiVerificationEvidenceGate.ValidatePersistedSnapshot(
            "task-17", id, snapshot).Allowed);

        snapshot.ScopeId = "another-task";
        Assert.False(UiVerificationEvidenceGate.ValidatePersistedSnapshot(
            "task-17", id, snapshot).Allowed);

        snapshot.ScopeId = "task-17";
        snapshot.Source = "user";
        Assert.False(UiVerificationEvidenceGate.ValidatePersistedSnapshot(
            "task-17", id, snapshot).Allowed);

        snapshot.Source = "ui-verification";
        snapshot.Sha256 = new string('X', 64);
        Assert.False(UiVerificationEvidenceGate.ValidatePersistedSnapshot(
            "task-17", id, snapshot).Allowed);
        Assert.False(UiVerificationEvidenceGate.ValidatePersistedSnapshot(
            "task-17", id, null).Allowed);
    }

    [Fact]
    public void Ordinary_coding_and_research_tasks_are_not_subject_to_screen_capture_gate()
    {
        Assert.True(UiVerificationEvidenceGate.Evaluate("coding", null).Allowed);
        Assert.True(UiVerificationEvidenceGate.Evaluate("research", "not JSON").Allowed);
    }
}
