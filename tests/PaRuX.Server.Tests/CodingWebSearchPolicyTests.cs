using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingWebSearchPolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("C# WinUI tests", true)]
    [InlineData("source documentation", true)]
    [InlineData("line\ninjection", false)]
    [InlineData("tab\tinjection", false)]
    public void Only_short_human_readable_documentation_queries_are_accepted(
        string? value, bool expected)
    {
        Assert.Equal(expected, CodingWebSearchPolicy.IsValidQuery(value));
    }

    [Fact]
    public void Long_search_query_does_not_exhaust_external_search_provider()
    {
        Assert.True(CodingWebSearchPolicy.IsValidQuery(new string('q', 512)));
        Assert.False(CodingWebSearchPolicy.IsValidQuery(new string('q', 513)));
    }

    [Theory]
    [InlineData("https://learn.microsoft.com/en-us/windows/apps/", true)]
    [InlineData("http://example.org/docs", true)]
    [InlineData("file:///C:/secrets.txt", false)]
    [InlineData("http://127.0.0.1:8080/private", false)]
    [InlineData("http://192.168.1.20/docs", false)]
    [InlineData("http://localhost/docs", false)]
    [InlineData("http://intranet.local/docs", false)]
    [InlineData("https://alice:token@example.org/", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Search_sources_cannot_reference_private_endpoints_or_embedded_credentials(
        string source, bool allowed)
    {
        Assert.Equal(allowed, CodingWebSearchPolicy.IsSafeSourceUrl(source));
    }

    [Fact]
    public void Search_does_not_truncate_overlong_urls_into_fake_references()
    {
        Assert.False(CodingWebSearchPolicy.IsSafeSourceUrl(
            "https://example.org/" + new string('a', 2_100)));
    }

    [Theory]
    [InlineData(-100, 5)]
    [InlineData(0, 5)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(30, 5)]
    public void Search_result_count_is_always_bounded(int requested, int expected)
    {
        Assert.Equal(expected, CodingWebSearchPolicy.ResultLimit(requested));
    }
}
