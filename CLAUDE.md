# CLAUDE.md — PaRuX Development Contract

## Mission
Finish RBG PaRuX AI Studio (PaRuX) into a best-in-class source-available local-AI operating environment. Claude/Codex are development tools only; shipped core AI operation must not depend on proprietary cloud inference.

## ONE canonical execution plan
Read and maintain exactly one operational plan:

`PARUX_EXECUTION_PLAN.md`

Do not use archived PaRuX/vNext/early-PaRuX plans, release checklists or execution logs to choose the next task. They are historical evidence only in Git history or retained legacy evidence files.

Truth order:
1. CURRENT WORKING CODE
2. CURRENT TESTS
3. REAL RUNTIME / LIVE EVIDENCE
4. `PARUX_EXECUTION_PLAN.md`
5. HISTORICAL DOCS

Before substantial work:
- inspect branch, HEAD, git status, recent log and local diff;
- read only relevant sections of `PARUX_EXECUTION_PLAN.md`;
- verify the first actionable incomplete item against current code;
- preserve unrelated user/agent changes;
- if another writer is active, isolate work deliberately.

## Core product constraints
- Local-first/local-only core AI inference.
- Chat is the default/home workspace.
- Advanced capabilities become first-class workspaces with Normal vs Advanced/Studio disclosure.
- PRX Orbit is the approved identity direction.
- Component setup is Detect → Locate existing / Install with PaRuX / Skip.
- Ownership: PARUX_MANAGED / USER_MANAGED / SYSTEM_DETECTED.
- Never silently mutate USER_MANAGED software.
- Runtime Manager owns components/runtimes; Model Hub owns models; Update Manager coordinates PaRuX-managed updates.
- Missing internal capability is implementation work, not an external blocker.

## Engineering behavior
- Audit before coding; reuse existing services/contracts/stores.
- Preserve auth, pairing, LAN security, Gaming hard-off, cancellation, backup safety and user data.
- No fake success. Compile-only/UI-only/mock-only is not DONE.
- Autonomous work must have bounded retries, cancellation, observable state and honest failures.
- Prefer small coherent end-to-end slices over broad speculative refactors.
- Keep the active Execution Plan concise; long evidence belongs in commit messages or historical execution logs.

## Definition of Done
A capability is DONE only when applicable implementation, focused tests, regression tests, real integration, failure/recovery paths, platform wiring, security/resource review and status reconciliation are complete.

Allowed status: DONE / PARTIAL / MISSING / BLOCKED-EXTERNAL.

## Long-run rule
After each verified batch: update `PARUX_EXECUTION_PLAN.md`, commit coherently, then continue to the next highest-priority actionable gap. Do not stop merely because the roadmap is large or an internal capability is missing.
