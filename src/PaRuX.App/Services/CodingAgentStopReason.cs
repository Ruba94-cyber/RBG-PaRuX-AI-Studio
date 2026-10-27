using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Why a coding attempt stopped without finishing. The task card used to show a generic
    /// "Failed"/"sandbox unavailable" even when the real cause was a too-small model window or a
    /// model looping on the same read, so each stop now carries a specific, user-readable reason.
    /// </summary>
    public static class CodingAgentStopReason
    {
        public const string None = "";
        public const string ContextTooSmall = "context-too-small";
        public const string RepeatedAction = "repeated-action";
        public const string NoProgress = "no-progress";
        public const string Sandbox = "sandbox-unavailable";

        public static string Stage(string? kind) => kind switch
        {
            ContextTooSmall => "Stopped: model context window too small",
            RepeatedAction => "Stopped: model repeated the same action",
            NoProgress => "Stopped: no progress without a code change",
            Sandbox => "Coding sandbox unavailable",
            _ => "Coding attempt unfinished"
        };

        /// <summary>What the user can do about it, appended to the recorded error.</summary>
        public static string NextStep(string? kind) => kind switch
        {
            ContextTooSmall =>
                " PaRuX tries to raise the window automatically; if it still failed, free GPU memory, "
                + "pick a smaller model or raise the context in Runtime Manager. The checkpoint is preserved.",
            RepeatedAction =>
                " Resume the task to continue from the preserved checkpoint, or restate the goal more narrowly.",
            NoProgress =>
                " Resume with a narrower goal or name the file and symbol to change.",
            _ => string.Empty
        };
    }
}
