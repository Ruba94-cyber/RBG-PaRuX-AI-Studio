using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>vNext Phase 29 - the unified Skill/Plugin/MCP registry view. Real API-level tests
/// (matching PersonaTests/other endpoint tests' convention) since this is a pure read-model over
/// the real SkillRegistryService/McpClientRegistry - no new store, nothing hermetic to isolate.</summary>
public sealed class UnifiedRegistryTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public UnifiedRegistryTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Capabilities_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/registry/capabilities");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Capabilities_endpoint_includes_the_real_built_in_skill_catalog_as_provider_type_skill()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/registry/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        UnifiedCapabilityListResponse? body = await response.Content.ReadFromJsonAsync<UnifiedCapabilityListResponse>();
        Assert.NotNull(body);

        List<UnifiedCapabilityDto> skillCapabilities = body!.Capabilities.Where(c => c.ProviderType == "skill").ToList();
        Assert.NotEmpty(skillCapabilities); // the real built-in catalog, same one SkillRegistryLoadsTheBuiltInCatalog already proves is non-empty
        Assert.All(skillCapabilities, c => Assert.StartsWith("skill:", c.Id));
        Assert.All(skillCapabilities, c => Assert.True(c.Reachable)); // skills have no separate connection concept
        // TechnicalDetailsJson should be real, parseable JSON of the underlying SkillManifest, not a placeholder.
        Assert.All(skillCapabilities, c => Assert.Contains("\"id\"", c.TechnicalDetailsJson));
    }

    /// <summary>McpClientRegistry reads from a real, fixed config path (no test-injectable root -
    /// same known trade-off as PersonaService, see PersonaTests), so this environment's real MCP
    /// config is whatever is actually persisted on this machine, not a hermetic empty state - a
    /// first version of this test wrongly assumed zero configured servers and failed against a
    /// real "Everything" MCP server already configured here. Fixed to verify structure instead of
    /// assuming absence, which is the actually-correct thing to assert about a real shared
    /// config file this test doesn't own.</summary>
    [Fact]
    public async Task Capabilities_endpoint_reports_any_real_configured_mcp_servers_with_the_correct_shape()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/registry/capabilities");
        UnifiedCapabilityListResponse body = (await response.Content.ReadFromJsonAsync<UnifiedCapabilityListResponse>())!;

        List<UnifiedCapabilityDto> mcpCapabilities = body.Capabilities.Where(c => c.ProviderType == "mcp").ToList();
        Assert.All(mcpCapabilities, c => Assert.StartsWith("mcp:", c.Id));
        Assert.All(mcpCapabilities, c => Assert.Equal("mcp", c.Source));
        Assert.All(mcpCapabilities, c => Assert.Contains("\"id\"", c.TechnicalDetailsJson));
    }
}
