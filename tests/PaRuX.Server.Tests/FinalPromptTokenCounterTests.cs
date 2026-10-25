using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class FinalPromptTokenCounterTests
{
    [Fact]
    public async Task Direct_engine_final_prompt_is_counted_even_when_mcp_tools_are_available()
    {
        var engine = new PromptCountingEngine(8_100);
        LocalInferenceChatPreparation preparation = Preparation(engine);
        var request = new ChatCompletionRequest
        {
            PreferLowLatency = true,
            Messages = new List<ChatCompletionMessage>
            {
                new() { Role = "system", Content = "The tool cycle already ran; this is the direct engine input." },
                new() { Role = "tool", Content = new string('x', 10_000) }
            }
        };
        JsonArray tools = ToolSchemas();
        int? count = await ChatProxyService.CountDirectRenderedPromptAsync(
            request,
            tools,
            preparation,
            CancellationToken.None);

        Assert.Equal(8_100, count);
        Assert.Equal(1, engine.CountCalls);

        LocalInferenceRequest streamed = LocalInferenceChatBridge.BuildEngineRequest(preparation, request, stream: true);
        Assert.NotNull(engine.LastRequest);
        LocalInferenceRequest counted = engine.LastRequest!;
        Assert.Equal(streamed.ModelVariantId, counted.ModelVariantId);
        Assert.Equal(streamed.Messages, counted.Messages);
        Assert.Equal(streamed.MaxOutputTokens, counted.MaxOutputTokens);
        Assert.Equal(streamed.Temperature, counted.Temperature);
        Assert.Equal(streamed.Workload, counted.Workload);
        Assert.Equal(streamed.EnableThinking, counted.EnableThinking);
        Assert.Equal(streamed.StructuredOutput, counted.StructuredOutput);
        Assert.False(counted.Stream);
        Assert.Equal(10_000, counted.Messages[^1].Content.Length);
        Assert.Equal(4_224, counted.MaxOutputTokens);
        Assert.True(counted.EnableThinking);
    }

    private static LocalInferenceChatPreparation Preparation(PromptCountingEngine engine)
    {
        var selection = new LocalInferenceEngineSelection(
            engine,
            new ManagedModelVariantIdentity("test-model", "rev-1", "q4", ManagedModelFormat.Gguf, "Q4_K_M", "model.gguf"),
            new RuntimeEngineRaceEvidenceRecord(),
            "test-selection");
        return new LocalInferenceChatPreparation(
            selection,
            "chat",
            new AdaptiveReasoningPolicy.Decision("balanced", 128, 1),
            null);
    }

    private static JsonArray ToolSchemas() => new()
    {
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "lookup",
                ["description"] = "Look up a source.",
                ["parameters"] = new JsonObject { ["type"] = "object" }
            }
        }
    };

    private sealed class PromptCountingEngine(int tokenCount) : ILocalInferenceEngine, ILocalInferencePromptTokenCounter
    {
        public LocalInferenceEngineDescriptor Descriptor { get; } = new(
            "prompt-counter-test",
            "Prompt Counter Test",
            EngineImplementationState.Integrated,
            new HashSet<ManagedModelFormat> { ManagedModelFormat.Gguf },
            LocalInferenceCapabilities.Text | LocalInferenceCapabilities.ToolCalling,
            new HashSet<string>(),
            PaRuXManaged: false,
            SupportsManagedUpdates: false);

        public LocalInferenceRequest? LastRequest { get; private set; }
        public int CountCalls { get; private set; }
        public Task<LocalInferenceEngineStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new LocalInferenceEngineStatus(Descriptor.Id, true, true, "test", "variant"));
        public Task EnsureReadyAsync(string modelVariantId, CancellationToken cancellationToken) => Task.CompletedTask;
        public IAsyncEnumerable<LocalInferenceChunk> StreamAsync(LocalInferenceRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This test exercises counting only.");
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<int> CountPromptTokensAsync(LocalInferenceRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            CountCalls++;
            return Task.FromResult(tokenCount);
        }
    }

}
