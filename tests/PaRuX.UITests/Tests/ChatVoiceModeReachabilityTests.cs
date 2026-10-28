using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Voice mode (hands-free conversation) reachability. Compile-checked; run only on an owned desktop session.
/// The test never turns the mode on, so it needs no microphone and records nothing.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ChatVoiceModeReachabilityTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "Modality")]
    public void Chat_composer_offers_a_voice_mode_toggle_that_is_off_by_default()
    {
        const string name = nameof(Chat_composer_offers_a_voice_mode_toggle_that_is_off_by_default);
        using var app = new UiTestApplication(name);
        try
        {
            AutomationElement toggle = UiWait.ById(app.MainWindow, "Chat_VoiceMode_Toggle", ControlType.Button);
            Assert.Equal("Start voice conversation", toggle.Name);

            // The status text is collapsed until the mode is on, so it must not be reachable while off.
            var cf = app.MainWindow.ConditionFactory;
            AutomationElement? status = app.MainWindow.FindFirstDescendant(cf.ByAutomationId("Chat_VoiceMode_Status_Text"));
            Assert.True(status == null || status.IsOffscreen || string.IsNullOrEmpty(status.Name));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
