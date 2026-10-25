using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserDevToolsEvaluationPolicyTests
{
    [Theory]
    [InlineData("""{"result":{"type":"object","value":{"ok":false,"status":"missing"}}}""", true, false)]
    [InlineData("""{"result":{"type":"object","value":{"ok":false,"error":"selector not found"}}}""", true, false)]
    [InlineData("""{"result":{"type":"object","value":{"ok":true,"status":"passed"}}}""", true, true)]
    [InlineData("""{"result":{"type":"boolean","value":false}}""", true, true)]
    [InlineData("""{"result":{"subtype":"error"}}""", false, false)]
    [InlineData("""{"result":{"value":{"ok":false}},"exceptionDetails":{"text":"secret"}}""", false, false)]
    [InlineData("""{"notACdpResult":true}""", false, false)]
    public void CDP_transport_validity_is_not_the_same_as_an_action_success(
        string json, bool expectedTransportValid, bool expectedActionSuccess)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(expectedTransportValid, BrowserDevToolsEvaluationPolicy.HasUsableResult(doc.RootElement));
        Assert.Equal(expectedActionSuccess, BrowserDevToolsEvaluationPolicy.IsSuccessful(doc.RootElement));
    }

    [Theory]
    [InlineData("""{"result":{"type":"object","value":{"ok":true,"tag":"BUTTON"}}}""", true)]
    [InlineData("""{"result":{"type":"object","value":{"ok":false,"error":"selector not found"}}}""", false)]
    [InlineData("""{"result":{"type":"undefined"},"exceptionDetails":{"text":"TypeError"}}""", false)]
    [InlineData("""{"result":{"subtype":"error","description":"ReferenceError"}}""", false)]
    [InlineData("""{"result":{"type":"boolean","value":false}}""", true)]
    [InlineData("""{"result":{"type":"string","value":"ok"}}""", true)]
    [InlineData("""{"result":{"type":"object","value":{"answer":42}}}""", true)]
    [InlineData("""{"notACdpResult":true}""", false)]
    [InlineData("""[]""", false)]
    public void Browser_evaluation_fails_only_for_actual_protocol_or_action_failures(
        string responseJson, bool expectedSuccess)
    {
        using JsonDocument doc = JsonDocument.Parse(responseJson);
        Assert.Equal(expectedSuccess, BrowserDevToolsEvaluationPolicy.IsSuccessful(doc.RootElement));
    }
}
