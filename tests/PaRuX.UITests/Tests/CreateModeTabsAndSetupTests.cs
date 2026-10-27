using System;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>The redesigned Create workspace: one mode tab at a time, honest per-mode setup cards, example chips and size presets.</summary>
[Collection(UiTestCollection.Name)]
public sealed class CreateModeTabsAndSetupTests
{
    private static readonly string[] AllTabs = { "Image", "Video", "Music", "Edit", "3D", "Speech", "Workflows", "Gallery" };

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
    public void Create_mode_tabs_exist_start_on_image_and_show_only_the_selected_mode() => Run(
        nameof(Create_mode_tabs_exist_start_on_image_and_show_only_the_selected_mode), (app, page) =>
        {
            foreach (string tab in AllTabs) Assert.NotNull(page.FindModeTab(tab));

            Assert.True(page.IsModeSelected("Image"), "Image is the default Create mode.");
            Assert.NotNull(page.FindRawById("Create_Image_Prompt_Box"));
            Assert.Null(page.FindRawById("Create_TTS_Text_Box"));

            page.SelectMode("Speech");
            Assert.NotNull(page.FindRawById("Create_TTS_Text_Box"));
            UiWait.For(() => page.FindRawById("Create_Image_Prompt_Box") == null, "Image panel hidden while Speech is selected");
            Assert.False(page.IsModeSelected("Image"));

            page.SelectMode("Edit");
            Assert.NotNull(page.FindRawById("Create_EditMode_ComboBox"));
            UiWait.For(() => page.FindRawById("Create_TTS_Text_Box") == null, "Speech panel hidden while Edit is selected");

            // Every remaining tab must be selectable (no dead tab) and become the selected one.
            foreach (string tab in new[] { "Video", "Music", "3D", "Workflows", "Gallery", "Image" })
            {
                page.SelectMode(tab);
                Assert.True(page.IsModeSelected(tab), tab + " tab should be selected after choosing it.");
            }

            // The status, cancel and provenance stay available whichever mode is shown.
            Assert.NotNull(page.CancelActiveJobButton);
            Assert.False(string.IsNullOrWhiteSpace(page.GetStatusText()));
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_setup_cards_are_honest_about_the_core_setup_state() => Run(
        nameof(Create_setup_cards_are_honest_about_the_core_setup_state), (app, page) =>
        {
            string settled = page.WaitForSettledSetupStatus();

            foreach ((string tab, string type) in new[] { ("Image", "image"), ("Video", "video"), ("Edit", "edit"), ("3D", "3d"), ("Speech", "speech") })
            {
                page.SelectMode(tab);
                // A card is Collapsed (absent from the tree) once that capability is ready; otherwise it must say what is missing.
                UiWait.For(() =>
                {
                    var card = page.FindRawById("Create_Setup_" + type + "_Card");
                    if (card == null) return true;
                    var headline = page.FindRawById("Create_Setup_" + type + "_Headline");
                    return !string.IsNullOrWhiteSpace(headline?.Properties.Name.ValueOrDefault);
                }, "the " + tab + " setup card is either hidden (ready) or explains what is missing");
            }

            page.SelectMode("Image");
            bool imageCardShown = page.FindRawById("Create_Setup_image_Card") != null;
            if (settled == CreateWorkspaceModesPage.NeedsSetupStatus)
            {
                Assert.True(imageCardShown, "The status says image generation needs setup, so the Image setup card must be shown.");
                var button = page.FindRawById("Create_Setup_image_Button");
                Assert.NotNull(button);
                Assert.False(string.IsNullOrWhiteSpace(button!.Properties.Name.ValueOrDefault),
                    "The setup card button must say what it does.");
            }
            else
            {
                Assert.Equal(CreateWorkspaceModesPage.ReadyStatus, settled);
                Assert.False(imageCardShown, "The status says Create is ready, so no Image setup card may be shown.");
            }
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_example_chips_fill_the_prompt_without_starting_a_job() => Run(
        nameof(Create_example_chips_fill_the_prompt_without_starting_a_job), (app, page) =>
        {
            AutomationElement prompt = UiWait.ById(app.MainWindow, "Create_Image_Prompt_Box", ControlType.Edit);
            Assert.True(string.IsNullOrEmpty(prompt.Patterns.Value.Pattern.Value));

            AutomationElement chip = UiWait.ById(app.MainWindow, "Create_Image_Example_2", ControlType.Button);
            string example = chip.Properties.Name.ValueOrDefault;
            Assert.False(string.IsNullOrWhiteSpace(example));
            page.ClickById("Create_Image_Example_2", ControlType.Button);

            UiWait.For(() => prompt.Patterns.Value.Pattern.Value == example, "image prompt is filled with the chosen example");
            // Only the prompt changes: no job started, so the cancel control still reports idle.
            page.ClickById("Create_Cancel_Active_Job_Button", ControlType.Button);
            UiWait.For(() => page.GetStatusText() == "No Create job is active.", "no job was started by the example chip");

            page.SelectMode("Video");
            AutomationElement videoPrompt = UiWait.ById(app.MainWindow, "Create_Video_Prompt_Box", ControlType.Edit);
            string videoExample = UiWait.ById(app.MainWindow, "Create_Video_Example_1", ControlType.Button).Properties.Name.ValueOrDefault;
            page.ClickById("Create_Video_Example_1", ControlType.Button);
            UiWait.For(() => videoPrompt.Patterns.Value.Pattern.Value == videoExample, "video prompt is filled with the chosen example");
            Assert.Equal(example, UiWaitImagePrompt(app));
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_size_presets_fill_width_and_height() => Run(
        nameof(Create_size_presets_fill_width_and_height), (app, page) =>
        {
            Assert.Equal((1024.0, 1024.0), (ReadNumber(page, "Create_Image_Width_Box"), ReadNumber(page, "Create_Image_Height_Box")));

            page.ClickById("Create_Image_Size_Landscape", ControlType.Button);
            UiWait.For(() => ReadNumber(page, "Create_Image_Width_Box") == 1344 && ReadNumber(page, "Create_Image_Height_Box") == 768,
                "Landscape preset sets 1344x768");

            page.ClickById("Create_Image_Size_Phone", ControlType.Button);
            UiWait.For(() => ReadNumber(page, "Create_Image_Width_Box") == 768 && ReadNumber(page, "Create_Image_Height_Box") == 1344,
                "Phone preset sets 768x1344");

            page.ClickById("Create_Image_Size_Square", ControlType.Button);
            UiWait.For(() => ReadNumber(page, "Create_Image_Width_Box") == 1024 && ReadNumber(page, "Create_Image_Height_Box") == 1024,
                "Square preset restores 1024x1024");

            page.SelectMode("Video");
            Assert.Equal((1280.0, 720.0), (ReadNumber(page, "Create_Video_Width_Box"), ReadNumber(page, "Create_Video_Height_Box")));
            page.ClickById("Create_Video_Size_Vertical", ControlType.Button);
            UiWait.For(() => ReadNumber(page, "Create_Video_Width_Box") == 480 && ReadNumber(page, "Create_Video_Height_Box") == 832,
                "Vertical video preset sets 480x832");
        });

    private static string UiWaitImagePrompt(UiTestApplication app)
    {
        // Switching modes must not lose what was typed or chosen in another mode.
        new CreateWorkspaceModesPage(app.MainWindow, app).SelectMode("Image");
        return UiWait.ById(app.MainWindow, "Create_Image_Prompt_Box", ControlType.Edit).Patterns.Value.Pattern.Value;
    }

    /// <summary>Reads a NumberBox through whichever pattern WinUI exposes for it (range value, value, or its inner edit).</summary>
    private static double ReadNumber(CreateWorkspaceModesPage page, string id)
    {
        AutomationElement box = page.FindRawById(id) ?? throw new InvalidOperationException("Missing number box " + id);
        var range = box.Patterns.RangeValue.PatternOrDefault;
        if (range != null) return range.Value.Value;
        var value = box.Patterns.Value.PatternOrDefault;
        if (value != null && double.TryParse(value.Value.Value, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed)) return parsed;
        AutomationElement? edit = box.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        if (edit != null && double.TryParse(edit.Patterns.Value.Pattern.Value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed)) return parsed;
        throw new InvalidOperationException("Cannot read the number box " + id);
    }
}
