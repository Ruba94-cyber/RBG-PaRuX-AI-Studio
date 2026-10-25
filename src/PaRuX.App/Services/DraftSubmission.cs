using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>Result of submitting text typed into a box. A rejected submit keeps the text in the box.</summary>
    public readonly record struct DraftSubmitResult(bool Accepted, string? Error);

    /// <summary>
    /// Sends a draft the user typed. The caller clears the box only when <see cref="DraftSubmitResult.Accepted"/> is
    /// true, so a failed save never discards the text; the reason is returned for the page to show.
    /// </summary>
    public static class DraftSubmission
    {
        public static async Task<DraftSubmitResult> SubmitAsync(Func<CancellationToken, Task> submit, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(submit);
            try
            {
                await submit(ct);
                return new DraftSubmitResult(Accepted: true, Error: null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DraftSubmitResult(Accepted: false, Error: ex.Message);
            }
        }
    }
}
