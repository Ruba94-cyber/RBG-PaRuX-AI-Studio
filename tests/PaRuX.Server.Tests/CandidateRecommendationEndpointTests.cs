using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateRecommendationEndpointTests
{
    private const string Base = "/api/v1/models/candidate-recommendation";

    [Fact]
    public async Task Endpoints_are_desktop_owner_only_typed_and_never_promote_without_confirmation()
    {
        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, LoopbackStartupFilter>()));
        using HttpClient client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Base + "/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Base + "/rollback",
            new RollbackCandidateRecommendationRequest(0, true))).StatusCode);
        foreach (string path in new[] { "/status", "/promote", "/rollback" })
        {
            Assert.False(PairedDeviceEndpointPolicy.IsAllowed("GET", Base + path));
            Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", Base + path));
        }

        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        HttpResponseMessage status = await client.GetAsync(Base + "/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        CandidateRecommendationStatusResponse? body = await status.Content.ReadFromJsonAsync<CandidateRecommendationStatusResponse>();
        Assert.Equal("None", body!.State);
        Assert.False(body.CanRollback);

        // Unconfirmed: 400 with a typed, plain-language body; nothing is attempted.
        HttpResponseMessage unconfirmed = await client.PostAsJsonAsync(Base + "/promote",
            new PromoteCandidateRecommendationRequest(new string('a', 64), "m.gguf", 0, false));
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        CandidateRecommendationTransitionResponse? typed = await unconfirmed.Content.ReadFromJsonAsync<CandidateRecommendationTransitionResponse>();
        Assert.Equal("consent_required", typed!.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(typed.Message));

        // Unsafe identity is rejected before any work.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Base + "/promote",
            new PromoteCandidateRecommendationRequest("../x", "..\\m.gguf", 0, true))).StatusCode);

        // Rollback with nothing to go back to: 409, typed.
        HttpResponseMessage rollback = await client.PostAsJsonAsync(Base + "/rollback",
            new RollbackCandidateRecommendationRequest(0, true));
        Assert.True(rollback.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable);
        Assert.False(string.IsNullOrWhiteSpace((await rollback.Content.ReadFromJsonAsync<CandidateRecommendationTransitionResponse>())!.Message));
    }

    private sealed class LoopbackStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                await continuation();
            });
            next(app);
        };
    }
}
