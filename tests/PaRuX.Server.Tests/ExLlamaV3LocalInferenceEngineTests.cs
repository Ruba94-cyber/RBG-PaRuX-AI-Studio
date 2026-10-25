using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExLlamaV3LocalInferenceEngineTests
{
    [Fact]
    public void Worker_ready_event_uses_exact_v2_protocol()
    {
        ExLlamaWorkerEvent evt = ExLlamaV3LocalInferenceEngine.ParseWorkerEvent(
            "{\"apiVersion\":2,\"type\":\"ready\"}");

        Assert.Equal(2, evt.ApiVersion);
        Assert.Equal("ready", evt.Type);
    }

    [Fact]
    public void Worker_delta_preserves_unicode_content()
    {
        ExLlamaWorkerEvent evt = ExLlamaV3LocalInferenceEngine.ParseWorkerEvent(
            "{\"apiVersion\":2,\"id\":\"abc\",\"type\":\"delta\",\"content\":\"Ciao 👋\"}");

        Assert.Equal("abc", evt.Id);
        Assert.Equal("Ciao 👋", evt.Content);
    }

    [Fact]
    public void Worker_reasoning_event_preserves_separate_reasoning_content()
    {
        ExLlamaWorkerEvent evt = ExLlamaV3LocalInferenceEngine.ParseWorkerEvent(
            "{\"apiVersion\":2,\"id\":\"abc\",\"type\":\"reasoning\",\"content\":\"Analizzo prima i vincoli…\"}");

        Assert.Equal("abc", evt.Id);
        Assert.Equal("reasoning", evt.Type);
        Assert.Equal("Analizzo prima i vincoli…", evt.Content);
    }

    [Theory]
    [InlineData("{\"apiVersion\":1,\"type\":\"ready\"}")]
    [InlineData("{\"apiVersion\":3,\"type\":\"ready\"}")]
    [InlineData("{\"apiVersion\":1,\"type\":\"delta\"}")]
    [InlineData("{\"apiVersion\":1,\"type\":\"reasoning\"}")]
    [InlineData("not-json")]
    public void Worker_protocol_fails_closed_on_incompatible_events(string line)
    {
        Assert.Throws<InvalidDataException>(() => ExLlamaV3LocalInferenceEngine.ParseWorkerEvent(line));
    }

    [Fact]
    public void Built_in_descriptor_advertises_only_capabilities_implemented_by_direct_adapter()
    {
        var registry = new LocalInferenceEngineRegistry();
        LocalInferenceEngineDescriptor? descriptor = registry.FindDescriptor(LocalInferenceEngineRegistry.ExLlamaV3Id);

        Assert.NotNull(descriptor);
        Assert.Equal(EngineImplementationState.Integrated, descriptor!.ImplementationState);
        Assert.True(descriptor.Capabilities.HasFlag(LocalInferenceCapabilities.Text));
        Assert.False(descriptor.Capabilities.HasFlag(LocalInferenceCapabilities.ToolCalling));
        Assert.False(descriptor.Capabilities.HasFlag(LocalInferenceCapabilities.Vision));
        Assert.False(descriptor.Capabilities.HasFlag(LocalInferenceCapabilities.ContinuousBatching));
    }
}
