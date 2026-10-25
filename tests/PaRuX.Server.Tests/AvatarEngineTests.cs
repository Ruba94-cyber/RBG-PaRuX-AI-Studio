using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// AvatarEngineService reads/writes a real, fixed %LOCALAPPDATA%\PaRuX\ai-runtimes\avatar\ with
/// no test-injectable root (same trade-off as EmbeddingService/ThreedService). GetStatusAsync is
/// a safe read-only check (file existence + a quick import probe if a venv happens to already
/// exist on the machine running these tests), so it's covered directly; install/generate are
/// real, slow, multi-GB operations deliberately not exercised here - matching this codebase's
/// established pattern of scoping hermetic tests to what never writes real state.
/// </summary>
public sealed class AvatarEngineTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public AvatarEngineTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_avatar_engine_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/avatar/engine");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_avatar_engine_endpoint_returns_a_status()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/avatar/engine");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AvatarEngineStatus? status = await response.Content.ReadFromJsonAsync<AvatarEngineStatus>();
        Assert.NotNull(status);
    }

    [Fact]
    public async Task Install_avatar_engine_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsync("/api/v1/avatar/engine/install", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Set_avatar_photo_endpoint_rejects_empty_photo()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/avatar/photo",
            new AvatarPhotoRequest { PhotoBase64 = "", FileName = "photo.jpg" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Set_avatar_photo_endpoint_rejects_invalid_base64()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/avatar/photo",
            new AvatarPhotoRequest { PhotoBase64 = "not-valid-base64!!!", FileName = "photo.jpg" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
