using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;
using Xunit.Sdk;

namespace PaRuX.Server.Tests;

/// <summary>Remote pairing changes process-wide PaRuX data-root state and therefore shares the
/// suite's existing serial collection with tests that exercise global runtime roots.</summary>
[Collection("OverheadBenchmarkSerial")]
public sealed class RemotePairingEndpointTests
{
    [Fact]
    public async Task Paired_https_private_client_cannot_refresh_explicit_discovery()
    {
        var provider = new CountingExplicitDiscoveryProvider();
        await WithFactory(async factory =>
        {
            using HttpClient desktop = DesktopClient(factory);
            string address = RemoteAccessService.GetPrivateIpv4Addresses().FirstOrDefault()?.ToString()
                ?? throw SkipException.ForSkip("An assigned private IPv4 interface is required to exercise LAN pairing.");
            Assert.Equal(HttpStatusCode.OK, (await desktop.PostAsJsonAsync("/api/v1/remote/config",
                new RemoteAccessConfigRequest { Enabled = true, Address = address, Port = 55123 })).StatusCode);
            PairingCompleteResponse identity = await PairAsync(desktop);
            using HttpClient paired = factory.CreateClient();
            paired.DefaultRequestHeaders.Add("X-PaRuX-Test-Remote", "1");
            paired.DefaultRequestHeaders.Add("X-PaRuX-Device-Token", identity.DeviceToken);
            Assert.Equal(HttpStatusCode.OK, (await paired.GetAsync("/api/v1/mobile/artifacts")).StatusCode);

            HttpResponseMessage forbidden = await paired.PostAsJsonAsync(
                "/api/v1/models/discovery/refresh-explicit",
                new ModelDiscoveryExplicitRefreshRequest { RepositoryIds = new List<string> { "org/target" } });
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Equal(0, provider.Calls);
        }, services =>
        {
            services.RemoveAll<IModelDiscoveryProvider>();
            services.RemoveAll<ModelDiscoveryService>();
            services.AddSingleton<IModelDiscoveryProvider>(provider);
            services.AddSingleton(sp => new ModelDiscoveryService(provider,
                sp.GetRequiredService<ModelDiscoveryCatalogStore>()));
        });
    }

    [Fact]
    public async Task Pairing_remote_access_and_revoke_all_are_enforced_over_http()
    {
        await WithFactory(async factory =>
        {
            using HttpClient desktop = DesktopClient(factory);
            using HttpClient paired = factory.CreateClient();
            paired.DefaultRequestHeaders.Add("X-PaRuX-Test-Remote", "1");

            string address = RemoteAccessService.GetPrivateIpv4Addresses().FirstOrDefault()?.ToString()
                ?? throw SkipException.ForSkip("An assigned private IPv4 interface is required to exercise LAN pairing.");
            HttpResponseMessage configured = await desktop.PostAsJsonAsync("/api/v1/remote/config",
                new RemoteAccessConfigRequest { Enabled = true, Address = address, Port = 55123 });
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);

            PairingCompleteResponse first = await PairAsync(desktop);
            paired.DefaultRequestHeaders.Add("X-PaRuX-Device-Token", first.DeviceToken);
            Assert.Equal(HttpStatusCode.OK, (await paired.GetAsync("/api/v1/mobile/artifacts")).StatusCode);

            HttpResponseMessage revoke = await desktop.DeleteAsync("/api/v1/remote/devices");
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            Assert.Equal(1, (await revoke.Content.ReadFromJsonAsync<RevokeAllResponse>())?.Count);
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await paired.GetAsync("/api/v1/mobile/artifacts")).StatusCode);

