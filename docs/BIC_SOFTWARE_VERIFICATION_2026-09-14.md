# Software-only verification checkpoint — 2026-09-14

Audited branch before this checkpoint: `chatgpt/bic09-delegation-slot-20260913` at `72cf53f8f197caa2fe5a56a6e960395c588ce7dc`.

This document records only checks actually executed in the current Linux runner and separates them from Windows/.NET/hardware/publication acceptance. It does not promote any BIC item to DONE.

## Environment

- Node.js: `v22.16.0`
- Python: `3.13.5`
- Git: `2.47.3`
- OS: Debian 13 Linux container
- .NET SDK/MSBuild/PowerShell: **not available**
- shell network access required to install the .NET SDK: unavailable in this runner (DNS/download attempts failed)

GitHub Actions were not created, modified, enabled or dispatched.

## BIC-09 — coding orchestration/editor review

Actually executed:

1. `core-client.test.js`, `hunk-identity.test.js`, `hunk-review.test.js`: **17 passed / 0 failed** against byte-for-byte GitHub blobs.
2. New review-finding rerun regression reproduced the defect before the fix: **5 passed / 1 failed**, with the same finding duplicated when the review task id changed.
3. After commit `72cf53f8f197caa2fe5a56a6e960395c588ce7dc`, the focused review-findings suite passed **6 / 6**. Published source/test blob identities match the locally tested blobs.

Not executed here:

- the C# scheduler regressions added by `f74c89f5445db8d412bc56dc5346b0b3e80bc880`;
- the broader .NET suite;
- VS Code extension-host/native-editor interaction;
- same-machine coding-agent comparisons against competitors.

Status remains **PARTIAL**.

## BIC-14 — narrow-screen browser conversation navigation

Actually executed against exact branch files and verified source blob identities:

`node tests/webclient-mobile-conversation-patch.test.mjs`

Result: **`webclient mobile conversation drawer contract: PASS`**.

The contract checks accessible drawer state (`aria-controls`/`aria-expanded`), Escape/focus behavior, conversation-list interaction, narrow-screen CSS/drawer behavior including reduced motion, endpoint injection of the mobile resources, and project resource inclusion.

This is a deterministic browser-client contract check. It does **not** substitute for a served-browser paired session, Android emulator/instrumented pairing/revocation/reconnect, artifact retrieval, STOP/cancellation or permissions acceptance.

Status remains **PARTIAL**.

## BIC-15 — persistence/locality/listener boundary

Current source includes `scripts/verify-sqlite-pool-isolation.mjs`, which rejects process-wide `SqliteConnection.ClearAllPools(` uses in `src`/`tests`, and repository search did not identify a current `ClearAllPools` occurrence. However the verifier was **not executed over a complete checkout in this runner**, so no PASS is claimed for that script.

The .NET/live Windows concurrency and security tests remain required for database-scoped pool cleanup, strict-local cloud-fallback refusal, and listener ownership/race behavior.

Status remains **PARTIAL**.

## Other BIC software gates blocked in this runner

The repository contains focused C# coverage for several reopened areas, including Ollama compatibility (BIC-05), Windows ML cancellation/protocol behavior (BIC-08), semantic-index scale/equivalence (BIC-10), tokenizer identity/rendered prompt counting (BIC-11), native workbook preservation/semantics (BIC-12), and SQLite/listener/cloud-fallback behavior (BIC-15). Those suites were not executed here because the required .NET toolchain is absent and could not be installed from this environment.

This is a software-verification environment blocker, not physical-hardware acceptance. Existing committed tests must still be run on a compatible .NET/Windows environment; their mere presence is not recorded as PASS.

## Physical/external acceptance still distinct

No claim is made in this checkpoint for:

- packaged WinUI/UI Automation acceptance;
- real GPU/NPU provider assignment, Engine Race or pressure/thermal behavior;
- media/audio/training execution quality;
- real paired Android workflow;
- signing credentials/signed update candidate;
- same-machine competitor performance/task-quality results required for release claims.

The canonical status therefore remains **PARTIAL — active software gaps; PHYSICAL_PC_ACCEPTANCE_REQUIRED**. This checkpoint narrows the verified software surface but does not restore any blanket `SOURCE_SOFTWARE_COMPLETE` claim.
