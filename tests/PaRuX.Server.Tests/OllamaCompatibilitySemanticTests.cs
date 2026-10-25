using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OllamaCompatibilitySemanticTests
{
    private static readonly Type EndpointType = typeof(PaRuX.Server.ApiKeyService).Assembly
        .GetType("PaRuX.Server.OllamaCompatibilityEndpoints")!;
    private static readonly Type StreamTranslatorType = typeof(PaRuX.Server.ApiKeyService).Assembly
        .GetType("PaRuX.Server.OllamaStreamTranslator")!;

    private static string Translate(string body, bool generate = false)
    {
        MethodInfo method = EndpointType.GetMethod(
            "ToOpenAiRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object[] { body, "parux-main", generate })!;
    }

    private static Exception TranslateFailure(string body, bool generate = false)
    {
        TargetInvocationException wrapper = Assert.Throws<TargetInvocationException>(() => Translate(body, generate));
        return Assert.IsAssignableFrom<Exception>(wrapper.InnerException);
    }

    [Fact]
    public void Tool_definitions_calls_and_results_keep_their_correlation()
    {
        string translated = Translate("""
        {
          "model":"parux-main",
          "messages":[
            {"role":"user","content":"Weather in Turin?"},
            {"role":"assistant","content":"","tool_calls":[
              {"type":"function","function":{"name":"weather","arguments":{"city":"Turin"}}}
            ]},
            {"role":"tool","tool_name":"weather","content":"sunny"}
          ],
          "tools":[
            {"type":"function","function":{"name":"weather","description":"Weather lookup","parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}}
          ],
          "stream":false
        }
        """);

        using JsonDocument document = JsonDocument.Parse(translated);
        JsonElement root = document.RootElement;
        JsonElement messages = root.GetProperty("messages");
        string callId = messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString()!;

        Assert.StartsWith("call_ollama_", callId, StringComparison.Ordinal);
        Assert.Equal("weather", messages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        using JsonDocument arguments = JsonDocument.Parse(
            messages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString()!);
        Assert.Equal("Turin", arguments.RootElement.GetProperty("city").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal(callId, messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("sunny", messages[2].GetProperty("content").GetString());
        Assert.Equal("weather", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public void Base64_Ollama_images_become_bounded_OpenAI_image_url_parts()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl8mWQAAAAASUVORK5CYII=";
        string body = "{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"describe\",\"images\":[\""
            + png + "\"]}],\"stream\":false}";
        string translated = Translate(body);

        using JsonDocument document = JsonDocument.Parse(translated);
        JsonElement content = document.RootElement.GetProperty("messages")[0].GetProperty("content");

        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("describe", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64," + png,
            content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void Json_mode_and_JSON_Schema_map_to_real_response_format_contracts()
    {
        string jsonMode = Translate("""
        {"model":"parux-main","messages":[{"role":"user","content":"json"}],"format":"json","stream":false}
        """);
        using (JsonDocument json = JsonDocument.Parse(jsonMode))
            Assert.Equal("json_object", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());

        string schemaMode = Translate("""
        {
          "model":"parux-main",
          "messages":[{"role":"user","content":"structured"}],
          "format":{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false},
          "stream":false
        }
        """);
        using JsonDocument schema = JsonDocument.Parse(schemaMode);
        JsonElement format = schema.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("ollama_response", format.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.Equal("object", format.GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public void Boolean_thinking_and_supported_sampling_options_are_translated_not_dropped()
    {
        string translated = Translate("""
        {
          "model":"parux-main",
          "messages":[{"role":"user","content":"solve"}],
          "think":false,
          "logprobs":true,
          "top_logprobs":3,
          "options":{"temperature":0.25,"top_p":0.8,"seed":7,"stop":["END"],"num_predict":96},
          "stream":false
        }
        """);

        using JsonDocument document = JsonDocument.Parse(translated);
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.True(root.GetProperty("logprobs").GetBoolean());
        Assert.Equal(3, root.GetProperty("top_logprobs").GetInt32());
        Assert.Equal(0.25, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.8, root.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(7, root.GetProperty("seed").GetInt32());
        Assert.Equal(96, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("END", root.GetProperty("stop")[0].GetString());
    }

    [Theory]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"keep_alive\":\"5m\"}", "keep_alive")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"think\":\"high\"}", "think level")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"options\":{\"num_ctx\":32768}}", "num_ctx")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"mystery\":true}", "mystery")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"weather\"},\"vendor_extension\":true}]}", "tool field 'vendor_extension'")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\",\"vendor_extension\":true}]}", "message field 'vendor_extension'")]
    public void Unsupported_non_null_intent_fails_explicitly(string body, string expected)
    {
        Exception failure = TranslateFailure(body);
        Assert.Contains("unsupported Ollama feature", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expected, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"model\":\"parux-main\",\"stream\":\"false\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}", "stream' must be a boolean")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"weather\",\"parameters\":[]}}]}", "parameters must be a JSON Schema object")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":12}]}", "content' must be a string")]
    public void Malformed_supported_fields_are_rejected_instead_of_defaulted_or_dropped(string body, string expected)
    {
        Exception failure = TranslateFailure(body);
        Assert.Contains(expected, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"model\":\"parux-main\",\"prompt\":\"x\",\"raw\":\"false\"}", "raw' must be a boolean")]
    [InlineData("{\"model\":\"parux-main\",\"prompt\":\"x\",\"suffix\":42}", "suffix' must be a string")]
    public void Malformed_generate_fields_are_rejected_instead_of_defaulted_or_dropped(string body, string expected)
    {
        Exception failure = TranslateFailure(body, generate: true);
        Assert.Contains(expected, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tool_result_without_a_matching_prior_call_is_rejected()
    {
        Exception failure = TranslateFailure("""
        {"model":"parux-main","messages":[{"role":"tool","tool_name":"weather","content":"sunny"}]}
        """);

        Assert.Contains("no preceding matching assistant tool call", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preserved_client_tool_ids_cannot_collide_with_synthesized_ids()
    {
        string translated = Translate("""
        {"model":"parux-main","messages":[{"role":"assistant","tool_calls":[
          {"id":"call_ollama_1","type":"function","function":{"name":"first","arguments":"{}"}},
          {"type":"function","function":{"name":"second","arguments":"{}"}}
        ]}]}
        """);

        using JsonDocument document = JsonDocument.Parse(translated);
        JsonElement calls = document.RootElement.GetProperty("messages")[0].GetProperty("tool_calls");
        Assert.Equal(2, calls.GetArrayLength());
        Assert.Equal("call_ollama_1", calls[0].GetProperty("id").GetString());
        Assert.Equal("call_ollama_2", calls[1].GetProperty("id").GetString());
    }

    [Fact]
    public void Duplicate_client_tool_ids_fail_explicitly()
    {
        Exception failure = TranslateFailure("""
        {"model":"parux-main","messages":[{"role":"assistant","tool_calls":[
          {"id":"duplicate","type":"function","function":{"name":"first","arguments":"{}"}},
          {"id":"duplicate","type":"function","function":{"name":"second","arguments":"{}"}}
        ]}]}
        """);

        Assert.Contains("duplicated", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Buffered_response_preserves_finish_reason_usage_and_tool_arguments()
    {
        MethodInfo frame = EndpointType.GetMethod("FrameFromOpenAi", BindingFlags.NonPublic | BindingFlags.Static)!;
        string openAi = """
        {
          "choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":"","tool_calls":[
            {"id":"call_x","type":"function","function":{"name":"weather","arguments":"{\"city\":\"Turin\"}"}}
          ]}}],
          "usage":{"prompt_tokens":11,"completion_tokens":4}
        }
        """;
        string ollama = (string)frame.Invoke(null, new object[]
        {
            "parux-main", "2026-09-10T10:00:00Z", openAi, false, false
        })!;

        using JsonDocument document = JsonDocument.Parse(ollama);
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("done").GetBoolean());
        Assert.Equal("tool_calls", root.GetProperty("done_reason").GetString());
        Assert.Equal(11, root.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(4, root.GetProperty("eval_count").GetInt32());
        Assert.Equal("Turin", root.GetProperty("message").GetProperty("tool_calls")[0]
            .GetProperty("function").GetProperty("arguments").GetProperty("city").GetString());
    }

    [Fact]
    public async Task Streaming_forwards_requested_thinking_and_real_terminal_usage_without_fabricated_durations()
    {
        await using var destination = new MemoryStream();
        var stream = (Stream)Activator.CreateInstance(
            StreamTranslatorType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object[] { destination, "parux-main", false, true },
            culture: null)!;

        string sse =
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"plan\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":2}}\n\n" +
            "data: [DONE]\n\n";
        byte[] bytes = Encoding.UTF8.GetBytes(sse);
        await stream.WriteAsync(bytes);

        MethodInfo done = StreamTranslatorType.GetMethod("WriteDoneAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)done.Invoke(stream, new object[] { CancellationToken.None })!;

        string ndjson = Encoding.UTF8.GetString(destination.ToArray());
        string[] lines = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.Contains("\"thinking\":\"plan\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"content\":\"answer\"", StringComparison.Ordinal));
        using JsonDocument terminal = JsonDocument.Parse(lines[^1]);
        Assert.True(terminal.RootElement.GetProperty("done").GetBoolean());
        Assert.Equal("stop", terminal.RootElement.GetProperty("done_reason").GetString());
        Assert.Equal(9, terminal.RootElement.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(2, terminal.RootElement.GetProperty("eval_count").GetInt32());
        Assert.False(terminal.RootElement.TryGetProperty("total_duration", out _));
        Assert.False(terminal.RootElement.TryGetProperty("eval_duration", out _));
    }

    [Fact]
    public void Compatibility_discovery_is_versioned_and_keeps_control_plane_closed()
    {
        MethodInfo contractMethod = EndpointType.GetMethod(
            "BuildCapabilityContract", BindingFlags.NonPublic | BindingFlags.Static)!;
        object contract = contractMethod.Invoke(null, null)!;
        string json = JsonSerializer.Serialize(contract);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(1, document.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("supported-or-explicit-error", document.RootElement.GetProperty("behavior").GetString());
        Assert.Equal("buffered", document.RootElement.GetProperty("chat").GetProperty("tools").GetString());
        Assert.False(document.RootElement.GetProperty("chat").GetProperty("streaming_tools").GetBoolean());
        Assert.True(ProviderSurfaceProbe.Covers("/api/parux/capabilities"));
        Assert.False(ProviderSurfaceProbe.Covers("/api/v1/conversations"));
        Assert.True(document.RootElement.GetProperty("embeddings").GetBoolean());
        Assert.True(document.RootElement.GetProperty("embed").GetBoolean());
        Assert.True(document.RootElement.GetProperty("ps").GetBoolean());
        Assert.True(ProviderSurfaceProbe.Covers("/api/ps"));
        Assert.True(ProviderSurfaceProbe.Covers("/api/embeddings"));
        Assert.True(ProviderSurfaceProbe.Covers("/api/embed"));
    }

    [Fact]
    public void HandleEmbeddings_rejects_missing_or_empty_prompt()
    {
        MethodInfo method = EndpointType.GetMethod(
            "HandleEmbeddings", BindingFlags.NonPublic | BindingFlags.Static)!;

        var missing = (Microsoft.AspNetCore.Http.IResult)method.Invoke(null, new object?[] { "{}", null })!;
        Assert.NotNull(missing);

        var empty = (Microsoft.AspNetCore.Http.IResult)method.Invoke(null, new object?[] { "{\"prompt\":\"\"}", null })!;
        Assert.NotNull(empty);
    }

    [Fact]
    public void HandleEmbed_rejects_missing_input()
    {
        MethodInfo method = EndpointType.GetMethod(
            "HandleEmbed", BindingFlags.NonPublic | BindingFlags.Static)!;

        var missing = (Microsoft.AspNetCore.Http.IResult)method.Invoke(null, new object?[] { "{}", null })!;
        Assert.NotNull(missing);
    }
}
