using System.Text.RegularExpressions;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The browser client is what makes a headless PaRuX Core usable by a person rather than only by an
/// API client. These tests pin the two properties that make shipping it acceptable: it renders from
/// this build alone with nothing fetched from the network, and the token it is handed in a URL does
/// not stay in the address bar.
/// </summary>
public sealed class WebClientEndpointTests
{
    [Fact]
    public void The_client_is_embedded_in_the_build_rather_than_left_on_disk()
    {
        string html = WebClientEndpoint.Html;

        Assert.StartsWith("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/v1/chat/completions", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A product whose claim is that it runs on your machine cannot have a client that reaches out
    /// to render itself - and a headless Core is often on a box with no outbound access at all, so
    /// a CDN reference would not merely be impolite, it would be a blank page.
    /// </summary>
    [Fact]
    public void The_client_loads_nothing_from_the_network()
    {
        string html = WebClientEndpoint.Html;

        MatchCollection external = Regex.Matches(html, @"(src|href)\s*=\s*[""']https?://[^""']+", RegexOptions.IgnoreCase);
        Assert.True(external.Count == 0,
            "The web client references external resources: " + string.Join(", ", external.Select(m => m.Value)));

        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fonts.googleapis.com", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_token_is_removed_from_the_address_bar_as_soon_as_the_page_loads()
    {
        string html = WebClientEndpoint.Html;

        Assert.Contains("sessionStorage.setItem(\"parux-token\"", html, StringComparison.Ordinal);
        Assert.Contains("searchParams.delete(\"token\")", html, StringComparison.Ordinal);
        Assert.Contains("history.replaceState", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Authenticated_browser_requests_reject_redirects()
    {
        string html = WebClientEndpoint.Html;

        Assert.Contains("opts.redirect = \"error\";", html, StringComparison.Ordinal);
    }

    /// <summary>The cloud fallback has to be as visible here as it is in the desktop and Android
    /// clients; a third client that quietly omitted the notice would undo the guarantee.</summary>
    [Fact]
    public void A_cloud_answered_turn_is_announced_in_the_browser_client_too()
    {
        Assert.Contains("cloud_fallback", WebClientEndpoint.Html, StringComparison.Ordinal);
        Assert.Contains("left your machine", WebClientEndpoint.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:7801", "abc123", "http://127.0.0.1:7801/app?token=abc123")]
    [InlineData("http://127.0.0.1:7801/", "abc123", "http://127.0.0.1:7801/app?token=abc123")]
    [InlineData("http://127.0.0.1:7801", "a b+c", "http://127.0.0.1:7801/app?token=a%20b%2Bc")]
    public void The_printed_url_carries_an_escaped_token(string endpoint, string token, string expected)
    {
        Assert.Equal(expected, WebClientEndpoint.BuildLocalUrl(endpoint, token));
    }
}
