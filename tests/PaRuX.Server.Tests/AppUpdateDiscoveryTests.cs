using System.Net;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AppUpdateDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GitHub_404_never_proves_that_no_update_exists(bool authenticated)
    {
        AppReleaseStatus result = AppUpdateClient.DiscoveryHttpFailure(
            "1.8.0", HttpStatusCode.NotFound, authenticated);

        Assert.False(result.CheckSucceeded);
        Assert.False(result.UpdateAvailable);
        Assert.Equal("unknown-not-found-or-inaccessible", result.DiscoveryState);
        Assert.Equal(authenticated, result.DiscoveryAuthenticated);
        Assert.Contains("cannot verify", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not performed", result.SecureInstallerMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unauthenticated_404_explains_the_private_repository_ambiguity_and_opt_in_auth()
    {
        AppReleaseStatus result = AppUpdateClient.DiscoveryHttpFailure(
            "1.8.0", HttpStatusCode.NotFound, authenticated: false);

        Assert.Contains("Private repositories", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AppUpdateClient.GitHubUpdateTokenEnvironmentVariable, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejected_credentials_are_not_reported_as_a_successful_release_check()
    {
        AppReleaseStatus result = AppUpdateClient.DiscoveryHttpFailure(
            "1.8.0", HttpStatusCode.Unauthorized, authenticated: true);

        Assert.False(result.CheckSucceeded);
        Assert.Equal("authentication-rejected", result.DiscoveryState);
        Assert.True(result.DiscoveryAuthenticated);
        Assert.Contains("unknown", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forbidden_or_rate_limited_discovery_is_explicitly_unverified()
    {
        AppReleaseStatus result = AppUpdateClient.DiscoveryHttpFailure(
            "1.8.0", HttpStatusCode.Forbidden, authenticated: true);

        Assert.False(result.CheckSucceeded);
        Assert.Equal("forbidden-or-rate-limited", result.DiscoveryState);
        Assert.Contains("prevented update verification", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Other_http_failures_do_not_collapse_to_no_update()
    {
        AppReleaseStatus result = AppUpdateClient.DiscoveryHttpFailure(
            "1.8.0", HttpStatusCode.ServiceUnavailable, authenticated: false);

        Assert.False(result.CheckSucceeded);
        Assert.Equal("http-error", result.DiscoveryState);
        Assert.Contains("unknown", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => f(r, ct);
    }

    private static Task<AppReleaseStatus> Check(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f, TimeSpan? timeout = null, CancellationToken ct = default)
        => AppUpdateClient.Current.CheckLatestAsync(
            new HttpClient(new StubHandler(f)) { Timeout = timeout ?? TimeSpan.FromSeconds(5) },
            "http://127.0.0.1/releases/latest", null, "1.8.0", ct);

    private static Task<HttpResponseMessage> Respond(HttpStatusCode c, string body = "")
        => Task.FromResult(new HttpResponseMessage(c) { Content = new StringContent(body) });

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication-rejected")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden-or-rate-limited")]
    [InlineData(HttpStatusCode.NotFound, "unknown-not-found-or-inaccessible")]
    [InlineData(HttpStatusCode.InternalServerError, "http-error")]
    public async Task Http_failures_yield_distinct_unverified_states(HttpStatusCode code, string state)
    {
        AppReleaseStatus r = await Check((_, _) => Respond(code));
        Assert.False(r.CheckSucceeded);
        Assert.False(r.UpdateAvailable);
        Assert.Equal(state, r.DiscoveryState);
        Assert.DoesNotContain("up to date", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not older", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not json {{")]
    [InlineData("{}")]
    [InlineData("")]
    public async Task Garbage_or_empty_json_is_invalid_metadata_not_no_update(string body)
    {
        AppReleaseStatus r = await Check((_, _) => Respond(HttpStatusCode.OK, body));
        Assert.False(r.CheckSucceeded);
        Assert.Equal("invalid-release-metadata", r.DiscoveryState);
        Assert.DoesNotContain("not older", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Timeout_is_a_typed_unverified_result_not_a_cancellation_or_no_update()
    {
        AppReleaseStatus r = await Check(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }, TimeSpan.FromMilliseconds(150));
        Assert.False(r.CheckSucceeded);
        Assert.Equal("timeout", r.DiscoveryState);
        Assert.Contains("unknown", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_cancellation_still_propagates()
    {
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Check(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }, null, cts.Token));
    }

    [Fact]
    public async Task Verified_current_release_is_the_only_success_without_update()
    {
        AppReleaseStatus r = await Check((_, _) => Respond(HttpStatusCode.OK, "{\"tag_name\":\"v1.8.0\",\"name\":\"x\"}"));
        Assert.True(r.CheckSucceeded);
        Assert.False(r.UpdateAvailable);
        Assert.Equal("verified-release", r.DiscoveryState);
    }
}
