using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The built-in skill and the Settings caption tell the user what a paired device may do on this PC. Both used to say that
/// paired devices could never reach Computer Use, while the Windows worker starts their tasks without a per-task prompt
/// when Full Computer Use is on. The text is pinned to the boundary the code enforces. Whether remote tasks should need a
/// per-task approval even with the switch on is a product decision, and it is not decided here.
/// </summary>
public sealed class ComputerUseManifestTextTests
{
    [Fact]
    public void The_built_in_skill_no_longer_claims_paired_devices_are_excluded()
    {
        string root = FindRepositoryRoot();
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "skills", "built-in", "computer-use.json")));
        string description = manifest.RootElement.GetProperty("description").GetString() ?? string.Empty;

        Assert.DoesNotContain("never exposed to paired devices", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("paired Android device", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one-task approval", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_Full_Computer_Use_caption_says_paired_devices_can_start_tasks_without_a_prompt()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src", "PaRuX.App", "Views", "SettingsView.xaml"));

        Assert.Contains("Paired Android devices can start Computer Use tasks on this PC", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test output directory.");
    }
}
