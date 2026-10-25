using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Regression coverage for the legacy inference boundary. Merely storing a remote
/// credential or configuring a fallback must never activate prompt egress.
/// A future explicit API transport must use a separate, consent-checked path.
/// </summary>
public sealed class CloudInferenceBoundaryTests
{
    [Fact]
    public void UnconfiguredCloudFallbackIsNeverArmed()
    {
        // No credential or settings are needed: the shipped service's inference
        // boundary must remain closed even if settings exist from an older version.
        var service = new CloudFallbackService();
        Assert.False(service.IsArmed(out _));
    }

    [Fact]
    public async Task LegacyCloudStreamAlwaysFailsBeforeNetwork()
    {
        var service = new CloudFallbackService();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new ChatCompletionRequest();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.OpenStreamAsync(request, cancellation.Token));

        Assert.Contains("cloud inference egress is disabled", error.Message);
    }

    [Fact]
    public async Task LegacyCloudStreamRejectsNullRequest()
    {
        var service = new CloudFallbackService();
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await service.OpenStreamAsync(null!, CancellationToken.None));
    }
}
