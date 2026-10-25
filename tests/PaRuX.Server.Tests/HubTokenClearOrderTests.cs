using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class HubTokenClearOrderTests : IDisposable
{
    private readonly string _stateDirectory = Path.Combine(
        Path.GetTempPath(), "parux-hub-clear-order-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Failed_clear_keeps_status_configured_and_delete_returns_problem_details()
    {
        ModelDownloadService models = CreateServiceWithStoredToken();
        models.ClearHubTokenStore = _ => throw new IOException("Simulated storage failure.");

        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ModelDownloadService>();
                services.AddSingleton(models);
            }));
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/models/download-credentials");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal((int)HttpStatusCode.InternalServerError, problem?.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem?.Title));

        ModelDownloadCredentialStatus? status = await client.GetFromJsonAsync<ModelDownloadCredentialStatus>(
            "/api/v1/models/download-credentials");
        Assert.True(status?.TokenConfigured);
        Assert.Equal("example-token", ModelHubTokenStore.Load(_stateDirectory));
    }

    [Fact]
    public void Clear_updates_status_only_after_the_store_reports_success()
    {
        ModelDownloadService models = CreateServiceWithStoredToken();
        bool configuredDuringClear = false;
        models.ClearHubTokenStore = directory =>
        {
            configuredDuringClear = models.GetHubCredentialStatus().TokenConfigured;
            return ModelHubTokenStore.Clear(directory);
        };

        Assert.True(models.ClearHubToken());

        Assert.True(configuredDuringClear);
        Assert.False(models.GetHubCredentialStatus().TokenConfigured);
        Assert.False(ModelHubTokenStore.Exists(_stateDirectory));
    }

    [Fact]
    public void Clear_when_the_token_file_is_already_gone_reports_unconfigured()
    {
        ModelDownloadService models = CreateServiceWithStoredToken();
        Assert.True(ModelHubTokenStore.Clear(_stateDirectory));
        Assert.True(models.GetHubCredentialStatus().TokenConfigured);

        Assert.False(models.ClearHubToken());

        Assert.False(models.GetHubCredentialStatus().TokenConfigured);
    }

    [Fact]
    public async Task Default_store_remains_protected_and_successful_delete_reports_unconfigured()
    {
        ModelDownloadService models = CreateServiceWithStoredToken();
        string onDisk = string.Join(Environment.NewLine,
            Directory.GetFiles(_stateDirectory).Select(File.ReadAllText));
        Assert.DoesNotContain("example-token", onDisk);
        Assert.Equal("example-token", ModelHubTokenStore.Load(_stateDirectory));

        string protection = ModelHubTokenStore.Protection(_stateDirectory);
        Assert.Equal(
            OperatingSystem.IsWindows(),
            protection.Contains("Windows DPAPI", StringComparison.OrdinalIgnoreCase));

        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ModelDownloadService>();
                services.AddSingleton(models);
            }));
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/models/download-credentials");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        ModelDownloadCredentialStatus? status = await client.GetFromJsonAsync<ModelDownloadCredentialStatus>(
            "/api/v1/models/download-credentials");
        Assert.False(status?.TokenConfigured);
        Assert.False(ModelHubTokenStore.Exists(_stateDirectory));
    }

    [Fact]
    public void Authorization_header_remains_limited_to_the_official_hub_host()
    {
        MethodInfo? attachAuthorization = typeof(ModelDownloadService).GetMethod(
            "AttachHubAuthorization", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(attachAuthorization);

        using var hubRequest = new HttpRequestMessage(HttpMethod.Get, "https://huggingface.co/alice/model");
        using var cdnRequest = new HttpRequestMessage(HttpMethod.Get, "https://cdn-lfs.hf.co/alice/model");
        attachAuthorization!.Invoke(null, new object?[] { hubRequest, "example-token" });
        attachAuthorization.Invoke(null, new object?[] { cdnRequest, "example-token" });

        Assert.Equal("Bearer", hubRequest.Headers.Authorization?.Scheme);
        Assert.Equal("example-token", hubRequest.Headers.Authorization?.Parameter);
        Assert.Null(cdnRequest.Headers.Authorization);
    }

    private ModelDownloadService CreateServiceWithStoredToken()
    {
        var models = new ModelDownloadService(stateDirectory: _stateDirectory);
        models.SetHubToken("example-token");
        return models;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
        }
        catch { }
    }
}
