using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
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
/// Exercises Product V2 through the real HTTP pipeline. TestDataRootIsolation sends the host's
/// durable stores and LocalAI paths to a disposable process root; files created here are also
/// confined to a unique temp directory.
/// </summary>
public sealed class ProductV2EndpointsCoverageTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "parux-product-v2-" + Guid.NewGuid().ToString("N"));

    public ProductV2EndpointsCoverageTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static IEnumerable<object[]> ProtectedMutationRoutes() => new[]
    {
        new object[] { "POST", "/api/v2/backup/import" },
        new object[] { "PUT", "/api/v2/execution-profiles/quality" },
        new object[] { "PUT", "/api/v2/execution-profiles/models/coverage-model" },
        new object[] { "DELETE", "/api/v2/execution-profiles/models/coverage-model" },
        new object[] { "POST", "/api/v2/plugins/install" },
        new object[] { "DELETE", "/api/v2/plugins/trusted-publishers/" + new string('a', 64) },
        new object[] { "POST", "/api/v2/goals" },
        new object[] { "POST", "/api/v2/goals/coverage-goal/versions" },
        new object[] { "POST", "/api/v2/goals/tasks" },
        new object[] { "POST", "/api/v2/virtual-context/ledger" },
        new object[] { "PUT", "/api/v2/semantic-index/repositories" },
        new object[] { "DELETE", "/api/v2/semantic-index/repositories/coverage-repo" },
        new object[] { "POST", "/api/v2/semantic-index/sync" },
        new object[] { "PUT", "/api/v2/mobile/execution-profile/quality" },
        new object[] { "POST", "/api/v2/mobile/goals" },
        new object[] { "POST", "/api/v2/mobile/goals/coverage-goal/versions" },
        new object[] { "POST", "/api/v2/mobile/goals/tasks" },
    };

    [Theory]
    [MemberData(nameof(ProtectedMutationRoutes))]
    public async Task Product_mutations_reject_requests_without_a_token(string method, string path)
    {
        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT") request.Content = JsonContent.Create(new { });

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_plugin_bundle_returns_the_typed_inspection_error()
    {
        using HttpClient client = AuthorizedClient();
        string missingBundle = Path.Combine(_scratch, "missing.parux-plugin");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v2/plugins/inspect", new PluginBundleInspectRequest { BundlePath = missingBundle });
        PluginBundleInspection? inspection = await response.Content.ReadFromJsonAsync<PluginBundleInspection>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(inspection);
        Assert.False(inspection!.Valid);
        Assert.False(string.IsNullOrWhiteSpace(inspection.Error));
    }

    [Fact]
    public async Task Invalid_execution_quality_returns_a_typed_error_body()
    {
        using HttpClient client = AuthorizedClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v2/execution-profiles/quality",
            new SetExecutionQualityProfileRequest { QualityProfile = "unrecognized-profile" });
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(body.RootElement.TryGetProperty("error", out JsonElement error));
        Assert.False(string.IsNullOrWhiteSpace(error.GetString()));
    }

    [Fact]
    public async Task Model_override_routes_round_trip_and_clamp_budget_values()
    {
        using HttpClient client = AuthorizedClient();
        string modelKey = "coverage-" + Guid.NewGuid().ToString("N");

        using HttpResponseMessage update = await client.PutAsJsonAsync(
            $"/api/v2/execution-profiles/models/{modelKey}",
            new ModelExecutionProfile
            {
                QualityProfile = ExecutionQualityProfile.MaximumQuality,
                EvidenceSource = "coverage fixture",
                Budget = new ExecutionResourceBudget { MaxToolSteps = 25_000, MaxParallelTasks = 99 }
            });
        ModelExecutionProfile? updated = await update.Content.ReadFromJsonAsync<ModelExecutionProfile>();
        using HttpResponseMessage read = await client.GetAsync("/api/v2/execution-profiles?modelKey=" + Uri.EscapeDataString(modelKey));
        ExecutionProfileStatus? status = await read.Content.ReadFromJsonAsync<ExecutionProfileStatus>();

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(modelKey, updated?.ModelKey);
        Assert.Equal(20_000, updated?.Budget.MaxToolSteps);
        Assert.Equal(8, updated?.Budget.MaxParallelTasks);
        Assert.Equal(modelKey, status?.Effective.ModelKey);

        using HttpResponseMessage remove = await client.DeleteAsync($"/api/v2/execution-profiles/models/{modelKey}");
        using HttpResponseMessage after = await client.GetAsync("/api/v2/execution-profiles?modelKey=" + Uri.EscapeDataString(modelKey));
        ExecutionProfileStatus? reset = await after.Content.ReadFromJsonAsync<ExecutionProfileStatus>();

        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.DoesNotContain(reset!.ModelOverrides, profile => profile.ModelKey == modelKey);
    }

    [Fact]
    public async Task Plugin_install_enforces_the_bundle_entry_limit()
    {
        using HttpClient client = AuthorizedClient();
        string bundle = Path.Combine(_scratch, "too-many-plugin-entries.parux-plugin");
        using (ZipArchive archive = ZipFile.Open(bundle, ZipArchiveMode.Create))
        {
            for (int i = 0; i < 129; i++)
            {
                ZipArchiveEntry entry = archive.CreateEntry($"entry-{i}.json");
                await using Stream stream = entry.Open();
                await stream.WriteAsync(Array.Empty<byte>());
            }
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v2/plugins/install", new PluginInstallRequest { BundlePath = bundle, AllowUnsigned = true });
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(body.RootElement.TryGetProperty("error", out JsonElement error));
        Assert.Contains("128", error.GetString());
    }

    [Fact]
    public async Task Signed_plugin_install_and_publisher_revocation_persist_through_http()
    {
        using HttpClient client = AuthorizedClient();
        string bundlePath = CreateSignedPluginBundle("coverage-plugin-" + Guid.NewGuid().ToString("N"));
        string? fingerprint = null;

        try
        {
            using HttpResponseMessage inspectResponse = await client.PostAsJsonAsync(
                "/api/v2/plugins/inspect", new PluginBundleInspectRequest { BundlePath = bundlePath });
            PluginBundleInspection? inspection = await inspectResponse.Content.ReadFromJsonAsync<PluginBundleInspection>();
            Assert.Equal(HttpStatusCode.OK, inspectResponse.StatusCode);
            Assert.True(inspection?.Valid);
            Assert.True(inspection?.SignatureValid);
            fingerprint = inspection!.PublisherKeyFingerprint;

            using HttpResponseMessage installResponse = await client.PostAsJsonAsync(
                "/api/v2/plugins/install", new PluginInstallRequest { BundlePath = bundlePath, TrustPublisher = true });
            PluginInstallResult? installed = await installResponse.Content.ReadFromJsonAsync<PluginInstallResult>();
            using HttpResponseMessage trusted = await client.GetAsync("/api/v2/plugins/trusted-publishers");
            List<PluginPublisherTrustDto>? publishers = await trusted.Content.ReadFromJsonAsync<List<PluginPublisherTrustDto>>();

            Assert.Equal(HttpStatusCode.OK, installResponse.StatusCode);
            Assert.True(installed?.Installed);
            Assert.Contains(publishers!, publisher => publisher.Fingerprint == fingerprint);

            using HttpResponseMessage revoke = await client.DeleteAsync($"/api/v2/plugins/trusted-publishers/{fingerprint}");
            using HttpResponseMessage after = await client.GetAsync("/api/v2/plugins/trusted-publishers");
            List<PluginPublisherTrustDto>? remaining = await after.Content.ReadFromJsonAsync<List<PluginPublisherTrustDto>>();

            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
            Assert.DoesNotContain(remaining!, publisher => publisher.Fingerprint == fingerprint);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(fingerprint))
                using (await client.DeleteAsync($"/api/v2/plugins/trusted-publishers/{fingerprint}")) { }
        }
    }

    [Fact]
    public async Task Execution_quality_update_is_visible_on_the_read_route()
    {
        using HttpClient client = AuthorizedClient();
        using HttpResponseMessage beforeResponse = await client.GetAsync("/api/v2/execution-profiles");
        ExecutionProfileStatus? before = await beforeResponse.Content.ReadFromJsonAsync<ExecutionProfileStatus>();
        Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode);
        Assert.NotNull(before);

        try
        {
            using HttpResponseMessage update = await client.PutAsJsonAsync(
                "/api/v2/execution-profiles/quality",
                new SetExecutionQualityProfileRequest { QualityProfile = ExecutionQualityProfile.MaximumQuality });
            ExecutionProfileStatus? updated = await update.Content.ReadFromJsonAsync<ExecutionProfileStatus>();
            using HttpResponseMessage read = await client.GetAsync("/api/v2/execution-profiles");
            ExecutionProfileStatus? persisted = await read.Content.ReadFromJsonAsync<ExecutionProfileStatus>();

            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(ExecutionQualityProfile.MaximumQuality, updated?.ActiveQualityProfile);
            Assert.Equal(ExecutionQualityProfile.MaximumQuality, persisted?.ActiveQualityProfile);
        }
        finally
        {
            using HttpResponseMessage restore = await client.PutAsJsonAsync(
                "/api/v2/execution-profiles/quality",
                new SetExecutionQualityProfileRequest { QualityProfile = before!.ActiveQualityProfile });
        }
    }

    [Fact]
    public async Task Goal_creation_revision_and_read_routes_preserve_version_identity()
    {
        using HttpClient client = AuthorizedClient();
        GoalSpecificationVersionDto first = await CreateGoalAsync(client, "create and read");

        using HttpResponseMessage revision = await client.PostAsJsonAsync(
            $"/api/v2/goals/{first.GoalId}/versions",
            new CreateGoalSpecificationVersionRequest
            {
                ExpectedPreviousVersion = 1,
                Goal = "Keep the coverage goal versioned",
                AcceptanceCriteria = new List<string> { "The second revision is retrievable" },
                ChangeReason = "coverage revision"
            });
        GoalSpecificationVersionDto? second = await revision.Content.ReadFromJsonAsync<GoalSpecificationVersionDto>();
        using HttpResponseMessage get = await client.GetAsync($"/api/v2/goals/{first.GoalId}");
        GoalSpecificationVersionDto? latest = await get.Content.ReadFromJsonAsync<GoalSpecificationVersionDto>();
        using HttpResponseMessage list = await client.GetAsync($"/api/v2/goals/{first.GoalId}/versions");
        List<GoalSpecificationVersionDto>? versions = await list.Content.ReadFromJsonAsync<List<GoalSpecificationVersionDto>>();

        Assert.Equal(HttpStatusCode.OK, revision.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.NotNull(second);
        Assert.Equal(first.GoalId, second!.GoalId);
        Assert.Equal(2, second.Version);
        Assert.Equal(2, latest?.Version);
        Assert.Equal(new[] { 1, 2 }, versions?.Select(value => value.Version).ToArray());
    }

    [Fact]
    public async Task Stale_goal_revision_returns_an_error_and_does_not_add_a_version()
    {
        using HttpClient client = AuthorizedClient();
        GoalSpecificationVersionDto first = await CreateGoalAsync(client, "stale revision");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v2/goals/{first.GoalId}/versions",
            new CreateGoalSpecificationVersionRequest
            {
                ExpectedPreviousVersion = 4,
                Goal = "This revision must be refused",
                AcceptanceCriteria = new List<string> { "The current version remains unchanged" }
            });
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using HttpResponseMessage versionsResponse = await client.GetAsync($"/api/v2/goals/{first.GoalId}/versions");
        List<GoalSpecificationVersionDto>? versions = await versionsResponse.Content.ReadFromJsonAsync<List<GoalSpecificationVersionDto>>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(body.RootElement.TryGetProperty("error", out JsonElement error));
        Assert.Contains("current version", error.GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(versions!);
        Assert.Equal(1, versions![0].Version);
    }

    [Fact]
    public async Task Goal_task_creation_caps_repair_attempts_at_the_server_limit()
    {
        using HttpClient client = AuthorizedClient();
        GoalSpecificationVersionDto goal = await CreateGoalAsync(client, "repair attempt cap");
        string? createdTaskId = null;

        try
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/v2/goals/tasks",
                new CreateGoalModeTaskRequest
                {
                    GoalId = goal.GoalId,
                    GoalVersion = 1,
                    Role = "review",
                    MaxRepairAttempts = 99
                });
            GoalModeTaskResponse? result = await response.Content.ReadFromJsonAsync<GoalModeTaskResponse>();
            createdTaskId = result?.Task.Id;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(result);
            Assert.Equal(goal.GoalId, result!.Binding.GoalId);
            Assert.Equal(3, result.Task.MaxRepairAttempts);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(createdTaskId))
                _factory.Services.GetRequiredService<TaskStore>().UpdateStatus(createdTaskId,
                    new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled, Stage = "Coverage test cleanup" });
        }
    }

    [Fact]
    public async Task Virtual_context_ledger_limits_the_returned_history_window()
    {
        using HttpClient client = AuthorizedClient();
        string scopeId = Guid.NewGuid().ToString("N");
        foreach (string text in new[] { "first fixture entry", "latest fixture entry" })
        {
            using HttpResponseMessage append = await client.PostAsJsonAsync(
                "/api/v2/virtual-context/ledger",
                new AppendVirtualContextLedgerRequest
                {
                    Scope = "conversation",
                    ScopeId = scopeId,
                    Kind = VirtualContextLedgerKind.Note,
                    Text = text,
                    Source = "coverage-test"
                });
            Assert.Equal(HttpStatusCode.OK, append.StatusCode);
        }

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/v2/virtual-context/ledger/conversation/{scopeId}?maxEntries=1");
        VirtualContextLedgerStateResponse? state = await response.Content.ReadFromJsonAsync<VirtualContextLedgerStateResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(state);
        Assert.Single(state!.Entries);
        Assert.Equal("latest fixture entry", state.Entries[0].Text);
    }

    [Fact]
    public async Task Semantic_repository_register_list_and_delete_use_the_fixture_root()
    {
        using HttpClient client = AuthorizedClient();
        string repository = Path.Combine(_scratch, "repository");
        Directory.CreateDirectory(repository);

        using HttpResponseMessage register = await client.PutAsJsonAsync(
            "/api/v2/semantic-index/repositories",
            new RegisterSemanticRepositoryRequest { Alias = "coverage-repo", RepositoryRoot = repository });
        SemanticRepositoryRegistrationDto? registered = await register.Content.ReadFromJsonAsync<SemanticRepositoryRegistrationDto>();
        using HttpResponseMessage list = await client.GetAsync("/api/v2/semantic-index/repositories");
        List<SemanticRepositoryRegistrationDto>? repositories = await list.Content.ReadFromJsonAsync<List<SemanticRepositoryRegistrationDto>>();
        using HttpResponseMessage remove = await client.DeleteAsync("/api/v2/semantic-index/repositories/coverage-repo?removeIndex=false");
        using HttpResponseMessage after = await client.GetAsync("/api/v2/semantic-index/repositories");
        List<SemanticRepositoryRegistrationDto>? remaining = await after.Content.ReadFromJsonAsync<List<SemanticRepositoryRegistrationDto>>();

        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        Assert.Equal(Path.GetFullPath(repository), registered?.RepositoryRoot);
        Assert.Contains(repositories!, item => item.Alias == "coverage-repo");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.DoesNotContain(remaining!, item => item.Alias == "coverage-repo");
    }

    [Fact]
    public async Task Empty_semantic_search_returns_a_typed_client_error()
    {
        using HttpClient client = AuthorizedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v2/semantic-index/search", new SemanticIndexSearchRequest { Query = " " });
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(body.RootElement.TryGetProperty("error", out JsonElement error));
        Assert.Contains("1-32,000", error.GetString());
    }

    [Fact]
    public async Task Invalid_v2_backup_archive_returns_a_validation_error()
    {
        using HttpClient client = AuthorizedClient();
        using var content = new ByteArrayContent(new byte[] { 0x01, 0x02, 0x03 });
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        using HttpResponseMessage response = await client.PostAsync("/api/v2/backup/import", content);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(body.RootElement.TryGetProperty("error", out JsonElement error));
        Assert.False(string.IsNullOrWhiteSpace(error.GetString()));
    }

    [Fact]
    public async Task V2_backup_import_rejects_declared_content_above_the_compressed_size_limit()
    {
        using HttpClient client = AuthorizedClient();
        using var content = new StreamContent(new MemoryStream(Array.Empty<byte>()));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Headers.ContentLength = 8L * 1024 * 1024 * 1024 + 1;

        using HttpResponseMessage response = await client.PostAsync("/api/v2/backup/import", content);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("8 GiB", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Cancelling_a_v2_backup_upload_aborts_body_validation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        string token = _factory.Services.GetRequiredService<ServerState>().Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/api/v2/backup/import";
            context.Request.ContentType = "application/zip";
            context.Request.Body = new MemoryStream(new byte[] { 0x50, 0x4b, 0x03, 0x04 });
            context.Request.Headers["X-PaRuX-Token"] = token;
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.RequestAborted = cancelled.Token;
        }));
    }

    [Fact]
    public async Task Ref2va_generation_is_refused_when_the_fixture_gaming_marker_is_active()
    {
        using IDisposable localAiRoot = LocalAiPaths.UseRootForTests(Path.Combine(_scratch, "gaming-localai"));
        using IDisposable stateRoot = LocalAiPaths.UseStateDirForTests(Path.Combine(_scratch, "gaming-state"));
        File.WriteAllText(LocalAiPaths.GameMarkerFile, "{}");
        Directory.CreateDirectory(Path.Combine(LocalAiPaths.StateDir, "media-jobs"));

        using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddTransient<IStartupFilter, LoopbackClientStartupFilter>();
            });
        });
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v2/media/engines/ref2va/generate", new { });
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Gaming Mode", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mobile_goal_task_route_clamps_repair_attempts_without_running_a_model()
    {
        using HttpClient client = AuthorizedClient();
        using HttpResponseMessage profileResponse = await client.GetAsync("/api/v2/mobile/execution-profile");
        using JsonDocument profileBody = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
        string originalQuality = profileBody.RootElement.GetProperty("activeQualityProfile").GetString()!;
        GoalSpecificationVersionDto? goal = null;
        string? taskId = null;

        try
        {
            using HttpResponseMessage profileUpdate = await client.PutAsJsonAsync(
                "/api/v2/mobile/execution-profile/quality",
                new MobileProductV2Endpoints.MobileExecutionQualityRequest { QualityProfile = ExecutionQualityProfile.Fast });
            using JsonDocument updatedProfile = JsonDocument.Parse(await profileUpdate.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, profileUpdate.StatusCode);
            Assert.Equal(ExecutionQualityProfile.Fast, updatedProfile.RootElement.GetProperty("activeQualityProfile").GetString());

            using HttpResponseMessage createGoal = await client.PostAsJsonAsync(
                "/api/v2/mobile/goals", new CreateGoalSpecificationRequest
                {
                    Goal = "Exercise mobile Product V2 coverage",
                    AcceptanceCriteria = new List<string> { "The task is bounded and persisted" },
                    ChangeReason = "coverage"
                });
            goal = await createGoal.Content.ReadFromJsonAsync<GoalSpecificationVersionDto>();
            Assert.Equal(HttpStatusCode.OK, createGoal.StatusCode);
            Assert.NotNull(goal);

            using HttpResponseMessage revision = await client.PostAsJsonAsync(
                $"/api/v2/mobile/goals/{goal!.GoalId}/versions", new CreateGoalSpecificationVersionRequest
                {
                    ExpectedPreviousVersion = goal.Version,
                    Goal = "Exercise mobile Product V2 coverage with a revision",
                    AcceptanceCriteria = new List<string> { "The newest mobile goal version is bound" },
                    ChangeReason = "coverage revision"
                });
            GoalSpecificationVersionDto? revised = await revision.Content.ReadFromJsonAsync<GoalSpecificationVersionDto>();
            Assert.Equal(HttpStatusCode.OK, revision.StatusCode);
            Assert.Equal(2, revised?.Version);
            goal = revised;

            using HttpResponseMessage createTask = await client.PostAsJsonAsync(
                "/api/v2/mobile/goals/tasks", new MobileProductV2Endpoints.MobileCreateGoalModeTaskRequest
                {
                    GoalId = goal!.GoalId,
                    GoalVersion = goal!.Version,
                    Role = "review",
                    MaxRepairAttempts = 99
                });
            using JsonDocument body = JsonDocument.Parse(await createTask.Content.ReadAsStringAsync());
            taskId = body.RootElement.GetProperty("task").GetProperty("id").GetString();
            AgentTaskDto? task = string.IsNullOrWhiteSpace(taskId)
                ? null
                : _factory.Services.GetRequiredService<TaskStore>().Get(taskId);

            Assert.Equal(HttpStatusCode.OK, createTask.StatusCode);
            Assert.NotNull(task);
            Assert.Equal(3, task!.MaxRepairAttempts);
            Assert.Equal(goal!.GoalId, body.RootElement.GetProperty("binding").GetProperty("goalId").GetString());
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(taskId))
                _factory.Services.GetRequiredService<TaskStore>().UpdateStatus(taskId,
                    new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled, Stage = "Coverage test cleanup" });
            using HttpResponseMessage restoreProfile = await client.PutAsJsonAsync(
                "/api/v2/mobile/execution-profile/quality",
                new MobileProductV2Endpoints.MobileExecutionQualityRequest { QualityProfile = originalQuality });
        }
    }

    private HttpClient AuthorizedClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    private static async Task<GoalSpecificationVersionDto> CreateGoalAsync(HttpClient client, string label)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v2/goals", new CreateGoalSpecificationRequest
        {
            Goal = "Exercise Product V2 " + label,
            AcceptanceCriteria = new List<string> { "The goal is persisted and queryable" },
            ChangeReason = "coverage"
        });
        GoalSpecificationVersionDto? created = await response.Content.ReadFromJsonAsync<GoalSpecificationVersionDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(created);
        return created!;
    }

    private string CreateSignedPluginBundle(string id)
    {
        string path = Path.Combine(_scratch, id + ".parux-plugin");
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new PaRuXPluginManifest
        {
            Id = id,
            DisplayName = id,
            Version = "1.0.0",
            Publisher = "Coverage fixtures",
            Description = "Declarative endpoint fixture"
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["plugin.json"] = manifest };

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = key.SignHash(PluginBundleService.ComputeDeclarativeHash(files));
        string signatureDocument = JsonSerializer.Serialize(new
        {
            algorithm = "ECDSA-SHA256",
            publicKeyPem = key.ExportSubjectPublicKeyInfoPem(),
            signatureBase64 = Convert.ToBase64String(signature)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteZipEntry(archive, "plugin.json", manifest);
        WriteZipEntry(archive, "signature.json", Encoding.UTF8.GetBytes(signatureDocument));
        return path;
    }

    private static void WriteZipEntry(ZipArchive archive, string name, byte[] data)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using Stream stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    private sealed class LoopbackClientStartupFilter : IStartupFilter
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
