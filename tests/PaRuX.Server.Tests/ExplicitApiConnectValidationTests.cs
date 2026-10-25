using System.Net;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Connect-time address policy driven by an injected resolver. Every case here is refused before a socket is
/// opened, so the tests never generate real network traffic.
/// </summary>
public sealed class ExplicitApiConnectValidationTests
{
    private sealed class FakeResolver
    {
        private readonly IPAddress[] _answers;

        public FakeResolver(params string[] answers) =>
            _answers = answers.Select(IPAddress.Parse).ToArray();

        public int Calls { get; private set; }

        public Task<IPAddress[]> Resolve(string host, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_answers);
        }
    }

    private static bool PublicOnly(string host, IPAddress address) =>
        ExplicitApiInferenceTransport.IsPublicAddress(address);

    [Fact]
    public async Task OnePrivateAnswerRefusesTheWholeHost()
    {
        var resolver = new FakeResolver("8.8.8.8", "10.0.0.5");
        await Assert.ThrowsAsync<HttpRequestException>(() => ValidatedRemoteConnector.ResolveAllowedAsync(
            "provider.example", resolver.Resolve, PublicOnly, CancellationToken.None));
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task EmptyAnswerIsRefused()
    {
        var resolver = new FakeResolver();
        await Assert.ThrowsAsync<HttpRequestException>(() => ValidatedRemoteConnector.ResolveAllowedAsync(
            "provider.example", resolver.Resolve, PublicOnly, CancellationToken.None));
    }

    [Fact]
    public async Task ConnectRefusesAnUnsafeAnswerBeforeOpeningASocket()
    {
        var resolver = new FakeResolver("192.168.1.20");
        await Assert.ThrowsAsync<HttpRequestException>(() => ValidatedRemoteConnector.ConnectAsync(
            new DnsEndPoint("provider.example", 443), resolver.Resolve, PublicOnly, CancellationToken.None));
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task PublicAnswersPassTheAddressRuleBeforeAnyConnection()
    {
        var resolver = new FakeResolver("8.8.8.8", "1.1.1.1");
        IPAddress[] accepted = await ValidatedRemoteConnector.ResolveAllowedAsync(
            "provider.example", resolver.Resolve, PublicOnly, CancellationToken.None);
        Assert.Equal(new[] { "8.8.8.8", "1.1.1.1" }, accepted.Select(address => address.ToString()));
    }

    [Fact]
    public void ConnectHandlerNeverFollowsRedirectsSendsCookiesOrUsesAProxy()
    {
        using SocketsHttpHandler handler = ValidatedRemoteConnector.CreateHandler(PublicOnly);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
    }
}
