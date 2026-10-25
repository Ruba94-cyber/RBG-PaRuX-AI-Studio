using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The search API belongs to the authenticated local desktop control plane.
/// An unauthenticated or provider-key caller may not acquire a new browser/tool
/// capability by requesting the endpoint directly.
/// </summary>
public sealed class CodingWebSearchEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public CodingWebSearchEndpointTests(ApiSecurityTests.PaRuXFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task Unauthenticated_calls_never_trigger_external_documentation_search()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/coding/web-search",
            new CodingWebSearchRequest { Query = "Windows UIA test docs", MaxResults = 3 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Stands in for the provider call. The user switches Internet off while the request is in flight, and the
    /// call stops the way the real service does: with <see cref="InternetAccessDisabledException"/>.</summary>
    private sealed class SwitchOffDuringSearchService : WebSearchService
    {
        private readonly InternetSettingsService _internet;

        public SwitchOffDuringSearchService(InternetSettingsService internet) : base(internet) => _internet = internet;

        public override Task<List<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
        {
            _internet.SetEnabled(false);
            return Task.FromException<List<WebSearchResult>>(new InternetAccessDisabledException());
        }
    }

    /// <summary>The user switches Internet off while a documentation search is in flight. The stopped call must come back
    /// as the same conflict the post-search check reports, not as "providers unavailable".</summary>
    [Fact]
    public async Task Switching_internet_off_during_a_search_is_reported_as_a_conflict()
    {
        // This host gets its own switch on its own state directory. The shared factory resolves the switch from one
        // internet.json for the whole test process, so flipping it here would change what every other test reads.
        string stateDirectory = Path.Combine(Path.GetTempPath(), "parux-coding-search-" + Guid.NewGuid().ToString("N"));
        try
        {
            var internet = new InternetSettingsService(stateDirectory);
            using WebApplicationFactory<global::Program> factory = _factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<InternetSettingsService>();
                    services.AddSingleton(internet);
                    services.RemoveAll<WebSearchService>();
                    services.AddSingleton<WebSearchService>(provider =>
                        new SwitchOffDuringSearchService(provider.GetRequiredService<InternetSettingsService>()));
                }));

            internet.SetEnabled(true);
            using HttpClient desktop = DesktopClient(factory);
            using HttpResponseMessage response = await desktop.PostAsJsonAsync(
                "/api/v1/coding/web-search",
                new CodingWebSearchRequest { Query = "Windows UIA test docs", MaxResults = 3 });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Internet was switched off during this search.", body.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            try { Directory.Delete(stateDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static HttpClient DesktopClient(WebApplicationFactory<global::Program> factory)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }
}
