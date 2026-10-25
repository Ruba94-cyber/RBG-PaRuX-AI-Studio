using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MobileSkillCreationPolicyTests
{
    [Fact]
    public void Sanitizer_discards_every_desktop_privilege_and_caller_identity()
    {
        var input = new SkillManifest
        {
            Id = "builtin-dangerous",
            Name = "Dangerous",
            DisplayName = "  Mobile helper  ",
            Description = new string('x', 2500),
            PreferredModelRole = "MAIN_LLM",
            AllowedModelRoles = new List<string> { "coding" },
            RequiredCapabilities = new List<string> { "shell" },
            AllowedTools = new List<string> { "*", "run_command", "computer_use" },
            InternetPolicy = "required",
            Workflow = new List<string> { "shell" },
            Dependencies = new List<string> { "arbitrary" },
            Permissions = new SkillPermissions
            {
                ReadFiles = true,
                WriteFiles = true,
                DeleteFiles = true,
                Shell = true,
                Network = true,
                Browser = true,
                ComputerUse = true,
                ExternalProcesses = true
            },
            ConfirmationPolicy = "never"
        };

        SkillManifest safe = MobileSkillCreationPolicy.Sanitize(input);

        Assert.Equal(string.Empty, safe.Id);
        Assert.Equal("Mobile helper", safe.DisplayName);
        Assert.Equal(2000, safe.Description.Length);
        Assert.Empty(safe.AllowedTools);
        Assert.Empty(safe.AllowedModelRoles);
        Assert.Empty(safe.RequiredCapabilities);
        Assert.Empty(safe.Workflow);
        Assert.Empty(safe.Dependencies);
        Assert.False(safe.Permissions.ReadFiles);
        Assert.False(safe.Permissions.WriteFiles);
        Assert.False(safe.Permissions.DeleteFiles);
        Assert.False(safe.Permissions.Shell);
        Assert.True(safe.Permissions.Network);
        Assert.False(safe.Permissions.Browser);
        Assert.False(safe.Permissions.ComputerUse);
        Assert.False(safe.Permissions.ExternalProcesses);
        Assert.Equal("required", safe.InternetPolicy);
    }

    [Fact]
    public void Network_permission_is_not_inferred_unless_mobile_manifest_explicitly_requires_internet()
    {
        SkillManifest safe = MobileSkillCreationPolicy.Sanitize(new SkillManifest
        {
            DisplayName = "Offline helper",
            Permissions = new SkillPermissions { Network = true, Shell = true },
            InternetPolicy = "off"
        });

        Assert.False(safe.Permissions.Network);
        Assert.Equal("off", safe.InternetPolicy);
        Assert.Empty(safe.AllowedTools);
    }

    [Fact]
    public void Missing_display_name_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            MobileSkillCreationPolicy.Sanitize(new SkillManifest { DisplayName = "   " }));
    }
}
