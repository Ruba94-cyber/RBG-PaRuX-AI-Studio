namespace PaRuX.Services
{
    /// <summary>Bounds consecutive prose-only responses and supplies distinct, actionable nudges.</summary>
    public static class CodingAgentNarrationStallPolicy
    {
        public const int MaximumNudges = 2;
        public const int StopAfterConsecutiveResponses = MaximumNudges + 2;

        public static bool IsNarrationOnly(string? response) =>
            !string.IsNullOrWhiteSpace(response)
            && response.IndexOf('{') < 0
            && response.IndexOf('}') < 0;

        public static string? NudgeFor(int consecutiveResponses) => consecutiveResponses switch
        {
            2 => "Return exactly one JSON action now. Pick the next concrete read, edit, command, question, or done action; do not narrate a plan.",
            3 => "This is the final protocol recovery turn. Output one complete JSON object with an action field, or the task will stop and preserve its checkpoint.",
            _ => null
        };

        public static bool ShouldStop(int consecutiveResponses) =>
            consecutiveResponses >= StopAfterConsecutiveResponses;
    }
}
