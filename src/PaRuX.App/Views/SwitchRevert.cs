using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Puts a switch back to the position the server reports after its call failed, so a switch never keeps
    /// the position the user clicked when the server refused it. The decision itself is
    /// <see cref="ToggleFailureRecovery"/>; this only applies it to the control.
    /// </summary>
    internal static class SwitchRevert
    {
        /// <summary>
        /// Re-reads the server state and sets the switch to it. The switch is held in <paramref name="reverting"/>
        /// while it changes, so its Toggled handler ignores that change instead of sending it to the server again.
        /// </summary>
        public static async Task<ToggleRecovery> ApplyAsync(
            ToggleSwitch toggle,
            bool requested,
            HashSet<ToggleSwitch> reverting,
            Func<CancellationToken, Task<bool?>> readServerState)
        {
            // The last state the server reported is the opposite of the request that just failed.
            ToggleRecovery recovered = await ToggleFailureRecovery.RecoverAsync(!requested, readServerState, CancellationToken.None);
            reverting.Add(toggle);
            try
            {
                toggle.IsOn = recovered.Displayed;
            }
            finally
            {
                reverting.Remove(toggle);
            }
            return recovered;
        }

        /// <summary>The visible line for a failed switch, saying whether the shown state came from the server.</summary>
        public static string Explain(string what, bool requested, string reason, ToggleRecovery recovered) =>
            "Couldn't turn " + what + (requested ? " on" : " off") + ": " + reason + " "
            + (recovered.ServerConfirmed
                ? "The switch shows the server's state."
                : "The server could not confirm the state, so the switch shows the last known one.");
    }
}
