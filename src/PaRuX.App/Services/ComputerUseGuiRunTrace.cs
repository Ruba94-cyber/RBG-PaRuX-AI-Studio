using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Bounded, task-local telemetry assembled by the trusted Computer Use executor.
    /// No UI values, selectors, screenshots, model text, window titles, process IDs,
    /// device paths, command lines or credentials enter this object.
    /// This is not an independently signed, tamper-proof execution receipt.
    /// </summary>
    internal sealed class ComputerUseGuiRunTrace
    {
        internal const int MaxEvents = 100;
        internal int ActionsAttempted { get; private set; }
        internal int ActionsDelivered { get; private set; }
        internal int UiAssertionsPassed { get; private set; }
        internal int UiAssertionsFailed { get; private set; }
        internal int FinalReadbackChecks { get; private set; }
        internal int FinalReadbackFailures { get; private set; }
        internal bool LastFinalReadbackPassed { get; private set; }
        internal int LastDistinctControlsVerified { get; private set; }

        internal void RecordActionAttempt()
        {
            if (ActionsAttempted < MaxEvents) ActionsAttempted++;
        }

        internal void RecordActionDelivered()
        {
            if (ActionsDelivered < MaxEvents) ActionsDelivered++;
        }

        internal void RecordAssertion(bool verified)
        {
            if (verified)
            {
                if (UiAssertionsPassed < MaxEvents) UiAssertionsPassed++;
            }
            else if (UiAssertionsFailed < MaxEvents) UiAssertionsFailed++;
        }

        internal void RecordFinalReadback(bool verified, int distinctControlsVerified = 1)
        {
            LastFinalReadbackPassed = verified;
            // This is executor-reported control count, not an assertion from
            // the language model. Never retain selectors or expected values.
            LastDistinctControlsVerified = verified
                ? Math.Clamp(distinctControlsVerified, 1, ComputerUseGuiProofLedger.MaxControls) : 0;
            if (FinalReadbackChecks < MaxEvents) FinalReadbackChecks++;
            if (!verified && FinalReadbackFailures < MaxEvents) FinalReadbackFailures++;
        }

        /// <summary>Stable, redacted outcome. "ready" is not "passed";
        /// it only admits a separate independent goal verifier.</summary>
        internal ComputerUseGuiRunReceipt Build(
            bool guiTestRequired, bool notStarted, bool goalReported,
            bool cancelled, bool guiProofSatisfied, bool finalReadbackVerified,
            int reportedUiAssertions, int stepsTaken)
        {
            int boundedSteps = Math.Clamp(stepsTaken, 0, 15);
            bool consistentAssertionCount = reportedUiAssertions >= 0
                && reportedUiAssertions == UiAssertionsPassed;
            bool verified = !guiTestRequired || (guiProofSatisfied
                && finalReadbackVerified && consistentAssertionCount
                && UiAssertionsPassed > 0 && FinalReadbackChecks > 0
                && LastFinalReadbackPassed && LastDistinctControlsVerified > 0);
            string outcome = notStarted ? "not_started"
                : cancelled ? "cancelled"
                : !goalReported ? "incomplete"
                : !verified ? "unverified"
                : "ready_for_independent_verifier";
            string reason = notStarted ? "executor_not_started"
                : cancelled ? "execution_cancelled"
                : !goalReported ? "no_valid_completion"
                : guiTestRequired && !consistentAssertionCount ? "assertion_count_conflict"
                : guiTestRequired && UiAssertionsPassed == 0 ? "missing_ui_assertion"
                : guiTestRequired && (!finalReadbackVerified || !LastFinalReadbackPassed)
                    ? "final_readback_unverified"
                : guiTestRequired && !guiProofSatisfied ? "stale_assertion"
                : verified ? "handoff_only" : "evidence_incomplete";

            return new ComputerUseGuiRunReceipt
            {
                SchemaVersion = 1,
                Outcome = outcome,
                ReasonCode = reason,
                Assurance = "executor-reported-not-independently-attested",
                GuiTestRequired = guiTestRequired,
                GoalReported = goalReported,
                ActionsAttempted = ActionsAttempted,
                ActionsDelivered = ActionsDelivered,
                UiAssertionsPassed = UiAssertionsPassed,
                UiAssertionsFailed = UiAssertionsFailed,
                FinalReadbackChecks = FinalReadbackChecks,
                FinalReadbackFailures = FinalReadbackFailures,
                FinalDistinctControlsVerified = LastDistinctControlsVerified,
                StepsTaken = boundedSteps,
                FinalControlReadbackVerified = finalReadbackVerified,
                ReadyForIndependentVerifier = outcome == "ready_for_independent_verifier"
            };
        }
    }

    /// <summary>
    /// Redacted, machine-readable Computer Use run summary, not a user-goal PASS.
    /// Every property is a fixed string, boolean or bounded integer.
    /// </summary>
    internal sealed class ComputerUseGuiRunReceipt
    {
        public int SchemaVersion { get; init; }
        public string Outcome { get; init; } = string.Empty;
        public string ReasonCode { get; init; } = string.Empty;
        public string Assurance { get; init; } = string.Empty;
        public bool GuiTestRequired { get; init; }
        public bool GoalReported { get; init; }
        public int ActionsAttempted { get; init; }
        public int ActionsDelivered { get; init; }
        public int UiAssertionsPassed { get; init; }
        public int UiAssertionsFailed { get; init; }
        public int FinalReadbackChecks { get; init; }
        public int FinalReadbackFailures { get; init; }
        public int FinalDistinctControlsVerified { get; init; }
        public int StepsTaken { get; init; }
        public bool FinalControlReadbackVerified { get; init; }
        public bool ReadyForIndependentVerifier { get; init; }
    }
}
