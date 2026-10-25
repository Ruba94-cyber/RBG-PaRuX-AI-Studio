using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Same test-isolation trade-off as ModelRolesTests: LocalAiPaths.SetMediaCheckpointOverride
/// writes to a real, fixed %LOCALAPPDATA%\PaRuX\media-checkpoint-overrides.json with no
/// test-injectable root. Scoped to what's safe in a hermetic run - request validation that fails
/// before any file write happens - plus the real folder listing, which is read-only.
/// </summary>
public sealed class MediaCheckpointsTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public MediaCheckpointsTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_checkpoints_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/media/checkpoints");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_checkpoints_endpoint_returns_image_video_and_edit()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/media/checkpoints");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<MediaCheckpointStatus>? checkpoints = await response.Content.ReadFromJsonAsync<List<MediaCheckpointStatus>>();
        Assert.NotNull(checkpoints);
        Assert.Contains(checkpoints!, c => c.Type == "image");
        Assert.Contains(checkpoints!, c => c.Type == "video");
        Assert.Contains(checkpoints!, c => c.Type == "edit");
    }

    [Fact]
    public async Task Set_checkpoint_endpoint_rejects_an_unknown_type_name()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/media/checkpoints/upscale",
            new SetMediaCheckpointRequest { FileName = "anything.safetensors" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Set_checkpoint_endpoint_rejects_a_file_not_in_the_diffusion_models_folder()
    {
        // Never reaches the file write: LocalAiPaths.SetMediaCheckpointOverride checks the real
        // folder listing before persisting anything, and this filename is guaranteed absent.
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/media/checkpoints/image",
            new SetMediaCheckpointRequest { FileName = "parux-test-missing-" + Guid.NewGuid().ToString("N") + ".safetensors" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reset_checkpoint_endpoint_rejects_an_unknown_type_name()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsync("/api/v1/media/checkpoints/upscale/reset", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Recommended_checkpoints_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/media/checkpoints/recommended");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Recommended_checkpoints_endpoint_returns_only_verified_same_family_entries()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/media/checkpoints/recommended");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<RecommendedCheckpoint>? recommended = await response.Content.ReadFromJsonAsync<List<RecommendedCheckpoint>>();
        Assert.NotNull(recommended);
        Assert.NotEmpty(recommended!);
        Assert.All(recommended!, r =>
        {
            Assert.True(r.Type is "image" or "video" or "edit");
            Assert.NotEmpty(r.FileName);
            Assert.NotEmpty(r.DownloadUrl);
            Assert.True(r.EstimatedSizeBytes > 0);
        });
    }

    /// <summary>Same test-isolation trade-off as the other endpoints here: activation both moves a
    /// real file and writes the real overrides file, so this suite only reaches the validation
    /// branches that return before either of those happens - an unknown type, an unsafe file name
    /// (path traversal), and a file name that was never actually downloaded.</summary>
    [Fact]
    public async Task Activate_checkpoint_endpoint_rejects_an_unknown_type_name()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/media/checkpoints/activate",
            new ActivateCheckpointRequest { Type = "upscale", FileName = "anything.safetensors" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Activate_checkpoint_endpoint_rejects_a_path_traversal_file_name()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/media/checkpoints/activate",
            new ActivateCheckpointRequest { Type = "image", FileName = "..\\..\\evil.safetensors" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Activate_checkpoint_endpoint_rejects_a_file_that_was_never_downloaded()
    {
        // Never reaches File.Move or SetMediaCheckpointOverride: the source-file existence check
        // in PaRuX's own managed download folder fails first for a name this guaranteed not to
        // have been downloaded.
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/media/checkpoints/activate",
            new ActivateCheckpointRequest { Type = "image", FileName = "parux-test-never-downloaded-" + Guid.NewGuid().ToString("N") + ".safetensors" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
