using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// This class exercises the real DI-wired PersonaService singleton through the full HTTP pipeline
/// (via ApiSecurityTests.PaRuXFactory), which still points at the real, fixed
/// ServerState.StateDirectory\personas.json - not a per-test isolated directory. Scoped to what's
/// genuinely safe against that shared real state: reading the built-in list (never written to
/// disk), plus request validation that fails before Create/Delete/SetActive ever calls Save().
/// A directory-injectable PersonaService(string stateDirectory) constructor now exists (added for
/// the 2026-08-27 Backup/Restore re-audit - see BackupServiceTests, which constructs its own
/// isolated instance directly rather than going through this DI-wired one).
/// </summary>
public sealed class PersonaTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public PersonaTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_personas_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/personas");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_personas_endpoint_returns_the_built_in_default_persona()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/personas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PersonaListResponse? body = await response.Content.ReadFromJsonAsync<PersonaListResponse>();
        Assert.NotNull(body);
        Assert.Contains(body!.Personas, p => p.Id == "default" && p.IsBuiltIn);
        Assert.NotEmpty(body.ActivePersonaId);
    }

    [Fact]
    public async Task Create_persona_endpoint_rejects_an_empty_name()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/personas",
            new CreatePersonaRequest { Name = "  ", SystemPrompt = "Be dramatic." });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_persona_endpoint_rejects_an_empty_system_prompt()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/personas",
            new CreatePersonaRequest { Name = "Test Persona", SystemPrompt = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_persona_endpoint_rejects_a_built_in_id()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/personas/default");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Set_active_persona_endpoint_rejects_an_unknown_id()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/personas/active",
            new SetActivePersonaRequest { PersonaId = "parux-test-unknown-" + Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
