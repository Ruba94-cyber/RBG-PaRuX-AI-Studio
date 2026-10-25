using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Closes a developer browser that was launched while PaRuX Internet was ON, once the switch reads OFF.
    /// Such a browser has no egress sink, so its WebSocket and WebRTC connections would stay open until the next
    /// browser action. The watch polls the switch, and a read that fails counts as OFF (fail closed). The decision and
    /// the loop are kept apart from the browser, so tests can drive them with fakes.
    /// </summary>
    internal static class BrowserInternetSwitchWatch
    {
        /// <summary>True when the browser must be closed. A null reading means the switch could not be read.</summary>
        public static bool MustClose(bool launchedWithInternetOn, bool? internetEnabledNow) =>
            launchedWithInternetOn && internetEnabledNow != true;

        /// <summary>
        /// Polls until the browser is closed or <paramref name="ct"/> is cancelled. A read that throws counts as
        /// unreadable. A close that throws is reported and retried on the next poll, while the switch still reads OFF.
        /// </summary>
        public static async Task RunAsync(
            Func<CancellationToken, Task<bool?>> readInternetEnabled,
            Func<Task> closeBrowser,
            TimeSpan interval,
            CancellationToken ct,
            Action<Exception>? reportCloseFailure = null)
        {
            while (true)
            {
                try { await Task.Delay(interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                bool? enabled;
                try { enabled = await readInternetEnabled(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception) { enabled = null; }

                if (!MustClose(launchedWithInternetOn: true, enabled)) continue;

                try
                {
                    await closeBrowser().ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { reportCloseFailure?.Invoke(ex); }
            }
        }
    }
}
