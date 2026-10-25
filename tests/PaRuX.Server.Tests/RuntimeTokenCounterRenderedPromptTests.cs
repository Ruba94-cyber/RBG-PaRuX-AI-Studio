using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeTokenCounterRenderedPromptTests
{
    private sealed class RenderedHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? Path { get; private set; }
        public JsonObject? Body { get; private set; }
        public int ReturnedTokens { get; set; } = 321;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Path = request.RequestUri?.AbsolutePath;
            string json = request.Content == null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Body = JsonNode.Parse(json) as JsonObject;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { input_tokens = ReturnedTokens }),
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    [Fact]
    public async Task Final_counter_preserves_prompt_semantics_and_strips_only_execution_hints()
    {
        var handler = new RenderedHandler { ReturnedTokens = 777 };
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        var payload = new JsonObject
        {
            ["model"] = "local-default",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }),
            ["stream"] = true,
            ["cache_prompt"] = true,
            ["n_cache_reuse"] = 2048,
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "function" }),
            ["response_format"] = new JsonObject { ["type"] = "json_schema" },
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true }
        };

        int? counted = await counter.TryCountRenderedChatPromptAsync(
            LocalRuntimeService.ManagedProviderEndpoint, payload, CancellationToken.None);

        Assert.Equal(777, counted);
        Assert.Equal(1, handler.Requests);
        Assert.Equal("/v1/chat/completions/input_tokens", handler.Path);
        Assert.NotNull(handler.Body);
        Assert.False(handler.Body!.ContainsKey("stream"));
        Assert.False(handler.Body.ContainsKey("cache_prompt"));
        Assert.False(handler.Body.ContainsKey("n_cache_reuse"));
        Assert.True(handler.Body.ContainsKey("tools"));
        Assert.True(handler.Body.ContainsKey("response_format"));
        Assert.True(handler.Body.ContainsKey("chat_template_kwargs"));
    }

    [Fact]
    public async Task Non_managed_provider_is_never_probed_with_llama_specific_final_counter()
    {
        var handler = new RenderedHandler();
        var counter = new RuntimeTokenCounter(new HttpClient(handler));

        int? counted = await counter.TryCountRenderedChatPromptAsync(
            "http://127.0.0.1:9999",
            new JsonObject { ["messages"] = new JsonArray() },
            CancellationToken.None);

        Assert.Null(counted);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"input_tokens\":0}")]
    [InlineData("{\"input_tokens\":\"bad\"}")]
    public async Task Missing_or_invalid_final_count_degrades_to_unknown_instead_of_faking_a_number(string responseJson)
    {
        var handler = new RawHandler(responseJson);
        var counter = new RuntimeTokenCounter(new HttpClient(handler));

        int? counted = await counter.TryCountRenderedChatPromptAsync(
            LocalRuntimeService.ManagedProviderEndpoint,
            new JsonObject { ["messages"] = new JsonArray() },
            CancellationToken.None);

        Assert.Null(counted);
    }

    private sealed class RawHandler : HttpMessageHandler
    {
        private readonly string _json;
        public RawHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
    }
}