            paired.DefaultRequestHeaders.Remove("X-PaRuX-Device-Token");
            PairingCompleteResponse reconnected = await PairAsync(desktop);
            Assert.NotEqual(first.DeviceId, reconnected.DeviceId);
            Assert.NotEqual(first.DeviceToken, reconnected.DeviceToken);
            paired.DefaultRequestHeaders.Add("X-PaRuX-Device-Token", reconnected.DeviceToken);
            Assert.Equal(HttpStatusCode.OK, (await paired.GetAsync("/api/v1/mobile/artifacts")).StatusCode);
        });
    }

    [Fact]
    public async Task Remote_routes_reject_disabled_malformed_and_non_loopback_requests()
    {
        await WithFactory(async factory =>
        {
            using HttpClient unauthenticated = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await unauthenticated.DeleteAsync("/api/v1/remote/devices")).StatusCode);

            using HttpClient malformedDesktop = factory.CreateClient();
            malformedDesktop.DefaultRequestHeaders.Add("X-PaRuX-Token", "not-a-valid-session-token");
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await malformedDesktop.DeleteAsync("/api/v1/remote/devices")).StatusCode);

            using HttpClient desktop = DesktopClient(factory);
            Assert.Equal(HttpStatusCode.Conflict,
                (await desktop.PostAsync("/api/v1/remote/pair/start", content: null)).StatusCode);

            using HttpClient remote = factory.CreateClient();
            remote.DefaultRequestHeaders.Add("X-PaRuX-Test-Remote", "1");
            remote.DefaultRequestHeaders.Add("X-PaRuX-Device-Token", "malformed-device-token");
            Assert.Equal(HttpStatusCode.Forbidden,
                (await remote.GetAsync("/api/v1/mobile/artifacts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await remote.DeleteAsync("/api/v1/remote/devices")).StatusCode);

            // Pair completion is the only unauthenticated bootstrap route; malformed consent/code
            // data cannot manufacture an identity.
            HttpResponseMessage rejectedPair = await desktop.PostAsJsonAsync(
                "/api/v1/remote/pair/complete",
                new PairingCompleteRequest { Code = "000000", DeviceName = "Unpaired" });
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedPair.StatusCode);
        });
    }

    private static async Task<PairingCompleteResponse> PairAsync(HttpClient desktop)
    {
        HttpResponseMessage start = await desktop.PostAsync("/api/v1/remote/pair/start", content: null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        PairingStartResponse? pairing = await start.Content.ReadFromJsonAsync<PairingStartResponse>();
        Assert.NotNull(pairing);

        HttpResponseMessage complete = await desktop.PostAsJsonAsync(
            "/api/v1/remote/pair/complete",
            new PairingCompleteRequest { Code = pairing!.Code, DeviceName = "Consent test client" });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        PairingCompleteResponse? paired = await complete.Content.ReadFromJsonAsync<PairingCompleteResponse>();
        Assert.NotNull(paired);
        Assert.False(string.IsNullOrWhiteSpace(paired!.DeviceToken));
        return paired;
    }

    private static HttpClient DesktopClient(WebApplicationFactory<global::Program> factory)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    private static async Task WithFactory(Func<WebApplicationFactory<global::Program>, Task> test,
        Action<IServiceCollection>? configureServices = null)
    {
        string previousRoot = PaRuXDataRoot.Path;
        string isolatedRoot = Path.Combine(
            FindRepositoryRoot(), ".ai", "artifacts", "remote-pairing-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedRoot);
        PaRuXDataRoot.UseForTests(isolatedRoot);
        try
        {
            using var baseFactory = new ApiSecurityTests.PaRuXFactory();
            using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.AddTransient<IStartupFilter, SimulatedPrivateClientStartupFilter>();
                    configureServices?.Invoke(services);
                }));
            await test(factory);
        }
        finally
        {
            PaRuXDataRoot.UseForTests(previousRoot);
            try { Directory.Delete(isolatedRoot, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find PaRuX.sln from the test output directory.");
    }

    private sealed class SimulatedPrivateClientStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Headers["X-PaRuX-Test-Remote"] == "1")
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse("10.43.0.7");
                    context.Request.Scheme = "https";
                }
                await continuation();
            });
            next(app);
        };
    }

    private sealed record RevokeAllResponse(int Count);

    private sealed class CountingExplicitDiscoveryProvider : IModelDiscoveryProvider, IExplicitModelDiscoveryProvider
    {
        internal int Calls { get; private set; }
        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelDiscoveryCandidate>>(Array.Empty<ModelDiscoveryCandidate>());
        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverExplicitAsync(
            IReadOnlyList<string> repositoryIds, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ModelDiscoveryCandidate>>(Array.Empty<ModelDiscoveryCandidate>());
        }
    }
}
