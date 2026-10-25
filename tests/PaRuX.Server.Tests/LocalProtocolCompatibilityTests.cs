using System.Text.Json.Nodes;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalProtocolCompatibilityTests
{
    [Fact]
    public void Responses_string_input_maps_to_the_existing_chat_runtime_shape()
    {
        var result = LocalProtocolCompatibilityEndpoints.NormalizeResponsesRequest(
            "{\"model\":\"parux-main\",\"input\":\"hello\",\"max_output_tokens\":321,\"stream\":true}");

        Assert.Null(result.Error);
        Assert.NotNull(result.Request);
        Assert.True(result.Stream);
        Assert.Equal("parux-main", result.Model);
        Assert.Equal(false, result.Request!["stream"]!.GetValue<bool>());
        Assert.Equal(321, result.Request["max_tokens"]!.GetValue<int>());
        Assert.Equal("user", result.Request["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("hello", result.Request["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Single(result.Messages!);
    }

    [Fact]
    public void Responses_structured_text_blocks_preserve_roles_and_instructions()
    {
        string json = """
        {
          "model":"parux-reasoning",
          "instructions":"system rule",
          "input":[
            {"type":"message","role":"user","content":[{"type":"input_text","text":"question"}]},
            {"type":"message","role":"assistant","content":[{"type":"output_text","text":"prior"}]}
          ]
        }
        """;

        var result = LocalProtocolCompatibilityEndpoints.NormalizeResponsesRequest(json);

        Assert.Null(result.Error);
        JsonArray messages = result.Request!["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("system rule", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("question", messages[1]!["content"]!.GetValue<string>());
        Assert.Equal("assistant", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("prior", messages[2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Responses_custom_tools_translate_to_chat_completions_without_execution()
    {
        string json = """
        {
          "model":"parux-main",
          "input":"what is the weather?",
          "tools":[{
            "type":"function",
            "name":"get_weather",
            "description":"Return weather data",
            "parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}
          }],
          "tool_choice":{"type":"function","name":"get_weather"}
        }
        """;

        var result = LocalProtocolCompatibilityEndpoints.NormalizeResponsesRequest(json);

        Assert.Null(result.Error);
        JsonObject tool = result.Request!["tools"]![0]!.AsObject();
        Assert.Equal("function", tool["type"]!.GetValue<string>());
        Assert.Equal("get_weather", tool["function"]!["name"]!.GetValue<string>());
        Assert.Equal("object", tool["function"]!["parameters"]!["type"]!.GetValue<string>());
        Assert.Equal("get_weather", result.Request["tool_choice"]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Responses_tool_output_can_return_a_bounded_image_to_the_model()
    {
        string json = """
        {
          "model":"parux-main",
          "input":[
            {"type":"function_call","call_id":"call_1","name":"take_screenshot","arguments":"{}"},
            {"type":"function_call_output","call_id":"call_1","output":[
              {"type":"input_text","text":"screen captured"},
              {"type":"input_image","image_url":"data:image/png;base64,AAAA","detail":"low"}
            ]}
          ]
        }
        """;

        var result = LocalProtocolCompatibilityEndpoints.NormalizeResponsesRequest(json);

        Assert.Null(result.Error);
        JsonArray messages = result.Request!["messages"]!.AsArray();
        Assert.Equal("assistant", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", messages[0]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", messages[1]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("image_url", messages[1]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,AAAA", messages[1]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("low", messages[1]!["content"]![1]!["image_url"]!["detail"]!.GetValue<string>());
    }

    [Fact]
    public void Responses_rejects_local_file_image_references()
    {
        string json = """
        {
          "input":[{"type":"message","role":"user","content":[
            {"type":"input_image","image_url":"file:///C:/Users/me/secret.png"}
          ]}]
        }
        """;

        var result = LocalProtocolCompatibilityEndpoints.NormalizeResponsesRequest(json);

        Assert.Null(result.Request);
        Assert.Contains("local file", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Responses_chat_tool_calls_translate_back_to_function_call_items()
    {
        string chat = """
        {
          "choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{
            "id":"call_42","type":"function","function":{"name":"lookup","arguments":"{\"id\":42}"}
          }]}}],
          "usage":{"prompt_tokens":10,"completion_tokens":4}
        }
        """;

        JsonObject response = LocalProtocolCompatibilityEndpoints.TranslateChatAnswerToResponsesForTest(chat);

        Assert.Equal("function_call", response["output"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("call_42", response["output"]![0]!["call_id"]!.GetValue<string>());
        Assert.Equal("lookup", response["output"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("{\"id\":42}", response["output"]![0]!["arguments"]!.GetValue<string>());
    }

    [Fact]
    public void Unknown_previous_response_id_fails_explicitly()
    {
        var result = LocalProtocolCompatibilityEndpoints.NormalizeResponsesRequest(
            "{\"previous_response_id\":\"resp_missing\",\"input\":\"continue\"}");

        Assert.Null(result.Request);
        Assert.Contains("unknown", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Anthropic_messages_map_system_content_and_generation_options()
    {
        string json = """
        {
          "model":"parux-main",
          "system":[{"type":"text","text":"be concise"}],
          "messages":[
            {"role":"user","content":[{"type":"text","text":"hello"}]},
            {"role":"assistant","content":"prior"},
            {"role":"user","content":"again"}
          ],
          "max_tokens":222,
          "temperature":0.2,
          "stop_sequences":["STOP"],
          "stream":true
        }
        """;

        var result = LocalProtocolCompatibilityEndpoints.NormalizeAnthropicRequest(json);

        Assert.Null(result.Error);
        Assert.True(result.Stream);
        Assert.Equal("parux-main", result.Model);
        Assert.NotNull(result.Request);
        Assert.False(result.Request!["stream"]!.GetValue<bool>());
        Assert.Equal(222, result.Request["max_tokens"]!.GetValue<int>());
        Assert.Equal(0.2, result.Request["temperature"]!.GetValue<double>());
        JsonArray messages = result.Request["messages"]!.AsArray();
        Assert.Equal(4, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("be concise", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("again", messages[3]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_tools_tool_use_and_image_tool_results_map_to_chat_runtime_shape()
    {
        string json = """
        {
          "model":"parux-main",
          "tools":[{"name":"inspect_screen","description":"Inspect image","input_schema":{"type":"object"}}],
          "tool_choice":{"type":"tool","name":"inspect_screen","disable_parallel_tool_use":true},
          "messages":[
            {"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"inspect_screen","input":{}}]},
            {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":[
              {"type":"text","text":"captured"},
              {"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"AAAA"}}
            ]}]}
          ]
        }
        """;

        var result = LocalProtocolCompatibilityEndpoints.NormalizeAnthropicRequest(json);

        Assert.Null(result.Error);
        Assert.Equal("inspect_screen", result.Request!["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.False(result.Request["parallel_tool_calls"]!.GetValue<bool>());
        JsonArray messages = result.Request["messages"]!.AsArray();
        Assert.Equal("assistant", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("toolu_1", messages[0]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("image_url", messages[1]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,AAAA", messages[1]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Chat_tool_calls_translate_back_to_anthropic_tool_use()
    {
        string chat = """
        {"choices":[{"message":{"role":"assistant","content":"I'll inspect it.","tool_calls":[{
          "id":"toolu_9","type":"function","function":{"name":"inspect","arguments":"{\"region\":\"header\"}"}
        }]}}]}
        """;

        JsonObject message = LocalProtocolCompatibilityEndpoints.TranslateChatAnswerToAnthropicForTest(chat);

        Assert.Equal("tool_use", message["stop_reason"]!.GetValue<string>());
        Assert.Equal("text", message["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("tool_use", message["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("header", message["content"]![1]!["input"]!["region"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[]}")]
    public void Anthropic_invalid_shapes_fail_closed(string json)
    {
        var result = LocalProtocolCompatibilityEndpoints.NormalizeAnthropicRequest(json);
        Assert.Null(result.Request);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Theory]
    [InlineData("{\"input\":\"hello\",\"vendor_extension\":true}", "Responses request field")]
    [InlineData("{\"input\":\"hello\",\"stream\":\"false\"}", "stream must be a boolean")]
    // "developer" (OpenAI's system role) is accepted since Codex sends it; an invented role is not.
    [InlineData("{\"input\":[{\"type\":\"message\",\"role\":\"moderator\",\"content\":\"x\"}]}", "Unsupported message role")]
    // Replayed "reasoning" items are skipped (Codex); an unknown item type is still rejected.
    [InlineData("{\"input\":[{\"type\":\"computer_call\",\"summary\":[]}]}", "Unsupported Responses input item type")]
    [InlineData("{\"input\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"no\"}]}]}", "Unsupported content block type")]
    [InlineData("{\"input\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"input_image\",\"image_url\":\"https://example.test/image.png\"}]}]}", "only supported in user")]
    [InlineData("{\"input\":[{\"type\":\"message\",\"content\":[{\"type\":\"input_text\",\"text\":\"a\",\"content\":\"b\"}]}]}", "cannot supply both text and content")]
    [InlineData("{\"input\":[{\"type\":\"message\",\"content\":[{\"type\":\"input_image\",\"image_url\":\"https://example.test/image.png\",\"detail\":42}]}]}", "image detail must be a string")]
    [InlineData("{\"input\":\"x\",\"tools\":[{\"type\":\"function\",\"name\":\"weather\",\"parameters\":{},\"vendor_extension\":true}]}", "Responses tool field")]
    public void Responses_rejects_unsupported_or_malformed_fields_without_dropping_them(string json, string expectedError)
    {
        var result = LocalProtocolCompatibilityEndpoints.NormalizeResponsesRequest(json);
        Assert.Null(result.Request);
        Assert.Contains(expectedError, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"vendor_extension\":true}", "Anthropic request field")]
    [InlineData("{\"model\":\"parux-main\",\"stream\":\"false\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}", "stream must be a boolean")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"developer\",\"content\":\"x\"}]}", "message role must be user, assistant or system")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"web_search_tool_result\",\"content\":[]}] }]}", "Unsupported Anthropic user content block type")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"tools\":[{\"name\":\"weather\",\"input_schema\":{},\"vendor_extension\":true}]}", "Anthropic tool field")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"tool_choice\":{\"type\":\"auto\",\"name\":\"weather\"}}", "name is valid only when type is tool")]
    [InlineData("{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"tool_choice\":{\"type\":12}}", "type must be a string")]
    public void Anthropic_rejects_unsupported_or_malformed_fields_without_dropping_them(string json, string expectedError)
    {
        var result = LocalProtocolCompatibilityEndpoints.NormalizeAnthropicRequest(json);
        Assert.Null(result.Request);
        Assert.Contains(expectedError, result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
