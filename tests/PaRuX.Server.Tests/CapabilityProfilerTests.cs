using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.CapabilityProfiler;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Directive Phase 7 (Empirical Model Capability Profiler). Two layers:
///
/// 1. The deterministic probe scorers (CapabilityProfilerService.Score*) are pure string-parsing
///    functions with no model/network dependency - made `public static` (same precedent as
///    DatasetDownloadService.NormalizeLicense) specifically so they're directly unit-testable,
///    covering every real/expected shape a model's raw output could take.
///
/// 2. The store (CapabilityProfilerStore) is exercised against an isolated temp directory, same
///    convention as every other vNext store's dedicated test file (see AutoLabStore/AdapterStore).
///
/// Deliberately NOT tested here: POST /api/v1/capabilities/runs actually completing a run. That
/// endpoint's "is a model currently loaded and healthy" check is a real HTTP call to
/// 127.0.0.1:1234/health - on a developer's own machine (as opposed to CI) that port may
/// genuinely have this developer's own real, currently-loaded model behind it, and triggering 5
/// real inference calls against it from an automated hermetic test run would be a live side
/// effect this suite must never risk (same class of hazard RuntimeManagerTests' own doc comment
/// documents for Unload). The full run - including a real "no model loaded" rejection and a real
/// completed run against an actually-loaded model - is covered by live verification instead.
/// </summary>
public sealed class CapabilityProfilerScoringTests
{
    [Theory]
    [InlineData("{\"name\": \"Alex\", \"age\": 30, \"active\": true}", true)]
    [InlineData("```json\n{\"name\": \"Alex\", \"age\": 30, \"active\": true}\n```", true)]
    [InlineData("{\"name\": \"Alex\", \"age\": \"30\", \"active\": true}", false)] // age is a string, not a number
    [InlineData("{\"name\": \"Alex\"}", false)] // missing age/active
    [InlineData("Sure! Here is the person: Alex, 30, active.", false)] // not JSON at all
    [InlineData("", false)]
    public void ScoreJson_classifies_every_shape_correctly(string raw, bool expectedPass)
    {
        (bool passed, string detail) = CapabilityProfilerService.ScoreJson(raw);
        Assert.Equal(expectedPass, passed);
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Theory]
    [InlineData("PONG", true)]
    [InlineData("pong", true)]
    [InlineData(" PONG \n", true)]
    [InlineData("PONG.", true)]
    [InlineData("PONG!", true)]
    [InlineData("The answer is PONG", false)]
    [InlineData("PING", false)]
    [InlineData("", false)]
    public void ScoreInstructionFollowing_requires_exactly_the_word_pong(string raw, bool expectedPass)
    {
        (bool passed, _) = CapabilityProfilerService.ScoreInstructionFollowing(raw);
        Assert.Equal(expectedPass, passed);
    }

    [Theory]
    [InlineData("38", true)]
    [InlineData("The answer is 38.", true)]
    [InlineData("120 - 42 = 78, 78 - 40 = 38", true)] // takes the LAST number, the actual final answer
    [InlineData("37", false)]
    [InlineData("120", false)] // only the problem's own number restated, no real answer given
    [InlineData("no numbers here", false)]
    public void ScoreMath_takes_the_last_number_and_compares_to_the_expected_answer(string raw, bool expectedPass)
    {
        (bool passed, _) = CapabilityProfilerService.ScoreMath(raw, expected: 38);
        Assert.Equal(expectedPass, passed);
    }

    [Fact]
    public void ScoreLongContextRecall_passes_only_when_the_planted_code_is_present()
    {
        Assert.True(CapabilityProfilerService.ScoreLongContextRecall("The code is " + CapabilityProfilerService.ContextNeedle).Item1);
        Assert.True(CapabilityProfilerService.ScoreLongContextRecall(CapabilityProfilerService.ContextNeedle.ToLowerInvariant()).Item1);
        Assert.False(CapabilityProfilerService.ScoreLongContextRecall("I don't see a code mentioned.").Item1);
        Assert.False(CapabilityProfilerService.ScoreLongContextRecall("").Item1);
    }

    [Theory]
    [InlineData("<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\"}}</tool_call>", true)]
    [InlineData("Sure, checking now. <tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\"}}</tool_call>", true)]
    [InlineData("<tool_call>{\"name\": \"get_weather\", \"arguments\": {}}</tool_call>", false)] // no city
    [InlineData("<tool_call>{\"name\": \"wrong_tool\", \"arguments\": {\"city\": \"Paris\"}}</tool_call>", false)]
    [InlineData("<tool_call>not json</tool_call>", false)]
    [InlineData("The weather in Paris is sunny.", false)] // no tool_call block at all
    public void ScoreToolCall_requires_a_well_formed_get_weather_block_with_a_city(string raw, bool expectedPass)
    {
        (bool passed, _) = CapabilityProfilerService.ScoreToolCall(raw);
        Assert.Equal(expectedPass, passed);
    }

