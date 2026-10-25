using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Directive Phase 8 (Smart Model Router). MapTaskTypeToRole is a pure function, directly unit
/// tested. POST /api/v1/router/resolve's real body is deliberately NOT called in ANY shape from
/// this hermetic suite, not even with a nonsense role - ResolveAsync's own deterministic-fallback
/// policy means even an obviously-invalid role falls through to a real EnsureRoleLoadedAsync("standard")
/// call, and on a real developer machine (as opposed to a bare CI box) "standard" almost always
/// already resolves to a real, existing GGUF file - so a fallback attempt there reaches real
/// process-control code (LocalAiProcessManager.RunActionAsync) against this developer's own
/// shared runtime state. This is the same class of hazard RuntimeManagerTests/
/// CapabilityProfilerTests already document for Unload/a full profiling run; the fix here is the
/// same one those files use - never invoke it hermetically at all. What IS safe and covered here:
/// auth rejection (returns before the handler runs), and GET .../decisions with no prior resolve
/// call. The real reuse-vs-swap and fallback behavior is covered by live verification instead.
/// </summary>
public sealed class SmartRouterMappingTests
{
    [Theory]
    [InlineData("write some code for the login page", "coding")]
    [InlineData("please refactor this function", "coding")]
    [InlineData("review this pull request", "reviewer")]
    [InlineData("audit the security of this change", "reviewer")]
    [InlineData("plan out the next sprint", "planner")]
    [InlineData("decompose this into smaller tasks", "planner")]
    [InlineData("do some deep research on this topic", "reasoning")]
    [InlineData("analyze the tradeoffs carefully", "reasoning")]
    [InlineData("describe what you see in this image", "vision")]
    [InlineData("give me a quick answer", "fast")]
    [InlineData("this is a trivial lookup", "fast")]
    [InlineData("switch to uncensored mode", "uncensored")]
    [InlineData("just have a normal chat", "standard")]
    [InlineData("", "standard")]
    [InlineData(null, "standard")]
    // Multimodal roles (directive Phase 24 return-to-close, 2026-08-27):
    [InlineData("please generate an image of a cat", "image")]
    [InlineData("draw me a sunset over mountains", "image")]
    [InlineData("edit this photo and remove background", "image_edit")]
    [InlineData("please generate a video of waves", "video")]
    public void MapTaskTypeToRole_maps_every_known_hint_and_falls_back_to_standard(string? taskType, string expectedRole)
    {
        Assert.Equal(expectedRole, SmartRouterService.MapTaskTypeToRole(taskType));
    }

    [Fact]
    public void MapTaskTypeToRole_never_returns_a_role_outside_the_full_catalog()
    {
        string[] knownRoles = LocalAiPaths.KnownTextRoles.Concat(ModelRoleCatalog.MediaRoles).ToArray();
        foreach (string? hint in new[]
        {
            null, "", "gibberish nonsense text", "coding review plan research",
            "generate an image", "edit this photo", "generate a video"
        })
        {
            string role = SmartRouterService.MapTaskTypeToRole(hint);
            Assert.Contains(role, knownRoles);
        }
    }
}

public sealed class SmartRouterApiTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public SmartRouterApiTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    [Fact]
    public async Task Router_endpoints_reject_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/router/resolve", new RouteRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/router/decisions")).StatusCode);
    }

    [Fact]
    public async Task List_decisions_returns_ok_with_no_prior_resolve_call()
    {
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/router/decisions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<RouteDecisionDto>? decisions = await response.Content.ReadFromJsonAsync<List<RouteDecisionDto>>();
        Assert.NotNull(decisions);
    }

    // The candidate-pool endpoints below are tested only up to (never past) the point where they
    // would touch real shared machine state - GET reaches RuntimeManagerService.GetStatusAsync
    // (real GPU/PID queries) for any KNOWN role, and POST/DELETE reach
    // LocalAiPaths.AddCandidate/RemoveCandidate (a real %LOCALAPPDATA% write) for any file that
    // genuinely exists in the Managed Model Library - same class of hazard, same fix, as
    // ModelRolesTests/SmartRouterMappingTests already document for SetRoleOverride/ResolveAsync.

    [Fact]
    public async Task Candidate_endpoints_reject_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/router/candidates/standard")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/router/candidates/standard", new AddCandidateRequest { FileName = "x.gguf" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/api/v1/router/candidates/standard/x.gguf")).StatusCode);
    }

    [Fact]
    public async Task Get_candidates_rejects_an_unknown_role_before_touching_runtime_state()
    {
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/router/candidates/not-a-real-role");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Add_candidate_rejects_a_file_not_in_the_managed_library_before_any_write()
    {
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/router/candidates/standard",
            new AddCandidateRequest { FileName = "parux-test-does-not-exist-" + Guid.NewGuid().ToString("N") + ".gguf" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Remove_candidate_rejects_a_file_not_in_the_managed_library_before_any_write()
    {
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.DeleteAsync(
            "/api/v1/router/candidates/standard/parux-test-does-not-exist-" + Guid.NewGuid().ToString("N") + ".gguf");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("Tell me a joke about breakfast", "standard")]
    [InlineData("I seem to have lost my keys", "standard")]
    [InlineData("What does this barcode mean", "standard")]
    [InlineData("Create an image of a code editor on a desk", "image")]
    [InlineData("Genera un'immagine di un gatto", "image")]
    [InlineData("Crea un video di un tramonto", "video")]
    [InlineData("Rimuovi lo sfondo da questa foto", "image_edit")]
    [InlineData("Refactor the codebase", "coding")]
    [InlineData("Analizza questo contratto", "reasoning")]
    [InlineData("edit_image", "image_edit")]
    public void Task_classifier_matches_whole_words_and_prefers_explicit_media_requests(string prompt, string expectedRole) =>
        Assert.Equal(expectedRole, SmartRouterService.MapTaskTypeToRole(prompt));
}
