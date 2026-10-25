using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// LocalAiPaths.SetRoleOverride/ClearRoleOverride write to a real, fixed
/// %LOCALAPPDATA%\PaRuX\model-role-overrides.json - there is no constructor-injected root the
/// way BackupServiceTests gets for the client-owned files it touches, since LocalAiPaths is a
/// fully static class. Rather than have an automated test run write into the real developer's
/// LocalAppData (exactly the hazard BackupServiceTests' clientRoot override exists to avoid), this
/// suite is scoped to what's genuinely safe in a hermetic run: request validation that fails
/// before SetRoleOverride is ever called. The success path (a real file actually getting assigned
/// to a role, and the model then loading) is covered by live verification instead - same trade-off
/// this codebase already makes for LocalAiPaths.SetRoot, which has no automated coverage either.
/// </summary>
public sealed class ModelRolesTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public ModelRolesTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_roles_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/models/roles");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_roles_endpoint_returns_standard_and_uncensored()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/models/roles");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<ModelRoleStatus>? roles = await response.Content.ReadFromJsonAsync<List<ModelRoleStatus>>();
        Assert.NotNull(roles);
        Assert.Contains(roles!, r => r.Role == "standard");
        Assert.Contains(roles!, r => r.Role == "uncensored");
    }

    [Fact]
    public async Task Set_role_endpoint_rejects_an_unknown_role_name()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/roles/bogus-role",
            new SetModelRoleRequest { ModelPath = "C:\\does-not-matter.gguf" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("reasoning")]
    [InlineData("planner")]
    [InlineData("coding")]
    [InlineData("reviewer")]
    [InlineData("vision")]
    [InlineData("main")] // alias for "standard" - see LocalAiPaths.NormalizeTextRole
    public async Task Set_role_endpoint_accepts_every_generalized_role_name_for_validation(string role)
    {
        // Reaches SetRoleOverride's own File.Exists check and fails there (BadRequest, same as a
        // missing path for "standard") - proves the role-name whitelist itself now accepts these
        // names, without ever writing to the real model-role-overrides.json (no existing file).
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        string missingPath = Path.Combine(Path.GetTempPath(), "parux-role-test-missing-" + Guid.NewGuid().ToString("N") + ".gguf");
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/roles/" + role,
            new SetModelRoleRequest { ModelPath = missingPath });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Unknown role", body);
    }

    [Fact]
    public async Task Get_roles_endpoint_returns_the_full_generalized_role_catalog()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/models/roles");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<ModelRoleStatus>? roles = await response.Content.ReadFromJsonAsync<List<ModelRoleStatus>>();
        Assert.NotNull(roles);

        foreach (string role in new[] { "standard", "uncensored", "fast", "reasoning", "planner", "coding", "reviewer", "vision" })
            Assert.Contains(roles!, r => r.Role == role && r.Kind == "text");
        foreach (string role in new[] { "image", "image_edit", "video" })
            Assert.Contains(roles!, r => r.Role == role && r.Kind == "media");
        foreach (string role in new[] { "3d", "stt", "tts", "embedding", "reranker" })
            Assert.Contains(roles!, r => r.Role == role && r.Kind == "fixed-engine" && !string.IsNullOrEmpty(r.Note));
        // RERANKER closed 2026-08-27 (RerankerService) - ModelRoleCatalog.NotImplementedRoles is
        // now empty, so there is nothing left to assert here; kept as an explicit empty check
        // (not just deleted) so a future genuinely-unimplemented role has a place to land.
        Assert.Empty(ModelRoleCatalog.NotImplementedRoles);
    }

    [Fact]
    public async Task Set_role_endpoint_rejects_a_path_that_does_not_exist()
    {
        // Never reaches SetRoleOverride's file write: File.Exists fails first for a path this
        // guaranteed not to exist, so this is safe against a hermetic test run's real LocalAppData.
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        string missingPath = Path.Combine(Path.GetTempPath(), "parux-role-test-missing-" + Guid.NewGuid().ToString("N") + ".gguf");
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/roles/standard",
            new SetModelRoleRequest { ModelPath = missingPath });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Set_role_endpoint_rejects_a_non_gguf_file()
    {
        // A real, existing file - but not .gguf - to reach and verify the extension check
        // specifically, without ever calling File.WriteAllText on the real overrides file.
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        string notGguf = Path.Combine(Path.GetTempPath(), "parux-role-test-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(notGguf, "not a model");
        try
        {
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/roles/standard",
                new SetModelRoleRequest { ModelPath = notGguf });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            try { File.Delete(notGguf); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Reset_role_endpoint_rejects_an_unknown_role_name()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsync("/api/v1/models/roles/bogus-role/reset", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // 2026-08-27, LoRA-at-inference: the adapter-assignment endpoints reach AdapterStore.Get and,
    // on success, LocalAiPaths.SetRoleAdapter - a real shared %LOCALAPPDATA% write, same hazard
    // this whole file already documents for SetRoleOverride. Scoped to what's genuinely safe: auth,
    // and an id a real adapter registry will essentially never contain (a fresh random guid), which
    // returns 400 before AdapterStore or LocalAiPaths is ever touched with real data.

    [Fact]
    public async Task Set_role_adapter_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/roles/fast/adapter",
            new SetRoleAdapterRequest { AdapterId = "anything" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Clear_role_adapter_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.DeleteAsync("/api/v1/models/roles/fast/adapter");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Set_role_adapter_endpoint_rejects_an_unknown_adapter_id_before_any_write()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/roles/fast/adapter",
            new SetRoleAdapterRequest { AdapterId = "parux-test-unknown-" + Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Clear_role_adapter_endpoint_rejects_an_unknown_role_name()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/models/roles/bogus-role/adapter");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
