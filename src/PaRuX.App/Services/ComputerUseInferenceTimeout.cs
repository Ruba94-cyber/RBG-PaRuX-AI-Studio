using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Bounds one local-model inference, so a stalled model cannot keep a task Running and hold its execution slot forever.
    /// Only the time limit is reported as a timeout, and it is returned as a result the caller can turn into an explicit
    /// failure. A user stop or an outer cancellation still propagates as cancellation, so it is never mistaken for a timeout.
    /// </summary>
    internal static class ComputerUseInferenceTimeout
    {
        /// <summary>
        /// True when the inference finished within <paramref name="limit"/>. False when the limit elapsed first.
        /// Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        internal static async Task<bool> CompletesWithinAsync(
            Func<CancellationToken, Task> inference,
            TimeSpan limit,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(inference);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(limit);
            try
            {
                await inference(bounded.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && bounded.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}
