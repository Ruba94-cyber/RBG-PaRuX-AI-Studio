using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OpenCodeSessionEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public OpenCodeSessionEndpointTests(ApiSecurityTests.PaRuXFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task Unauthenticated_call_cannot_observe_opencode_sessions()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/agent-backends/opencode/sessions/ses_123/observation");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Owner_authentication_does_not_imply_session_authority()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/agent-backends/opencode/sessions/..%2Fother/observation");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
