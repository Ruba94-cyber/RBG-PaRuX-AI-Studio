# Contributing to RBG PaRuX AI Studio

Thanks for considering a contribution to **PaRuX**, a source-available (FSL-1.1-MIT), local-first AI operating environment.

## Read first

Before changing code, read:

1. `CLAUDE.md` — engineering contract and non-negotiable product constraints.
2. `PARUX_EXECUTION_PLAN.md` — the **single current operational roadmap**.

Retained historical PaRuX/vNext evidence must not be used to choose current priorities or to resurrect work that current code has already completed. Removed archives are not prerequisites for contributing.

Truth order:

`current code > current tests > real runtime evidence > PARUX_EXECUTION_PLAN.md > historical docs`

## Non-negotiables

- **Core AI inference stays local.** No cloud AI provider may become a required inference runtime or fallback path. Internet access as an explicit tool for web search, documentation, model/runtime/package downloads and updates is allowed; core inference must continue locally after required assets are installed.
- **Zero manual stack assembly is the normal path.** PaRuX should detect what the user already has, let the user locate an existing compatible component, or install/manage a PaRuX-owned copy.
- **Ownership is explicit.** `PARUX_MANAGED`, `USER_MANAGED`, `SYSTEM_DETECTED`. Never silently update, repair, remove or take ownership of user-managed software.
- **Audit before rewriting.** Extend current services/contracts/stores instead of creating parallel subsystems unless the existing architecture genuinely cannot support the requirement.
- **No fake progress.** No reachable happy path may silently do nothing. Compile-only, mock-only, UI-only or placeholder implementation is not DONE.
- **Autonomy is bounded.** Agent/tool/process execution must have cancellation, finite retries/timeouts where applicable, observable state and honest failure handling.
- **Gaming Mode remains a hard-off boundary.** New local-AI jobs/runtimes must respect the existing suspend/release semantics.
- **Chat remains Home.** Advanced functionality should become discoverable first-class workspaces through progressive disclosure, not replace Chat with a generic dashboard.
- **License-check before reusing code.** If code is adapted from another project, record source/revision/license and what changed in `OPEN_SOURCE_PROVENANCE.md`. If the license is unclear or incompatible, reimplement independently from documented behavior instead of copying.
- **Android is a companion with narrower privileges.** Desktop-only capabilities must be denied server-side, not merely hidden in the Android UI.

## Development workflow

1. Start from current `main`; inspect `git status`, HEAD, recent changes and the relevant existing implementation.
2. Verify the gap against current code/tests before implementing it.
3. Make the smallest coherent end-to-end change that closes a real product or acceptance gap.
4. Add focused tests plus relevant security/error/cancellation/restart coverage.
5. Run the strongest applicable build/test/runtime verification available in your environment.
6. Never describe an unexecuted test as passing. Record hardware/Windows/Android-specific verification as pending when it genuinely could not be executed.
7. Keep `PARUX_EXECUTION_PLAN.md` concise. Put long chronological evidence in commits or dedicated execution evidence, not back into a mega-plan.

## Persistent state

When adding persistent user state:

- use the existing natural owner/store when possible;
- if a new store is truly necessary, include it in Backup/Restore in the same change;
- preserve user data across schema upgrades;
- define ownership and deletion semantics clearly;
- test backup → mutate/delete → restore → restart where practical.

## Managed downloads and runtimes

For PaRuX-managed components:

- prefer deterministic locations and versions;
- validate downloaded content/provenance/integrity where the upstream provides suitable evidence;
- use bounded, cancellable downloads/processes;
- support honest failure/recovery/rollback semantics appropriate to the component;
- check existing PaRuX-managed inventory and compatible located/system installs before downloading duplicates.

## Skills, MCP and tools

A built-in skill/tool must have a real backend behind it. Permissions declared in manifests/policy are security boundaries, not UI hints. Imported content and web content are untrusted input and must never gain implicit shell/filesystem/network privileges.

## Tests

New server-side behavior should normally get xUnit coverage under `tests/PaRuX.Server.Tests/`, using existing real-store/WebApplicationFactory patterns where appropriate.

Security-relevant changes (auth, SSRF, path handling, archive extraction, downloads, runtime installation, permissions, shell/tool execution, LAN pairing) need regression tests whenever the behavior can be exercised hermetically.

A capability is not considered complete solely because it compiles. Depending on scope, verification should cover:

- focused unit/integration tests;
- relevant regression tests;
- real API/service path;
- invalid input and unavailable dependency behavior;
- cancellation/timeouts;
- persistence/restart/restore behavior;
- ownership/security boundaries;
- Windows/Android/hardware-specific evidence when the feature depends on them.

## Pull requests and commits

Keep changes reviewable and avoid unrelated refactors. A useful PR description states:

- the problem being solved;
- architecture/ownership decisions;
- tests actually executed and their result;
- validation that could not be executed and why;
- migration/backup/security/compatibility impact.

Prefer focused commit messages that explain the reason for a change rather than merely restating the diff.

## Security issues

Do not publish an exploitable vulnerability in a normal public issue. Follow `SECURITY.md`.