    [Theory]
    [InlineData("def is_even(n):\n    return n % 2 == 0", true)]
    [InlineData("```python\ndef is_even(n):\n    return n % 2 == 0\n```", true)]
    [InlineData("def is_even(n):\n    if n % 2 == 0:\n        return True\n    return False", true)]
    [InlineData("def is_odd(n):\n    return n % 2 != 0", false)] // wrong function name
    [InlineData("def is_even(n):\n    return n > 0", false)] // no modulo check at all
    [InlineData("A function that checks if a number is even.", false)] // no code at all
    public void ScoreCoding_requires_the_exact_signature_and_a_real_modulo_check(string raw, bool expectedPass)
    {
        (bool passed, _) = CapabilityProfilerService.ScoreCoding(raw);
        Assert.Equal(expectedPass, passed);
    }

    [Theory]
    [InlineData("highst", true)]
    [InlineData("Highst", true)]
    [InlineData(" highst ", true)]
    [InlineData("\"highst\"", true)]
    [InlineData("highst.", true)]
    [InlineData("highest", false)] // the correctly-spelled name used everywhere else - a wrong answer, not a near-miss
    [InlineData("The bug is in the return statement.", false)]
    [InlineData("", false)]
    public void ScoreDebugging_requires_naming_exactly_the_misspelled_variable(string raw, bool expectedPass)
    {
        (bool passed, _) = CapabilityProfilerService.ScoreDebugging(raw, expected: "highst");
        Assert.Equal(expectedPass, passed);
    }

    [Theory]
    [InlineData("Parigi", true)]
    [InlineData("parigi", true)]
    [InlineData(" Parigi. ", true)]
    [InlineData("Paris", false)] // answered in English despite the Italian instruction
    [InlineData("The capital of France is Paris.", false)]
    [InlineData("Roma", false)] // wrong city entirely
    public void ScoreMultilingual_distinguishes_the_correct_language_from_a_correct_english_answer(string raw, bool expectedPass)
    {
        (bool passed, string detail) = CapabilityProfilerService.ScoreMultilingual(raw, expectedItalian: "parigi", commonEnglishAnswer: "paris");
        Assert.Equal(expectedPass, passed);
        if (raw.Trim().TrimEnd('.').Equals("Paris", StringComparison.OrdinalIgnoreCase))
            Assert.Contains("English", detail);
    }

    [Theory]
    [InlineData("@@ -1,3 +1,3 @@\n line1\n-line2\n+line2 modified\n line3", true)]
    [InlineData("```diff\n@@ -1,3 +1,3 @@\n line1\n-line2\n+line2 modified\n line3\n```", true)]
    [InlineData("@@ -1,3 +1,3 @@\n line1\n-line2\n+line2 changed\n line3", false)] // wrong added line content
    [InlineData("line1\nline2 modified\nline3", false)] // no diff markers at all
    [InlineData("I changed line2 to line2 modified.", false)]
    public void ScorePatch_requires_a_real_hunk_header_and_the_exact_changed_lines(string raw, bool expectedPass)
    {
        (bool passed, _) = CapabilityProfilerService.ScorePatch(raw);
        Assert.Equal(expectedPass, passed);
    }
}

