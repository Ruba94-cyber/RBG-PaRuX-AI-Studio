using System;
using System.IO;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Extracts execution constraints that the user stated in a coding request. These are resolved
    /// before a task is created so a stale Settings workspace cannot override an explicit target.
    /// </summary>
    public static class CodingTaskIntent
    {
        public const string ComputerUseEvidenceMarker = "PARUX_REQUIRE_COMPUTER_USE_EVIDENCE";

        private static readonly Regex QuotedWindowsPath = new(
            "[\\\"'](?<path>[A-Za-z]:\\\\[^\\\"'\\r\\n]+)[\\\"']",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ComputerUseRequest = new(
            @"\b(comput(?:er|e)\s+use|uso\s+del\s+computer|controllo\s+(?:reale\s+)?(?:del\s+)?(?:pc|computer)|test(?:a|are|ala|arlo|ing)?\s+(?:realmente\s+)?(?:la\s+|l['’])?(?:ui|ux|interfaccia)|real\s+(?:ui|ux|gui)\s+tests?)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex PreserveGitRequest = new(
            @"\b(non\s+(?:toccare|usare|modificare)\s+git|senza\s+(?:toccare|usare|modificare)\s+git|do\s+not\s+(?:touch|use|modify)\s+git|without\s+(?:touching|using|modifying)\s+git)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string? ExtractExplicitWindowsPath(string? objective)
        {
            if (string.IsNullOrWhiteSpace(objective)) return null;
            Match match = QuotedWindowsPath.Match(objective);
            if (!match.Success) return null;
            try { return Path.GetFullPath(match.Groups["path"].Value.Trim()); }
            catch { return null; }
        }

        public static bool RequiresComputerUse(string? objective) =>
            !string.IsNullOrWhiteSpace(objective) && ComputerUseRequest.IsMatch(objective);

        public static bool PreserveOriginalGit(string? objective) =>
            !string.IsNullOrWhiteSpace(objective) && PreserveGitRequest.IsMatch(objective);

        public static string AppendExecutionRequirements(string goal, string? objective)
        {
            if (!RequiresComputerUse(objective)) return goal;
            return goal
                + "\n\n" + ComputerUseEvidenceMarker + "\n"
                + "The user explicitly requires testing through PaRuX Computer Use on the real Windows UI. "
                + "After the final code mutation and build, launch the real candidate, then delegate one child with role=computer-use to exercise the requested UI flows and measurements. "
                + "Do not finish from browser DOM checks, source inspection, screenshots alone, or ordinary unit tests. "
                + "The parent may finish only after it receives a completed, independently verified role=computer-use child result. "
                + "If Windows control needs approval or another user-owned permission, use ask_user and preserve the checkpoint.";
        }

        public static bool HasVerifiedComputerUseEvidence(string? task) =>
            !string.IsNullOrWhiteSpace(task)
            && Regex.IsMatch(task,
                @"\[DEPENDENCY RESULT [^\]\r\n]+[· ]+role=computer-use[· ]+verification=pass\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
