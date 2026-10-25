using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ActiveContextWindowTests
{
    [Fact]
    public void Llama_props_reads_nested_runtime_context()
    {
        using JsonDocument doc = JsonDocument.Parse("{\"default_generation_settings\":{\"n_ctx\":32768}}");
        Assert.Equal(32768, ActiveContextWindow.ParseLlamaProps(doc.RootElement));
    }

    [Fact]
    public void Llama_props_reads_direct_runtime_context()
    {
        using JsonDocument doc = JsonDocument.Parse("{\"n_ctx\":131072}");
        Assert.Equal(131072, ActiveContextWindow.ParseLlamaProps(doc.RootElement));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"default_generation_settings\":{}}")]
    [InlineData("{\"n_ctx\":0}")]
    public void Unknown_or_invalid_managed_shapes_remain_unknown(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(0, ActiveContextWindow.ParseLlamaProps(doc.RootElement));
    }
}
