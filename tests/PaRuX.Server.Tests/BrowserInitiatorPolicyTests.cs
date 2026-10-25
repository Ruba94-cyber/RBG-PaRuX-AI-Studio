using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserInitiatorPolicyTests
{
    [Theory]
    [InlineData("http://localhost:5173/", true)]
    [InlineData("http://127.0.0.1:8080/", true)]
    [InlineData("http://127.0.0.2/", true)]
    [InlineData("http://[::1]:5180/live", true)]
    [InlineData("https://example.org/", false)]
    [InlineData("http://192.168.1.10/", false)]
    [InlineData("http://10.0.0.5/", false)]
    public void Only_loopback_hosts_are_local_destinations(string url, bool expected)
    {
        Assert.Equal(expected, BrowserInitiatorPolicy.IsLoopbackDestination(new Uri(url)));
        Assert.False(BrowserInitiatorPolicy.IsLoopbackDestination(null));
    }

    [Fact]
    public void Loopback_requests_are_admitted_from_the_empty_page_and_from_local_documents()
    {
        Assert.True(BrowserInitiatorPolicy.MayIssueLoopbackRequest(Array.Empty<string?>()));
        Assert.True(BrowserInitiatorPolicy.MayIssueLoopbackRequest(new string?[] { "about:blank" }));
        Assert.True(BrowserInitiatorPolicy.MayIssueLoopbackRequest(new string?[] { "http://127.0.0.1:8080/app" }));
        Assert.True(BrowserInitiatorPolicy.MayIssueLoopbackRequest(
            new string?[] { "http://localhost:3000/", "http://127.0.0.1:8080/frame" }));
        Assert.True(BrowserInitiatorPolicy.MayIssueLoopbackRequest(
            new string?[] { "about:blank", "http://127.0.0.1:8080/app" }));
        Assert.True(BrowserInitiatorPolicy.MayIssueLoopbackRequest(
            new string?[] { "about:srcdoc", "http://[::1]:5180/" }));
    }

    [Fact]
    public void A_public_document_anywhere_in_the_chain_denies_a_loopback_request()
    {
        Assert.False(BrowserInitiatorPolicy.MayIssueLoopbackRequest(new string?[] { "https://example.org/" }));
        Assert.False(BrowserInitiatorPolicy.MayIssueLoopbackRequest(
            new string?[] { "https://ads.example/frame", "http://127.0.0.1:8080/app" }));
        Assert.False(BrowserInitiatorPolicy.MayIssueLoopbackRequest(
            new string?[] { "about:blank", "https://example.org/" }));
        Assert.False(BrowserInitiatorPolicy.MayIssueLoopbackRequest(
            new string?[] { "http://127.0.0.1:8080/app", "https://example.org/" }));
    }

    [Fact]
    public void Unknown_opaque_or_non_loopback_initiators_are_denied()
    {
        Assert.False(BrowserInitiatorPolicy.MayIssueLoopbackRequest(null));
        Assert.False(BrowserInitiatorPolicy.MayIssueLoopbackRequest(new string?[] { null }));
        Assert.False(BrowserInitiatorPolicy.MayIssueLoopbackRequest(new string?[] { "http://127.0.0.1:8080/", null }));
        Assert.False(BrowserInitiatorPolicy.MayIssueLoopbackRequest(new string?[] { "null" }));
        Assert.False(BrowserInitiatorPolicy.MayIssueLoopbackRequest(new string?[] { "data:text/html,<script>" }));
        Assert.False(BrowserInitiatorPolicy.MayIssueLoopbackRequest(new string?[] { "http://192.168.1.10/app" }));
    }
}
