using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserUiMutationPolicyTests
{
    [Theory]
    [InlineData("http://localhost:5173/", true)]
    [InlineData("https://127.0.0.1:5001/form", true)]
    [InlineData("http://[::1]:3000/test", true)]
    [InlineData("https://LOCALHOST/app", true)]
    [InlineData("https://example.org/checkout", false)]
    [InlineData("https://example.org/", false)]
    [InlineData("https://192.168.1.1/router", false)]
    [InlineData("http://169.254.169.254/latest/meta-data/", false)]
    [InlineData("http://user:password@localhost:5173/", false)]
    [InlineData("file:///C:/secret.txt", false)]
    [InlineData("javascript:alert('X')", false)]
    [InlineData("data:text/html,ok", false)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    public void Internet_toggle_is_not_permission_for_remote_browser_click_type_or_javascript(
        string url, bool expected)
    {
        Assert.Equal(expected, BrowserUiMutationPolicy.IsAllowed(url));
    }

    [Fact]
    public void Unbounded_url_is_never_accepted_as_a_mutation_origin()
    {
        Assert.False(BrowserUiMutationPolicy.IsAllowed(
            "http://localhost/" + new string('a', 9_000)));
    }

    [Fact]
    public void Loopback_username_or_password_never_provides_mutation_authority()
    {
        Assert.False(BrowserUiMutationPolicy.IsAllowed("http://alice@127.0.0.1:3333/"));
    }
}
