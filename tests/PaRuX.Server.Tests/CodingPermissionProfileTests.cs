using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingPermissionProfileTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("OFFLINE")]
    public void Unknown_or_offline_profile_is_fail_closed(string? value)
    {
        Assert.Equal(CodingPermissionProfile.Offline, CodingPermissionProfile.Normalize(value));
        Assert.Empty(CodingPermissionProfile.CapabilitiesFor(value));
    }

    [Fact]
    public void Package_fetch_grants_only_no_script_restore()
    {
        IReadOnlyList<string> grants = CodingPermissionProfile.CapabilitiesFor(CodingPermissionProfile.PackageFetch);

        Assert.Equal(new[] { CodingPackageRestoreDiscovery.SafeRestoreCapability }, grants);
        Assert.Equal(CodingPermissionProfile.PackageFetch, CodingPermissionProfile.FromCapabilities(grants));
    }

    [Fact]
    public void Trusted_restore_includes_safe_and_scripted_authority()
    {
        IReadOnlyList<string> grants = CodingPermissionProfile.CapabilitiesFor(CodingPermissionProfile.TrustedRestore);

        Assert.Contains(CodingPackageRestoreDiscovery.SafeRestoreCapability, grants);
        Assert.Contains(CodingPackageRestoreDiscovery.ScriptedRestoreCapability, grants);
        Assert.Equal(CodingPermissionProfile.TrustedRestore, CodingPermissionProfile.FromCapabilities(grants));
    }
}
