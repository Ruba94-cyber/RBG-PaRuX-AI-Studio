using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>BIC-15: foreign listeners arriving/leaving with fake tables, and cloud-fallback no-egress.</summary>
public sealed class Bic15BoundaryTests : IDisposable
{
    private const int Port = 1234;
    private const int Owner = 4242;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-bic15-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed class FakeTables
    {
        public List<int> V4 { get; } = new() { Owner };
        public List<int> V6 { get; } = new();
        public IReadOnlyList<int> Read(int port, int family) => family == 2 ? V4.ToArray() : V6.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Foreign_listener_arriving_after_a_trusted_read_is_refused_on_the_next_check_and_trust_returns_when_it_leaves(bool ipv6)
    {
        var tables = new FakeTables();
        Assert.True(ManagedListenerOwnership.IsStrictlyOwnedBy(Port, Owner, tables.Read));

        (ipv6 ? tables.V6 : tables.V4).Add(9999); // foreign process shares the port
        Assert.False(ManagedListenerOwnership.IsStrictlyOwnedBy(Port, Owner, tables.Read),
            "a positive answer must never be reused once a foreign listener is present");

        (ipv6 ? tables.V6 : tables.V4).Remove(9999);
        Assert.True(ManagedListenerOwnership.IsStrictlyOwnedBy(Port, Owner, tables.Read));
    }

    [Fact]
    public void Foreign_listener_replacing_the_owner_is_refused_and_an_empty_table_is_never_trusted()
    {
        var tables = new FakeTables();
        tables.V4.Clear();
        Assert.False(ManagedListenerOwnership.IsStrictlyOwnedBy(Port, Owner, tables.Read));
        tables.V4.Add(9999);
        Assert.False(ManagedListenerOwnership.IsStrictlyOwnedBy(Port, Owner, tables.Read));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw new Xunit.Sdk.XunitException("cloud fallback attempted outbound request to " + request.RequestUri);
        }
    }

    private sealed class EncodingProtector : ISecretProtector
    {
        public string Description => "test";
        public string Protect(string purpose, string plaintext) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(purpose + "\u0000" + plaintext));
        public string? Unprotect(string purpose, string blob)
        {
            string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(blob));
            string prefix = purpose + "\u0000";
            return decoded.StartsWith(prefix, StringComparison.Ordinal) ? decoded[prefix.Length..] : null;
        }
    }

    [Fact]
    public async Task OpenStream_sends_no_request_even_with_enabled_valid_remote_configuration_and_credentials()
    {
        var policy = new CloudFallbackPolicy(_directory, new EncodingProtector());
        var handler = new FailingHandler();
        var service = new CloudFallbackService(policy, handler, null);
        service.Configure(new SetCloudFallbackRequest
        {
            Enabled = true, ProviderId = "openai", BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-test", ApiKey = "sk-test-not-a-real-key"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.OpenStreamAsync(new ChatCompletionRequest(), CancellationToken.None));

        Assert.Equal(0, handler.Requests);
        Assert.False(service.IsArmed(out _));
    }
}
