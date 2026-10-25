using System;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class DiscoveryPolicyPresenterTests
    {
        [Fact]
        public void Defaults_AreAskAndAsk()
        {
            Assert.Equal("ask", DiscoveryPolicyPresenter.DefaultAutoEvaluate);
            Assert.Equal("ask", DiscoveryPolicyPresenter.DefaultPromotion);
            Assert.False(DiscoveryPolicyPresenter.IsAutoEvaluateOn(DiscoveryPolicyPresenter.DefaultAutoEvaluate));

            var defaultState = new DiscoveryPolicyState();
            Assert.Equal("ask", defaultState.AutoEvaluate);
            Assert.Equal("ask", defaultState.PromotionPolicy);
            Assert.False(defaultState.GamingActive);
        }

        [Fact]
        public void PresentAutoEvaluate_NormalMode_MapsLabelExplanationAndEnabled()
        {
            PolicyControlPresentation offPresentation = DiscoveryPolicyPresenter.PresentAutoEvaluate("ask", gamingActive: false);
            Assert.Equal(DiscoveryPolicyPresenter.AutoEvaluateLabel, offPresentation.Label);
            Assert.True(offPresentation.IsEnabled);
            Assert.Equal("ask", offPresentation.Value);
            Assert.Null(offPresentation.UnavailableReason);
            Assert.Contains("Ask before", offPresentation.Explanation);

            PolicyControlPresentation onPresentation = DiscoveryPolicyPresenter.PresentAutoEvaluate("auto", gamingActive: false);
            Assert.Equal(DiscoveryPolicyPresenter.AutoEvaluateLabel, onPresentation.Label);
            Assert.True(onPresentation.IsEnabled);
            Assert.Equal("auto", onPresentation.Value);
            Assert.Null(onPresentation.UnavailableReason);
            Assert.Contains("Automatically benchmark", onPresentation.Explanation);
        }

        [Fact]
        public void PresentAutoEvaluate_GamingActive_ShowsUnavailableWithPausedReason()
        {
            PolicyControlPresentation presentation = DiscoveryPolicyPresenter.PresentAutoEvaluate("auto", gamingActive: true);
            Assert.Equal(DiscoveryPolicyPresenter.AutoEvaluateLabel, presentation.Label);
            Assert.False(presentation.IsEnabled);
            Assert.Equal(DiscoveryPolicyPresenter.GamingPausedReason, presentation.UnavailableReason);
            Assert.Equal(DiscoveryPolicyPresenter.GamingPausedReason, presentation.Explanation);
            Assert.Equal("Paused while Gaming is active", presentation.Explanation);
        }

        [Fact]
        public void PresentPromotion_MapsAllThreePolicyValues()
        {
            PolicyControlPresentation askPresentation = DiscoveryPolicyPresenter.PresentPromotion("ask");
            Assert.Equal(DiscoveryPolicyPresenter.PromotionPolicyLabel, askPresentation.Label);
            Assert.True(askPresentation.IsEnabled);
            Assert.Equal("ask", askPresentation.Value);
            Assert.Contains("Ask before switching", askPresentation.Explanation);

            PolicyControlPresentation clearlyBetterPresentation = DiscoveryPolicyPresenter.PresentPromotion("clearly_better");
            Assert.Equal(DiscoveryPolicyPresenter.PromotionPolicyLabel, clearlyBetterPresentation.Label);
            Assert.True(clearlyBetterPresentation.IsEnabled);
            Assert.Equal("clearly_better", clearlyBetterPresentation.Value);
            Assert.Contains("strictly better quality and speed", clearlyBetterPresentation.Explanation);

            PolicyControlPresentation neverPresentation = DiscoveryPolicyPresenter.PresentPromotion("never");
            Assert.Equal(DiscoveryPolicyPresenter.PromotionPolicyLabel, neverPresentation.Label);
            Assert.True(neverPresentation.IsEnabled);
            Assert.Equal("never", neverPresentation.Value);
            Assert.Contains("Never replace pinned or manual choices", neverPresentation.Explanation);

            var options = DiscoveryPolicyPresenter.GetPromotionOptions();
            Assert.Equal(3, options.Count);
            Assert.Contains(options, o => o.Value == "ask" && o.Label == "Ask before replacing");
            Assert.Contains(options, o => o.Value == "clearly_better" && o.Label == "Auto-promote only when clearly better");
            Assert.Contains(options, o => o.Value == "never" && o.Label == "Never replace pinned or manual choices");
        }

        [Theory]
        [InlineData(null, "ask")]
        [InlineData("", "ask")]
        [InlineData("   ", "ask")]
        [InlineData("unknown_val", "ask")]
        [InlineData("off", "ask")]
        [InlineData("false", "ask")]
        [InlineData("true", "auto")]
        [InlineData("on", "auto")]
        [InlineData("auto", "auto")]
        [InlineData("automatic", "auto")]
        public void Normalization_AutoEvaluate_NormalizesExpected(string? input, string expected)
        {
            Assert.Equal(expected, DiscoveryPolicyPresenter.NormalizeAutoEvaluate(input));
        }

        [Theory]
        [InlineData(null, "ask")]
        [InlineData("", "ask")]
        [InlineData("unknown_policy", "ask")]
        [InlineData("ask", "ask")]
        [InlineData("clearly_better", "clearly_better")]
        [InlineData("better", "clearly_better")]
        [InlineData("never", "never")]
        [InlineData("manual", "never")]
        public void Normalization_Promotion_NormalizesExpected(string? input, string expected)
        {
            Assert.Equal(expected, DiscoveryPolicyPresenter.NormalizePromotion(input));
        }

        [Fact]
        public void StatusFormatting_SavedAndError_ProducePlainLanguage()
        {
            string saved = DiscoveryPolicyPresenter.FormatSavedStatus();
            Assert.Equal("Applied for this session only. Saving to PaRuX Core is not available yet.", saved);

            string error = DiscoveryPolicyPresenter.FormatErrorStatus("Network timeout");
            Assert.StartsWith("Couldn't save candidate discovery policy: Network timeout", error);

            string actionError = DiscoveryPolicyPresenter.FormatErrorStatus("automatic evaluation", "Core unavailable");
            Assert.Equal("Couldn't save automatic evaluation: Core unavailable", actionError);
        }

        [Fact]
        public void RevertHelpers_PreservePreviousConfirmedState()
        {
            bool revertedToggle = DiscoveryPolicyPresenter.ResolveRevertAutoEvaluate(requested: true, lastConfirmedState: false);
            Assert.False(revertedToggle);

            string revertedPromotion = DiscoveryPolicyPresenter.ResolveRevertPromotion(requested: "clearly_better", lastConfirmedPolicy: "ask");
            Assert.Equal("ask", revertedPromotion);
        }

        [Fact]
        public async Task StubClientSeam_GetAndSetPolicies()
        {
            var stub = new StubDiscoveryPolicyClient(initialGamingActive: false);

            DiscoveryPolicyState initial = await stub.GetPolicyAsync(CancellationToken.None);
            Assert.Equal("ask", initial.AutoEvaluate);
            Assert.Equal("ask", initial.PromotionPolicy);
            Assert.False(initial.GamingActive);

            DiscoveryPolicyState updatedAuto = await stub.SetAutoEvaluateAsync("auto", CancellationToken.None);
            Assert.Equal("auto", updatedAuto.AutoEvaluate);
            Assert.Equal("ask", updatedAuto.PromotionPolicy);

            DiscoveryPolicyState updatedPromotion = await stub.SetPromotionPolicyAsync("clearly_better", CancellationToken.None);
            Assert.Equal("auto", updatedPromotion.AutoEvaluate);
            Assert.Equal("clearly_better", updatedPromotion.PromotionPolicy);

            stub.SetGamingActive(true);
            DiscoveryPolicyState gamingState = await stub.GetPolicyAsync(CancellationToken.None);
            Assert.True(gamingState.GamingActive);

            DiscoveryPolicyPresentation presented = DiscoveryPolicyPresenter.Present(gamingState, gamingActive: true);
            Assert.False(presented.AutoEvaluate.IsEnabled);
            Assert.Equal("Paused while Gaming is active", presented.AutoEvaluate.Explanation);
        }

        [Fact]
        public void FullPresentation_AggregatesBothControls()
        {
            var state = new DiscoveryPolicyState("ask", "clearly_better", GamingActive: false);
            DiscoveryPolicyPresentation presentation = DiscoveryPolicyPresenter.Present(state, gamingActive: false, "All settings up to date.");

            Assert.Equal(DiscoveryPolicyPresenter.AutoEvaluateLabel, presentation.AutoEvaluate.Label);
            Assert.True(presentation.AutoEvaluate.IsEnabled);
            Assert.Equal("ask", presentation.AutoEvaluate.Value);

            Assert.Equal(DiscoveryPolicyPresenter.PromotionPolicyLabel, presentation.Promotion.Label);
            Assert.True(presentation.Promotion.IsEnabled);
            Assert.Equal("clearly_better", presentation.Promotion.Value);

            Assert.Equal(3, presentation.PromotionOptions.Count);
            Assert.Equal("All settings up to date.", presentation.StatusText);
        }

        [Fact]
        public void NarrowWidthAndWrapping_LabelsAndOptions_AreSingleSentencesWithoutTrailingNewlines()
        {
            Assert.DoesNotContain("\n", DiscoveryPolicyPresenter.AutoEvaluateLabel);
            Assert.DoesNotContain("\n", DiscoveryPolicyPresenter.PromotionPolicyLabel);
            Assert.DoesNotContain("\n", DiscoveryPolicyPresenter.GamingPausedReason);

            Assert.DoesNotContain("\n", DiscoveryPolicyPresenter.GetAutoEvaluateExplanation("ask", gamingActive: false));
            Assert.DoesNotContain("\n", DiscoveryPolicyPresenter.GetAutoEvaluateExplanation("auto", gamingActive: false));
            Assert.DoesNotContain("\n", DiscoveryPolicyPresenter.GetAutoEvaluateExplanation("auto", gamingActive: true));

            foreach (PromotionOptionPresentation option in DiscoveryPolicyPresenter.GetPromotionOptions())
            {
                Assert.DoesNotContain("\n", option.Label);
                Assert.DoesNotContain("\n", option.Explanation);
                Assert.EndsWith(".", option.Explanation);
            }
        }
    }
}
