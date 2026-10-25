using System;
using System.Collections.Generic;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    public enum BackgroundWorkKind
    {
        Generation,
        Download,
        Install
    }

    public enum BackgroundWorkOutcome
    {
        Completed,
        Failed
    }

    public enum BackgroundWorkOpenTarget
    {
        Chat,
        ModelHub,
        RuntimeManager,
        Settings
    }

    /// <summary>
    /// User preferences for background work notifications per type.
    /// In accordance with the clean-room specification: "Notifications can be turned off per type."
    /// </summary>
    public sealed record BackgroundNotificationPreferences(
        bool NotifyOnGeneration = true,
        bool NotifyOnDownloads = true,
        bool NotifyOnInstalls = true)
    {
        public bool IsEnabled(BackgroundWorkKind kind) => kind switch
        {
            BackgroundWorkKind.Generation => NotifyOnGeneration,
            BackgroundWorkKind.Download => NotifyOnDownloads,
            BackgroundWorkKind.Install => NotifyOnInstalls,
            _ => false
        };
    }

    /// <summary>
    /// Normalized representation of a background job completion or failure.
    /// </summary>
    public sealed record BackgroundWorkItem(
        string Id,
        BackgroundWorkKind Kind,
        BackgroundWorkOutcome Outcome,
        string Title,
        string Message,
        BackgroundWorkOpenTarget OpenTarget,
        bool IsUserCancelled = false);

    /// <summary>
    /// Decision outcome from evaluating a background work notification candidate.
    /// </summary>
    public sealed record BackgroundNotificationDecision(
        bool ShouldNotify,
        string Reason,
        BackgroundWorkItem? Item = null)
    {
        public static BackgroundNotificationDecision Notify(BackgroundWorkItem item, string reason = "Eligible") =>
            new(true, reason, item);

        public static BackgroundNotificationDecision Suppress(string reason, BackgroundWorkItem? item = null) =>
            new(false, reason, item);
    }

    /// <summary>
    /// Pure policy governing when notifications are raised for background work (downloads, installs, generation).
    /// Enforces:
    /// 1. Exactly one notification per finished or failed job (deduplicated by job ID).
    /// 2. Notifications are only raised when the window is hidden (minimized or parked in tray).
    /// 3. Notifications can be turned off per type (Generation, Downloads, Installs).
    /// 4. User cancellations/stops never raise notifications.
    /// 5. Failed jobs provide actionable error messages.
    /// </summary>
    public static class BackgroundWorkNotificationPolicy
    {
        /// <summary>
        /// Evaluates whether a background work item should raise a desktop/tray notification.
        /// </summary>
        public static BackgroundNotificationDecision Evaluate(
            BackgroundWorkItem? item,
            bool isWindowHidden,
            BackgroundNotificationPreferences preferences,
            IReadOnlySet<string>? alreadyNotifiedJobIds)
        {
            if (item == null)
            {
                return BackgroundNotificationDecision.Suppress("NullItem");
            }

            if (string.IsNullOrWhiteSpace(item.Id))
            {
                return BackgroundNotificationDecision.Suppress("MissingJobId", item);
            }

            if (item.IsUserCancelled)
            {
                return BackgroundNotificationDecision.Suppress("UserCancelled", item);
            }

            if (alreadyNotifiedJobIds != null && alreadyNotifiedJobIds.Contains(item.Id))
            {
                return BackgroundNotificationDecision.Suppress("AlreadyNotified", item);
            }

            if (!isWindowHidden)
            {
                return BackgroundNotificationDecision.Suppress("WindowNotHidden", item);
            }

            if (!preferences.IsEnabled(item.Kind))
            {
                return BackgroundNotificationDecision.Suppress("DisabledByType:" + item.Kind, item);
            }

            return BackgroundNotificationDecision.Notify(item, "Eligible");
        }

        /// <summary>
        /// Formats a media generation result (Image, Video, ImageEdit) into a normalized work item.
        /// </summary>
        public static BackgroundWorkItem FormatGeneration(
            string kindName,
            bool isError,
            string? content,
            string jobId,
            bool isUserCancelled)
        {
            string displayKind = string.IsNullOrWhiteSpace(kindName) ? "Generation" : kindName.Trim();
            if (isError)
            {
                string message = string.IsNullOrWhiteSpace(content) ? "The job did not complete." : content.Trim();
                return new BackgroundWorkItem(
                    Id: jobId,
                    Kind: BackgroundWorkKind.Generation,
                    Outcome: BackgroundWorkOutcome.Failed,
                    Title: displayKind + " generation failed",
                    Message: message,
                    OpenTarget: BackgroundWorkOpenTarget.Chat,
                    IsUserCancelled: isUserCancelled);
            }
            else
            {
                string message = string.IsNullOrWhiteSpace(content) ? "Your generation finished." : content.Trim();
                return new BackgroundWorkItem(
                    Id: jobId,
                    Kind: BackgroundWorkKind.Generation,
                    Outcome: BackgroundWorkOutcome.Completed,
                    Title: displayKind + " ready",
                    Message: message,
                    OpenTarget: BackgroundWorkOpenTarget.Chat,
                    IsUserCancelled: isUserCancelled);
            }
        }

        /// <summary>
        /// Formats a model download status into a normalized work item.
        /// Returns null if the download is still in progress (queued or downloading).
        /// </summary>
        public static BackgroundWorkItem? FormatDownload(ModelDownloadStatus status)
        {
            if (status == null) return null;
            if (string.IsNullOrWhiteSpace(status.Id)) return null;

            string normalizedStatus = (status.Status ?? string.Empty).Trim().ToLowerInvariant();
            string fileName = string.IsNullOrWhiteSpace(status.FileName) ? "Model" : status.FileName.Trim();

            if (normalizedStatus == "completed")
            {
                return new BackgroundWorkItem(
                    Id: status.Id,
                    Kind: BackgroundWorkKind.Download,
                    Outcome: BackgroundWorkOutcome.Completed,
                    Title: "Download completed",
                    Message: fileName + " is ready to use.",
                    OpenTarget: BackgroundWorkOpenTarget.ModelHub,
                    IsUserCancelled: false);
            }
            else if (normalizedStatus == "failed")
            {
                string errorMsg = string.IsNullOrWhiteSpace(status.Error)
                    ? fileName + " could not be downloaded."
                    : fileName + ": " + status.Error.Trim();

                return new BackgroundWorkItem(
                    Id: status.Id,
                    Kind: BackgroundWorkKind.Download,
                    Outcome: BackgroundWorkOutcome.Failed,
                    Title: "Download failed",
                    Message: errorMsg,
                    OpenTarget: BackgroundWorkOpenTarget.ModelHub,
                    IsUserCancelled: false);
            }
            else if (normalizedStatus == "cancelled")
            {
                return new BackgroundWorkItem(
                    Id: status.Id,
                    Kind: BackgroundWorkKind.Download,
                    Outcome: BackgroundWorkOutcome.Failed,
                    Title: "Download cancelled",
                    Message: fileName + " download was cancelled.",
                    OpenTarget: BackgroundWorkOpenTarget.ModelHub,
                    IsUserCancelled: true);
            }

            // In-flight states ("queued", "downloading", "paused", etc.)
            return null;
        }

        /// <summary>
        /// Formats a runtime build/install status into a normalized work item.
        /// Returns null if the install is still in progress.
        /// </summary>
        public static BackgroundWorkItem? FormatInstall(RuntimeInstallStatus status)
        {
            if (status == null) return null;
            if (string.IsNullOrWhiteSpace(status.Id)) return null;

            string normalizedStage = (status.Stage ?? string.Empty).Trim().ToLowerInvariant();
            string backendName = string.IsNullOrWhiteSpace(status.Backend) ? "Runtime" : status.Backend.Trim();
            string label = string.IsNullOrWhiteSpace(status.Tag)
                ? backendName
                : backendName + " (" + status.Tag.Trim() + ")";

            if (normalizedStage == "completed")
            {
                return new BackgroundWorkItem(
                    Id: status.Id,
                    Kind: BackgroundWorkKind.Install,
                    Outcome: BackgroundWorkOutcome.Completed,
                    Title: "Runtime install completed",
                    Message: label + " is ready.",
                    OpenTarget: BackgroundWorkOpenTarget.RuntimeManager,
                    IsUserCancelled: false);
            }
            else if (normalizedStage == "failed")
            {
                string errorMsg = string.IsNullOrWhiteSpace(status.Error)
                    ? label + " install failed."
                    : label + ": " + status.Error.Trim();

                return new BackgroundWorkItem(
                    Id: status.Id,
                    Kind: BackgroundWorkKind.Install,
                    Outcome: BackgroundWorkOutcome.Failed,
                    Title: "Runtime install failed",
                    Message: errorMsg,
                    OpenTarget: BackgroundWorkOpenTarget.RuntimeManager,
                    IsUserCancelled: false);
            }
            else if (normalizedStage == "cancelled")
            {
                return new BackgroundWorkItem(
                    Id: status.Id,
                    Kind: BackgroundWorkKind.Install,
                    Outcome: BackgroundWorkOutcome.Failed,
                    Title: "Runtime install cancelled",
                    Message: label + " install was cancelled.",
                    OpenTarget: BackgroundWorkOpenTarget.RuntimeManager,
                    IsUserCancelled: true);
            }

            // In-flight stages ("queued", "downloading", "verifying", "extracting", "swapping", "performance-check")
            return null;
        }
    }
}
