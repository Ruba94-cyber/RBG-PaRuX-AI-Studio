using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// When a coding prompt cannot fit even after evidence compaction, widen the coding model's
    /// window instead of failing: ask the same planner Runtime Manager uses for the cheapest launchable
    /// length (KV-cache quantization, then system RAM; never past the trained window), store it, and
    /// reload the model. Everything is reported to the task activity log; nothing is retried more
    /// than once per run by the caller.
    /// </summary>
    internal static class CodingContextRemedy
    {
        internal sealed record Outcome(bool Raised, int NewContextTokens, string Message);

        public static async Task<Outcome> TryRaiseAsync(int currentTokens, CancellationToken ct)
        {
            try
            {
                var roles = await ModelRolesClient.Current.GetRolesAsync(ct).ConfigureAwait(false);
                ModelRoleStatus? role = roles.FirstOrDefault(r => string.Equals(r.Role, "coding", StringComparison.OrdinalIgnoreCase) && r.Kind == "text")
                    ?? roles.FirstOrDefault(r => string.Equals(r.Role, "standard", StringComparison.OrdinalIgnoreCase) && r.Kind == "text");
                if (role == null || string.IsNullOrWhiteSpace(role.ModelPath) || !role.FileExists)
                    return new Outcome(false, currentTokens, "Automatic context increase skipped: no usable local coding model was found.");

                long needed = CodingContextRemedyPolicy.NeededTokens(currentTokens);
                ModelContextOptions options = await ModelRolesClient.Current
                    .GetContextOptionsAsync(role.ModelPath, ct).ConfigureAwait(false);
                ModelContextOption? choice = CodingContextRemedyPolicy.ChooseOption(options.Options, currentTokens, needed);
                if (choice == null)
                {
                    long best = options.Options.Count == 0 ? 0 : options.Options.Max(o => o.ContextLength);
                    return new Outcome(false, currentTokens,
                        "Automatic context increase not possible: this machine can launch " + Path.GetFileName(role.ModelPath)
                        + " at most at " + best.ToString("N0") + " tokens within its trained window (needed at least "
                        + needed.ToString("N0") + "). Free GPU memory, close other GPU programs, or choose a smaller model.");
                }

                ModelContextPreferenceResult applied = await ModelRolesClient.Current.SetContextPreferenceAsync(
                    new ModelContextPreferenceRequest
                    {
                        ModelPath = role.ModelPath,
                        ContextLength = choice.ContextLength,
                        CacheType = choice.CacheType,
                        UsesSystemRam = choice.UsesSystemRam
                    }, ct).ConfigureAwait(false);
                if (!applied.Applied)
                    return new Outcome(false, currentTokens, "Automatic context increase refused: " + applied.Message);

                RuntimeLoadResult reload = await ModelRolesClient.Current.LoadManagedModelAsync(
                    Path.GetFileName(role.ModelPath), role.Role, ct).ConfigureAwait(false);
                CodingContextBudgetResolver.Invalidate();
                if (!reload.Success)
                    return new Outcome(false, currentTokens, "Context preference stored but the model reload failed: " + reload.Message);

                CodingContextBudgetResolver.Budget budget = await CodingContextBudgetResolver.ResolveAsync(ct).ConfigureAwait(false);
                string cost = choice.UsesSystemRam ? " (KV cache in system RAM: slower)" : " (KV cache " + choice.CacheType + ")";
                return new Outcome(budget.ContextTokens > currentTokens, budget.ContextTokens,
                    "Raised the coding model context from " + currentTokens.ToString("N0") + " to "
                    + budget.ContextTokens.ToString("N0") + " tokens" + cost
                    + " and reloaded it; this model's context preference was updated and can be changed in Runtime Manager.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return new Outcome(false, currentTokens, "Automatic context increase failed: " + ex.Message);
            }
        }
    }
}
