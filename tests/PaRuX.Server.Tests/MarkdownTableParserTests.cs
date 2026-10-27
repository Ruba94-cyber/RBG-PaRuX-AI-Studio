using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MarkdownTableParserTests
{
    [Fact]
    public void Parses_header_alignment_and_rows()
    {
        var lines = new[] { "| Name | Diameter (km) | Moons |", "|:-----|--------------:|:-----:|", "| Earth | 12,742 | 1 |", "| Mars | 6,779 | 2 |", "", "after" };

        MarkdownTableParser.Table? table = MarkdownTableParser.TryParse(lines, 0);

        Assert.NotNull(table);
        Assert.Equal(new[] { "Name", "Diameter (km)", "Moons" }, table!.Header);
        Assert.Equal(new[] { 'l', 'r', 'c' }, table.Alignments);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new[] { "Mars", "6,779", "2" }, table.Rows[1]);
        Assert.Equal(4, table.LinesConsumed);
    }

    [Fact]
    public void Accepts_rows_without_outer_pipes_and_pads_short_rows()
    {
        var lines = new[] { "A | B | C", "--- | --- | ---", "1 | 2" };

        MarkdownTableParser.Table? table = MarkdownTableParser.TryParse(lines, 0);

        Assert.NotNull(table);
        Assert.Equal(new[] { "1", "2", "" }, table!.Rows[0]);
    }

    [Fact]
    public void Escaped_pipe_stays_inside_the_cell()
    {
        string escaped = "| a " + (char)92 + "| b | or |";
        var lines = new[] { "| Op | Meaning |", "|---|---|", escaped };

        MarkdownTableParser.Table? table = MarkdownTableParser.TryParse(lines, 0);

        Assert.Equal("a | b", table!.Rows[0][0]);
    }

    [Theory]
    [InlineData("just text", "more text")]
    [InlineData("| only one |", "|---|")]
    [InlineData("| A | B |", "| not | separator |")]
    public void Non_tables_are_not_swallowed(string first, string second)
    {
        Assert.Null(MarkdownTableParser.TryParse(new[] { first, second }, 0));
    }
}
