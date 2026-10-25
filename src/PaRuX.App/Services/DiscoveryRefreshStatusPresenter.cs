using System;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record DiscoveryRefreshStatusPresentation(
        string StatusText,
        string? TooltipText = null);

    /// <summary>
    /// Pure presenter for Model Hub candidate discovery refresh status (BIC-16).
    /// Maps last check time, next scheduled time, offline state and gaming pauses
    /// to a single visible status line and optional tooltip text.
    /// </summary>
    public static class DiscoveryRefreshStatusPresenter
    {
        public const string StatusTextAutomationId = "ModelHub_DiscoveryRefresh_Status_Text";
        public const string DefaultPausedReason = "Paused while Gaming is active - Return to AI to resume checks";
        public const string CatalogUnavailableText = "Candidate catalog unavailable";

        public static DiscoveryRefreshStatusPresentation Present(
            DateTime? lastSuccessUtc,
            DateTime? nextRefreshUtc,
            string? pausedReason = null,
            bool isOffline = false,
            Func<DateTime>? clock = null)
        {
            DateTime now = (clock ?? (() => DateTime.UtcNow))();
            if (now.Kind == DateTimeKind.Unspecified)
                now = DateTime.SpecifyKind(now, DateTimeKind.Utc);
            else if (now.Kind == DateTimeKind.Local)
                now = now.ToUniversalTime();

            // 1. Paused state (e.g. Gaming Mode active)
            if (!string.IsNullOrWhiteSpace(pausedReason))
            {
                string text = string.Equals(pausedReason.Trim(), "gaming_active", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pausedReason.Trim(), "GamingActive", StringComparison.OrdinalIgnoreCase)
                    ? DefaultPausedReason
                    : pausedReason.Trim();

                return new DiscoveryRefreshStatusPresentation(
                    text,
                    "Discovery is paused while Gaming Mode is active. Return to AI mode to resume automatic checks.");
            }

            // 2. Offline state (catalog flag indicates refresh failure)
            // Acceptance: states "Offline" ONLY when the catalog flag says the last refresh failed AND a last trusted list exists.
            if (isOffline)
            {
                if (lastSuccessUtc.HasValue)
                {
                    string relativeTime = FormatRelativePast(lastSuccessUtc.Value, now);
                    string status = "Offline: showing the last trusted list from " + relativeTime;
                    string tooltip = "Offline: discovery refresh failed. Showing last trusted catalog from "
                        + ToUtcString(lastSuccessUtc.Value) + ".";
                    return new DiscoveryRefreshStatusPresentation(status, tooltip);
                }

                return new DiscoveryRefreshStatusPresentation(
                    CatalogUnavailableText,
                    "The candidate discovery catalog could not be loaded.");
            }

            // 3. Normal / Online state
            if (lastSuccessUtc.HasValue)
            {
                string pastRelative = FormatRelativePast(lastSuccessUtc.Value, now);
                if (nextRefreshUtc.HasValue)
                {
                    string futureRelative = FormatRelativeFuture(nextRefreshUtc.Value, now);
                    string status = "Last checked " + pastRelative + "; next check " + futureRelative;
                    string tooltip = "Last checked: " + ToUtcString(lastSuccessUtc.Value)
                        + "\nNext check: " + ToUtcString(nextRefreshUtc.Value);
                    return new DiscoveryRefreshStatusPresentation(status, tooltip);
                }

                return new DiscoveryRefreshStatusPresentation(
                    "Last checked " + pastRelative,
                    "Last checked: " + ToUtcString(lastSuccessUtc.Value));
            }

            if (nextRefreshUtc.HasValue)
            {
                string futureRelative = FormatRelativeFuture(nextRefreshUtc.Value, now);
                return new DiscoveryRefreshStatusPresentation(
                    "Not checked yet; next check " + futureRelative,
                    "Next check: " + ToUtcString(nextRefreshUtc.Value));
            }

            return new DiscoveryRefreshStatusPresentation("Not checked yet", null);
        }

        public static DiscoveryRefreshStatusPresentation Present(
            DateTime? lastSuccessUtc,
            DateTime? nextRefreshUtc,
            string? pausedReason,
            bool isOffline,
            DateTime nowUtc) => Present(lastSuccessUtc, nextRefreshUtc, pausedReason, isOffline, () => nowUtc);

        public static DiscoveryRefreshStatusPresentation Present(
            ModelDiscoveryCatalog? catalog,
            string? pausedReason = null,
            bool? isOffline = null,
            Func<DateTime>? clock = null)
        {
            bool offline = isOffline ?? IsFailedRefreshCode(catalog?.LastErrorCode);
            return Present(catalog?.LastSuccessUtc, catalog?.NextRefreshUtc, pausedReason, offline, clock);
        }

        public static DiscoveryRefreshStatusPresentation Present(
            ModelDiscoveryCatalog? catalog,
            string? pausedReason,
            bool? isOffline,
            DateTime nowUtc) => Present(catalog, pausedReason, isOffline, () => nowUtc);

        /// <summary>
        /// Formats a past timestamp as relative text. Clamps future timestamps to 'just now'
        /// to guarantee a time in the future is never shown as 'last checked'.
        /// </summary>
        public static string FormatRelativePast(DateTime timestampUtc, DateTime nowUtc)
        {
            if (timestampUtc.Kind == DateTimeKind.Local) timestampUtc = timestampUtc.ToUniversalTime();
            if (nowUtc.Kind == DateTimeKind.Local) nowUtc = nowUtc.ToUniversalTime();

            if (timestampUtc > nowUtc)
            {
                // Never show future time as last checked
                return "just now";
            }

            TimeSpan elapsed = nowUtc - timestampUtc;
            if (elapsed < TimeSpan.FromSeconds(60))
                return "just now";
            if (elapsed < TimeSpan.FromMinutes(2))
                return "1 minute ago";
            if (elapsed < TimeSpan.FromHours(1))
                return (int)elapsed.TotalMinutes + " minutes ago";
            if (elapsed < TimeSpan.FromHours(2))
                return "1 hour ago";
            if (elapsed < TimeSpan.FromDays(1))
                return (int)elapsed.TotalHours + " hours ago";
            if (elapsed < TimeSpan.FromDays(2))
                return "1 day ago";

            return (int)elapsed.TotalDays + " days ago";
        }

        /// <summary>
        /// Formats a future scheduled timestamp as relative text.
        /// </summary>
        public static string FormatRelativeFuture(DateTime timestampUtc, DateTime nowUtc)
        {
            if (timestampUtc.Kind == DateTimeKind.Local) timestampUtc = timestampUtc.ToUniversalTime();
            if (nowUtc.Kind == DateTimeKind.Local) nowUtc = nowUtc.ToUniversalTime();

            if (timestampUtc <= nowUtc)
                return "due now";

            TimeSpan remaining = timestampUtc - nowUtc;
            if (remaining < TimeSpan.FromSeconds(60))
                return "in less than a minute";
            if (remaining < TimeSpan.FromMinutes(2))
                return "in 1 minute";
            if (remaining < TimeSpan.FromHours(1))
                return "in " + (int)remaining.TotalMinutes + " minutes";
            if (remaining < TimeSpan.FromHours(2))
                return "in 1 hour";
            if (remaining < TimeSpan.FromDays(1))
                return "in " + (int)remaining.TotalHours + " hours";
            if (remaining < TimeSpan.FromDays(2))
                return "in 1 day";

            return "in " + (int)remaining.TotalDays + " days";
        }

        /// <summary>True only for real refresh failures; a user cancellation is not an offline state.</summary>
        public static bool IsFailedRefreshCode(string? lastErrorCode) =>
            !string.IsNullOrWhiteSpace(lastErrorCode)
            && !string.Equals(lastErrorCode.Trim(), "cancelled", StringComparison.OrdinalIgnoreCase);

        private static string ToUtcString(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Local) dt = dt.ToUniversalTime();
            return dt.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
        }
    }
}
