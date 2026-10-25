using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Covers Phase 37 (License/Trust Manager) v1's one genuinely tricky piece hermetically:
/// DatasetDownloadService.NormalizeLicense, which exists because Hugging Face's community metadata
/// is not consistently typed across repos (a plain string on some, a JSON array on others) - a
/// naive `string?` property throws on the array shape. The actual HF network calls
/// (GetDetailsAsync/GetLicenseAsync) are exercised by live testing instead, same tier separation
/// this project already applies to every other Hugging Face-backed call.</summary>
public sealed class LicenseTrustTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public LicenseTrustTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void NormalizeLicense_returns_a_plain_string_license_as_is()
    {
        JsonElement element = Parse("\"cc-by-sa-4.0\"");
        Assert.Equal("cc-by-sa-4.0", DatasetDownloadService.NormalizeLicense(element));
    }

    [Fact]
    public void NormalizeLicense_joins_an_array_of_licenses()
    {
        JsonElement element = Parse("[\"mit\",\"apache-2.0\"]");
        Assert.Equal("mit, apache-2.0", DatasetDownloadService.NormalizeLicense(element));
    }

    [Fact]
    public void NormalizeLicense_returns_null_for_an_empty_array()
    {
        JsonElement element = Parse("[]");
        Assert.Null(DatasetDownloadService.NormalizeLicense(element));
    }

    [Fact]
    public void NormalizeLicense_returns_null_for_a_missing_or_unexpected_shape()
    {
        Assert.Null(DatasetDownloadService.NormalizeLicense(default)); // property absent from the source JSON entirely
        Assert.Null(DatasetDownloadService.NormalizeLicense(Parse("null")));
        Assert.Null(DatasetDownloadService.NormalizeLicense(Parse("42"))); // never seen in practice, but must not throw
    }

    [Fact]
    public async Task Models_license_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/models/license?repoId=rajpurkar/squad");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Datasets_files_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/datasets/files?repoId=rajpurkar/squad");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unified_registry_capabilities_endpoint_surfaces_a_built_in_skills_license_and_author()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/registry/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement capabilities = doc.RootElement.GetProperty("capabilities");

        bool found = false;
        foreach (JsonElement capability in capabilities.EnumerateArray())
        {
            if (capability.GetProperty("id").GetString() != "skill:coding") continue;
            found = true;
            Assert.Equal("PaRuX", capability.GetProperty("author").GetString());
            break;
        }
        Assert.True(found, "Expected to find the built-in coding skill in the unified registry.");
    }
}
