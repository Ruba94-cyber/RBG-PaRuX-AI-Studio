namespace PaRuX.Contracts;

/// <summary>What a headless watcher does after one look at a coding or agent run.</summary>
public enum AgentRunWaitDecision
{
    KeepPolling,
    Finished,
    WaitingForUserAnswer,
    TimedOut
}

/// <summary>
/// The decision behind `parux agent run`. A run that pauses for a user answer is reported at once, with its
/// question id, and exits with a code of its own. Polling it until the deadline and failing as a timeout is
/// what made a headless question look like a 30-minute failure (CF-05).
/// </summary>
public static class AgentRunWaitPolicy
{
    public static AgentRunWaitDecision Decide(string status, bool hasPendingUserQuestion, bool deadlinePassed)
    {
        if (AgentTaskStatus.Terminal.Contains(status)) return AgentRunWaitDecision.Finished;
        if (string.Equals(status, AgentTaskStatus.Waiting, StringComparison.Ordinal) && hasPendingUserQuestion)
            return AgentRunWaitDecision.WaitingForUserAnswer;
        return deadlinePassed ? AgentRunWaitDecision.TimedOut : AgentRunWaitDecision.KeepPolling;
    }
}
