using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The mutating AI-backend routes are desktop-owned, and a request with no recorded remote address is never
/// treated as the local desktop. Every request here is refused or rejected by validation before any state is
/// written or any provider is contacted.
/// </summary>
public sealed class ApiKeyAiBackendAccessTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public ApiKeyAiBackendAccessTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    /// <summary>The same host as the shared factory, but without the loopback filter: requests carry no remote address.</summary>
    private sealed class NoRemoteAddressFactory : WebApplicationFactory<global::Program>
    {
        private readonly string _avatarDirectory = Path.Combine(
            Path.GetTempPath(), "parux-no-remote-address-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AvatarProviderService>();
                services.AddSingleton(provider => new AvatarProviderService(
                    provider.GetRequiredService<AvatarEngineService>(),
                    Path.Combine(_avatarDirectory, "avatar.local.json")));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (Directory.Exists(_avatarDirectory)) Directory.Delete(_avatarDirectory, true); } catch { }
        }
    }

    private static HttpClient DesktopClient(WebApplicationFactory<global::Program> factory)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    [Fact]
    public async Task The_loopback_desktop_reaches_the_control_plane()
    {
        using HttpClient desktop = DesktopClient(_factory);

        HttpResponseMessage keys = await desktop.GetAsync("/api/v1/apikeys");

        Assert.Equal(HttpStatusCode.OK, keys.StatusCode);
    }

    [Fact]
    public async Task The_loopback_desktop_reaches_the_backend_probe_and_status_routes()
    {
        using HttpClient desktop = DesktopClient(_factory);

        HttpResponseMessage detect = await desktop.GetAsync("/api/v1/ai/backends/detect");
        HttpResponseMessage external = await desktop.GetAsync("/api/v1/ai/backends/external");

        Assert.Equal(HttpStatusCode.OK, detect.StatusCode);
        Assert.Equal(HttpStatusCode.OK, external.StatusCode);
    }

    [Fact]
    public async Task The_loopback_desktop_reaches_the_mutating_backend_routes()
    {
        using HttpClient desktop = DesktopClient(_factory);

        // Both payloads fail validation before anything is written, so a 400 proves the owner check let them through.
        HttpResponseMessage external = await desktop.PostAsJsonAsync(
            "/api/v1/ai/backends/external",
            new SetExternalTextProviderRequest { ProviderName = "probe", Endpoint = "not-a-url" });
        HttpResponseMessage cloudFallback = await desktop.PostAsJsonAsync(
            "/api/v1/ai/backends/cloud-fallback",
            new SetCloudFallbackRequest
            {
                Enabled = true,
                ProviderId = "openrouter",
                BaseUrl = "http://plain.example.com/v1",
                Model = "a-model",
                ApiKey = "test-only-key"
            });

        Assert.Equal(HttpStatusCode.BadRequest, external.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, cloudFallback.StatusCode);
    }

    [Fact]
    public async Task A_request_without_a_recorded_remote_address_is_not_treated_as_the_desktop()
    {
        using var unaddressed = new NoRemoteAddressFactory();
        using HttpClient client = DesktopClient(unaddressed);

        HttpResponseMessage keys = await client.GetAsync("/api/v1/apikeys");
        HttpResponseMessage backend = await client.PostAsJsonAsync(
            "/api/v1/ai/backends/external",
            new SetExternalTextProviderRequest { ProviderName = "probe", Endpoint = "not-a-url" });

        Assert.Equal(HttpStatusCode.Forbidden, keys.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, backend.StatusCode);
    }
}
