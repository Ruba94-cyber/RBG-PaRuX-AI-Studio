using System.Text.Json.Nodes;
using PaRuX.Server;
using PaRuX.Server.Gateway;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Universal Gateway: virtual model ids and real agent-client request shapes.</summary>
public sealed class GatewayCompatibilityTests
{
    [Theory]
    [InlineData("parux-auto", null, true)]
    [InlineData("parux-code", "coding", false)]
    [InlineData("parux-reasoning", "reasoning", false)]
    [InlineData("parux-fast", "fast", false)]
    [InlineData("parux-vision", "vision", false)]
    [InlineData("parux-main", "standard", false)]
    [InlineData("parux-coding", "coding", false)]
    [InlineData("coding", "coding", false)]
    public void Virtual_and_role_ids_resolve_identically_for_every_adapter(string id, string? role, bool automatic)
    {
        GatewayModelAliases.Resolution? resolution = GatewayModelAliases.Resolve(id);

        Assert.NotNull(resolution);
        Assert.Equal(role, resolution!.Role);
        Assert.Equal(automatic, resolution.Automatic);
    }

    [Theory]
    [InlineData("claude-sonnet-4-5")]
    [InlineData("claude-3-5-haiku-latest")]
    [InlineData("gpt-5-codex")]
    [InlineData("o4-mini")]
    public void Vendor_ids_sent_by_agent_clients_route_to_the_configured_default(string id)
    {
        GatewayModelAliases.Resolution? resolution = GatewayModelAliases.Resolve(id, GatewayModelAliases.Code);

        Assert.NotNull(resolution);
        Assert.Equal("coding", resolution!.Role);
        Assert.StartsWith("vendor-default:", resolution.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_ids_are_rejected()
    {
        Assert.Null(GatewayModelAliases.Resolve("totally-unknown-model"));
    }

    [Fact]
    public void Auto_routes_tool_using_agent_requests_to_the_coding_model_and_chat_to_the_router()
    {
        var agent = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "fix the bug" }),
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "function" })
        };
        var chat = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "what is the capital of Italy?" })
        };

        Assert.Equal("coding", GatewayModelAliases.AutoRouteRequest(agent).Role);
        var chatRoute = GatewayModelAliases.AutoRouteRequest(chat);
        Assert.Null(chatRoute.Role);
        Assert.Equal("what is the capital of Italy?", chatRoute.PromptText);
    }

    [Fact]
    public void Claude_code_request_fields_are_accepted()
    {
        // Shape of a real Claude Code turn: system array with cache_control, metadata, thinking, top_k.
        string json = """
        {
          "model":"claude-sonnet-4-5","max_tokens":8192,"stream":true,"temperature":1,"top_k":40,
          "metadata":{"user_id":"u"},
          "thinking":{"type":"enabled","budget_tokens":1024},
          "system":[{"type":"text","text":"You are Claude Code.","cache_control":{"type":"ephemeral"}}],
          "messages":[{"role":"user","content":[{"type":"text","text":"hi","cache_control":{"type":"ephemeral"}}]}],
          "tools":[{"name":"Write","description":"Write a file","input_schema":{"type":"object","properties":{"file_path":{"type":"string"}}}}]
        }
        """;

        var result = LocalProtocolCompatibilityEndpoints.NormalizeAnthropicRequest(json);

        Assert.Null(result.Error);
        Assert.True(result.Stream);
        Assert.Equal("system", result.Request!["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void Codex_responses_request_fields_are_accepted()
    {
        string json = """
        {
          "model":"parux-auto","instructions":"You are Codex.","stream":true,"store":false,
          "include":["reasoning.encrypted_content"],"prompt_cache_key":"abc",
          "reasoning":{"effort":"medium","summary":"auto"},"text":{"verbosity":"medium"},
          "parallel_tool_calls":false,
          "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"list files"}]}],
          "tools":[{"type":"function","name":"shell","description":"run","strict":false,"parameters":{"type":"object","properties":{}}}]
        }
        """;

        var result = LocalProtocolCompatibilityEndpoints.NormalizeResponsesRequest(json);

        Assert.Null(result.Error);
        Assert.NotNull(result.Request!["tools"]);
    }

    [Theory]
    [InlineData("/v1/messages", true)]
    [InlineData("/v1/responses", true)]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/v1/models", true)]
    [InlineData("/health", true)]
    [InlineData("/api/v1/health", false)]
    [InlineData("/api/v1/apikeys", false)]
    [InlineData("/api/tags", false)]
    [InlineData("/api/chat", false)]
    [InlineData("/", false)]
    public void Gateway_port_serves_only_model_apis(string path, bool allowed)
    {
        Assert.Equal(allowed, GatewaySettings.IsGatewayPath(new Microsoft.AspNetCore.Http.PathString(path)));
    }

    [Fact]
    public void Gateway_settings_normalize_unsafe_values_and_round_trip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-gw-" + Guid.NewGuid().ToString("N"));
        try
        {
            new GatewaySettings { Port = 51123, VendorModelDefault = "gpt-4" }.Save(dir);
            GatewaySettings loaded = GatewaySettings.Load(dir);
            Assert.Equal(GatewaySettings.DefaultPort, loaded.Port);           // never the Core port
            Assert.Equal(GatewayModelAliases.Auto, loaded.VendorModelDefault); // never a vendor id loop

            new GatewaySettings { Port = 12000, VendorModelDefault = GatewayModelAliases.Code }.Save(dir);
            loaded = GatewaySettings.Load(dir);
            Assert.Equal(12000, loaded.Port);
            Assert.Equal(GatewayModelAliases.Code, loaded.VendorModelDefault);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- Regressions found with the real clients (Claude Code 2.1.283, Codex 0.158, OpenCode 1.18) ----

    [Fact]
    public void Claude_code_output_config_and_mid_conversation_system_turns_are_accepted()
    {
        string json = """
        {
          "model":"parux-auto","max_tokens":1024,"output_config":{"effort":"high"},
          "thinking":{"type":"adaptive","display":"omitted"},"context_management":{"edits":[]},
          "system":[{"type":"text","text":"rules"}],
          "messages":[
            {"role":"system","content":"early reminder"},
            {"role":"user","content":"hello"},
            {"role":"assistant","content":"hi"},
            {"role":"system","content":"late reminder"},
            {"role":"user","content":"continue"}
          ]
        }
        """;

        var result = LocalProtocolCompatibilityEndpoints.NormalizeAnthropicRequest(json);

        Assert.Null(result.Error);
        JsonArray messages = result.Request!["messages"]!.AsArray();
        Assert.Single(messages, m => m!["role"]!.GetValue<string>() == "system");
        Assert.Contains("early reminder", messages[0]!["content"]!.GetValue<string>());
        Assert.Contains(messages, m => m!["role"]!.GetValue<string>() == "user"
            && m["content"]!.ToJsonString().Contains("late reminder"));
    }

    [Fact]
    public void Codex_developer_role_reasoning_items_and_hosted_tools_are_handled()
    {
        string json = """
        {
          "model":"parux-auto","stream":true,"client_metadata":{"x":"y"},
          "input":[
            {"type":"message","role":"developer","content":[{"type":"input_text","text":"dev rules"}]},
            {"type":"message","role":"user","content":[{"type":"input_text","text":"do it"}]},
            {"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"opaque"},
            {"type":"function_call","call_id":"c1","name":"exec_command","arguments":"{}"},
            {"type":"function_call_output","call_id":"c1","output":"ok"}
          ],
          "tools":[
            {"type":"function","name":"exec_command","parameters":{"type":"object","properties":{}}},
            {"type":"namespace","name":"multi_agent_v1","tools":[]},
            {"type":"web_search"}
          ]
        }
        """;

        var result = LocalProtocolCompatibilityEndpoints.NormalizeResponsesRequest(json);

        Assert.Null(result.Error);
        JsonArray messages = result.Request!["messages"]!.AsArray();
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Contains("dev rules", messages[0]!["content"]!.ToJsonString());
        // Only the local function tool is offered; hosted/namespace tools are not faked.
        Assert.Single(result.Request!["tools"]!.AsArray());
    }

    [Fact]
    public void Responses_usage_uses_input_output_total_tokens()
    {
        // Codex rejected response.completed whose usage used chat field names.
        JsonObject usage = LocalProtocolCompatibilityEndpoints.BuildResponsesUsage(
            JsonNode.Parse("{\"prompt_tokens\":120,\"completion_tokens\":30,\"total_tokens\":150}"));

        Assert.Equal(120, usage["input_tokens"]!.GetValue<int>());
        Assert.Equal(30, usage["output_tokens"]!.GetValue<int>());
        Assert.Equal(150, usage["total_tokens"]!.GetValue<int>());
        Assert.NotNull(usage["input_tokens_details"]);
        Assert.NotNull(usage["output_tokens_details"]);
        Assert.Equal(0, LocalProtocolCompatibilityEndpoints.BuildResponsesUsage(null)["input_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Long_sessions_keep_the_system_prompt_and_start_at_a_user_turn()
    {
        var messages = new List<JsonObject> { new() { ["role"] = "system", ["content"] = "rules" } };
        for (int i = 0; i < 100; i++)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "u" + i });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "a" + i });
            messages.Add(new JsonObject { ["role"] = "tool", ["content"] = "t" + i });
        }

        List<JsonObject> trimmed = LocalProtocolCompatibilityEndpoints.TrimConversation(messages, 128);

        Assert.True(trimmed.Count <= 128);
        Assert.Equal("system", trimmed[0]["role"]!.GetValue<string>());
        Assert.Equal("user", trimmed[1]["role"]!.GetValue<string>());
        Assert.Equal("t99", trimmed[^1]["content"]!.GetValue<string>());
    }
}
