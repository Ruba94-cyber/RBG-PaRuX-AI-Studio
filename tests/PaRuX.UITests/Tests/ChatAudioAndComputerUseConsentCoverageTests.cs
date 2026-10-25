using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Chat audio attach and Computer Use consent surfaces. Compile-checked; run only on an owned desktop session.
/// The consent dialog appears when a chat request is a Computer Use request (for example "use my computer ...").
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ChatAudioAndComputerUseConsentCoverageTests
{
    private static void Run(string testName, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(testName);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(testName, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "Modality")]
    public void Chat_composer_offers_an_audio_attach_button() => Run(
        nameof(Chat_composer_offers_an_audio_attach_button), app =>
        {
            AutomationElement button = UiWait.ById(app.MainWindow, "Chat_Audio_Attach_Button", ControlType.Button);
            Assert.Equal("Attach audio", button.Name);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "Modality")]
    public void Computer_use_request_shows_a_consent_dialog_that_defaults_to_cancel() => Run(
        nameof(Computer_use_request_shows_a_consent_dialog_that_defaults_to_cancel), app =>
        {
            var chat = new PageObjects.ChatPage(app.MainWindow);
            chat.SendPrompt("use my computer to tidy the desktop");

            AutomationElement dialog = UiWait.ById(app.MainWindow, "Chat_ComputerUse_Consent_Dialog", ControlType.Window);
            Assert.NotNull(dialog);
            AutomationElement confirm = UiWait.ById(app.MainWindow, "Chat_ComputerUse_Consent_Confirm_Button", ControlType.Button);
            Assert.Equal("Allow once", confirm.Name);
            // Do not confirm: consent must always be an explicit human action.
        });
}
