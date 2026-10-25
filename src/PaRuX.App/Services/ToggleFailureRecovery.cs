using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// The position a settings switch shows after its server call failed. When <see cref="ServerConfirmed"/> is
    /// false the server could not be asked, so the switch keeps the last state it knew instead of the one clicked.
    /// </summary>
    public readonly record struct ToggleRecovery(bool Displayed, bool ServerConfirmed);

    /// <summary>
    /// Decides which position a switch must show after a failed call. A switch never keeps a position the server
    /// did not accept: the server's own answer from a re-read wins, and when the re-read fails too the last state
    /// the server reported is kept. Privacy and cloud controls depend on this, so the failure is never guessed at.
    /// </summary>
    public static class ToggleFailureRecovery
    {
        /// <param name="lastKnownServerState">The state the server had before the failed change.</param>
        /// <param name="readServerState">Re-reads the server's state; null means the item is no longer listed.</param>
        public static async Task<ToggleRecovery> RecoverAsync(
            bool lastKnownServerState,
            Func<CancellationToken, Task<bool?>> readServerState,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(readServerState);
            try
            {
                bool? confirmed = await readServerState(ct);
                if (confirmed is bool value)
                {
                    return new ToggleRecovery(value, ServerConfirmed: true);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // The re-read failed as well: keep the last state the server reported.
            }

            return new ToggleRecovery(lastKnownServerState, ServerConfirmed: false);
        }
    }
}
