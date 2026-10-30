using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseWindowSelectionPolicyTests
{
    private static readonly string[] Windows =
    {
        "Inbox - Outlook",
        "Calendar - Outlook",
        "Budget.xlsx - Excel",
        "Test app - PaRuX",
        "Test app settings - PaRuX"
    };

    [Theory]
    [InlineData("Budget.xlsx - Excel", 2)]
    [InlineData("budget.XLSX - excel", 2)]
    [InlineData("Budget.xlsx", 2)]
    [InlineData("Inbox - Outlook", 0)]
    [InlineData("Calendar - Outlook", 1)]
    [InlineData("Outlook", ComputerUseWindowSelectionPolicy.Ambiguous)]
    [InlineData("Test app", ComputerUseWindowSelectionPolicy.Ambiguous)]
    [InlineData("not installed", ComputerUseWindowSelectionPolicy.NotFound)]
    [InlineData("a", ComputerUseWindowSelectionPolicy.NotFound)]
    public void Exact_first_and_only_unambiguous_partial_window_titles_can_be_focused(string title, int expectedIndex)
    {
        Assert.Equal(expectedIndex, ComputerUseWindowSelectionPolicy.FindUniqueIndex(Windows, title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_queries_do_not_pick_the_first_visible_app(string? title)
    {
        Assert.Equal(ComputerUseWindowSelectionPolicy.NotFound,
            ComputerUseWindowSelectionPolicy.FindUniqueIndex(Windows, title));
    }

    [Fact]
    public void Duplicate_exact_titles_are_not_resolved_by_enumeration_order()
    {
        Assert.Equal(ComputerUseWindowSelectionPolicy.Ambiguous,
            ComputerUseWindowSelectionPolicy.FindUniqueIndex(
                new[] { "Untitled - Notepad", "Untitled - Notepad" }, "Untitled - Notepad"));
    }

    [Fact]
    public void Exact_short_app_name_is_ok_but_partial_match_is_not()
    {
        Assert.Equal(0, ComputerUseWindowSelectionPolicy.FindUniqueIndex(new[] { "X" }, "X"));
        Assert.Equal(ComputerUseWindowSelectionPolicy.NotFound,
            ComputerUseWindowSelectionPolicy.FindUniqueIndex(new[] { "X app" }, "X"));
    }

    [Fact]
    public void Null_window_list_and_oversized_user_title_fail_closed()
    {
        Assert.Equal(ComputerUseWindowSelectionPolicy.NotFound,
            ComputerUseWindowSelectionPolicy.FindUniqueIndex(null!, "Outlook"));
        Assert.Equal(ComputerUseWindowSelectionPolicy.NotFound,
            ComputerUseWindowSelectionPolicy.FindUniqueIndex(Windows, new string('a', 257)));
    }
}
