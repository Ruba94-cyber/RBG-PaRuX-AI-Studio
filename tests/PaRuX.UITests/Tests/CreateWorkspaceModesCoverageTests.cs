using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class CreateWorkspaceModesCoverageTests
{
    private static void Run(string step, Action<UiTestApplication, CreateWorkspaceModesPage> action)
    {
        using var app = new UiTestApplication(step);
        try
        {
            var page = new CreateWorkspaceModesPage(app.MainWindow, app);
            page.OpenCreateWorkspace();
            action(app, page);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(step, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_workspace_displays_ready_status_and_all_six_creation_mode_sections() => Run(
        nameof(Create_workspace_displays_ready_status_and_all_six_creation_mode_sections), (app, page) =>
        {
            // The status starts as "Checking what image generation needs..." and settles on what Core's media setup state
            // really is. Against the test Core that is either ready or needs-setup; the setup card must agree with it.
            string settled = page.WaitForSettledSetupStatus();
            AssertSetupCardMatchesStatus(page, settled);

            (string Tab, string Heading)[] expectedSections =
            {
                ("Image", "IMAGE"), ("Video", "VIDEO"), ("Edit", "IMAGE EDIT"), ("3D", "IMAGE → 3D"),
                ("Speech", "TEXT TO SPEECH"), ("Speech", "SPEECH TO TEXT")
            };
            foreach ((string tab, string section) in expectedSections)
            {
                page.SelectMode(tab);
                var heading = page.FindTextElement(section);
                Assert.NotNull(heading);
                Assert.Equal(section, heading.Name);
            }

            var subtitle = page.FindTextElement("Image, video, edit, 3D and speech share one local workspace.");
            Assert.NotNull(subtitle);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_cancel_control_exists_and_reports_idle_state_when_no_job_active() => Run(
        nameof(Create_cancel_control_exists_and_reports_idle_state_when_no_job_active), (app, page) =>
        {
            var cancel = page.CancelActiveJobButton;
            Assert.True(cancel.IsEnabled, "Cancel button should be enabled when the workspace is idle.");

            page.ClickById("Create_Cancel_Active_Job_Button", ControlType.Button);

            UiWait.For(() => page.GetStatusText() == "No Create job is active.",
                "status updates to idle cancellation note");
            Assert.Equal("No Create job is active.", page.GetStatusText());
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_image_mode_validates_required_prompt_before_generation() => Run(
        nameof(Create_image_mode_validates_required_prompt_before_generation), (app, page) =>
        {
            page.ClickButtonByName("Generate image");

            UiWait.For(() => page.GetStatusText() == "Generation failed: An image prompt is required.",
                "status reports image prompt validation failure");
            Assert.Equal("Generation failed: An image prompt is required.", page.GetStatusText());
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_video_mode_validates_required_prompt_and_exposes_default_parameters() => Run(
        nameof(Create_video_mode_validates_required_prompt_and_exposes_default_parameters), (app, page) =>
        {
            page.SelectMode("Video");
            var startImageLabel = page.FindTextElement("No start image: text-to-video");
            Assert.NotNull(startImageLabel);

            page.ClickButtonByName("Clear start image");
            Assert.Equal("No start image: text-to-video", startImageLabel.Name);

            page.ClickButtonByName("Generate video");

            UiWait.For(() => page.GetStatusText() == "Generation failed: A video prompt is required.",
                "status reports video prompt validation failure");
            Assert.Equal("Generation failed: A video prompt is required.", page.GetStatusText());
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_edit_mode_validates_required_source_image_selection() => Run(
        nameof(Create_edit_mode_validates_required_source_image_selection), (app, page) =>
        {
            page.SelectMode("Edit");
            var sourceLabel = page.FindTextElement("No source image selected");
            Assert.NotNull(sourceLabel);

            page.ClickById("Create_Edit_Run_Button", ControlType.Button);

            UiWait.For(() => page.GetStatusText() == "Generation failed: Choose a source image first.",
                "status reports missing source image failure");
            Assert.Equal("Generation failed: Choose a source image first.", page.GetStatusText());
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_threed_mode_displays_engine_guidance_and_validates_source_image() => Run(
        nameof(Create_threed_mode_displays_engine_guidance_and_validates_source_image), (app, page) =>
        {
            page.SelectMode("3D");
            var engineGuidance = page.FindTextElement("Uses the managed local 3D engine.");
            Assert.NotNull(engineGuidance);
            Assert.Contains("Runtime/Settings can install it before generation.", engineGuidance.Name, StringComparison.Ordinal);

            page.ClickButtonByName("Generate 3D model");

            UiWait.For(() => page.GetStatusText() == "Choose a source image first.",
                "status reports 3D missing source image validation failure");
            Assert.Equal("Choose a source image first.", page.GetStatusText());
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_speech_modes_validate_required_text_and_audio_inputs() => Run(
        nameof(Create_speech_modes_validate_required_text_and_audio_inputs), (app, page) =>
        {
            page.SelectMode("Speech");
            page.ClickById("Create_TTS_Run_Button", ControlType.Button);

            UiWait.For(() => page.GetStatusText() == "Enter text to synthesize.",
                "status reports empty TTS text validation error");
            Assert.Equal("Enter text to synthesize.", page.GetStatusText());

            var audioLabel = page.FindTextElement("No audio file selected");
            Assert.NotNull(audioLabel);

            page.ClickButtonByName("Transcribe");

            UiWait.For(() => page.GetStatusText() == "Choose an audio file first.",
                "status reports empty STT audio validation error");
            Assert.Equal("Choose an audio file first.", page.GetStatusText());
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_modes_maintain_input_isolation_across_creation_sections() => Run(
        nameof(Create_modes_maintain_input_isolation_across_creation_sections), (app, page) =>
        {
            page.SelectMode("Speech");
            var voiceBox = page.TtsVoiceBox;
            voiceBox.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            voiceBox.Patterns.Value.Pattern.SetValue("isolated_test_voice");

            UiWait.For(() => voiceBox.Patterns.Value.Pattern.Value == "isolated_test_voice",
                "voice box retains the entered value");

            var ttsTextBox = page.TtsTextBox;
            Assert.True(string.IsNullOrEmpty(ttsTextBox.Patterns.Value.Pattern.Value),
                "TTS text box must remain unaffected by voice box input.");

            var sttAudioLabel = page.FindTextElement("No audio file selected");
            Assert.NotNull(sttAudioLabel);

            // Leaving for another mode and coming back must neither lose nor leak the typed voice.
            page.SelectMode("Video");
            var videoStartLabel = page.FindTextElement("No start image: text-to-video");
            Assert.NotNull(videoStartLabel);
            page.SelectMode("Speech");
            UiWait.For(() => page.TtsVoiceBox.Patterns.Value.Pattern.Value == "isolated_test_voice",
                "voice box keeps its value after switching modes and back");
            Assert.True(string.IsNullOrEmpty(page.TtsTextBox.Patterns.Value.Pattern.Value),
                "TTS text box must still be empty after switching modes.");

            page.ClickById("Create_Cancel_Active_Job_Button", ControlType.Button);
            UiWait.For(() => page.GetStatusText() == "No Create job is active.",
                "status reports no active job after input modification");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_media_provenance_row_allows_toggling_details() => Run(
        nameof(Create_media_provenance_row_allows_toggling_details), (app, page) =>
        {
            var provenanceText = page.ProvenanceText;
            Assert.Equal("Provenance not recorded for this older result", provenanceText.Name);

            var detailsButton = page.ProvenanceDetailsButton;
            Assert.True(detailsButton.IsEnabled);
            Assert.Equal("Details", detailsButton.Name);

            page.ClickById("Create_Media_Provenance_Details_Button", ControlType.Button);

            page.ClickById("Create_Media_Provenance_Details_Button", ControlType.Button);
            // Toggling the provenance details must not disturb the honest setup status.
            AssertSetupCardMatchesStatus(page, page.WaitForSettledSetupStatus());
        });

    /// <summary>The status line and the Image setup card are two views of the same Core setup state: they must agree.</summary>
    private static void AssertSetupCardMatchesStatus(CreateWorkspaceModesPage page, string settledStatus)
    {
        page.SelectMode("Image");
        if (settledStatus == CreateWorkspaceModesPage.NeedsSetupStatus)
        {
            UiWait.For(() => page.FindRawById("Create_Setup_image_Card") is { } card && !card.Properties.IsOffscreen.ValueOrDefault,
                "Image setup card is shown while image generation needs setup");
            Assert.NotNull(page.FindRawById("Create_Setup_image_Button"));
        }
        else
        {
            Assert.Equal(CreateWorkspaceModesPage.ReadyStatus, settledStatus);
            UiWait.For(() => page.FindRawById("Create_Setup_image_Card") is not { } card || card.Properties.IsOffscreen.ValueOrDefault,
                "Image setup card is hidden once image generation is ready");
        }
    }
}
