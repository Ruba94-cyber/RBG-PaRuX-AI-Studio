using System;
using System.Globalization;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Presentation data for an installation or download progress state.
    /// Supports positional deconstruction: var (fraction, label, eta) = presentation.
    /// </summary>
    public sealed record InstallProgressPresentation(double? Fraction, string Label, string? EtaLabel)
    {
        /// <summary>True when progress is indeterminate (TotalBytes unknown or zero).</summary>
        public bool IsIndeterminate => !Fraction.HasValue;

        /// <summary>Progress percentage from 0.0 to 100.0, or 0.0 when indeterminate.</summary>
        public double Percentage => (Fraction ?? 0.0) * 100.0;

        /// <summary>Full combined status line with ETA if present.</summary>
        public string FullText => string.IsNullOrEmpty(EtaLabel) ? Label : Label + " · " + EtaLabel;

        public static implicit operator (double? Fraction, string Label, string? EtaLabel)(InstallProgressPresentation p) =>
            p == null ? (null, string.Empty, null) : (p.Fraction, p.Label, p.EtaLabel);

        public override string ToString() => FullText;
    }

    /// <summary>
    /// Pure presenter mapping installation and download progress to determinate/indeterminate fraction,
    /// human-readable status labels, and measured ETA labels. Guarantees no internal enum identifiers
    /// or hyphenated tokens leak to the user interface.
    /// </summary>
    public static class InstallProgressPresenter
    {
        /// <summary>
        /// Maps a runtime install status into presentation values.
        /// </summary>
        public static InstallProgressPresentation Present(
            RuntimeInstallStatus? status,
            string? prefix = null,
            double? speedBytesPerSecond = null)
        {
            if (status == null)
                return new InstallProgressPresentation(null, prefix ?? string.Empty, null);

            double? fraction = CalculateFraction(status.BytesDownloaded, status.TotalBytes);
            string? eta = CalculateEta(status.BytesDownloaded, status.TotalBytes, speedBytesPerSecond);
            string stageName = GetStageName(status.Stage);
            string label = BuildLabel(stageName, status.BytesDownloaded, status.TotalBytes, prefix, status.Error);

            return new InstallProgressPresentation(fraction, label, eta);
        }

        /// <summary>
        /// Maps a model download status into presentation values.
        /// </summary>
        public static InstallProgressPresentation Present(
            ModelDownloadStatus? status,
            string? prefix = null)
        {
            if (status == null)
                return new InstallProgressPresentation(null, prefix ?? string.Empty, null);

            string? displayPrefix = prefix ?? (string.IsNullOrEmpty(status.FileName) ? null : status.FileName);
            double? fraction = CalculateFraction(status.BytesDownloaded, status.TotalBytes);
            string? eta = CalculateEta(status.BytesDownloaded, status.TotalBytes, status.SpeedBytesPerSecond, status.EtaSeconds);
            string stageName = GetStageName(status.Status);
            string label = BuildLabel(stageName, status.BytesDownloaded, status.TotalBytes, displayPrefix, status.Error);

            return new InstallProgressPresentation(fraction, label, eta);
        }

        /// <summary>
        /// Maps arbitrary download/install byte counts and stage to presentation values.
        /// </summary>
        public static InstallProgressPresentation Present(
            long bytesDownloaded,
            long totalBytes,
            string? stage = null,
            double? speedBytesPerSecond = null,
            string? prefix = null,
            double? etaSeconds = null,
            string? error = null)
        {
            double? fraction = CalculateFraction(bytesDownloaded, totalBytes);
            string? eta = CalculateEta(bytesDownloaded, totalBytes, speedBytesPerSecond, etaSeconds);
            string stageName = GetStageName(stage);
            string label = BuildLabel(stageName, bytesDownloaded, totalBytes, prefix, error);

            return new InstallProgressPresentation(fraction, label, eta);
        }

        /// <summary>
        /// Alias for Present to satisfy functional mapping callers.
        /// </summary>
        public static InstallProgressPresentation Map(
            RuntimeInstallStatus? status,
            string? prefix = null,
            double? speedBytesPerSecond = null) =>
            Present(status, prefix, speedBytesPerSecond);

        /// <summary>
        /// Alias for Present to satisfy functional mapping callers.
        /// </summary>
        public static InstallProgressPresentation Map(
            ModelDownloadStatus? status,
            string? prefix = null) =>
            Present(status, prefix);

        /// <summary>
        /// Alias for Present to satisfy functional mapping callers.
        /// </summary>
        public static InstallProgressPresentation Map(
            long bytesDownloaded,
            long totalBytes,
            string? stage = null,
            double? speedBytesPerSecond = null,
            string? prefix = null,
            double? etaSeconds = null,
            string? error = null) =>
            Present(bytesDownloaded, totalBytes, stage, speedBytesPerSecond, prefix, etaSeconds, error);

        /// <summary>
        /// Calculates progress fraction clamped to [0.0, 1.0], or null when TotalBytes is zero or unknown.
        /// </summary>
        public static double? CalculateFraction(long bytesDownloaded, long totalBytes)
        {
            if (totalBytes <= 0) return null;

            double raw = (double)bytesDownloaded / totalBytes;
            if (!double.IsFinite(raw)) return 0.0;
            return Math.Clamp(raw, 0.0, 1.0);
        }

        /// <summary>
        /// Estimates and formats ETA only when speed is measured (> 0).
        /// Returns null when speed is not measured, or when total size is unknown and no explicit ETA was given.
        /// </summary>
        public static string? CalculateEta(
            long bytesDownloaded,
            long totalBytes,
            double? speedBytesPerSecond,
            double? explicitEtaSeconds = null)
        {
            if (!speedBytesPerSecond.HasValue || !double.IsFinite(speedBytesPerSecond.Value) || speedBytesPerSecond.Value <= 0)
                return null;

            if (explicitEtaSeconds.HasValue && double.IsFinite(explicitEtaSeconds.Value) && explicitEtaSeconds.Value >= 0)
                return FormatEta(explicitEtaSeconds.Value);

            if (totalBytes <= 0) return null;

            long remaining = Math.Max(0, totalBytes - bytesDownloaded);
            if (remaining == 0) return null;

            double seconds = remaining / speedBytesPerSecond.Value;
            return FormatEta(seconds);
        }

        /// <summary>
        /// Maps internal stage identifiers to human-readable plain language.
        /// Replaces internal engine terms (e.g. "swapping" -> "Activating", "performance-check" -> "Checking performance").
        /// </summary>
        public static string GetStageName(string? stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return string.Empty;

            string normalized = stage.Trim().ToLowerInvariant().Replace('_', '-');
            return normalized switch
            {
                "queued" => "Queued",
                "downloading" => "Downloading",
                "verifying" => "Verifying",
                "extracting" => "Extracting",
                "swapping" => "Activating",
                "performance-check" or "performancecheck" => "Checking performance",
                "interrupted" => "Interrupted",
                "completed" => "Completed",
                "failed" => "Failed",
                "cancelled" or "canceled" => "Cancelled",
                "retrying" => "Retrying",
                "paused" => "Paused",
                "installing" => "Installing",
                "active" => "Active",
                "idle" => "Idle",
                _ => FormatFallbackStageName(stage)
            };
        }

        public static string DescribeStage(string? stage) => GetStageName(stage);
        public static string FormatStage(string? stage) => GetStageName(stage);

        /// <summary>
        /// Maps ComponentOwnership enum to human-friendly display text, preventing internal uppercase enum leakage.
        /// </summary>
        public static string DescribeOwnership(ComponentOwnership ownership) =>
            ownership switch
            {
                ComponentOwnership.PARUX_MANAGED => "PaRuX-managed",
                ComponentOwnership.USER_MANAGED => "User-managed",
                ComponentOwnership.SYSTEM_DETECTED => "System-detected",
                _ => "Unknown"
            };

        /// <summary>
        /// Maps string ownership tokens to human-friendly display text.
        /// </summary>
        public static string DescribeOwnership(string? ownership)
        {
            if (string.IsNullOrWhiteSpace(ownership)) return "Unknown";
            string normalized = ownership.Trim().ToUpperInvariant();
            return normalized switch
            {
                "PARUX_MANAGED" or "PARUXMANAGED" => "PaRuX-managed",
                "USER_MANAGED" or "USERMANAGED" => "User-managed",
                "SYSTEM_DETECTED" or "SYSTEMDETECTED" => "System-detected",
                _ => FormatFallbackStageName(ownership)
            };
        }

        /// <summary>
        /// Maps health tokens to sentence case without raw internal codes.
        /// </summary>
        public static string DescribeHealth(string? health)
        {
            if (string.IsNullOrWhiteSpace(health)) return "Unknown";
            return health.Trim().ToLowerInvariant() switch
            {
                "healthy" => "Healthy",
                "installed" => "Installed",
                "detected" => "Detected",
                "missing" => "Missing",
                "unknown" => "Unknown",
                _ => FormatFallbackStageName(health)
            };
        }

        /// <summary>
        /// Maps validation status tokens to sentence case without raw internal codes.
        /// </summary>
        public static string DescribeValidation(string? validation)
        {
            if (string.IsNullOrWhiteSpace(validation)) return "Unverified";
            return validation.Trim().ToLowerInvariant() switch
            {
                "validated" => "Validated",
                "unverified" => "Unverified",
                "unhealthy" => "Unhealthy",
                "missing" => "Missing",
                _ => FormatFallbackStageName(validation)
            };
        }

        /// <summary>
        /// Formats byte counts consistently across views (B, KB, MB, GB, TB).
        /// </summary>
        public static string FormatBytes(long value)
        {
            double bytes = Math.Max(0, value);
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1)
            {
                bytes /= 1024;
                unit++;
            }
            return bytes.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        /// <summary>
        /// Formats measured transfer speed in bytes per second. Returns null when speed is unmeasured or invalid.
        /// </summary>
        public static string? FormatSpeed(double? bytesPerSecond)
        {
            if (!bytesPerSecond.HasValue || !double.IsFinite(bytesPerSecond.Value) || bytesPerSecond.Value <= 0)
                return null;

            double size = bytesPerSecond.Value;
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return size.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        /// <summary>
        /// Formats duration in seconds into an ETA label. Returns null when seconds is unmeasured, negative, or invalid.
        /// </summary>
        public static string? FormatEta(double? seconds)
        {
            if (!seconds.HasValue || !double.IsFinite(seconds.Value) || seconds.Value < 0
                || seconds.Value > TimeSpan.MaxValue.TotalSeconds)
                return null;

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

        private static string BuildLabel(string stageName, long bytesDownloaded, long totalBytes, string? prefix, string? error)
        {
            string bytesPart;
            if (totalBytes > 0)
            {
                bytesPart = FormatBytes(bytesDownloaded) + " / " + FormatBytes(totalBytes);
            }
            else if (bytesDownloaded > 0)
            {
                bytesPart = FormatBytes(bytesDownloaded) + " downloaded";
            }
            else
            {
                bytesPart = string.Empty;
            }

            string content;
            if (!string.IsNullOrWhiteSpace(error) && (stageName is "Failed" or "Interrupted"))
            {
                content = stageName + ": " + error.Trim();
            }
            else if (!string.IsNullOrEmpty(stageName) && !string.IsNullOrEmpty(bytesPart))
            {
                content = stageName + " · " + bytesPart;
            }
            else if (!string.IsNullOrEmpty(stageName))
            {
                content = stageName;
            }
            else if (!string.IsNullOrEmpty(bytesPart))
            {
                content = bytesPart;
            }
            else
            {
                content = "Ready";
            }

            if (!string.IsNullOrWhiteSpace(prefix))
            {
                string cleanPrefix = prefix.Trim().TrimEnd(':');
                return cleanPrefix + ": " + content;
            }

            return content;
        }

        private static string FormatFallbackStageName(string raw)
        {
            string clean = raw.Trim().Replace('_', ' ').Replace('-', ' ');
            if (clean.Length == 0) return string.Empty;

            // Sentence case fallback to avoid raw ALL_CAPS enum tokens
            string lower = clean.ToLowerInvariant();
            return char.ToUpperInvariant(lower[0]) + lower[1..];
        }
    }
}
