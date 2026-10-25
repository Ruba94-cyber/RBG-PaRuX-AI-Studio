using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Covers the Local API Server (/v1/*): auth
/// (both X-PaRuX-Token and Authorization: Bearer), request validation, logical-model routing,
/// and the deterministic OpenAI-style SSE proxy path. Tests never need a real model process: the
/// HTTP seam is replaced with a tiny in-memory handler where provider behavior matters.</summary>
public sealed class LocalApiServerTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public LocalApiServerTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Models_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Models_endpoint_accepts_the_desktop_session_token()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("list", body?["object"]?.GetValue<string>());
        JsonArray data = body!["data"]!.AsArray();
        Assert.Contains(data, m => m!["id"]!.GetValue<string>() == "parux-main");
        Assert.Contains(data, m => m!["id"]!.GetValue<string>() == "parux-uncensored");
    }

    [Fact]
    public async Task Models_endpoint_accepts_a_standard_bearer_token_for_the_same_secret()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.Token);

        HttpResponseMessage response = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Models_endpoint_rejects_an_invalid_bearer_token()
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-real-token");

        HttpResponseMessage response = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chat_completions_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CompleteAsync_rejects_invalid_json()
    {
        LocalApiServerService localApi = _factory.Services.GetRequiredService<LocalApiServerService>();
        LocalApiCompletionResult result = await localApi.CompleteAsync("not json", CancellationToken.None);
        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public async Task CompleteAsync_rejects_a_missing_messages_array()
    {
        LocalApiServerService localApi = _factory.Services.GetRequiredService<LocalApiServerService>();
        LocalApiCompletionResult result = await localApi.CompleteAsync("{\"model\":\"parux-main\"}", CancellationToken.None);
        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public async Task CompleteAsync_rejects_an_empty_messages_array()
    {
        LocalApiServerService localApi = _factory.Services.GetRequiredService<LocalApiServerService>();
        LocalApiCompletionResult result = await localApi.CompleteAsync("{\"model\":\"parux-main\",\"messages\":[]}", CancellationToken.None);
        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public async Task CompleteAsync_keeps_streaming_requests_off_the_buffered_path()
    {
        LocalApiServerService localApi = _factory.Services.GetRequiredService<LocalApiServerService>();
        const string body = "{\"model\":\"parux-main\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        LocalApiCompletionResult result = await localApi.CompleteAsync(body, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("SSE streaming path", result.Body);
    }

    [Fact]
    public void RequestsStreaming_only_selects_an_explicit_true_boolean()
    {
        Assert.True(LocalApiServerService.RequestsStreaming("{\"stream\":true}"));
        Assert.False(LocalApiServerService.RequestsStreaming("{\"stream\":false}"));
        Assert.False(LocalApiServerService.RequestsStreaming("{}"));
        Assert.False(LocalApiServerService.RequestsStreaming("not json"));
    }

    [Fact]
    public void ListModels_exposes_both_logical_role_names()
    {
        LocalApiServerService localApi = _factory.Services.GetRequiredService<LocalApiServerService>();
        string json = JsonSerializer.Serialize(localApi.ListModels());
        Assert.Contains("parux-main", json);
        Assert.Contains("parux-uncensored", json);
    }

    [Fact]
    public void ListModels_exposes_every_text_role_not_just_the_original_two()
    {
        LocalApiServerService localApi = _factory.Services.GetRequiredService<LocalApiServerService>();
        string json = JsonSerializer.Serialize(localApi.ListModels());
        foreach (string id in new[] { "parux-fast", "parux-reasoning", "parux-planner", "parux-coding", "parux-reviewer", "parux-vision" })
            Assert.Contains(id, json);
    }

    [Fact]
    public async Task CompleteAsync_rejects_an_unknown_model_id_instead_of_silently_using_whatever_is_loaded()
    {
        LocalApiServerService localApi = _factory.Services.GetRequiredService<LocalApiServerService>();
        // Vendor ids (gpt-*, claude-*) now map explicitly to the configured gateway default; an id
        // that is neither a PaRuX id nor a vendor id must still be rejected.
        const string body = "{\"model\":\"mystery-model-7b\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        LocalApiCompletionResult result = await localApi.CompleteAsync(body, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("Unknown model", result.Body);
    }

    [Theory]
    [InlineData("parux-fast")]
    [InlineData("fast")]
    [InlineData("parux-coding")]
    public async Task CompleteAsync_routes_a_real_role_id_through_the_router_instead_of_rejecting_it(string modelId)
    {
        var fakeRouter = new FakeRoleRouter { Succeed = true };
        using var upstream = new StubHttpMessageHandler(
            "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"model\":\"local-default\",\"choices\":[]}",
            "application/json");
        using var http = new HttpClient(upstream);
        var localApi = new LocalApiServerService(
            _factory.Services.GetRequiredService<LocalRuntimeService>(), fakeRouter, http, ownsRuntime: () => true);
        string body = "{\"model\":\"" + modelId + "\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

        LocalApiCompletionResult result = await localApi.CompleteAsync(body, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        PaRuX.Contracts.RouteRequest routed = Assert.Single(fakeRouter.Calls).Request;
        Assert.Equal(modelId.TrimStart().Replace("parux-", ""), routed.Role, ignoreCase: true);
    }

    [Theory]
    [InlineData("pool-routed", "hot-alias-b")]
    [InlineData("pool-default", "hot-alias-a")]
    [InlineData("no-pool", "local-default")]
    public async Task Provider_model_addresses_a_resident_pool_entry_instead_of_the_logical_single_model_id(
        string scenario, string expectedProviderModel)
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-pool-route-" + Guid.NewGuid().ToString("N"));
        try
        {
            using IDisposable isolatedRoot = LocalAiPaths.UseRootForTests(root);
            if (scenario != "no-pool")
            {
                // llama-server exposes only these hashed aliases while a pool is active, so
                // "local-default" would come back as HTTP 400 "model 'local-default' not found".
                var plan = new HotModelPoolCatalog.Plan(new List<HotModelPoolCatalog.Entry>
                {
                    new("/fake/other.gguf", "hot-alias-a", 1, true),
                    new(scenario == "pool-routed" ? "/fake/model.gguf" : "/fake/unrelated.gguf", "hot-alias-b", 1, true)
                }, 1, 2);
                HotModelPoolCatalog.MarkActive(plan, "test-build");
            }
            var fakeRouter = new FakeRoleRouter { Succeed = true };
            using var upstream = new StubHttpMessageHandler(
                "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"model\":\"x\",\"choices\":[]}", "application/json");
            using var http = new HttpClient(upstream);
            var localApi = new LocalApiServerService(
                _factory.Services.GetRequiredService<LocalRuntimeService>(), fakeRouter, http, ownsRuntime: () => true);

            LocalApiCompletionResult result = await localApi.CompleteAsync(
                "{\"model\":\"parux-auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", CancellationToken.None);

            Assert.Equal(200, result.StatusCode);
            using JsonDocument sent = JsonDocument.Parse(upstream.LastRequestBody!);
            Assert.Equal(expectedProviderModel, sent.RootElement.GetProperty("model").GetString());
            // The caller still sees the model id it asked for.
            using JsonDocument returned = JsonDocument.Parse(result.Body);
            Assert.Equal("parux-auto", returned.RootElement.GetProperty("model").GetString());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Coding_role_uses_the_measured_model_and_falls_back_to_the_assignment_when_it_cannot_load(bool exactLoads)
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-coding-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new TaskStore(root);
            var selection = new CodingModelSelectionService(store,
                _ => Task.FromResult(new List<CandidateEvidenceDto>
                {
                    new() { FileName = "small.gguf", FilePath = @"C:\m\small.gguf", IsCurrentAssignment = true, HardwareCompatible = true },
                    new() { FileName = "large.gguf", FilePath = @"C:\m\large.gguf", IsCurrentAssignment = false, HardwareCompatible = true }
                }),
                () => "hw", root);
            foreach ((string path, bool verified) in Enumerable.Repeat((@"C:\m\small.gguf", false), 3)
                         .Concat(Enumerable.Repeat((@"C:\m\large.gguf", true), 3)))
            {
                AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "fix", Role = "coding" });
                store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
                selection.RecordCodingResolution(path, null);
                store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = verified ? AgentTaskStatus.Completed : AgentTaskStatus.Failed,
                    VerificationStatus = verified ? "pass" : "fail",
                    ResultJson = verified ? "{\"verifiedByCommand\":true,\"agentElapsedSeconds\":90}" : "{\"verificationAvailable\":true}"
                });
            }
            var router = new ExactAwareRouter(exactLoads);
            using var upstream = new StubHttpMessageHandler(
                "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"model\":\"local-default\",\"choices\":[]}",
                "application/json");
            using var http = new HttpClient(upstream);
            var localApi = new LocalApiServerService(
                _factory.Services.GetRequiredService<LocalRuntimeService>(), router, http, ownsRuntime: () => true, selection);

            LocalApiCompletionResult result = await localApi.CompleteAsync(
                "{\"model\":\"parux-coding\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", CancellationToken.None);

            Assert.Equal(200, result.StatusCode);
            Assert.Equal("large.gguf", router.Requests[0].ExactCandidateFileName);
            if (exactLoads)
                Assert.Single(router.Requests);
            else
            {
                Assert.Equal(2, router.Requests.Count);
                Assert.Null(router.Requests[1].ExactCandidateFileName);
                Assert.Equal("coding", router.Requests[1].Role);
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class ExactAwareRouter(bool exactLoads) : IRoleRouter
    {
        public List<RouteRequest> Requests { get; } = new();

        public Task<RouteDecisionDto> ResolveAsync(RouteRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            bool success = exactLoads || request.ExactCandidateFileName == null;
            return Task.FromResult(new RouteDecisionDto
            {
                Mode = RouteMode.Manual, ResolvedRole = "coding", FellBackToStandard = false, Success = success,
                Swapped = false, ModelPath = success ? (request.ExactCandidateFileName ?? "small.gguf") : null,
                Message = success ? "ok" : "does not fit right now", DurationMs = 1, DecidedUtc = DateTime.UtcNow
            });
        }
    }

    /// <summary>Clients enumerate /v1/models to discover which embedding model id to send to
    /// /v1/embeddings, so the local embedding engine has to appear there alongside the text roles.</summary>
    [Fact]
    public void ListModels_advertises_the_openai_compatible_embedding_model()
    {
        var localApi = new LocalApiServerService(
            _factory.Services.GetRequiredService<LocalRuntimeService>(), new FakeRoleRouter());

        string json = System.Text.Json.JsonSerializer.Serialize(localApi.ListModels());

        Assert.Contains(LocalApiEmbeddingsService.ModelId, json, StringComparison.Ordinal);
        Assert.Contains("parux-main", json, StringComparison.Ordinal);
    }

    /// <summary>Official OpenAI SDKs call the single-model retrieve route to validate a configured
    /// model id before sending work, so every id /v1/models advertises must resolve here, and an
    /// unknown id must 404 rather than return a fabricated model entry.</summary>
    [Fact]
    public void GetModel_resolves_every_advertised_id_and_rejects_unknown_ids()
    {
        var localApi = new LocalApiServerService(
            _factory.Services.GetRequiredService<LocalRuntimeService>(), new FakeRoleRouter());

        Assert.NotNull(localApi.GetModel("parux-main"));
        Assert.NotNull(localApi.GetModel(LocalApiEmbeddingsService.ModelId));
        Assert.Equal("parux-coding", localApi.GetModel("parux-coding")!.id);

        Assert.Null(localApi.GetModel("gpt-4o"));
        Assert.Null(localApi.GetModel("PARUX-MAIN"));  // ids are case-sensitive, as in OpenAI
        Assert.Null(localApi.GetModel(""));
        Assert.Null(localApi.GetModel(null));
    }

    /// <summary>The legacy route must validate before doing any model work, and must refuse
    /// streaming explicitly rather than silently downgrading it to a buffered reply - a caller
    /// waiting for SSE frames that never arrive looks like a hang, not like an unsupported option.</summary>
    [Theory]
    [InlineData("not json", "Invalid JSON")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("{}", "prompt")]
    [InlineData("{\"prompt\":\"\"}", "prompt")]
    [InlineData("{\"prompt\":[\"a\",\"b\"]}", "prompt")]
    [InlineData("{\"prompt\":\"hi\",\"stream\":true}", "Streaming is not supported")]
    public async Task CompleteLegacyAsync_rejects_invalid_or_unsupported_requests(string body, string expected)
    {
        var localApi = new LocalApiServerService(
            _factory.Services.GetRequiredService<LocalRuntimeService>(), new FakeRoleRouter());

        LocalApiCompletionResult result = await localApi.CompleteLegacyAsync(body, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains(expected, result.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A router failure has to surface through the legacy route with the same honesty as
    /// the chat route, proving the translation reuses the single real pipeline rather than
    /// reimplementing (and silently diverging from) its error handling.</summary>
    [Fact]
    public async Task CompleteLegacyAsync_reuses_the_chat_pipeline_including_its_honest_failures()
    {
        var fakeRouter = new FakeRoleRouter { Succeed = false, FailureMessage = "simulated: no GPU available" };
        var localApi = new LocalApiServerService(
            _factory.Services.GetRequiredService<LocalRuntimeService>(), fakeRouter);

        LocalApiCompletionResult result = await localApi.CompleteLegacyAsync(
            "{\"model\":\"parux-coding\",\"prompt\":\"hello\"}", CancellationToken.None);

        Assert.Equal(502, result.StatusCode);
        Assert.Contains("simulated: no GPU available", result.Body, StringComparison.Ordinal);
        PaRuX.Contracts.RouteRequest routed = Assert.Single(fakeRouter.Calls).Request;
        Assert.Equal("coding", routed.Role, ignoreCase: true);
    }

    [Fact]
    public async Task CompleteAsync_surfaces_an_honest_router_failure_instead_of_a_generic_error()
    {
        var fakeRouter = new FakeRoleRouter { Succeed = false, FailureMessage = "simulated: no GPU available" };
        var localApi = new LocalApiServerService(_factory.Services.GetRequiredService<LocalRuntimeService>(), fakeRouter);
        const string body = "{\"model\":\"parux-coding\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

        LocalApiCompletionResult result = await localApi.CompleteAsync(body, CancellationToken.None);

        Assert.Equal(502, result.StatusCode);
        Assert.Contains("simulated: no GPU available", result.Body);
    }

    [Fact]
    public async Task StreamAsync_proxies_real_sse_rewrites_the_logical_model_and_preserves_done()
    {
        var fakeRouter = new FakeRoleRouter { Succeed = true };
        const string upstreamSse =
            "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"model\":\"local-default\",\"choices\":[{\"delta\":{\"content\":\"hel\"}}]}\n\n" +
            "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"model\":\"local-default\",\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
            "data: [DONE]\n\n";

        using var upstream = new StubHttpMessageHandler(upstreamSse, "text/event-stream");
        using var http = new HttpClient(upstream);
        var localApi = new LocalApiServerService(
            _factory.Services.GetRequiredService<LocalRuntimeService>(), fakeRouter, http, ownsRuntime: () => true);
        const string body = "{\"model\":\"parux-coding\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        await using var destination = new MemoryStream();

        LocalApiCompletionResult result = await localApi.StreamAsync(body, destination, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        string streamed = Encoding.UTF8.GetString(destination.ToArray());
        Assert.Contains("\"model\":\"parux-coding\"", streamed);
        Assert.DoesNotContain("\"model\":\"local-default\"", streamed);
        Assert.Contains("data: [DONE]\n\n", streamed);

        JsonNode forwarded = JsonNode.Parse(upstream.LastRequestBody!)!;
        Assert.True(forwarded["stream"]!.GetValue<bool>());
        Assert.Equal("local-default", forwarded["model"]!.GetValue<string>());
        Assert.Equal("coding", Assert.Single(fakeRouter.Calls).Request.Role);
    }

    [Fact]
    public async Task StreamAsync_returns_upstream_error_before_writing_any_sse_bytes()
    {
        var fakeRouter = new FakeRoleRouter { Succeed = true };
        using var upstream = new StubHttpMessageHandler(
            "{\"error\":{\"message\":\"provider rejected request\"}}",
            "application/json",
            HttpStatusCode.BadRequest);
        using var http = new HttpClient(upstream);
        var localApi = new LocalApiServerService(
            _factory.Services.GetRequiredService<LocalRuntimeService>(), fakeRouter, http, ownsRuntime: () => true);
        const string body = "{\"model\":\"parux-main\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        await using var destination = new MemoryStream();

        LocalApiCompletionResult result = await localApi.StreamAsync(body, destination, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal(0, destination.Length);
        Assert.Contains("provider rejected request", result.Body);
    }

    [Fact]
    public async Task Chat_completions_endpoint_uses_text_event_stream_for_stream_true()
    {
        var fakeRouter = new FakeRoleRouter { Succeed = true };
        const string upstreamSse =
            "data: {\"id\":\"chatcmpl-endpoint\",\"object\":\"chat.completion.chunk\",\"model\":\"local-default\",\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
            "data: [DONE]\n\n";
        using var upstream = new StubHttpMessageHandler(upstreamSse, "text/event-stream");
        using var providerHttp = new HttpClient(upstream);
        using var configuredFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRoleRouter>();
                services.AddSingleton<IRoleRouter>(fakeRouter);
                services.AddSingleton(providerHttp);
                services.RemoveAll<LocalApiServerService>();
                services.AddSingleton(services => new LocalApiServerService(
                    services.GetRequiredService<LocalRuntimeService>(), fakeRouter, providerHttp,
                    ownsRuntime: () => true));
            });
        });
        using HttpClient client = configuredFactory.CreateClient();
        ServerState state = configuredFactory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        const string body = "{\"model\":\"parux-main\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        string streamed = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"model\":\"parux-main\"", streamed);
        Assert.Contains("data: [DONE]", streamed);
    }

    [Theory]
    [InlineData("buffered")]
    [InlineData("sse")]
    [InlineData("chunks")]
    public async Task Protocol_completion_refuses_an_unowned_runtime_after_successful_routing(string path)
    {
        var router = new FakeRoleRouter { Succeed = true };
        using var upstream = new StubHttpMessageHandler("{}", "application/json");
        using var http = new HttpClient(upstream);
        var localApi = new LocalApiServerService(
            _factory.Services.GetRequiredService<LocalRuntimeService>(), router, http,
            ownsRuntime: () => false);
        string body = JsonSerializer.Serialize(new { model = "parux-main", stream = path != "buffered",
            messages = new[] { new { role = "user", content = "private prompt marker" } } });
        await using var destination = new MemoryStream();
        bool started = false;
        LocalApiCompletionResult result = path switch
        {
            "buffered" => await localApi.CompleteAsync(body, CancellationToken.None),
            "sse" => await localApi.StreamAsync(body, destination, CancellationToken.None),
            _ => await localApi.StreamChunksAsync(body,
                () => { started = true; return Task.CompletedTask; },
                _ => Task.CompletedTask, CancellationToken.None)
        };
        Assert.Single(router.Calls);
        Assert.Equal(502, result.StatusCode);
        Assert.Contains("prompt was not sent", result.Body);
        Assert.Null(upstream.LastRequestBody);
        Assert.Equal(0, destination.Length);
        Assert.False(started);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _payload;
        private readonly string _contentType;
        private readonly HttpStatusCode _statusCode;

        public StubHttpMessageHandler(
            string payload, string contentType, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _payload = payload;
            _contentType = contentType;
            _statusCode = statusCode;
        }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_payload, Encoding.UTF8, _contentType)
            };
        }
    }
}
