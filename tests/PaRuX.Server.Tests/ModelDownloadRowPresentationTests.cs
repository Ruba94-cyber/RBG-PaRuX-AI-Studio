using System;
using System.Globalization;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDownloadRowPresentationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1024.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Unknown_speed_shows_dash_and_never_zero(double? bytesPerSecond)
    {
        string labelSpeed = ModelDownloadRowPresentation.FormatSpeed(bytesPerSecond);
        string bareSpeed = ModelDownloadRowPresentation.FormatSpeedValue(bytesPerSecond);

        Assert.Contains("-", labelSpeed);
        Assert.DoesNotContain("0", labelSpeed);

        Assert.Equal("-", bareSpeed);
        Assert.DoesNotContain("0", bareSpeed);
    }

    [Fact]
    public void Known_speed_formats_with_binary_units()
    {
        string formatted = ModelDownloadRowPresentation.FormatSpeed(1024 * 1024);
        string expectedNumber = 1.0.ToString("0.0", CultureInfo.CurrentCulture);

        Assert.Contains("Speed " + expectedNumber + " MiB/s", formatted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-15.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Unknown_eta_shows_dash_and_never_zero(double? seconds)
    {
        string labelEta = ModelDownloadRowPresentation.FormatEta(seconds);
        string bareEta = ModelDownloadRowPresentation.FormatEtaValue(seconds);

        Assert.Contains("-", labelEta);
        Assert.DoesNotContain("0", labelEta);

        Assert.Equal("-", bareEta);
        Assert.DoesNotContain("0", bareEta);
    }

    [Theory]
    [InlineData(45.0, "ETA 45s", "45s")]
    [InlineData(90.0, "ETA 1m 30s", "1m 30s")]
    [InlineData(3660.0, "ETA 1h 1m", "1h 1m")]
    [InlineData(90000.0, "ETA 1d 1h", "1d 1h")]
    public void Known_eta_formats_with_compact_time(double seconds, string expectedLabeled, string expectedBare)
    {
        Assert.Equal(expectedLabeled, ModelDownloadRowPresentation.FormatEta(seconds));
        Assert.Equal(expectedBare, ModelDownloadRowPresentation.FormatEtaValue(seconds));
    }

    [Fact]
    public void Active_download_with_unmeasured_speed_and_eta_shows_dash_never_zero_in_details()
    {
        var job = new ModelDownloadStatus
        {
            Status = "downloading",
            SpeedBytesPerSecond = 0,
            EtaSeconds = 0
        };

        string details = ModelDownloadRowPresentation.FormatDetails(job);

        Assert.Contains("Speed -", details);
        Assert.Contains("ETA -", details);
        Assert.DoesNotContain("0", details);
    }

    [Theory]
    [InlineData("permanent", "permanent")]
    [InlineData("transient", "temporary")]
    [InlineData("auth", "authentication required")]
    [InlineData("disk", "disk space")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("unrecognized_class", "download issue")]
    public void Every_failure_class_maps_to_plain_language_text(string failureClass, string expectedFragment)
    {
        string plainText = ModelDownloadRowPresentation.DescribeFailureClass(failureClass);
        Assert.Equal(expectedFragment, plainText);

        string formatted = ModelDownloadRowPresentation.FormatFailure(failureClass, "Verify connection and try again.");
        Assert.Contains("Failure: " + expectedFragment, formatted);
        Assert.Contains("Verify connection and try again.", formatted);
    }

    [Fact]
    public void Attempt_text_formats_as_attempt_x_of_y()
    {
        Assert.Equal("attempt 2 of 4", ModelDownloadRowPresentation.FormatAttempt(2, 4));
        Assert.Equal("attempt 1", ModelDownloadRowPresentation.FormatAttempt(1, 0));
        Assert.Equal(string.Empty, ModelDownloadRowPresentation.FormatAttempt(0, 4));
    }

    [Fact]
    public void Next_retry_formats_as_relative_time()
    {
        var referenceNow = new DateTime(2026, 10, 21, 10, 0, 0, DateTimeKind.Utc);

        string in15s = ModelDownloadRowPresentation.FormatNextRetry(referenceNow.AddSeconds(15), referenceNow);
        Assert.Equal("Next retry: in 15s", in15s);

        string in90s = ModelDownloadRowPresentation.FormatNextRetry(referenceNow.AddSeconds(90), referenceNow);
        Assert.Equal("Next retry: in 1m 30s", in90s);

        string past = ModelDownloadRowPresentation.FormatNextRetry(referenceNow.AddSeconds(-5), referenceNow);
        Assert.Equal("Next retry: now", past);
    }

    [Fact]
    public void Full_details_combines_metrics_attempts_retry_time_failure_and_remediation()
    {
        var referenceNow = new DateTime(2026, 10, 21, 10, 0, 0, DateTimeKind.Utc);
        var job = new ModelDownloadStatus
        {
            Status = "retrying",
            SpeedBytesPerSecond = 1024 * 1024,
            EtaSeconds = 90,
            FailureClass = "transient",
            Error = "Remote host refused connection.",
            Remediation = "Automatic retry scheduled; inspect network if issue persists.",
            Attempt = 2,
            MaxAttempts = 4,
            NextRetryUtc = referenceNow.AddSeconds(30)
        };

        string details = ModelDownloadRowPresentation.FormatDetails(job, referenceNow);

        string speedNumber = 1.0.ToString("0.0", CultureInfo.CurrentCulture);
        Assert.Contains("Speed " + speedNumber + " MiB/s", details);
        Assert.Contains("ETA 1m 30s", details);
        Assert.Contains("attempt 2 of 4", details);
        Assert.Contains("Next retry: in 30s", details);
        Assert.Contains("Failure: temporary", details);
        Assert.Contains("Remote host refused connection.", details);
        Assert.Contains("Automatic retry scheduled; inspect network if issue persists.", details);
    }

    [Theory]
    [InlineData("downloading", true)]
    [InlineData("queued", false)]
    [InlineData("retrying", false)]
    [InlineData("paused", false)]
    [InlineData("failed", false)]
    [InlineData("cancelled", false)]
    [InlineData("completed", false)]
    public void Action_availability_pause_only_while_downloading(string status, bool expectedCanPause)
    {
        Assert.Equal(expectedCanPause, ModelDownloadRowPresentation.CanPause(status));
        Assert.Equal(expectedCanPause, ModelDownloadRowPresentation.CanPause(new ModelDownloadStatus { Status = status }));
    }

    [Theory]
    [InlineData("paused", false, null, true)]
    [InlineData("failed", true, "permanent", true)]
    [InlineData("failed", false, "transient", true)]
    [InlineData("failed", false, "auth", true)]
    [InlineData("failed", false, "disk", true)]
    [InlineData("failed", false, "permanent", false)]
    [InlineData("failed", false, null, false)]
    [InlineData("downloading", true, "transient", false)]
    [InlineData("queued", true, "transient", false)]
    [InlineData("retrying", true, "transient", false)]
    [InlineData("cancelled", false, null, true)]
    [InlineData("completed", false, null, false)]
    public void Action_availability_resume_only_when_paused_or_retryable_failure(
        string status, bool retryRecommended, string? failureClass, bool expectedCanResume)
    {
        Assert.Equal(expectedCanResume, ModelDownloadRowPresentation.CanResume(status, retryRecommended, failureClass));

        var job = new ModelDownloadStatus
        {
            Status = status,
            RetryRecommended = retryRecommended,
            FailureClass = failureClass
        };
        Assert.Equal(expectedCanResume, ModelDownloadRowPresentation.CanResume(job));
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("queued")]
    [InlineData("retrying")]
    [InlineData("paused")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("completed")]
    public void Action_availability_remove_always_with_confirmation(string status)
    {
        Assert.True(ModelDownloadRowPresentation.CanRemove(status));
        Assert.True(ModelDownloadRowPresentation.CanRemove(new ModelDownloadStatus { Status = status }));
        Assert.True(ModelDownloadRowPresentation.RequiresRemoveConfirmation);
    }

    [Fact]
    public void Automation_ids_are_stable_and_preserve_existing_format()
    {
        string pauseHub = ModelDownloadRowPresentation.GetActionAutomationId("ModelHub", "job:ab-12", "Pause");
        string resumeHub = ModelDownloadRowPresentation.GetActionAutomationId("ModelHub", "job:ab-12", "Resume");
        string removeHub = ModelDownloadRowPresentation.GetActionAutomationId("ModelHub", "job:ab-12", "Remove");

        Assert.Equal("ModelHub_Download_jobab12_Pause_Button", pauseHub);
        Assert.Equal("ModelHub_Download_jobab12_Resume_Button", resumeHub);
        Assert.Equal("ModelHub_Download_jobab12_Remove_Button", removeHub);

        string onboardingPause = ModelDownloadRowPresentation.GetActionAutomationId("Onboarding", "job-1", "Pause");
        Assert.Equal("Onboarding_Download_job1_Pause_Button", onboardingPause);
    }
}
