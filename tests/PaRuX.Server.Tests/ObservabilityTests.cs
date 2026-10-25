using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.AutoLab;
using PaRuX.Server.Automation;
using PaRuX.Server.EvalLab;
using PaRuX.Server.Orchestration;
using PaRuX.Server.Workflow;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Covers Observability: merging real events from existing owners into one chronological,
/// limited timeline without creating a second event store.</summary>
public sealed class ObservabilityTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly string _dbDirectory = Path.Combine(Path.GetTempPath(), "parux-observability-tests-" + Guid.NewGuid().ToString("N"));

    public ObservabilityTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_dbDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDirectory, true); } catch { }
    }

    private sealed class Harness
    {
        public required ObservabilityService Observability { get; init; }
        public required TaskStore Tasks { get; init; }
        public required AdapterManagerService Adapters { get; init; }
        public required AutoLabService AutoLab { get; init; }
        public required AutoLabStore AutoLabStore { get; init; }
        public required EvalStore Eval { get; init; }
        public required ArtifactStore Artifacts { get; init; }
        public required AutomationStore Automations { get; init; }
        public required WorkflowStore Workflows { get; init; }
    }

    private Harness NewService()
    {
        var tasks = new TaskStore(_dbDirectory);
        var models = new ModelDownloadService();
        var datasets = new DatasetDownloadService();
        var training = _factory.Services.GetRequiredService<TrainingLabService>(); // only List(); no job is started
        var adapters = new AdapterManagerService(
            new AdapterStore(_dbDirectory),
            new LoraGgufConversionService(Path.Combine(_dbDirectory, "lora-conversion-runtime"), Path.Combine(_dbDirectory, "lora-conversion-scripts")));
        var evalStore = new EvalStore(_dbDirectory);
        var runner = new EvalRunnerService(new FakeLocalCompletionClient(), evalStore);
        var autoLabStore = new AutoLabStore(_dbDirectory);
        var autoLab = new AutoLabService(autoLabStore, evalStore, runner,
            _factory.Services.GetRequiredService<AiProfileService>(), _factory.Services.GetRequiredService<SmartRouterService>());
        var artifacts = new ArtifactStore(_dbDirectory);
        var automations = new AutomationStore(_dbDirectory);
        var workflows = new WorkflowStore(_dbDirectory);
        var observability = new ObservabilityService(
            tasks, models, datasets, training, adapters, autoLab, artifacts, automations, workflows);

        return new Harness
        {
            Observability = observability,
            Tasks = tasks,
            Adapters = adapters,
            AutoLab = autoLab,
            AutoLabStore = autoLabStore,
            Eval = evalStore,
            Artifacts = artifacts,
            Automations = automations,
            Workflows = workflows
        };
    }

    [Fact]
    public void GetTimeline_merges_tasks_and_adapters_by_recency()
    {
        Harness h = NewService();

        h.Tasks.Create(new CreateAgentTaskRequest { Goal = "Observability test task" });
        Thread.Sleep(10); // ensure a distinguishable UpdatedUtc ordering
        h.Adapters.Register(new RegisterAdapterRequest { Name = "Observability test adapter", BaseModel = "m", AdapterPath = _dbDirectory });

        List<TimelineEntry> timeline = h.Observability.GetTimeline(100);

        Assert.Contains(timeline, e => e.Category == "task" && e.Title == "Observability test task");
        Assert.Contains(timeline, e => e.Category == "adapter" && e.Title == "Observability test adapter");
        Assert.Equal("adapter", timeline[0].Category);
    }

    [Fact]
    public void GetTimeline_respects_the_limit()
    {
        Harness h = NewService();
        for (int i = 0; i < 5; i++) h.Tasks.Create(new CreateAgentTaskRequest { Goal = "Task " + i });

        List<TimelineEntry> timeline = h.Observability.GetTimeline(3);

        Assert.Equal(3, timeline.Count);
    }

    [Fact]
    public void GetTimeline_includes_experiments()
    {
        Harness h = NewService();
        EvalDatasetDto dataset = h.Eval.CreateDataset(new CreateEvalDatasetRequest { Name = "d" });
        EvalRunDto run = h.Eval.CreateRun(dataset.Id);
        h.AutoLabStore.Create(new ExperimentDto
        {
            Id = Guid.NewGuid().ToString("N"), Name = "Observability test experiment",
            DatasetId = dataset.Id, EvalRunId = run.Id, ModelProfile = "STANDARD", ModelPath = "x",
            CreatedUtc = DateTime.UtcNow,
        });

        List<TimelineEntry> timeline = h.Observability.GetTimeline(100);

        Assert.Contains(timeline, e => e.Category == "experiment" && e.Title == "Observability test experiment");
    }

    [Fact]
    public void GetTimeline_includes_artifact_automation_and_workflow_records_from_their_real_stores()
    {
        Harness h = NewService();
        ArtifactDto artifact = h.Artifacts.Create(new CreateArtifactRequest
        {
            ArtifactType = "report",
            Title = "Observed artifact",
            ContentRef = Path.Combine(_dbDirectory, "report.md"),
            Source = "test"
        });
        h.Artifacts.SetStatus(artifact.Id, ArtifactStore.StatusFinal);

        AutomationDto automation = h.Automations.Create(new CreateAutomationRequest
        {
            Name = "Observed automation",
            TriggerType = AutomationTriggerType.Schedule,
            TriggerConfigJson = "{\"intervalSeconds\":3600}",
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"Do scheduled work\"}"
        });

        WorkflowDto workflow = h.Workflows.Create(new CreateWorkflowRequest
        {
            Name = "Observed workflow",
            Steps = new List<WorkflowStepTemplate> { new() { Name = "Step", Goal = "Do work" } }
        });

        List<TimelineEntry> timeline = h.Observability.GetTimeline(100);

        TimelineEntry artifactEntry = Assert.Single(timeline.Where(e => e.Category == "artifact" && e.RelatedId == artifact.Id));
        Assert.Contains("final", artifactEntry.Detail);
        Assert.Contains("v1", artifactEntry.Detail);

        TimelineEntry automationEntry = Assert.Single(timeline.Where(e => e.Category == "automation" && e.RelatedId == automation.Id));
        Assert.Contains("schedule", automationEntry.Detail);

        TimelineEntry workflowEntry = Assert.Single(timeline.Where(e => e.Category == "workflow" && e.RelatedId == workflow.Id));
        Assert.Contains("v1", workflowEntry.Title);
        Assert.Contains("1 step", workflowEntry.Detail);
    }

    [Fact]
    public async Task Timeline_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/observability/timeline");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Timeline_endpoint_accepts_the_desktop_session_token()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/observability/timeline");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
