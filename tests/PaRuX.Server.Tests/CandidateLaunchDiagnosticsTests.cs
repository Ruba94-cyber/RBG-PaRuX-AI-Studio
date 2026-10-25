using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateLaunchDiagnosticsTests
{
    [Fact]
    public void Descriptor_refusal_is_surfaced_for_a_resident_model_with_bounded_reason_only()
    {
        Assert.Null(RuntimeManagerService.LaunchDescriptorIssue(true, null));
        Assert.Null(RuntimeManagerService.LaunchDescriptorIssue(false, "invalid_environment_value"));
        Assert.Null(RuntimeManagerService.LaunchDescriptorIssue(true, ""));

        RuntimeDiagnosticIssue issue = RuntimeManagerService.LaunchDescriptorIssue(true, "invalid_environment_value")!;
        Assert.Equal("info", issue.Severity);
        Assert.Contains("invalid environment value", issue.Message);
        Assert.Contains("no exact launch descriptor", issue.Message);
    }

    [Theory]
    [InlineData("licence_evidence_missing")]
    [InlineData("licence_evidence_changed")]
    [InlineData("evidence_environment_unavailable")]
    [InlineData("evaluation_launch_profile_not_actual")]
    [InlineData("quality_performance_launch_mismatch")]
    [InlineData("launch_model_mismatch")]
    [InlineData("launch_configuration_mismatch")]
    [InlineData("backend_mismatch")]
    [InlineData("hardware_changed")]
    [InlineData("runtime_changed")]
    [InlineData("runtime_dependencies_changed")]
    public void Every_launch_scope_and_licence_refusal_has_a_plain_sentence_with_a_next_step(string code)
    {
        string text = CandidateReasonText.Explain(code);
        Assert.DoesNotContain("_", text);
        Assert.EndsWith(".", text);
        Assert.NotEqual(code.Replace('_', ' '), text);
        Assert.True(text.Contains("again", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Load the", StringComparison.Ordinal)
            || text.Contains("Download", StringComparison.Ordinal), text);
    }

    [Fact]
    public void Launch_scope_refusal_codes_are_all_explained()
    {
        var reasons = new List<string>();
        CandidateProofEnvironment? none = null;
        reasons.Add(CandidateLaunchScope.Check(none, none, null, null, null).Reason);
        Assert.All(reasons, reason => Assert.NotEqual(reason.Replace('_', ' '), CandidateReasonText.Explain(reason)));
    }

    [Theory]
    [InlineData("active_model_proof_stale", "force the reference model")]
    [InlineData("incumbent_launch_configuration_mismatch", "scoped")]
    [InlineData("request_parity_execution_profile_differs", "reset this model's execution profile")]
    public void Reason_codes_explain_cause_and_fix_in_plain_language(string code, string expected)
    {
        string text = CandidateReasonText.Explain(code);
        Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("_", text);
    }
}
