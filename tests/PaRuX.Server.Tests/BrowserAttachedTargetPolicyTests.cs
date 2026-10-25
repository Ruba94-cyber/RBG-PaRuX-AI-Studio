using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserAttachedTargetPolicyTests
{
    [Theory]
    [InlineData("page")]
    [InlineData("service_worker")]
    [InlineData("shared_worker")]
    [InlineData("worker")]
    [InlineData("iframe")]
    [InlineData("background_page")]
    public void Any_unchecked_child_execution_context_invalidates_the_whole_browser(string type)
    {
        using JsonDocument data = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            sessionId = "untrusted-session",
            targetInfo = new { type, targetId = "untrusted-child" },
            waitingForDebugger = true
        }));
        Assert.True(BrowserAttachedTargetPolicy.MustAbortSession(
            "Target.attachedToTarget", data.RootElement));
    }

    [Fact]
    public void Even_malformed_attach_events_fail_closed()
    {
        using JsonDocument empty = JsonDocument.Parse("{}");
        Assert.True(BrowserAttachedTargetPolicy.MustAbortSession(
            "Target.attachedToTarget", empty.RootElement));
        Assert.False(BrowserAttachedTargetPolicy.MustAbortSession(
            "Target.targetInfoChanged", empty.RootElement));
        Assert.False(BrowserAttachedTargetPolicy.MustAbortSession(
            "Fetch.requestPaused", empty.RootElement));
        Assert.False(BrowserAttachedTargetPolicy.MustAbortSession(
            "Runtime.consoleAPICalled", empty.RootElement));
    }
}
