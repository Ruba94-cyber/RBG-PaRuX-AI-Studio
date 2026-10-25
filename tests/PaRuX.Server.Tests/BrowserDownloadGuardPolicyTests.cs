using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserDownloadGuardPolicyTests
{
    [Fact]
    public void Session_guard_sends_only_a_deny_download_behavior()
    {
        (string method, object parameters) = BrowserDownloadGuardPolicy.DenyAllDownloads();

        Assert.Equal("Browser.setDownloadBehavior", method);
        Assert.Equal("{\"behavior\":\"deny\"}", JsonSerializer.Serialize(parameters));
    }

    [Fact]
    public void Deny_command_never_names_a_download_directory_or_allow_mode()
    {
        (_, object parameters) = BrowserDownloadGuardPolicy.DenyAllDownloads();
        string json = JsonSerializer.Serialize(parameters);

        Assert.DoesNotContain("allow", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("downloadPath", json, StringComparison.OrdinalIgnoreCase);
    }
}
