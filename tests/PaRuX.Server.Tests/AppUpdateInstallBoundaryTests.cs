using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AppUpdateInstallBoundaryTests
{
    [Fact]
    public void Official_program_files_layout_is_accepted()
    {
        string pf = Path.Combine(Path.DirectorySeparatorChar.ToString(), "Program Files");
        string app = Path.Combine(pf, "PaRuX");
        string helper = Path.Combine(app, "UpdateHelper", "PaRuX.UpdateHelper.exe");

        Assert.True(AppUpdateInstallBoundary.IsProtectedInstalledHelper(app, helper, pf));
    }

    [Fact]
    public void Portable_user_writable_layout_is_rejected()
    {
        string pf = Path.Combine(Path.DirectorySeparatorChar.ToString(), "Program Files");
        string app = Path.Combine(Path.DirectorySeparatorChar.ToString(), "Users", "ExampleUser", "Downloads", "PaRuX");
        string helper = Path.Combine(app, "UpdateHelper", "PaRuX.UpdateHelper.exe");

        Assert.False(AppUpdateInstallBoundary.IsProtectedInstalledHelper(app, helper, pf));
    }

    [Fact]
    public void Helper_outside_the_application_tree_is_rejected()
    {
        string pf = Path.Combine(Path.DirectorySeparatorChar.ToString(), "Program Files");
        string app = Path.Combine(pf, "PaRuX");
        string helper = Path.Combine(pf, "OtherApp", "PaRuX.UpdateHelper.exe");

        Assert.False(AppUpdateInstallBoundary.IsProtectedInstalledHelper(app, helper, pf));
    }

    [Fact]
    public void Wrong_helper_name_or_nested_location_is_rejected()
    {
        string pf = Path.Combine(Path.DirectorySeparatorChar.ToString(), "Program Files");
        string app = Path.Combine(pf, "PaRuX");

        Assert.False(AppUpdateInstallBoundary.IsProtectedInstalledHelper(
            app, Path.Combine(app, "UpdateHelper", "evil.exe"), pf));
        Assert.False(AppUpdateInstallBoundary.IsProtectedInstalledHelper(
            app, Path.Combine(app, "UpdateHelper", "nested", "PaRuX.UpdateHelper.exe"), pf));
    }
}
