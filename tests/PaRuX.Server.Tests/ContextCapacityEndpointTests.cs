using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The input guards on GET /api/v1/models/context-capacity. The capacity computation itself is
/// covered by ContextCapacityPlannerTests against known geometry; what matters here is that the
/// endpoint refuses ambiguous or impossible requests with a specific reason instead of answering
/// with a number the caller would then act on.
///
/// Scoped to the paths that need no real model file, for the same reason ModelRolesTests is: this
/// suite must never read or write the developer's actual model library.
/// </summary>
public sealed class ContextCapacityEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public ContextCapacityEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private HttpClient Authenticated()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    [Fact]
    public async Task Requires_a_token_like_every_other_model_endpoint()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/models/context-capacity?path=C:/x.gguf");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Asking_without_a_path_or_a_role_is_refused_with_a_reason()
    {
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.GetAsync("/api/v1/models/context-capacity");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("either", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_role_is_refused_rather_than_silently_treated_as_unassigned()
    {
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.GetAsync("/api/v1/models/context-capacity?role=not-a-real-role");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_path_that_does_not_exist_is_reported_as_missing_not_as_zero_capacity()
    {
        // Answering "0 tokens" for a file that is simply absent would read as "this model cannot
        // hold a conversation", which is a different and misleading statement.
        using HttpClient client = Authenticated();
        string missing = Path.Combine(Path.GetTempPath(), "parux-does-not-exist-" + Guid.NewGuid().ToString("N") + ".gguf");
        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/models/context-capacity?path=" + Uri.EscapeDataString(missing));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
