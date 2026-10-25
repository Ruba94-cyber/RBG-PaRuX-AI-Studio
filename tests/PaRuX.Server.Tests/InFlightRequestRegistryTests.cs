using System.Net;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Revocation must cancel work that was already admitted under the revoked authority, not only refuse new work. Each
/// case uses an isolated registry, and every remote target is a name answered by an injected resolver that never
/// returns, so nothing here opens a socket or reaches the network.
/// </summary>
public sealed class InFlightRequestRegistryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-inflight-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Cancelling_a_scope_cancels_its_leases_and_nothing_else()
    {
        var registry = new InFlightRequestRegistry();
        using InFlightRequestLease revoked = registry.Begin(InFlightRequestRegistry.ApiKeyScope("k1"), CancellationToken.None);
        using InFlightRequestLease other = registry.Begin(InFlightRequestRegistry.ApiKeyScope("k2"), CancellationToken.None);

        int cancelled = registry.Cancel(InFlightRequestRegistry.ApiKeyScope("k1"));

        Assert.Equal(1, cancelled);
        Assert.True(revoked.Token.IsCancellationRequested);
        Assert.False(other.Token.IsCancellationRequested);
        Assert.Equal(0, registry.Cancel(InFlightRequestRegistry.ApiKeyScope("k1")));
    }

    [Fact]
    public void A_lease_is_cancelled_when_its_request_aborts()
    {
        var registry = new InFlightRequestRegistry();
        using var aborted = new CancellationTokenSource();
        using InFlightRequestLease lease = registry.Begin("scope", aborted.Token);

        aborted.Cancel();

        Assert.True(lease.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_finished_lease_is_forgotten_so_a_later_revocation_reaches_no_finished_work()
    {
        var registry = new InFlightRequestRegistry();
        InFlightRequestLease lease = registry.Begin("scope", CancellationToken.None);
        Assert.Equal(1, registry.ActiveCount("scope"));

        lease.Dispose();

        Assert.Equal(0, registry.ActiveCount("scope"));
        Assert.Equal(0, registry.Cancel("scope"));
    }

    [Fact]
    public void Revoking_an_API_key_cancels_its_admitted_requests_and_refuses_the_next_one()
    {
        var registry = new InFlightRequestRegistry();
        var keys = new ApiKeyService(Path.Combine(_directory, "api-keys.json"), registry);
        ApiKeyCreated first = keys.Create("first");
        ApiKeyCreated second = keys.Create("second");
        using InFlightRequestLease firstRequest = registry.Begin(InFlightRequestRegistry.ApiKeyScope(first.Id), CancellationToken.None);
        using InFlightRequestLease secondRequest = registry.Begin(InFlightRequestRegistry.ApiKeyScope(second.Id), CancellationToken.None);

        Assert.True(keys.Revoke(first.Id));

        Assert.True(firstRequest.Token.IsCancellationRequested);
        Assert.False(secondRequest.Token.IsCancellationRequested);
        Assert.False(keys.IsActive(first.Id));
        Assert.True(keys.IsActive(second.Id));
        Assert.False(keys.TryValidate(first.Key, out _));
        Assert.True(keys.TryValidate(second.Key, out _));
    }

    [Fact]
    public void Turning_remote_access_off_cancels_remote_calls_and_leaves_API_key_requests_running()
    {
        var registry = new InFlightRequestRegistry();
        var policy = new RemoteProviderPolicy(_directory, new PassthroughProtector(), registry);
        using InFlightRequestLease remoteCall = registry.Begin(InFlightRequestRegistry.RemoteProviderScope, CancellationToken.None);
        using InFlightRequestLease apiKeyRequest = registry.Begin(InFlightRequestRegistry.ApiKeyScope("k"), CancellationToken.None);

        policy.SetRemoteAllowed(true);
        Assert.False(remoteCall.Token.IsCancellationRequested);

        policy.SetRemoteAllowed(false);

        Assert.True(remoteCall.Token.IsCancellationRequested);
        Assert.False(apiKeyRequest.Token.IsCancellationRequested);
        Assert.False(policy.IsRemoteAllowed());
    }

    [Fact]
    public async Task Turning_remote_access_off_cancels_a_benchmark_probe_that_is_still_resolving_its_target()
    {
        var registry = new InFlightRequestRegistry();
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> probe = ExternalTextProviderService.ProbeAsync(
            "https://probe.example:8443", allowRemote: true, CancellationToken.None,
            async (host, ct) =>
            {
                resolving.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return Array.Empty<IPAddress>();
            },
            registry);
        await resolving.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, registry.ActiveCount(InFlightRequestRegistry.RemoteProviderScope));

        registry.Cancel(InFlightRequestRegistry.RemoteProviderScope);

        Assert.False(await probe.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Turning_the_cloud_fallback_off_cancels_a_credential_check_that_is_still_connecting()
    {
        var registry = new InFlightRequestRegistry();
        var policy = new CloudFallbackPolicy(_directory, new PassthroughProtector());
        policy.Save(enabled: true, "custom", "https://api.inflight-check.example/v1", "a-model", "sk-inflight-check");
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new CloudFallbackService(policy,
            async (host, ct) =>
            {
                resolving.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return Array.Empty<IPAddress>();
            },
            registry);

        Task<CloudFallbackTestResult> check = service.TestAsync(CancellationToken.None);
        await resolving.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, registry.ActiveCount(InFlightRequestRegistry.CloudFallbackScope));

        service.Disable();

        CloudFallbackTestResult result = await check.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(result.Success);
        Assert.Contains("cancelled", result.Detail, StringComparison.Ordinal);
        Assert.False(service.GetStatus().Enabled);
        Assert.Equal(0, registry.ActiveCount(InFlightRequestRegistry.CloudFallbackScope));
    }

    /// <summary>Reversible, test-only protection: the real protectors are machine- and account-bound.</summary>
    private sealed class PassthroughProtector : ISecretProtector
    {
        public string Description => "test-only reversible encoding";

        public string Protect(string purpose, string plaintext) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(purpose + "\u0000" + plaintext));

        public string? Unprotect(string purpose, string blob)
        {
            try
            {
                string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(blob));
                string prefix = purpose + "\u0000";
                return decoded.StartsWith(prefix, StringComparison.Ordinal) ? decoded[prefix.Length..] : null;
            }
            catch (FormatException) { return null; }
        }
    }
}
