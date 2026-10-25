using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserRedirectObservationPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5173/", false, true)]
    [InlineData("http://localhost:5000/", false, true)]
    [InlineData("http://[::1]:3000/", false, true)]
    [InlineData("about:blank", false, true)]
    [InlineData("https://example.org/docs", false, false)]
    [InlineData("http://192.168.0.1/admin", false, false)]
    [InlineData("https://example.org/docs", true, true)]
    [InlineData("file:///C:/Users/private.txt", true, false)]
    [InlineData("javascript:alert(1)", true, false)]
    [InlineData("https://user:password@example.org/", true, false)]
    [InlineData("", false, false)]
    public void Post_navigation_scope_is_stricter_when_internet_is_disabled(
        string page, bool internetEnabled, bool allowed)
    {
        Assert.Equal(allowed,
            BrowserRedirectObservationPolicy.IsAllowedPage(page, internetEnabled));
    }

    [Fact]
    public void A_local_page_cannot_redirect_to_an_external_site_without_internet_authority()
    {
        Assert.True(BrowserRedirectObservationPolicy.IsAllowedPage(
            "http://localhost:5173/test", false));
        Assert.False(BrowserRedirectObservationPolicy.IsAllowedPage(
            "https://example.com/login", false));
    }
}
