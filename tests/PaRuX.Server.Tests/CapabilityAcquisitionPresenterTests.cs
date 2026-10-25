using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PaRuX.Services;
using UiAcquisitionOutcome = PaRuX.Services.CapabilityAcquisitionOutcome;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class CapabilityAcquisitionPresenterTests
    {
        private const long OneGiB = 1024L * 1024L * 1024L;

        [Fact]
        public void AutomationIdsAndLabels_MatchContract()
        {
            Assert.Equal("ModelHub_CapabilityAcquire_Card", CapabilityAcquisitionPresenter.CardAutomationId);
            Assert.Equal("ModelHub_CapabilityAcquire_Download_Button", CapabilityAcquisitionPresenter.DownloadButtonAutomationId);
            Assert.Equal("ModelHub_CapabilityAcquire_Later_Button", CapabilityAcquisitionPresenter.LaterButtonAutomationId);

            Assert.Equal("Download", CapabilityAcquisitionPresenter.DownloadButtonLabel);
            Assert.Equal("Not now", CapabilityAcquisitionPresenter.LaterButtonLabel);

            Assert.True(CapabilityAcquisitionPresenter.DownloadTabIndex < CapabilityAcquisitionPresenter.LaterTabIndex,
                "Tab order must reach Download before Not now.");
        }

        [Theory]
        [InlineData(UiAcquisitionOutcome.ProposeWithConsent)]
        [InlineData(UiAcquisitionOutcome.AlreadySatisfied)]
        [InlineData(UiAcquisitionOutcome.NoCompatibleCandidate)]
        [InlineData(UiAcquisitionOutcome.StorageBudgetExceeded)]
        [InlineData(UiAcquisitionOutcome.LicenceRequiresDecision)]
        [InlineData(UiAcquisitionOutcome.DoNotOffer)]
        public void NoOutcome_EverOffersCloudAction(UiAcquisitionOutcome outcome)
        {
            var proposal = new CapabilityAcquisitionProposal
            {
                Outcome = outcome,
                Capability = "image editing",
                ModelName = "instruct-pix2pix-gguf",
                DownloadSizeBytes = 2 * OneGiB,
                StorageImpactBytes = 3 * OneGiB
            };

            CapabilityAcquisitionPresentation presentation = CapabilityAcquisitionPresenter.Present(proposal);

            Assert.False(presentation.OffersCloudAction, "Card presentation must never offer cloud.");
            Assert.False(CapabilityAcquisitionPresenter.OffersCloudAction(outcome), "Presenter must not offer cloud.");

            foreach (CapabilityAcquisitionAction action in presentation.AllowedActions)
            {
                Assert.DoesNotContain("cloud", action.ToString(), StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain("cloud", presentation.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cloud", presentation.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cloud", presentation.SizeText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ProposeWithConsent_MapsTitleReasonSizeAndAllowedActions()
        {
            var proposal = new CapabilityAcquisitionProposal
            {
                Outcome = UiAcquisitionOutcome.ProposeWithConsent,
                Capability = "image editing",
                ModelName = "instruct-pix2pix-gguf",
                DownloadSizeBytes = (long)(1.5 * OneGiB),
                StorageImpactBytes = 2 * OneGiB,
                LicenceDecisionNeeded = false,
                HardwareFit = ModelFit.ExcellentFit
            };

            CapabilityAcquisitionPresentation presentation = CapabilityAcquisitionPresenter.Present(proposal);

            Assert.Equal(UiAcquisitionOutcome.ProposeWithConsent, presentation.Outcome);
            Assert.Equal("Acquire image editing capability", presentation.Title);
            Assert.Contains("1.5 GB download", presentation.SizeText);
            Assert.Contains("2.0 GB storage impact", presentation.SizeText);

            Assert.True(presentation.CanDownload);
            Assert.False(presentation.IsLicenceDecisionNeeded);
            Assert.Equal(2, presentation.AllowedActions.Count);
            Assert.Equal(CapabilityAcquisitionAction.Download, presentation.AllowedActions[0]);
            Assert.Equal(CapabilityAcquisitionAction.NotNow, presentation.AllowedActions[1]);

            AssertOneSentence(presentation.Reason);
            Assert.Contains("image editing", presentation.Reason);
            Assert.Contains("instruct-pix2pix-gguf", presentation.Reason);
            Assert.Contains("1.5 GB download", presentation.Reason);
            Assert.NotNull(presentation.HardwareFitVerdict);
            Assert.Contains("Fits", presentation.HardwareFitVerdict);
        }

        [Fact]
        public void ProposeWithConsent_WhenLicenceDecisionNeeded_ExplainsLicenceInReason()
        {
            var proposal = new CapabilityAcquisitionProposal
            {
                Outcome = UiAcquisitionOutcome.ProposeWithConsent,
                Capability = "image editing",
                ModelName = "custom-edit-model",
                DownloadSizeBytes = 1 * OneGiB,
                StorageImpactBytes = 2 * OneGiB,
                LicenceDecisionNeeded = true
            };

            CapabilityAcquisitionPresentation presentation = CapabilityAcquisitionPresenter.Present(proposal);

            Assert.True(presentation.IsLicenceDecisionNeeded);
            Assert.True(presentation.CanDownload);
            AssertOneSentence(presentation.Reason);
            Assert.Contains("licence decision", presentation.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void LicenceRequiresDecision_MapsTitleAndRequiresReviewInReason()
        {
            var proposal = new CapabilityAcquisitionProposal
            {
                Outcome = UiAcquisitionOutcome.LicenceRequiresDecision,
                Capability = "vision",
                ModelName = "qwen-vl-chat",
                DownloadSizeBytes = 2 * OneGiB,
                StorageImpactBytes = 3 * OneGiB,
                LicenceDecisionNeeded = true
            };

            CapabilityAcquisitionPresentation presentation = CapabilityAcquisitionPresenter.Present(proposal);

            Assert.Equal("Licence review needed: qwen-vl-chat", presentation.Title);
            Assert.True(presentation.CanDownload);
            Assert.True(presentation.IsLicenceDecisionNeeded);
            Assert.Equal(2, presentation.AllowedActions.Count);
            Assert.Equal(CapabilityAcquisitionAction.Download, presentation.AllowedActions[0]);
            Assert.Equal(CapabilityAcquisitionAction.NotNow, presentation.AllowedActions[1]);

            AssertOneSentence(presentation.Reason);
            Assert.Contains("licence terms", presentation.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2.0 GB download", presentation.Reason);
        }

        [Fact]
        public void AlreadySatisfied_DoesNotAllowDownload_ExplainsAlreadyInstalled()
        {
            var proposal = new CapabilityAcquisitionProposal
            {
                Outcome = UiAcquisitionOutcome.AlreadySatisfied,
                Capability = "coding"
            };

            CapabilityAcquisitionPresentation presentation = CapabilityAcquisitionPresenter.Present(proposal);

            Assert.Equal("Coding is already installed", presentation.Title);
            Assert.False(presentation.CanDownload);
            Assert.Equal("0 B", presentation.SizeText);
            Assert.Single(presentation.AllowedActions);
            Assert.Equal(CapabilityAcquisitionAction.NotNow, presentation.AllowedActions[0]);

            AssertOneSentence(presentation.Reason);
            Assert.Contains("already includes an installed model", presentation.Reason);
        }

        [Fact]
        public void NoCompatibleCandidate_DoesNotAllowDownload_ExplainsNoCandidate()
        {
            var proposal = new CapabilityAcquisitionProposal
            {
                Outcome = UiAcquisitionOutcome.NoCompatibleCandidate,
                Capability = "audio-transcription"
            };

            CapabilityAcquisitionPresentation presentation = CapabilityAcquisitionPresenter.Present(proposal);

            Assert.Equal("No compatible model for audio-transcription", presentation.Title);
            Assert.False(presentation.CanDownload);
            Assert.Equal("0 B", presentation.SizeText);
            Assert.Single(presentation.AllowedActions);
            Assert.Equal(CapabilityAcquisitionAction.NotNow, presentation.AllowedActions[0]);

            AssertOneSentence(presentation.Reason);
            Assert.Contains("No compatible local model candidate was found", presentation.Reason);
        }

        [Fact]
        public void StorageBudgetExceeded_DoesNotAllowDownload_ExplainsBudget()
        {
            var proposal = new CapabilityAcquisitionProposal
            {
                Outcome = UiAcquisitionOutcome.StorageBudgetExceeded,
                Capability = "reasoning",
                ModelName = "deepseek-r1-70b-gguf",
                DownloadSizeBytes = 40 * OneGiB,
                StorageImpactBytes = 45 * OneGiB
            };

            CapabilityAcquisitionPresentation presentation = CapabilityAcquisitionPresenter.Present(proposal);

            Assert.Equal("Storage budget exceeded for reasoning", presentation.Title);
            Assert.False(presentation.CanDownload);
            Assert.Contains("40.0 GB download", presentation.SizeText);
            Assert.Contains("45.0 GB storage impact", presentation.SizeText);
            Assert.Single(presentation.AllowedActions);
            Assert.Equal(CapabilityAcquisitionAction.NotNow, presentation.AllowedActions[0]);

            AssertOneSentence(presentation.Reason);
            Assert.Contains("exceeds the available AI storage budget", presentation.Reason);
        }

        [Fact]
        public void DoNotOffer_HasNoActionsAndCannotDownload()
        {
            var proposal = new CapabilityAcquisitionProposal
            {
                Outcome = UiAcquisitionOutcome.DoNotOffer,
                Capability = "general"
            };

            CapabilityAcquisitionPresentation presentation = CapabilityAcquisitionPresenter.Present(proposal);

            Assert.Equal("Capability acquisition unavailable", presentation.Title);
            Assert.False(presentation.CanDownload);
            Assert.Empty(presentation.AllowedActions);

            AssertOneSentence(presentation.Reason);
            Assert.Contains("not offered", presentation.Reason);
        }

        [Fact]
        public void CustomReason_IsPreservedWhenProvided()
        {
            const string custom = "Custom explanation provided by orchestrator.";
            var proposal = new CapabilityAcquisitionProposal
            {
                Outcome = UiAcquisitionOutcome.ProposeWithConsent,
                Capability = "image editing",
                CustomReason = custom
            };

            CapabilityAcquisitionPresentation presentation = CapabilityAcquisitionPresenter.Present(proposal);
            Assert.Equal(custom, presentation.Reason);
        }

        [Fact]
        public void FormatBytes_ProducesPlainUnits()
        {
            Assert.Equal("0 B", CapabilityAcquisitionPresenter.FormatBytes(0));
            Assert.Equal("0 B", CapabilityAcquisitionPresenter.FormatBytes(-10));
            Assert.Equal("500 B", CapabilityAcquisitionPresenter.FormatBytes(500));
            Assert.Equal("1.0 KB", CapabilityAcquisitionPresenter.FormatBytes(1024));
            Assert.Equal("10.0 MB", CapabilityAcquisitionPresenter.FormatBytes(10 * 1024 * 1024));
            Assert.Equal("1.5 GB", CapabilityAcquisitionPresenter.FormatBytes((long)(1.5 * 1024 * 1024 * 1024)));
            Assert.Equal("2.0 TB", CapabilityAcquisitionPresenter.FormatBytes(2L * 1024 * 1024 * 1024 * 1024));
        }

        [Fact]
        public void FormatSizeText_HandlesVariousInputs()
        {
            Assert.Equal("0 B", CapabilityAcquisitionPresenter.FormatSizeText(null, null));
            Assert.Equal("0 B", CapabilityAcquisitionPresenter.FormatSizeText(0, 0));
            Assert.Equal("500.0 MB download", CapabilityAcquisitionPresenter.FormatSizeText(500 * 1024 * 1024, null));
            Assert.Equal("1.0 GB download · 2.0 GB storage impact",
                CapabilityAcquisitionPresenter.FormatSizeText(1 * OneGiB, 2 * OneGiB));
        }

        [Fact]
        public void Present_ThrowsOnNullProposal()
        {
            Assert.Throws<ArgumentNullException>(() => CapabilityAcquisitionPresenter.Present(null!));
        }

        private static void AssertOneSentence(string text)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), "Sentence text must not be empty.");
            Assert.EndsWith(".", text.Trim());

            // A single sentence should not contain a period followed by a space and an uppercase letter
            // (ignoring abbreviations or numbers like 1.5 GB).
            MatchCollection intermediateSentenceTerminators = Regex.Matches(text, @"(?<![A-Za-z0-9]\.[A-Za-z0-9])\.\s+[A-Z]");
            Assert.Empty(intermediateSentenceTerminators);
        }
    }
}
