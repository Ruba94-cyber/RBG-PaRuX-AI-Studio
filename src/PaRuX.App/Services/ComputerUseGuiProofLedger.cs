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
        private readonly List<ComputerUseWindowFocusPolicy.Candidate> _windows = new();
        private bool _strictSingleWindowOnly;

        internal int Count => _proofs.Count;
        internal int WindowCount => _windows.Count;
        internal IReadOnlyList<ComputerUseWindowFocusPolicy.Candidate> VerifiedWindows =>
            _windows.AsReadOnly();
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

        internal AddResult TryRecord(ComputerUseGuiFinalProof? proof) =>
            Record(proof, null);

        /// <summary>Only used after an explicit model focus_window and
        /// independently re-read OS PID/HWND/EXE on a current authorized task.
        /// Window identity is never provided by a model or remote client.</summary>
        internal AddResult TryRecordVerifiedWindow(
            ComputerUseGuiFinalProof? proof,
            ComputerUseWindowFocusPolicy.Candidate? osVerifiedWindow) =>
            Record(proof, osVerifiedWindow);

        private AddResult Record(
            ComputerUseGuiFinalProof? proof,
            ComputerUseWindowFocusPolicy.Candidate? identity)
        {
            if (proof == null) return AddResult.InvalidProof;
            if (_proofs.Count == 0)
                _strictSingleWindowOnly = identity == null;
            if (_strictSingleWindowOnly && _proofs.Count > 0
                && !_proofs[0].SameWindowAs(proof))
                return AddResult.DifferentWindow;
            if (!_strictSingleWindowOnly)
            {
                if (identity == null
                    || !proof.MatchesWindow(identity.ProcessId, identity.WindowHandle)
                    || !ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(identity.ExecutablePath))
                    return AddResult.InvalidProof;
                ComputerUseWindowFocusPolicy.Candidate? registered =
                    _windows.Find(x => x.ProcessId == identity.ProcessId
                        && x.WindowHandle == identity.WindowHandle);
                if (registered != null
                    && !ComputerUseWindowFocusPolicy.IsUnchanged(registered, identity))
                    return AddResult.DifferentWindow;
            }

            foreach (ComputerUseGuiFinalProof existing in _proofs)
            {
                if (!existing.SameControlAs(proof)) continue;
                return existing.SameExpectationAs(proof)
                    ? AddResult.AlreadyRecorded
                    : AddResult.ConflictingExpectation;
            }

            if (_proofs.Count >= MaxControls) return AddResult.CapacityExceeded;
            _proofs.Add(proof);
            if (!_strictSingleWindowOnly && !_windows.Exists(x =>
                    x.ProcessId == identity!.ProcessId
                    && x.WindowHandle == identity.WindowHandle))
                _windows.Add(identity!);
            return AddResult.Recorded;
        }

        internal void Invalidate()
        {
            _proofs.Clear();
            _windows.Clear();
            _strictSingleWindowOnly = false;
        }

        /// <summary>All distinct asserted controls are re-evaluated against the
        /// SAME fresh observation on the SAME original foreground PID/HWND.
        /// A change to ANY control makes the entire batch fail, not just the
        /// last assertion the model chose to show the verifier.</summary>
        /// <summary>Readback policy only: the executor separately owns OS
        /// PID/HWND/EXE reattestation and current allowlist checks before/after
        /// every read. Never accept missing/duplicate target observations.</summary>
        internal bool RecheckAllWindows(
            IReadOnlyList<WindowsUiTreeObservationService.Observation>? fresh)
        {
            if (_strictSingleWindowOnly || _proofs.Count == 0 || _windows.Count == 0
                || fresh == null || fresh.Count != _windows.Count)
                return false;

            foreach (ComputerUseWindowFocusPolicy.Candidate target in _windows)
            {
                int matches = 0;
                foreach (WindowsUiTreeObservationService.Observation observed in fresh)
                {
                    if (observed.HasEvidence && target.ProcessId == observed.ProcessId
                        && target.WindowHandle == observed.WindowHandle)
                        matches++;
                }
                if (matches != 1) return false;
            }
            foreach (ComputerUseGuiFinalProof proof in _proofs)
            {
                bool found = false;
                foreach (WindowsUiTreeObservationService.Observation observed in fresh)
                {
                    if (proof.MatchesWindow(observed.ProcessId, observed.WindowHandle))
                    {
                        if (!proof.Recheck(observed)) return false;
                        found = true;
                    }
                }
                if (!found) return false;
            }
            return true;
        }

        internal bool RecheckAll(WindowsUiTreeObservationService.Observation? fresh)
        {
            if (_proofs.Count == 0 || fresh is not { HasEvidence: true }
                || _windows.Count > 1)
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
