using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Hermetic GUI tests for any Windows application exposing UI Automation.
/// No test may inspect the actual PC or start a desktop process.</summary>
public sealed class WindowsUiAssertionPolicyTests
{
    private const long Foreground = 131906;
    private const string Tree = """
    {
      "windows": [
        {
          "hwnd": 131906,
          "title": "Disposable test application",
          "elements": [
            {
              "selector": "txt-input-123",
              "name": "Value input",
              "value": "final value",
              "children": [
                { "selector": "lbl-result-456", "name": "Passed", "text": "Passed" }
              ]
            }
          ]
        },
        {
          "hwnd": 98765,
          "elements": [
            { "selector": "lbl-result-456", "name": "Failed", "text": "Failed" }
          ]
        }
      ],
      "metadata": { "selector": "lbl-fake", "value": "Passed" }
    }
    """;

    [Theory]
    [InlineData("txt-input-123", "value", "final value")]
    [InlineData("txt-input-123", "name", "Value input")]
    [InlineData("lbl-result-456", "name", "Passed")]
    [InlineData("lbl-result-456", "text", "Passed")]
    public void Exact_control_properties_are_verified_under_the_authorized_window(
        string selector, string property, string expected)
    {
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.Verified,
            WindowsUiAssertionPolicy.AssertExact(Tree, Foreground, selector, property, expected));
    }

    [Fact]
    public void Wrong_value_is_not_a_pass_even_if_it_appears_in_another_window()
    {
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.DifferentValue,
            WindowsUiAssertionPolicy.AssertExact(Tree, Foreground, "lbl-result-456", "text", "Failed"));
    }

    [Fact]
    public void Wrong_window_and_spoofed_metadata_never_authorize_assertions()
    {
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.MissingTarget,
            WindowsUiAssertionPolicy.AssertExact(Tree, 222222, "lbl-result-456", "text", "Passed"));
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.MissingTarget,
            WindowsUiAssertionPolicy.AssertExact(Tree, Foreground, "lbl-fake", "value", "Passed"));
    }

    [Fact]
    public void Two_same_selectors_in_one_window_are_ambiguous_not_success()
    {
        const string duplicated = """
        {"windows":[{"hwnd":131906,"elements":[
          {"selector":"lbl-status","text":"Passed"},
          {"selector":"lbl-status","text":"Passed"}]}]}
        """;
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.AmbiguousTarget,
            WindowsUiAssertionPolicy.AssertExact(duplicated, Foreground, "lbl-status", "text", "Passed"));
    }

    [Fact]
    public void Missing_property_is_not_conflated_with_empty_expected_text()
    {
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.MissingProperty,
            WindowsUiAssertionPolicy.AssertExact(Tree, Foreground, "lbl-result-456", "value", ""));
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.DifferentValue,
            WindowsUiAssertionPolicy.AssertExact(Tree, Foreground, "lbl-result-456", "text", ""));
    }

    [Theory]
    [InlineData("lbl-status", "value", null)]
    [InlineData("lbl status", "value", "Passed")]
    [InlineData("lbl-status", "role", "Passed")]
    [InlineData("lbl-status", "value", "a\0b")]
    [InlineData("--output", "value", "Passed")]
    public void Invalid_selectors_properties_and_payloads_are_refused(
        string selector, string property, string? expected)
    {
        Assert.False(WindowsUiAssertionPolicy.IsValidRequest(selector, property, expected));
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.InvalidRequest,
            WindowsUiAssertionPolicy.AssertExact(Tree, Foreground, selector, property, expected));
    }

    [Fact]
    public void Oversized_expectation_and_tree_fail_closed()
    {
        Assert.False(WindowsUiAssertionPolicy.IsValidRequest("lbl-ok", "value", new string('x', 513)));
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.InvalidObservation,
            WindowsUiAssertionPolicy.AssertExact(Tree + new string(' ', 8100),
                Foreground, "lbl-result-456", "text", "Passed"));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"error":{"selector":"lbl-ok","text":"Passed"}}""")]
    [InlineData("""{"windows":[{"hwnd":131906,"elements":"not-an-array"}]}""")]
    public void Error_and_malformed_observations_are_not_success(string raw)
    {
        Assert.Equal(WindowsUiAssertionPolicy.Verdict.InvalidObservation,
            WindowsUiAssertionPolicy.AssertExact(raw, Foreground, "lbl-ok", "text", "Passed"));
    }

    [Fact]
    public void The_action_parser_accepts_bounded_read_only_assertions_without_logging_the_secret_value()
    {
        const string expectation = "private expected value";
        ComputerUseAction? action = ComputerUseActionParser.TryParse(
            """{"action":"ui_assert","selector":"txt-input-123","property":"value","expected":"private expected value"}""");
        Assert.NotNull(action);
        Assert.True(ComputerUseActionParser.TryCanonicalize(action!));
        Assert.Equal(expectation, action.Expected);
        Assert.Null(ComputerUseActionParser.ValidateExecutable(action, new ScreenBounds(0, 0, 1920, 1080)));
        Assert.True(ComputerUseActionParser.IsExecutable("ui_assert"));
        Assert.DoesNotContain(expectation, ComputerUseActionParser.Describe(action), StringComparison.Ordinal);
        Assert.Equal(ComputerUseSensitiveActionPolicy.Disposition.Allow,
            ComputerUseSensitiveActionPolicy.Evaluate("ui_assert", "Status text", null, null).Disposition);
    }

    [Fact]
    public void Different_expectations_do_not_collapse_into_the_same_repeat_key()
    {
        var a = new ComputerUseAction { Action = "ui_assert", Selector = "lbl-result", Property = "text", Expected = "Failed" };
        var b = new ComputerUseAction { Action = "ui_assert", Selector = "lbl-result", Property = "text", Expected = "Passed" };
        Assert.NotEqual(ComputerUseActionParser.RepeatKey(a), ComputerUseActionParser.RepeatKey(b));
    }
}
