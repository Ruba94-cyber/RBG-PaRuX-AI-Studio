using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Hub access-token storage: protected at rest, never returned, validated before anything is written.</summary>
public sealed class ModelHubCredentialTests : IDisposable
{
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), "parux-hub-credentials-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_stateDirectory, recursive: true); } catch { }
    }

    [Fact]
    public void A_saved_token_round_trips_through_the_secret_protector_and_is_never_stored_in_plain_text()
    {
        ModelHubTokenStore.Save(_stateDirectory, "hf_PlainTokenValue");

        string onDisk = File.ReadAllText(Path.Combine(_stateDirectory, "model-hub-token.secret"));
        Assert.DoesNotContain("hf_PlainTokenValue", onDisk);
        Assert.Equal("hf_PlainTokenValue", ModelHubTokenStore.Load(_stateDirectory));
        Assert.True(ModelHubTokenStore.Clear(_stateDirectory));
        Assert.Null(ModelHubTokenStore.Load(_stateDirectory));
        Assert.False(ModelHubTokenStore.Exists(_stateDirectory));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("hf_line\nbreak")]
    [InlineData("")]
    public void Malformed_tokens_are_refused_before_anything_is_written(string token)
    {
        Assert.Throws<InvalidDataException>(() => ModelHubTokenStore.Save(_stateDirectory, token));
        Assert.False(ModelHubTokenStore.Exists(_stateDirectory));
    }

    [Fact]
    public void A_token_longer_than_the_documented_limit_is_refused_before_anything_is_written()
    {
        Assert.Throws<InvalidDataException>(() => ModelHubTokenStore.Save(_stateDirectory, new string('a', 513)));
        Assert.False(ModelHubTokenStore.Exists(_stateDirectory));
    }

    [Fact]
    public void The_service_reports_presence_and_protection_but_never_the_token()
    {
        var service = new ModelDownloadService(stateDirectory: _stateDirectory);
        Assert.False(service.GetHubCredentialStatus().TokenConfigured);

        service.SetHubToken("hf_StatusToken");
        ModelDownloadCredentialStatus status = service.GetHubCredentialStatus();
        Assert.True(status.TokenConfigured);
        Assert.DoesNotContain("hf_StatusToken", status.Protection);

        Assert.True(service.ClearHubToken());
        Assert.False(service.GetHubCredentialStatus().TokenConfigured);
        Assert.False(service.ClearHubToken());
    }
}
