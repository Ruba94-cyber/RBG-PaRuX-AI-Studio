using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class CapabilityOverridePresenterTests
    {
        [Fact]
        public void AutomationIdsAndLabels_MatchContract()
        {
            Assert.Equal("ModelRouting_CapabilityOverride_ComboBox", CapabilityOverridePresenter.OverrideComboBoxAutomationId);
            Assert.Equal("ModelRouting_CapabilityOverride_Model_ComboBox", CapabilityOverridePresenter.OverrideModelComboBoxAutomationId);
            Assert.Equal("ModelRouting_CapabilityOverride_Status_Text", CapabilityOverridePresenter.OverrideStatusTextAutomationId);

            Assert.Equal("Advanced override", CapabilityOverridePresenter.OverrideComboBoxLabel);
            Assert.Equal("Override model", CapabilityOverridePresenter.OverrideModelComboBoxLabel);
            Assert.Equal("Override status", CapabilityOverridePresenter.OverrideStatusLabel);
        }

        [Theory]
        [InlineData(ScopedRouteMode.Automatic, "Automatic · PaRuX decides", "PaRuX decides which model to use based on compatibility, speed, and intelligence.")]
        [InlineData(ScopedRouteMode.Prefer, "Prefer · compatible model with fallback", "Use this model when it is compatible, with a visible fallback.")]
        [InlineData(ScopedRouteMode.Force, "Force · strict model, fail if incompatible", "Use only this model; fail with the reason rather than using another model.")]
        public void Presenter_MapsThreeStates_ToLabelsAndExplanations(string mode, string expectedLabel, string expectedExplanation)
        {
            Assert.Equal(expectedLabel, CapabilityOverridePresenter.GetStateLabel(mode));
            Assert.Equal(expectedExplanation, CapabilityOverridePresenter.GetStateExplanation(mode));

            CapabilityOverridePresentation presentation = CapabilityOverridePresenter.Present(mode, "test-model.gguf");
            Assert.Equal(expectedLabel, presentation.CurrentLabel);
            Assert.Equal(expectedExplanation, presentation.CurrentExplanation);
        }

        [Fact]
        public void GetStateOptions_ReturnsThreeStatesInCorrectOrder()
        {
            IReadOnlyList<CapabilityOverrideOptionPresentation> options = CapabilityOverridePresenter.GetStateOptions();
            Assert.Equal(3, options.Count);

            Assert.Equal(ScopedRouteMode.Automatic, options[0].Mode);
            Assert.Equal(CapabilityOverridePresenter.AutomaticLabel, options[0].Label);
            Assert.False(options[0].RequiresModel);
            Assert.False(options[0].RequiresConfirmation);

            Assert.Equal(ScopedRouteMode.Prefer, options[1].Mode);
            Assert.Equal(CapabilityOverridePresenter.PreferLabel, options[1].Label);
            Assert.True(options[1].RequiresModel);
            Assert.False(options[1].RequiresConfirmation);

            Assert.Equal(ScopedRouteMode.Force, options[2].Mode);
            Assert.Equal(CapabilityOverridePresenter.ForceLabel, options[2].Label);
            Assert.True(options[2].RequiresModel);
            Assert.True(options[2].RequiresConfirmation);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void RejectsPrefer_WithoutModelChoice_WithPlainMessage(string? modelChoice)
        {
            CapabilityOverrideValidationResult result = CapabilityOverridePresenter.Validate(ScopedRouteMode.Prefer, modelChoice);
            Assert.False(result.IsValid);
            Assert.Equal(CapabilityOverridePresenter.MissingModelForPreferMessage, result.ErrorMessage);
            Assert.Equal("Prefer requires a model choice.", result.ErrorMessage);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void RejectsForce_WithoutModelChoice_WithPlainMessage(string? modelChoice)
        {
            CapabilityOverrideValidationResult result = CapabilityOverridePresenter.Validate(ScopedRouteMode.Force, modelChoice);
            Assert.False(result.IsValid);
            Assert.Equal(CapabilityOverridePresenter.MissingModelForForceMessage, result.ErrorMessage);
            Assert.Equal("Force requires a model choice.", result.ErrorMessage);
        }

        [Theory]
        [InlineData("qwen3-4b.gguf")]
        [InlineData("deepseek-r1-7b.gguf")]
        public void AcceptsPreferAndForce_WithModelChoice(string modelChoice)
        {
            CapabilityOverrideValidationResult preferResult = CapabilityOverridePresenter.Validate(ScopedRouteMode.Prefer, modelChoice);
            Assert.True(preferResult.IsValid);
            Assert.Null(preferResult.ErrorMessage);

            CapabilityOverrideValidationResult forceResult = CapabilityOverridePresenter.Validate(ScopedRouteMode.Force, modelChoice);
            Assert.True(forceResult.IsValid);
            Assert.Null(forceResult.ErrorMessage);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("any-model.gguf")]
        public void AcceptsAutomatic_RegardlessOfModelChoice(string? modelChoice)
        {
            CapabilityOverrideValidationResult result = CapabilityOverridePresenter.Validate(ScopedRouteMode.Automatic, modelChoice);
            Assert.True(result.IsValid);
            Assert.Null(result.ErrorMessage);
        }

        [Fact]
        public void ChoosingForce_ShowsWarningText_ThatRequestWillFailRatherThanUseAnotherModel()
        {
            Assert.Equal("A request will fail rather than use another model.", CapabilityOverridePresenter.ForceWarningText);

            CapabilityOverridePresentation presentation = CapabilityOverridePresenter.Present(ScopedRouteMode.Force, "qwen3.gguf");
            Assert.True(presentation.WarningVisible);
            Assert.Equal(CapabilityOverridePresenter.ForceWarningText, presentation.WarningText);
            Assert.Equal(CapabilityOverridePresenter.ForceWarningText, presentation.StatusText);

            Assert.Equal(CapabilityOverridePresenter.ForceWarningText, CapabilityOverridePresenter.GetForceWarning(ScopedRouteMode.Force));
            Assert.Null(CapabilityOverridePresenter.GetForceWarning(ScopedRouteMode.Prefer));
            Assert.Null(CapabilityOverridePresenter.GetForceWarning(ScopedRouteMode.Automatic));
        }

        [Fact]
        public void ChoosingForce_RequiresConfirmationBeforeSaving()
        {
            Assert.True(CapabilityOverridePresenter.RequiresConfirmation(ScopedRouteMode.Force));
            Assert.False(CapabilityOverridePresenter.RequiresConfirmation(ScopedRouteMode.Prefer));
            Assert.False(CapabilityOverridePresenter.RequiresConfirmation(ScopedRouteMode.Automatic));

            Assert.Equal("Confirm Force Override", CapabilityOverridePresenter.ForceConfirmationTitle);
            Assert.Contains("fail rather than use another model", CapabilityOverridePresenter.ForceConfirmationPrompt);
        }

        [Theory]
        [InlineData(ScopedRouteMode.Automatic, false)]
        [InlineData(ScopedRouteMode.Prefer, true)]
        [InlineData(ScopedRouteMode.Force, true)]
        public void RequiresModelSelection_OnlyForPreferAndForce(string mode, bool expected)
        {
            Assert.Equal(expected, CapabilityOverridePresenter.RequiresModelSelection(mode));
        }

        [Theory]
        [InlineData("automatic", "automatic")]
        [InlineData("AUTOMATIC", "automatic")]
        [InlineData("prefer", "prefer")]
        [InlineData("Prefer", "prefer")]
        [InlineData("force", "force")]
        [InlineData("FORCE", "force")]
        [InlineData("unknown", "automatic")]
        [InlineData(null, "automatic")]
        [InlineData("", "automatic")]
        public void NormalizeMode_HandlesVariations(string? input, string expected)
        {
            Assert.Equal(expected, CapabilityOverridePresenter.NormalizeMode(input));
        }

        [Fact]
        public void NarrowWidthAndWrapping_LabelsAndExplanations_AreSingleSentencesWithoutTrailingNewlines()
        {
            string[] stringsToCheck =
            {
                CapabilityOverridePresenter.OverrideComboBoxLabel,
                CapabilityOverridePresenter.OverrideModelComboBoxLabel,
                CapabilityOverridePresenter.OverrideStatusLabel,
                CapabilityOverridePresenter.AutomaticLabel,
                CapabilityOverridePresenter.AutomaticExplanation,
                CapabilityOverridePresenter.PreferLabel,
                CapabilityOverridePresenter.PreferExplanation,
                CapabilityOverridePresenter.ForceLabel,
                CapabilityOverridePresenter.ForceExplanation,
                CapabilityOverridePresenter.ForceWarningText,
                CapabilityOverridePresenter.MissingModelForPreferMessage,
                CapabilityOverridePresenter.MissingModelForForceMessage
            };

            foreach (string text in stringsToCheck)
            {
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain("\n", text);
                Assert.DoesNotContain("\r", text);
                Assert.DoesNotContain("\t", text);
            }
        }

        [Fact]
        public void FormatSaveSuccess_ReturnsDescriptiveSingleSentence()
        {
            string auto = CapabilityOverridePresenter.FormatSaveSuccess("coding", ScopedRouteMode.Automatic, null);
            Assert.Equal("Automatic routing override saved for coding.", auto);

            string prefer = CapabilityOverridePresenter.FormatSaveSuccess("coding", ScopedRouteMode.Prefer, "qwen.gguf");
            Assert.Equal("Prefer qwen.gguf override saved for coding.", prefer);

            string force = CapabilityOverridePresenter.FormatSaveSuccess("reasoning", ScopedRouteMode.Force, "deepseek.gguf");
            Assert.Equal("Force deepseek.gguf override saved for reasoning.", force);
        }

        [Fact]
        public async Task StubCapabilityOverrideClient_PerformsCrudOperations()
        {
            var client = new StubCapabilityOverrideClient();

            IReadOnlyList<ScopedRoutingOverrideDto> initial = await client.ListOverridesAsync();
            Assert.Empty(initial);

            var setRequest = new SetScopedRoutingOverrideRequest
            {
                Mode = ScopedRouteMode.Prefer,
                ManagedFileName = "model.gguf"
            };

            ScopedRoutingOverrideDto saved = await client.SetOverrideAsync("coding", "balanced", setRequest);
            Assert.Equal("coding", saved.RouteRole);
            Assert.Equal("balanced", saved.AutoProfile);
            Assert.Equal(ScopedRouteMode.Prefer, saved.Mode);
            Assert.Equal("model.gguf", saved.ManagedFileName);

            ScopedRoutingOverrideDto? retrieved = await client.GetOverrideAsync("coding", "balanced");
            Assert.NotNull(retrieved);
            Assert.Equal(ScopedRouteMode.Prefer, retrieved.Mode);
            Assert.Equal("model.gguf", retrieved.ManagedFileName);

            IReadOnlyList<ScopedRoutingOverrideDto> list = await client.ListOverridesAsync();
            Assert.Single(list);

            bool deleted = await client.DeleteOverrideAsync("coding", "balanced");
            Assert.True(deleted);

            ScopedRoutingOverrideDto? afterDelete = await client.GetOverrideAsync("coding", "balanced");
            Assert.Null(afterDelete);
        }
    }
}
