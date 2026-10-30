using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The point in Computer Use where a model's reply turns into something PaRuX does to the user's
/// real machine. Two live-found defects are pinned here.
///
/// 1. The reply was located with a first-brace-to-last-brace span. A local model with reasoning
///    enabled emits several brace-bearing fragments, and that span joined the opening of one to the
///    closing of another, so two individually valid objects parsed as invalid JSON and the run
///    stopped at step one with "the model's response wasn't a recognizable action".
/// 2. An action name outside the supported set fell through the executor's switch and did nothing,
///    while the step was still logged and counted - the run continued as if the action had been
///    performed. ACT then ASSUME SUCCESS, over an action that never ran.
/// </summary>
public sealed class ComputerUseActionParserTests
{
    [Fact]
    public void A_bare_action_object_parses()
    {
        ComputerUseAction? action = ComputerUseActionParser.TryParse("""{"action":"click","x":10,"y":20}""");

        Assert.NotNull(action);
        Assert.Equal("click", action!.Action);
        Assert.Equal(10, action.X);
        Assert.Equal(20, action.Y);
    }

    [Fact]
    public void An_action_wrapped_in_prose_and_fences_still_parses()
    {
        ComputerUseAction? action = ComputerUseActionParser.TryParse(
            "I will click the Start button.\n```json\n{\"action\":\"click\",\"x\":5,\"y\":700}\n```\nThat should work.");

        Assert.NotNull(action);
        Assert.Equal("click", action!.Action);
        Assert.Equal(5, action.X);
    }

    [Fact]
    public void A_reply_containing_two_objects_takes_the_first_complete_one()
    {
        // The exact shape the old first-brace..last-brace scan destroyed: it produced
        // {"action":"click","x":1,"y":2} ... {"action":"type","text":"hi"} as one span.
        ComputerUseAction? action = ComputerUseActionParser.TryParse(
            "{\"action\":\"click\",\"x\":1,\"y\":2}\nthen\n{\"action\":\"type\",\"text\":\"hi\"}");

        Assert.NotNull(action);
        Assert.Equal("click", action!.Action);
        Assert.Equal(1, action.X);
    }

    [Fact]
    public void A_brace_inside_the_typed_text_does_not_end_the_object_early()
    {
        ComputerUseAction? action = ComputerUseActionParser.TryParse(
            """{"action":"type","text":"func() { return 1; }"}""");

        Assert.NotNull(action);
        Assert.Equal("type", action!.Action);
        Assert.Equal("func() { return 1; }", action.Text);
    }

    [Fact]
    public void An_escaped_quote_inside_the_typed_text_is_handled()
    {
        ComputerUseAction? action = ComputerUseActionParser.TryParse(
            "{\"action\":\"type\",\"text\":\"say \\\"hello\\\" now\"}");

        Assert.NotNull(action);
        Assert.Equal("say \"hello\" now", action!.Text);
    }

