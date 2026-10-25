using System.Net;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserRequestAdmissionPolicyTests
{
    [Theory]
    [InlineData("http://localhost:5173/app.js", false, true)]
    [InlineData("http://127.0.0.1:5173/", false, true)]
    [InlineData("http://[::1]:5180/live", false, true)]
    [InlineData("ws://localhost:5173/hmr", false, true)]
    [InlineData("wss://127.0.0.1:5443/ws", false, true)]
    [InlineData("about:blank", false, true)]
    [InlineData("data:text/plain,hello", false, true)]
    [InlineData("https://example.org/", false, false)]
    [InlineData("http://192.168.1.1/", false, false)]
    [InlineData("http://8.8.8.8/", false, false)]
    [InlineData("ws://remote.example.com/socket", false, false)]
    [InlineData("file:///C:/private.txt", false, false)]
    [InlineData("chrome://settings/", false, false)]
    [InlineData("javascript:alert(1)", false, false)]
    [InlineData("https://user:pw@example.org", true, false)]
    [InlineData("https://example.org/", true, true)]
    [InlineData("ws://remote.example.org/socket", true, true)]
    [InlineData("", false, false)]
    [InlineData("not-a-url", true, false)]
    public void Requests_are_admitted_before_egress_according_to_the_live_internet_policy(
        string url, bool internetEnabled, bool expected)
    {
        Assert.Equal(expected,
            BrowserRequestAdmissionPolicy.IsAllowed(url, internetEnabled));
    }

    [Fact]
    public void Redirect_to_external_host_never_inherits_authority_of_local_page()
    {
        Assert.True(BrowserRequestAdmissionPolicy.IsAllowed(
            "http://localhost:5173/start", false));
        Assert.False(BrowserRequestAdmissionPolicy.IsAllowed(
            "https://login.example.org/callback", false));
    }

    [Fact]
    public void Very_long_urls_are_rejected_before_processing()
    {
        Assert.False(BrowserRequestAdmissionPolicy.IsAllowed(
            "http://localhost/" + new string('x', 9_000), true));
    }

    [Fact]
    public void Allowed_online_navigation_becomes_denied_after_toggle_changes()
    {
        const string requested = "https://example.org/redirect";
        Assert.True(BrowserRequestAdmissionPolicy.IsAllowed(requested, true));
        Assert.False(BrowserRequestAdmissionPolicy.IsAllowed(requested, false));
    }

    [Fact]
    public async Task Hostnames_are_resolved_only_while_internet_is_on_and_only_public_answers_pass()
    {
        var resolved = new List<string>();
        Func<string, CancellationToken, Task<IPAddress[]>> publicAnswer = (host, ct) =>
        {
            resolved.Add(host);
            return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
        };
        Func<string, CancellationToken, Task<IPAddress[]>> rebindingAnswer = (host, ct) =>
        {
            resolved.Add(host);
            return Task.FromResult(new[] { IPAddress.Parse("127.0.0.1") });
        };

        Assert.True(await BrowserRequestAdmissionPolicy.IsAllowedAsync(
            "https://example.org/", true, publicAnswer, CancellationToken.None));
        Assert.False(await BrowserRequestAdmissionPolicy.IsAllowedAsync(
            "https://rebind.example/", true, rebindingAnswer, CancellationToken.None));
        Assert.False(await BrowserRequestAdmissionPolicy.IsAllowedAsync(
            "https://example.org/", false, publicAnswer, CancellationToken.None));
        Assert.True(await BrowserRequestAdmissionPolicy.IsAllowedAsync(
            "http://localhost:5173/app.js", true, rebindingAnswer, CancellationToken.None));
        Assert.True(await BrowserRequestAdmissionPolicy.IsAllowedAsync(
            "about:blank", true, rebindingAnswer, CancellationToken.None));

        // Offline and explicit localhost requests never reach the resolver.
        Assert.Equal(new[] { "example.org", "rebind.example" }, resolved);
    }

    [Fact]
    public async Task Hostname_resolving_to_cloud_metadata_is_denied_with_internet_on()
    {
        Func<string, CancellationToken, Task<IPAddress[]>> metadata = (host, ct) =>
            Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") });
        Assert.False(await BrowserRequestAdmissionPolicy.IsAllowedAsync(
            "http://metadata.example/latest/meta-data/", true, metadata, CancellationToken.None));
    }
}
