using System;
using System.Globalization;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDownloadClientWiringPolicyTests
{
    [Fact]
    public void Active_retry_row_shows_speed_eta_failure_remediation_attempt_and_retry_time()
    {
        var status = new ModelDownloadStatus
        {
            Status = "retrying",
            SpeedBytesPerSecond = 1024 * 1024,
            EtaSeconds = 90,
            FailureClass = "transient",
            Error = "The source returned a temporary refusal.",
            Remediation = "PaRuX will retry automatically; check the network if it continues.",
            Attempt = 2,
            MaxAttempts = 4,
            NextRetryUtc = new DateTime(2026, 10, 20, 8, 30, 0, DateTimeKind.Utc)
        };

        string details = ModelDownloadPresentationPolicy.FormatDetails(status);

        Assert.Contains("Speed " + 1.0.ToString("0.0", CultureInfo.CurrentCulture) + " MiB/s", details);
        Assert.Contains("ETA 1m 30s", details);
        Assert.Contains("Failure: temporary", details);
        Assert.Contains("Attempt 2/4", details);
        Assert.Contains("Next retry: 2026-10-20 08:30:00 UTC", details);
        Assert.Contains(status.Remediation, details);
    }

    [Fact]
    public void Active_row_explains_when_speed_and_eta_are_not_yet_available()
    {
        string details = ModelDownloadPresentationPolicy.FormatDetails(new ModelDownloadStatus { Status = "downloading" });

        Assert.Contains("Speed unavailable", details);
        Assert.Contains("ETA unavailable", details);
    }

    [Theory]
    [InlineData("paused", null, "paused / resumable")]
    [InlineData("paused", "Paused for Gaming", "paused for Gaming / resumable")]
    [InlineData("cancelled", null, "cancelled / resumable")]
    [InlineData("retrying", null, "retrying")]
    public void Download_states_keep_pause_cancel_and_retry_distinct(string state, string? error, string expected)
    {
        Assert.Equal(expected, ModelDownloadPresentationPolicy.DescribeStatus(new ModelDownloadStatus
        {
            Status = state,
            Error = error
        }));
    }

    [Theory]
    [InlineData("auth", "Failure: authentication required")]
    [InlineData("disk", "Failure: disk space")]
    [InlineData("permanent", "Failure: permanent")]
    [InlineData("cancelled", "Failure: cancelled")]
    public void Failure_class_is_rendered_as_plain_user_text(string failureClass, string expected)
    {
        string details = ModelDownloadPresentationPolicy.FormatDetails(new ModelDownloadStatus
        {
            Status = "failed",
            FailureClass = failureClass,
            Remediation = "Take the next step."
        });

        Assert.Contains(expected, details);
        Assert.Contains("Take the next step.", details);
    }

    [Fact]
    public void Dynamic_action_ids_are_stable_and_strip_non_identifier_characters()
    {
        string first = ModelDownloadPresentationPolicy.GetActionAutomationId("Model-Hub", "job:ab-12", "Pause");
        string second = ModelDownloadPresentationPolicy.GetActionAutomationId("Model-Hub", "job:ab-12", "Pause");

        Assert.Equal("ModelHub_Download_jobab12_Pause_Button", first);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1.5", 1.5)]
    [InlineData("10000", 10000)]
    public void Bandwidth_limit_accepts_zero_and_values_in_the_supported_range(string input, double expected)
    {
        Assert.True(ModelDownloadSettingsPolicy.TryParseBandwidthLimit(input, out double actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fast")]
    [InlineData("-1")]
    [InlineData("10000.01")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Bandwidth_limit_rejects_invalid_or_out_of_range_values(string input)
    {
        Assert.False(ModelDownloadSettingsPolicy.TryParseBandwidthLimit(input, out _));
    }

    [Fact]
    public void Bandwidth_label_reads_unlimited_for_zero_and_the_rate_otherwise()
    {
        Assert.Equal("Unlimited", ModelDownloadSettingsPolicy.DescribeBandwidthLimit(0));
        Assert.Equal("2 MB/s", ModelDownloadSettingsPolicy.DescribeBandwidthLimit(2));
    }

    [Fact]
    public void Credential_status_never_contains_or_requests_the_saved_token_value()
    {
        string status = ModelDownloadSettingsPolicy.DescribeCredentialStatus(new ModelDownloadCredentialStatus
        {
            TokenConfigured = true,
            Protection = "DPAPI"
        });

        Assert.Equal("A token is saved; its value is never displayed here. Protected with DPAPI.", status);
        Assert.DoesNotContain("hf_", status, StringComparison.OrdinalIgnoreCase);
    }
}