    [Fact]
    public void A_leading_object_that_carries_no_action_is_skipped_rather_than_failing()
    {
        ComputerUseAction? action = ComputerUseActionParser.TryParse(
            "{\"thought\":\"the Start menu is open\"}\n{\"action\":\"key\",\"combo\":\"Ctrl+S\"}");

        Assert.NotNull(action);
        Assert.Equal("key", action!.Action);
        Assert.Equal("Ctrl+S", action.Combo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I cannot see the screen.")]
    [InlineData("{ this is not json")]
    public void Text_with_no_usable_action_object_returns_null(string reply)
    {
        Assert.Null(ComputerUseActionParser.TryParse(reply));
    }

    [Theory]
    [InlineData("click")]
    [InlineData("double_click")]
    [InlineData("right_click")]
    [InlineData("move")]
    [InlineData("type")]
    [InlineData("key")]
    [InlineData("scroll")]
    [InlineData("focus_window")]
    [InlineData("wait")]
    [InlineData("  CLICK  ")]
    public void Every_action_the_executor_implements_is_accepted(string action)
    {
        Assert.True(ComputerUseActionParser.IsExecutable(action));
    }

    [Theory]
    [InlineData("drag")]
    [InlineData("screenshot")]
    [InlineData("open_browser")]
    [InlineData("run_command")]
    [InlineData("")]
    [InlineData(null)]
    public void An_action_the_executor_cannot_perform_is_refused_not_ignored(string action)
    {
        // The agent turns a false here into an explicit stop. It must never be treated as
        // executable, because the executor would silently do nothing with it.
        Assert.False(ComputerUseActionParser.IsExecutable(action));
    }

    [Theory]
    [InlineData("teleport")]
    [InlineData("TELEPORT")]
    [InlineData(" teleport ")]
    [InlineData("clicks")]
    [InlineData("click2")]
    [InlineData("ui_click")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData(null)]
    public void An_action_name_the_grammar_does_not_define_is_refused_before_any_stage_reads_it(string? action)
    {
        // Refused by every entry point, whatever its case or padding. Before the single canonical name, the gates and the
        // executor matched names by different rules, so a padded name could pass one stage and be reported as done.
        var reply = new ComputerUseAction { Action = action, X = 10, Y = 10 };

        Assert.Null(ComputerUseActionParser.CanonicalAction(action));
        Assert.False(ComputerUseActionParser.IsExecutable(action));
        Assert.False(ComputerUseActionParser.TryCanonicalize(reply));
        Assert.Equal(action, reply.Action);
        Assert.NotNull(ComputerUseActionParser.ValidateExecutable(reply, Primary));
    }

    [Theory]
    [InlineData("click", "click")]
    [InlineData("  CLICK  ", "click")]
    [InlineData("Double_Click", "double_click")]
    [InlineData("\tRight_Click\n", "right_click")]
    [InlineData("UI_INVOKE", "ui_invoke")]
    [InlineData(" Ui_Set_Value ", "ui_set_value")]
    [InlineData("Focus_Window", "focus_window")]
    [InlineData("DONE", "done")]
    public void A_recognised_action_name_is_canonicalised_once_for_every_later_stage(string raw, string canonical)
    {
        var action = new ComputerUseAction { Action = raw };

        Assert.True(ComputerUseActionParser.TryCanonicalize(action));
        Assert.Equal(canonical, action.Action);
        Assert.Equal(canonical, ComputerUseActionParser.CanonicalAction(raw));
    }

    [Fact]
    public void A_padded_done_is_terminal_and_passes_the_gate_as_done()
    {
        // "  DONE " used to fail the loop's exact "done" comparison, so the run kept going and reported the step as carried out.
        var reply = new ComputerUseAction { Action = "  DONE " };

        Assert.True(ComputerUseActionParser.TryCanonicalize(reply));
        Assert.Equal("done", reply.Action);
        Assert.Null(ComputerUseActionParser.ValidateExecutable(reply, Primary));
    }

    [Theory]
    [InlineData("  Click  ")]
    [InlineData("SCROLL")]
    [InlineData("Double_Click")]
    public void A_mis_cased_pointer_action_without_coordinates_gets_the_same_refusal_as_its_canonical_form(string action)
    {
        string? refusal = ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = action }, Primary);

        Assert.NotNull(refusal);
        Assert.Contains("no x/y position", refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_focus_window_action_with_no_title_is_refused_instead_of_focusing_the_first_window(string? title)
    {
        // An empty title matches every window, so the old executor focused whichever window was listed first.
        Assert.NotNull(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = "focus_window", Title = title }, Primary));
        Assert.Null(ComputerUseActionParser.ValidateExecutable(
            new ComputerUseAction { Action = "focus_window", Title = "Untitled - Notepad" }, Primary));
    }

