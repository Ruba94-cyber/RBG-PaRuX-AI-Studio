using System;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record MediaSetupCardModel(
        bool Visible, string Headline, string Detail, string ButtonLabel, bool ButtonEnabled,
        bool ShowProgress, double Progress, bool Indeterminate);

    /// <summary>
    /// The Create status line shows the setup state only until something more specific replaces it (a validation error, a job,
    /// a result). The card's periodic refresh must never overwrite such a message, or an error vanishes within seconds.
    /// </summary>
    public static class CreateSetupStatusLine
    {
        public const string Checking = "Checking what image generation needs on this PC.";
        public const string Ready = "Create is ready. Every generation runs locally through PaRuX Core.";
        public const string NeedsSetup = "Image generation needs a one-time setup. Use the setup card in the Image section; everything runs and stays on this PC.";

        public static string For(bool ready) => ready ? Ready : NeedsSetup;

        /// <summary>True when the line still shows only a setup-state message (or nothing), so the refresh may update it.</summary>
        public static bool MayReplace(string? current) =>
            string.IsNullOrWhiteSpace(current) || current == Checking || current == Ready || current == NeedsSetup;
    }

    /// <summary>What the Create "Set up image generation" card shows for one server state. Pure, so tests can pin it.</summary>
    public static class MediaSetupPresenter
    {
        public static MediaSetupCardModel Present(MediaSetupStatus status, string label = "image generation")
        {
            if (status == null) throw new ArgumentNullException(nameof(status));
            if (status.Ready)
                return new MediaSetupCardModel(false, string.Empty, string.Empty, string.Empty, false, false, 0, false);

            switch (status.State)
            {
                case "running":
                    bool known = status.TotalBytes > 0;
                    double fraction = known ? Math.Min(1.0, (double)status.BytesDownloaded / status.TotalBytes) : 0;
                    return new MediaSetupCardModel(true, "Setting up " + label, status.Message, "Cancel setup", true,
                        true, fraction, !known);
                case "waiting":
                    return new MediaSetupCardModel(true, "Waiting for another setup to finish", status.Message,
                        "Set up " + label, false, false, 0, false);
                case "failed":
                    return new MediaSetupCardModel(true, Cap(label) + " setup stopped",
                        string.IsNullOrWhiteSpace(status.Error) ? status.Message : status.Message + " " + status.Error,
                        "Try again", true, false, 0, false);
                case "cancelled":
                    return new MediaSetupCardModel(true, Cap(label) + " setup was cancelled",
                        status.Message + " Downloads resume where they stopped.", "Resume setup", true, false, 0, false);
                case "blocked":
                    return new MediaSetupCardModel(true, Cap(label) + " is not available on this PC",
                        status.Message, "Set up " + label, false, false, 0, false);
                default:
                    var pending = status.Steps.Where(s => s.State == "pending").ToList();
                    long bytes = pending.Sum(s => s.SizeBytes);
                    string what = pending.Count == 0
                        ? status.Message
                        : "PaRuX will download and install, one verified step at a time: "
                          + string.Join(", ", pending.Select(s => s.Title)) + " (about " + FormatSize(bytes) + "). Licences: "
                          + string.Join(", ", pending.Where(s => !string.IsNullOrWhiteSpace(s.License)).Select(s => s.License + " (" + s.Title + ")")) + ".";
                    return new MediaSetupCardModel(true, "Set up " + label, what, "Set up " + label, true, false, 0, false);
            }
        }

        private static string Cap(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        private static string FormatSize(long bytes) =>
            bytes >= 1024L * 1024 * 1024
                ? (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
                : (bytes / (1024.0 * 1024)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
    }
}
