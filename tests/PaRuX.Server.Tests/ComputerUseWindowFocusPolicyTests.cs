using System;
using System.Collections.Generic;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseWindowFocusPolicyTests
{
    private static string Exe(string name) => Path.Combine(Path.GetTempPath(), "parux-fixture", name);
    private static ComputerUseWindowFocusPolicy.Candidate Window(
        string title, int pid, long handle, string? path) =>
        new(title, pid, handle, path);

    [Fact]
    public void An_unapproved_process_cannot_impersonate_an_approved_app_by_title()
    {
        var windows = new[]
        {
            Window("Budget.xlsx - Excel", 83, 100, Exe("unapproved.exe")),
            Window("Budget.xlsx - Excel", 92, 200, Exe("Excel.exe"))
        };
        Assert.Equal(1, ComputerUseWindowFocusPolicy.FindScopedUniqueIndex(
            windows, "Budget.xlsx", 0, new[] { Exe("Excel.exe") }));
    }

    [Fact]
    public void Unapproved_windows_do_not_create_false_title_ambiguity()
    {
        var windows = new[]
        {
            Window("Report", 40, 100, Exe("rogue.exe")),
            Window("Report", 41, 200, Exe("Editor.exe"))
        };
        Assert.Equal(1, ComputerUseWindowFocusPolicy.FindScopedUniqueIndex(
            windows, "Report", 0, new[] { Exe("Editor.exe") }));
    }

    [Fact]
    public void Two_authorized_windows_with_same_title_are_ambiguous()
    {
        var windows = new[]
        {
            Window("Report", 41, 200, Exe("Editor.exe")),
            Window("Report", 42, 300, Exe("Editor.exe"))
        };
        Assert.Equal(ComputerUseWindowFocusPolicy.Ambiguous,
            ComputerUseWindowFocusPolicy.FindScopedUniqueIndex(
                windows, "Report", 0, new[] { Exe("Editor.exe") }));
    }

    [Fact]
    public void Same_foreground_process_may_focus_its_second_window()
    {
        var windows = new[]
        {
            Window("Primary", 41, 200, Exe("Editor.exe")),
            Window("Child", 41, 300, Exe("Editor.exe"))
        };
        Assert.Equal(1, ComputerUseWindowFocusPolicy.FindScopedUniqueIndex(
            windows, "Child", 41, Array.Empty<string>()));
    }

    [Fact]
    public void Arbitrary_different_process_is_not_inherited_from_foreground_authority()
    {
        var windows = new[]
        {
            Window("Other", 99, 900, Exe("Other.exe")),
        };
        Assert.Equal(ComputerUseWindowFocusPolicy.NotFound,
            ComputerUseWindowFocusPolicy.FindScopedUniqueIndex(
                windows, "Other", 41, Array.Empty<string>()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("notepad.exe")]
    [InlineData("foo.cmd")]
    [InlineData(@"\\server\share\editor.exe")]
    public void Missing_relative_script_and_network_executables_are_denied(string? path)
    {
        Assert.False(ComputerUseWindowFocusPolicy.IsInScope(
            Window("Title", 1, 2, path), 1, new[] { Exe("Editor.exe") }));
    }

    [Fact]
    public void A_different_target_handle_process_or_binary_at_dispatch_is_refused()
    {
        var original = Window("Title", 41, 100, Exe("Editor.exe"));
        Assert.True(ComputerUseWindowFocusPolicy.IsUnchanged(
            original, Window("Retitled", 41, 100, Exe("Editor.exe"))));
        Assert.False(ComputerUseWindowFocusPolicy.IsUnchanged(
            original, Window("Title", 42, 100, Exe("Editor.exe"))));
        Assert.False(ComputerUseWindowFocusPolicy.IsUnchanged(
            original, Window("Title", 41, 200, Exe("Editor.exe"))));
        Assert.False(ComputerUseWindowFocusPolicy.IsUnchanged(
            original, Window("Title", 41, 100, Exe("Other.exe"))));
        Assert.False(ComputerUseWindowFocusPolicy.IsUnchanged(original, null));
    }

    [Fact]
    public void Unknown_identity_and_unapproved_paths_never_match()
    {
        Assert.Equal(ComputerUseWindowFocusPolicy.NotFound,
            ComputerUseWindowFocusPolicy.FindScopedUniqueIndex(
                new[] { Window("Fake", 0, 400, Exe("Editor.exe")) },
                "Fake", 0, new[] { Exe("Editor.exe") }));
        Assert.Equal(ComputerUseWindowFocusPolicy.NotFound,
            ComputerUseWindowFocusPolicy.FindScopedUniqueIndex(
                new[] { Window("Fake", 70, 400, null) },
                "Fake", 70, new[] { Exe("Editor.exe") }));
    }
}
