using System;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseAppLaunchPolicyTests
{
    private static string AbsoluteApp(string name) =>
        Path.Combine(Path.GetTempPath(), "parux-disposable-apps", name);

    [Theory]
    [InlineData("Notepad++.exe")]
    [InlineData("Editor.exe")]
    [InlineData("Code.exe")]
    [InlineData("Teams.exe")]
    [InlineData("Excel.exe")]
    public void Explicitly_configured_gui_executables_are_valid_app_launch_candidates(
        string appName)
    {
        Assert.True(ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(AbsoluteApp(appName)));
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("wscript.exe")]
    [InlineData("cscript.exe")]
    [InlineData("mshta.exe")]
    [InlineData("rundll32.exe")]
    [InlineData("regsvr32.exe")]
    [InlineData("msiexec.exe")]
    [InlineData("wsl.exe")]
    [InlineData("python.exe")]
    [InlineData("node.exe")]
    [InlineData("git.exe")]
    [InlineData("curl.exe")]
    [InlineData("schtasks.exe")]
    public void Registered_command_interpreters_cannot_be_spawned_by_the_desktop_agent(
        string executable)
    {
        Assert.False(ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(AbsoluteApp(executable)));
        Assert.False(ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(
            AbsoluteApp(executable.ToUpperInvariant())));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("app.exe")]
    [InlineData("relative/app.exe")]
    [InlineData("script.ps1")]
    [InlineData("run.bat")]
    [InlineData("start.cmd")]
    [InlineData("C:\\Windows\\System32\\cmd.exe\0--hidden")]
    [InlineData("\\\\server\\share\\editor.exe")]
    [InlineData("\\\\?\\C:\\apps\\editor.exe")]
    public void Missing_relative_script_unsafe_and_unc_launcher_paths_are_refused(
        string? path)
    {
        Assert.False(ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(path));
    }

    [Fact]
    public void Absolute_script_and_shell_files_are_never_interpreted_as_apps()
    {
        Assert.False(ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(AbsoluteApp("script.ps1")));
        Assert.False(ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(AbsoluteApp("app.lnk")));
        Assert.False(ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(AbsoluteApp("setup.msi")));
    }
}
