using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// A developer browser is shared as a process for resource efficiency but
    /// NEVER across independently running coding tasks. Claim occurs before
    /// any browser tool, and release blocks new claims until its process stops.
    /// Ownership is in-memory authority, not a persistent user grant.
    /// </summary>
    public sealed class BrowserTaskOwnerLease
    {
        private readonly object _guard = new();
        private string? _owner;
        private bool _releasing;

        /// <summary>The proof of a release that could not be confirmed. A later claim re-checks it.</summary>
        private Func<bool>? _unprovenStop;

        public bool TryClaim(string? taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return false;
            Func<bool>? unprovenStop;
            lock (_guard)
            {
                if (!_releasing) return ClaimLocked(taskId);
                unprovenStop = _unprovenStop;
            }

            // A release that could not be proven stays fail closed. The proof is re-checked on every claim, though,
            // so a browser that stopped later does not keep the lease locked until PaRuX restarts. The check can
            // query the process table, so it runs outside the lock.
            if (unprovenStop == null || !IsProvenStopped(unprovenStop)) return false;

            lock (_guard)
            {
                if (_releasing && ReferenceEquals(_unprovenStop, unprovenStop))
                {
                    _owner = null;
                    _releasing = false;
                    _unprovenStop = null;
                }
                return ClaimLocked(taskId);
            }
        }

        /// <summary>Caller must hold the guard.</summary>
        private bool ClaimLocked(string taskId)
        {
            if (_releasing) return false;
            if (_owner == null)
            {
                _owner = taskId;
                return true;
            }
            return string.Equals(_owner, taskId, StringComparison.Ordinal);
        }

        public bool BeginRelease(string? taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return false;
            lock (_guard)
            {
                if (_releasing || !string.Equals(_owner, taskId, StringComparison.Ordinal))
                    return false;
                _releasing = true;
                return true;
            }
        }

        public void CompleteRelease(string? taskId)
        {
            lock (_guard)
            {
                if (_releasing && string.Equals(_owner, taskId, StringComparison.Ordinal))
                {
                    _owner = null;
                    _releasing = false;
                    _unprovenStop = null;
                }
            }
        }

        /// <summary>Keeps the proof of an unconfirmed release so that a later claim can re-check it. Until a claim
        /// succeeds the lease stays locked, which is still fail closed.</summary>
        private void KeepProofForLaterClaims(string? taskId, Func<bool> isProvenStopped)
        {
            lock (_guard)
            {
                if (_releasing && string.Equals(_owner, taskId, StringComparison.Ordinal))
                    _unprovenStop = isProvenStopped;
            }
        }

        /// <summary>
        /// Releases the lease only after the browser is proven stopped. The graceful
        /// shutdown is bounded. If it fails or does not finish, a forced stop runs and
        /// the lease completes only when the proof then holds. Otherwise the lease stays
        /// locked (fail closed), so a process that may still hold the profile is never
        /// lent to another task. The completion runs in finally, so no exception path
        /// can leave a proven-stopped lease held.
        /// </summary>
        public async Task<bool> ReleaseAsync(
            string? taskId,
            Func<CancellationToken, Task> gracefulShutdown,
            Func<CancellationToken, Task> forceStop,
            Func<bool> isProvenStopped,
            TimeSpan gracefulTimeout,
            TimeSpan forceStopTimeout)
        {
            if (!BeginRelease(taskId)) return false;
            bool completed = false;
            try
            {
                bool graceful = await RunBoundedAsync(gracefulShutdown, gracefulTimeout).ConfigureAwait(false);
                if (!graceful || !IsProvenStopped(isProvenStopped))
                    await RunBoundedAsync(forceStop, forceStopTimeout).ConfigureAwait(false);
                completed = IsProvenStopped(isProvenStopped);
                if (!completed) KeepProofForLaterClaims(taskId, isProvenStopped);
                return completed;
            }
            finally
            {
                if (completed) CompleteRelease(taskId);
            }
        }

        private static async Task<bool> RunBoundedAsync(Func<CancellationToken, Task> action, TimeSpan timeout)
        {
            using var bounded = new CancellationTokenSource();
            try
            {
                Task work = action(bounded.Token);
                Task finished = await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false);
                if (finished != work)
                {
                    bounded.Cancel();
                    return false;
                }
                await work.ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsProvenStopped(Func<bool> isProvenStopped)
        {
            try
            {
                return isProvenStopped();
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool IsOwnedBy(string? taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return false;
            lock (_guard)
                return !_releasing && string.Equals(_owner, taskId, StringComparison.Ordinal);
        }
    }
}
