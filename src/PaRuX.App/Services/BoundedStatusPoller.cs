using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>How often a run's status is read, and how far the reading may go on. Every limit is finite.</summary>
    /// <param name="Interval">Wait between two successful reads.</param>
    /// <param name="MaxPolls">Upper bound on reads, so a run that never finishes cannot hold the page forever.</param>
    /// <param name="MaxConsecutiveFailures">Failed reads in a row before the poll gives up and says so.</param>
    /// <param name="MaxFailureBackoff">Longest wait after a failed read; waits double from <c>Interval</c> up to this.</param>
    public sealed record StatusPollPolicy(TimeSpan Interval, int MaxPolls, int MaxConsecutiveFailures, TimeSpan MaxFailureBackoff);

    /// <summary>How a bounded poll ended.</summary>
    public enum StatusPollEnd
    {
        /// <summary>The run reached a state the caller treats as final.</summary>
        Terminal,

        /// <summary>The server no longer lists the run.</summary>
        NotListed,

        /// <summary>Reads kept failing; the last error is reported.</summary>
        GaveUp,

        /// <summary>The read limit was reached before a final state.</summary>
        TimedOut,

        /// <summary>The caller cancelled the poll.</summary>
        Cancelled
    }

    public sealed record StatusPollResult<T>(StatusPollEnd End, T? Last, string? LastError);

    /// <summary>
    /// Polls one run's status until it reaches a final state. A failed read is retried with growing waits until the
    /// consecutive-failure limit, and the number of reads is capped, so the poll always ends with an observable
    /// result. Nothing here knows about the UI: the caller shows each reading and each outcome.
    /// </summary>
    public static class BoundedStatusPoller
    {
        /// <param name="read">Reads the current status. Throws on a failed request; returns null when the run is not listed.</param>
        /// <param name="isTerminal">True for a status the run will not leave.</param>
        /// <param name="onStatus">Called with every successful reading.</param>
        /// <param name="onFailure">Called with the error text and the number of consecutive failures so far.</param>
        /// <param name="delay">Waiting strategy; null waits in real time.</param>
        public static async Task<StatusPollResult<T>> RunAsync<T>(
            Func<CancellationToken, Task<T?>> read,
            Func<T, bool> isTerminal,
            StatusPollPolicy policy,
            Action<T>? onStatus,
            Action<string, int>? onFailure,
            Func<TimeSpan, CancellationToken, Task>? delay,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(read);
            ArgumentNullException.ThrowIfNull(isTerminal);
            ArgumentNullException.ThrowIfNull(policy);
            if (policy.MaxPolls < 1 || policy.MaxConsecutiveFailures < 1)
                throw new ArgumentOutOfRangeException(nameof(policy), "A poll needs at least one read and one allowed failure.");

            Func<TimeSpan, CancellationToken, Task> wait = delay ?? ((duration, token) => Task.Delay(duration, token));
            T? last = default;
            string? lastError = null;
            int failures = 0;

            for (int poll = 1; poll <= policy.MaxPolls; poll++)
            {
                T? status;
                try
                {
                    status = await read(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return new StatusPollResult<T>(StatusPollEnd.Cancelled, last, lastError);
                }
                catch (Exception ex)
                {
                    failures++;
                    lastError = ex.Message;
                    onFailure?.Invoke(ex.Message, failures);
                    if (failures >= policy.MaxConsecutiveFailures)
                        return new StatusPollResult<T>(StatusPollEnd.GaveUp, last, lastError);
                    if (!await WaitAsync(wait, BackoffAfter(policy, failures), ct))
                        return new StatusPollResult<T>(StatusPollEnd.Cancelled, last, lastError);
                    continue;
                }

                failures = 0;
                lastError = null;
                if (status is null)
                    return new StatusPollResult<T>(StatusPollEnd.NotListed, last, lastError);

                last = status;
                onStatus?.Invoke(status);
                if (isTerminal(status))
                    return new StatusPollResult<T>(StatusPollEnd.Terminal, status, null);

                if (poll < policy.MaxPolls && !await WaitAsync(wait, policy.Interval, ct))
                    return new StatusPollResult<T>(StatusPollEnd.Cancelled, last, lastError);
            }

            return new StatusPollResult<T>(StatusPollEnd.TimedOut, last, lastError);
        }

        /// <summary>The wait after the n-th consecutive failure: Interval, doubled per failure, capped.</summary>
        public static TimeSpan BackoffAfter(StatusPollPolicy policy, int consecutiveFailures)
        {
            int exponent = Math.Clamp(consecutiveFailures - 1, 0, 20);
            long ticks = policy.Interval.Ticks * (1L << exponent);
            return TimeSpan.FromTicks(Math.Min(ticks, policy.MaxFailureBackoff.Ticks));
        }

        private static async Task<bool> WaitAsync(Func<TimeSpan, CancellationToken, Task> wait, TimeSpan duration, CancellationToken ct)
        {
            try
            {
                await wait(duration, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}
