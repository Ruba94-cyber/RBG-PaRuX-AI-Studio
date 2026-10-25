using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    public enum ChatMediaReconcileAction
    {
        /// <summary>Nothing to do (not a media message, already settled, or already attached).</summary>
        None,
        /// <summary>Server could not be asked yet; keep the message as running and try again later.</summary>
        Defer,
        /// <summary>Job is still queued/running on the server: show progress and poll again.</summary>
        Reattach,
        /// <summary>Job completed: the caller saves the asset and calls <see cref="ChatMediaJobReconciler.ApplyCompleted"/>.</summary>
        AttachAsset,
        /// <summary>Message was settled as failed, cancelled or lost; a Retry is offered.</summary>
        Settled
    }

    /// <summary>Pure decision logic for a chat reply that owns a media job. No I/O, no UI, no clock.</summary>
    public static class ChatMediaJobReconciler
    {
        public const string Running = "running";
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";

        public const string CancelledText = "Media generation was cancelled.";
        public const string LostText = "The media job is no longer known to the local server (it was lost, for example after a data reset), so it cannot be resumed.";

        public static bool IsMediaMessage(ChatMessage m) => m != null && !string.IsNullOrEmpty(m.MediaJobState);

        /// <summary>True for a persisted assistant reply that still claims a running job. The caller excludes the reply it is actively generating.</summary>
        public static bool NeedsReconcile(ChatMessage m) =>
            m != null && m.Role == "assistant" && string.Equals(m.MediaJobState, Running, StringComparison.Ordinal);

        public static void BeginStep(ChatMessage m, string kind, bool chainVideoPending)
        {
            m.MediaJobKind = kind;
            m.MediaJobState = Running;
            m.MediaJobError = null;
            m.MediaJobId = null;
            m.MediaProgressPercent = 0;
            m.MediaProgressStage = null;
            m.MediaChainVideoPending = chainVideoPending;
            m.IsError = false;
        }

        public static void SetJobId(ChatMessage m, string jobId) => m.MediaJobId = jobId;

        public static void ReportProgress(ChatMessage m, MediaJobStatus job)
        {
            m.MediaProgressPercent = Math.Clamp(job.ProgressPercent, 0, 100);
            m.MediaProgressStage = job.Stage;
        }

        /// <summary>Decides what to do with a persisted running media message given the server's view.</summary>
        /// <param name="job">The server job, or null when the server answered but does not know the id.</param>
        /// <param name="serverReachable">False when the server could not be asked at all.</param>
        public static ChatMediaReconcileAction Reconcile(ChatMessage m, MediaJobStatus? job, bool serverReachable)
        {
            if (!NeedsReconcile(m)) return ChatMediaReconcileAction.None;
            if (!serverReachable) return ChatMediaReconcileAction.Defer;
            if (job == null || string.IsNullOrEmpty(m.MediaJobId))
            {
                ApplyFailure(m, LostText);
                return ChatMediaReconcileAction.Settled;
            }

            switch ((job.Status ?? string.Empty).ToLowerInvariant())
            {
                case "queued":
                case "running":
                    ReportProgress(m, job);
                    return ChatMediaReconcileAction.Reattach;
                case "completed":
                    if (m.Attachments.Any(a => a.MediaJobId == job.Id))
                    {
                        MarkCompleted(m);
                        return ChatMediaReconcileAction.Settled;
                    }
                    if (job.Asset == null)
                    {
                        ApplyFailure(m, "The media job finished but returned no file.");
                        return ChatMediaReconcileAction.Settled;
                    }
                    return ChatMediaReconcileAction.AttachAsset;
                case "cancelled":
                    ApplyCancelled(m);
                    return ChatMediaReconcileAction.Settled;
                case "failed":
                    ApplyFailure(m, string.IsNullOrWhiteSpace(job.Error) ? "Media generation failed." : job.Error!);
                    return ChatMediaReconcileAction.Settled;
                default:
                    ApplyFailure(m, "The media job reported an unknown state '" + job.Status + "'.");
                    return ChatMediaReconcileAction.Settled;
            }
        }

        /// <summary>Attaches a finished asset exactly once (keyed by job id) and settles the message.</summary>
        public static void ApplyCompleted(ChatMessage m, ChatAttachment saved, string jobId, string description)
        {
            if (!m.Attachments.Any(a => a.MediaJobId == jobId))
            {
                saved.MediaJobId = jobId;
                m.Attachments.Add(saved);
            }
            m.Content = description;
            MarkCompleted(m);
        }

        private static void MarkCompleted(ChatMessage m)
        {
            m.MediaJobState = Completed;
            m.MediaJobError = null;
            m.MediaProgressPercent = 100;
            m.IsError = false;
            if (IsVideo(m)) m.MediaChainVideoPending = false;
        }

        public static bool HasFinishedImage(ChatMessage m) =>
            m.Attachments.Any(a => a.MediaType != null && a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

        public static void ApplyFailure(ChatMessage m, string error)
        {
            m.MediaJobState = Failed;
            m.MediaJobError = error;
            bool partial = HasFinishedImage(m) && IsVideo(m);
            if (partial) m.MediaChainVideoPending = true;
            string head = partial ? "Image generated; the video step failed: " : "Media generation failed: ";
            m.Content = KeepFinishedSummary(m) + head + error;
            // A finished image is a durable artifact, not an error bubble.
            m.IsError = !partial;
            m.IsStreaming = false;
        }

        public static void ApplyCancelled(ChatMessage m)
        {
            m.MediaJobState = Cancelled;
            m.MediaJobError = null;
            bool partial = HasFinishedImage(m) && IsVideo(m);
            if (partial) m.MediaChainVideoPending = true;
            m.Content = KeepFinishedSummary(m) + (partial ? "Image generated; video generation was cancelled." : CancelledText);
            m.IsError = false;
            m.IsStreaming = false;
        }

        private static bool IsVideo(ChatMessage m) => string.Equals(m.MediaJobKind, "video", StringComparison.Ordinal);

        private static string KeepFinishedSummary(ChatMessage m)
        {
            string c = m.Content ?? string.Empty;
            // Drop any text from a previous failure/cancel note so retries do not stack notes.
            foreach (string marker in new[] { "Image generated; ", "Media generation " })
            {
                int i = c.IndexOf(marker, StringComparison.Ordinal);
                if (i == 0) return string.Empty;
                if (i > 0) c = c.Substring(0, i).TrimEnd();
            }
            return c.Length == 0 ? string.Empty : c.TrimEnd() + "\n\n";
        }

        // ------------------------------------------------------------------ presentation

        public static ChatMediaPresentation Present(ChatMessage m)
        {
            if (!IsMediaMessage(m)) return ChatMediaPresentation.Hidden;
            switch (m.MediaJobState)
            {
                case Running:
                    string stage = string.IsNullOrWhiteSpace(m.MediaProgressStage) ? "Generating" : m.MediaProgressStage!;
                    return new ChatMediaPresentation(true, stage + " · " + m.MediaProgressPercent + "%", null, false, string.Empty);
                case Cancelled:
                    return new ChatMediaPresentation(false, null, CancelledText, true, RetryLabel(m));
                case Failed:
                    return new ChatMediaPresentation(false, null, null, true, RetryLabel(m));
                default:
                    return m.MediaChainVideoPending && HasFinishedImage(m)
                        ? new ChatMediaPresentation(false, null, null, true, "Retry video")
                        : ChatMediaPresentation.Hidden;
            }
        }

        public static string RetryLabel(ChatMessage m) =>
            IsVideo(m) && HasFinishedImage(m) ? "Retry video" : "Retry";
    }

    public sealed class ChatMediaPresentation
    {
        public static readonly ChatMediaPresentation Hidden = new ChatMediaPresentation(false, null, null, false, string.Empty);
        public ChatMediaPresentation(bool showProgress, string? progressText, string? cancelledText, bool showRetry, string retryLabel)
        {
            ShowProgress = showProgress; ProgressText = progressText; CancelledText = cancelledText;
            ShowRetry = showRetry; RetryLabel = retryLabel;
        }
        public bool ShowProgress { get; }
        public string? ProgressText { get; }
        public string? CancelledText { get; }
        public bool ShowRetry { get; }
        public string RetryLabel { get; }
    }

    /// <summary>What a Retry click should run. Same prompt and the Create workspace's random-seed policy (Seed = -1).</summary>
    public sealed class ChatMediaRetryPlan
    {
        public bool RunImage { get; init; }
        public bool RunVideo { get; init; }
        public bool RunEdit { get; init; }
        public string EditType { get; init; } = "edit";
        public string Prompt { get; init; } = string.Empty;
        /// <summary>Existing local image to animate/edit; null means use the image produced in this retry (chain) or none.</summary>
        public string? SourceImagePath { get; init; }
        public long Seed => -1;
    }

    public static class ChatMediaRetryPlanner
    {
        /// <summary>Plans a retry for <paramref name="reply"/>. Null when the reply is not retryable.</summary>
        public static ChatMediaRetryPlan? Plan(IReadOnlyList<ChatMessage> messages, ChatMessage reply)
        {
            if (reply == null) return null;
            bool settledBad = reply.MediaJobState is ChatMediaJobReconciler.Failed or ChatMediaJobReconciler.Cancelled;
            bool chainOwed = reply.MediaJobState == ChatMediaJobReconciler.Completed && reply.MediaChainVideoPending
                && ChatMediaJobReconciler.HasFinishedImage(reply);
            if (!settledBad && !chainOwed) return null;
            int index = -1;
            for (int i = 0; i < messages.Count; i++) if (ReferenceEquals(messages[i], reply)) { index = i; break; }
            if (index < 0) return null;
            ChatMessage? user = null;
            for (int i = index - 1; i >= 0; i--) if (messages[i].Role == "user") { user = messages[i]; break; }
            if (user == null || string.IsNullOrWhiteSpace(user.Content)) return null;

            string? userImage = user.Attachments?.FirstOrDefault(a => a.MediaType != null
                && a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(a.LocalPath))?.LocalPath;
            ChatAttachment? ownImage = reply.Attachments.FirstOrDefault(a => a.MediaType != null
                && a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

            // Image finished but the video step never completed: only the video is retried, the image is kept.
            string? kind = ownImage != null && reply.MediaChainVideoPending ? "video" : reply.MediaJobKind;
            switch (kind)
            {
                case "video":
                    return new ChatMediaRetryPlan
                    {
                        RunVideo = true, Prompt = user.Content,
                        SourceImagePath = ownImage?.LocalPath ?? userImage
                    };
                case "image":
                    return new ChatMediaRetryPlan
                    {
                        RunImage = true, Prompt = user.Content,
                        RunVideo = RouterService.HasFollowUpVideoStep(user.Content) || reply.MediaChainVideoPending
                    };
                case "edit":
                case "img2img":
                    return userImage == null ? null : new ChatMediaRetryPlan
                    {
                        RunEdit = true, EditType = reply.MediaJobKind, Prompt = user.Content, SourceImagePath = userImage
                    };
                default:
                    return null;
            }
        }

        /// <summary>Marks the reply as running again without touching attachments (no duplicate artifact).</summary>
        public static void PrepareRetry(ChatMessage reply, string kind, bool chainVideoPending)
        {
            // Preserve the finished image summary text; the next outcome rewrites the note.
            ChatMediaJobReconciler.BeginStep(reply, kind, chainVideoPending);
        }
    }
}
