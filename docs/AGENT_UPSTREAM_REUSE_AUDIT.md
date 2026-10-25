# PaRuX Agent Platform V2 — upstream reuse audit

**Reviewed:** 2026-10-10  
**Status:** design and integration research; this document does **not** certify a successful local build or a production release.

## Selection rule

Integrate a feature when it improves measured reliability, autonomy or performance beyond PaRuX's existing local-first stack. Prefer PaRuX's current C#/Windows/AppContainer + Core data/permissions paths over a second unsupervised agent. A third-party GitHub repository is a source of implementation evidence, **not** an automatic authorization to copy code, execute binaries or change license. Pin a revision and keep original LICENSE/NOTICE/attribution before bundling any foreign source.

| Upstream and reviewed revision | Verified license | Relevant implementation examined | PaRuX action |
|---|---|---|---|
| **Jan** [janhq/jan](https://github.com/janhq/jan), `f2cd837fabe99cd6b8bbe985891bab8782e4034b` | **Apache-2.0** in root LICENSE, Menlo Research attribution requested | `docs/src/pages/docs/agent/context.mdx` (live exact vs fallback token counts; context headroom; stable prompt prefixes), `docs/src/pages/docs/agent/permissions.mdx` (Windows AppContainer, stripped secret env, scoped tools), `adk/python/jan_adk/session.py` (bounded event streams, late call cancellation), sessions/worktrees docs | PaRuX already has real managed tokenizer count, worktrees, permissioned sandbox and durable checkpoints. Keep those, add clear runtime provenance and avoid inventing a second model engine. Further UI usage/headroom metrics may follow after on-PC acceptance. |
| **Odysseus** [odysseus-dev/odysseus](https://github.com/odysseus-dev/odysseus), `a8c147b238db01dbd00b57774ed8b453f1e43971` | **AGPL-3.0** | `src/agent_runtime/journal.py`, `authority.py`, `completion.py` and `docs/AGENT_TURN_CONTRACT.md`: distinguish proposed, admitted, dispatched, observed and verified; owner-scoped grants, no fake success | Implemented an **independent original C# write-ahead journal** in `CodingAgentCheckpointStore.cs` and integrated it into `CodingAgentService`. Persist uncertain tool intent BEFORE dispatch, reconcile crash without repeating side effects, invalidate verification, and require repo inspection. No Odysseus Python copied, bundled or linked; AGPL adoption would need a separate compliance decision. |
| **OpenCode** [anomalyco/opencode](https://github.com/anomalyco/opencode), `055d95bb7e278c94baf06235a52cac79dd13ba67` | **MIT** | Local server/session APIs and agent approval/routing | Existing desktop-owned *read-only* health/session observer + native coding-backend abstraction. External code execution remains unavailable until an OS-confined process can enforce PaRuX permission/STOP/workspace boundaries. No code imported. |
| **Browser Use** [browser-use/browser-use](https://github.com/browser-use/browser-use), `c75e8476e26d18b7617643bc2ae082fae8eae431` | **MIT** | `browser_use/browser/watchdogs/security_watchdog.py`: URL checks before navigation and after redirects, separate from model intent | PaRuX already has dedicated CDP browser, URL admission and false-success handling. **Implemented, NOT runtime-accepted:** post-navigation redirect checks and page-target CDP Fetch request interception; still missing kernel-level egress validation and enforcement for popups/new targets. Do not claim the existing host resolver rule alone is an enforced egress firewall. No code imported. |
| **Microsoft WinApp CLI** [microsoft/winappCli](https://github.com/microsoft/winappCli), `57aee35fcd083c0e892bcd8a03ac9ad16e372c08` | **MIT** | Windows UIA inspect/invoke/set-value/wait-for CLI | Existing optional operator-installed WinApp CLI bridge with foreground HWND, typed observed selectors and independent verifier. No binaries/source bundled. Windows tests unrun. |

## Follow-on hardening inspired by upstream source inspection

- **Jan, scoped project context:** `CodingProjectInstructionResolver.Collect` now admits explicitly path-shaped files named in the task goal *before* the first edit. An unmodified `src/module/File.cs` can therefore contribute its ancestor `AGENTS.md` / `CLAUDE.md` on step one instead of appearing only after Git sees a change. Only workspace-relative paths are considered; existing no-symlink checks and one-document-per-directory precedence still apply. The compiler passes the user's task text into this filter. Tests cover first-step visibility, sibling separation, attempted escape and symlinks. The C# regex and rules are original PaRuX code, not copied Jan Rust.
- **Browser Use and CDP official protocol:** `BrowserDevToolsService` now installs a **Fetch.requestPaused request-stage interceptor** on the dedicated page target. `BrowserRequestAdmissionPolicy` authorizes HTTP(S)/WS(S) loopback while offline, rejects external requests and credential-bearing URLs, and rechecks Internet for each request on online sessions. Offline-launched sessions use their already-stricter launch mode without making a redundant Core HTTP call for every CSS/JS resource. CDP `Fetch.failRequest` returns `BlockedByClient` on unauthorized network attempts; a page redirect cannot inherit the original local URL grant. The old post-navigation URL check remains, and new `BrowserDevToolsNavigationResultPolicy` rejects protocol-level errors/downloads and observed blocked network requests. Tests cover both policy layers.
- **CDP lifecycle and evidence privacy:** pending command completions now clear even when acquiring the send lock or WebSocket send throws, preventing memory leaks under interception load. `BrowserNetworkEvidencePolicy` also strips URL userinfo, query strings and fragments from intercepted network evidence before the agent sees it; DOM extraction fails on CDP JavaScript exceptions instead of emitting fabricated success.
- **Limits:** CDP Fetch currently attaches to the designated page target. Popups/new targets, worker/WebSocket behavior, actual DNS/IP egress, and dynamic policy switching require a dedicated Windows run with network tracing. **Do not claim that this is a kernel-enforced network firewall or end-to-end browser acceptance.** If interception initialization fails, `_fetchGuardReady` stays false and the next tool call cannot reuse that unguarded session. No Browser Use code or binaries imported.
- Upstream reference: https://chromedevtools.github.io/devtools-protocol/tot/Fetch/ and https://github.com/browser-use/browser-use ; only protocol commands and original .NET C# used.

## Actual original-code implementation in this review

- `CodingAgentDispatchJournal.Begin`: record an impending potentially mutating coding action in the normal on-disk checkpoint *before* execution. Unknown/repeated action state refuses a conflicting dispatch.
- `CodingAgentDispatchJournal.Complete`: clear pending state only as the tool observation is committed to the existing durable checkpoint.
- `CodingAgentDispatchJournal.ReconcileInterruptedAction`: if a previous process died between these boundaries, consume the uncertain step, downgrade verification, and add an explicit inspection/revalidation instruction rather than falsely claiming success or blindly repeating the mutation.
- `CodingAgentService` persists the receipt before side effects. Read-only, repeat-guarded actions do **not** pay for a duplicate checkpoint write; the native coding loop and AppContainer stay intact.
- xUnit regression cases cover persisted pending-state restart, denied overlapping dispatches, verification invalidation and completed-action cleanup. These are **new tests not yet executed** in the available environment.

## Original-code follow-on: browser privacy and task isolation

Follow-on source inspection of the native `CodingAgentService` /
`BrowserDevToolsService` revealed two local defects independent of
which third-party browser agent we adopt: `browser_type` returned the entered
field value to the agent, and the globally shared CDP process retained a browser
profile across unrelated coding sessions.

Original PaRuX C# implementations now:
- build click/type scripts with escaped literals and no reflected values;
- use strict `ok` classification and predefined error messages instead
  of emitting raw CDP results (including JS errors containing form inputs);
- deny recognized sensitive form fields and invalid/non-editable targets;
- keep an exclusive coding-task browser lease through process shutdown and
  use fresh GUID-named browser profiles with best-effort deletion;
- redact successful navigation URLs in task-visible evidence.

The added xUnit cases cover policy shape, refusal and ownership races. A separate original scoped `CodingTaskRunLease` now rejects concurrent same-session checkpoint executors within one PaRuX process; cross-process locking remains unverified.
These changes do **not** copy Browser Use, Jan or Odysseus code and do not add
third-party packages, runtime processes or license obligations. Browser Use
remains a source of design comparisons under its MIT license; AGPL Odysseus
remains reference-only. Actual Windows/CDP tests, process cleanup and physical
egress isolation are unverified and explicitly remain open.

## A5 browser assertion slice — no third-party code imported

Original PaRuX C# `BrowserDomAssertionPolicy` generates exact postcondition
checks for local web-app work, with JSON-string-escaped literals, fixed boolean
results, and a strict evidence classifier. It rejects credential and non-text
input value inspection, avoiding raw page values in agent observations. A
bounded CDP polling path checks actual DOM state after interactive commands.
`BrowserUiMutationPolicy` keeps click, typing and arbitrary JavaScript
local-only until task-scoped remote-site grants can be enforced. Process-exit
confirmation prevents release of browser isolation while a process remains
alive. A dependency-free Node test exercises the generated JS on synthetic
DOM, in addition to original xUnit tests. These changes implement concepts
from general browser-automation patterns but copy NO Browser Use/OpenCode/Jan
source. Browser/Windows runtime tests are NOT RUN here.

## Decision log

1. **Do not clone Jan's desktop or inference runtime into PaRuX.** It overlaps model management, GPU/runtime lifecycle, conversation/history and settings; a second model server would increase memory use and ambiguity over which model served a coding task.
2. **Do not vendor Odysseus Python modules into PaRuX.** They are AGPL-licensed and operate under a different runtime/authority model. A distinct C# implementation using architectural ideas avoids unnecessary licensing coupling.
3. **Do not run OpenCode unrestricted just because its HTTP API is reachable.** External agent health is not evidence of an AppContainer or task permission boundary.
4. **Do not treat UI/browser action delivery as an independent test result.** Follow verified-state-change semantics, with actual evidence and independent postconditions.
5. **Record every pending, unrun or unverified gate as such.** Upstream docs and prior benchmark numbers are research, not local PaRuX acceptance evidence.

## Remaining before claiming software complete

- Build/compile on a Windows-capable development environment and run the new targeted test suite before merging the growing feature branch.
- Add a confined *executing* OpenCode adapter only when executable identity, workspace scope, inherited environment, job-object STOP and provider/session permissions are enforced outside the model.
- Add browser post-redirect and new-tab policy gates at the CDP event boundary; verify local-only egress isolation on Windows with actual network probes.
- Complete code → build → launch → UIA assertion → independent verification evidence pipeline, benchmark the same tasks, and close existing BIC rows.
- Execute physical PC GPU/model/long-context/installer acceptance last.

**Source provenance:** file paths and revision hashes above are snapshots of the public upstream repositories as examined on the review date. No third-party source was copied verbatim by the work in this audit.
