using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Exercises every Mobile Product V2 route through the server HTTP pipeline. The test module
/// initializer redirects durable stores to a per-run temp data root; this class also uses isolated
/// scratch and LocalAI roots and keeps the Gaming marker active. Hosted workers are removed so
/// creating a mobile task can only exercise persistence and scheduling, never model execution.
/// </summary>
public sealed class MobileProductV2EndpointsCoverageTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly WebApplicationFactory<global::Program> _factory;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "parux-mobile-product-v2-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _localAiRootScope;
    private readonly IDisposable _localAiStateScope;
    private readonly List<string> _taskIds = new();

    public MobileProductV2EndpointsCoverageTests(ApiSecurityTests.PaRuXFactory baseFactory)
    {
        Directory.CreateDirectory(_scratch);
        _localAiRootScope = LocalAiPaths.UseRootForTests(Path.Combine(_scratch, "localai-root"));
        _localAiStateScope = LocalAiPaths.UseStateDirForTests(Path.Combine(_scratch, "localai-state"));
        Directory.CreateDirectory(Path.Combine(LocalAiPaths.StateDir, "media-jobs"));
        Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.GameMarkerFile)!);
        File.WriteAllText(LocalAiPaths.GameMarkerFile, "{}");

        _factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.RemoveAll<IHostedService>()));
    }

    public static IEnumerable<object[]> ProtectedRoutes() => new[]
    {
        new object[] { "GET", "/api/v2/mobile/execution-profile" },
        new object[] { "PUT", "/api/v2/mobile/execution-profile/quality" },
        new object[] { "GET", "/api/v2/mobile/observability/dashboard" },
        new object[] { "POST", "/api/v2/mobile/goals" },
        new object[] { "POST", "/api/v2/mobile/goals/0123456789abcdef0123456789abcdef/versions" },
        new object[] { "GET", "/api/v2/mobile/goals/0123456789abcdef0123456789abcdef" },
        new object[] { "GET", "/api/v2/mobile/goals/0123456789abcdef0123456789abcdef/versions" },
        new object[] { "POST", "/api/v2/mobile/goals/tasks" },
        new object[] { "GET", "/api/v2/mobile/goals/tasks/example-task" },
        new object[] { "GET", "/api/v2/mobile/goals/tasks/example-task/verification-records" }
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Mobile_product_v2_routes_refuse_requests_without_a_token(string method, string path)
    {
        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "PUT" or "POST") request.Content = JsonContent.Create(new { });

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Execution_profile_read_and_quality_update_use_the_mobile_projection()
    {
        using HttpClient client = AuthorizedClient();
        using HttpResponseMessage beforeResponse = await client.GetAsync("/api/v2/mobile/execution-profile");
        using JsonDocument beforeBody = JsonDocument.Parse(await beforeResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode);
        string originalQuality = beforeBody.RootElement.GetProperty("activeQualityProfile").GetString()!;

        try
        {
            using HttpResponseMessage update = await client.PutAsJsonAsync(
                "/api/v2/mobile/execution-profile/quality",
                new MobileProductV2Endpoints.MobileExecutionQualityRequest { QualityProfile = ExecutionQualityProfile.Fast });
            using JsonDocument updatedBody = JsonDocument.Parse(await update.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            Assert.Equal(ExecutionQualityProfile.Fast, updatedBody.RootElement.GetProperty("activeQualityProfile").GetString());
            Assert.True(updatedBody.RootElement.TryGetProperty("budget", out _));
            Assert.False(updatedBody.RootElement.TryGetProperty("modelOverrides", out _));
            Assert.False(updatedBody.RootElement.TryGetProperty("modelKey", out _));
        }
        finally
        {
            using HttpResponseMessage restore = await client.PutAsJsonAsync(
                "/api/v2/mobile/execution-profile/quality",
                new MobileProductV2Endpoints.MobileExecutionQualityRequest { QualityProfile = originalQuality });
        }
    }

    [Fact]
    public async Task Invalid_quality_profile_returns_a_typed_error_and_preserves_the_current_value()
    {
        using HttpClient client = AuthorizedClient();
        string originalQuality = await ReadActiveQualityAsync(client);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v2/mobile/execution-profile/quality",
            new MobileProductV2Endpoints.MobileExecutionQualityRequest { QualityProfile = "unknown-quality" });
        string error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Unknown execution quality profile", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalQuality, await ReadActiveQualityAsync(client));
    }

    [Fact]
    public async Task Goal_creation_rejects_more_than_the_acceptance_criteria_limit_with_a_typed_error()
    {
        using HttpClient client = AuthorizedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v2/mobile/goals",
            new CreateGoalSpecificationRequest
            {
                Goal = "Characterize the mobile goal limit",
                AcceptanceCriteria = Enumerable.Range(1, 65).Select(index => "criterion " + index).ToList(),
                ChangeReason = "coverage"
            });
        string error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("1-64", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Goal_creation_revision_and_read_routes_preserve_the_immutable_version_chain()
    {
        using HttpClient client = AuthorizedClient();
        GoalSpecificationVersionDto first = await CreateGoalAsync(client, "version-chain");

        using HttpResponseMessage revision = await client.PostAsJsonAsync(
            $"/api/v2/mobile/goals/{first.GoalId}/versions",
            new CreateGoalSpecificationVersionRequest
            {
                ExpectedPreviousVersion = first.Version,
                Goal = "Read the revised mobile goal",
                AcceptanceCriteria = new List<string> { "The new version is retrievable" },
                ChangeReason = "coverage revision"
            });
        GoalSpecificationVersionDto? second = await revision.Content.ReadFromJsonAsync<GoalSpecificationVersionDto>();
        using HttpResponseMessage latestResponse = await client.GetAsync($"/api/v2/mobile/goals/{first.GoalId}");
        GoalSpecificationVersionDto? latest = await latestResponse.Content.ReadFromJsonAsync<GoalSpecificationVersionDto>();
        using HttpResponseMessage firstResponse = await client.GetAsync($"/api/v2/mobile/goals/{first.GoalId}?version=1");
        GoalSpecificationVersionDto? versionOne = await firstResponse.Content.ReadFromJsonAsync<GoalSpecificationVersionDto>();
        using HttpResponseMessage versionsResponse = await client.GetAsync($"/api/v2/mobile/goals/{first.GoalId}/versions");
        List<GoalSpecificationVersionDto>? versions = await versionsResponse.Content.ReadFromJsonAsync<List<GoalSpecificationVersionDto>>();

        Assert.Equal(HttpStatusCode.OK, revision.StatusCode);
        Assert.Equal(HttpStatusCode.OK, latestResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, versionsResponse.StatusCode);
        Assert.NotNull(second);
        Assert.Equal(2, second!.Version);
        Assert.Equal(first.ContentSha256, second.PreviousContentSha256);
        Assert.Equal(2, latest?.Version);
        Assert.Equal(1, versionOne?.Version);
        Assert.Equal(new[] { 1, 2 }, versions?.Select(value => value.Version).ToArray());
    }

    [Fact]
    public async Task Stale_goal_revision_returns_a_typed_error_without_adding_a_version()
    {
        using HttpClient client = AuthorizedClient();
        GoalSpecificationVersionDto first = await CreateGoalAsync(client, "stale-revision");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v2/mobile/goals/{first.GoalId}/versions",
            new CreateGoalSpecificationVersionRequest
            {
                ExpectedPreviousVersion = first.Version + 1,
                Goal = "This stale revision must be refused",
                AcceptanceCriteria = new List<string> { "The stored version remains unchanged" }
            });
        string error = await ReadErrorAsync(response);
        using HttpResponseMessage versionsResponse = await client.GetAsync($"/api/v2/mobile/goals/{first.GoalId}/versions");
        List<GoalSpecificationVersionDto>? versions = await versionsResponse.Content.ReadFromJsonAsync<List<GoalSpecificationVersionDto>>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("current version", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, versionsResponse.StatusCode);
        Assert.Single(versions!);
        Assert.Equal(1, versions![0].Version);
    }

    [Fact]
    public async Task Dashboard_route_clamps_recent_activity_to_its_minimum_window()
    {
        TaskStore tasks = _factory.Services.GetRequiredService<TaskStore>();
        for (int index = 0; index < 6; index++)
        {
            AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest
            {
                Goal = "Mobile V2 dashboard fixture " + Guid.NewGuid().ToString("N"),
                Role = "review",
                GrantedCapabilities = new List<string>(),
                MaxRepairAttempts = 0
            });
            _taskIds.Add(task.Id);
        }

        using HttpClient client = AuthorizedClient();
        using HttpResponseMessage response = await client.GetAsync(
            "/api/v2/mobile/observability/dashboard?recentLimit=1&benchmarkLimit=0");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.RootElement.TryGetProperty("engineRaceSummary", out _));
        Assert.Equal(5, body.RootElement.GetProperty("recentActivity").GetArrayLength());
    }

    [Fact]
    public async Task Mobile_goal_task_caps_repair_attempts_and_reads_its_binding_and_verification_records()
    {
        using HttpClient client = AuthorizedClient();
        GoalSpecificationVersionDto goal = await CreateGoalAsync(client, "task-binding");

        using HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            "/api/v2/mobile/goals/tasks",
            new MobileProductV2Endpoints.MobileCreateGoalModeTaskRequest
            {
                GoalId = goal.GoalId,
                GoalVersion = goal.Version,
                Role = "review",
                MaxRepairAttempts = 99
            });
        using JsonDocument createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        string taskId = createdBody.RootElement.GetProperty("task").GetProperty("id").GetString()!;
        _taskIds.Add(taskId);
        TaskStore tasks = _factory.Services.GetRequiredService<TaskStore>();
        AgentTaskDto task = tasks.Get(taskId)!;

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.Equal(3, task.MaxRepairAttempts);
        Assert.Empty(task.GrantedCapabilities);
        Assert.Equal(goal.GoalId, createdBody.RootElement.GetProperty("binding").GetProperty("goalId").GetString());

        using HttpResponseMessage bindingResponse = await client.GetAsync($"/api/v2/mobile/goals/tasks/{taskId}");
        using JsonDocument bindingBody = JsonDocument.Parse(await bindingResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, bindingResponse.StatusCode);
        Assert.Equal(goal.GoalId, bindingBody.RootElement.GetProperty("specification").GetProperty("goalId").GetString());

        GoalModeV2Store.Current.RecordVerification(taskId, 1, goal.AcceptanceCriteria.Select(criterion =>
            new GoalAcceptanceCriterionVerificationDto
            {
                CriterionId = criterion.Id,
                Satisfied = true,
                Evidence = "verified by isolated endpoint fixture"
            }));
        using HttpResponseMessage recordsResponse = await client.GetAsync(
            $"/api/v2/mobile/goals/tasks/{taskId}/verification-records");
        List<GoalVerificationRecordDto>? records = await recordsResponse.Content.ReadFromJsonAsync<List<GoalVerificationRecordDto>>();

        Assert.Equal(HttpStatusCode.OK, recordsResponse.StatusCode);
        Assert.NotNull(records);
        Assert.Single(records!);
        Assert.Equal(goal.GoalId, records![0].GoalId);
        Assert.True(records[0].AllRequiredSatisfied);
    }

    [Fact]
    public async Task Task_creation_for_a_missing_goal_returns_a_typed_not_found_error()
    {
        using HttpClient client = AuthorizedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v2/mobile/goals/tasks",
            new MobileProductV2Endpoints.MobileCreateGoalModeTaskRequest
            {
                GoalId = "0123456789abcdef0123456789abcdef",
                GoalVersion = 1,
                Role = "review",
                MaxRepairAttempts = 1
            });
        string error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_taskIds.Count > 0)
        {
            TaskStore tasks = _factory.Services.GetRequiredService<TaskStore>();
            foreach (string taskId in _taskIds.AsEnumerable().Reverse())
            {
                AgentTaskDto? task = tasks.Get(taskId);
                if (task == null) continue;
                try
                {
                    tasks.UpdateStatus(taskId, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Cancelled,
                        Stage = "Mobile V2 coverage cleanup"
                    });
                    tasks.Delete(taskId);
                }
                catch (InvalidOperationException) { }
            }
        }

        _factory.Dispose();
        _localAiStateScope.Dispose();
        _localAiRootScope.Dispose();
        try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private HttpClient AuthorizedClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    private static async Task<GoalSpecificationVersionDto> CreateGoalAsync(HttpClient client, string label)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v2/mobile/goals", new CreateGoalSpecificationRequest
        {
            Goal = "Exercise mobile Product V2 " + label,
            AcceptanceCriteria = new List<string> { "The goal is persisted and queryable" },
            ChangeReason = "coverage"
        });
        GoalSpecificationVersionDto? created = await response.Content.ReadFromJsonAsync<GoalSpecificationVersionDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(created);
        return created!;
    }

    private static async Task<string> ReadActiveQualityAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/v2/mobile/execution-profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("activeQualityProfile").GetString()!;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("error", out JsonElement error), "Expected the typed error property.");
        Assert.Equal(JsonValueKind.String, error.ValueKind);
        return error.GetString() ?? string.Empty;
    }
}
