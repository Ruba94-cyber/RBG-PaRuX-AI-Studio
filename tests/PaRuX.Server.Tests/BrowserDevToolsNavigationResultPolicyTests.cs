using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserDevToolsNavigationResultPolicyTests
{
    [Theory]
    [InlineData("""{"frameId":"C0FFEE"}""", false, true)]
    [InlineData("""{"frameId":"C0FFEE"}""", true, false)]
    [InlineData("""{"frameId":"C0FFEE","errorText":"net::ERR_BLOCKED_BY_CLIENT"}""", false, false)]
    [InlineData("""{"errorText":"net::ERR_CONNECTION_REFUSED"}""", false, false)]
    [InlineData("""{"frameId":"C0FFEE","isDownload":true}""", false, false)]
    [InlineData("""{"frameId":"C0FFEE","isDownload":false}""", false, true)]
    [InlineData("""[]""", false, false)]
    public void Blocked_redirect_and_failed_navigation_are_not_recorded_as_success(
        string reply, bool blockedRequest, bool expected)
    {
        using JsonDocument doc = JsonDocument.Parse(reply);
        Assert.Equal(expected,
            BrowserDevToolsNavigationResultPolicy.IsSuccessful(doc.RootElement, blockedRequest));
    }
}
