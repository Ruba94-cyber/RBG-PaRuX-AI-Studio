using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.AutoLab;
using PaRuX.Server.EvalLab;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Covers PaRuX AutoLab: experiment creation
/// capturing real model identity, compare/recommend logic, and Promote's reuse of the existing
/// Phase 6 role-override mechanism. Uses FakeLocalCompletionClient for the underlying Eval Lab
/// run (same hermetic-test convention as EvalRunnerServiceTests) and the real AiProfileService
/// resolved from the WebApplicationFactory container (read-only status check, same pattern
/// ApiSecurityTests.cs's own Chat_proxy_injects_relevant_memory_and_rag_context_selectively test
/// already uses) - no real model load required.</summary>
public sealed class AutoLabTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly string _dbDirectory = Path.Combine(Path.GetTempPath(), "parux-autolab-tests-" + Guid.NewGuid().ToString("N"));

    public AutoLabTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_dbDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDirectory, true); } catch { }
    }

    private (AutoLabService autoLab, EvalStore evalStore, FakeLocalCompletionClient client) NewService()
    {
        var evalStore = new EvalStore(_dbDirectory);
        var client = new FakeLocalCompletionClient();
        var runner = new EvalRunnerService(client, evalStore);
        var autoLabStore = new AutoLabStore(_dbDirectory);
        AiProfileService profiles = _factory.Services.GetRequiredService<AiProfileService>();
        SmartRouterService router = _factory.Services.GetRequiredService<SmartRouterService>();
        return (new AutoLabService(autoLabStore, evalStore, runner, profiles, router), evalStore, client);
    }

    [Fact]
    public async Task CreateExperimentAsync_rejects_an_unknown_dataset()
    {
        (AutoLabService autoLab, _, _) = NewService();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            autoLab.CreateExperimentAsync(new CreateExperimentRequest { DatasetId = "does-not-exist" }, CancellationToken.None));
    }

    [Fact]
    public async Task CreateComparisonSweepAsync_rejects_an_unknown_dataset_before_touching_the_router()
    {
        // Safe hermetically: returns before ever calling SmartRouterService.ResolveAsync, which
        // (per SmartRouterTests' own documented hazard) can reach real process-control code via
        // its deterministic fallback-to-standard policy on a developer machine with a real model
        // file present.
        (AutoLabService autoLab, _, _) = NewService();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            autoLab.CreateComparisonSweepAsync(new CreateComparisonSweepRequest { DatasetId = "does-not-exist", Roles = new List<string> { "fast" } }, CancellationToken.None));
    }

    [Fact]
    public async Task CreateComparisonSweepAsync_rejects_an_empty_role_list_before_touching_the_router()
    {
        (AutoLabService autoLab, EvalStore evalStore, _) = NewService();
        EvalDatasetDto dataset = evalStore.CreateDataset(new CreateEvalDatasetRequest { Name = "Sweep validation dataset" });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            autoLab.CreateComparisonSweepAsync(new CreateComparisonSweepRequest { DatasetId = dataset.Id, Roles = new List<string>() }, CancellationToken.None));
    }

    [Fact]
    public async Task CreateExperimentAsync_captures_a_real_model_path_and_runs_the_real_eval_dataset()
    {
        (AutoLabService autoLab, EvalStore evalStore, FakeLocalCompletionClient client) = NewService();
        EvalDatasetDto dataset = evalStore.CreateDataset(new CreateEvalDatasetRequest { Name = "AutoLab test dataset" });
        evalStore.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "2+2?", ExpectedBehavior = "Says 4" });
        client.EnqueueOk("4");
        client.EnqueueOk("PASS - correctly answered 4");

        ExperimentDto experiment = await autoLab.CreateExperimentAsync(new CreateExperimentRequest { DatasetId = dataset.Id, Name = "Exp A" }, CancellationToken.None);

        Assert.Equal("Exp A", experiment.Name);
        Assert.False(string.IsNullOrWhiteSpace(experiment.ModelPath));
        Assert.False(string.IsNullOrWhiteSpace(experiment.EvalRunId));

        // The run executes synchronously enough on a fake (non-networked) client for a single
        // case that it should already be done by the time CreateExperimentAsync returns control
        // back through the fire-and-forget Task.Run - poll briefly to avoid a flaky race either way.
        EvalRunDto? run = null;
        for (int i = 0; i < 50 && (run == null || run.Status == EvalRunStatus.Running); i++)
        {
            await Task.Delay(20);
            run = evalStore.GetRun(experiment.EvalRunId);
        }
        Assert.Equal(EvalRunStatus.Completed, run?.Status);
        Assert.Equal(1, run!.PassCount);
    }

    /// <summary>2026-08-27 (third pass): now that Phase 12's own two gaps (--lora runtime
    /// support, then the GGUF-conversion pipeline) are both closed, an experiment additionally
    /// captures whichever real adapter LocalAiPaths.GetRoleAdapter reports for the active role -
    /// the same file EnsureRoleLoadedAsync's own JIT path already loads. Read-only, so this is
    /// exactly as hermetically safe as the pre-existing StandardModel/UncensoredModel capture this
    /// same method already does - it just asserts the wiring matches the real function's own
    /// answer, not a specific value (which varies by machine, same reasoning as ModelPath
    /// above).</summary>
    [Fact]
    public async Task CreateExperimentAsync_captures_the_real_role_adapter_path()
    {
        (AutoLabService autoLab, EvalStore evalStore, FakeLocalCompletionClient client) = NewService();
        EvalDatasetDto dataset = evalStore.CreateDataset(new CreateEvalDatasetRequest { Name = "AutoLab adapter test dataset" });
        evalStore.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "2+2?", ExpectedBehavior = "Says 4" });
        client.EnqueueOk("4");
        client.EnqueueOk("PASS - correctly answered 4");

        AiProfileService profiles = _factory.Services.GetRequiredService<AiProfileService>();
        AiProfileStatus profile = await profiles.GetStatusAsync(CancellationToken.None);
        bool uncensored = string.Equals(profile.TextProfile, "UNCENSORED", StringComparison.OrdinalIgnoreCase);
        string? expectedAdapter = LocalAiPaths.GetRoleAdapter(uncensored ? "uncensored" : "standard");

        ExperimentDto experiment = await autoLab.CreateExperimentAsync(new CreateExperimentRequest { DatasetId = dataset.Id }, CancellationToken.None);

        Assert.Equal(expectedAdapter, experiment.AdapterPath);
    }

    [Fact]
    public void Compare_recommends_the_highest_pass_rate_among_completed_runs_only()
    {
        (AutoLabService autoLab, EvalStore evalStore, _) = NewService();
        EvalDatasetDto dataset = evalStore.CreateDataset(new CreateEvalDatasetRequest { Name = "d" });

        EvalRunDto goodRun = evalStore.CreateRun(dataset.Id);
        evalStore.UpdateRunStatus(goodRun.Id, EvalRunStatus.Completed, passCount: 9, failCount: 1, inconclusiveCount: 0);
        EvalRunDto badRun = evalStore.CreateRun(dataset.Id);
        evalStore.UpdateRunStatus(badRun.Id, EvalRunStatus.Completed, passCount: 2, failCount: 8, inconclusiveCount: 0);
        EvalRunDto stillRunning = evalStore.CreateRun(dataset.Id); // left as "running" - must never be recommended

        var autoLabStore = new AutoLabStore(_dbDirectory);
        ExperimentDto good = autoLabStore.Create(NewExperiment(dataset.Id, goodRun.Id, "Good model"));
        ExperimentDto bad = autoLabStore.Create(NewExperiment(dataset.Id, badRun.Id, "Bad model"));
        ExperimentDto running = autoLabStore.Create(NewExperiment(dataset.Id, stillRunning.Id, "Still running"));

        ExperimentComparisonResponse comparison = autoLab.Compare(new[] { good.Id, bad.Id, running.Id });

        Assert.Equal(3, comparison.Experiments.Count);
        Assert.Equal(good.Id, comparison.RecommendedExperimentId);
        Assert.Contains("90%", comparison.RecommendationReason);
    }

    /// <summary>Compare must surface each experiment's own captured AdapterPath verbatim - the
    /// real, honest way to see an adapter-vs-no-adapter (or adapter-A-vs-adapter-B) side by side,
    /// using the existing comparison mechanism rather than a second one.</summary>
    [Fact]
    public void Compare_surfaces_each_experiments_own_adapter_path()
    {
        (AutoLabService autoLab, EvalStore evalStore, _) = NewService();
        EvalDatasetDto dataset = evalStore.CreateDataset(new CreateEvalDatasetRequest { Name = "adapter compare dataset" });
        EvalRunDto withAdapterRun = evalStore.CreateRun(dataset.Id);
        evalStore.UpdateRunStatus(withAdapterRun.Id, EvalRunStatus.Completed, passCount: 8, failCount: 2, inconclusiveCount: 0);
        EvalRunDto noAdapterRun = evalStore.CreateRun(dataset.Id);
        evalStore.UpdateRunStatus(noAdapterRun.Id, EvalRunStatus.Completed, passCount: 5, failCount: 5, inconclusiveCount: 0);

        var autoLabStore = new AutoLabStore(_dbDirectory);
        ExperimentDto withAdapter = autoLabStore.Create(NewExperiment(dataset.Id, withAdapterRun.Id, "With adapter", adapterPath: "C:\\fake\\adapter.gguf"));
        ExperimentDto withoutAdapter = autoLabStore.Create(NewExperiment(dataset.Id, noAdapterRun.Id, "Without adapter"));

        ExperimentComparisonResponse comparison = autoLab.Compare(new[] { withAdapter.Id, withoutAdapter.Id });

        Assert.Equal("C:\\fake\\adapter.gguf", comparison.Experiments.Single(e => e.ExperimentId == withAdapter.Id).AdapterPath);
        Assert.Null(comparison.Experiments.Single(e => e.ExperimentId == withoutAdapter.Id).AdapterPath);
        Assert.Equal(withAdapter.Id, comparison.RecommendedExperimentId);
    }

    [Fact]
    public void Compare_recommends_nothing_when_every_case_in_every_completed_run_errored()
    {
        // Real bug caught by live testing (2026-08-26) against the actual running server with no
        // model loaded: every case errors, so PassCount=FailCount=InconclusiveCount=0 - identical
        // to an empty dataset. A 0/0 "completed" run must never be recommended as if it were a
        // genuine 0% pass rate.
        (AutoLabService autoLab, EvalStore evalStore, _) = NewService();
        EvalDatasetDto dataset = evalStore.CreateDataset(new CreateEvalDatasetRequest { Name = "d" });
        EvalRunDto allErrored = evalStore.CreateRun(dataset.Id);
        evalStore.UpdateRunStatus(allErrored.Id, EvalRunStatus.Completed, passCount: 0, failCount: 0, inconclusiveCount: 0);

        var autoLabStore = new AutoLabStore(_dbDirectory);
        ExperimentDto experiment = autoLabStore.Create(NewExperiment(dataset.Id, allErrored.Id, "All errored"));

        ExperimentComparisonResponse comparison = autoLab.Compare(new[] { experiment.Id });

        Assert.Null(comparison.RecommendedExperimentId);
    }

    [Fact]
    public void Compare_recommends_nothing_when_no_run_has_completed_yet()
    {
        (AutoLabService autoLab, EvalStore evalStore, _) = NewService();
        EvalDatasetDto dataset = evalStore.CreateDataset(new CreateEvalDatasetRequest { Name = "d" });
        EvalRunDto run = evalStore.CreateRun(dataset.Id); // still "running"

        var autoLabStore = new AutoLabStore(_dbDirectory);
        ExperimentDto experiment = autoLabStore.Create(NewExperiment(dataset.Id, run.Id, "In progress"));

        ExperimentComparisonResponse comparison = autoLab.Compare(new[] { experiment.Id });

        Assert.Null(comparison.RecommendedExperimentId);
    }

    // Promote's success path calls LocalAiPaths.SetRoleOverride, which writes to a real, fixed
    // %LOCALAPPDATA%\PaRuX\model-role-overrides.json - there is no test-injectable root for it
    // (LocalAiPaths is fully static), so a hermetic test must never reach that write, the exact
    // same hazard ModelRolesTests.cs already documents and scopes around. Both cases below fail
    // before SetRoleOverride is ever called; the success path needs live verification instead.

    [Fact]
    public void Promote_rejects_an_invalid_role_before_ever_touching_the_real_override_file()
    {
        (AutoLabService autoLab, EvalStore evalStore, _) = NewService();
        EvalDatasetDto dataset = evalStore.CreateDataset(new CreateEvalDatasetRequest { Name = "d" });
        EvalRunDto run = evalStore.CreateRun(dataset.Id);
        var autoLabStore = new AutoLabStore(_dbDirectory);
        ExperimentDto experiment = autoLabStore.Create(NewExperiment(dataset.Id, run.Id, "x"));

        Assert.Throws<InvalidDataException>(() => autoLab.Promote(experiment.Id, "not-a-real-role"));
    }

    [Fact]
    public async Task Sweep_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/autolab/sweep",
            new CreateComparisonSweepRequest { DatasetId = "x", Roles = new List<string> { "fast" } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Promote_rejects_an_unknown_experiment_id()
    {
        (AutoLabService autoLab, _, _) = NewService();
        Assert.Throws<InvalidDataException>(() => autoLab.Promote("does-not-exist", "standard"));
    }

    private static ExperimentDto NewExperiment(string datasetId, string runId, string name, string modelPath = "C:\\fake\\model.gguf", string? adapterPath = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Name = name, DatasetId = datasetId, EvalRunId = runId,
        ModelProfile = "STANDARD", ModelPath = modelPath, AdapterPath = adapterPath, CreatedUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task Experiments_list_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/autolab/experiments");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Experiments_list_endpoint_accepts_the_desktop_session_token()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/autolab/experiments");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
