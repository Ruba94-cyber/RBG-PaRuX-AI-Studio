using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Exact count includes the rendered template/schema. The visible response and
    /// reasoning allowance stay separate from the prompt. Unknown context is not a
    /// token-fit proof. Long arithmetic prevents integer overflow at large budgets.
    /// </summary>
    public static class CodingExactPromptAdmissionPolicy
    {
        public static bool Fits(int inputTokens, int contextTokens, int outputReserveTokens) =>
            inputTokens > 0 && contextTokens > 0
            && (long)inputTokens + Math.Max(0, outputReserveTokens) <= contextTokens;
    }
}
