using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Endpoint boundary tests that deliberately never load a model. Real tokenizer
/// parity and model routing still require a managed llama runtime acceptance run.
/// </summary>
public sealed class CodingPromptTokenCountEndpointTests
    : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public CodingPromptTokenCountEndpointTests(ApiSecurityTests.PaRuXFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task Anonymous_caller_cannot_count_private_coding_prompts()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/coding/prompt-token-count", Sample());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_prompt_is_rejected_before_any_model_is_loaded()
    {
        using HttpClient client = OwnerClient();
        CodingRenderedPromptCountRequest request = Sample();
        request.UserPrompt = new string('x', 250_001);
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/coding/prompt-token-count", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_schema_name_is_rejected_without_model_side_effects()
    {
        using HttpClient client = OwnerClient();
        CodingRenderedPromptCountRequest request = Sample();
        request.SchemaName = "../delete-model";
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/coding/prompt-token-count", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpClient OwnerClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    private static CodingRenderedPromptCountRequest Sample() => new()
    {
        SystemPrompt = "Return one action",
        UserPrompt = "Read file",
        SchemaName = "parux_coding_action",
        JsonSchema = """{"type":"object"}"""
    };
}