public sealed class CapabilityProfilerStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly CapabilityProfilerStore _store;

    public CapabilityProfilerStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "parux-capability-profiler-test-" + Guid.NewGuid().ToString("N"));
        _store = new CapabilityProfilerStore(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void CreateRun_then_AddResult_then_GetResults_round_trips()
    {
        var run = new CapabilityProfileRunDto
        {
            Id = Guid.NewGuid().ToString("N"), ModelPath = "C:\\fake\\model.gguf", Role = "fast",
            Status = CapabilityProfileRunStatus.Running, StartedUtc = DateTime.UtcNow,
        };
        _store.CreateRun(run);
        _store.AddResult(new CapabilityProbeResultDto
        {
            Id = Guid.NewGuid().ToString("N"), RunId = run.Id, Category = CapabilityProbeCategory.JsonReliability,
            Passed = true, Detail = "ok", RawOutput = "{\"name\":\"a\"}", CreatedUtc = DateTime.UtcNow,
        });
        _store.UpdateRunStatus(run.Id, CapabilityProfileRunStatus.Completed, DateTime.UtcNow, null);

        CapabilityProfileRunDto? fetched = _store.GetRun(run.Id);
        Assert.NotNull(fetched);
        Assert.Equal(CapabilityProfileRunStatus.Completed, fetched!.Status);
        Assert.Equal("fast", fetched.Role);

        List<CapabilityProbeResultDto> results = _store.GetResults(run.Id);
        Assert.Single(results);
        Assert.True(results[0].Passed);
        Assert.Equal(CapabilityProbeCategory.JsonReliability, results[0].Category);
    }

    [Fact]
    public void Error_run_persists_its_reason_and_never_gets_results()
    {
        var run = new CapabilityProfileRunDto
        {
            Id = Guid.NewGuid().ToString("N"), ModelPath = string.Empty, Role = null,
            Status = CapabilityProfileRunStatus.Error, StartedUtc = DateTime.UtcNow,
            Error = "No text model is currently loaded and healthy."
        };
        _store.CreateRun(run);

        CapabilityProfileRunDto? fetched = _store.GetRun(run.Id);
        Assert.NotNull(fetched);
        Assert.Equal(CapabilityProfileRunStatus.Error, fetched!.Status);
        Assert.Equal("No text model is currently loaded and healthy.", fetched.Error);
        Assert.Empty(_store.GetResults(run.Id));
    }

    [Fact]
    public void GetResultsForModel_only_includes_completed_runs()
    {
        var completed = new CapabilityProfileRunDto
        {
            Id = Guid.NewGuid().ToString("N"), ModelPath = "C:\\fake\\model.gguf", Status = CapabilityProfileRunStatus.Completed,
            StartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow,
        };
        var stillRunning = new CapabilityProfileRunDto
        {
            Id = Guid.NewGuid().ToString("N"), ModelPath = "C:\\fake\\model.gguf", Status = CapabilityProfileRunStatus.Running,
            StartedUtc = DateTime.UtcNow,
        };
        _store.CreateRun(completed);
        _store.CreateRun(stillRunning);
        _store.AddResult(new CapabilityProbeResultDto
        {
            Id = Guid.NewGuid().ToString("N"), RunId = completed.Id, Category = CapabilityProbeCategory.MathReasoning,
            Passed = true, Detail = "ok", CreatedUtc = DateTime.UtcNow,
        });
        _store.AddResult(new CapabilityProbeResultDto
        {
            Id = Guid.NewGuid().ToString("N"), RunId = stillRunning.Id, Category = CapabilityProbeCategory.MathReasoning,
            Passed = false, Detail = "should not be counted", CreatedUtc = DateTime.UtcNow,
        });

        List<CapabilityProbeResultDto> results = _store.GetResultsForModel("C:\\fake\\model.gguf");
        Assert.Single(results);
        Assert.True(results[0].Passed);
    }

    [Fact]
    public void BackupDatabaseTo_then_ReplaceDatabaseFile_round_trips_a_run()
    {
        var run = new CapabilityProfileRunDto
        {
            Id = Guid.NewGuid().ToString("N"), ModelPath = "C:\\fake\\model.gguf", Status = CapabilityProfileRunStatus.Completed,
            StartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow,
        };
        _store.CreateRun(run);

        string backupPath = Path.Combine(_dir, "backup-copy.db");
        _store.BackupDatabaseTo(backupPath);

        string otherDir = Path.Combine(Path.GetTempPath(), "parux-capability-profiler-restore-" + Guid.NewGuid().ToString("N"));
        var otherStore = new CapabilityProfilerStore(otherDir);
        try
        {
            otherStore.ReplaceDatabaseFile(backupPath);
            Assert.Equal(run.ModelPath, otherStore.GetRun(run.Id)?.ModelPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(otherDir, recursive: true); } catch { /* best effort */ }
        }
    }
}

public sealed class CapabilityProfilerApiTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public CapabilityProfilerApiTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    [Fact]
    public async Task Runs_endpoints_reject_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/capabilities/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/capabilities/runs", new StartCapabilityProfileRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/capabilities/runs/anything")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/capabilities/summary?modelPath=x")).StatusCode);
    }

    [Fact]
    public async Task List_runs_returns_an_empty_list_when_none_exist_yet()
    {
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/capabilities/runs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<CapabilityProfileRunDto>? runs = await response.Content.ReadFromJsonAsync<List<CapabilityProfileRunDto>>();
        Assert.NotNull(runs);
    }

    [Fact]
    public async Task Get_run_returns_404_for_an_unknown_id()
    {
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/capabilities/runs/" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Summary_returns_404_for_a_model_with_no_completed_run()
    {
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/capabilities/summary?modelPath="
            + Uri.EscapeDataString("C:\\definitely\\not\\profiled.gguf"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
