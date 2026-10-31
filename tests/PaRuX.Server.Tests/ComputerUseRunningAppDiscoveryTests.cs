using System;
using System.Collections.Generic;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseRunningAppDiscoveryTests
{
    private static string App(string name) =>
        Path.Combine(Path.GetTempPath(), "parux-disposable-apps", name);

    [Theory]
    [InlineData("Notepad", "Notepad.exe", true)]
    [InlineData("Excel", "Excel.exe", true)]
    public void Matching_local_executable_with_window_is_eligible(
        string name, string exe, bool visible)
    {
        Assert.True(ComputerUseRunningAppDiscovery.IsEligibleCandidate(name, App(exe), visible));
    }

    [Fact]
    public void Untrusted_window_titles_and_broker_process_are_not_app_identities()
    {
        Assert.False(ComputerUseRunningAppDiscovery.IsEligibleCandidate("Excel", App("unknown.exe"), true));
        Assert.False(ComputerUseRunningAppDiscovery.IsEligibleCandidate("Excel", App("Excel.exe"), false));
        Assert.False(ComputerUseRunningAppDiscovery.IsEligibleCandidate(
            "ApplicationFrameHost", App("ApplicationFrameHost.exe"), true));
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("wscript.exe")]
    [InlineData("python.exe")]
    [InlineData("setup.msi")]
    public void Scripts_shells_and_installers_cannot_be_discovered_as_gui_apps(string filename)
    {
        Assert.False(ComputerUseRunningAppDiscovery.IsEligibleCandidate(
            Path.GetFileNameWithoutExtension(filename), App(filename), true));
    }

    [Fact]
    public void Network_and_relative_paths_are_refused()
    {
        Assert.False(ComputerUseRunningAppDiscovery.IsEligibleCandidate(
            "editor", @"\\server\share\editor.exe", true));
        Assert.False(ComputerUseRunningAppDiscovery.IsEligibleCandidate(
            "editor", "editor.exe", true));
    }

    [Fact]
    public void Sorted_inventory_deduplicates_executables_and_respects_limit()
    {
        var input = new List<ComputerUseRunningAppDiscovery.DiscoveredApp>
        {
            new("Zeta", App("Zeta.exe")),
            new("alpha", App("alpha.exe")),
            new("ALPHA", App("ALPHA.exe")),
            new("cmd", App("cmd.exe")),
            new("Beta", App("Beta.exe")),
        };
        var result = ComputerUseRunningAppDiscovery.SelectCandidates(input, 2);
        Assert.Equal(2, result.Count);
        Assert.Equal("alpha", result[0].Name);
        Assert.Equal("Beta", result[1].Name);
        Assert.Equal(3, ComputerUseRunningAppDiscovery.SelectCandidates(input).Count);
    }

    [Fact]
    public void Empty_candidates_cannot_grant_any_application()
    {
        Assert.Empty(ComputerUseRunningAppDiscovery.SelectCandidates(
            Array.Empty<ComputerUseRunningAppDiscovery.DiscoveredApp>()));
    }
}
