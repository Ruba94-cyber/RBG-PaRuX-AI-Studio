using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Gate for destructive actions such as deleting or removing a registration. The action runs only when the
    /// confirmation answers yes; a closed or declined dialog, or a confirmation that cannot be shown, runs nothing.
    /// </summary>
    public static class ConfirmedAction
    {
        /// <returns>True when the action ran.</returns>
        public static async Task<bool> RunIfConfirmedAsync(
            Func<CancellationToken, Task<bool>> confirm,
            Func<CancellationToken, Task> perform,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(confirm);
            ArgumentNullException.ThrowIfNull(perform);

            bool confirmed;
            try
            {
                confirmed = await confirm(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // A confirmation that could not be shown is not a yes: nothing is deleted.
                confirmed = false;
            }

            if (!confirmed) return false;
            await perform(ct);
            return true;
        }
    }
}
