using System;
using System.Text.RegularExpressions;

namespace PaRuX.Contracts
{
    /// <summary>
    /// Conservative, shared classification of an ORIGINAL user goal as a
    /// Windows GUI-testing task. Both desktop executor and Core verifier
    /// must use this exact contract; delegated context, web text and model
    /// output must never change the required proof level.
    ///
    /// This is a defensive heuristic, not an explicit typed test-mode flag.
    /// Some genuine GUI test intentions may remain unrecognized until the
    /// separate typed task-mode UI and contract are implemented.
    /// </summary>
    public static class ComputerUseGuiIntentPolicy
    {
        private static readonly Regex StrongTestWords = new(
            @"\b(test|tests|testing|testa|testare|collauda|collaudare)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex VerifyWords = new(
            @"\b(test|tests|testing|testa|testare|testaremo|collauda|collaudare|verifica|verificare|verify|validate|check|checks)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex GuiWords = new(
            @"\b(app|apps|applicazione|applicazioni|application|applications|ui|gui|interfaccia|interfacce|interface|finestra|finestre|window|windows|form|desktop)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static bool RequiresAssertion(string? originalGoal) =>
            !string.IsNullOrWhiteSpace(originalGoal)
            && originalGoal!.Length <= 16_384
            && (StrongTestWords.IsMatch(originalGoal)
                || (VerifyWords.IsMatch(originalGoal) && GuiWords.IsMatch(originalGoal)));
    }
}
