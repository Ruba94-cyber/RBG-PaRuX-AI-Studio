using System;

namespace PaRuX.Services
{
    public enum OnboardingDownloadState
    {
        Queued,
        Downloading,
        Paused,
        Completed,
        Failed,
        Cancelled
    }

    public enum OnboardingReturnAction
    {
        Stay,
        ReturnToSetup,
        ShowSuccess,
        ShowRetry
    }

    /// <summary>
    /// Pure policy governing onboarding return actions from Model Hub.
    /// Evaluates model download state and verified chat answer evidence to ensure
    /// the user returns to Setup upon download completion without dead ends,
    /// while requiring a verified chat answer (not only a file on disk) for a success state.
    /// </summary>
    public static class OnboardingReturnPolicy
    {
        /// <summary>
        /// Given the download state (queued, downloading, paused, completed, failed, cancelled)
        /// and whether a chat turn has produced a verified answer, returns Stay, ReturnToSetup,
        /// ShowSuccess or ShowRetry.
        /// </summary>
        public static OnboardingReturnAction Evaluate(OnboardingDownloadState downloadState, bool chatAnswerVerified)
        {
            if (chatAnswerVerified)
            {
                return OnboardingReturnAction.ShowSuccess;
            }

            return downloadState switch
            {
                OnboardingDownloadState.Completed => OnboardingReturnAction.ReturnToSetup,
                OnboardingDownloadState.Failed => OnboardingReturnAction.ShowRetry,
                OnboardingDownloadState.Cancelled => OnboardingReturnAction.ShowRetry,
                OnboardingDownloadState.Queued => OnboardingReturnAction.Stay,
                OnboardingDownloadState.Downloading => OnboardingReturnAction.Stay,
                OnboardingDownloadState.Paused => OnboardingReturnAction.Stay,
                _ => OnboardingReturnAction.Stay
            };
        }

        /// <summary>
        /// Evaluates string download status (e.g. from ModelDownloadStatus.Status).
        /// </summary>
        public static OnboardingReturnAction Evaluate(string? downloadStatus, bool chatAnswerVerified)
        {
            OnboardingDownloadState state = ParseDownloadState(downloadStatus);
            return Evaluate(state, chatAnswerVerified);
        }

        public static OnboardingDownloadState ParseDownloadState(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return OnboardingDownloadState.Queued;
            }

            return status.Trim().ToLowerInvariant() switch
            {
                "completed" or "complete" or "finished" or "succeeded" or "done" => OnboardingDownloadState.Completed,
                "failed" or "error" => OnboardingDownloadState.Failed,
                "cancelled" or "canceled" or "aborted" => OnboardingDownloadState.Cancelled,
                "downloading" or "in_progress" or "active" or "transferring" or "retrying" => OnboardingDownloadState.Downloading,
                "paused" => OnboardingDownloadState.Paused,
                "queued" or "pending" => OnboardingDownloadState.Queued,
                _ => OnboardingDownloadState.Queued
            };
        }

        public static bool ShouldReturnToSetup(OnboardingDownloadState downloadState, bool chatAnswerVerified) =>
            Evaluate(downloadState, chatAnswerVerified) == OnboardingReturnAction.ReturnToSetup;

        public static bool ShouldShowSuccess(OnboardingDownloadState downloadState, bool chatAnswerVerified) =>
            Evaluate(downloadState, chatAnswerVerified) == OnboardingReturnAction.ShowSuccess;

        public static bool ShouldShowRetry(OnboardingDownloadState downloadState, bool chatAnswerVerified) =>
            Evaluate(downloadState, chatAnswerVerified) == OnboardingReturnAction.ShowRetry;
    }
}
