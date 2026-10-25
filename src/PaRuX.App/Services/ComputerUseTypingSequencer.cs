using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Character-by-character typing with its stop conditions, kept apart from the Win32 keyboard primitive so they can be
    /// unit-tested. Cancellation, a revoked authorization, a change of foreground window and a refused keystroke are all
    /// checked BEFORE each character is sent. A stop therefore never leaves a character unaccounted for, and no character
    /// is sent into a window other than the one the screenshot showed.
    /// </summary>
    internal static class ComputerUseTypingSequencer
    {
        internal enum Outcome
        {
            /// <summary>Every character was sent.</summary>
            Completed,

            /// <summary>The target window lost foreground; the remaining characters were not sent.</summary>
            TargetChanged,

            /// <summary>The authorization for this run ended (Full Computer Use was turned off, or Gaming Mode started).</summary>
            Revoked,

            /// <summary>Windows inserted no event for a keystroke; the remaining characters were not sent.</summary>
            InputBlocked
        }

        internal sealed record Result(Outcome Outcome, int CharactersSent);

        /// <param name="sendCharacter">Sends one character. Returns false when Windows did not accept it.</param>
        /// <param name="mayContinue">
        /// Read before each character. Returns false to stop because the run's authorization ended. Null means no
        /// authorization can end mid-typing (a per-task grant that a settings change does not revoke).
        /// </param>
        internal static async Task<Result> SendAsync(
            string? text,
            Func<char, bool> sendCharacter,
            Func<bool> targetStillForeground,
            TimeSpan interCharacterDelay,
            CancellationToken cancellationToken,
            Func<bool>? mayContinue = null)
        {
            ArgumentNullException.ThrowIfNull(sendCharacter);
            ArgumentNullException.ThrowIfNull(targetStillForeground);

            int sent = 0;
            foreach (char character in text ?? string.Empty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (mayContinue != null && !mayContinue())
                    return new Result(Outcome.Revoked, sent);
                if (!targetStillForeground())
                    return new Result(Outcome.TargetChanged, sent);
                if (!sendCharacter(character))
                    return new Result(Outcome.InputBlocked, sent);

                sent++;
                await Task.Delay(interCharacterDelay, cancellationToken).ConfigureAwait(false);
            }
            return new Result(Outcome.Completed, sent);
        }
    }

    /// <summary>
    /// A pause that ends as soon as its authorization does. A wait can run for five seconds, and turning Full Computer
    /// Use off or starting Gaming Mode must not have to wait for it to finish.
    /// </summary>
    internal static class ComputerUseWait
    {
        private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(50);

        /// <summary>True when the whole duration elapsed. False when <paramref name="mayContinue"/> returned false first.</summary>
        internal static async Task<bool> WaitWhileAuthorizedAsync(
            TimeSpan duration, Func<bool> mayContinue, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(mayContinue);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!mayContinue()) return false;
                TimeSpan remaining = duration - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) return true;
                await Task.Delay(remaining < Slice ? remaining : Slice, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
