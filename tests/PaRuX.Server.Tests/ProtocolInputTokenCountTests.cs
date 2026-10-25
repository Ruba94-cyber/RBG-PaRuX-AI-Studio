using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ProtocolInputTokenCountTests
{
    private sealed class FakeRuntimeTokenizer(int? count)
    {
        public JsonObject? LastPayload { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<int?> TryCountAsync(JsonObject payload, CancellationToken cancellationToken)
        {
            LastCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            LastPayload = (JsonObject)payload.DeepClone();
            return Task.FromResult(count);
        }
    }

    [Fact]
    public async Task Runtime_tokenizer_count_is_returned_with_Tokenizer_basis()
    {
        JsonObject normalized = CreateNormalizedRequest();
        var tokenizer = new FakeRuntimeTokenizer(4032);
        var context = new DefaultHttpContext();
        using var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        await LocalProtocolCompatibilityEndpoints.WriteInputTokenCountResponseAsync(
            context.Response, normalized, tokenizer.TryCountAsync, CancellationToken.None);

        responseStream.Position = 0;
        using JsonDocument response = await JsonDocument.ParseAsync(responseStream);
        Assert.Equal(4032, response.RootElement.GetProperty("input_tokens").GetInt32());
        Assert.Equal("Tokenizer", response.RootElement.GetProperty("input_tokens_basis").GetString());
        Assert.Equal("exact", context.Response.Headers["X-PaRuX-Token-Count"]);
        Assert.Equal("local-default", tokenizer.LastPayload!["model"]!.GetValue<string>());
        Assert.Contains("lookup", tokenizer.LastPayload["tools"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_runtime_count_returns_and_labels_the_existing_estimate()
    {
        JsonObject normalized = CreateNormalizedRequest();
        var tokenizer = new FakeRuntimeTokenizer(null);
        var context = new DefaultHttpContext();
        using var responseStream = new MemoryStream();
        context.Response.Body = responseStream;
        int expected = LocalProtocolCompatibilityEndpoints.EstimateInputTokens(normalized);

        await LocalProtocolCompatibilityEndpoints.WriteInputTokenCountResponseAsync(
            context.Response, normalized, tokenizer.TryCountAsync, CancellationToken.None);

        responseStream.Position = 0;
        using JsonDocument response = await JsonDocument.ParseAsync(responseStream);
        Assert.Equal(expected, response.RootElement.GetProperty("input_tokens").GetInt32());
        Assert.Equal("Estimated", response.RootElement.GetProperty("input_tokens_basis").GetString());
        Assert.Equal("estimated", context.Response.Headers["X-PaRuX-Token-Count"]);
        Assert.False(response.RootElement.GetProperty("exact").GetBoolean());
        Assert.Contains("not exact", response.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Estimate_prices_the_tool_block_and_template_overhead_above_the_raw_character_count()
    {
        JsonObject normalized = CreateNormalizedRequest();
        int raw = ConversationWindowPlanner.EstimateTokens(
            normalized["messages"]!.ToJsonString() + normalized["tools"]!.ToJsonString());
        Assert.True(LocalProtocolCompatibilityEndpoints.EstimateInputTokens(normalized) > raw);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(5000, 4000, true)]
    [InlineData(5000, 4000, false)]
    public async Task Oversize_prompt_returns_413_with_clear_error_and_exactness_label(int count, int limit, bool exact)
    {
        JsonObject normalized = CreateNormalizedRequest();
        var tokenizer = new FakeRuntimeTokenizer(exact ? count : null);
        if (!exact) normalized["messages"] = new JsonArray(new JsonObject
        { ["role"] = "user", ["content"] = new string('a', count * 8) });
        var context = new DefaultHttpContext();
        using var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        await LocalProtocolCompatibilityEndpoints.WriteInputTokenCountResponseAsync(
            context.Response, normalized, tokenizer.TryCountAsync, CancellationToken.None, limit);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        responseStream.Position = 0;
        using JsonDocument response = await JsonDocument.ParseAsync(responseStream);
        JsonElement error = response.RootElement.GetProperty("error");
        Assert.Equal("prompt_exceeds_context", error.GetProperty("code").GetString());
        Assert.Equal(exact, error.GetProperty("exact").GetBoolean());
        Assert.Contains(exact ? "needs" : "not exact", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Prompt_within_the_limit_is_not_rejected()
    {
        var tokenizer = new FakeRuntimeTokenizer(100);
        var context = new DefaultHttpContext();
        using var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        await LocalProtocolCompatibilityEndpoints.WriteInputTokenCountResponseAsync(
            context.Response, CreateNormalizedRequest(), tokenizer.TryCountAsync, CancellationToken.None, 4000);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Adding_count_basis_leaves_generation_budget_fields_unchanged()
    {
        JsonObject normalized = CreateNormalizedRequest();
        JsonObject before = (JsonObject)normalized.DeepClone();
        var tokenizer = new FakeRuntimeTokenizer(24);
        var context = new DefaultHttpContext();
        using var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        await LocalProtocolCompatibilityEndpoints.WriteInputTokenCountResponseAsync(
            context.Response, normalized, tokenizer.TryCountAsync, CancellationToken.None);

        Assert.Equal(before.ToJsonString(), normalized.ToJsonString());
        Assert.Equal(222, tokenizer.LastPayload!["max_tokens"]!.GetValue<int>());
        Assert.Equal(0.2, tokenizer.LastPayload["temperature"]!.GetValue<double>());
        Assert.Equal(222, normalized["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Count_request_is_bound_to_the_caller_cancellation_token()
    {
        using var cancellation = new CancellationTokenSource();
        JsonObject normalized = CreateNormalizedRequest();
        var tokenizer = new FakeRuntimeTokenizer(7);
        var context = new DefaultHttpContext();
        using var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        await LocalProtocolCompatibilityEndpoints.WriteInputTokenCountResponseAsync(
            context.Response, normalized, tokenizer.TryCountAsync, cancellation.Token);

        Assert.Equal(cancellation.Token, tokenizer.LastCancellationToken);
    }

    private static JsonObject CreateNormalizedRequest()
    {
        const string request = """
        {
          "model":"example",
          "messages":[{"role":"user","content":"hello"}],
          "max_tokens":222,
          "temperature":0.2,
          "tools":[{
            "name":"lookup",
            "description":"Find a value",
            "input_schema":{"type":"object","properties":{"query":{"type":"string"}}}
          }]
        }
        """;
        var result = LocalProtocolCompatibilityEndpoints.NormalizeAnthropicRequest(request);
        Assert.Null(result.Error);
        Assert.NotNull(result.Request);
        result.Request!["model"] = "local-default";
        return result.Request;
    }
}
