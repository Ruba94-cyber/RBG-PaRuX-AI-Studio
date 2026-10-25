using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserNavigationPlannerTests
{
    [Theory]
    [InlineData("Vai su openai.com", "https://openai.com/")]
    [InlineData("Visit https://example.com/docs", "https://example.com/docs")]
    public void Explicit_web_targets_create_https_navigation(string input, string expectedUrl)
    {
        Assert.True(BrowserNavigationPlanner.TryFind(input, out BrowserNavigationTarget? target));
        Assert.NotNull(target);
        Assert.Equal(expectedUrl, target!.AbsoluteUri);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    public void Non_http_schemes_are_rejected(string input) =>
        Assert.False(BrowserNavigationPlanner.TryNormalize(input, out _));
}
