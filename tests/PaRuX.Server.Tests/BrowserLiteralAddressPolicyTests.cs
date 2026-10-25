using System;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserLiteralAddressPolicyTests
{
    [Theory]
    [InlineData("http://localhost:5173", false)]
    [InlineData("http://127.0.0.1:5173", false)]
    [InlineData("http://[::1]:5173", false)]
    [InlineData("http://[::ffff:127.0.0.1]:5173", false)]
    [InlineData("http://8.8.8.8", false)]
    [InlineData("https://93.184.216.34", false)]
    [InlineData("https://[2001:4860:4860::8888]", false)]
    [InlineData("http://192.168.1.1", true)]
    [InlineData("http://10.0.0.1", true)]
    [InlineData("http://172.31.255.254", true)]
    [InlineData("http://100.100.100.200", true)]
    [InlineData("http://169.254.169.254", true)]
    [InlineData("http://198.18.0.1", true)]
    [InlineData("http://192.0.2.1", true)]
    [InlineData("http://224.0.0.1", true)]
    [InlineData("http://[fd12::abcd]", true)]
    [InlineData("http://[::2]", true)]
    [InlineData("http://[fe80::1]", true)]
    [InlineData("http://[2001:db8::1]", true)]
    [InlineData("http://[::ffff:192.168.1.1]", true)]
    public void Private_or_reserved_literal_addresses_are_denied(
        string url, bool expectedBlocked)
    {
        Assert.Equal(expectedBlocked,
            BrowserLiteralAddressPolicy.IsDisallowed(new Uri(url)));
    }

    [Fact]
    public void Unresolved_hostnames_are_not_treated_as_public_ip_verification()
    {
        // DNS rebinding/host resolution is an explicit separate OS-network gap.
        Assert.False(BrowserLiteralAddressPolicy.IsDisallowed(
            new Uri("https://example.org/")));
    }

    [Theory]
    [InlineData("http://192.168.1.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.100.100.200/latest/meta-data/")]
    [InlineData("http://[fd00::1]/secret")]
    public void Enabled_internet_still_denies_local_network_targets(
        string url)
    {
        Assert.False(BrowserRequestAdmissionPolicy.IsAllowed(url, true));
        Assert.False(BrowserRedirectObservationPolicy.IsAllowedPage(url, true));
    }

    [Fact]
    public void Public_web_and_loopback_web_are_still_usable_when_online()
    {
        Assert.True(BrowserRequestAdmissionPolicy.IsAllowed(
            "https://example.org/", true));
        Assert.True(BrowserRequestAdmissionPolicy.IsAllowed(
            "https://8.8.8.8/", true));
        Assert.True(BrowserRequestAdmissionPolicy.IsAllowed(
            "http://127.0.0.1:5173/", true));
        Assert.True(BrowserRedirectObservationPolicy.IsAllowedPage(
            "http://127.0.0.1:5173/", true));
    }
}
