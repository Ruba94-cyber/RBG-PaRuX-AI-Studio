using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserNetworkEvidencePolicyTests
{
    [Theory]
    [InlineData("https://example.org/path?access_token=SECRET123#private", "https://example.org/path")]
    [InlineData("http://user:pass@localhost:5173/config?a=b", "http://localhost:5173/config")]
    [InlineData("ws://127.0.0.1:4000/socket?session=123", "ws://127.0.0.1:4000/socket")]
    public void Network_events_never_forward_embedded_credentials_or_query_tokens(
        string url, string expected)
    {
        string safe = BrowserNetworkEvidencePolicy.DisplayUrl(url);
        Assert.Equal(expected, safe);
        Assert.DoesNotContain("SECRET123", safe);
        Assert.DoesNotContain("pass@", safe);
        Assert.DoesNotContain("session=123", safe);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///C:/Users/private.txt")]
    [InlineData("javascript:alert(1)")]
    public void Nonweb_protocols_are_not_rendered_as_model_network_evidence(string url)
    {
        Assert.StartsWith("(", BrowserNetworkEvidencePolicy.DisplayUrl(url));
    }

    [Fact]
    public void Long_network_urls_are_bounded()
    {
        Assert.True(BrowserNetworkEvidencePolicy.DisplayUrl(
            "https://example.org/" + new string('x', 3_000)).Length <= 251);
    }
}
