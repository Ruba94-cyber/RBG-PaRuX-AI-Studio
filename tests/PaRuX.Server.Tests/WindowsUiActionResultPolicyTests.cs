using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WindowsUiActionResultPolicyTests
{
    [Theory]
    [InlineData("""{"found":true,"waitedMs":10,"timedOut":false}""", true)]
    [InlineData("""{"found":false,"waitedMs":3000,"timedOut":true}""", false)]
    [InlineData("""{"found":true,"waitedMs":3000,"timedOut":true}""", false)]
    [InlineData("""{"found":false,"timedOut":false}""", false)]
    [InlineData("""{"ok":true}""", false)]
    [InlineData("""{"error":"ambiguous_selector"}""", false)]
    public void Wait_for_needs_positive_structured_evidence(string response, bool delivered)
    {
        Assert.Equal(delivered,
            WindowsUiActionResultPolicy.IsDelivered("ui_wait_for", response));
    }

    [Theory]
    [InlineData("""{"success":true}""", true)]
    [InlineData("""{"performedAction":"InvokePattern.Invoke"}""", true)]
    [InlineData("""{"success":false}""", false)]
    [InlineData("""{"error":"element_not_found"}""", false)]
    [InlineData("not-json", false)]
    [InlineData("", false)]
    [InlineData("[]", false)]
    public void Invoke_needs_well_formed_nonerror_command_response(string response, bool delivered)
    {
        Assert.Equal(delivered,
            WindowsUiActionResultPolicy.IsDelivered("ui_invoke", response));
    }

    [Fact]
    public void Unknown_action_cannot_leverage_valid_json_response()
    {
        Assert.False(WindowsUiActionResultPolicy.IsDelivered(
            "run_command", """{"success":true}"""));
    }

    [Theory]
    [InlineData("ui_invoke", "{}")]
    [InlineData("ui_set_value", "{}")]
    [InlineData("ui_invoke", """{"status":"ok"}""")]
    [InlineData("ui_set_value", """{"ok":true}""")]
    [InlineData("ui_invoke", """{"performedAction":"   "}""")]
    [InlineData("ui_set_value", """{"performedAction":42}""")]
    [InlineData("ui_set_value", """{"success":"true"}""")]
    public void An_inert_envelope_with_no_error_is_not_delivery_evidence(string action, string response)
    {
        // Before this, any JSON object without an error field was reported as a delivered action, so an empty reply
        // with exit code zero was logged as progress that never happened.
        Assert.False(WindowsUiActionResultPolicy.IsDelivered(action, response));
    }

    [Theory]
    [InlineData("ui_invoke", """{"success":true}""")]
    [InlineData("ui_set_value", """{"success":true}""")]
    [InlineData("ui_set_value", """{"performedAction":"ValuePattern.SetValue"}""")]
    public void A_positive_envelope_is_delivery_evidence(string action, string response)
    {
        Assert.True(WindowsUiActionResultPolicy.IsDelivered(action, response));
    }
}
