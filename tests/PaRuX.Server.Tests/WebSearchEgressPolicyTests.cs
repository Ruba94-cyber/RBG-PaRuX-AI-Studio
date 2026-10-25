using System.Net;
using System.Net.Sockets;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Search and page reads must obey the same egress rules as the cloud transport: no system proxy, no cookie
/// jar shared between unrelated requests, and one public-address rule that every DNS answer must satisfy.
/// Everything here is refused before a socket is created, so these tests need no network access.
/// </summary>
public sealed class WebSearchEgressPolicyTests
{
    /// <summary>The refusal raised by the validation step. Asserting it proves the host was refused by policy and not
    /// by a failed connect, which would also surface as an <see cref="HttpRequestException"/>.</summary>
    private const string UnsafeAddressMessage = "Remote provider DNS returned an unsafe address.";

    [Fact]
    public void Web_reads_never_use_a_system_proxy_or_cookies_and_follow_only_bounded_redirects()
    {
        using SocketsHttpHandler handler = WebSearchService.CreateValidatedHandler(ValidatedRemoteConnector.SystemResolver);

        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.True(handler.AllowAutoRedirect);
        Assert.Equal(5, handler.MaxAutomaticRedirections);
    }

    [Theory]
    [InlineData("100.100.100.200")]   // carrier-grade NAT, where Tailscale and provider metadata live
    [InlineData("100.64.0.1")]        // RFC 6598 shared address space
    [InlineData("169.254.1.1")]       // link-local
    [InlineData("198.18.0.1")]        // benchmarking range
    [InlineData("240.0.0.1")]         // reserved
    [InlineData("224.0.0.1")]         // multicast
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]                // IPv6 unspecified
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]  // IPv4-mapped loopback
    [InlineData("::ffff:100.64.0.1")] // IPv4-mapped carrier-grade NAT
    [InlineData("2002:0a00:0001::1")] // 6to4 embedding 10.0.0.1
    [InlineData("fc00::1")]           // unique local
    [InlineData("fe80::1")]           // link-local
    [InlineData("ff02::1")]           // multicast
    public void Web_reads_refuse_every_address_that_is_not_global(string address) =>
        Assert.False(WebSearchService.IsAllowedAddress("example.test", IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    public void Web_reads_accept_global_unicast_addresses(string address) =>
        Assert.True(WebSearchService.IsAllowedAddress("example.test", IPAddress.Parse(address)));

    /// <summary>A host with one public answer and one carrier-grade answer is refused as a whole. Connecting to
    /// the public answer would still let the carrier-grade one be used by a later attempt or by a rebinding DNS.</summary>
    [Fact]
    public async Task One_non_global_answer_refuses_the_whole_host()
    {
        ValidatedRemoteConnector.HostResolver resolver = (host, cancellationToken) => Task.FromResult(new[]
        {
            IPAddress.Parse("93.184.216.34"),
            IPAddress.Parse("100.64.0.1")
        });

        // Validation only: no socket is opened, so a weakened predicate cannot turn this test into a live connection.
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            ValidatedRemoteConnector.ResolveAllowedAsync("mixed.example.test", resolver, WebSearchService.IsAllowedAddress,
                CancellationToken.None));
        Assert.Equal(UnsafeAddressMessage, ex.Message);
    }

    [Fact]
    public async Task A_host_that_resolves_only_to_loopback_is_refused_before_any_socket_is_created()
    {
        // A real loopback listener proves the negative: a refused host must not leave even a pending connection.
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            ValidatedRemoteConnector.HostResolver resolver = (host, cancellationToken) =>
                Task.FromResult(new[] { IPAddress.Loopback });

            HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => ValidatedRemoteConnector.ConnectAsync(
                new DnsEndPoint("localhost.example.test", port), resolver, WebSearchService.IsAllowedAddress, CancellationToken.None));
            Assert.Equal(UnsafeAddressMessage, ex.Message);
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>DNS is consulted once per connection, and the socket goes to the answer that was validated. The
    /// answer a rebinding DNS returns on a later lookup is never dialled. Loopback stands in for the public answer so
    /// the test never reaches the internet.</summary>
    [Fact]
    public async Task A_later_dns_answer_cannot_redirect_a_connection_that_was_already_validated()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            int lookups = 0;
            ValidatedRemoteConnector.HostResolver resolver = (host, cancellationToken) =>
            {
                lookups++;
                // The first answer was validated. The rebinding answer below must never be used.
                return Task.FromResult(lookups == 1
                    ? new[] { IPAddress.Loopback }
                    : new[] { IPAddress.Parse("10.0.0.1") });
            };

            using Stream stream = await ValidatedRemoteConnector.ConnectAsync(
                new DnsEndPoint("rebinding.example.test", port), resolver,
                (host, address) => IPAddress.IsLoopback(address), CancellationToken.None);
            using TcpClient accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, lookups);
            Assert.Equal(port, ((IPEndPoint)accepted.Client.LocalEndPoint!).Port);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Every connection looks the host up again, so a rebinding answer on a later connection is the one to
    /// refuse. The first, validated connection reaches its listener. The second answer is private and fails validation,
    /// so the connector never dials it and the listener sees nothing new.</summary>
    [Fact]
    public async Task A_rebinding_answer_on_a_later_connection_is_refused_before_any_socket_is_created()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            int lookups = 0;
            ValidatedRemoteConnector.HostResolver resolver = (host, cancellationToken) =>
            {
                lookups++;
                return Task.FromResult(lookups == 1
                    ? new[] { IPAddress.Loopback }
                    : new[] { IPAddress.Parse("10.0.0.1") });
            };
            // Accepts loopback so the test can dial a local listener. The production predicate refuses loopback, and
            // it refuses 10.0.0.1 by the same rule that this predicate applies.
            static bool AcceptsOnlyLoopback(string host, IPAddress address) => IPAddress.IsLoopback(address);

            using Stream first = await ValidatedRemoteConnector.ConnectAsync(
                new DnsEndPoint("rebinding.example.test", port), resolver, AcceptsOnlyLoopback, CancellationToken.None);
            using TcpClient accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, lookups);

            HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
                ValidatedRemoteConnector.ConnectAsync(
                    new DnsEndPoint("rebinding.example.test", port), resolver, AcceptsOnlyLoopback, CancellationToken.None));
            Assert.Equal(UnsafeAddressMessage, ex.Message);
            Assert.Equal(2, lookups);
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }
}
