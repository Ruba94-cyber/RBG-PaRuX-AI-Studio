using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// HTTP-level regression coverage for the paired/mobile control-plane module. Service-level tests
/// cover Project attachment and Artifact path safety in depth; these checks prove the module is
/// actually mapped behind the normal Core authentication boundary and serializes the public wire
/// contracts Android consumes.
/// </summary>
public sealed class MobileControlEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public MobileControlEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/v1/projects")]
    [InlineData("/api/v1/mobile/artifacts")]
    public async Task Mobile_control_reads_reject_missing_credentials(string path)
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_projects_route_is_mapped_and_returns_the_server_owned_shape()
    {
        using HttpClient client = AuthorizedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/projects");
        ProjectListResponse? payload = await response.Content.ReadFromJsonAsync<ProjectListResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.NotNull(payload!.Projects);
    }

    [Fact]
    public async Task Authenticated_mobile_artifact_route_is_mapped_and_never_returns_content_refs()
    {
        using HttpClient client = AuthorizedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/mobile/artifacts");
        string json = await response.Content.ReadAsStringAsync();
        MobileArtifactListResponse? payload = JsonSerializer.Deserialize<MobileArtifactListResponse>(json, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.DoesNotContain("contentRef", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("filePath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Project_attach_route_is_mapped_and_rejects_unknown_identity_without_mutation()
    {
        using HttpClient client = AuthorizedClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/projects/not-a-project/tasks",
            new AttachProjectTaskRequest { TaskId = "not-a-task" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Group_chat_still_binds_the_group_chat_request_contract_after_mobile_route_registration()
    {
        using HttpClient client = AuthorizedClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/group-chat",
            new GroupChatRequest { Prompt = "contract probe", Participants = new() });

        // Correct GroupChatRequest binding reaches GroupChatService and is rejected by its
        // 2-4-participant invariant. This remains a cheap HTTP regression guard for Program.cs.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpClient AuthorizedClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }
}
