using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Outcome of evaluating whether the guided tour should start.
    /// </summary>
    public sealed record GuidedTourDecision(bool ShouldStart, string Reason)
    {
        public static GuidedTourDecision Start(string reason) => new(true, reason);
        public static GuidedTourDecision DoNotStart(string reason) => new(false, reason);
    }

    /// <summary>
    /// Pure policy governing when the sidebar guided tour is initiated, replayed, or suppressed.
    /// In accordance with the UX specification:
    /// 1. The guided tour starts after the first successful answer or after any hand-off.
    /// 2. It can be skipped (marking it completed so it does not pop up uninvited).
    /// 3. It can be replayed from a Help entry unconditionally.
    /// </summary>
    public static class GuidedTourTriggerPolicy
    {
        /// <summary>
        /// Evaluates whether an assistant chat reply should trigger the first-run guided tour.
        /// Starts only if:
        /// - The tour has not already been completed or dismissed.
        /// - This is the first assistant response turn.
        /// - The reply is an assistant turn (not user or system).
        /// - The reply completed successfully (not an error, not still streaming, not empty, and not stopped by user).
        /// </summary>
        public static GuidedTourDecision EvaluateFirstAnswer(
            bool isTourCompleted,
            bool hasHadSuccessfulAnswer,
            string? role,
            string? content,
            bool isError,
            bool isStreaming)
        {
            if (isTourCompleted)
            {
                return GuidedTourDecision.DoNotStart("TourAlreadyCompleted");
            }

            if (hasHadSuccessfulAnswer)
            {
                return GuidedTourDecision.DoNotStart("NotFirstAnswer");
            }

            if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return GuidedTourDecision.DoNotStart("NotAssistantTurn");
            }

            if (isError)
            {
                return GuidedTourDecision.DoNotStart("AssistantMessageIsError");
            }

            if (isStreaming)
            {
                return GuidedTourDecision.DoNotStart("MessageStillStreaming");
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return GuidedTourDecision.DoNotStart("AssistantMessageEmpty");
            }

            if (content.Contains("_Stopped._", StringComparison.Ordinal))
            {
                return GuidedTourDecision.DoNotStart("TurnStopped");
            }

            return GuidedTourDecision.Start("FirstSuccessfulAnswer");
        }

        /// <summary>
        /// Evaluates whether a workspace or sub-system hand-off (e.g. Onboarding -> Model Hub, or Chat -> Coding Agent)
        /// should start the guided tour.
        /// </summary>
        public static GuidedTourDecision EvaluateHandoff(
            bool isTourCompleted,
            string? handoffKind)
        {
            if (isTourCompleted)
            {
                return GuidedTourDecision.DoNotStart("TourAlreadyCompleted");
            }

            if (string.IsNullOrWhiteSpace(handoffKind))
            {
                return GuidedTourDecision.DoNotStart("UnknownHandoff");
            }

            return GuidedTourDecision.Start("Handoff:" + handoffKind.Trim());
        }

        /// <summary>
        /// Evaluates an explicit user request to replay the tour from a Help entry or Settings.
        /// This unconditionally starts the tour, bypassing the completion flag.
        /// </summary>
        public static GuidedTourDecision EvaluateReplayRequest()
        {
            return GuidedTourDecision.Start("ReplayRequested");
        }

        /// <summary>
        /// Evaluates whether completing the Onboarding golden path via "Get Started" should trigger the tour.
        /// </summary>
        public static GuidedTourDecision EvaluateGetStarted(bool isTourCompleted)
        {
            if (isTourCompleted)
            {
                return GuidedTourDecision.DoNotStart("TourAlreadyCompleted");
            }

            return GuidedTourDecision.Start("GetStarted");
        }

        /// <summary>
        /// Skipping or closing the tour is a decision that marks the tour as completed
        /// so it does not intrude on subsequent actions or app launches.
        /// </summary>
        public static bool ShouldRecordCompletionOnDismiss(bool currentCompleted)
        {
            return true;
        }
    }
}
