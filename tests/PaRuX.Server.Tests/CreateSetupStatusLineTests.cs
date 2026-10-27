using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>The Create status line: the periodic setup refresh may update setup text but must never erase a validation error or result.</summary>
public sealed class CreateSetupStatusLineTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(CreateSetupStatusLine.Checking)]
    [InlineData(CreateSetupStatusLine.Ready)]
    [InlineData(CreateSetupStatusLine.NeedsSetup)]
    public void Setup_messages_and_empty_text_may_be_replaced_by_the_refresh(string? current) =>
        Assert.True(CreateSetupStatusLine.MayReplace(current));

    [Theory]
    [InlineData("Enter text to synthesize.")]
    [InlineData("Choose a source image first.")]
    [InlineData("Generation failed: An image prompt is required.")]
    [InlineData("No Create job is active.")]
    [InlineData("Generation completed locally and registered in Artifact Workspace.")]
    [InlineData("Generating image… · 40%")]
    public void Validation_errors_results_and_progress_are_never_overwritten(string current) =>
        Assert.False(CreateSetupStatusLine.MayReplace(current));

    [Fact]
    public void The_setup_state_maps_to_distinct_honest_messages()
    {
        Assert.Equal(CreateSetupStatusLine.Ready, CreateSetupStatusLine.For(true));
        Assert.Equal(CreateSetupStatusLine.NeedsSetup, CreateSetupStatusLine.For(false));
        Assert.StartsWith("Image generation needs a one-time setup", CreateSetupStatusLine.NeedsSetup);
    }
}
