using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A private IPv4 address is not a private network. Windows Firewall applies its rules by the
/// category the user assigned to the network, and this PC was measured on [redacted private LAN address] -
/// unmistakably RFC1918 - attached to a router Windows classes as Public. PaRuX enabled LAN access
/// and told the user to allow PaRuX Core "on Private networks only", advice that could not be
/// followed there; a phone on the same Wi-Fi then failed with "Couldn't reach PaRuX PC" and the PC
/// explained nothing. The probe exists so PaRuX can report what Windows really says.
/// </summary>
public sealed class NetworkCategoryProbeTests
{
    [Fact]
    public void The_category_is_one_of_the_values_the_user_interface_knows_how_to_explain()
    {
        string described = NetworkCategoryProbe.Describe(NetworkCategoryProbe.GetConnectedCategory());

        Assert.Contains(described, new[] { "Public", "Private", "Domain", "unknown" });
    }

    [Fact]
    public void An_unreadable_category_is_reported_as_unknown_rather_than_guessed_to_be_private()
    {
        // The failure mode that matters: never claim Private when nothing was read, because that is
        // the one answer that would make PaRuX print firewall advice it has no evidence for.
        Assert.Equal("unknown", NetworkCategoryProbe.Describe(NetworkCategoryProbe.Category.Unknown));
        Assert.Equal("Public", NetworkCategoryProbe.Describe(NetworkCategoryProbe.Category.Public));
        Assert.Equal("Private", NetworkCategoryProbe.Describe(NetworkCategoryProbe.Category.Private));
        Assert.Equal("Domain", NetworkCategoryProbe.Describe(NetworkCategoryProbe.Category.DomainAuthenticated));
    }

    [Fact]
    public void The_remote_access_status_reports_the_category_so_the_user_interface_can_explain_it()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-netcat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (LocalAiPaths.UseRootForTests(root))
        {
            var service = new RemoteAccessService();

            RemoteAccessStatus status = service.GetStatus();

            Assert.False(string.IsNullOrWhiteSpace(status.NetworkCategory));
            Assert.Contains(status.NetworkCategory, new[] { "Public", "Private", "Domain", "unknown" });
        }
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
