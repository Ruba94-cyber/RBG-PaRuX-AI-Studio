using System.Collections.Generic;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseAppSelectionPolicyTests
{
    private static readonly IReadOnlyList<ComputerUseAppSelectionPolicy.Candidate> Apps =
        new List<ComputerUseAppSelectionPolicy.Candidate>
        {
            new("Notepad", new[] { "blocco note", "notes" }),
            new("Calendar", new[] { "agenda", "calendar" }),
            new("My approved editor", new[] { "editor", "notes" })
        };

    [Theory]
    [InlineData("Notepad", 0)]
    [InlineData("notepad", 0)]
    [InlineData("blocco note", 0)]
    [InlineData("agenda", 1)]
    [InlineData("editor", 2)]
    public void Only_exact_known_name_or_alias_can_select_an_application(string name, int index)
    {
        Assert.Equal(index, ComputerUseAppSelectionPolicy.FindUniqueIndex(Apps, name));
    }

    [Theory]
    [InlineData("Notepad.exe")]
    [InlineData("notepad.exe /c delete")]
    [InlineData("C:\\Windows\\notepad.exe")]
    [InlineData("note")]
    [InlineData("calendar --unsafe")]
    [InlineData("")]
    [InlineData("  Notepad  ")]
    [InlineData(null)]
    public void Executable_paths_commands_substrings_and_unregistered_names_never_resolve(string? name)
    {
        Assert.Equal(ComputerUseAppSelectionPolicy.NotFound,
            ComputerUseAppSelectionPolicy.FindUniqueIndex(Apps, name));
    }

    [Fact]
    public void Shared_alias_is_ambiguous_not_first_match()
    {
        Assert.Equal(ComputerUseAppSelectionPolicy.Ambiguous,
            ComputerUseAppSelectionPolicy.FindUniqueIndex(Apps, "notes"));
    }

    [Theory]
    [InlineData("Notepad", true)]
    [InlineData("Approved desktop app", true)]
    [InlineData("new\napp", false)]
    [InlineData("unsafe\0app", false)]
    [InlineData("  Notepad", false)]
    [InlineData("Notepad  ", false)]
    [InlineData("", false)]
    public void Approved_app_labels_are_bounded_and_cannot_embed_control_characters(
        string name, bool valid)
    {
        Assert.Equal(valid, ComputerUseAppSelectionPolicy.IsValidName(name));
    }

    [Fact]
    public void Missing_or_very_long_names_are_invalid()
    {
        Assert.False(ComputerUseAppSelectionPolicy.IsValidName(null));
        Assert.False(ComputerUseAppSelectionPolicy.IsValidName(new string('x', 81)));
    }

    [Fact]
    public void Even_an_empty_app_catalogue_does_not_launch_an_arbitrary_executable()
    {
        Assert.Equal(ComputerUseAppSelectionPolicy.NotFound,
            ComputerUseAppSelectionPolicy.FindUniqueIndex(
                new List<ComputerUseAppSelectionPolicy.Candidate>(), "explorer.exe"));
    }

    [Fact]
    public void Agent_grammar_and_validation_require_a_typed_app_name()
    {
        var a = ComputerUseActionParser.TryParse(
            """{"action":"open_app","app":"Notepad"}""");
        Assert.NotNull(a);
        Assert.True(ComputerUseActionParser.TryCanonicalize(a!));
        Assert.Equal("open_app", a.Action);
        Assert.Equal("Notepad", a.App);
        Assert.True(ComputerUseActionParser.IsExecutable(a.Action));
        Assert.Null(ComputerUseActionParser.ValidateExecutable(a, new ScreenBounds(0, 0, 100, 100)));
        Assert.NotNull(ComputerUseActionParser.ValidateExecutable(
            new ComputerUseAction { Action = "open_app", App = "notepad.exe\n--unsafe" },
            new ScreenBounds(0, 0, 100, 100)));
        Assert.Equal(ComputerUseSensitiveActionPolicy.Disposition.Allow,
            ComputerUseSensitiveActionPolicy.Evaluate("open_app", null, null, null).Disposition);
    }
}
