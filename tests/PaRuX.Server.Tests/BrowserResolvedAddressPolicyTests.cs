using System.Net;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserResolvedAddressPolicyTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.8.9.10", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.254", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("192.0.2.10", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("203.0.113.9", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2001::1", false)]
    [InlineData("2002:c0a8:101::1", false)]
    public void Only_globally_routable_unicast_answers_are_admitted(string address, bool expected)
    {
        Assert.Equal(expected, BrowserResolvedAddressPolicy.IsGlobalUnicast(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Hostname_is_admitted_only_when_every_answer_is_public()
    {
        var resolver = new FakeResolver(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:4700:4700::1111"));
        Assert.True(await BrowserResolvedAddressPolicy.AllowsHostnameAsync(
            "example.test", resolver.Resolve, TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Equal("example.test", resolver.LastHost);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    public async Task One_non_public_answer_denies_the_hostname(string privateAnswer)
    {
        var resolver = new FakeResolver(IPAddress.Parse("93.184.216.34"), IPAddress.Parse(privateAnswer));
        Assert.False(await BrowserResolvedAddressPolicy.AllowsHostnameAsync(
            "mixed.test", resolver.Resolve, TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public async Task Empty_failed_or_hung_resolution_fails_closed()
    {
        var empty = new FakeResolver();
        Assert.False(await BrowserResolvedAddressPolicy.AllowsHostnameAsync(
            "empty.test", empty.Resolve, TimeSpan.FromSeconds(2), CancellationToken.None));

        Assert.False(await BrowserResolvedAddressPolicy.AllowsHostnameAsync(
            "broken.test",
            (host, ct) => Task.FromException<IPAddress[]>(new IOException("name not found")),
            TimeSpan.FromSeconds(2),
            CancellationToken.None));

        var slow = new FakeResolver(IPAddress.Parse("93.184.216.34")) { Delay = TimeSpan.FromSeconds(30) };
        Assert.False(await BrowserResolvedAddressPolicy.AllowsHostnameAsync(
            "slow.test", slow.Resolve, TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_being_treated_as_a_denial()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var resolver = new FakeResolver(IPAddress.Parse("93.184.216.34"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BrowserResolvedAddressPolicy.AllowsHostnameAsync(
                "cancelled.test", resolver.Resolve, TimeSpan.FromSeconds(2), cancelled.Token));
    }

    private sealed class FakeResolver
    {
        private readonly IPAddress[] _answers;

        public FakeResolver(params IPAddress[] answers) => _answers = answers;

        public TimeSpan Delay { get; init; }

        public string? LastHost { get; private set; }

        public async Task<IPAddress[]> Resolve(string host, CancellationToken ct)
        {
            LastHost = host;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return _answers;
        }
    }
}
