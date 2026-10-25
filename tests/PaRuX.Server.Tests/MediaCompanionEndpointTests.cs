using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The companion plan and the consent-gated fetch, through the real HTTP pipeline. The test host installs an isolated
/// LocalAiPaths root, so the plan sees an empty ComfyUI and nothing real is read. Every request here is refused before
/// a byte is downloaded: a missing consent, a stale plan hash, or an unknown type.
/// </summary>
public sealed class MediaCompanionEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public MediaCompanionEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private HttpClient DesktopClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    [Fact]
    public async Task Companion_plan_rejects_a_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/media/companions?type=image");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Companion_plan_rejects_an_unknown_type()
    {
        using HttpClient client = DesktopClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/media/companions?type=upscale");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Companion_plan_for_an_uninstalled_checkpoint_is_unidentified_and_not_installable()
    {
        using HttpClient client = DesktopClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/media/companions?type=image");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        MediaCompanionPlan? plan = await response.Content.ReadFromJsonAsync<MediaCompanionPlan>();
        Assert.NotNull(plan);
        Assert.Equal("image", plan!.Type);
        Assert.Equal("unidentified", plan.Support);
        Assert.False(plan.Installable);
        Assert.Empty(plan.Companions);
        Assert.Equal(64, plan.PlanHash.Length);
    }

    [Fact]
    public async Task Companion_fetch_is_refused_without_consent_and_names_no_failed_step()
    {
        using HttpClient client = DesktopClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/media/companions/fetch",
            new MediaCompanionFetchRequest { Type = "image", PlanHash = new string('0', 64), Consent = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        MediaCompanionFetchResult? result = await response.Content.ReadFromJsonAsync<MediaCompanionFetchResult>();
        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.Contains("consent", result.Message);
        Assert.Null(result.FailedStep);
    }

    [Fact]
    public async Task Companion_fetch_is_refused_when_the_reviewed_plan_hash_is_stale()
    {
        using HttpClient client = DesktopClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/media/companions/fetch",
            new MediaCompanionFetchRequest { Type = "image", PlanHash = new string('0', 64), Consent = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        MediaCompanionFetchResult? result = await response.Content.ReadFromJsonAsync<MediaCompanionFetchResult>();
        Assert.NotNull(result);
        Assert.Contains("changed since you reviewed it", result!.Message);
    }

    [Fact]
    public async Task Companion_fetch_rejects_an_unknown_type()
    {
        using HttpClient client = DesktopClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/media/companions/fetch",
            new MediaCompanionFetchRequest { Type = "upscale", PlanHash = new string('0', 64), Consent = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
