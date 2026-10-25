using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Model Compare coverage is deliberately scoped to what's genuinely safe and deterministic in a
/// hermetic `dotnet test` run: request validation, id-based lookup semantics, and the available-
/// models listing (a pure filesystem read). It never calls ModelCompareService.Create with a
/// request that would actually resolve to an existing model file, because a successful Create
/// schedules a real background run (ModelCompareService.RunAsync -&gt;
/// LocalAiProcessManager.StartArbitraryTextModelAsync), which on a machine that actually has the
/// local AI runtime installed (this dev machine does) would really launch llama-server. That real
/// end-to-end path is intentionally left to manual verification, not this automated suite - see
/// the session notes on why an unsupervised test run must not spin up real GPU processes.
/// </summary>
public sealed class ModelCompareServiceTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public ModelCompareServiceTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private ModelCompareService Service => _factory.Services.GetRequiredService<ModelCompareService>();

    [Fact]
    public void Create_rejects_an_empty_prompt_without_scheduling_a_run()
    {
        var request = new ModelCompareRequest
        {
            Prompt = "   ",
            ModelA = new ModelCompareCandidate { Kind = "standard" },
            ModelB = new ModelCompareCandidate { Kind = "uncensored" }
        };
        Assert.Throws<InvalidDataException>(() => Service.Create(request));
    }

    [Fact]
    public void Create_rejects_an_unknown_candidate_kind()
    {
        var request = new ModelCompareRequest
        {
            Prompt = "Explain recursion.",
            ModelA = new ModelCompareCandidate { Kind = "bogus" },
            ModelB = new ModelCompareCandidate { Kind = "uncensored" }
        };
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => Service.Create(request));
        Assert.Contains("Model A", ex.Message);
    }

    [Fact]
    public void Create_rejects_a_path_candidate_pointing_at_a_nonexistent_file()
    {
        // Model A must resolve to a real, existing file so the check reaches Model B - using
        // Kind="standard"/"uncensored" here would depend on LocalAiPaths.StandardModel actually
        // existing on whatever machine runs this test (true on a dev box with the runtime
        // installed, false on a clean CI runner), making the test's outcome environment-dependent.
        // A self-created temp file keeps it hermetic everywhere.
        string existingFile = Path.Combine(Path.GetTempPath(), "parux-compare-test-existing-" + Guid.NewGuid().ToString("N") + ".gguf");
        File.WriteAllBytes(existingFile, Array.Empty<byte>());
        try
        {
            var request = new ModelCompareRequest
            {
                Prompt = "Explain recursion.",
                ModelA = new ModelCompareCandidate { Kind = "path", ModelPath = existingFile },
                ModelB = new ModelCompareCandidate
                {
                    Kind = "path",
                    ModelPath = Path.Combine(Path.GetTempPath(), "parux-compare-test-missing-" + Guid.NewGuid().ToString("N") + ".gguf")
                }
            };
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => Service.Create(request));
            Assert.Contains("Model B", ex.Message);
            Assert.Contains("not found", ex.Message);
        }
        finally
        {
            try { File.Delete(existingFile); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Create_rejects_a_path_candidate_with_no_path_supplied()
    {
        var request = new ModelCompareRequest
        {
            Prompt = "Explain recursion.",
            ModelA = new ModelCompareCandidate { Kind = "path", ModelPath = "" },
            ModelB = new ModelCompareCandidate { Kind = "uncensored" }
        };
        Assert.Throws<InvalidDataException>(() => Service.Create(request));
    }

    [Fact]
    public void Get_returns_null_and_cancel_returns_false_for_an_unknown_run_id()
    {
        string unknownId = Guid.NewGuid().ToString("N");
        Assert.Null(Service.Get(unknownId));
        Assert.False(Service.Cancel(unknownId));
    }

    [Fact]
    public void Available_models_listing_never_throws_and_has_no_duplicate_paths()
    {
        // Exercises the real machine state (same "honest either way" pattern as the Speech/
        // Training engine status tests) - a pure filesystem read, no process is ever spawned.
        IReadOnlyList<ModelCompareAvailableModel> models = Service.ListAvailableModels();

        Assert.NotNull(models);
        var distinctPaths = models.Select(m => m.ModelPath.ToLowerInvariant()).Distinct().ToList();
        Assert.Equal(models.Count, distinctPaths.Count);
        Assert.All(models, m => Assert.Contains(m.Kind, new[] { "standard", "uncensored", "path" }));
        Assert.All(models, m => Assert.False(string.IsNullOrWhiteSpace(m.Label)));
    }

    [Fact]
    public async Task Compare_start_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/compare", new ModelCompareRequest
        {
            Prompt = "hello",
            ModelA = new ModelCompareCandidate { Kind = "standard" },
            ModelB = new ModelCompareCandidate { Kind = "uncensored" }
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Compare_start_endpoint_rejects_an_invalid_request_body()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/compare", new ModelCompareRequest
        {
            Prompt = "", // invalid: empty prompt
            ModelA = new ModelCompareCandidate { Kind = "standard" },
            ModelB = new ModelCompareCandidate { Kind = "uncensored" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Compare_status_endpoint_returns_not_found_for_an_unknown_id()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/compare/" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Compare_cancel_endpoint_returns_not_found_for_an_unknown_id()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsync("/api/v1/compare/" + Guid.NewGuid().ToString("N") + "/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Available_models_endpoint_requires_a_token_and_returns_a_valid_shape()
    {
        using HttpClient anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/compare/available-models")).StatusCode);

        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/compare/available-models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<ModelCompareAvailableModel>? models = await response.Content.ReadFromJsonAsync<List<ModelCompareAvailableModel>>();
        Assert.NotNull(models);
    }

    [Fact]
    public void Compare_status_default_shape_matches_the_documented_status_values()
    {
        var status = new ModelCompareStatus();
        Assert.Equal("queued", status.Status);
        Assert.Null(status.ResponseA);
        Assert.Null(status.ResponseB);
        Assert.Null(status.ModelALabel);
        Assert.Null(status.ModelBLabel);
    }
}
