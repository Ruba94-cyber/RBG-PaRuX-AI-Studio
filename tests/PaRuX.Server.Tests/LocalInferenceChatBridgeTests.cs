using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalInferenceChatBridgeTests
{
    [Fact]
    public void Ordinary_chat_maps_to_chat_workload()
    {
        ChatCompletionRequest request = Request("Ciao, spiegami in breve come funziona questo modello.");

        Assert.Equal("chat", LocalInferenceChatBridge.InferWorkload(request, hasRetrievalContext: false));
    }

    [Fact]
    public void Coding_prompt_maps_to_coding_even_when_retrieval_is_present()
    {
        ChatCompletionRequest request = Request("Implementa e testa questo refactor C# nel repository.");

        Assert.Equal("coding", LocalInferenceChatBridge.InferWorkload(request, hasRetrievalContext: true));
    }

    [Fact]
    public void Retrieval_turn_maps_to_long_context_when_not_coding()
    {
        ChatCompletionRequest request = Request("Riassumi i documenti che hai trovato per questa domanda.");

        Assert.Equal("long-context", LocalInferenceChatBridge.InferWorkload(request, hasRetrievalContext: true));
    }

    [Fact]
    public void Low_latency_chat_keeps_minimum_visible_output_budget()
    {
        var reasoning = new AdaptiveReasoningPolicy.Decision("minimal", 0, 0.1);

        Assert.Equal(4096, LocalInferenceChatBridge.RecommendMaxOutputTokens("chat", reasoning));
    }

    [Fact]
    public void Coding_budget_includes_reasoning_headroom()
    {
        var reasoning = new AdaptiveReasoningPolicy.Decision("high", 4096, 0.8);

        Assert.Equal(12_288, LocalInferenceChatBridge.RecommendMaxOutputTokens("coding", reasoning));
    }

    [Fact]
    public void Output_budget_is_bounded_even_for_extreme_reasoning_request()
    {
        var reasoning = new AdaptiveReasoningPolicy.Decision("xhigh", 100_000, 1.0);

        Assert.Equal(24_576, LocalInferenceChatBridge.RecommendMaxOutputTokens("long-context", reasoning));
    }

    [Fact]
    public void Unknown_workload_fails_closed()
    {
        var reasoning = new AdaptiveReasoningPolicy.Decision("minimal", 0, 0.1);

        Assert.Throws<InvalidDataException>(() =>
            LocalInferenceChatBridge.RecommendMaxOutputTokens("unsupported", reasoning));
    }

    private static ChatCompletionRequest Request(string prompt) => new()
    {
        Messages = new List<ChatCompletionMessage>
        {
            new() { Role = "user", Content = prompt }
        }
    };
}
