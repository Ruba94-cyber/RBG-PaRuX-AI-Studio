using System;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// The single-use state of an "Allow once" Computer Use card. The card authorizes exactly one execution,
    /// and its first click consumes that authorization. The consumed flag is persisted on the message, so
    /// re-rendering the conversation or restarting PaRuX cannot replay it. Before this, the click handlers
    /// never consumed the approval, so the same stored payload ran again on every click.
    /// </summary>
    internal readonly record struct ComputerUseApprovalState(bool Consumed)
    {
        /// <summary>Label shown on a card whose approval has been spent.</summary>
        internal const string UsedLabel = "Allowed once · already used";

        /// <summary>
        /// Attempts the one permitted execution. Returns the state after the attempt and whether this attempt may
        /// execute. A card without a payload, or one already consumed, never executes.
        /// </summary>
        internal (ComputerUseApprovalState Next, bool MayExecute) TryExecute(string? payload)
        {
            if (Consumed || string.IsNullOrWhiteSpace(payload))
                return (this, false);
            return (this with { Consumed = true }, true);
        }
    }

    /// <summary>
    /// Admits one execution of an "Allow once" card. The approval is spent, and persisted, only after the run has taken
    /// the shared mouse and keyboard gate and Gaming Mode is off. Before this, the click handler spent the card first, and
    /// the run then refused itself when the device was busy. The card read "already used" with nothing run (CU-09).
    /// The spend is persisted before the executor starts, so neither a second click nor a restart can replay it (TI-04).
    /// </summary>
    internal static class ComputerUseApprovalAdmission
    {
        internal enum Refusal
        {
            /// <summary>The executor ran, and the card is now spent.</summary>
            None,

            /// <summary>The card was already spent, or has no action to run. Nothing ran.</summary>
            Unavailable,

            /// <summary>Another run holds the mouse and keyboard. Nothing ran, and the card stays unspent.</summary>
            GateBusy,

            /// <summary>Gaming Mode is on. Nothing ran, and the card stays unspent.</summary>
            GamingActive
        }

        /// <param name="alreadySpent">The card's persisted consumed flag.</param>
        /// <param name="payload">The stored action. A missing payload is not runnable.</param>
        /// <param name="tryTakeGate">Takes the shared gate without waiting. Null while another run holds it.</param>
        /// <param name="gamingActive">Read after the gate is taken. True refuses the attempt.</param>
        /// <param name="markSpentAndPersist">Spends the approval and persists it. Called before <paramref name="execute"/> starts.</param>
        /// <param name="execute">The one permitted execution. It receives the gate lease, which it may hand to the run.</param>
        internal static async Task<Refusal> RunOnceAsync(
            bool alreadySpent,
            string? payload,
            Func<ComputerUseRunGate.Lease?> tryTakeGate,
            Func<bool> gamingActive,
            Action markSpentAndPersist,
            Func<ComputerUseRunGate.Lease, Task> execute)
        {
            ArgumentNullException.ThrowIfNull(tryTakeGate);
            ArgumentNullException.ThrowIfNull(gamingActive);
            ArgumentNullException.ThrowIfNull(markSpentAndPersist);
            ArgumentNullException.ThrowIfNull(execute);

            if (!new ComputerUseApprovalState(alreadySpent).TryExecute(payload).MayExecute)
                return Refusal.Unavailable;

            using ComputerUseRunGate.Lease? lease = tryTakeGate();
            if (lease == null) return Refusal.GateBusy;
            if (gamingActive()) return Refusal.GamingActive;

            markSpentAndPersist();
            await execute(lease);
            return Refusal.None;
        }
    }
}
