using System;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class OnboardingReturnPolicyTests
    {
        [Fact]
        public void Evaluate_CompletedWithoutVerifiedAnswer_ReturnsReturnToSetup()
        {
            OnboardingReturnAction action = OnboardingReturnPolicy.Evaluate(
                OnboardingDownloadState.Completed,
                chatAnswerVerified: false);

            Assert.Equal(OnboardingReturnAction.ReturnToSetup, action);
            Assert.True(OnboardingReturnPolicy.ShouldReturnToSetup(OnboardingDownloadState.Completed, false));
            Assert.False(OnboardingReturnPolicy.ShouldShowSuccess(OnboardingDownloadState.Completed, false));
        }

        [Fact]
        public void Evaluate_CompletedWithVerifiedAnswer_ReturnsShowSuccess()
        {
            OnboardingReturnAction action = OnboardingReturnPolicy.Evaluate(
                OnboardingDownloadState.Completed,
                chatAnswerVerified: true);

            Assert.Equal(OnboardingReturnAction.ShowSuccess, action);
            Assert.True(OnboardingReturnPolicy.ShouldShowSuccess(OnboardingDownloadState.Completed, true));
            Assert.False(OnboardingReturnPolicy.ShouldReturnToSetup(OnboardingDownloadState.Completed, true));
        }

        [Fact]
        public void Evaluate_FailedWithoutVerifiedAnswer_ReturnsShowRetry()
        {
            OnboardingReturnAction action = OnboardingReturnPolicy.Evaluate(
                OnboardingDownloadState.Failed,
                chatAnswerVerified: false);

            Assert.Equal(OnboardingReturnAction.ShowRetry, action);
            Assert.True(OnboardingReturnPolicy.ShouldShowRetry(OnboardingDownloadState.Failed, false));
            Assert.False(OnboardingReturnPolicy.ShouldReturnToSetup(OnboardingDownloadState.Failed, false));
        }

        [Fact]
        public void Evaluate_CancelledWithoutVerifiedAnswer_ReturnsShowRetry()
        {
            OnboardingReturnAction action = OnboardingReturnPolicy.Evaluate(
                OnboardingDownloadState.Cancelled,
                chatAnswerVerified: false);

            Assert.Equal(OnboardingReturnAction.ShowRetry, action);
            Assert.True(OnboardingReturnPolicy.ShouldShowRetry(OnboardingDownloadState.Cancelled, false));
        }

        [Theory]
        [InlineData(OnboardingDownloadState.Queued)]
        [InlineData(OnboardingDownloadState.Downloading)]
        [InlineData(OnboardingDownloadState.Paused)]
        public void Evaluate_ActiveOrPausedWithoutVerifiedAnswer_ReturnsStay(OnboardingDownloadState state)
        {
            OnboardingReturnAction action = OnboardingReturnPolicy.Evaluate(state, chatAnswerVerified: false);

            Assert.Equal(OnboardingReturnAction.Stay, action);
            Assert.False(OnboardingReturnPolicy.ShouldReturnToSetup(state, false));
            Assert.False(OnboardingReturnPolicy.ShouldShowSuccess(state, false));
            Assert.False(OnboardingReturnPolicy.ShouldShowRetry(state, false));
        }

        [Theory]
        [InlineData(OnboardingDownloadState.Queued)]
        [InlineData(OnboardingDownloadState.Downloading)]
        [InlineData(OnboardingDownloadState.Paused)]
        [InlineData(OnboardingDownloadState.Completed)]
        [InlineData(OnboardingDownloadState.Failed)]
        [InlineData(OnboardingDownloadState.Cancelled)]
        public void Evaluate_AnyStateWithVerifiedAnswer_ReturnsShowSuccess(OnboardingDownloadState state)
        {
            OnboardingReturnAction action = OnboardingReturnPolicy.Evaluate(state, chatAnswerVerified: true);

            Assert.Equal(OnboardingReturnAction.ShowSuccess, action);
            Assert.True(OnboardingReturnPolicy.ShouldShowSuccess(state, true));
        }

        [Fact]
        public void Evaluate_ShowSuccess_RequiresVerifiedChatAnswer_NotOnlyFileOnDisk()
        {
            // A completed download puts the model file on disk, but until a chat turn produces
            // a verified answer, ShowSuccess is NOT returned; policy returns ReturnToSetup.
            OnboardingReturnAction fileOnDiskOnly = OnboardingReturnPolicy.Evaluate(
                OnboardingDownloadState.Completed,
                chatAnswerVerified: false);
            Assert.NotEqual(OnboardingReturnAction.ShowSuccess, fileOnDiskOnly);
            Assert.Equal(OnboardingReturnAction.ReturnToSetup, fileOnDiskOnly);

            OnboardingReturnAction answerVerified = OnboardingReturnPolicy.Evaluate(
                OnboardingDownloadState.Completed,
                chatAnswerVerified: true);
            Assert.Equal(OnboardingReturnAction.ShowSuccess, answerVerified);
        }

        [Theory]
        [InlineData("completed", false, OnboardingReturnAction.ReturnToSetup)]
        [InlineData("COMPLETED", false, OnboardingReturnAction.ReturnToSetup)]
        [InlineData("finished", false, OnboardingReturnAction.ReturnToSetup)]
        [InlineData("failed", false, OnboardingReturnAction.ShowRetry)]
        [InlineData("FAILED", false, OnboardingReturnAction.ShowRetry)]
        [InlineData("cancelled", false, OnboardingReturnAction.ShowRetry)]
        [InlineData("canceled", false, OnboardingReturnAction.ShowRetry)]
        [InlineData("downloading", false, OnboardingReturnAction.Stay)]
        [InlineData("queued", false, OnboardingReturnAction.Stay)]
        [InlineData("paused", false, OnboardingReturnAction.Stay)]
        [InlineData("completed", true, OnboardingReturnAction.ShowSuccess)]
        [InlineData("downloading", true, OnboardingReturnAction.ShowSuccess)]
        [InlineData(null, false, OnboardingReturnAction.Stay)]
        public void Evaluate_StringStatusOverload_MatchesExpectedActions(
            string? status,
            bool verified,
            OnboardingReturnAction expected)
        {
            OnboardingReturnAction actual = OnboardingReturnPolicy.Evaluate(status, verified);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData("completed", OnboardingDownloadState.Completed)]
        [InlineData("finished", OnboardingDownloadState.Completed)]
        [InlineData("failed", OnboardingDownloadState.Failed)]
        [InlineData("error", OnboardingDownloadState.Failed)]
        [InlineData("cancelled", OnboardingDownloadState.Cancelled)]
        [InlineData("canceled", OnboardingDownloadState.Cancelled)]
        [InlineData("downloading", OnboardingDownloadState.Downloading)]
        [InlineData("retrying", OnboardingDownloadState.Downloading)]
        [InlineData("paused", OnboardingDownloadState.Paused)]
        [InlineData("queued", OnboardingDownloadState.Queued)]
        [InlineData("unknown", OnboardingDownloadState.Queued)]
        [InlineData("", OnboardingDownloadState.Queued)]
        [InlineData(null, OnboardingDownloadState.Queued)]
        public void ParseDownloadState_MapsCorrectly(string? status, OnboardingDownloadState expected)
        {
            Assert.Equal(expected, OnboardingReturnPolicy.ParseDownloadState(status));
        }
    }
}
