using System;

namespace PaRuX.Services
{
    /// <summary>
    /// How a Model Hub download row follows its transfer. A download is followed for as long as it runs, but a read that
    /// keeps failing stops the follow-up after eight tries, with the wait doubling from 1.5 s up to 30 s, and the row says
    /// the download is unknown. The read limit is 24 hours of reads at the 1.5 s interval.
    /// </summary>
    public static class ModelDownloadStatusPolicy
    {
        public static bool CanPause(string status) => status is "queued" or "downloading" or "retrying";

        public static bool CanResume(string status) => status is "cancelled" or "failed" or "paused";

        public static bool CanRemove(string status, string? id = null) =>
            status is "cancelled" or "failed" or "paused" && !ModelDownloadResumeCatalog.IsSyntheticId(id);

        public static bool IsActive(string status) => status is "queued" or "downloading" or "retrying";

        public static StatusPollPolicy Follow { get; } = new(
            Interval: TimeSpan.FromMilliseconds(1500),
            MaxPolls: 24 * 60 * 60 * 1000 / 1500,
            MaxConsecutiveFailures: 8,
            MaxFailureBackoff: TimeSpan.FromSeconds(30));
    }
}
