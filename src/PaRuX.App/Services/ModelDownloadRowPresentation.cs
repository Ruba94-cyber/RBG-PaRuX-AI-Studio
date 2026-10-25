using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure presentation and action policy for model download rows in Model Hub and onboarding.
    /// Ensures unknown speed or ETA shows a dash (never zero), every failure class maps to plain text
    /// plus remediation, retry attempts and next retry time are clearly stated, and action availability
    /// follows download state rules.
    /// </summary>
    public static class ModelDownloadRowPresentation
    {
        public static string DescribeStatus(ModelDownloadStatus job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            return DescribeStatus(job.Status, job.Error);
        }

        public static string DescribeStatus(string? status, string? error = null)
        {
            return (status ?? string.Empty) switch
            {
                "queued" => "queued",
                "downloading" => "downloading",
                "retrying" => "retrying",
                "paused" when error == "Paused for Gaming" => "paused for Gaming / resumable",
                "paused" => "paused / resumable",
                "cancelled" => "cancelled / resumable",
                "failed" => "failed",
                "completed" => "completed",
                _ => status ?? string.Empty
            };
        }

        public static string FormatSpeed(double? bytesPerSecond)
        {
            if (!bytesPerSecond.HasValue || !double.IsFinite(bytesPerSecond.Value) || bytesPerSecond.Value <= 0)
                return "Speed -";

            double size = bytesPerSecond.Value;
            string[] units = { "B/s", "KiB/s", "MiB/s", "GiB/s", "TiB/s" };
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return "Speed " + size.ToString("0.0", CultureInfo.CurrentCulture) + " " + units[unit];
        }

        public static string FormatSpeedValue(double? bytesPerSecond)
        {
            if (!bytesPerSecond.HasValue || !double.IsFinite(bytesPerSecond.Value) || bytesPerSecond.Value <= 0)
                return "-";

            double size = bytesPerSecond.Value;
            string[] units = { "B/s", "KiB/s", "MiB/s", "GiB/s", "TiB/s" };
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return size.ToString("0.0", CultureInfo.CurrentCulture) + " " + units[unit];
        }

        public static string FormatEta(double? seconds)
        {
            if (!seconds.HasValue || !double.IsFinite(seconds.Value) || seconds.Value <= 0
                || seconds.Value > TimeSpan.MaxValue.TotalSeconds)
                return "ETA -";

            if (seconds.Value < 1) return "ETA <1s";
            TimeSpan eta = TimeSpan.FromSeconds(seconds.Value);
            if (eta.TotalDays >= 1)
                return "ETA " + ((int)eta.TotalDays).ToString(CultureInfo.InvariantCulture) + "d " + eta.Hours.ToString(CultureInfo.InvariantCulture) + "h";
            if (eta.TotalHours >= 1)
                return "ETA " + ((int)eta.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " + eta.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
            if (eta.TotalMinutes >= 1)
                return "ETA " + ((int)eta.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m " + eta.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
            return "ETA " + Math.Max(1, eta.Seconds).ToString(CultureInfo.InvariantCulture) + "s";
        }

        public static string FormatEtaValue(double? seconds)
        {
            if (!seconds.HasValue || !double.IsFinite(seconds.Value) || seconds.Value <= 0
                || seconds.Value > TimeSpan.MaxValue.TotalSeconds)
                return "-";

            if (seconds.Value < 1) return "<1s";
            TimeSpan eta = TimeSpan.FromSeconds(seconds.Value);
            if (eta.TotalDays >= 1)
                return ((int)eta.TotalDays).ToString(CultureInfo.InvariantCulture) + "d " + eta.Hours.ToString(CultureInfo.InvariantCulture) + "h";
            if (eta.TotalHours >= 1)
                return ((int)eta.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " + eta.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
            if (eta.TotalMinutes >= 1)
                return ((int)eta.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m " + eta.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
            return Math.Max(1, eta.Seconds).ToString(CultureInfo.InvariantCulture) + "s";
        }

        public static string DescribeFailureClass(string? failureClass)
        {
            if (string.IsNullOrWhiteSpace(failureClass)) return string.Empty;
            return failureClass.Trim().ToLowerInvariant() switch
            {
                "permanent" => "permanent",
                "transient" => "temporary",
                "auth" => "authentication required",
                "disk" => "disk space",
                "cancelled" => "cancelled",
                _ => "download issue"
            };
        }

        public static string FormatFailure(string? failureClass, string? remediation)
        {
            string desc = DescribeFailureClass(failureClass);
            string rem = remediation?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(desc) && string.IsNullOrEmpty(rem)) return string.Empty;
            if (string.IsNullOrEmpty(desc)) return rem;
            if (string.IsNullOrEmpty(rem)) return "Failure: " + desc;
            return "Failure: " + desc + " · " + rem;
        }

        public static string FormatAttempt(int attempt, int maxAttempts)
        {
            if (attempt <= 0) return string.Empty;
            if (maxAttempts > 0)
                return "attempt " + attempt.ToString(CultureInfo.InvariantCulture) + " of " + maxAttempts.ToString(CultureInfo.InvariantCulture);
            return "attempt " + attempt.ToString(CultureInfo.InvariantCulture);
        }

        public static string FormatRelativeTime(TimeSpan delta)
        {
            if (delta <= TimeSpan.Zero) return "now";
            if (delta.TotalSeconds < 1) return "in <1s";
            if (delta.TotalMinutes < 1)
            {
                int sec = Math.Max(1, (int)Math.Round(delta.TotalSeconds));
                return "in " + sec.ToString(CultureInfo.InvariantCulture) + "s";
            }
            if (delta.TotalHours < 1)
            {
                int min = (int)delta.TotalMinutes;
                int sec = delta.Seconds;
                return sec > 0
                    ? "in " + min.ToString(CultureInfo.InvariantCulture) + "m " + sec.ToString(CultureInfo.InvariantCulture) + "s"
                    : "in " + min.ToString(CultureInfo.InvariantCulture) + "m";
            }
            if (delta.TotalDays < 1)
            {
                int hr = (int)delta.TotalHours;
                int min = delta.Minutes;
                return min > 0
                    ? "in " + hr.ToString(CultureInfo.InvariantCulture) + "h " + min.ToString(CultureInfo.InvariantCulture) + "m"
                    : "in " + hr.ToString(CultureInfo.InvariantCulture) + "h";
            }
            int days = (int)delta.TotalDays;
            int hours = delta.Hours;
            return hours > 0
                ? "in " + days.ToString(CultureInfo.InvariantCulture) + "d " + hours.ToString(CultureInfo.InvariantCulture) + "h"
                : "in " + days.ToString(CultureInfo.InvariantCulture) + "d";
        }

        public static string FormatNextRetry(DateTime nextRetryUtc, DateTime? nowUtc = null)
        {
            DateTime now = nowUtc ?? DateTime.UtcNow;
            DateTime targetUtc = nextRetryUtc.Kind switch
            {
                DateTimeKind.Local => nextRetryUtc.ToUniversalTime(),
                DateTimeKind.Unspecified => DateTime.SpecifyKind(nextRetryUtc, DateTimeKind.Utc),
                _ => nextRetryUtc
            };
            TimeSpan delta = targetUtc - now;
            return "Next retry: " + FormatRelativeTime(delta);
        }

        public static string FormatDetails(ModelDownloadStatus job, DateTime? nowUtc = null)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            var details = new List<string>();

            if (IsActive(job.Status))
            {
                details.Add(FormatSpeed(job.SpeedBytesPerSecond));
                details.Add(FormatEta(job.EtaSeconds));
            }
            else if (job.SpeedBytesPerSecond.HasValue || job.EtaSeconds.HasValue)
            {
                if (job.SpeedBytesPerSecond.HasValue) details.Add(FormatSpeed(job.SpeedBytesPerSecond));
                if (job.EtaSeconds.HasValue) details.Add(FormatEta(job.EtaSeconds));
            }

            if (job.Attempt > 0)
                details.Add(FormatAttempt(job.Attempt, job.MaxAttempts));

            if (job.NextRetryUtc.HasValue)
                details.Add(FormatNextRetry(job.NextRetryUtc.Value, nowUtc));

            if (!string.IsNullOrWhiteSpace(job.FailureClass))
                details.Add("Failure: " + DescribeFailureClass(job.FailureClass));

            if (!string.IsNullOrWhiteSpace(job.Error))
                details.Add(job.Error.Trim());

            if (!string.IsNullOrWhiteSpace(job.Remediation))
                details.Add(job.Remediation.Trim());

            return string.Join(" · ", details);
        }

        public static bool CanPause(string? status) => status == "downloading";

        public static bool CanPause(ModelDownloadStatus? job) => job != null && CanPause(job.Status);

        public static bool CanResume(string? status, bool retryRecommended = false, string? failureClass = null)
        {
            // Core (ModelDownloadService.Resume) accepts paused, cancelled and failed jobs; cancelled is resumable.
            if (status is "paused" or "cancelled") return true;
            if (status == "failed")
                return retryRecommended || IsRetryableFailure(failureClass);
            return false;
        }

        public static bool CanResume(ModelDownloadStatus? job) =>
            job != null && CanResume(job.Status, job.RetryRecommended, job.FailureClass);

        public static bool IsRetryableFailure(string? failureClass)
        {
            if (string.IsNullOrWhiteSpace(failureClass)) return false;
            string normalized = failureClass.Trim().ToLowerInvariant();
            if (normalized == "permanent") return false;
            return normalized is "transient" or "auth" or "disk";
        }

        public static bool CanRemove(string? status = null, string? id = null) => true;

        public static bool CanRemove(ModelDownloadStatus? job) => true;

        public static bool RequiresRemoveConfirmation => true;

        public static bool IsActive(string? status) => status is "queued" or "downloading" or "retrying";

        public static string GetActionAutomationId(string surface, string downloadId, string action)
        {
            string safeSurface = Sanitize(surface);
            string safeId = Sanitize(downloadId);
            string safeAction = Sanitize(action);
            if (safeSurface.Length == 0 || safeId.Length == 0 || safeAction.Length == 0)
                throw new ArgumentException("A surface, download id and action are required.");
            return safeSurface + "_Download_" + safeId + "_" + safeAction + "_Button";
        }

        private static string Sanitize(string value) => string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit));
    }
}
