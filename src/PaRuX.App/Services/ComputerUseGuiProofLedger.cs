using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    /// <summary>
    /// A bounded, task-local collection of independent exact UIA assertions
    /// recorded since the LAST UI mutation or app/window focus transition.
    /// The old implementation remembered only the final successful ui_assert
    /// and could therefore overlook a preceding control changed asynchronously.
    ///
    /// All expectations must be in the same OS-observed PID/HWND so that ONE
    /// new, foreground-scoped WinApp observation can independently recheck all
    /// controls at done. Proof for another window must not be silently admitted
    /// on the basis of a screenshot from the current one. Cross-window QA
    /// remains a separately scoped future contract, not implied by this ledger.
    ///
    /// No selectors, expected strings, HWNDs, PIDs or private UIA content
    /// leave this object through status, ToString or a persisted receipt.
    /// </summary>
    internal sealed class ComputerUseGuiProofLedger
    {
        internal const int MaxControls = 8;
        private readonly List<ComputerUseGuiFinalProof> _proofs = new();

        internal int Count => _proofs.Count;
        internal bool HasProof => _proofs.Count > 0;

        internal enum AddResult
        {
            Recorded,
            AlreadyRecorded,
            DifferentWindow,
            ConflictingExpectation,
            CapacityExceeded,
            InvalidProof
        }

        internal AddResult TryRecord(ComputerUseGuiFinalProof? proof)
        {
            if (proof == null) return AddResult.InvalidProof;

            if (_proofs.Count > 0 && !_proofs[0].SameWindowAs(proof))
                return AddResult.DifferentWindow;

            foreach (ComputerUseGuiFinalProof existing in _proofs)
            {
                if (!existing.SameControlAs(proof)) continue;
                return existing.SameExpectationAs(proof)
                    ? AddResult.AlreadyRecorded
                    : AddResult.ConflictingExpectation;
            }

            if (_proofs.Count >= MaxControls) return AddResult.CapacityExceeded;
            _proofs.Add(proof);
            return AddResult.Recorded;
        }

        internal void Invalidate() => _proofs.Clear();

        /// <summary>All distinct asserted controls are re-evaluated against the
        /// SAME fresh observation on the SAME original foreground PID/HWND.
        /// A change to ANY control makes the entire batch fail, not just the
        /// last assertion the model chose to show the verifier.</summary>
        internal bool RecheckAll(WindowsUiTreeObservationService.Observation? fresh)
        {
            if (_proofs.Count == 0 || fresh is not { HasEvidence: true })
                return false;
            foreach (ComputerUseGuiFinalProof proof in _proofs)
            {
                if (!proof.Recheck(fresh))
                    return false;
            }
            return true;
        }
    }
}
