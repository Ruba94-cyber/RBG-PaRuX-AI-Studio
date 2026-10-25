using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingRenderedPromptCountContractTests
{
    private static CodingRenderedPromptCountRequest Valid() => new()
    {
        SystemPrompt = "You are a coding agent.",
        UserPrompt = "Fix the bug; return JSON.",
        SchemaName = "parux_coding_action",
        JsonSchema = """{"type":"object","properties":{"action":{"type":"string"}},"required":["action"]}"""
    };

    [Fact]
    public void Builds_same_json_schema_chat_shape_as_direct_coding_role()
    {
        string body = CodingRenderedPromptCountContract.BuildProviderPayload(Valid());
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;

        Assert.Equal("parux-coding", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        JsonElement messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        JsonElement format = root.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("parux_coding_action",
            format.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.Equal("object",
            format.GetProperty("json_schema").GetProperty("schema")
                .GetProperty("type").GetString());
    }

    [Fact]
    public void Invalid_schema_and_oversized_prompt_are_not_sent_to_runtime()
    {
        CodingRenderedPromptCountRequest request = Valid();
        request.UserPrompt = new string('a', 250_001);
        Assert.False(CodingRenderedPromptCountContract.IsValid(request));

        request = Valid();
        request.SchemaName = "bad schema name";
        Assert.False(CodingRenderedPromptCountContract.IsValid(request));

        request = Valid();
        request.JsonSchema = "{}x";
        Assert.Throws<JsonException>(() =>
            CodingRenderedPromptCountContract.BuildProviderPayload(request));
    }

    [Theory]
    [InlineData(1_200, 8_192, 4_864, true)]
    [InlineData(3_329, 8_192, 4_864, false)]
    [InlineData(3_328, 8_192, 4_864, true)]
    [InlineData(0, 32_768, 4_864, false)]
    [InlineData(1_200, 0, 4_864, false)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, false)]
    public void Exact_token_admission_preserves_full_generation_reserve_and_avoids_overflow(
        int renderedTokens, int availableTokens, int reserve, bool expected)
    {
        Assert.Equal(expected,
            PaRuX.Services.CodingExactPromptAdmissionPolicy.Fits(
                renderedTokens, availableTokens, reserve));
    }
}
