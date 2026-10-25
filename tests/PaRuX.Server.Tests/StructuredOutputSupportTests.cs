using System.Reflection;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class StructuredOutputSupportTests
{
    [Fact]
    public void Managed_llama_engines_advertise_structured_output_but_exllama_does_not()
    {
        var registry = new LocalInferenceEngineRegistry();
        LocalInferenceEngineDescriptor cuda = registry.FindDescriptor(LocalInferenceEngineRegistry.LlamaCppCudaId)!;
        LocalInferenceEngineDescriptor vulkan = registry.FindDescriptor(LocalInferenceEngineRegistry.LlamaCppVulkanId)!;
        LocalInferenceEngineDescriptor exllama = registry.FindDescriptor(LocalInferenceEngineRegistry.ExLlamaV3Id)!;
        Assert.True(cuda.Capabilities.HasFlag(LocalInferenceCapabilities.StructuredOutput));
        Assert.True(vulkan.Capabilities.HasFlag(LocalInferenceCapabilities.StructuredOutput));
        Assert.False(exllama.Capabilities.HasFlag(LocalInferenceCapabilities.StructuredOutput));
    }

    [Fact]
    public void Structured_output_requirement_blocks_an_incompatible_auto_engine_without_touching_manual_semantics()
    {
        Assert.False(LocalInferenceEngineCoordinator.IsTurnEligibleForAlternative(false, false, false, false, true, false, @"C:\models\model.gguf"));
        Assert.True(LocalInferenceEngineCoordinator.IsTurnEligibleForAlternative(false, false, false, false, false, false, @"C:\models\model.gguf"));
        Assert.False(LocalInferenceEngineCoordinator.IsTurnEligibleForAlternative(true, false, false, false, false, false, @"C:\models\model.gguf"));
    }

    [Fact]
    public void Exllama_rejects_structured_output_instead_of_silently_ignoring_the_schema()
    {
        var request = new LocalInferenceRequest(
            "variant",
            new List<(string Role, string Content)> { ("user", "hello") },
            128,
            0.2,
            Stream: true,
            StructuredOutput: new LocalStructuredOutputRequest("answer", "{\"type\":\"object\"}"));
        MethodInfo validate = typeof(ExLlamaV3LocalInferenceEngine).GetMethod(
            "ValidateRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
        TargetInvocationException error = Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, new object[] { request }));
        Assert.IsType<NotSupportedException>(error.InnerException);
        Assert.Contains("silently ignore", error.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Engine_neutral_schema_maps_to_openai_compatible_response_format_only_at_provider_boundary()
    {
        var request = new LocalStructuredOutputRequest(
            "agent_action",
            "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\"}},\"required\":[\"action\"],\"additionalProperties\":false}");
        JsonObject mapped = StructuredOutputJson.BuildOpenAiResponseFormat(request);
        Assert.Equal("json_schema", mapped["type"]!.GetValue<string>());
        Assert.Equal("agent_action", mapped["json_schema"]!["name"]!.GetValue<string>());
        Assert.True(mapped["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.Equal("object", mapped["json_schema"]!["schema"]!["type"]!.GetValue<string>());
        Assert.Equal("string", mapped["json_schema"]!["schema"]!["properties"]!["action"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Backward_compatible_chat_transport_round_trips_and_can_be_removed_before_fallback()
    {
        var chat = new ChatCompletionRequest();
        var expected = new LocalStructuredOutputRequest(
            "binary_action",
            "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\"}},\"required\":[\"action\"]}",
            Strict: true);
        StructuredOutputRequestTransport.Set(chat, expected);
        LocalStructuredOutputRequest? decoded = StructuredOutputRequestTransport.TryRead(chat);
        Assert.NotNull(decoded);
        Assert.Equal(expected.Name, decoded!.Name);
        Assert.Equal(expected.JsonSchema, decoded.JsonSchema);
        Assert.True(decoded.Strict);
        Assert.True(chat.SkillParameters.ContainsKey(StructuredOutputTransportKeys.JsonSchema));
        StructuredOutputRequestTransport.RemoveReserved(chat.SkillParameters);
        Assert.Null(StructuredOutputRequestTransport.TryRead(chat));
        Assert.False(chat.SkillParameters.Keys.Any(StructuredOutputTransportKeys.IsReserved));
    }

    [Theory]
    [InlineData("bad name", "{\"type\":\"object\"}")]
    [InlineData("ok", "not-json")]
    [InlineData("ok", "[]")]
    public void Invalid_structured_output_contracts_fail_closed(string name, string schema)
    {
        var request = new LocalStructuredOutputRequest(name, schema);
        Assert.Throws<InvalidDataException>(() => request.Validate());
    }

    [Fact]
    public async Task Default_completion_client_path_falls_back_without_pretending_schema_was_applied()
    {
        ILocalCompletionClient client = new PlainCompletionClient();
        var schema = new LocalStructuredOutputRequest("verdict", "{\"type\":\"object\"}");
        CompletionResult result = await client.CompleteStructuredAsync("system", "user", schema, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("PASS - legacy parser path", result.Content);
        Assert.False(result.StructuredOutputApplied);
    }

    [Fact]
    public void Verifier_accepts_schema_constrained_json_and_renders_a_bounded_legacy_compatible_verdict()
    {
        bool parsed = VerifierService.TryParseStructuredVerdict(
            "{\"verdict\":\"FAIL\",\"reason\":\"missing concrete evidence\"}",
            out bool pass,
            out bool fail,
            out string rendered);
        Assert.True(parsed);
        Assert.False(pass);
        Assert.True(fail);
        Assert.Equal("FAIL - missing concrete evidence", rendered);
    }

    [Fact]
    public void Structured_verifier_never_treats_arbitrary_json_as_pass()
    {
        bool parsed = VerifierService.TryParseStructuredVerdict(
            "{\"status\":\"looks good\"}", out bool pass, out bool fail, out _);
        Assert.False(parsed);
        Assert.False(pass);
        Assert.False(fail);
    }

    private sealed class PlainCompletionClient : ILocalCompletionClient
    {
        public Task<CompletionResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken) =>
            Task.FromResult(CompletionResult.Ok("PASS - legacy parser path"));
    }
}
