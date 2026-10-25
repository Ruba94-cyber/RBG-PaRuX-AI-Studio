using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The browser door as a real HTTP client sees it: a LAN request gets no exemption, and a signed-in browser is refused every
/// route that only the desktop itself may use, even though it reaches PaRuX from loopback through a reverse proxy.
/// </summary>
public sealed class BrowserOwnerAndLanHardeningTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private const string GoodPassphrase = "correct horse battery staple";

    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public BrowserOwnerAndLanHardeningTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private WebAccessService Web => _factory.Services.GetRequiredService<WebAccessService>();

    private HttpClient DesktopClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    private static async Task<(string Cookie, string CsrfToken)> SignInAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/web/session", new WebSessionRequest { Passphrase = GoodPassphrase });
        response.EnsureSuccessStatusCode();
        string setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        WebSessionResponse body = (await response.Content.ReadFromJsonAsync<WebSessionResponse>())!;
        return (setCookie.Split(';')[0], body.CsrfToken!);
    }

    [Fact]
    public async Task A_sign_in_from_the_lan_is_refused_even_with_the_right_passphrase()
    {
        Web.Reset();
        Web.SetPassphrase(GoodPassphrase);
        Web.SetEnabled(true);
        WebApplicationFactory<Program> lan = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(
                new RemoteAddressStartupFilter(IPAddress.Parse("192.168.1.10")))));
        try
        {
            HttpResponseMessage response = await lan.CreateClient().PostAsJsonAsync(
                "/api/v1/web/session", new WebSessionRequest { Passphrase = GoodPassphrase });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        finally
        {
            lan.Dispose();
            Web.Reset();
        }
    }

    [Theory]
    [InlineData("GET", "/api/v1/ai/backends/remote-policy", null)]
    [InlineData("GET", "/api/v1/ai/backends/cloud-fallback", null)]
    [InlineData("GET", "/api/v1/remote/status", null)]
    [InlineData("GET", "/api/v1/backup/export", null)]
    [InlineData("POST", "/api/v1/backup/import", null)]
    [InlineData("POST", "/api/v1/remote/config", "{}")]
    [InlineData("POST", "/api/v1/remote/pair/start", null)]
    [InlineData("DELETE", "/api/v1/remote/devices", null)]
    [InlineData("DELETE", "/api/v1/remote/devices/no-such-device", null)]
    [InlineData("POST", "/api/v1/mcp/servers", "{}")]
    [InlineData("DELETE", "/api/v1/mcp/servers/no-such-server", null)]
    [InlineData("POST", "/api/v1/mcp/servers/no-such-server/enabled", "{}")]
    [InlineData("POST", "/api/v1/mcp/servers/no-such-server/allowed-tools", "{}")]
    [InlineData("POST", "/api/v1/desktop/integrations/no-such-source/connect", "{}")]
    [InlineData("DELETE", "/api/v1/desktop/integrations/no-such-source", null)]
    [InlineData("POST", "/api/v1/desktop/documents/output", "{}")]
    [InlineData("POST", "/api/v1/runtime/actions", "{\"action\":\"no-such-action\"}")]
    [InlineData("DELETE", "/api/v1/training/engine", null)]
    [InlineData("GET", "/api/v2/backup/export", null)]
    [InlineData("POST", "/api/v2/backup/import", null)]
    [InlineData("DELETE", "/api/v2/training/engine", null)]
    [InlineData("GET", "/api/v1/mcp/servers", null)]
    [InlineData("GET", "/api/v1/system/localai-root", null)]
    [InlineData("POST", "/api/v1/runtime/performance/tune", "{}")]
    [InlineData("POST", "/api/v1/runtime/performance/tune/no-such-job/cancel", null)]
    [InlineData("POST", "/api/v1/runtime/performance/optimize", "{}")]
    [InlineData("POST", "/api/v1/runtime/performance/optimize/no-such-job/cancel", null)]
    [InlineData("POST", "/api/v1/runtime/performance/build-race", "{}")]
    [InlineData("POST", "/api/v1/runtime/performance/build-race/no-such-job/cancel", null)]
    [InlineData("POST", "/api/v1/runtime/performance/backend-race", "{}")]
    [InlineData("POST", "/api/v1/runtime/performance/engine-race", "{}")]
    [InlineData("POST", "/api/v1/runtime/performance/mode", "{\"mode\":\"balanced\"}")]
    [InlineData("POST", "/api/v1/rag/performance/provider-race/embedding", "{}")]
    [InlineData("POST", "/api/v1/rag/performance/provider-race/reranker", "{}")]
    [InlineData("POST", "/api/v1/rag/performance/index-race", "{}")]
    [InlineData("POST", "/api/v1/rag/performance/providers/reset", "{}")]
    [InlineData("POST", "/api/v1/runtime/engines/exllamav3/install", "{}")]
    [InlineData("POST", "/api/v1/runtime/engines/exllamav3/install/no-such-job/cancel", null)]
    [InlineData("POST", "/api/v1/updates/check", null)]
    [InlineData("POST", "/api/v1/updates/models/install", "{}")]
    [InlineData("POST", "/api/v1/updates/models/install/no-such-job/cancel", null)]
    [InlineData("POST", "/api/v1/updates/acknowledge", "{}")]
    [InlineData("POST", "/api/v1/updates/dismiss", "{}")]
    [InlineData("POST", "/api/v1/training/engine/install", null)]
    [InlineData("POST", "/api/v1/training/jobs", "{}")]
    [InlineData("POST", "/api/v1/training/jobs/no-such-job/cancel", null)]
    [InlineData("POST", "/api/v2/training/engine/install", null)]
    [InlineData("POST", "/api/v2/training/jobs", "{}")]
    [InlineData("POST", "/api/v2/media/engines/ref2va/install", null)]
    [InlineData("POST", "/api/v2/media/engines/ref2va/generate", "{}")]
    [InlineData("POST", "/api/v2/media/engines/diffusers/install", null)]
    [InlineData("POST", "/api/v2/media/engines/diffusers/benchmark/warm", "{}")]
    [InlineData("POST", "/api/v2/media/engine-race/image", "{}")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/no-such-id/delegate", "{\"parentTaskId\":\"p\",\"goal\":\"g\"}")]
    [InlineData("POST", "/api/v1/gaming/enter", "{}")]
    [InlineData("POST", "/api/v1/gaming/exit", null)]
    [InlineData("POST", "/api/v1/gaming/suspend", null)]
    [InlineData("POST", "/api/v1/gaming/resume", null)]
    [InlineData("POST", "/api/v1/gaming/profiles", "{}")]
    [InlineData("DELETE", "/api/v1/gaming/profiles/no-such-id", null)]
    [InlineData("PUT", "/api/v2/execution-profiles/quality", "{}")]
    [InlineData("PUT", "/api/v2/execution-profiles/models/no-such-id", "{}")]
    [InlineData("DELETE", "/api/v2/execution-profiles/models/no-such-id", null)]
    [InlineData("POST", "/api/v2/plugins/inspect", "{}")]
    [InlineData("POST", "/api/v2/plugins/install", "{}")]
    [InlineData("DELETE", "/api/v2/plugins/trusted-publishers/no-such-id", null)]
    [InlineData("POST", "/api/v2/goals", "{}")]
    [InlineData("POST", "/api/v2/goals/no-such-id/versions", "{}")]
    [InlineData("POST", "/api/v2/goals/tasks", "{}")]
    [InlineData("POST", "/api/v2/virtual-context/ledger", "{}")]
    [InlineData("PUT", "/api/v2/semantic-index/repositories", "{}")]
    [InlineData("DELETE", "/api/v2/semantic-index/repositories/no-such-id", null)]
    [InlineData("POST", "/api/v2/semantic-index/sync", "{}")]
    [InlineData("POST", "/api/v2/semantic-index/search", "{}")]
    [InlineData("POST", "/api/v1/images/generations", "{}")]
    [InlineData("POST", "/api/v1/ai/profile/auto-fallback", "{}")]
    [InlineData("PUT", "/api/v1/memory/no-such-id", "{}")]
    [InlineData("POST", "/api/v1/memory/no-such-id/enabled", "{}")]
    [InlineData("DELETE", "/api/v1/memory", null)]
    [InlineData("POST", "/api/v1/memory/import", "[]")]
    [InlineData("POST", "/api/v1/rag/sources", "{}")]
    [InlineData("POST", "/api/v1/rag/sources/no-such-id/enabled", "{}")]
    [InlineData("POST", "/api/v1/research/deep", "{}")]
    [InlineData("POST", "/api/v1/embeddings/engine/install", null)]
    [InlineData("POST", "/api/v1/reranker/engine/install", null)]
    [InlineData("POST", "/api/v1/reranker/rerank", "{}")]
    [InlineData("POST", "/api/v1/personas", "{}")]
    [InlineData("DELETE", "/api/v1/personas/no-such-id", null)]
    [InlineData("POST", "/api/v1/personas/active", "{}")]
    [InlineData("POST", "/api/v1/skills", "{}")]
    [InlineData("POST", "/api/v1/skills/import", "{}")]
    [InlineData("POST", "/api/v1/skills/no-such-id/permissions", "{}")]
    [InlineData("POST", "/api/v1/skills/route", "{}")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/no-such-id/status", "{\"status\":\"running\"}")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/no-such-id/logs", "{\"message\":\"m\"}")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/no-such-id/worktree", "{\"worktreePath\":\"p\"}")]
    [InlineData("POST", "/api/v1/orchestrator/schedule", null)]
    [InlineData("POST", "/api/v1/automations", "{\"name\":\"n\",\"triggerType\":\"t\",\"triggerConfigJson\":\"{}\",\"actionType\":\"a\",\"actionConfigJson\":\"{}\"}")]
    [InlineData("POST", "/api/v1/automations/tick", null)]
    [InlineData("POST", "/api/v1/context/entities", "{\"entityType\":\"t\",\"name\":\"n\"}")]
    [InlineData("DELETE", "/api/v1/context/entities/no-such-id", null)]
    [InlineData("POST", "/api/v1/context/relations", "{\"fromEntityId\":\"a\",\"relationType\":\"r\",\"toEntityId\":\"b\"}")]
    [InlineData("DELETE", "/api/v1/context/relations/no-such-id", null)]
    [InlineData("POST", "/api/v1/context/build", "{\"query\":\"q\"}")]
    [InlineData("POST", "/api/v1/repo-index", "{\"repositoryPath\":\"p\"}")]
    [InlineData("POST", "/api/v1/repo-index/search", "{\"repositoryPath\":\"p\",\"goalText\":\"g\",\"maxFiles\":1,\"maxSymbols\":1}")]
    [InlineData("POST", "/api/v1/artifacts", "{\"artifactType\":\"note\",\"title\":\"t\",\"contentRef\":\"c\"}")]
    [InlineData("POST", "/api/v1/artifacts/no-such-id/status", "{\"status\":\"draft\"}")]
    [InlineData("POST", "/api/v1/artifacts/no-such-id/versions", "{\"contentRef\":\"c\"}")]
    [InlineData("POST", "/api/v1/artifacts/no-such-id/versions/1/restore", null)]
    [InlineData("POST", "/api/v1/projects/desktop", "{}")]
    [InlineData("POST", "/api/v1/eval/datasets", "{\"name\":\"n\"}")]
    [InlineData("POST", "/api/v1/eval/datasets/no-such-id/cases", "{\"prompt\":\"p\",\"expectedBehavior\":\"e\"}")]
    [InlineData("POST", "/api/v1/eval/datasets/no-such-id/runs", "{}")]
    [InlineData("POST", "/api/v1/autolab/experiments", "{}")]
    [InlineData("POST", "/api/v1/autolab/experiments/no-such-id/promote", "{}")]
    [InlineData("POST", "/api/v1/autolab/sweep", "{}")]
    [InlineData("POST", "/api/v1/diff/text", "{}")]
    [InlineData("POST", "/api/v1/workflows", "{}")]
    [InlineData("DELETE", "/api/v1/workflows/no-such-id", null)]
    [InlineData("POST", "/api/v1/avatar/engine/install", null)]
    [InlineData("POST", "/api/v1/avatar/photo", "{}")]
    [InlineData("POST", "/api/v1/training/plan", "{}")]
    [InlineData("POST", "/api/v1/adapters", "{}")]
    [InlineData("POST", "/api/v1/adapters/no-such-id/enabled", "{}")]
    [InlineData("POST", "/api/v1/adapters/no-such-id/rename", "{}")]
    [InlineData("POST", "/api/v1/adapters/no-such-id/export", "{}")]
    [InlineData("DELETE", "/api/v1/adapters/no-such-id", null)]
    [InlineData("POST", "/api/v1/adapters/conversion/install", null)]
    [InlineData("POST", "/api/v1/adapters/no-such-id/convert", "{}")]
    [InlineData("POST", "/api/v1/adapters/no-such-id/merge", "{}")]
    [InlineData("POST", "/api/v1/speech/engine/install", null)]
    [InlineData("POST", "/api/v1/models/discovery/refresh", null)]
    [InlineData("POST", "/api/v1/models/discovery/no-such-id/ignore", null)]
    [InlineData("POST", "/api/v1/models/discovery/no-such-id/notice/later", null)]
    [InlineData("POST", "/api/v1/models/discovery/no-such-id/notice/handled", null)]
    [InlineData("POST", "/api/v1/models/roles/auto-assign", null)]
    [InlineData("DELETE", "/api/v1/models/library/no-such.gguf", null)]
    [InlineData("POST", "/api/v1/models/library/import", "{}")]
    [InlineData("POST", "/api/v1/models/library/no-such.gguf/alias", "{}")]
    [InlineData("DELETE", "/api/v1/models/downloads/no-such-id", null)]
    [InlineData("POST", "/api/v1/models/benchmarks", "{}")]
    [InlineData("POST", "/api/v1/models/benchmarks/no-such-id/cancel", null)]
    [InlineData("PUT", "/api/v1/models/context-preference", "{}")]
    [InlineData("DELETE", "/api/v1/models/context-preference?path=no-such", null)]
    [InlineData("PUT", "/api/v1/coding/model-selection/mode", "{\"mode\":\"automatic\"}")]
    [InlineData("POST", "/api/v1/models/roles/no-such-id", "{}")]
    [InlineData("POST", "/api/v1/models/roles/no-such-id/adapter", "{}")]
    [InlineData("DELETE", "/api/v1/models/roles/no-such-id/adapter", null)]
    [InlineData("POST", "/api/v1/models/roles/no-such-id/reset", null)]
    [InlineData("POST", "/api/v1/runtime/components/no-such-id/locate", "{}")]
    [InlineData("POST", "/api/v1/runtime/components/no-such-id/remove-location", null)]
    [InlineData("POST", "/api/v1/runtime/components/no-such-id/skip", null)]
    [InlineData("POST", "/api/v1/runtime/components/no-such-id/unskip", null)]
    [InlineData("POST", "/api/v1/runtime/builds/install", "{}")]
    [InlineData("POST", "/api/v1/runtime/builds/install/no-such-id/cancel", null)]
    [InlineData("POST", "/api/v1/runtime/builds/rollback", "{}")]
    [InlineData("POST", "/api/v1/runtime/builds/reset", null)]
    [InlineData("POST", "/api/v1/capabilities/runs", "{}")]
    [InlineData("POST", "/api/v1/router/resolve", "{}")]
    [InlineData("POST", "/api/v1/router/candidates/no-such-id", "{\"fileName\":\"f.gguf\"}")]
    [InlineData("DELETE", "/api/v1/router/candidates/no-such-id/no-such.gguf", null)]
    [InlineData("POST", "/api/v1/datasets/downloads", "{}")]
    [InlineData("POST", "/api/v1/datasets/downloads/no-such-id/cancel", null)]
    [InlineData("POST", "/api/v1/datasets/inspect", "{}")]
    [InlineData("POST", "/api/v1/datasets/split", "{}")]
    [InlineData("POST", "/api/v1/datasets/import-eval", "{}")]
    [InlineData("POST", "/api/v1/media/checkpoints/no-such-id", "{}")]
    [InlineData("POST", "/api/v1/media/checkpoints/no-such-id/reset", null)]
    [InlineData("POST", "/api/v1/media/checkpoints/activate", "{}")]
    [InlineData("POST", "/api/v1/media/companions/fetch", "{}")]
    [InlineData("POST", "/api/v1/media/workflows", "{}")]
    [InlineData("DELETE", "/api/v1/media/workflows/no-such-id", null)]
    [InlineData("POST", "/api/v1/compare", "{}")]
    [InlineData("POST", "/api/v1/compare/no-such-id/cancel", null)]
    [InlineData("POST", "/api/v1/group-chat", "{}")]
    [InlineData("POST", "/api/v1/group-chat/no-such-id/cancel", null)]
    [InlineData("POST", "/api/v1/threed/engine/install", null)]
    public async Task A_browser_session_is_refused_every_desktop_owner_route(string method, string path, string? body)
    {
        Web.Reset();
        Web.SetPassphrase(GoodPassphrase);
        Web.SetEnabled(true);
        try
        {
            HttpClient browser = _factory.CreateClient();
            (string cookie, string csrf) = await SignInAsync(browser);

            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Add("Cookie", cookie);
            if (method != "GET") request.Headers.Add(WebAccessService.CsrfHeaderName, csrf);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await browser.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally { Web.Reset(); }
    }

    [Theory]
    [InlineData("/api/v1/ai/backends/remote-policy")]
    [InlineData("/api/v1/ai/backends/cloud-fallback")]
    [InlineData("/api/v1/remote/status")]
    [InlineData("/api/v1/mcp/servers")]
    [InlineData("/api/v1/system/localai-root")]
    public async Task The_desktop_itself_still_reads_the_policy_and_status_routes(string path)
    {
        HttpResponseMessage response = await DesktopClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_desktop_itself_still_reaches_a_training_run_cancel()
    {
        // The owner gate must not swallow the desktop's own calls: the handler runs and reports the job it does not have.
        HttpResponseMessage response = await DesktopClient().PostAsync("/api/v1/training/jobs/no-such-job/cancel", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", "desktop", true)]
    [InlineData("127.0.0.1", "web:session", false)]
    [InlineData("127.0.0.1", "device:phone", false)]
    [InlineData("127.0.0.1", "apikey:key", false)]
    [InlineData("192.168.1.10", "desktop", false)]
    [InlineData(null, "desktop", false)]
    public void Only_the_desktop_on_loopback_passes_the_desktop_owner_predicate(string? address, string owner, bool expected)
    {
        // The predicate every desktop-only route shares. A reverse proxy's request is loopback too, so the owner decides.
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
        context.Items["PaRuXOwner"] = owner;

        Assert.Equal(expected, DesktopOwnerRouteExtensions.IsDesktopOwner(context));
    }

    [Theory]
    [InlineData("/api/v1/models/benchmarks/no-such-run/cancel", HttpStatusCode.NotFound)]
    [InlineData("/api/v1/compare/no-such-run/cancel", HttpStatusCode.NotFound)]
    [InlineData("/api/v1/group-chat/no-such-run/cancel", HttpStatusCode.NotFound)]
    [InlineData("/api/v1/datasets/downloads/no-such-run/cancel", HttpStatusCode.NotFound)]
    [InlineData("/api/v1/runtime/builds/install/no-such-run/cancel", HttpStatusCode.BadRequest)]
    public async Task The_desktop_itself_still_reaches_the_gated_cancel_routes(string path, HttpStatusCode expected)
    {
        // Each handler answers for a run it does not have and changes nothing, so the status shows the desktop got past
        // the owner gate and into the handler. The install routes are not called here: they would start a real download.
        HttpResponseMessage response = await DesktopClient().PostAsync(path, null);

        Assert.Equal(expected, response.StatusCode);
    }

    private sealed class RemoteAddressStartupFilter : IStartupFilter
    {
        private readonly IPAddress _address;

        public RemoteAddressStartupFilter(IPAddress address) => _address = address;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.Connection.RemoteIpAddress = _address;
                await continuation();
            });
            next(app);
        };
    }
}
