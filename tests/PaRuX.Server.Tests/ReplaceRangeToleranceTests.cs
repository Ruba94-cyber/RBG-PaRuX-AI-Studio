using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Precise edits have to survive the differences a language model reliably gets wrong, without
/// ever guessing between two plausible locations.
///
/// Measured on the physical PC (2026-09-21): after the context and memory fixes the coding agent
/// finally reached an edit - "Step 7: replace_range" - and the tool rejected it with "oldText was
/// not found ... doesn't match exactly (including whitespace)". The model had the intent right and
/// the literal text slightly wrong, the error gave it nothing to act on, and the run ended with no
/// diff. These tests pin what is tolerated, what is still refused, and that a failure now tells
/// the model where to look.
/// </summary>
public sealed class ReplaceRangeToleranceTests
{
    private const string Source =
        "class Sample\n" +
        "{\n" +
        "    public int Compute(int value)\n" +
        "    {\n" +
        "        return value * 2;\n" +
        "    }\n" +
        "}\n";

    [Fact]
    public void An_exact_match_still_applies_with_no_note()
    {
        (bool success, string updated, string? error, string? note) =
            CodingAgentTools.TryReplaceRangeDetailed(Source, "return value * 2;", "return value * 3;");

        Assert.True(success, error);
        Assert.Null(note);
        Assert.Contains("return value * 3;", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void A_crlf_file_accepts_lf_oldText_and_stays_crlf()
    {
        string crlf = Source.Replace("\n", "\r\n");

        (bool success, string updated, string? error, string? note) =
            CodingAgentTools.TryReplaceRangeDetailed(crlf, "        return value * 2;\n    }", "        return value * 3;\n    }");

        Assert.True(success, error);
        Assert.Equal("matched after normalizing line endings", note);
        Assert.Contains("return value * 3;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("value * 2", updated, StringComparison.Ordinal);
        Assert.Contains("\r\n", updated, StringComparison.Ordinal);
        // Every newline must still be CRLF - a tolerated match must not silently rewrite the file.
        Assert.Equal(updated.Split('\n').Length - 1, updated.Split("\r\n").Length - 1);
    }

    [Fact]
    public void Trailing_whitespace_in_oldText_is_tolerated()
    {
        (bool success, string updated, string? error, string? note) = CodingAgentTools.TryReplaceRangeDetailed(
            Source, "    public int Compute(int value)   \n    {", "    public int Compute(long value)\n    {");

        Assert.True(success, error);
        Assert.Equal("matched ignoring trailing whitespace", note);
        Assert.Contains("Compute(long value)", updated, StringComparison.Ordinal);
    }

    /// <summary>
    /// The most common near-miss: right lines, wrong indent level. The replacement must be moved
    /// to the file's own indentation rather than pasted at the model's.
    /// </summary>
    [Fact]
    public void Wrong_indentation_is_tolerated_and_the_replacement_is_reindented()
    {
        // Over-indented by four spaces on both lines, so it is not a substring of the file at all.
        (bool success, string updated, string? error, string? note) = CodingAgentTools.TryReplaceRangeDetailed(
            Source,
            "            return value * 2;\n        }",
            "            return value * 4;\n        }");

        Assert.True(success, error);
        Assert.Equal("matched ignoring indentation; the replacement was re-indented to the file's own level", note);
        Assert.Contains("        return value * 4;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("  return value * 4;\n    }", updated, StringComparison.Ordinal);
    }

    /// <summary>Tolerance must never turn into guessing between two candidate locations.</summary>
    [Fact]
    public void An_ambiguous_loose_match_is_refused_rather_than_guessed()
    {
        const string twice =
            "void A()\n{\n    Log(\"same\");\n}\n" +
            "void B()\n{\n        Log(\"same\");\n}\n";

        (bool success, _, string? error, _) = CodingAgentTools.TryReplaceRangeDetailed(
            twice, "Log(\"same\");", "Log(\"changed\");");

        Assert.False(success);
        Assert.Contains("more than once", error, StringComparison.Ordinal);
    }

    /// <summary>A single trivial line is too weak an anchor to match loosely.</summary>
    [Fact]
    public void A_very_short_single_line_is_not_matched_loosely()
    {
        (bool success, _, string? error, _) = CodingAgentTools.TryReplaceRangeDetailed(Source, "  {  ", "  { // x");

        Assert.False(success);
        Assert.NotNull(error);
    }

    /// <summary>
    /// The failure the agent actually hit must now hand back real text to copy, not a dead end.
    /// </summary>
    [Fact]
    public void A_genuine_mismatch_reports_the_closest_region_verbatim()
    {
        (bool success, _, string? error, _) = CodingAgentTools.TryReplaceRangeDetailed(
            Source, "        return value * 2; // doubled\n        Console.WriteLine(value);", "        return value * 5;");

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("closest region", error!, StringComparison.Ordinal);
        Assert.Contains("return value * 2;", error, StringComparison.Ordinal);
        Assert.Matches(@"\d+: ", error);
    }

    [Fact]
    public void Text_that_appears_nowhere_says_so_instead_of_inventing_a_region()
    {
        (bool success, _, string? error, _) = CodingAgentTools.TryReplaceRangeDetailed(
            Source, "zzzz_not_in_this_file_at_all", "x");

        Assert.False(success);
        Assert.Contains("re-read the file", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_legacy_three_value_overload_still_works()
    {
        (bool success, string updated, string? error) =
            CodingAgentTools.TryReplaceRange(Source, "return value * 2;", "return value * 9;");

        Assert.True(success, error);
        Assert.Contains("value * 9", updated, StringComparison.Ordinal);
    }
}
