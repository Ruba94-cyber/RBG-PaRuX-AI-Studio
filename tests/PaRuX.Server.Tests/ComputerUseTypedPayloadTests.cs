using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Typed text is the most sensitive payload Computer Use handles: it can be a password typed into a
/// window the user is looking at. Three defects are pinned here.
///
/// 1. The first 80 characters of every typed text were written to the task log, to the chat and into
///    the persisted visual-evidence notes. Only the length is described now.
/// 2. A "type" action had no size limit, so an unbounded model payload was typed one keystroke at a
///    time into whatever window had focus. Payloads above the cap are refused before any input.
/// 3. The repeat detector compared the redacted description, so two different texts of equal length
///    looked like one repeated action and could stop a run that was making progress.
/// </summary>
public sealed class ComputerUseTypedPayloadTests
{
    private static readonly ScreenBounds Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void Typed_text_is_described_by_length_only_so_the_secret_never_reaches_the_log()
    {
        const string secret = "hunter2-correct-horse";

        string description = ComputerUseActionParser.Describe(new ComputerUseAction { Action = "type", Text = secret });

        Assert.Equal("type (text: " + secret.Length + " chars, redacted)", description);
        Assert.DoesNotContain("hunter2", description);
        Assert.DoesNotContain("horse", description);
    }

    [Fact]
    public void A_typed_text_above_the_cap_is_refused_and_the_reason_does_not_echo_it()
    {
        string oversized = new string('a', 2001);

        string? refusal = ComputerUseActionParser.ValidateExecutable(
            new ComputerUseAction { Action = "type", Text = oversized }, Screen);

        Assert.NotNull(refusal);
        Assert.Contains("2001 characters", refusal);
        Assert.Contains("2000-character limit", refusal);
        Assert.DoesNotContain("aaaa", refusal);
    }

    [Fact]
    public void A_typed_text_exactly_at_the_cap_is_allowed()
    {
        string atCap = new string('a', ComputerUseActionParser.MaxTypeTextLength);

        Assert.Equal(2000, ComputerUseActionParser.MaxTypeTextLength);
        Assert.Null(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = "type", Text = atCap }, Screen));
    }

    [Fact]
    public void A_type_action_with_no_text_is_not_treated_as_oversized()
    {
        Assert.Null(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = "type", Text = null }, Screen));
    }

    [Fact]
    public void Two_different_texts_of_equal_length_are_not_mistaken_for_one_repeated_action()
    {
        string alpha = ComputerUseActionParser.RepeatKey(new ComputerUseAction { Action = "type", Text = "alpha" });
        string bravo = ComputerUseActionParser.RepeatKey(new ComputerUseAction { Action = "type", Text = "bravo" });

        Assert.NotEqual(alpha, bravo);
        Assert.Equal(alpha, ComputerUseActionParser.RepeatKey(new ComputerUseAction { Action = "type", Text = "alpha" }));
    }

    [Fact]
    public void The_same_click_still_counts_as_a_repeat_and_a_moved_click_does_not()
    {
        string original = ComputerUseActionParser.RepeatKey(new ComputerUseAction { Action = "click", X = 10, Y = 20, Button = "left" });

        Assert.Equal(original, ComputerUseActionParser.RepeatKey(new ComputerUseAction { Action = "click", X = 10, Y = 20, Button = "left" }));
        Assert.NotEqual(original, ComputerUseActionParser.RepeatKey(new ComputerUseAction { Action = "click", X = 11, Y = 20, Button = "left" }));
    }
}
