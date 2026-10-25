using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Completion verification is evidence, not merely a shell command that happens to begin with
    /// a familiar build/test token. Only the exact command discovered by
    /// <see cref="CodingVerificationDiscovery"/> may satisfy the coding completion gate. This
    /// deliberately rejects appended flags, command chaining and exit-code masking such as
    /// `dotnet test & echo ok`; the model can run other commands, but they remain ordinary evidence.
    /// </summary>
    public enum CodingWorkerHandoffDecision
    {
        WaitForUser,
        WaitForDelegatedChild,
        RejectPendingVerification,
        SendToVerifier
    }

    public static class CodingVerificationCommandPolicy
    {
        public static bool IsExactMatch(string? discoveredCommand, string? executedCommand)
        {
            if (string.IsNullOrWhiteSpace(discoveredCommand) || string.IsNullOrWhiteSpace(executedCommand))
                return false;
            string discovered = Normalize(discoveredCommand);
            string executed = Normalize(executedCommand);
            if (string.Equals(discovered, executed, StringComparison.OrdinalIgnoreCase)) return true;
            // "--no-restore" is only an offline optimisation: the same .NET command without it
            // restores first and then builds/tests the same projects, so it verifies at least as much.
            string withoutNoRestore = RemoveToken(discovered, "--no-restore");
            if (discovered.StartsWith("dotnet ", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(withoutNoRestore, discovered, StringComparison.Ordinal)
                && (string.Equals(withoutNoRestore, executed, StringComparison.OrdinalIgnoreCase)
                    || IsDotNetWithOnlySerialBuildFlags(withoutNoRestore, executed)))
                return true;
            return IsDotNetWithOnlySerialBuildFlags(discovered, executed);
        }

        private static string RemoveToken(string command, string token) =>
            string.Join(" ", command.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !string.Equals(part, token, StringComparison.OrdinalIgnoreCase)));

        public const int MaxIdenticalSuccessfulCommandRepeats = 3;

        /// <summary>
        /// Live benchmark: a 27B model reran an already-successful `dotnet test` 42 times with no
        /// change in between; each success counted as progress, so the watchdog never fired.
        /// </summary>
        public static bool ShouldPauseAfterRepeatedSuccess(int identicalRepeats) =>
            identicalRepeats >= MaxIdenticalSuccessfulCommandRepeats;

        /// <summary>
        /// MSBuild's out-of-process worker nodes can fail inside the AppContainer (named-pipe
        /// creation is denied, MSB1025), so a model may rerun the exact discovered .NET command with
        /// in-process/serial build switches. Those switches change parallelism only, not what is
        /// built or tested; any other appended token (filters, chaining, redirection) still fails.
        /// </summary>
        private static readonly string[] DotNetSerialBuildFlags =
        {
            "-m:1", "/m:1", "-maxcpucount:1", "/maxcpucount:1", "--disable-build-servers",
            "-nodereuse:false", "/nodereuse:false", "-nr:false", "/nr:false"
        };

        private static bool IsDotNetWithOnlySerialBuildFlags(string discovered, string executed)
        {
            if (!discovered.StartsWith("dotnet ", StringComparison.OrdinalIgnoreCase)
                || !executed.StartsWith(discovered + " ", StringComparison.OrdinalIgnoreCase))
                return false;
            string[] extra = executed[(discovered.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return extra.Length > 0 && extra.All(token =>
                DotNetSerialBuildFlags.Contains(token, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// A paused attempt may reach the independent verifier only when no required verification
        /// is outstanding. A model-based verifier judges the diff and summary; it does not run the
        /// discovered command, so handing it an unverified attempt could promote an unexecuted
        /// change to Verified.
        /// </summary>
        public static bool MayHandPausedAttemptToVerifier(bool goalReported, bool verificationPending) =>
            goalReported && !verificationPending;

        /// <summary>
        /// One audited authority decision for coding-worker handoff. A durable
        /// pause does not become a failed task just because verification is
        /// pending; a terminal completion never bypasses pending verification.
        /// </summary>
        public static CodingWorkerHandoffDecision DecideHandoff(
            bool waitingForUser, bool waitingForDelegatedChild,
            bool goalReported, bool verificationPending)
        {
            if (waitingForUser) return CodingWorkerHandoffDecision.WaitForUser;
            if (waitingForDelegatedChild) return CodingWorkerHandoffDecision.WaitForDelegatedChild;
            return MayHandPausedAttemptToVerifier(goalReported, verificationPending)
                ? CodingWorkerHandoffDecision.SendToVerifier
                : CodingWorkerHandoffDecision.RejectPendingVerification;
        }

        public const int MaxUnverifiedCompletionRejections = 3;

        /// <summary>
        /// Bounds a loop observed live: the model repeated a successful non-verification command
        /// (which counts as progress) and reported done 22 times without running an accepted check.
        /// </summary>
        public static bool ShouldPauseAfterUnverifiedCompletions(int rejectionsSinceLastMutationOrVerification) =>
            rejectionsSinceLastMutationOrVerification >= MaxUnverifiedCompletionRejections;

        /// <summary>
        /// An answered user exchange is consumed once. Unanswered exchanges are not recorded, so a
        /// later answer to the same question is still delivered.
        /// </summary>
        public static bool IsNewUserAnswer(bool alreadyConsumed, string? answer) =>
            !alreadyConsumed && !string.IsNullOrWhiteSpace(answer);

        /// <summary>
        /// What a verification run's own output proves about the tests it executed. Exit code 0 is necessary
        /// but not sufficient: a filter that matches nothing, a project without tests, or a suite whose tests
        /// were all skipped also exits 0 while executing nothing. Only the deterministic summary formats below
        /// are read. Any other output is not parsed; its acceptance keeps resting on the exit code, and the
        /// evidence says so rather than implying that tests were counted.
        /// </summary>
        public sealed record TestRunEvidence(bool Parsed, int Executed, int Skipped)
        {
            /// <summary>A recognised summary reported zero executed tests, so exit code 0 verifies nothing.</summary>
            public bool ExecutedNoTests => Parsed && Executed == 0;

            public string Note => !Parsed
                ? "Test evidence: no recognised test summary; acceptance rests on the exit code alone."
                : Executed == 0
                    ? "Test evidence: no tests executed (" + Skipped + " skipped). Exit code 0 does not verify this change."
                    : "Test evidence: " + Executed + " tests executed, " + Skipped + " skipped.";
        }

        // VSTest (`dotnet test`) prints one summary per test assembly in the console language, for example
        // "Passed!  - Failed: 0, Passed: 30, Skipped: 0, Total: 30, ..." or
        // "Superato!     - Non superati: 0. Superati: 30. Ignorati: 0. Totale: 30. ...". Labels are matched case-sensitively.
        private static readonly Regex VsTestCount = new(
            @"(Passed|Failed|Skipped|Total|Superati|Non superati|Ignorati|Totale):\s*(\d+)", RegexOptions.CultureInvariant);

        // A non-matching filter (or an assembly without tests) prints no summary line and still exits 0.
        // These messages are the only evidence of that case, in the English and Italian consoles. A run that
        // prints one is refused whatever its exit code.
        private static readonly string[] VsTestNoTestMessages =
        {
            "No test matches the given testcase filter",
            "No test is available in",
            "No tests matched the filter because it contains one or more properties that are not valid",
            "Nessun test corrisponde al filtro di test case",
            "Non ci sono test disponibili in",
            "Nessun test corrisponde al filtro perché contiene una o più proprietà non valide"
        };

        // pytest closes with "===== 3 passed, 1 skipped in 0.12s =====" or "===== no tests ran in 0.01s =====".
        private static readonly Regex PytestSummary = new(
            @"^=+ (?<body>.+?) in \d+(?:\.\d+)?s(?: \(\d+:\d{2}:\d{2}\))? =+\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex PytestCount = new(
            @"(\d+) (passed|failed|errors?|xfailed|xpassed|skipped)\b", RegexOptions.CultureInvariant);

        // Jest prints "Tests:       1 failed, 2 passed, 3 total"; Vitest prints "Tests  1 failed | 2 passed (3)".
        private static readonly Regex JestTests = new(@"^\s*Tests:\s+(?<body>.+)$", RegexOptions.CultureInvariant);
        private static readonly Regex VitestTests = new(
            @"^\s*Tests\s{2,}(?<body>[^(]+?)\s*\(\d+\)\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex JsCount = new(
            @"(\d+) (passed|failed|skipped|todo|total)\b", RegexOptions.CultureInvariant);

        /// <summary>
        /// Reads executed and skipped test counts from a verification run's output. Counts from every recognised
        /// summary in the output are added together, so a run that executes tests in one runner and none in another
        /// is not mistaken for a run with no tests.
        /// </summary>
        public static TestRunEvidence ParseTestRunSummary(string? output)
        {
            if (string.IsNullOrWhiteSpace(output)) return new TestRunEvidence(false, 0, 0);

            bool parsed = false;
            int executed = 0;
            int skipped = 0;
            bool vsTestNoTests = false;
            int vsTestSummaries = 0;
            foreach (string line in output.Split('\n'))
            {
                if (VsTestNoTestMessages.Any(message => line.Contains(message, StringComparison.Ordinal)))
                    vsTestNoTests = true;
                int? passed = null, failed = null, skippedCount = null, total = null;
                foreach (Match match in VsTestCount.Matches(line))
                {
                    if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) continue;
                    switch (match.Groups[1].Value)
                    {
                        case "Passed": case "Superati": passed = value; break;
                        case "Failed": case "Non superati": failed = value; break;
                        case "Skipped": case "Ignorati": skippedCount = value; break;
                        default: total = value; break; // Total, Totale
                    }
                }
                if (passed != null && total != null)
                {
                    vsTestSummaries++;
                    executed += (passed ?? 0) + (failed ?? 0);
                    skipped += skippedCount ?? 0;
                }

                Match pytest = PytestSummary.Match(line);
                if (pytest.Success)
                {
                    string body = pytest.Groups["body"].Value.Trim();
                    if (string.Equals(body, "no tests ran", StringComparison.Ordinal))
                    {
                        parsed = true;
                    }
                    else
                    {
                        foreach (Match count in PytestCount.Matches(body))
                        {
                            if (!int.TryParse(count.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) continue;
                            parsed = true;
                            if (count.Groups[2].Value == "skipped") skipped += value;
                            else executed += value;
                        }
                    }
                }

                parsed |= AddJavaScriptCounts(JestTests.Match(line), ref executed, ref skipped);
                parsed |= AddJavaScriptCounts(VitestTests.Match(line), ref executed, ref skipped);
            }

            if (vsTestSummaries > 0 || vsTestNoTests) parsed = true;
            return new TestRunEvidence(parsed, executed, skipped);
        }

        private static bool AddJavaScriptCounts(Match line, ref int executed, ref int skipped)
        {
            if (!line.Success) return false;
            bool any = false;
            foreach (Match count in JsCount.Matches(line.Groups["body"].Value))
            {
                if (!int.TryParse(count.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) continue;
                any = true;
                if (count.Groups[2].Value == "skipped" || count.Groups[2].Value == "todo") skipped += value;
                else if (count.Groups[2].Value != "total") executed += value;
            }
            return any;
        }

        internal static string Normalize(string command) =>
            string.Join(" ", command.Trim().Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries));
    }
}
