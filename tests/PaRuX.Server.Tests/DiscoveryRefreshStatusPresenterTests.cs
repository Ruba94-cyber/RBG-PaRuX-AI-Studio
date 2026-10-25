using System;
using System.Collections.Generic;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class DiscoveryRefreshStatusPresenterTests
    {
        private static readonly DateTime BaseTimeUtc = new(2026, 10, 21, 14, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void AutomationId_MatchesExpected()
        {
            Assert.Equal("ModelHub_DiscoveryRefresh_Status_Text", DiscoveryRefreshStatusPresenter.StatusTextAutomationId);
        }

        [Fact]
        public void Present_NormalOnline_WithLastSuccessAndNextCheck_MapsToStatusAndTooltip()
        {
            DateTime lastSuccess = BaseTimeUtc - TimeSpan.FromMinutes(5);
            DateTime nextCheck = BaseTimeUtc + TimeSpan.FromMinutes(15);

            DiscoveryRefreshStatusPresentation presentation = DiscoveryRefreshStatusPresenter.Present(
                lastSuccess,
                nextCheck,
                pausedReason: null,
                isOffline: false,
                BaseTimeUtc);

            Assert.Equal("Last checked 5 minutes ago; next check in 15 minutes", presentation.StatusText);
            Assert.NotNull(presentation.TooltipText);
            Assert.Contains("Last checked:", presentation.TooltipText);
            Assert.Contains("Next check:", presentation.TooltipText);
            Assert.Contains("UTC", presentation.TooltipText);
        }

        [Fact]
        public void FormatRelativePast_VariousIntervals_FormatsExpectedRelativeText()
        {
            Assert.Equal("just now", DiscoveryRefreshStatusPresenter.FormatRelativePast(BaseTimeUtc, BaseTimeUtc));
            Assert.Equal("just now", DiscoveryRefreshStatusPresenter.FormatRelativePast(BaseTimeUtc - TimeSpan.FromSeconds(30), BaseTimeUtc));
            Assert.Equal("just now", DiscoveryRefreshStatusPresenter.FormatRelativePast(BaseTimeUtc - TimeSpan.FromSeconds(59), BaseTimeUtc));
            Assert.Equal("1 minute ago", DiscoveryRefreshStatusPresenter.FormatRelativePast(BaseTimeUtc - TimeSpan.FromSeconds(60), BaseTimeUtc));
            Assert.Equal("1 minute ago", DiscoveryRefreshStatusPresenter.FormatRelativePast(BaseTimeUtc - TimeSpan.FromMinutes(1), BaseTimeUtc));
            Assert.Equal("10 minutes ago", DiscoveryRefreshStatusPresenter.FormatRelativePast(BaseTimeUtc - TimeSpan.FromMinutes(10), BaseTimeUtc));
            Assert.Equal("1 hour ago", DiscoveryRefreshStatusPresenter.FormatRelativePast(BaseTimeUtc - TimeSpan.FromHours(1), BaseTimeUtc));
            Assert.Equal("3 hours ago", DiscoveryRefreshStatusPresenter.FormatRelativePast(BaseTimeUtc - TimeSpan.FromHours(3), BaseTimeUtc));
            Assert.Equal("1 day ago", DiscoveryRefreshStatusPresenter.FormatRelativePast(BaseTimeUtc - TimeSpan.FromDays(1), BaseTimeUtc));
            Assert.Equal("4 days ago", DiscoveryRefreshStatusPresenter.FormatRelativePast(BaseTimeUtc - TimeSpan.FromDays(4), BaseTimeUtc));
        }

        [Fact]
        public void FormatRelativeFuture_VariousIntervals_FormatsExpectedRelativeText()
        {
            Assert.Equal("due now", DiscoveryRefreshStatusPresenter.FormatRelativeFuture(BaseTimeUtc, BaseTimeUtc));
            Assert.Equal("due now", DiscoveryRefreshStatusPresenter.FormatRelativeFuture(BaseTimeUtc - TimeSpan.FromSeconds(10), BaseTimeUtc));
            Assert.Equal("in less than a minute", DiscoveryRefreshStatusPresenter.FormatRelativeFuture(BaseTimeUtc + TimeSpan.FromSeconds(30), BaseTimeUtc));
            Assert.Equal("in 1 minute", DiscoveryRefreshStatusPresenter.FormatRelativeFuture(BaseTimeUtc + TimeSpan.FromSeconds(60), BaseTimeUtc));
            Assert.Equal("in 1 minute", DiscoveryRefreshStatusPresenter.FormatRelativeFuture(BaseTimeUtc + TimeSpan.FromMinutes(1), BaseTimeUtc));
            Assert.Equal("in 20 minutes", DiscoveryRefreshStatusPresenter.FormatRelativeFuture(BaseTimeUtc + TimeSpan.FromMinutes(20), BaseTimeUtc));
            Assert.Equal("in 1 hour", DiscoveryRefreshStatusPresenter.FormatRelativeFuture(BaseTimeUtc + TimeSpan.FromHours(1), BaseTimeUtc));
            Assert.Equal("in 4 hours", DiscoveryRefreshStatusPresenter.FormatRelativeFuture(BaseTimeUtc + TimeSpan.FromHours(4), BaseTimeUtc));
            Assert.Equal("in 1 day", DiscoveryRefreshStatusPresenter.FormatRelativeFuture(BaseTimeUtc + TimeSpan.FromDays(1), BaseTimeUtc));
            Assert.Equal("in 3 days", DiscoveryRefreshStatusPresenter.FormatRelativeFuture(BaseTimeUtc + TimeSpan.FromDays(3), BaseTimeUtc));
        }

        [Fact]
        public void Acceptance2_NeverShowsFutureTimeAsLastChecked()
        {
            DateTime futureTime = BaseTimeUtc + TimeSpan.FromMinutes(10);
            DateTime nextCheck = BaseTimeUtc + TimeSpan.FromMinutes(25);

            // Directly verify relative past formatting clamps future to "just now"
            string relativePast = DiscoveryRefreshStatusPresenter.FormatRelativePast(futureTime, BaseTimeUtc);
            Assert.Equal("just now", relativePast);

            string relativePastFarFuture = DiscoveryRefreshStatusPresenter.FormatRelativePast(BaseTimeUtc + TimeSpan.FromDays(10), BaseTimeUtc);
            Assert.Equal("just now", relativePastFarFuture);

            // Verify the status presentation never shows a future time for last checked
            DiscoveryRefreshStatusPresentation presentation = DiscoveryRefreshStatusPresenter.Present(
                futureTime,
                nextCheck,
                pausedReason: null,
                isOffline: false,
                BaseTimeUtc);

            Assert.Equal("Last checked just now; next check in 25 minutes", presentation.StatusText);
            Assert.DoesNotContain("in 10 minutes", presentation.StatusText);
        }

        [Fact]
        public void Acceptance3_StatesOffline_OnlyWhenFailedAndLastTrustedListExists()
        {
            DateTime trustedTime = BaseTimeUtc - TimeSpan.FromHours(2);

            // Case A: Failed flag is true AND last trusted list exists -> States "Offline"
            DiscoveryRefreshStatusPresentation caseA = DiscoveryRefreshStatusPresenter.Present(
                trustedTime,
                nextRefreshUtc: null,
                pausedReason: null,
                isOffline: true,
                BaseTimeUtc);

            Assert.StartsWith("Offline: showing the last trusted list from 2 hours ago", caseA.StatusText);
            Assert.Contains("Offline", caseA.StatusText);

            // Case B: Failed flag is true BUT last trusted list does NOT exist -> Does NOT state "Offline"
            DiscoveryRefreshStatusPresentation caseB = DiscoveryRefreshStatusPresenter.Present(
                lastSuccessUtc: null,
                nextRefreshUtc: null,
                pausedReason: null,
                isOffline: true,
                BaseTimeUtc);

            Assert.DoesNotContain("Offline", caseB.StatusText);
            Assert.Equal(DiscoveryRefreshStatusPresenter.CatalogUnavailableText, caseB.StatusText);

            // Case C: Failed flag is false, last trusted list exists -> Does NOT state "Offline"
            DiscoveryRefreshStatusPresentation caseC = DiscoveryRefreshStatusPresenter.Present(
                trustedTime,
                nextRefreshUtc: BaseTimeUtc + TimeSpan.FromMinutes(10),
                pausedReason: null,
                isOffline: false,
                BaseTimeUtc);

            Assert.DoesNotContain("Offline", caseC.StatusText);
            Assert.StartsWith("Last checked 2 hours ago", caseC.StatusText);

            // Case D: Failed flag is false, last trusted list does not exist -> Does NOT state "Offline"
            DiscoveryRefreshStatusPresentation caseD = DiscoveryRefreshStatusPresenter.Present(
                lastSuccessUtc: null,
                nextRefreshUtc: null,
                pausedReason: null,
                isOffline: false,
                BaseTimeUtc);

            Assert.DoesNotContain("Offline", caseD.StatusText);
            Assert.Equal("Not checked yet", caseD.StatusText);
        }

        [Theory]
        [InlineData("Paused while Gaming is active - Return to AI to resume checks")]
        [InlineData("gaming_active")]
        [InlineData("GamingActive")]
        public void PausedState_GamingActive_ShowsGamingPauseMessage(string inputReason)
        {
            DateTime lastSuccess = BaseTimeUtc - TimeSpan.FromMinutes(10);
            DateTime nextCheck = BaseTimeUtc + TimeSpan.FromMinutes(5);

            // Paused state takes precedence over normal or offline times
            DiscoveryRefreshStatusPresentation presentation = DiscoveryRefreshStatusPresenter.Present(
                lastSuccess,
                nextCheck,
                pausedReason: inputReason,
                isOffline: false,
                BaseTimeUtc);

            Assert.Equal(DiscoveryRefreshStatusPresenter.DefaultPausedReason, presentation.StatusText);
            Assert.NotNull(presentation.TooltipText);
            Assert.Contains("Gaming Mode is active", presentation.TooltipText);
        }

        [Fact]
        public void PausedState_CustomReason_UsesCustomText()
        {
            string customReason = "Discovery paused: custom test pause reason";
            DiscoveryRefreshStatusPresentation presentation = DiscoveryRefreshStatusPresenter.Present(
                lastSuccessUtc: BaseTimeUtc - TimeSpan.FromMinutes(10),
                nextRefreshUtc: null,
                pausedReason: customReason,
                isOffline: true, // even if offline, paused takes priority
                BaseTimeUtc);

            Assert.Equal(customReason, presentation.StatusText);
        }

        [Fact]
        public void Present_CatalogOverloads_MapsModelDiscoveryCatalogCorrectly()
        {
            // Catalog with successful prior check
            var catalogOk = new ModelDiscoveryCatalog
            {
                LastSuccessUtc = BaseTimeUtc - TimeSpan.FromMinutes(12),
                NextRefreshUtc = BaseTimeUtc + TimeSpan.FromMinutes(8),
                LastErrorCode = null
            };

            DiscoveryRefreshStatusPresentation presOk = DiscoveryRefreshStatusPresenter.Present(
                catalogOk,
                pausedReason: null,
                isOffline: null,
                BaseTimeUtc);

            Assert.Equal("Last checked 12 minutes ago; next check in 8 minutes", presOk.StatusText);

            // Catalog with error code indicating failure
            var catalogFailed = new ModelDiscoveryCatalog
            {
                LastSuccessUtc = BaseTimeUtc - TimeSpan.FromHours(1),
                NextRefreshUtc = BaseTimeUtc + TimeSpan.FromMinutes(5),
                LastErrorCode = "network_unavailable"
            };

            DiscoveryRefreshStatusPresentation presFailed = DiscoveryRefreshStatusPresenter.Present(
                catalogFailed,
                pausedReason: null,
                isOffline: null,
                BaseTimeUtc);

            Assert.Equal("Offline: showing the last trusted list from 1 hour ago", presFailed.StatusText);

            // Catalog with null
            DiscoveryRefreshStatusPresentation presNull = DiscoveryRefreshStatusPresenter.Present(
                (ModelDiscoveryCatalog?)null,
                pausedReason: null,
                isOffline: null,
                BaseTimeUtc);

            Assert.Equal("Not checked yet", presNull.StatusText);
        }

        [Fact]
        public void Present_WithoutLastSuccessOrNextCheck_FormatsExpectedFallback()
        {
            // Next check scheduled but never succeeded before
            DiscoveryRefreshStatusPresentation presNextOnly = DiscoveryRefreshStatusPresenter.Present(
                lastSuccessUtc: null,
                nextRefreshUtc: BaseTimeUtc + TimeSpan.FromMinutes(5),
                pausedReason: null,
                isOffline: false,
                BaseTimeUtc);

            Assert.Equal("Not checked yet; next check in 5 minutes", presNextOnly.StatusText);

            // Last success recorded but next refresh is not scheduled
            DiscoveryRefreshStatusPresentation presLastOnly = DiscoveryRefreshStatusPresenter.Present(
                lastSuccessUtc: BaseTimeUtc - TimeSpan.FromMinutes(5),
                nextRefreshUtc: null,
                pausedReason: null,
                isOffline: false,
                BaseTimeUtc);

            Assert.Equal("Last checked 5 minutes ago", presLastOnly.StatusText);
        }
    }
}
