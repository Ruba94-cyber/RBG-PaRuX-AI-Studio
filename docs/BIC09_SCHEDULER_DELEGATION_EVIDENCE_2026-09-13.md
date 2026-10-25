# BIC-09 scheduler and editor-review evidence — 2026-09-13/14

Status: **PARTIAL**. This note records verified software corrections and does not close BIC-09.

## Verified scheduler issue

At `main` commit `2357095c9803755dcdeeb2c3a6b136e36ccd93d2`, `AgentOrchestrator.RunSchedulingPass()` counted every `waiting` task against `ExecutionResourceBudget.MaxParallelTasks`. The durable delegation flow deliberately moves a delegating parent to `waiting` while its children remain `queued`/active, and `AgentWorkerService.ResumeWaitingDelegators()` resumes that parent only after all direct delegated children are terminal. Under a saturated profile this can starve the very delegated child that must finish before the parent can resume.

## Implemented scheduler correction

Commit `f74c89f5445db8d412bc56dc5346b0b3e80bc880` changes only scheduler slot accounting:

- running and verifying tasks still consume a global execution slot;
- ordinary waiting tasks still consume a slot;
- a waiting task releases its slot only when its stage is the Core-owned `Waiting for delegated child ...` state and at least one direct delegated child is non-terminal;
- the parent remains waiting and performs no concurrent inference while the child uses the released capacity;
- once all direct children are terminal, the parent no longer receives this exemption and the existing resume path remains authoritative;
- production profile resolution remains unchanged; a dependency-injected profile resolver was added only to make scheduler budget behavior testable without mutating global profile state.

This is intentionally narrower than generic parent/child concurrency. It does not weaken capability scoping, worktree isolation, STOP/cancellation, Gaming hard-off, pairing, authentication, inference locality or USER_MANAGED ownership rules.

## Added scheduler behavioral coverage

`tests/PaRuX.Server.Tests/AgentOrchestratorDelegationSlotTests.cs` adds two regressions:

1. with a Fast profile (`MaxParallelTasks = 2`), one unrelated running task plus one delegation-waiting parent still permits its queued delegated child to dispatch, while the parent remains waiting;
2. an ordinary waiting task plus one running task still saturates the same profile and prevents unrelated queued work from dispatching.

These C# tests were **added but not executed in the current environment** because the available runner does not contain the .NET SDK. They must be run with the repository's normal .NET test command before this slice can be treated as runtime-verified.

## Verified editor-review persistence defect — 2026-09-14

`integrations/vscode/review-findings.js` previously included the review task id in a finding's persisted identity. The same semantic finding emitted by a later review task therefore received a different id. A previously human-resolved or dismissed finding could be duplicated as a new open finding instead of preserving the disposition across review reruns.

A focused regression was added before the fix: `repeated review from a new task preserves disposition instead of duplicating the finding`. Against the pre-fix implementation the review-findings suite produced **5 passed / 1 failed**; the failing assertion observed two persisted findings where one stable semantic finding was expected.

Commit `72cf53f8f197caa2fe5a56a6e960395c588ce7dc` introduces a task-independent semantic identity using source/hunk content identity, anchors, line count, normalized severity and finding message. `taskId` is retained as provenance but no longer determines persistence identity. Existing legacy records are reconciled by the same semantic key, preferring an existing human disposition over a duplicate open record and preserving its original creation time.

After the correction the same suite completed **6 passed / 0 failed**. The GitHub-published blobs are the exact blobs used for the passing run:

- `integrations/vscode/review-findings.js` — `35f5e2905db1ca903b5dccb0e7d469ce55a9d602`
- `integrations/vscode/test/review-findings.test.js` — `6eb3411458d05c6838d80b2add314a8688ad0603`

## Other locally executed BIC-09 evidence

The available Node.js runner is `v22.16.0`. Against byte-for-byte GitHub blobs from the audited branch, the following command completed with **17 tests passed, 0 failed**:

`node --test integrations/vscode/test/core-client.test.js integrations/vscode/test/hunk-identity.test.js integrations/vscode/test/hunk-review.test.js`

The covered behavior includes loopback/token Core client constraints, multi-repository/resume payloads, hunk relocation after unrelated edits, fail-closed handling of changed/ambiguous hunks, and stage/revert after revalidation. Together with the focused 6/6 review-findings run, this is useful deterministic BIC-09 evidence, but it is not a substitute for a real VS Code extension-host/native editor run.

## Remaining BIC-09 acceptance work

BIC-09 remains **PARTIAL** pending at least:

- execution of the new C# scheduler regressions and broader `.NET` suite on a compatible SDK;
- a real VS Code extension-host/editor interaction pass for moved/deleted lines, overlapping edits, partial accept/reject, restart and error recovery;
- representative same-machine successful coding-task measurements separating inference, reads/searches, builds, queueing and reloads;
- confirmation that task-time improves without correctness, resource-budget, STOP or locality regressions;
- competitor/reference comparison under the canonical plan's equivalent-task/evidence rules.

No benchmark, physical-device result, Windows validation, signature or artifact result is claimed by this note.
