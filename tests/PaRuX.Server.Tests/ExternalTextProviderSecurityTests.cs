using System.Net;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExternalTextProviderSecurityTests
{
    [Theory]
    [InlineData("http://127.0.0.1:11434", "http://127.0.0.1:11434")]
    [InlineData("http://localhost:1234/", "http://localhost:1234")]
    [InlineData("http://10.0.0.20:5001", "http://10.0.0.20:5001")]
    [InlineData("https://192.168.1.50:8443", "https://192.168.1.50:8443")]
    [InlineData("http://172.16.0.2:5000", "http://172.16.0.2:5000")]
    [InlineData("http://169.254.10.4:9000", "http://169.254.10.4:9000")]
    [InlineData("http://[::1]:11434", "http://[::1]:11434")]
    [InlineData("http://[fd00::20]:11434", "http://[fd00::20]:11434")]
    public void Loopback_and_private_network_endpoints_are_allowed(string input, string expected)
    {
        Assert.True(ExternalTextProviderService.TryNormalizeLocalEndpoint(input, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("https://api.openai.com")]
    [InlineData("https://example.com/v1")]
    [InlineData("http://8.8.8.8:11434")]
    [InlineData("https://user:secret@192.168.1.20:8443")]
    [InlineData("http://192.168.1.20:11434?model=x")]
    [InlineData("http://192.168.1.20:11434/#fragment")]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("not-a-url")]
    public void Public_or_ambiguous_provider_endpoints_are_rejected(string input)
    {
        Assert.False(ExternalTextProviderService.TryNormalizeLocalEndpoint(input, out _));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.255.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.100.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("1.1.1.1", false)]
    public void Address_classifier_matches_local_network_boundary(string address, bool expected)
    {
        Assert.Equal(expected, ExternalTextProviderService.IsLocalAddress(IPAddress.Parse(address)));
    }
}
