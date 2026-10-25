using System;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class SpeechTimingPresenterTests
    {
        [Fact]
        public void AutomationIds_MatchContract()
        {
            Assert.Equal("Create_TTS_Status_Text", SpeechTimingPresenter.StatusTextAutomationId);
            Assert.Equal("Create_Cancel_Active_Job_Button", SpeechTimingPresenter.CancelActiveJobButtonAutomationId);
            Assert.Equal("Create_TTS_Run_Button", SpeechTimingPresenter.RunButtonAutomationId);
            Assert.Equal("Create_TTS_Text_Box", SpeechTimingPresenter.TextBoxAutomationId);
            Assert.Equal("Create_TTS_Voice_Box", SpeechTimingPresenter.VoiceBoxAutomationId);
        }

        [Theory]
        [InlineData(1, "First audio in 1 ms")]
        [InlineData(42, "First audio in 42 ms")]
        [InlineData(185, "First audio in 185 ms")]
        [InlineData(1200, "First audio in 1200 ms")]
        public void FormatTtfa_WhenPositive_ReturnsFormattedMilliseconds(long ttfa, string expected)
        {
            Assert.Equal(expected, SpeechTimingPresenter.FormatTtfa(ttfa));
            Assert.Equal(expected, SpeechTimingPresenter.FormatTtfa((long?)ttfa));

            SpeechTimingPresentation presentation = SpeechTimingPresenter.PresentCompleted(ttfa);
            Assert.Equal(SpeechTimingState.Completed, presentation.State);
            Assert.Equal(expected, presentation.StatusText);
            Assert.Equal(ttfa, presentation.TtfaMilliseconds);

            SpeechTimingPresentation presentFromEnum = SpeechTimingPresenter.Present(SpeechTimingState.Completed, ttfa);
            Assert.Equal(SpeechTimingState.Completed, presentFromEnum.State);
            Assert.Equal(expected, presentFromEnum.StatusText);
            Assert.Equal(ttfa, presentFromEnum.TtfaMilliseconds);
        }

        [Fact]
        public void FormatTtfa_WhenZero_ReturnsShortNoteAndNeverFabricatesValue()
        {
            string formatted = SpeechTimingPresenter.FormatTtfa(0);

            Assert.Equal("First audio timing unavailable", formatted);
            Assert.DoesNotContain("0 ms", formatted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("First audio in 0 ms", formatted, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(formatted));

            SpeechTimingPresentation presentation = SpeechTimingPresenter.PresentCompleted(0);
            Assert.Equal(SpeechTimingState.Completed, presentation.State);
            Assert.Equal("First audio timing unavailable", presentation.StatusText);
            Assert.Equal(0, presentation.TtfaMilliseconds);
        }

        [Fact]
        public void FormatTtfa_WhenMissing_ReturnsShortNoteAndNeverFabricatesValue()
        {
            string formattedNull = SpeechTimingPresenter.FormatTtfa(null);
            Assert.Equal("First audio timing unavailable", formattedNull);
            Assert.DoesNotContain("ms", formattedNull, StringComparison.OrdinalIgnoreCase);

            string formattedNegative = SpeechTimingPresenter.FormatTtfa(-1);
            Assert.Equal("First audio timing unavailable", formattedNegative);

            SpeechTimingPresentation presentation = SpeechTimingPresenter.PresentCompleted(null);
            Assert.Equal(SpeechTimingState.Completed, presentation.State);
            Assert.Equal("First audio timing unavailable", presentation.StatusText);
            Assert.Null(presentation.TtfaMilliseconds);

            SpeechTimingPresentation presentationFromEnum = SpeechTimingPresenter.Present(SpeechTimingState.Completed, null);
            Assert.Equal(SpeechTimingState.Completed, presentationFromEnum.State);
            Assert.Equal("First audio timing unavailable", presentationFromEnum.StatusText);
            Assert.Null(presentationFromEnum.TtfaMilliseconds);
        }

        [Fact]
        public void FormatGenerating_WhileJobRuns_ShowsVisibleStopState()
        {
            Assert.Equal("Generating speech... Stop", SpeechTimingPresenter.GeneratingStatus);
            Assert.Equal("Generating speech... Stop", SpeechTimingPresenter.FormatGenerating());
            Assert.Equal("Generating speech... Stop", SpeechTimingPresenter.FormatRunning());

            SpeechTimingPresentation presentation = SpeechTimingPresenter.PresentGenerating();
            Assert.Equal(SpeechTimingState.Generating, presentation.State);
            Assert.Equal("Generating speech... Stop", presentation.StatusText);

            SpeechTimingPresentation presentationFromEnum = SpeechTimingPresenter.Present(SpeechTimingState.Generating);
            Assert.Equal(SpeechTimingState.Generating, presentationFromEnum.State);
            Assert.Equal("Generating speech... Stop", presentationFromEnum.StatusText);
        }

        [Fact]
        public void FormatCancelled_AfterCancellation_StatesSpeechStoppedAndNoFileKept()
        {
            string status = SpeechTimingPresenter.FormatCancelled();

            Assert.Equal("Speech was stopped; no file was kept.", status);
            Assert.Contains("stopped", status, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no file was kept", status, StringComparison.OrdinalIgnoreCase);

            SpeechTimingPresentation presentation = SpeechTimingPresenter.PresentCancelled();
            Assert.Equal(SpeechTimingState.Cancelled, presentation.State);
            Assert.Equal("Speech was stopped; no file was kept.", presentation.StatusText);

            SpeechTimingPresentation presentationFromEnum = SpeechTimingPresenter.Present(SpeechTimingState.Cancelled);
            Assert.Equal(SpeechTimingState.Cancelled, presentationFromEnum.State);
            Assert.Equal("Speech was stopped; no file was kept.", presentationFromEnum.StatusText);
        }

        [Fact]
        public void FormatFailed_WithOrWithoutError_FormatsFailureStatus()
        {
            Assert.Equal("Speech generation failed.", SpeechTimingPresenter.FormatFailed(null));
            Assert.Equal("Speech generation failed.", SpeechTimingPresenter.FormatFailed(string.Empty));
            Assert.Equal("Speech generation failed.", SpeechTimingPresenter.FormatFailed("   "));

            string errorDetail = "Kokoro resident worker exited unexpectedly";
            string formatted = SpeechTimingPresenter.FormatFailed(errorDetail);
            Assert.Equal("Speech generation failed: Kokoro resident worker exited unexpectedly", formatted);
            Assert.Contains("failed", formatted, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(errorDetail, formatted);

            SpeechTimingPresentation presentation = SpeechTimingPresenter.PresentFailed(errorDetail);
            Assert.Equal(SpeechTimingState.Failed, presentation.State);
            Assert.Equal(formatted, presentation.StatusText);
            Assert.Equal(errorDetail, presentation.ErrorMessage);

            SpeechTimingPresentation presentationFromEnum = SpeechTimingPresenter.Present(SpeechTimingState.Failed, error: errorDetail);
            Assert.Equal(SpeechTimingState.Failed, presentationFromEnum.State);
            Assert.Equal(formatted, presentationFromEnum.StatusText);
            Assert.Equal(errorDetail, presentationFromEnum.ErrorMessage);
        }

        [Fact]
        public void FormatStatus_MatchesPresentOutputAcrossAllStates()
        {
            Assert.Equal(SpeechTimingPresenter.GeneratingStatus, SpeechTimingPresenter.FormatStatus(SpeechTimingState.Generating));
            Assert.Equal("First audio in 350 ms", SpeechTimingPresenter.FormatStatus(SpeechTimingState.Completed, 350));
            Assert.Equal("First audio timing unavailable", SpeechTimingPresenter.FormatStatus(SpeechTimingState.Completed, 0));
            Assert.Equal("First audio timing unavailable", SpeechTimingPresenter.FormatStatus(SpeechTimingState.Completed, null));
            Assert.Equal(SpeechTimingPresenter.CancelledStatus, SpeechTimingPresenter.FormatStatus(SpeechTimingState.Cancelled));
            Assert.Equal("Speech generation failed: Device error", SpeechTimingPresenter.FormatStatus(SpeechTimingState.Failed, error: "Device error"));
            Assert.Equal(string.Empty, SpeechTimingPresenter.FormatStatus(SpeechTimingState.Idle));
        }
    }
}
