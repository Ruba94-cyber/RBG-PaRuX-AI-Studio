using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WindowsUiTreeObservationServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("winapp")]
    [InlineData("winapp.exe")]
    [InlineData("C:/missing/executable.exe")]
    [InlineData("https://example.com/winapp.exe")]
    public void Requires_an_explicit_existing_absolute_executable(string? path)
    {
        Assert.False(WindowsUiTreeObservationService.IsConfiguredExecutable(path));
    }

    [Fact]
    public void Non_json_and_empty_ui_observations_never_become_evidence()
    {
        Assert.Equal("empty", WindowsUiTreeObservationService.ValidateJsonEvidence("").Status);
        Assert.Equal("invalid_json", WindowsUiTreeObservationService.ValidateJsonEvidence("visible button").Status);
        Assert.Equal("invalid_ui_tree", WindowsUiTreeObservationService.ValidateJsonEvidence("\"hello\"").Status);
        Assert.False(WindowsUiTreeObservationService.ValidateJsonEvidence("not-json").HasEvidence);
    }

    [Fact]
    public void Bounded_structured_ui_tree_is_observable_but_not_a_verdict()
    {
        var observation = WindowsUiTreeObservationService.ValidateJsonEvidence(
            "{\"windows\":[{\"hwnd\":131906,\"title\":\"Notepad\",\"elements\":[{\"type\":\"Button\",\"selector\":\"Save\"}]}]}", 131906);
        Assert.True(observation.HasEvidence);
        Assert.Equal("observed", observation.Status);
        Assert.Contains("Notepad", observation.Tree);
    }

    [Theory]
    [InlineData("""{"error":"missing target"}""")]
    [InlineData("""{"windows":[]}""")]
    [InlineData("""{"windows":[{"elements":[]}]}""")]
    public void Unrelated_or_error_json_never_counts_as_ui_tree(string input)
    {
        Assert.False(WindowsUiTreeObservationService.ValidateJsonEvidence(input).HasEvidence);
    }

    [Fact]
    public void Ui_tree_must_match_the_observed_foreground_window_handle()
    {
        const string valid = """{"windows":[{"hwnd":131906,"elements":[{"selector":"btn-save"}]}]}""";
        Assert.True(WindowsUiTreeObservationService.ValidateJsonEvidence(valid, 131906).HasEvidence);
        var wrong = WindowsUiTreeObservationService.ValidateJsonEvidence(valid, 777777);
        Assert.False(wrong.HasEvidence);
        Assert.Equal("target_unverified", wrong.Status);
    }

    [Fact]
    public void Excessive_ui_tree_is_rejected_instead_of_filling_model_context()
    {
        var observation = WindowsUiTreeObservationService.ValidateJsonEvidence(
            "{\"content\":\"" + new string('x', 8_100) + "\"}");
        Assert.False(observation.HasEvidence);
        Assert.Equal("too_large", observation.Status);
    }
}
