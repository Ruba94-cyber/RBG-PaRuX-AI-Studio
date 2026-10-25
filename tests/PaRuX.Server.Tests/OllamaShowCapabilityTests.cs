using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OllamaShowCapabilityTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public OllamaShowCapabilityTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public void Text_chat_models_advertise_completion_chat_and_buffered_tools()
    {
        IReadOnlyList<string> capabilities = OllamaCompatibilityEndpoints.BuildShowCapabilities(
            "parux-main",
            CreateModel("text-chat.gguf"));

        Assert.Equal(new[] { "completion", "chat", "tools" }, capabilities);
    }

    [Fact]
    public async Task Show_capabilities_change_for_the_embedding_model()
    {
        using HttpClient client = CreateAuthenticatedClient(_factory);
        using HttpResponseMessage response = await client.PostAsync(
            "/api/show",
            JsonBody(JsonSerializer.Serialize(new { model = LocalApiEmbeddingsService.ModelId })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string[] embeddingCapabilities = ReadCapabilities(payload);
        IReadOnlyList<string> chatCapabilities = OllamaCompatibilityEndpoints.BuildShowCapabilities(
            "parux-main",
            CreateModel("text-chat.gguf"));

        Assert.Equal(new[] { "embedding" }, embeddingCapabilities);
        Assert.DoesNotContain("chat", embeddingCapabilities);
        Assert.DoesNotContain("tools", embeddingCapabilities);
        Assert.False(embeddingCapabilities.SequenceEqual(chatCapabilities));
    }

    [Fact]
    public void Vision_requires_a_paired_projector_from_the_same_manifest_family()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "show-capabilities-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string modelPath = Path.Combine(directory, "Qwen3-VL-8B-Instruct-Q4_K_M.gguf");
            File.WriteAllText(modelPath, string.Empty);

            IReadOnlyList<string> withoutProjector = OllamaCompatibilityEndpoints.BuildShowCapabilities(
                "parux-vision",
                CreateModel(modelPath));
            Assert.DoesNotContain("vision", withoutProjector);

            string wrongProjector = Path.Combine(directory, "mmproj-llava-v1.6-f16.gguf");
            File.WriteAllText(wrongProjector, string.Empty);
            IReadOnlyList<string> withWrongFamily = OllamaCompatibilityEndpoints.BuildShowCapabilities(
                "parux-vision",
                CreateModel(modelPath));
            Assert.DoesNotContain("vision", withWrongFamily);

            string pairedProjector = Path.Combine(directory, "mmproj-Qwen3-VL-8B-Instruct-F16.gguf");
            File.WriteAllText(pairedProjector, string.Empty);
            IReadOnlyList<string> withPairedProjector = OllamaCompatibilityEndpoints.BuildShowCapabilities(
                "parux-vision",
                CreateModel(modelPath));

            Assert.Equal(new[] { "completion", "chat", "vision", "tools" }, withPairedProjector);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Qwen3-VL-4B-Instruct-Q4_K_M.gguf", "mmproj-Qwen3-VL-4B-Instruct-F16.gguf", true)]
    [InlineData("Qwen3-VL-4B-Thinking-Q4_K_M.gguf", "mmproj-Qwen3-VL-4B-Thinking-F16.gguf", true)]
    [InlineData("Qwen3-VL-4B-Instruct-Q4_K_M.gguf", null, false)]
    [InlineData("Qwen3-4B-Q4_K_M.gguf", "mmproj-Qwen3-VL-4B-Instruct-F16.gguf", false)]
    public void Small_models_report_vision_only_with_a_paired_managed_projector(
        string model, string? projector, bool expectVision)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "show-small-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string modelPath = Path.Combine(directory, model);
            File.WriteAllText(modelPath, string.Empty);
            if (projector != null) File.WriteAllText(Path.Combine(directory, projector), string.Empty);

            IReadOnlyList<string> capabilities = OllamaCompatibilityEndpoints.BuildShowCapabilities(
                "parux-small", CreateModel(modelPath));

            Assert.Equal(expectVision, capabilities.Contains("vision"));
            Assert.Contains("completion", capabilities);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("Qwen3-4B-Thinking-2507-Q4_K_M.gguf", true)]
    [InlineData("DeepSeek-R1-Distill-Qwen-7B-Q4_K_M.gguf", true)]
    [InlineData("Llama-3.2-3B-Instruct-Q4_K_M.gguf", false)]
    public void Thinking_is_reported_only_for_reasoning_models(string file, bool expectThinking)
    {
        IReadOnlyList<string> capabilities = OllamaCompatibilityEndpoints.BuildShowCapabilities(
            "parux-main", CreateModel(file));

        Assert.Equal(expectThinking, capabilities.Contains("thinking"));
    }

    private static string[] ReadCapabilities(JsonDocument payload) => payload.RootElement
        .GetProperty("capabilities")
        .EnumerateArray()
        .Select(capability => capability.GetString()!)
        .ToArray();

    private static LocalApiServerService.LocalApiModel CreateModel(string modelPath) => new(
        "test-model",
        "model",
        0,
        "parux",
        Path.GetFileName(modelPath))
    {
        ModelPath = Path.IsPathRooted(modelPath) ? modelPath : null
    };

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<global::Program> factory)
    {
        HttpClient client = factory.CreateClient();
        ServerState state = factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    private static StringContent JsonBody(string body) => new(body, Encoding.UTF8, "application/json");
}
