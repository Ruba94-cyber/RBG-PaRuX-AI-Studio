using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatModelRouteExplanationTests
{
    [Fact]
    public void Auto_explanation_distinguishes_router_estimate_from_recorded_benchmark()
    {
        ChatModelRouteInfo? route = ChatModelRouteInfo.TryParse("""
            {"parux_event":"model_routed","mode":"auto","autoProfile":"responsive","selectedModel":"C:\\models\\qwen.gguf","selectedIntelligence":0.91,"tokensPerSecond":18.4,"reason":"Responsive profile ranked capability before stored speed.","manualLock":false}
            """);

        Assert.NotNull(route);
        Assert.Equal("qwen.gguf", route.SelectedModel);
        IReadOnlyList<ChatModelRouteExplanationLine> explanation = ChatModelRouteExplanationBuilder.Build(route);

        Assert.Equal("qwen.gguf", Value(explanation, "Selected model"));
        Assert.Equal("AUTO · Responsive", Value(explanation, "Routing"));
        Assert.Contains("91%", Value(explanation, "Capability fit estimate"), StringComparison.Ordinal);
        Assert.Contains("not a verified score for answer quality", Value(explanation, "Capability fit estimate"), StringComparison.Ordinal);
        Assert.Contains("Recorded benchmark: 18", Value(explanation, "Speed evidence"), StringComparison.Ordinal);
        Assert.Contains("tokens/s", Value(explanation, "Speed evidence"), StringComparison.Ordinal);
        Assert.Contains("not a live speed measurement", Value(explanation, "Speed evidence"), StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\models", string.Join("\n", explanation.Select(line => line.Value)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manual_lock_fallback_and_missing_metrics_are_explicit()
    {
        ChatModelRouteInfo? route = ChatModelRouteInfo.TryParse("""
            {"parux_event":"model_routed","mode":"manual","manualLock":true,"fellBackToStandard":true,"selectedModel":"manual.gguf"}
            """);

        Assert.NotNull(route);
        IReadOnlyList<ChatModelRouteExplanationLine> explanation = ChatModelRouteExplanationBuilder.Build(route);

        Assert.Contains("Manual lock", Value(explanation, "Routing"), StringComparison.Ordinal);
        Assert.Contains("used the Standard role", Value(explanation, "Fallback"), StringComparison.Ordinal);
        Assert.Equal("Not measured for this model and role.", Value(explanation, "Capability fit estimate"));
        Assert.Equal("No benchmark speed is recorded for this model.", Value(explanation, "Speed evidence"));
        Assert.Contains("Core did not provide", Value(explanation, "Why it was chosen"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"parux_event\":\"token\"}")]
    [InlineData("not-json")]
    [InlineData("{\"parux_event\":\"model_routed\",\"manualLock\":\"yes\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"not an object\"")]
    [InlineData("42")]
    public void Old_or_malformed_route_events_do_not_break_chat(string payload)
    {
        ChatModelRouteInfo? route = ChatModelRouteInfo.TryParse(payload);

        if (payload.Contains("model_routed", StringComparison.Ordinal))
        {
            Assert.NotNull(route);
            Assert.False(route!.ManualLock);
            Assert.False(route.FellBackToStandard);
        }
        else
        {
            Assert.Null(route);
        }
    }

    [Fact]
    public void Empty_chat_has_a_useful_explanation_state()
    {
        IReadOnlyList<ChatModelRouteExplanationLine> explanation = ChatModelRouteExplanationBuilder.Build(null);

        Assert.Single(explanation);
        Assert.Equal("No route yet", explanation[0].Label);
        Assert.Contains("Send a chat turn", explanation[0].Value, StringComparison.Ordinal);
    }

    private static string Value(IReadOnlyList<ChatModelRouteExplanationLine> lines, string label) =>
        Assert.Single(lines, line => line.Label == label).Value;
}