    [Fact]
    public void A_focus_that_matched_no_window_or_was_refused_by_windows_is_never_reported_as_done()
    {
        Assert.NotNull(ComputerUseActionParser.FocusRefusal(windowFound: false, windowFocused: false));
        Assert.NotNull(ComputerUseActionParser.FocusRefusal(windowFound: true, windowFocused: false));
        Assert.Null(ComputerUseActionParser.FocusRefusal(windowFound: true, windowFocused: true));
    }

    [Fact]
    public void Done_is_terminal_and_is_not_an_executable_action()
    {
        Assert.True(ComputerUseActionParser.IsDone("done"));
        Assert.True(ComputerUseActionParser.IsDone("  DONE "));
        Assert.False(ComputerUseActionParser.IsExecutable("done"));
        Assert.False(ComputerUseActionParser.IsDone("click"));
    }

    [Fact]
    public void The_schema_offered_to_the_model_covers_exactly_the_actions_that_are_honored()
    {
        // A schema that permitted an action the executor cannot run would constrain the model
        // straight into the unsupported-action stop; one that omitted a supported action would
        // needlessly narrow what Computer Use can do. Both are caught by comparing the two lists.
        JsonNode schema = JsonNode.Parse(ComputerUseActionParser.ActionJsonSchema)!;
        JsonArray allowed = schema["properties"]!["action"]!["enum"]!.AsArray();

        foreach (JsonNode? entry in allowed)
        {
            string name = entry!.GetValue<string>();
            Assert.True(
                ComputerUseActionParser.IsExecutable(name) || ComputerUseActionParser.IsDone(name),
                "Schema offers \"" + name + "\" but the executor cannot perform it.");
        }

        foreach (string implemented in new[]
        {
            "click", "double_click", "right_click", "move", "type",
            "key", "scroll", "focus_window", "wait", "ui_assert", "done"
        })
        {
            Assert.Contains(allowed, node => node!.GetValue<string>() == implemented);
        }
    }

    [Theory]
    [InlineData("ui_invoke")]
    [InlineData("ui_set_value")]
    [InlineData("ui_wait_for")]
    [InlineData("ui_assert")]
    public void Uia_actions_are_typed_and_declared_in_the_constrained_schema(string action)
    {
        Assert.True(ComputerUseActionParser.IsExecutable(action));
        using JsonDocument document = JsonDocument.Parse(ComputerUseActionParser.ActionJsonSchema);
        JsonElement enums = document.RootElement.GetProperty("properties")
            .GetProperty("action").GetProperty("enum");
        Assert.Contains(enums.EnumerateArray(), node => node.GetString() == action);
    }

    [Fact]
    public void Uia_selector_is_parsed_as_data_not_as_a_shell_command()
    {
        ComputerUseAction? parsed = ComputerUseActionParser.TryParse(
            "{\"action\":\"ui_invoke\",\"selector\":\"btn-save-a1b2\"}");
        Assert.NotNull(parsed);
        Assert.Equal("ui_invoke", parsed!.Action);
        Assert.Equal("btn-save-a1b2", parsed.Selector);
    }

    [Fact]
    public void The_schema_is_valid_json_and_rejects_unknown_fields()
    {
        using JsonDocument document = JsonDocument.Parse(ComputerUseActionParser.ActionJsonSchema);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.False(document.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("action", document.RootElement.GetProperty("required")[0].GetString());
    }

    [Fact]
    public void An_unsupported_action_is_described_as_unsupported_rather_than_as_work_done()
    {
        string description = ComputerUseActionParser.Describe(new ComputerUseAction { Action = "drag" });
        Assert.Contains("unsupported", description);
    }

    private static readonly ScreenBounds Primary = new(0, 0, 1920, 1080);

    // A 1080p primary with a second monitor to its left: the virtual desktop spans x -1920..1919 and y 0..1079.
    private static readonly ScreenBounds TwoMonitors = new(-1920, 0, 3840, 1080);

    [Theory]
    [InlineData("click")]
    [InlineData("double_click")]
    [InlineData("right_click")]
    [InlineData("move")]
    [InlineData("scroll")]
    public void A_pointer_action_without_coordinates_is_refused_before_any_input(string action)
    {
        // A missing x or y used to become 0,0, which clicked the top-left pixel of the primary monitor.
        string? refusal = ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = action }, Primary);

