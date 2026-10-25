using System;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public class InstallProgressPresenterTests
    {
        [Fact]
        public void TotalBytes_zero_gives_indeterminate_progress()
        {
            var status = new RuntimeInstallStatus
            {
                Stage = "downloading",
                BytesDownloaded = 0,
                TotalBytes = 0
            };

            InstallProgressPresentation presentation = InstallProgressPresenter.Present(status);

            Assert.Null(presentation.Fraction);
            Assert.True(presentation.IsIndeterminate);
            Assert.Equal(0.0, presentation.Percentage);
        }

        [Fact]
        public void TotalBytes_negative_gives_indeterminate_progress()
        {
            var presentation = InstallProgressPresenter.Present(
                bytesDownloaded: 1024,
                totalBytes: -1,
                stage: "downloading");

            Assert.Null(presentation.Fraction);
            Assert.True(presentation.IsIndeterminate);
            Assert.Equal("Downloading · 1.0 KB downloaded", presentation.Label);
        }

        [Fact]
        public void TotalBytes_positive_gives_determinate_progress()
        {
            var status = new RuntimeInstallStatus
            {
                Stage = "downloading",
                BytesDownloaded = 512,
                TotalBytes = 1024
            };

            var (fraction, label, eta) = InstallProgressPresenter.Present(status);

            Assert.NotNull(fraction);
            Assert.Equal(0.5, fraction.Value, 4);
            Assert.Null(eta);
            Assert.Contains("512 B / 1.0 KB", label);
        }

        [Theory]
        [InlineData(-500, 1000, 0.0)]
        [InlineData(0, 1000, 0.0)]
        [InlineData(250, 1000, 0.25)]
        [InlineData(1000, 1000, 1.0)]
        [InlineData(1500, 1000, 1.0)]
        public void Fraction_is_strictly_clamped_between_0_and_1(long downloaded, long total, double expected)
        {
            double? fraction = InstallProgressPresenter.CalculateFraction(downloaded, total);

            Assert.NotNull(fraction);
            Assert.Equal(expected, fraction.Value, 4);
        }

        [Fact]
        public void Fraction_handles_non_finite_safely()
        {
            double? fraction = InstallProgressPresenter.CalculateFraction(long.MaxValue, long.MaxValue);

            Assert.NotNull(fraction);
            Assert.InRange(fraction.Value, 0.0, 1.0);
        }

        [Fact]
        public void Eta_is_null_when_speed_is_unmeasured()
        {
            var status = new RuntimeInstallStatus
            {
                Stage = "downloading",
                BytesDownloaded = 500,
                TotalBytes = 1000
            };

            // speed null
            InstallProgressPresentation p1 = InstallProgressPresenter.Present(status, speedBytesPerSecond: null);
            Assert.Null(p1.EtaLabel);

            // speed zero
            InstallProgressPresentation p2 = InstallProgressPresenter.Present(status, speedBytesPerSecond: 0);
            Assert.Null(p2.EtaLabel);

            // speed negative
            InstallProgressPresentation p3 = InstallProgressPresenter.Present(status, speedBytesPerSecond: -100);
            Assert.Null(p3.EtaLabel);

            // speed NaN or infinity
            InstallProgressPresentation p4 = InstallProgressPresenter.Present(status, speedBytesPerSecond: double.NaN);
            Assert.Null(p4.EtaLabel);
            InstallProgressPresentation p5 = InstallProgressPresenter.Present(status, speedBytesPerSecond: double.PositiveInfinity);
            Assert.Null(p5.EtaLabel);
        }

        [Fact]
        public void Eta_is_null_when_total_bytes_is_unknown_even_if_speed_is_measured()
        {
            var status = new RuntimeInstallStatus
            {
                Stage = "downloading",
                BytesDownloaded = 500,
                TotalBytes = 0
            };

            InstallProgressPresentation presentation = InstallProgressPresenter.Present(status, speedBytesPerSecond: 1000);

            Assert.Null(presentation.EtaLabel);
            Assert.Null(presentation.Fraction);
        }

        [Fact]
        public void Eta_appears_when_speed_is_measured_and_total_is_known()
        {
            var status = new RuntimeInstallStatus
            {
                Stage = "downloading",
                BytesDownloaded = 500,
                TotalBytes = 1500
            };

            // 1000 bytes remaining / 500 B/s = 2 seconds
            InstallProgressPresentation presentation = InstallProgressPresenter.Present(status, speedBytesPerSecond: 500);

            Assert.NotNull(presentation.EtaLabel);
            Assert.Equal("ETA 2s", presentation.EtaLabel);
            Assert.Contains("ETA 2s", presentation.FullText);
        }

        [Theory]
        [InlineData(0.5, "ETA <1s")]
        [InlineData(45, "ETA 45s")]
        [InlineData(75, "ETA 1m 15s")]
        [InlineData(3665, "ETA 1h 1m")]
        [InlineData(90000, "ETA 1d 1h")]
        public void FormatEta_formats_duration_tiers_accurately(double seconds, string expected)
        {
            string? formatted = InstallProgressPresenter.FormatEta(seconds);

            Assert.Equal(expected, formatted);
        }

        [Theory]
        [InlineData("swapping", "Activating")]
        [InlineData("performance-check", "Checking performance")]
        [InlineData("performance_check", "Checking performance")]
        [InlineData("queued", "Queued")]
        [InlineData("downloading", "Downloading")]
        [InlineData("verifying", "Verifying")]
        [InlineData("extracting", "Extracting")]
        [InlineData("interrupted", "Interrupted")]
        [InlineData("completed", "Completed")]
        [InlineData("failed", "Failed")]
        [InlineData("cancelled", "Cancelled")]
        [InlineData("retrying", "Retrying")]
        [InlineData("paused", "Paused")]
        public void Stage_names_map_to_plain_language(string rawStage, string expected)
        {
            string stageName = InstallProgressPresenter.GetStageName(rawStage);

            Assert.Equal(expected, stageName);
        }

        [Fact]
        public void Internal_enum_names_never_reach_the_label()
        {
            // Verify swapping is not leaked
            var swapping = new RuntimeInstallStatus { Stage = "swapping" };
            var pSwapping = InstallProgressPresenter.Present(swapping, prefix: "llama.cpp");
            Assert.DoesNotContain("swapping", pSwapping.Label, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("llama.cpp: Activating", pSwapping.Label);

            // Verify performance-check is not leaked
            var perfCheck = new RuntimeInstallStatus { Stage = "performance-check" };
            var pPerf = InstallProgressPresenter.Present(perfCheck, prefix: "llama.cpp");
            Assert.DoesNotContain("performance-check", pPerf.Label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("performance_check", pPerf.Label, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("llama.cpp: Checking performance", pPerf.Label);

            // Verify uppercase ComponentOwnership enum values never leak
            string ownership1 = InstallProgressPresenter.DescribeOwnership(ComponentOwnership.PARUX_MANAGED);
            string ownership2 = InstallProgressPresenter.DescribeOwnership(ComponentOwnership.USER_MANAGED);
            string ownership3 = InstallProgressPresenter.DescribeOwnership(ComponentOwnership.SYSTEM_DETECTED);

            Assert.DoesNotContain("PARUX_MANAGED", ownership1);
            Assert.DoesNotContain("USER_MANAGED", ownership2);
            Assert.DoesNotContain("SYSTEM_DETECTED", ownership3);

            Assert.Equal("PaRuX-managed", ownership1);
            Assert.Equal("User-managed", ownership2);
            Assert.Equal("System-detected", ownership3);

            // String overload
            Assert.Equal("PaRuX-managed", InstallProgressPresenter.DescribeOwnership("PARUX_MANAGED"));
            Assert.Equal("User-managed", InstallProgressPresenter.DescribeOwnership("USER_MANAGED"));
            Assert.Equal("System-detected", InstallProgressPresenter.DescribeOwnership("SYSTEM_DETECTED"));
        }

        [Fact]
        public void ModelDownloadStatus_maps_with_speed_and_eta()
        {
            var download = new ModelDownloadStatus
            {
                FileName = "test-model-q4.gguf",
                Status = "downloading",
                BytesDownloaded = 1048576,
                TotalBytes = 2097152,
                SpeedBytesPerSecond = 524288,
                EtaSeconds = 2
            };

            InstallProgressPresentation presentation = InstallProgressPresenter.Present(download);

            Assert.NotNull(presentation.Fraction);
            Assert.Equal(0.5, presentation.Fraction.Value, 4);
            Assert.Equal("ETA 2s", presentation.EtaLabel);
            Assert.Contains("test-model-q4.gguf: Downloading · 1.0 MB / 2.0 MB", presentation.Label);
            Assert.Equal("test-model-q4.gguf: Downloading · 1.0 MB / 2.0 MB · ETA 2s", presentation.FullText);
        }

        [Fact]
        public void ModelDownloadStatus_without_speed_has_null_eta()
        {
            var download = new ModelDownloadStatus
            {
                FileName = "test-model-q4.gguf",
                Status = "downloading",
                BytesDownloaded = 1048576,
                TotalBytes = 2097152,
                SpeedBytesPerSecond = null,
                EtaSeconds = null
            };

            InstallProgressPresentation presentation = InstallProgressPresenter.Present(download);

            Assert.NotNull(presentation.Fraction);
            Assert.Null(presentation.EtaLabel);
            Assert.Equal("test-model-q4.gguf: Downloading · 1.0 MB / 2.0 MB", presentation.FullText);
        }

        [Fact]
        public void Status_with_error_and_failure_shows_error_in_label()
        {
            var status = new RuntimeInstallStatus
            {
                Stage = "failed",
                Error = "Verification signature mismatch",
                BytesDownloaded = 1000,
                TotalBytes = 2000
            };

            var presentation = InstallProgressPresenter.Present(status, prefix: "ComfyUI");

            Assert.Equal("ComfyUI: Failed: Verification signature mismatch", presentation.Label);
        }

        [Fact]
        public void Map_alias_matches_Present()
        {
            var status = new RuntimeInstallStatus
            {
                Stage = "downloading",
                BytesDownloaded = 512,
                TotalBytes = 1024
            };

            (double? fraction, string label, string? eta) = InstallProgressPresenter.Map(status, "llama.cpp", 256);

            Assert.NotNull(fraction);
            Assert.Equal(0.5, fraction.Value, 4);
            Assert.Equal("llama.cpp: Downloading · 512 B / 1.0 KB", label);
            Assert.Equal("ETA 2s", eta);
        }

        [Theory]
        [InlineData(0, "0 B")]
        [InlineData(512, "512 B")]
        [InlineData(1024, "1.0 KB")]
        [InlineData(1048576, "1.0 MB")]
        [InlineData(1073741824, "1.0 GB")]
        [InlineData(1048575, "1.0 MB")]
        [InlineData(1073741823, "1.0 GB")]
        [InlineData(1099511627775, "1.0 TB")]
        public void FormatBytes_formats_correctly(long bytes, string expected)
        {
            string formatted = InstallProgressPresenter.FormatBytes(bytes);

            Assert.Equal(expected, formatted);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData(0.0, null)]
        [InlineData(-100.0, null)]
        [InlineData(1024.0, "1.0 KB/s")]
        [InlineData(2097152.0, "2.0 MB/s")]
        public void FormatSpeed_formats_correctly(double? speed, string? expected)
        {
            string? formatted = InstallProgressPresenter.FormatSpeed(speed);

            Assert.Equal(expected, formatted);
        }
    }
}
