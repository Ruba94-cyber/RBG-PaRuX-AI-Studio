using System;

namespace PaRuX.Services
{
    /// <summary>
    /// A health endpoint or installed model is not proof of a working chat. The first-run check
    /// requires a fresh model answer with confirmed local routing and no remote fallback.
    /// </summary>
    public static class OnboardingChatVerificationPolicy
    {
        public static string? FailureReason(string challenge, string answer, bool localRouteSeen, bool remoteRouteSeen)
        {
            if (remoteRouteSeen)
                return "The answer switched to a remote provider; only local inference counts for setup.";
            if (!localRouteSeen)
                return "PaRuX Core did not confirm a local inference route. Check runtime diagnostics.";
            if (string.IsNullOrWhiteSpace(challenge) || string.IsNullOrWhiteSpace(answer))
                return "The local model did not return an answer. Load a text model and retry.";
            if (!answer.Contains(challenge, StringComparison.OrdinalIgnoreCase))
                return "The local model did not repeat the verification code. Try again or choose a different model.";
            return null;
        }
    }
}
