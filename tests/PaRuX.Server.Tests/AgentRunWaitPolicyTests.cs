using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>CF-05: `parux agent run` reports a run that pauses for a user answer, instead of timing out on it.</summary>
public sealed class AgentRunWaitPolicyTests
{
    [Theory]
    [InlineData(AgentTaskStatus.Completed)]
    [InlineData(AgentTaskStatus.Failed)]
    [InlineData(AgentTaskStatus.Cancelled)]
    public void Terminal_runs_are_finished_whatever_else_is_pending(string status)
    {
        Assert.Equal(AgentRunWaitDecision.Finished,
            AgentRunWaitPolicy.Decide(status, hasPendingUserQuestion: true, deadlinePassed: true));
    }

    [Fact]
    public void A_waiting_run_with_a_question_pauses_the_watcher_even_after_the_deadline()
    {
        Assert.Equal(AgentRunWaitDecision.WaitingForUserAnswer,
            AgentRunWaitPolicy.Decide(AgentTaskStatus.Waiting, hasPendingUserQuestion: true, deadlinePassed: false));
        Assert.Equal(AgentRunWaitDecision.WaitingForUserAnswer,
            AgentRunWaitPolicy.Decide(AgentTaskStatus.Waiting, hasPendingUserQuestion: true, deadlinePassed: true));
    }

    [Fact]
    public void A_waiting_run_without_a_question_keeps_polling_and_times_out_only_at_the_deadline()
    {
        // For example a delegated child: still a pause, but no question for the CLI to answer.
        Assert.Equal(AgentRunWaitDecision.KeepPolling,
            AgentRunWaitPolicy.Decide(AgentTaskStatus.Waiting, hasPendingUserQuestion: false, deadlinePassed: false));
        Assert.Equal(AgentRunWaitDecision.TimedOut,
            AgentRunWaitPolicy.Decide(AgentTaskStatus.Waiting, hasPendingUserQuestion: false, deadlinePassed: true));
    }

    [Fact]
    public void A_running_run_is_polled_until_it_finishes_or_the_deadline_passes()
    {
        Assert.Equal(AgentRunWaitDecision.KeepPolling,
            AgentRunWaitPolicy.Decide(AgentTaskStatus.Running, hasPendingUserQuestion: false, deadlinePassed: false));
        Assert.Equal(AgentRunWaitDecision.TimedOut,
            AgentRunWaitPolicy.Decide(AgentTaskStatus.Running, hasPendingUserQuestion: false, deadlinePassed: true));
    }
}
