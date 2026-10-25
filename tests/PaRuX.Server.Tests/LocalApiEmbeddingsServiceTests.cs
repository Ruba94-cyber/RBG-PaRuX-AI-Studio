using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Covers the OpenAI-compatible /v1/embeddings surface. The behaviour that matters most here is
/// that it never invents vectors: an uninstalled engine has to be an explicit failure, because a
/// caller silently storing zero vectors would corrupt its own index in a way that looks like a
/// retrieval-quality problem much later.
/// </summary>
public sealed class LocalApiEmbeddingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-embed-api-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private LocalApiEmbeddingsService NotInstalled() =>
        new(new EmbeddingService(Path.Combine(_root, "engine-not-installed")));

    // EmbeddingEngineFact skips these at discovery when the optional engine is absent. This guard only fails
    // when PARUX_REQUIRE_OPTIONAL=1 requires an engine that the test host cannot see.
    private static LocalApiEmbeddingsService RealEngine()
    {
        var engine = new EmbeddingService();
        OptionalPrerequisite.AssertAvailable(engine.IsInstalled, "the local embedding engine (ai-runtimes/embeddings)");
        return new LocalApiEmbeddingsService(engine);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"input\":\"\"}")]
    [InlineData("{\"input\":[]}")]
    [InlineData("{\"input\":123}")]
    [InlineData("{\"input\":[\"ok\",42]}")]
    public void Malformed_requests_are_rejected_with_400_before_any_engine_work(string body)
    {
        LocalApiCompletionResult result = NotInstalled().CreateEmbeddings(body);
        Assert.Equal(LocalApiOutcome.BadRequest, result.Outcome);
        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public void Unsupported_encoding_format_is_rejected_rather_than_silently_ignored()
    {
        LocalApiCompletionResult result = NotInstalled()
            .CreateEmbeddings("{\"input\":\"hello\",\"encoding_format\":\"base64\"}");
        Assert.Equal(LocalApiOutcome.BadRequest, result.Outcome);
        Assert.Contains("encoding_format", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_uninstalled_engine_fails_honestly_instead_of_returning_zero_vectors()
    {
        LocalApiCompletionResult result = NotInstalled().CreateEmbeddings("{\"input\":\"hello\"}");

        Assert.Equal(503, result.StatusCode);
        Assert.Equal(LocalApiOutcome.UpstreamFailure, result.Outcome);
        Assert.Contains("not installed", result.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"embedding\"", result.Body, StringComparison.Ordinal);
    }

    [EmbeddingEngineFact]
    public void Single_string_input_returns_one_openai_shaped_embedding()
    {
        LocalApiEmbeddingsService service = RealEngine();

        LocalApiCompletionResult result = service.CreateEmbeddings("{\"input\":\"a local sentence to embed\"}");
        Assert.Equal(200, result.StatusCode);

        JsonNode root = JsonNode.Parse(result.Body)!;
        Assert.Equal("list", root["object"]!.GetValue<string>());
        Assert.Equal(LocalApiEmbeddingsService.ModelId, root["model"]!.GetValue<string>());

        JsonArray data = root["data"]!.AsArray();
        Assert.Single(data);
        Assert.Equal("embedding", data[0]!["object"]!.GetValue<string>());
        Assert.Equal(0, data[0]!["index"]!.GetValue<int>());

        JsonArray vector = data[0]!["embedding"]!.AsArray();
        Assert.Equal(EmbeddingService.Dimensions, vector.Count);
        Assert.Contains(vector, value => Math.Abs(value!.GetValue<float>()) > 0.0001f);
        Assert.True(root["usage"]!["total_tokens"]!.GetValue<int>() > 0);
    }

    [EmbeddingEngineFact]
    public void Array_input_returns_one_correctly_indexed_embedding_per_item()
    {
        LocalApiEmbeddingsService service = RealEngine();

        LocalApiCompletionResult result = service.CreateEmbeddings(
            "{\"input\":[\"telescopes resolve faint galaxies\",\"kneading dough builds gluten\",\"routers forward packets\"]}");
        Assert.Equal(200, result.StatusCode);

        JsonArray data = JsonNode.Parse(result.Body)!["data"]!.AsArray();
        Assert.Equal(3, data.Count);
        for (int i = 0; i < data.Count; i++)
        {
            Assert.Equal(i, data[i]!["index"]!.GetValue<int>());
            Assert.Equal(EmbeddingService.Dimensions, data[i]!["embedding"]!.AsArray().Count);
        }
    }

    /// <summary>Different text must produce different vectors - a constant-vector bug would still
    /// satisfy every shape assertion above while making retrieval useless.</summary>
    [EmbeddingEngineFact]
    public void Semantically_different_inputs_produce_different_vectors()
    {
        LocalApiEmbeddingsService service = RealEngine();

        LocalApiCompletionResult result = service.CreateEmbeddings(
            "{\"input\":[\"a telescope observing distant galaxies\",\"a router forwarding network packets\"]}");
        JsonArray data = JsonNode.Parse(result.Body)!["data"]!.AsArray();

        float[] first = data[0]!["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
        float[] second = data[1]!["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();

        float dot = 0;
        for (int i = 0; i < first.Length; i++) dot += first[i] * second[i];
        Assert.True(dot < 0.99f, $"unrelated inputs should not be near-identical vectors (cosine {dot:0.000})");
    }
}
