using System.Text.Json.Nodes;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The model_routed event tells the chat which model answered and where it ran. The fields are additive, and the event
/// must never carry a credential, a path or a remote URL beyond its host.
/// </summary>
public sealed class ChatProvenanceEventTests
{
    private static RouteDecisionDto Route(bool fellBackToStandard = false) => new()
    {
        Mode = "auto",
        ResolvedRole = "standard",
        FellBackToStandard = fellBackToStandard,
        Success = true,
        Swapped = false,
        Message = "ok",
        DurationMs = 1,
        DecidedUtc = DateTime.UtcNow,
        ModelPath = "C:\\models\\qwen.gguf",
        AutoProfile = "balanced",
        SelectedIntelligenceScore = 0.8,
        SelectionReason = "Balanced profile chose the fastest capable model."
    };

    private static LocalInferenceChatPreparation Preparation(string? fallbackReason = null) =>
        new(null, "chat", new AdaptiveReasoningPolicy.Decision("low", 0, 0), fallbackReason);

    [Fact]
    public void A_local_route_names_the_managed_runtime_and_keeps_the_existing_fields()
    {
        JsonObject routed = ChatProxyService.BuildModelRoutedEvent(
            Route(), Preparation(), manualModelLock: false, structuredOutput: false,
            requestModel: "qwen-local", usesExternalProvider: false, providerEndpoint: "http://127.0.0.1:1234");

        Assert.Equal("model_routed", routed["parux_event"]!.GetValue<string>());
        Assert.Equal("local", routed["provider"]!.GetValue<string>());
        Assert.Equal("local", routed["locality"]!.GetValue<string>());
        Assert.Equal("qwen.gguf", routed["selectedModel"]!.GetValue<string>());
        Assert.Equal("llama.cpp", routed["engineId"]!.GetValue<string>());
        Assert.Equal("auto", routed["mode"]!.GetValue<string>());
        Assert.Equal("Balanced profile chose the fastest capable model.", routed["reason"]!.GetValue<string>());
        Assert.False(routed.ContainsKey("endpointHost"));
        Assert.False(routed.ContainsKey("fallbackReason"));
    }

    [Fact]
    public void A_route_that_fell_back_to_standard_says_why()
    {
        JsonObject routed = ChatProxyService.BuildModelRoutedEvent(
            Route(fellBackToStandard: true), Preparation(), manualModelLock: false, structuredOutput: false,
            requestModel: "qwen-local", usesExternalProvider: false, providerEndpoint: "http://127.0.0.1:1234");

        Assert.True(routed["fellBackToStandard"]!.GetValue<bool>());
        Assert.Contains("Standard role", routed["fallbackReason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_engine_that_fell_back_to_llama_cpp_reports_its_reason_as_the_fallback()
    {
        JsonObject routed = ChatProxyService.BuildModelRoutedEvent(
            Route(), Preparation("The direct engine failed its readiness check."), manualModelLock: false,
            structuredOutput: false, requestModel: "qwen-local", usesExternalProvider: false,
            providerEndpoint: "http://127.0.0.1:1234");

        Assert.Equal("The direct engine failed its readiness check.", routed["fallbackReason"]!.GetValue<string>());
        Assert.Equal("The direct engine failed its readiness check.", routed["engineFallback"]!.GetValue<string>());
    }

    [Fact]
    public void A_remote_destination_reports_its_host_and_never_its_path_query_or_credentials()
    {
        JsonObject routed = ChatProxyService.BuildModelRoutedEvent(
            Route(), Preparation(), manualModelLock: false, structuredOutput: false,
            requestModel: "remote-model", usesExternalProvider: true,
            providerEndpoint: "https://models.example.com:8443/v1?key=sk-secret-value");

        Assert.Equal("external-openai-compatible", routed["provider"]!.GetValue<string>());
        Assert.Equal("remote", routed["locality"]!.GetValue<string>());
        Assert.Equal("models.example.com", routed["endpointHost"]!.GetValue<string>());
        string json = routed.ToJsonString();
        Assert.DoesNotContain("sk-secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_private_endpoint_on_the_external_provider_is_local_and_reports_no_host()
    {
        JsonObject routed = ChatProxyService.BuildModelRoutedEvent(
            Route(), Preparation(), manualModelLock: false, structuredOutput: false,
            requestModel: "lan-model", usesExternalProvider: true, providerEndpoint: "http://192.168.1.20:1234");

        Assert.Equal("external-openai-compatible", routed["provider"]!.GetValue<string>());
        Assert.Equal("local", routed["locality"]!.GetValue<string>());
        Assert.False(routed.ContainsKey("endpointHost"));
    }
}
