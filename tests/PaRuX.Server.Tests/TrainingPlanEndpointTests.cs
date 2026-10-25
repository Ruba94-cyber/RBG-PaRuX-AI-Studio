using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The guided fine-tune's checks, over HTTP.
///
/// The property worth pinning is that the check is not merely advisory. A caller that skips the
/// wizard and posts a job directly must get the same refusal, because the alternative is the worse
/// outcome the check exists to prevent: a model loaded, VRAM allocated, and a Python traceback
/// several minutes later. Warnings must still get through - they describe a run that will work but
/// may disappoint, which is the user's call, not the server's.
/// </summary>
public sealed class TrainingPlanEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parux-training-plan-" + Guid.NewGuid().ToString("N"));

    public TrainingPlanEndpointTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private HttpClient Authenticated()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    private string WriteDataset(string name, IEnumerable<string> lines)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private string ChatDataset(int examples = 400) => WriteDataset("chat.jsonl",
        Enumerable.Range(0, examples).Select(index =>
            "{\"messages\":[{\"role\":\"user\",\"content\":\"q" + index + "\"},"
            + "{\"role\":\"assistant\",\"content\":\"a" + index + "\"}]}"));

    private string PreferenceDataset(int examples = 400) => WriteDataset("pref.jsonl",
        Enumerable.Range(0, examples).Select(index =>
            "{\"prompt\":\"q" + index + "\",\"chosen\":\"good\",\"rejected\":\"bad\"}"));

    [Fact]
    public async Task Planning_requires_a_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/training/plan", new TrainingPlanRequest { TrainJsonlPath = "x.jsonl" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_matching_dataset_and_method_can_start_and_carries_recommendations()
    {
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/training/plan",
            new TrainingPlanRequest
            {
                TrainJsonlPath = ChatDataset(),
                Method = TrainingMethod.Sft,
                BaseModel = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
                LoraRank = 16,
                MaxSteps = 150,
                LearningRate = 0.0002
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        TrainingPlanResult? plan = await response.Content.ReadFromJsonAsync<TrainingPlanResult>();

        Assert.NotNull(plan);
        Assert.True(plan!.CanStart);
        Assert.True(plan.DatasetSupportsSupervised);
        Assert.Equal(400, plan.DatasetExamples);
        Assert.True(plan.RecommendedMaxSteps > 0);
        Assert.False(string.IsNullOrWhiteSpace(plan.RecommendationRationale));
    }

    [Fact]
    public async Task A_dataset_that_does_not_match_the_method_is_blocking_and_names_the_one_that_fits()
    {
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/training/plan",
            new TrainingPlanRequest
            {
                TrainJsonlPath = ChatDataset(),
                Method = TrainingMethod.Dpo,
                BaseModel = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
                LoraRank = 16,
                MaxSteps = 150,
                LearningRate = 0.000005
            });

        TrainingPlanResult? plan = await response.Content.ReadFromJsonAsync<TrainingPlanResult>();

        Assert.False(plan!.CanStart);
        Assert.Contains(plan.Findings, finding => finding.Severity == "Blocking" && finding.Field == "dataset");
        Assert.Contains(plan.Findings, finding => finding.Message.Contains("supervised fine-tuning would accept it"));
    }

    [Fact]
    public async Task An_unknown_method_is_refused_by_name()
    {
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/training/plan",
            new TrainingPlanRequest { TrainJsonlPath = ChatDataset(10), Method = "not-a-method" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Submitting_a_job_that_cannot_succeed_is_refused_with_the_plan_attached()
    {
        // The check is not merely advisory: a caller that skipped the wizard gets the same refusal
        // rather than a traceback minutes into a run.
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/training/jobs",
            new TrainingJobRequest
            {
                TrainJsonlPath = PreferenceDataset(),
                Method = TrainingMethod.Sft,
                BaseModel = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
                LoraRank = 16,
                MaxSteps = 150,
                LearningRate = 0.0002
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("cannot succeed", body);
        Assert.Contains("preference tuning would accept it", body);
    }

    [Fact]
    public async Task A_run_that_will_merely_disappoint_is_not_refused()
    {
        // 10 steps over 400 examples is a warning, not a blocker. Refusing it would be the server
        // overruling a choice that is the user's to make.
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/training/plan",
            new TrainingPlanRequest
            {
                TrainJsonlPath = ChatDataset(),
                Method = TrainingMethod.Sft,
                BaseModel = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
                LoraRank = 16,
                MaxSteps = 10,
                LearningRate = 0.0002
            });

        TrainingPlanResult? plan = await response.Content.ReadFromJsonAsync<TrainingPlanResult>();

        Assert.True(plan!.CanStart);
        Assert.Contains(plan.Findings, finding => finding.Severity == "Warning" && finding.Field == "maxSteps");
    }

    [Fact]
    public async Task A_missing_training_file_is_blocking_rather_than_an_empty_plan()
    {
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/training/plan",
            new TrainingPlanRequest { TrainJsonlPath = Path.Combine(_directory, "absent.jsonl") });

        TrainingPlanResult? plan = await response.Content.ReadFromJsonAsync<TrainingPlanResult>();

        Assert.False(plan!.CanStart);
        Assert.Contains(plan.Findings, finding => finding.Severity == "Blocking");
    }

    // ---- Live metrics ----

    [Theory]
    [InlineData("{'loss': 11.54, 'grad_norm': 24.55, 'learning_rate': 0.0002, 'epoch': 1.0}")]
    [InlineData("{'loss': '11.54', 'grad_norm': '24.55', 'learning_rate': '0.0002', 'epoch': '1.0'}")]
    public void Trainer_metrics_are_read_whether_or_not_the_values_are_quoted(string logLine)
    {
        // Which of the two shapes appears depends on the TRL and transformers versions in the
        // managed venv. A parser that handles only one reports no progress at all on the other,
        // which looks like a hung job rather than a parsing problem.
        TrainingMetricsParser.Sample sample = TrainingMetricsParser.Parse(logLine);

        Assert.Equal(11.54, sample.Loss!.Value, 3);
        Assert.Equal(24.55, sample.GradientNorm!.Value, 3);
        Assert.Equal(0.0002, sample.LearningRate!.Value, 6);
        Assert.Equal(1.0, sample.Epoch!.Value, 3);
    }

    [Fact]
    public void A_line_carrying_no_metrics_produces_nothing_rather_than_zeros()
    {
        TrainingMetricsParser.Sample sample = TrainingMetricsParser.Parse("Loading checkpoint shards: 100%");

        Assert.False(sample.HasAnything);
        Assert.Null(sample.Loss);
        Assert.Null(sample.GradientNorm);
    }

    [Fact]
    public void Metric_parsing_stays_culture_invariant()
    {
        // A real bug this codebase already hit: under an Italian culture "." is a thousands
        // separator, so a culture-sensitive parse read a loss of 10.98 as 1098.
        System.Globalization.CultureInfo original = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("it-IT");
        try
        {
            TrainingMetricsParser.Sample sample = TrainingMetricsParser.Parse("{'loss': 10.98}");
            Assert.Equal(10.98, sample.Loss!.Value, 5);
        }
        finally { Thread.CurrentThread.CurrentCulture = original; }
    }
}
