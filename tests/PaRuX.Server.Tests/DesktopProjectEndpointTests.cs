using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Regression cover for a client/server contract drift that no service-level test could catch:
/// the desktop app and the CLI both post to /api/v1/projects/desktop - the loopback path
/// CreateDesktopProjectRequest documents, which carries the chosen coding permission profile -
/// but the route was never mapped. The path still matched the GET /api/v1/projects/{projectId}
/// template, so both clients got a bare 405 and creating a Project was impossible from either.
///
/// The assertions deliberately drive an invalid goal: CreateProjectAsync rejects a blank goal
/// before it plans anything, so these tests prove the route is mapped and reaches the right
/// handler without needing a reachable local model.
/// </summary>
public sealed class DesktopProjectEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public DesktopProjectEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private HttpClient CreateAuthenticatedClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    [Fact]
    public async Task Desktop_project_creation_route_is_mapped_and_not_method_not_allowed()
    {
        using HttpClient client = CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/projects/desktop",
            new CreateDesktopProjectRequest
            {
                Goal = string.Empty,
                CodingPermissionProfile = CodingTaskPermissionProfile.PackageFetch
            });

        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Plain_project_creation_route_still_answers_its_own_contract()
    {
        using HttpClient client = CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/projects", new CreateProjectRequest { Goal = string.Empty });

        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
