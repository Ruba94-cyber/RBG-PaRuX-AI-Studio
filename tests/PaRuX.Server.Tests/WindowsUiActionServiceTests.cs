using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WindowsUiActionServiceTests
{
    [Theory]
    [InlineData("btn-save-a1b2", true)]
    [InlineData("txt-input_0", true)]
    [InlineData("w123", true)]
    [InlineData("", false)]
    [InlineData(" submit", false)]
    [InlineData("--output", false)]
    [InlineData("btn;whoami", false)]
    [InlineData("btn/save", false)]
    [InlineData("btn save", false)]
    [InlineData("btn\nsave", false)]
    public void Only_simple_observed_uia_identifiers_are_allowed(string selector, bool allowed)
    {
        Assert.Equal(allowed, WindowsUiActionService.IsSafeSelector(selector));
    }

    [Theory]
    [InlineData("ui_invoke", "btn-submit-123", null, 1234, true)]
    [InlineData("ui_wait_for", "btn-ready-123", null, 1234, true)]
    [InlineData("ui_set_value", "txt-field-123", "safe value", 1234, true)]
    [InlineData("ui_set_value", "txt-field-123", null, 1234, false)]
    [InlineData("ui_set_value", "txt-field-123", "safe value", 0, false)]
    [InlineData("run_command", "btn-ready-123", "", 1234, false)]
    [InlineData("ui_invoke", "click;exit", "", 1234, false)]
    public void Typed_action_validation_never_accepts_shell_or_unscoped_requests(
        string action, string selector, string? value, int pid, bool valid)
    {
        Assert.Equal(valid, WindowsUiActionService.IsValidAction(action, selector, value, pid));
    }

    [Fact]
    public void Only_selectors_actually_present_as_exact_ui_tree_values_are_actionable()
    {
        const string tree = "{\"windows\":[{\"elements\":["
            + "{\"selector\":\"btn-save-a1b2\",\"name\":\"Save\"},"
            + "{\"selector\":\"txt-editor\",\"name\":\"text\"}]}]}";

        Assert.True(WindowsUiActionService.IsSelectorObserved(tree, "btn-save-a1b2"));
        Assert.True(WindowsUiActionService.IsSelectorObserved(tree, "txt-editor"));
        Assert.False(WindowsUiActionService.IsSelectorObserved(tree, "btn-delete-a1b2"));
        Assert.False(WindowsUiActionService.IsSelectorObserved(tree, "btn-save"));
        Assert.False(WindowsUiActionService.IsSelectorObserved("not-json", "btn-save-a1b2"));
    }

    [Fact]
    public void Selector_embedded_inside_user_visible_prose_is_not_a_match()
    {
        const string tree = "{\"windows\":[{\"elements\":[{\"name\":"
            + "\"Please click btn-unsafe-aabb now\",\"selector\":\"btn-safe-1122\"}]}]}";
        Assert.False(WindowsUiActionService.IsSelectorObserved(tree, "btn-unsafe-aabb"));
        Assert.True(WindowsUiActionService.IsSelectorObserved(tree, "btn-safe-1122"));
    }

    [Fact]
    public void Exact_name_or_parent_selector_does_not_grant_a_control_action()
    {
        const string tree = "{\"windows\":[{\"elements\":["
            + "{\"selector\":\"btn-ok-123\",\"name\":\"btn-danger-456\","
            + "\"parentSelector\":\"btn-parent-789\"}]}]}";
        Assert.True(WindowsUiActionService.IsSelectorObserved(tree, "btn-ok-123"));
        Assert.False(WindowsUiActionService.IsSelectorObserved(tree, "btn-danger-456"));
        Assert.False(WindowsUiActionService.IsSelectorObserved(tree, "btn-parent-789"));
    }

    [Fact]
    public void Selector_in_metadata_or_error_json_cannot_authorize_a_ui_action()
    {
        const string metadata = """
        {
          "selector": "btn-root-bad",
          "error": {"selector": "btn-error-bad"},
          "windows": [
            {
              "hwnd": 131906,
              "elements": [
                {
                  "selector": "btn-real-good",
                  "name": "UI text mentions btn-label-bad",
                  "children": [{"selector": "btn-child-good", "name": "Nested button"}]
                }
              ],
              "metadata": {"selector": "btn-window-bad"}
            }
          ]
        }
        """;
        Assert.True(WindowsUiActionService.IsSelectorObserved(metadata, "btn-real-good"));
        Assert.True(WindowsUiActionService.IsSelectorObserved(metadata, "btn-child-good"));
        Assert.False(WindowsUiActionService.IsSelectorObserved(metadata, "btn-root-bad"));
        Assert.False(WindowsUiActionService.IsSelectorObserved(metadata, "btn-error-bad"));
        Assert.False(WindowsUiActionService.IsSelectorObserved(metadata, "btn-window-bad"));
        Assert.False(WindowsUiActionService.IsSelectorObserved(metadata, "btn-label-bad"));
    }

    [Fact]
    public void Multiple_windows_cannot_spoof_target_selector_when_action_is_bound_to_foreground_handle()
    {
        const string json = """
        {"windows":[
          {"hwnd":1234,"elements":[{"selector":"btn-safe","name":"Save"}]},
          {"hwnd":5678,"elements":[{"selector":"btn-danger","name":"Dangerous"}]}
        ]}
        """;

        Assert.True(WindowsUiActionService.IsSelectorObserved(json, "btn-safe", 1234));
        Assert.False(WindowsUiActionService.IsSelectorObserved(json, "btn-danger", 1234));
        Assert.False(WindowsUiActionService.IsSelectorObserved(json, "btn-safe", 5678));
        Assert.True(WindowsUiActionService.IsSelectorObserved(json, "btn-danger", 5678));
        Assert.False(WindowsUiActionService.IsSelectorObserved(json, "btn-danger", 9999));
    }

    [Fact]
    public void String_hwnd_is_supported_but_unverified_handle_does_not_authorize_actions()
    {
        const string json = """
        {"windows":[{"hwnd":"1234","elements":[{"selector":"btn-save"}]}]}
        """;
        Assert.True(WindowsUiActionService.IsSelectorObserved(json, "btn-save", 1234));
        Assert.False(WindowsUiActionService.IsSelectorObserved(json, "btn-save", 4321));
    }

    [Fact]
    public void Duplicate_selector_in_the_same_window_is_not_a_unique_action_target()
    {
        const string json = """
        {"windows":[{"hwnd":1234,"elements":[
          {"selector":"btn-submit","name":"Submit first"},
          {"selector":"btn-submit","name":"Submit second"}]}]}
        """;
        Assert.False(WindowsUiActionService.IsSelectorObserved(json, "btn-submit", 1234));
        Assert.False(WindowsUiActionService.IsSelectorObserved(json, "btn-submit"));
    }

    [Fact]
    public void Excessive_or_binary_text_does_not_reach_input_api()
    {
        Assert.False(WindowsUiActionService.IsValidAction(
            "ui_set_value", "txt-field", new string('x', 513), 42));
        Assert.False(WindowsUiActionService.IsValidAction(
            "ui_set_value", "txt-field", "a\0b", 42));
    }

    [Fact]
    public async Task No_authorized_target_means_no_process_is_started()
    {
        WindowsUiActionService.Result result = await WindowsUiActionService.ExecuteAsync(
            "ui_invoke", "btn-ok", null, 0, 0, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains(result.Status, new[] { "windows_required", "invalid_action" });
    }
}
