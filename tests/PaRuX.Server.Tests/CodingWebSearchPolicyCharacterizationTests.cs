using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingWebSearchPolicyCharacterizationTests
{
    [Fact]
    public void CodingWebSearchPolicy_exposes_fixed_public_constants()
    {
        Assert.Equal(512, CodingWebSearchPolicy.MaximumQueryCharacters);
        Assert.Equal(5, CodingWebSearchPolicy.MaximumResultCount);
    }

    [Theory]
    [InlineData("a\bb")]
    [InlineData("a\u0001b")]
    [InlineData("a\u007fb")]
    [InlineData("a\u0085b")]
    public void CodingWebSearchPolicy_rejects_queries_containing_control_characters(string query)
    {
        Assert.False(CodingWebSearchPolicy.IsValidQuery(query));
    }

    [Fact]
    public void CodingWebSearchPolicy_accepts_whitespace_only_padding_around_a_real_query()
    {
        Assert.True(CodingWebSearchPolicy.IsValidQuery("  docs for C#  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CodingWebSearchPolicy_rejects_blank_source_url_references(string? value)
    {
        Assert.False(CodingWebSearchPolicy.IsSafeSourceUrl(value));
    }

    [Theory]
    [InlineData("http://[::1]/docs")]
    [InlineData("https://[2001:db8::1]/docs")]
    [InlineData("http://0.0.0.0/private")]
    public void CodingWebSearchPolicy_rejects_ip_literal_and_any_address_hosts(string value)
    {
        Assert.False(CodingWebSearchPolicy.IsSafeSourceUrl(value));
    }

    [Theory]
    [InlineData("http://foo.internal/")]
    [InlineData("https://svc.internal/")]
    public void CodingWebSearchPolicy_rejects_internal_suffix_hosts(string value)
    {
        Assert.False(CodingWebSearchPolicy.IsSafeSourceUrl(value));
    }

    [Fact]
    public void CodingWebSearchPolicy_ignores_single_trailing_dot_on_allowed_host()
    {
        Assert.True(CodingWebSearchPolicy.IsSafeSourceUrl("https://example.org./docs"));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    public void CodingWebSearchPolicy_result_limit_never_exceeds_maximum(int requested)
    {
        int limit = CodingWebSearchPolicy.ResultLimit(requested);

        Assert.InRange(limit, 1, CodingWebSearchPolicy.MaximumResultCount);
    }
}
