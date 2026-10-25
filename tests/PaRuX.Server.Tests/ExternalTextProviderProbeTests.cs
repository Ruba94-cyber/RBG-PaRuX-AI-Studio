using System.Net;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Benchmark probes must not follow redirects and must check every connection. These tests drive the probe
/// path with an injected resolver; each case is refused before a socket is opened.
/// </summary>
public sealed class ExternalTextProviderProbeTests
{
    [Fact]
    public void Probe_handler_never_follows_redirects_sends_cookies_or_uses_a_proxy()
    {
        using SocketsHttpHandler handler = ExternalTextProviderService.CreateProbeHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
    }

    [Theory]
    [InlineData("api.example.com", "10.0.0.5", false)]
    [InlineData("api.example.com", "8.8.8.8", true)]
    [InlineData("api.example.com", "2001:db8::1", false)]
    [InlineData("localhost", "127.0.0.1", true)]
    [InlineData("localhost", "8.8.8.8", false)]
    [InlineData("192.168.1.20", "192.168.1.20", true)]
    [InlineData("192.168.1.20", "8.8.8.8", false)]
    [InlineData("[::1]", "::1", true)]
    public void Connect_rule_depends_on_whether_the_target_is_local(string host, string address, bool expected)
    {
        Assert.Equal(expected, ExternalTextProviderService.IsAllowedProbeAddress(host, IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("8.8.8.8|10.0.0.5")]
    public async Task Remote_probe_refuses_unsafe_dns_answers_without_connecting(string answers)
    {
        IPAddress[] addresses = answers.Split('|').Select(IPAddress.Parse).ToArray();
        int calls = 0;
        ValidatedRemoteConnector.HostResolver resolver = (host, cancellationToken) =>
        {
            calls++;
            return Task.FromResult(addresses);
        };

        bool reachable = await ExternalTextProviderService.ProbeAsync(
            "https://probe.example:8443", allowRemote: true, CancellationToken.None, resolver);

        Assert.False(reachable);
        Assert.True(calls >= 1, "The probe must resolve through the injected resolver before any connection.");
    }

    [Fact]
    public async Task Local_probe_refuses_a_public_dns_answer_for_localhost()
    {
        bool reachable = await ExternalTextProviderService.ProbeAsync(
            "http://localhost:11434", allowRemote: false, CancellationToken.None,
            (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));

        Assert.False(reachable);
    }
}
