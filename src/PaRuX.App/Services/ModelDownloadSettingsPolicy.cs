using System;
using System.Globalization;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Pure validation and safe status text for the shared download settings UI.</summary>
    public static class ModelDownloadSettingsPolicy
    {
        public const double MaximumMegabytesPerSecond = 10000;

        public static bool TryParseBandwidthLimit(string? text, out double megabytesPerSecond)
        {
            megabytesPerSecond = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            const NumberStyles styles = NumberStyles.Float;
            if (!double.TryParse(text.Trim(), styles, CultureInfo.CurrentCulture, out double parsed)
                && !double.TryParse(text.Trim(), styles, CultureInfo.InvariantCulture, out parsed))
                return false;
            if (!double.IsFinite(parsed) || parsed < 0 || parsed > MaximumMegabytesPerSecond) return false;

            megabytesPerSecond = parsed;
            return true;
        }

        public static string DescribeBandwidthLimit(double megabytesPerSecond) =>
            !double.IsFinite(megabytesPerSecond) || megabytesPerSecond <= 0
                ? "Unlimited"
                : megabytesPerSecond.ToString("0.##", CultureInfo.CurrentCulture) + " MB/s";

        public static string DescribeCredentialStatus(ModelDownloadCredentialStatus? status)
        {
            if (status == null) return "Hugging Face credential status is unavailable.";
            if (!status.TokenConfigured) return "No token is saved. A token is only needed for gated or private repositories.";

            string protection = string.IsNullOrWhiteSpace(status.Protection)
                ? string.Empty
                : " Protected with " + status.Protection.Trim() + ".";
            return "A token is saved; its value is never displayed here." + protection;
        }
    }
}
