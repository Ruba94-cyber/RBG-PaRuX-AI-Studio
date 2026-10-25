using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// This class exercises the real DI-wired McpClientRegistry singleton through the full HTTP
/// pipeline (via ApiSecurityTests.PaRuXFactory), which still points at the real, fixed
/// ServerState.StateDirectory\mcp-clients.json - not a per-test isolated directory. Scoped to
/// what's genuinely safe against that shared real state: request validation that fails before
/// Save() is ever called, plus the real (empty, in a fresh test run) server list. Actually
/// launching a real MCP server process is deliberately not exercised here - that's a live,
/// external-process integration verified separately, not something a hermetic unit test run
/// should be spawning processes for. A directory-injectable McpClientRegistry(string
/// stateDirectory) constructor now exists (added for the 2026-08-27 Backup/Restore re-audit -
/// see BackupServiceTests, which constructs its own isolated instance directly rather than going
/// through this DI-wired one).
/// </summary>
public sealed class McpClientTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public McpClientTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_mcp_servers_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/mcp/servers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Add_mcp_server_endpoint_rejects_an_empty_name()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/mcp/servers",
            new AddMcpServerRequest { Name = "  ", Command = "npx", Args = "-y something" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Add_mcp_server_endpoint_rejects_an_empty_command()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/mcp/servers",
            new AddMcpServerRequest { Name = "Test Server", Command = "", Args = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Set_enabled_endpoint_rejects_an_unknown_id()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/mcp/servers/parux-test-unknown-" + Guid.NewGuid().ToString("N") + "/enabled",
            new SetMcpServerEnabledRequest { Enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
