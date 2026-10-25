using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The app chat route (/api/v1/chat/completions) used to deserialize an OpenAI-shaped body and then
/// silently drop max_tokens / stop / temperature / top_p / seed (the contract had no such fields), so
/// a caller asking for 256 tokens received thousands. The route must either forward each field to the
/// runtime or reject it explicitly (BIC-05 honesty).
/// </summary>
public sealed class ChatProxySamplingPassthroughTests
{
    private static ChatCompletionRequest Parse(string json) =>
        JsonSerializer.Deserialize<ChatCompletionRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static ChatCompletionRequest WithUser(Action<ChatCompletionRequest>? configure = null)
    {
        var request = new ChatCompletionRequest
        {
            Messages = { new ChatCompletionMessage { Role = "user", Content = "hi" } }
        };
        configure?.Invoke(request);
        return request;
    }

    [Fact]
    public void Snake_case_openai_fields_bind_and_are_forwarded_to_the_runtime()
    {
        ChatCompletionRequest request = Parse(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_tokens\":64,\"temperature\":0.2,\"top_p\":0.9,\"seed\":7,\"stop\":[\"END\",\"STOP\"]}");

        JsonObject payload = ChatProxyService.BuildProviderPayload(request);

        Assert.Equal(64, payload["max_tokens"]!.GetValue<int>());
        Assert.Equal(0.2, payload["temperature"]!.GetValue<double>(), 6);
        Assert.Equal(0.9, payload["top_p"]!.GetValue<double>(), 6);
        Assert.Equal(7, payload["seed"]!.GetValue<long>());
        Assert.Equal(new[] { "END", "STOP" }, payload["stop"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void A_single_stop_string_is_forwarded()
    {
        ChatCompletionRequest request = Parse("{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stop\":\"END\"}");
        JsonObject payload = ChatProxyService.BuildProviderPayload(request);
        Assert.Equal("END", payload["stop"]!.GetValue<string>());
    }

    [Fact]
    public void Max_completion_tokens_wins_over_the_deprecated_max_tokens()
    {
        ChatCompletionRequest request = Parse(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_tokens\":500,\"max_completion_tokens\":32}");
        JsonObject payload = ChatProxyService.BuildProviderPayload(request);
        Assert.Equal(32, payload["max_tokens"]!.GetValue<int>());
        Assert.False(payload.ContainsKey("max_completion_tokens"));
    }

    [Fact]
    public void Absent_sampling_fields_are_not_invented()
    {
        JsonObject payload = ChatProxyService.BuildProviderPayload(WithUser());
        foreach (string key in new[] { "max_tokens", "temperature", "top_p", "seed", "stop" })
            Assert.False(payload.ContainsKey(key), key);
    }

    [Theory]
    [InlineData("{\"max_tokens\":0}", "max_tokens")]
    [InlineData("{\"max_tokens\":-5}", "max_tokens")]
    [InlineData("{\"max_completion_tokens\":0}", "max_completion_tokens")]
    [InlineData("{\"temperature\":3}", "temperature")]
    [InlineData("{\"temperature\":-0.1}", "temperature")]
    [InlineData("{\"top_p\":1.5}", "top_p")]
    [InlineData("{\"top_p\":-0.2}", "top_p")]
    [InlineData("{\"stop\":[\"a\",\"b\",\"c\",\"d\",\"e\"]}", "stop")]
    [InlineData("{\"stop\":[1,2]}", "stop")]
    [InlineData("{\"stop\":{\"x\":1}}", "stop")]
    public void Invalid_values_are_rejected_explicitly_never_ignored(string extra, string field)
    {
        string json = "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]," + extra.TrimStart('{');
        ChatCompletionRequest request = Parse(json);

        string? error = ChatSamplingOptions.Validate(request);

        Assert.NotNull(error);
        Assert.Contains(field, error);
    }

    [Fact]
    public void Valid_values_pass_validation()
    {
        ChatCompletionRequest request = Parse(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_tokens\":1,\"temperature\":0,\"top_p\":1,\"seed\":-3,\"stop\":[\"a\"]}");
        Assert.Null(ChatSamplingOptions.Validate(request));
        Assert.Null(ChatSamplingOptions.Validate(WithUser()));
    }
}

public sealed class LocalApiThinkingStarvationGuardTests
{
    [Theory]
    [InlineData("max_tokens")]
    [InlineData("max_completion_tokens")]
    public void A_small_cap_in_either_field_turns_hidden_thinking_off(string field)
    {
        var body = new JsonObject { [field] = 12 };
        LocalApiServerService.GuardAgainstThinkingStarvation(body);
        Assert.False(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("max_tokens", 4096)]
    [InlineData("max_completion_tokens", 4096)]
    public void A_generous_cap_keeps_the_callers_reasoning_behaviour(string field, int cap)
    {
        var body = new JsonObject { [field] = cap };
        LocalApiServerService.GuardAgainstThinkingStarvation(body);
        Assert.False(body.ContainsKey("chat_template_kwargs"));
    }
}