        Assert.NotNull(refusal);
        Assert.Contains("no x/y position", refusal);
    }

    [Fact]
    public void A_pointer_action_that_names_only_one_coordinate_is_refused()
    {
        Assert.NotNull(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = "click", X = 10 }, Primary));
        Assert.NotNull(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = "scroll", Y = 10 }, Primary));
    }

    [Theory]
    [InlineData("click")]
    [InlineData("double_click")]
    [InlineData("right_click")]
    [InlineData("move")]
    [InlineData("scroll")]
    public void A_pointer_action_inside_the_captured_screen_is_allowed(string action)
    {
        Assert.Null(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = action, X = 0, Y = 0 }, Primary));
        Assert.Null(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = action, X = 1919, Y = 1079 }, Primary));
    }

    [Theory]
    [InlineData(1920, 0)]
    [InlineData(0, 1080)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void A_pointer_action_just_outside_the_captured_screen_is_refused_without_echoing_the_position(int x, int y)
    {
        string? refusal = ComputerUseActionParser.ValidateExecutable(
            new ComputerUseAction { Action = "click", X = x, Y = y }, Primary);

        Assert.NotNull(refusal);
        Assert.Contains("outside the captured screen", refusal);
        Assert.DoesNotContain("1920", refusal);
        Assert.DoesNotContain("-1", refusal);
    }

    [Fact]
    public void Coordinates_on_a_monitor_left_of_the_primary_are_allowed_and_the_far_edges_are_exclusive()
    {
        Assert.Null(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = "click", X = -1920, Y = 0 }, TwoMonitors));
        Assert.Null(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = "click", X = 1919, Y = 1079 }, TwoMonitors));
        Assert.NotNull(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = "click", X = 1920, Y = 0 }, TwoMonitors));
        Assert.NotNull(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = "click", X = -1921, Y = 0 }, TwoMonitors));
    }

    [Fact]
    public void An_unreadable_screen_refuses_every_pointer_action_rather_than_guessing()
    {
        Assert.NotNull(ComputerUseActionParser.ValidateExecutable(
            new ComputerUseAction { Action = "click", X = 0, Y = 0 }, new ScreenBounds(0, 0, 0, 0)));
    }

    [Fact]
    public void Keyboard_and_wait_actions_do_not_need_a_screen_position()
    {
        Assert.Null(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = "key", Combo = "Enter" }, Primary));
        Assert.Null(ComputerUseActionParser.ValidateExecutable(new ComputerUseAction { Action = "wait", Ms = 100 }, Primary));
    }

    [Theory]
    [InlineData(null, "left")]
    [InlineData("", "left")]
    [InlineData("left", "left")]
    [InlineData(" Right ", "right")]
    [InlineData("MIDDLE", "middle")]
    public void A_recognized_mouse_button_normalizes_and_a_missing_one_means_left(string? button, string expected)
    {
        Assert.True(ComputerUseActionParser.TryNormalizeButton(button, out string name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("x1")]
    [InlineData("back")]
    [InlineData("left-ish")]
    public void An_unknown_mouse_button_is_refused_instead_of_being_sent_as_a_left_click(string button)
    {
        Assert.False(ComputerUseActionParser.TryNormalizeButton(button, out _));
        Assert.NotNull(ComputerUseActionParser.ValidateExecutable(
            new ComputerUseAction { Action = "click", X = 5, Y = 5, Button = button }, Primary));
        Assert.NotNull(ComputerUseActionParser.ValidateExecutable(
            new ComputerUseAction { Action = "double_click", X = 5, Y = 5, Button = button }, Primary));
    }
}
