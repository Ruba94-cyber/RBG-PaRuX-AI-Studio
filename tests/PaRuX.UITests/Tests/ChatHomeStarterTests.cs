using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ChatHomeStarterTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Starter_prefills_composer_without_sending_and_home_does_not_claim_fixed_model()
    {
        const string name = nameof(Starter_prefills_composer_without_sending_and_home_does_not_claim_fixed_model);
        using var app = new UiTestApplication(name);
        try
        {
            var description = UiWait.ById(app.MainWindow, "Chat_Home_Description_Text", ControlType.Text);
            Assert.True(description.Name == "Start a conversation with PaRuX." || description.Name == "Inizia una conversazione con PaRuX.");
            var suggestion = UiWait.ById(app.MainWindow, "Chat_Home_Code_Button", ControlType.Button);
            Assert.True(suggestion.Name == "Improve this codebase" || suggestion.Name == "Migliora questo progetto");
            suggestion.Patterns.Invoke.Pattern.Invoke();
            var composer = UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit).AsTextBox();
            UiWait.For(() => composer.Text.StartsWith("Improve the code in this project:", StringComparison.Ordinal) || composer.Text.StartsWith("Migliora il codice di questo progetto:", StringComparison.Ordinal), "starter prefilled composer");
            Assert.False(description.Properties.IsOffscreen.ValueOrDefault);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
