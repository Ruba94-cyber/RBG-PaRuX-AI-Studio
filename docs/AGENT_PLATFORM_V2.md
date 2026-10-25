# PaRuX Agent Platform V2 — implementation plan and evidence ledger

Status: **IN PROGRESS / not release-accepted**. Integration branch: `feature/parux-agent-platform-v2`.
Source-of-truth order remains: current code > current tests > real runtime evidence > `PARUX_EXECUTION_PLAN.md` > historical reports.

## Goal and non-goals

Turn the existing PaRuX native coding agent into a durable local-first agent that can research, edit an isolated Git worktree, execute allowed tools, inspect and test *actual* Windows UI, verify the outcome and resume from bounded context after interruption. Maintain Simple/Advanced, existing Windows Core/App + Android companion, user-managed runtime ownership, exact per-task grants, local inference defaults, STOP/Gaming hard-off and all current production APIs.

Do **not** create another unrestricted desktop shell, change an existing user-managed installation, auto-push Git changes, silently install models/runtimes, route secret-bearing material to cloud by default, or claim completed testing from a build-only result.

## Implementation order (dependency-gated)

| Phase | Work | Acceptance |
|---|---|---|
| A0 — Baseline | Work on a separate branch; preserve `main`; record outstanding BIC items and production invariants. | No change to released artifacts or user data; identifiable commits and test status. |
| A1 — Context survival | On the existing `CodingAgentService` path, check final prompts against observed per-slot context + generation reserve, compact only reconstructible evidence, persist the previous completed step when no request can fit. Then implement semantic checkpoint/snapshot on top of the existing checkpoint and Virtual Context stores, and bounded recovery after provider context-overflow signals. | Unit tests for budget arithmetic, replay reduction, oversized task fail-closed, lossless durable state, restart and cancellation; real coding task crosses an artificially limited context without repeating an already completed edit. |
| A2 — Interchangeable agent runners | Introduce a `IAgentExecutionBackend` boundary beneath Core authority: native runner first; OpenCode as an opt-in, pinned/versioned *external process*, never as a replacement for PaRuX Core auth. Preserve user-selected worktrees, cancellation, a canonical progress stream, step budgets and verifier. | On the same model and disposable repository, both runners produce comparable task evidence; no bypass of grants, network policy or file-scope isolation. |
| A3 — Windows Computer Use | Windows-owned execution broker: UIA/FlaUI and optionally WinApp CLI first; vision-assisted fallback only for non-accessible controls. Record target window/process, screenshot, action, before/after UIA state and exact approval. Keep real UI tests deterministic; run visual interactions only on an unlocked interactive desktop or isolated VM. | Open Notepad, use a test WinUI app, save a value, verify via independent UI inspection; focus/error/restart recovery; no uncontrolled access to UAC or unrelated windows. |
| A4 — Internet / browser | Keep SSRF/network-grant policy; add pluggable search with typed sources and provider health, Playwright for deterministic web/UI tests and optional Browser Use for exploration. Treat page content as untrusted data. Do not scrape or inject full pages into LLM context. | User-enabled search, source provenance, graceful provider failure, redirect/SSRF/prompt-injection regression; permitted browser test exercised on real browser. |
| A5 — One chat-to-verification flow | Drive native plan → (research, coding, UI inspection) → build → unit tests → FlaUI/Playwright → independent verifier → inspectable diff → explicit adoption. Implement actionable native progress UI with accessibility IDs, STOP and approval cards. | Real disposable Windows .NET app: one prompt produces correct code + actual UI verification + approved diff, fully reproducible with failure evidence. |
| A6 — Quality and release | Evaluate 30–50 mixed tasks against identical local model/revision/quantization/context where possible; track solved-task rate, p50/p95 time, tool failures, human interventions, VRAM, context failures, STOP/restart correctness. | Physical PC acceptance and the pre-existing release-gate pass; no false `BEST_IN_CLASS`, no public release from this branch until all relevant BIC gates close. |

Do not claim any phase complete before its tests and production acceptance have been executed. Keep GitHub Actions disabled according to the canonical project rules.

## First implemented slice (A1)

The existing coding agent already retains observations, artifacts and plan in `CodingAgentCheckpointStore`, compiles repository excerpts with `CodingContextCompiler` and retrieves durable memory from Virtual Context. The initial V2 change adds an independent **final assembled prompt admission** in `CodingAgentPromptBudgetPolicy` / `CodingAgentService`.

* Start from the runtime-observed context per request (not the configured total shared across parallel slots).
* Reserve hidden reasoning and visible action-generation space, plus a framing margin.
* If the *assembled* system + schema + user prompt exceeds the conservative budget, first shrink replayed observations and memory, then compile a smaller repository evidence packet.
* Keep task instructions, tool protocol, saved checkpoints, long tool artifacts and verification gates intact.
* If four bounded attempts cannot fit, pause **before inference** with a recoverable checkpoint and explicit actionable error, never invent success or silently skip the unexecuted step.
* Unknown context retains prior behavior; the character-to-token bound is a heuristic, not an exact tokenizer proof. Add a server-owned exact-token feasibility probe in a follow-up without violating the desktop/Core API boundary.

### A1 exact rendered-token preflight — implemented; real provider acceptance NOT RUN

The existing Core `RuntimeTokenCounter.TryCountRenderedChatPromptAsync` already talks to managed llama-server's chat `/v1/chat/completions/input_tokens` endpoint. Rather than copying a tokenizer into the Windows app, Agent Platform V2 now uses that existing component:

- `CodingRenderedPromptCountContract` builds a bounded `parux-coding` request with system and user messages and the SAME JSON response schema as `StructuredChatClient`.
- `LocalApiServerService.TryCountRenderedCodingPromptAsync` prepares the request through the existing model-role gateway, which resolves the actual coding model, then asks its resident tokenizer for a rendered chat-template count. It returns no count for unsupported/external providers; it does not generate an action.
- `POST /api/v1/coding/prompt-token-count` is limited to authenticated desktop-owner loopback. It returns only `inputTokens`, `exact` and a source label; neither task instructions nor schema content are logged or returned.
- `CodingRenderedTokenProbeClient` is used in `CodingAgentService` **before** every inference: after a successful exact count it invalidates the stale /props cache, reads the current coding model's observed context, then requires `inputTokens + reserved output/reasoning tokens <= context`. A too-large prompt enters the existing bounded, lossless recovery sequence. If a provider cannot count, the prior conservative character gate remains the fallback.
- New contract, reserve arithmetic and endpoint-auth tests cover JSON shape, safe sizes, denial of anonymous callers and no-model-loading rejection of invalid input. All are included in `tools/test-agent-platform-v2.ps1` but **NOT RUN** in this execution environment.

A token count is exact **for the managed runtime's rendered template and selected resident coding model at the moment of the probe**, not a proof that third-party remote providers or future model swaps use the same tokenizer. Physical test cases must include first-turn role swap, 8K and 32K contexts, enabled schema, overlarge repository evidence, provider refusal and restart. This is not yet release-accepted.

### A1 continuation — implemented, NOT accepted

- `CodingAgentPromptBudgetPolicy.IsProviderContextOverflow` classifies only explicit context-window errors; authentication, rate limit, transport and output-length errors do not enter the retry path.
- The production `CodingAgentService` catches a matching `LocalAiException` before executing an action and retries exactly once with the original task/system/schema, bounded repository evidence and a compact recovery digest. A second overflow pauses, preserving the last executed step. Cancellation and other errors keep existing behavior.
- `CodingAgentRecoveryDigest` is **deterministic** (derived from the checkpoint's actual observations and verification booleans), bounded, and recomputed on save/load rather than trusting a stored claim. It is not a free-form LLM summary or independent success proof.
- A zero-character replay budget now produces **zero** recent observation entries instead of inserting empty records/delimiters.
- Unit tests cover error classification, budget recovery, derived digest and read-only replay behavior. **Tests were added, not executed** in this GitHub-only session.

This is a **defensive improvement**, not yet full semantic compaction or demonstrated self-improvement. Pending: real tokenizer counting, resilience to model-context changes during runtime swaps, richer semantic compression and retained artifact links, real provider overflow integration test, physical Windows/.NET tests and GPU dogfooding.

### A2 preparatory OpenCode compatibility probe — implemented, NOT accepted

`src/PaRuX.Server/Orchestration/OpenCodeLocalProbe.cs` now contains a narrow, **read-only** probe for an **already-running** OpenCode instance. It allows only literal `http://127.0.0.1:<port>/` roots and performs one `GET /global/health` request with redirect, proxy and cookies disabled, with a timeout and bounded JSON response. Missing authentication is a distinct unavailable status; a positive health response is labelled **unverified identity** and cannot grant execution authority. Hermetic HTTP-handler tests exercise the endpoint policy, response parsing, oversize rejection and non-200 behavior. Tests are written but **NOT RUN** here.

Official API contract: https://opencode.ai/docs/server/ and https://opencode.ai/docs/sdk/. No OpenCode runtime, SDK code or model weights have been incorporated. The next A2 step is an authenticated, process-attested and Core-permission-scoped external runner with explicit workspace control. Health alone does not make OpenCode an approved execution backend.

### A2 continuation — opt-in OpenCode session observation (implemented; NOT execution)

`OpenCodeSessionObserver` extends the original read-only health probe with a **strict GET-only** bridge to the official OpenCode `/session/{id}`, `/session/{id}/todo` and `/session/{id}/diff` APIs. Every response is length-limited, no redirects/proxies/cookies are followed, the URI and session identifier are validated, and a mismatched session ID fails closed. Only bounded counts (todos, changed files) and observation status leave the adapter; no arbitrary diffs, prompts, credentials or external file paths are returned.

The Core exposes these two desktop-owner-only local endpoints:

- `GET /api/v1/agent-backends/opencode/health`
- `GET /api/v1/agent-backends/opencode/sessions/{sessionId}/observation`

Set `PARUX_OPENCODE_OBSERVE_URL=http://127.0.0.1:4096/` **on the PaRuX Core process** to opt in to an *already-running*, independent OpenCode server. There is no automatic installation, process startup, session creation, chat prompt, tool grant or session mutation. An authenticated/password-protected OpenCode server returns `authentication_required` to the observer rather than automatically exposing its credentials to another localhost process. The observer reports `observed_unverified_authority`, not readiness for code execution.

Offline contract tests cover the exact GET sequence, the session identity, unsafe IDs/endpoints, bounded results, protected servers and unauthorized PaRuX API access. **Tests not run here.**

Sources: https://opencode.ai/docs/server/ (session APIs) and https://opencode.ai/docs/permissions/ (OpenCode tools default enabled; therefore server reachability is not equivalent to PaRuX sandbox authority). Before implementing **executing** OpenCode, an independently enforced operating-system workspace/permission boundary and STOP/Job Object control are mandatory. This is an explicit remaining software gap, not just physical-PC testing.

### A3 preparatory harness — implemented, NOT accepted

`tools/test-winapp-ui.ps1` optionally calls Microsoft's existing WinApp CLI to inspect a running Windows application through UIA and save structured JSON evidence in a local validation folder. It never downloads software or issues mouse, keyboard, shell or write operations; screenshot capture requires the explicit `-CaptureScreenshot` switch. `-OnSandbox` forwards a Sandbox target to every WinApp call.

Examples (on a prepared Windows host with WinApp CLI installed):

```powershell
powershell -ExecutionPolicy Bypass -File tools\\test-winapp-ui.ps1 -AppName notepad
powershell -ExecutionPolicy Bypass -File tools\\test-winapp-ui.ps1 -AppName PaRuX -CaptureScreenshot
powershell -ExecutionPolicy Bypass -File tools\\test-winapp-ui.ps1 -AppName MyTestApp -OnSandbox
```

This is a **read-only UIA smoke harness**, not yet Core-integrated Computer Use and not an automated functional acceptance test. It must be tested on Windows; screenshots remain private, local evidence. Source: https://github.com/microsoft/winappCli/blob/main/docs/ui-automation.md (upstream MIT — command line invocation only, no source vendored).

## A3 implementation slice: optional UIA control in real Computer Use

The existing `ComputerUseWorkerService` continues to own **per-task approval or explicit Full Computer Use**. The existing vision-based `ComputerUseAgentService` now optionally augments its screen observation with a **foreground-process-scoped** WinApp CLI UIA JSON tree:

- `WindowsUiTreeObservationService` invokes only `ui inspect -w <foreground HWND> --interactive --json`, with a bounded output and timeout. The PID and HWND come from Windows, not the language model.
- `ComputerUseActionParser` advertises `ui_invoke`, `ui_set_value` and `ui_wait_for`. No model-supplied command line is evaluated; typed selectors have restricted ASCII syntax and must appear as **exact JSON `selector` property values** in the latest UIA tree; a matching name/label cannot authorize a click.
- `WindowsUiActionService` executes only `ui invoke`, `ui set-value` or `ui wait-for` scoped to the recorded HWND, verifies the same foreground PID and HWND immediately before execution, imposes output/time bounds, and never trusts delivery as proof of user-goal completion.
- `UiVerificationWorkerService` receives supplemental read-only UIA observations in addition to the existing screenshot, without treating either as an independent verdict. The existing verifier remains authoritative.
- WinApp CLI must already be installed, and an operator must deliberately set `PARUX_WINAPP_CLI` to the **absolute path of the actual executable**. No PATH search, automatic installation or privilege escalation. When not configured, existing screenshot-only behavior remains.
- Both success and failure of the optional CLI path must be verified on actual Windows. A task approval remains necessary even with the CLI installed.
- `ComputerUseCompletionGate` requires an uncancelled explicit completion claim before the worker may submit the task to the independent verifier. A failed or interrupted action is never a PASS candidate.
- `CodingAgentService` explicitly invalidates the cached runtime context-size probe after the first coding-model turn so the next step does not retain the previous Chat resident's window for 30 seconds.

Upstream CLI reference: https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/ui-automation. It supports structured UIA access plus screenshots and optional isolated Windows Sandbox targeting. Only external CLI invocation is added; **no source code from the Microsoft repository has been copied**.

## Real functional WinApp UIA acceptance (requires human-selected TEST target)

In addition to the read-only `test-winapp-ui.ps1` smoke, `tools/test-winapp-functional.ps1` performs an **actual UI interaction** against an *already-running, disposable test app* when an operator provides a verified HWND, a specific observed selector, the expected result selector/value, and `-ConfirmFunctionalTest`. It executes exactly one WinApp `invoke` or `set-value`, then a separate `wait-for --value` assertion and an after-inspect. A successful CLI action alone cannot pass: the independent UI state assertion must succeed. Before/action/assertion/after evidence goes into the ignored local artifacts folder; no app launch, arbitrary model-generated commands, unattended execution, or remote grants are introduced. **Not executed in this environment.**

Example:

```powershell
powershell -ExecutionPolicy Bypass -File tools\\test-winapp-functional.ps1 \
    -ConfirmFunctionalTest -WindowHwnd 131906 -Action invoke \
    -Selector btn-run-a1b2 -ExpectedSelector lbl-status-c3d4 -ExpectedValue Done
```

Substitute the real HWND and selectors from a known disposable test app; do **not** use the example values as if they were discovered on a device.

## Windows acceptance runner

Run `tools/test-agent-platform-v2.ps1` on a Windows checkout with .NET SDK. It runs targeted pure tests; `-RunReleaseGate`, `-RunFlaUI` and `-WinAppTarget PaRuX` opt into larger suites. The runner writes `PASS`, `FAIL` and `NOT_RUN` independently to `artifacts/validation/.../validation-status.json`. Real GPU and self-improvement dogfooding retain `NOT_RUN` until independently measured; a partial xUnit success must never mark the entire feature accepted.

Important: this harness is new source, **not executed** in the current GitHub-only session. Runtime CLI syntax, WinUI integration and UIA evidence payloads require real Windows acceptance.

## A4 implemented slice: optional local metasearch with fallback

`src/PaRuX.Server/LocalSearxngSearchProvider.cs` provides an **opt-in** JSON-based metasearch source for an already-running SearXNG server bound to literal IPv4 loopback. Configure `PARUX_SEARXNG_LOCAL_URL=http://127.0.0.1:<port>/` on the PaRuX Core process; without this variable **nothing changes**. On success, `WebSearchService` prioritizes locally returned web-search results; invalid, unavailable or empty local responses fall through to its existing DuckDuckGo HTML/Lite providers. The extra adapter imposes a strict endpoint allowlist, no proxy/cookies/redirects, response-size and result-count limits and filtering of unsafe result URLs. Untrusted search result text never becomes an execution instruction. The existing hardened web-page-fetch SSRF boundary remains authoritative.

No SearXNG server or other third-party source code is downloaded or bundled. Hermetic stub-handler tests cover endpoint restrictions, source parsing, result limit and network fail-closed behavior. **Real Core/Windows tests and a running SearXNG instance remain NOT RUN**. The desired future richer provider policy/user-facing search settings and Browser Use subprocess are still unimplemented.

## A4 coding-agent documentation research — implemented, NOT accepted

The native `CodingAgentService` now exposes a typed `web_search` JSON action with a `search` argument (up to 512 characters). This does not grant network access to `run_command` or the AppContainer:

1. `CodingWebSearchClient` uses the existing authenticated Core owner token on the local control plane only; it never sends the token to an external website.
2. The desktop-only `POST /api/v1/coding/web-search` route explicitly requires loopback **and** desktop-owner authentication, **and** the existing user-enabled PaRuX Internet setting. It combines the request cancellation with Gaming Mode cancellation.
3. The Core returns at most five bounded titles, URLs and snippets from the existing `WebSearchService`, including its optional local SearXNG JSON provider when configured. Untrusted source snippets are labelled as data, not model instructions.
4. The coding agent saves/searches the returned source data with the pre-existing `CodingAgentArtifactStore` evidence mechanism; returned results do not automatically approve arbitrary package installs, network shell execution, or edits.

Validation: `CodingWebSearchPolicyTests` exercises query/input/result limits; `CodingWebSearchEvidenceFormatterTests` covers untrusted source labelling, unsafe URLs, result caps and empty/malformed responses; `CodingWebSearchEndpointTests` denies unauthenticated API callers. All are included in the Windows acceptance runner. **Not run** here; the actual Core route, documentation search provider and coding-agent dogfood still require executable tests. The independent user Internet toggle and AppContainer no-network guarantees remain authoritative.

## A2 execution backend boundary (implemented; external execution remains blocked)

`AgentCodingWorkerService` now invokes `CodingAgentBackendRouter` instead of binding directly to `CodingAgentService`. `ICodingAgentExecutionBackend` maintains the same task ID, workspace/worktree, progress callback, cancellation and external independent-verifier route. The production `NativeCodingAgentExecutionBackend` delegates unchanged to the existing native implementation. `CodingAgentBackendSelectionPolicy` handles an optional operator development selection `PARUX_CODING_EXECUTION_BACKEND`, defaulting to `native`. Any unsupported backend and `opencode` **fail closed** with an explicit actionable reason rather than silently switching to an unconfined OpenCode process. OpenCode has not yet been entrusted with code execution.

Offline tests cover native-default, denial of external OpenCode and denial of arbitrary executor strings. **Not run on Windows/.NET in this session.** This is a real switch point for future verified backend substitution, *not* the completed optional OpenCode executor.

## Independent failure semantics for browser and UIA (implemented; not runtime-accepted)

`BrowserDevToolsEvaluationPolicy` is used by the dedicated CDP browser to treat a JavaScript `exceptionDetails`, `subtype:error` or an explicit action result `{ok:false}` as a FAILED tool action even when the DevTools transport succeeds. Earlier `browser_click`/`browser_type` calls could report progress when the target selector did not exist, contaminating the self-repair loop. Pure tests cover these cases and legitimate primitive JavaScript evaluation returns.

`WindowsUiActionResultPolicy` checks the bounded WinApp CLI `--json` response and requires `found:true` without timeout for `ui_wait_for`; a successful process exit or a syntactically valid error envelope alone never counts as evidence. This improves deterministic UI feedback while preserving the existing independent verifier's final authority. Offline tests cover negative/timeout/malformed JSON and successful observations.

`WindowsUiActionService.IsSelectorObserved` now walks only genuine `windows[].elements[]` / `children[]` entries. Selector strings elsewhere in metadata, errors or untrusted window text cannot authorize an action.

## External architecture comparison — Jan, Odysseus and Browser Use

See [the source-pinned upstream review](AGENT_UPSTREAM_REUSE_AUDIT.md) for the exact branches, commit IDs, files examined, licenses and rationale. Jan is Apache-2.0; Odysseus is AGPL-3.0; OpenCode, Browser Use and Microsoft WinApp CLI are MIT at the revisions reviewed. We have **not copied upstream code verbatim**: the implemented C# changes are independent and preserve the existing PaRuX runtime/permission boundaries.

### New crash-recovery safety: durable tool dispatch receipt

`CodingAgentDispatchJournal` now records the step and validated tool kind **BEFORE** a potentially mutating coding action is executed. The record is cleared only after the result and observation reach the durable checkpoint. If Windows, PaRuX or the model process stops between these phases, the next `CodingAgentService.RunAsync` restores a **result-unknown** receipt, consumes the uncertain step, clears stale verification, and tells the model to inspect the actual repository diff before taking any further action. It does not automatically replay the potentially successful mutation and it does not call an uncertain action complete.

For performance, existing repeat-guarded *read-only* observations keep their single checkpoint write. The extra pre-dispatch write is reserved for potentially side-effecting actions. Hermetic unit tests cover interrupted, resumed and completed journals; source-level tests do not simulate a killed Windows process. This architecture follows the **propose / dispatch / independently observe** separation found in Odysseus, without importing any AGPL Python implementation.

### Completion claims remain evidence-bound

`CodingCompletionClaimPolicy` now guards the native `done` response against explicit claims that tests/checks passed when no accepted verification command success is recorded after the last mutation. An unsupported test-pass statement becomes a correction request and remains in the durable evidence log rather than a false user-facing result. Honest `tests not run`, conditional examples, and normal completion messages remain permitted. Repeated unsupported claims pause instead of looping. The existing independent verifier still decides full task acceptance. Pure tests cover positive, negative and conditional phrasings. This is original PaRuX C# logic reflecting the evidence/claim separation studied in Odysseus; **no AGPL implementation copied**.

### Browser Use review: final-URL checks, not a fictitious egress sandbox

`BrowserRedirectObservationPolicy` verifies the actual `window.location.href` after CDP navigation and JavaScript actions. With PaRuX Internet disabled, only loopback pages and `about:blank` pass; a redirected remote page is rejected and the browser is navigated to `about:blank`. This rechecks the user Internet toggle, and the browser screenshot and DOM paths now refuse out-of-scope page evidence as well. Added offline policy tests.

**Known limitation:** an after-the-fact redirect observation cannot prevent a request already sent by the browser or a script. Chrome's host-resolver rule alone is not a formal Windows process network sandbox, especially for IP literals and internal resources. Full browser network-policy enforcement needs its own OS/network-layer guard and real Windows probe tests. Do not call browser isolation complete.

## October source comparison: Jan, Odysseus, OpenCode, OpenHands (source implemented; NOT Windows accepted)

Pinned upstream revisions and license analysis: [OPEN_SOURCE_AGENT_REVIEW_2026-10.md](OPEN_SOURCE_AGENT_REVIEW_2026-10.md).

- **Jan / Apache-2.0 reference:** the native coding agent's exact-token preflight now triggers a *soft* proactive compaction attempt at 85% of available input tokens (after the full reasoning/output reserve). It requires a real tokenizer measurement and at least three prior observations, and skips a second tokenizer call when fewer than 256 characters would be saved. Only the replayed execution ledger is shortened; persisted artifacts, tool protocol, goals and verifier state are preserved. It falls back to the old hard admission/overflow path when exact counting is unavailable.
- **Jan / Apache-2.0 reference:** hierarchical project rules now use `CodingProjectInstructionResolver` with deterministic root-to-nearest directory precedence for `AGENTS.md` then `CLAUDE.md`; only changed-file ancestor directories inside the task's authorized workspace are admitted. Paths, file sizes, junctions and output length are bounded; repository rules are explicitly *untrusted*, never an OS permission grant. General manifest discovery is sorted, bounded and skips reparse points.
- **OpenHands SDK / MIT reference:** asynchronous stage feedback cannot regress from a higher completed coding step to a lower delayed callback; the existing ordered/drainable stage queue remains authoritative until the worker's terminal status. This is not full OpenHands event-stream parity.
- **Odysseus / AGPL-3.0-or-later:** its evidence/retry tests and endpoint/model context boundaries were reviewed as **reference only**. No AGPL code was copied, linked or adapted into PaRuX.
- **OpenCode / MIT:** remains read-only observer, not an execution-capable runner until proper OS isolation and STOP are proven.

Offline C# test fixtures have been added to the existing xUnit project and `tools/test-agent-platform-v2.ps1`. **No compilation, unit tests, local model runs or Windows UI tests were run in this environment.** Do not infer a benchmark improvement without measuring p50/p95 step time, context errors, agent tool retries and model output quality on equivalent tasks.

## Remaining software implementation and validation debt (NOT physical-PC tests only)

A1 exact-tokenizer preflight has been implemented for managed llama-server but has **not been run against a real model**; full semantic compaction, cross-provider parity and deterministic overflow/restart integration tests are not closed. A2's supervised *executing* OpenCode backend is **not implemented**: the existing OpenCode health and session-observation bridges are read-only, because unrestricted OpenCode execution would bypass the existing coding AppContainer and Core permissions. A4 now has an optional local SearXNG adapter, authenticated Core-only native coding-agent web_search, and existing DuckDuckGo/CDP paths, but generalized provider management, Browser Use integration and real browser end-to-end acceptance are not complete. A5's app launch, actual Windows UI regression assertions, independent diff verification and unified Chat UI are not complete. A6 same-model benchmark and the existing 14 BIC acceptance gaps remain open. These are genuine software/integration gaps, not merely physical PC tests; do not merge or mark source complete.

## Proposed adapter contracts (not implemented yet)

Every backend must implement, under **Core-owned** authority, equivalent operations:
- `Plan/Start(task, workspace, permissions, limits)`
- `Observe(task) -> structured state and bounded evidence references`
- `Cancel(task) -> idempotent stop and resource release`
- `Resume(task, checkpoint) -> durable continuation without granting new capabilities`
- `Verify(task) -> immutable evidence: diff, exit codes, UI-state assertions, source hashes`

The Core determines authority, target files, allowed processes/network/browser/desktop actions, time/token/VRAM budgets and audit retention. External runners have *no access* to private Core tokens, remote Android credentials or unrestricted desktop capabilities. Even if a runner can request actions, it cannot grant itself permissions.

Use evidence handles instead of dumping large logs, screenshots or HTML into prompts. A verifier cannot solely rely on the developer agent's completion statement.

## Third-party integration candidates and provenance

These are **candidate projects only**: no third-party source code or runtime binaries are copied or redistributed by this initial branch slice.

| Candidate | Upstream | Stated source licence (verify at pinned revision) | Integration preference |
|---|---|---|---|
| OpenCode | https://github.com/anomalyco/opencode | MIT | Optional pinned isolated execution process / SDK, not vendored wholesale |
| Microsoft winapp CLI | https://github.com/microsoft/winappCli | MIT | Optional Windows UIA executable/API, with preview-feature compatibility test |
| FlaUI | https://github.com/FlaUI/FlaUI | MIT | Existing UI tests; consider Windows automation broker implementation |
| Windows-Use | https://github.com/Jeomon/Windows-Use | MIT | Reference or targeted adapted code after inspecting telemetry and sandbox limits |
| Browser Use | https://github.com/browser-use/browser-use | MIT | Optional isolated Python worker using a local model |
| Playwright | https://github.com/microsoft/playwright | Apache-2.0 | Deterministic real browser tests and automation |
| UI-TARS Desktop | https://github.com/bytedance/UI-TARS-desktop | Apache-2.0 | Optional vision computer-use backend; model weights have independent terms |
| Microsoft UFO | https://github.com/microsoft/UFO | MIT | Windows application agent reference; selectively reuse only license-compliant units |

Before importing any source: pin exact upstream revision; inspect transitive licensing, copyright, security and model weights; retain required LICENSE/NOTICE files; record copied/modified paths, authored modifications, revision and distribution in `OPEN_SOURCE_PROVENANCE.md` and `THIRD_PARTY_NOTICES.md`. A standalone optional process is not a grant to misrepresent upstream ownership. MIT requires retention of its copyright and license text for substantial copies. Apache-2.0 carries additional NOTICE/changes requirements where applicable.

## Continuation: first-step guidance and CDP request-stage admission (source committed; NOT Windows-accepted)

Source comparison against `janhq/jan` (Apache-2.0), `odysseus-dev/odysseus` (AGPL-3.0), `anomalyco/opencode` (MIT) and `OpenHands/software-agent-sdk` (MIT) is recorded in `docs/AGENT_UPSTREAM_REUSE_AUDIT.md`. No third-party code was copied or linked in this continuation.

**Context before a first edit:** `CodingProjectInstructionResolver` already loaded root and changed-file ancestor `AGENTS.md`/`CLAUDE.md`. It now also admits explicit relative file paths named in the task, so scoped guidance is available at step one when `git status` is still clean. Lexical path matching is bounded, rejects dot-dot escape, and the existing directory-chain reparse-point checks still prevent reading through junctions or symlinks. `CodingContextCompiler` supplies the exact task text. New regression tests cover first-step guidance, unrelated sibling isolation, malicious URLs and symlinks.

**Browser network:** The dedicated PaRuX Chromium CDP page now calls `Fetch.enable` with request-stage interception before accepting any user tool actions. `BrowserRequestAdmissionPolicy` checks each request URL against the active Internet setting: offline sessions may fetch only loopback HTTP(S)/WS(S), and requests with userinfo or dangerous schemes are denied. Rejected requests receive `Fetch.failRequest` (`BlockedByClient`), not `continueRequest`. Offline-launched sessions skip repeated Core status calls for CSS/JS/image traffic because their local-only restriction cannot be widened mid-session. When an online session continues, policy is checked on every intercepted request, including redirects. The existing post-navigation URL observation remains in place. CDP command errors and newly blocked requests cannot be mistaken for successful navigation. Unsuccessful or canceled CDP sends no longer retain pending response slots.

**Evidence privacy:** `BrowserNetworkEvidencePolicy` removes query strings, fragments and URL-embedded userinfo from browser network records before they enter the agent evidence log; DOM eval errors likewise cannot produce successful observations. Source-grounded unit tests are wired into `tools/test-agent-platform-v2.ps1`.

**Important remaining limits:** This guards requests made by the **attached page CDP target**. Separate popups/tabs, workers, WebSocket handshake bypass paths and system-level egress require additional design and an actual Windows network-trace acceptance test. This is not a verified OS firewall. `Fetch.enable` failure prevents the session being marked guard-ready, but the real browser behavior and performance impact of interception remain **NOT RUN**. Do not promote the PR to ready or release based only on source changes.

## Test ladder and evidence requirements

1. Pure deterministic logic tests (budgets, policy, intent, provenance and checkpoint state).
2. Core integration tests (real task state, grants, cancellation, network scope, provider errors and restart).
3. Native App + Core compiled with the existing `tools/release-gate.ps1 -Restore`.
4. Fresh-layout FlaUI Smoke and Regression (`tools/test-ui.ps1`) with stable AutomationIds and no sleeps.
5. Real GPU local-model runs, UIA/visual Windows runs, full workflow + independent verifier; distinguish `PASS`, `FAIL`, `NOT RUN`.
6. Independent review of each diff, vendor provenance/license audit, security, and release readiness.

No GitHub Actions, automated public release, destructive repository history operations or unsupervised broad computer control are introduced by this plan.


## A4 follow-on — browser action privacy and coding-task isolation (implemented; NOT runtime-accepted)

A source review found two additional correctness/privacy problems in the coding-agent browser:
the original `browser_type` script reflected the typed value in its CDP reply, and
the singleton developer browser reused a persistent profile across unrelated coding tasks.

- `BrowserDomActionScriptBuilder` replaces inline click/type JavaScript. It uses JSON
  string literals for the selector/value, rejects disabled/read-only/inert and unsupported
  targets, handles textarea, text inputs, selects and contenteditable, and refuses recognized
  credential/password/token fields that need direct user-owned input. Numeric inputs whose
  setter rejects the proposed value return `ok:false`. Browser actions never echo the
  text of clicked elements or the typed value in their successful result.
- `BrowserDomActionResultPolicy` accepts only a structured `{ok:true}` envelope,
  emits a fixed "delivered, inspect actual state" message, and allows only predefined
  non-sensitive denial reasons. `EvaluateUiActionAsync` never returns raw CDP
  exception payloads containing embedded source/inputs; cancellation still propagates.
  The general-purpose `browser_eval` remains explicitly separate and may reveal
  JavaScript output intentionally requested by the agent; it is not covered by this
  redaction contract.
- `BrowserTaskOwnerLease` requires the resolved coding task/session ID before any
  browser tool runs, refuses other owners, and holds the lease through shutdown
  on normal exit, cancellation, pause, verifier handoff or failures. If shutdown
  cannot be established, new claims stay blocked rather than borrowing a live CDP
  process. `CodingAgentService.RunAsync` releases its browser in `finally`.
- Each developer browser process uses a GUID-named temporary profile under
  `browser-dev-profile/session-*`, instead of the old persistent `online` or
  `local-only` profiles. The profile is removed after the browser process stops;
  process crashes or filesystem locks can leave local remnant directories that
  require maintenance. Old persistent profiles are no longer reused, but this
  change does not destroy a user's pre-existing profile data.
- Successful `browser_navigate` observations now omit query strings, fragments
  and URL userinfo. This is evidence minimization, not an assertion that arbitrary
  browser_eval or page DOM observations are free of secrets.

**Source/tests:** `BrowserDomActionScriptBuilder.cs`, `BrowserDomActionResultPolicy.cs`,
`BrowserTaskOwnerLease.cs` and their xUnit tests are included in
`tools/test-agent-platform-v2.ps1`. **All new tests, Windows build, CDP
functional behavior, concurrent browser tasks, physical network egress and
profile cleanup remain NOT RUN** in this GitHub-only development session.

**Same-task overlap:** `CodingTaskRunLease` now prevents two simultaneous
`CodingAgentService.RunAsync` calls in the SAME PaRuX process from using the
same deterministic checkpoint ID. A scoped IDisposable releases the lease
on every method exit, including exceptions, while unrelated tasks may run
in parallel. Tests exercise duplicate rejection, exception cleanup and
parallel admission. Multi-PROCESS/multi-PC workspace locking is still
out of scope and unverified here.

**Unresolved:** explicit per-action approval for risky website mutations; robust
target/popup/service-worker/WebSocket interception; OS-level network confinement;
verification
of edited page state via an independent DOM/UI observer. Do not call this complete
computer-use or a network firewall.


## A5 first slice — typed browser DOM postconditions and safer mutation authority (2026-10-10)

**IMPLEMENTED IN SOURCE; build, xUnit, Node and live Chrome/Windows execution NOT RUN in this environment.**

- `browser_assert` is now an explicit native coding-agent action. It requires
  `selector` and exactly one `target`: `exists`, `absent`, `visible`,
  `hidden`, `text_equals` or `value_equals`. Equality cases use
  `text` as the expected literal (including an intentional empty string,
  max 4096 characters). `BrowserDomAssertionPolicy` JSON-quotes all model
  arguments into a fixed read-only JS expression.
- `BrowserDevToolsService.AssertDomAsync` uses the dedicated browser and
  rechecks navigation policy before and after each assertion, with at most
  twelve inspections separated by a 150 ms cancellable wait. Failed comparisons
  may retry; denied credential fields, malformed CDP response and invalid
  controls fail without a retry. Result classification accepts only a
  recognizable `parux-dom-assertion` CDP envelope, returning no actual page
  text, passwords, form values or arbitrary exception messages.
- Browser agent postconditions do **not** set `VerifiedByCommand` or waive
  independent build/test/code-verifier acceptance. A positive DOM assertion
  means a specified state was observed in that browser at inspection time,
  NOT that a deployment, remote action or software change is fully correct.
- `BrowserUiMutationPolicy` deliberately limits `browser_click`,
  `browser_type` and unrestricted `browser_eval` to a locally hosted
  HTTP(S) page. Global Internet ON permits browsing and read-only page
  inspection but **does not imply authorization for remote site actions**.
  Future external-site computer use requires explicit origin/task-scoped Core
  permission, separate user consent for destructive actions and validated
  network confinement. Invalid, remote or credential-bearing origins fail
  closed. Arbitrary JavaScript is still potentially powerful *inside an
  allowed local test app*; popup/worker/child-target restrictions remain open.
- Browser shutdown now waits for observed process exit (bounded 8 seconds)
  after process-tree kill. Failed exit prevents the browser task lease from
  transferring to another task. Navigation also uses request-admission checks
  *before* CDP and suppresses raw CDP exception text that could contain URL
  query secrets.
- Hermetic C# tests cover assertion grammar/result classification, injection
  escaping and remote mutation policy. `tools/test-browser-dom-assertion.mjs`
  additionally reconstructs and executes the C#-assembled DOM assertion
  JavaScript against synthetic DOM objects in Node without third-party
  packages. The Windows `test-agent-platform-v2.ps1` runner records
  `browserDomJs` as PASS/FAIL when Node is available, otherwise NOT_RUN.
  Neither a synthetic DOM test nor xUnit source coverage can replace live
  Chrome/CDP + Windows UIA and a real test web application.

**Remaining blocking acceptance:** live local web-app flow
`navigate → type/click → browser_assert → actual UIA/independent verification`;
failure injection, cancellation and browser restart; popup/WebSocket/service
worker egress; external agent OS sandbox; production end-to-end coding and
benchmark gates. Keep PR in DRAFT. No GitHub Actions.


## A5 regression repair — typed CDP results and literal-IP defense (2026-10-10)

**Confirmed source defect and implemented fix:** the shared
`BrowserDevToolsEvaluationPolicy.IsSuccessful` correctly rejects
`{ok:false}` for generic `browser_eval`, but it was also called *before*
the specialized `BrowserDomAssertionResultPolicy` and
`BrowserDomActionResultPolicy` could classify their expected negative
envelopes. Thus `browser_assert` mismatches were wrongly treated as
non-retryable protocol failures, and known sensitive-field denials lost
their safe, actionable error category. `HasUsableResult` now evaluates
CDP structural/exception validity independently of task success. The two
typed classifiers use it to preserve the distinction between a transient
`missing/mismatch` retry and a permanent `restricted/unsupported` denial,
without weakening generic `IsSuccessful` or allowing raw CDP errors through.
Regression xUnit cases were added to both the generic and typed policies.

**Address admission hardening:** `BrowserLiteralAddressPolicy` now blocks
literal RFC1918, carrier-NAT, link-local/metadata, reserved and non-global
IPv6 destinations even when Internet is ON. It is shared by request-stage
`BrowserRequestAdmissionPolicy` and post-navigation
`BrowserRedirectObservationPolicy`; `browser_navigate` already uses
the request-stage policy as a preflight. Explicit IPv4-mapped IPv6
normalization avoids escaping the IPv4 checks. Public HTTP(S) IPs,
hostnames and loopback remain permitted by this *literal-address* policy;
offline mode retains its stricter loopback-only behavior. IPv4/IPv6,
mapped-address and live Internet-toggle regressions are added to the local
Windows xUnit gate.

**Execution evidence:** 16 synthetic DOM cases were actually executed
against the JS reconstructed from the C# builder in an isolated V8 runtime:
**16 PASS, 0 FAIL**. They include presence/absence, layout visibility,
expected text/value, credential and hidden input rejection, bad selectors
and injected selector text. This is script-level evidence only; it is
**not** the Windows `.NET` build, xUnit test run, the repository's
`node --test` runner or live browser/CDP verification. Those remain
**NOT RUN** here.

**Still open:** hostnames may resolve to private addresses (DNS rebinding),
and popup/child-target, worker, WebSocket and physical egress paths still
need enforceable OS/browser-target confinement and real Windows acceptance.
The change is NOT a complete SSRF firewall or an E2E software-verification
closeout. Keep `main` untouched, no GitHub Actions or release.


## A5 continuation — durable user answers cannot waive code verification (2026-10-10)

Source review identified a second correctness/authority issue unrelated to CDP:
`CodingAgentService.RunAsync` set `checkpoint.VerificationRequired = false`
and `checkpoint.VerificationSatisfied = true` on first consumption of a
user answer, before resuming coding. That could turn an answered question
into an unsupported "tests passed" claim for previously changed code,
potentially losing the pending gate or contradicting scoped verification.

**Implemented, NOT Windows accepted:**
- `CodingAgentCheckpointStore.ResumeAfterUserAnswer` clears only the
  no-progress watchdog. Pending/satisfied verification flags, per-repo
  verification scopes and `VerifiedByCommand` are preserved; source
  evidence and durable user answers remain available across resume.
- Loading a previously written checkpoint containing a `[USER INPUT ...]`
  receipt and `VerificationSatisfied=true` without a genuine
  `VerifiedByCommand` receipt now invalidates the unsupported success,
  clears satisfied scopes, and requires verification again. This migration
  errs on the side of rerunning tests; checkpoints with accepted command
  receipts are preserved. A round-trip regression exercises both cases.
- `CodingVerificationCommandPolicy.MayHandPausedAttemptToVerifier` never
  accepts outstanding verification even when the model reports `done`.
  `DecideHandoff` gives durable `WaitForUser` /
  `WaitForDelegatedChild` priority over terminal verifier handoff or
  "paused without tests" failure. `AgentCodingWorkerService` uses that
  typed decision rather than ordering of unrelated if-statements.
- `CodingAgentService` includes the current `VerificationPending` flag
  in user-question and child-delegation pause results; these are
  **non-terminal**, not failed attempts nor verifier passes.
- New checkpoint round-trip tests preserve mixed verification scopes and
  previous command evidence. Eight policy test combinations cover
  waiting, goal claims and outstanding tests; the focused Windows runner
  explicitly includes `CodingVerificationCommandPolicyTests`.

**Evidence boundary:** these are original C# source changes and newly
authored xUnit tests. No .NET SDK is present here; those tests and live
task-resume integration are **NOT RUN**. Confirm on development Windows
with a disposable repository: edit code, ask user, resume, attempt done
before testing (must reject), run discovered build/test, complete only
after accepted command and independent verifier. Also exercise child
delegation and user-answer replay across restart.


## A5 verifier hardening — genuine coding and UI evidence before independent PASS (2026-10-10)

**Source implemented on the draft branch, NOT Windows release accepted.**

1. `CodingResultVerifierGate` now runs inside Core `VerifierService.ReviewAsync`
   before any model PASS or finish-condition evaluation. A coding task must
   supply typed booleans `goalReported`, `verificationPending` and
   `verifiedByCommand`. Missing/malformed status, pending deterministic
   tests, or a purported accepted-command receipt without a checkpoint ID
   are rejected. When a command receipt is claimed, Core additionally
   requires `checkpointSessionId == task.Id`. The desktop coding worker
   now emits explicit `verificationPending` in `ResultJson` and is still
   responsible for exact-command admission. A failed preflight may enter
   bounded repair but never reaches the model verifier as a passing result.
   **Limitation:** the worker's boolean remains a trusted report, not an
   independently signed command-output attestation. Core still needs
   durable task/worktree-bound command receipts.

2. `UiCaptureProvenancePolicy` binds native screenshot capture to the
   foreground Windows PID and HWND before and after the screenshot. If
   WinApp UIA is available, its own observed window must match the screenshot
   window, and the foreground window must remain stable during the later UIA
   read. A window switch fails the task; no mixed-window screenshot/tree is
   fed to vision. Screenshot persistence now happens **after** these checks.

3. The UI worker treats Visual Context persistence failure or a returned
   snapshot lacking ID/SHA-256 as an **unverified task failure**, not a
   degraded model-only PASS. It includes `foregroundWindowStable` and
   `uiTreeMatchedCapture` in results.

4. `UiVerificationEvidenceGate` runs in Core before the independent
   verifier model. It requires typed stable-window metadata, a factual
   response, consistent UIA mode/status and a canonical snapshot ID. Unlike
   merely trusting `visualSnapshotId`, Core now queries its existing
   `VisualContextV2Store` to establish a **real retained PNG** with SHA-256
   digest, nonzero length, `scope=task`, `scopeId=task.Id` and
   `source=ui-verification`. Forged IDs and snapshots belonging to
   another task are rejected before the model can approve them.

5. Original C# xUnit tests exercise malformed/pending/foreign-checkpoint
   coding results, UI PID/HWND consistency, metadata inconsistency and
   actual temporary-SQLite Visual Context snapshot ownership. Focused local
   Windows runner now selects `CodingResultVerifierGateTests`,
   `UiCaptureProvenancePolicyTests`, `UiVerificationEvidenceGateTests`
   and `VerifierServiceTests`.

**Remaining evidence gaps:** no .NET SDK or interactive Windows/Chrome/WinApp
runtime available to execute these new C# tests here; they remain
**NOT RUN**. The server checks metadata integrity and retained screenshot
ownership but does **not** prove screenshot pixels correspond to the intended
application build, nor that the model's UI interpretation is correct. Native
FlaUI/UIA action assertions with stable AutomationIds, build-to-process
identity, explicit UI test requirements, independent reviewer acceptance,
OS-confined computer use and hardware/performance benchmarks are still
mandatory before BIC/release completion. No GitHub Actions, merge or release.


## A5 continuation — local native/browser fixtures, stale-test invalidation and guarded adoption (2026-10-10)

**Implemented on draft source. This is not proof of real Windows acceptance.**

- Fixed the existing `tools/test-winapp-functional.ps1` false-positive path:
  a zero exit code alone is no longer sufficient. The before/after WinApp
  inspect output must contain the expected controls in the **explicit HWND**;
  the action response must be valid nonerror JSON; and the independent
  `ui wait-for` output must contain `found:true` without timeout.
  Malformed/negative evidence is a failure.
- Added `tools/PaRuX.UiAcceptanceFixture/` — a disposable, isolated .NET 8
  WinForms executable with stable AutomationIds, unique nonce, and a
  button-to-readonly-textbox state transition. Optional
  `tools/test-agent-native-ui-e2e.ps1 -ConfirmFunctionalTest` builds and
  starts the fixture, discovers the actual PID/HWND and selectors via WinApp
  inspect, invokes the control, asserts the changed value separately, and
  tears down the test process. The local `test-agent-platform-v2.ps1`
  accepts `-RunNativeUiE2E` and records `nativeUiFunctional` independently.
  It does NOT target unrelated user applications and cannot execute on a
  headless CI runner.
- Added `tools/browser-agent-fixture.mjs` and its Node tests: a disposable
  127.0.0.1-only web page with `#parux-input`, `#parux-run` and
  `#parux-result`, an asynchronous same-origin state change, unique
  per-process nonce, origin/header checks, restrictive CSP and bounded
  payload. The accepted in-browser flow is
  `navigate -> type ready-NONCE -> click -> browser_assert text_equals ACCEPTED-NONCE`.
  The focused Node test gate checks loopback binding, HTTP behavior and
  rejection cases. These are test fixtures, not production website tooling.
- **Fixed a stale verification bypass discovered in code review:**
  `CodingAgentService` had previously made verification optional whenever
  no build/test command was discovered, and could persist old
  `VerificationSatisfiedScopes` across a subsequent successful code edit.
  Now **every testable edit requires verification**; the checkpoint clears
  all previously satisfied scopes and `VerifiedByCommand`; a newly
  accepted command outcome alone can satisfy the current scopes. If the
  project has no available verification command, the work remains unverified
  and requires operator review rather than an invented success. Focused
  round-trip regression tests cover edits after green, restart and failed
  verification.
- A coding task which reports `done` now **preserves** its local durable
  checkpoint if it belongs to an orchestrator task, because the independent
  Core verifier has not yet accepted the result. Direct chat sessions without
  an orchestrator continue the earlier cleanup behavior.
- Introduced `CodingTaskApplyAdmissionPolicy`: the chat's Apply action is
  available only when a fresh Core coding task is `Completed`, its
  independent verification is `pass`, `verificationPending=false`,
  `verifiedByCommand=true`, and the checkpoint is task-owned. The UI
  rechecks Core state before and **after** the confirmation dialog. A
  missing/invalid receipt shows an honest warning instead of a green verified
  badge or an apply button. Single and multi-repository review now compare a
  SHA-256 fingerprint of the reviewable diffs before and after user approval;
  missing/truncated/unreadable evidence fails closed. This is a narrow
  same-review check, not a filesystem lock: a concurrent write racing the
  final apply still needs task-scoped worktree locks in future work.
- Coding task buttons have stable WinUI AutomationIds for Apply, review in
  VS Code, STOP, answering questions and opening the live worktree.

**Actual test evidence this batch:** GitHub branch commits and source
consistency checks. **NOT RUN:** .NET build/xUnit, WinForms fixture and
WinApp functional E2E, release gate, FlaUI, live Chrome actions and GPU
benchmark. Node fixture behavior tests are checked into source but their
normal `node --test` runner has not executed on Windows in this session.

**Still software work, not merely physical-PC tests:** the optional
OpenCode executing backend is intentionally blocked pending OS isolation;
Chrome popup/service-worker/WebSocket/DNS-rebinding egress confinement is
not proven; there is no fully integrated automatic coding -> native
application launch -> FlaUI UI proof -> independent verdict -> reviewed
same-bytes adoption transaction; multi-process/cross-PC worktree locks,
all full BIC-01..17 acceptance cases and reproducible comparative benchmarks
remain open. Therefore do NOT mark PaRuX `SOURCE_SOFTWARE_COMPLETE` or
`BEST_IN_CLASS` based on this work, and do not merge/release.


## A1/A5 cross-process coding execution exclusion (2026-10-10)

The previous `CodingTaskRunLease` serialized work only inside one .NET
process. Two separate PaRuX App instances sharing a state root could both
execute the same durable checkpoint and browser task. The lease now combines
a fast per-process dictionary with a deterministic SHA-256-named exclusive
`FileStream(FileShare.None)` lock held for the full `RunAsync` scope.
Failures fail closed before any coding task starts. The operating system
automatically releases the handle on process exit, after which the existing
write-ahead journal governs uncertainty; lock filenames are deliberately
never deleted to prevent an unlink/recreate lock race. xUnit fixtures now
use isolated directories and exercise two independent lease instances,
duplicate denial, release and exception cleanup.

**Scope:** one Windows host, or clients sharing exactly the same lock
directory where underlying filesystem locking is honored. Two autonomous
PCs with *different* PaRuX state directories are not coordinated by this
mechanism. Central Core-owned task/workspace leases with fencing tokens
and cross-PC ownership still require separate implementation and runtime
tests. Not a distributed lock claim. Windows OS-process acceptance NOT RUN.


## A1/A5 immutable workspace verification receipt (2026-10-10)

Source review caught a race beyond the earlier `MutatedTestableCode`
reset: the agent can run an indirect modifying command or an external
process may change the worktree **after** its accepted build/test. Old
command evidence is insufficient in that case.

`CodingWorkspaceVerificationFingerprint` now computes a bounded,
deterministic SHA-256 summary of actual Git diffs (including within
documented untracked-file limits), for one trusted worktree or all
direct trusted repository aliases. It fails closed on missing/
untrusted workspaces, unsafe symlinks and detected omitted/truncated
diff evidence. The current Git state is captured after an accepted
verification command; the fingerprint is stored in the checkpoint as
`VerifiedWorkspaceFingerprint`, saved over restart and cleared after
a failed command or new testable source mutation. Before a `done`
claim can pass, the coding agent checks the current workspace
fingerprint against the command receipt, invalidates stale proofs
and requires another verification if different/unavailable.
`AgentCodingWorkerService` independently rechecks the task-owned
checkpoint and Git state immediately **before handing off** to the
Core verifier. For verified coding tasks the worker includes the
canonical lowercase SHA-256 `verifiedWorkspaceFingerprint` in its
result metadata; `CodingResultVerifierGate` and
`CodingTaskApplyAdmissionPolicy` require this digest and the task-owned
checkpoint ID instead of trusting an unscoped `verifiedByCommand=true`
boolean alone. This is source-relevant state consistency, not a
cryptographically trusted or signed test transcript, nor an atomic
cross-process filesystem snapshot.

C# tests exercise stable vs changed diffs, bounded/truncated output,
missing workspace, restart persistence and invalidation after edit
or failed command. Included in the local Windows xUnit gate; **NOT
EXECUTED** in the GitHub-only session. A full live regression must
also test legitimate code generation that modifies tracked assets,
untracked files, Git submodules and multi-alias workspaces.

Still open: stronger executable-specific command evidence and
task-scoped command attestation, workspace fencing beyond one shared
state root, complete browser target/OS egress isolation, supervised
OpenCode execution and an automatic coding-to-native UI test DAG.
These are active source gaps, not merely unperformed PC tests.


## A5 continuation — closed-loop browser postconditions, child-target exclusion and goal truth (2026-10-10)

**Committed original implementation; Windows .NET, live PaRuX and full Chromium fixture acceptance NOT RUN.**

- Server `CodingResultVerifierGate` and desktop
  `CodingVerificationCommandPolicy.MayHandPausedAttemptToVerifier`
  now require `goalReported=true`: an exhausted, cancelled,
  budget-limited or interrupted coding attempt with no explicit `done`
  is not eligible for independent model PASS merely because no test
  command is pending. Worker messages distinguish incomplete goals from
  completed-but-unverified source changes. Regression test expectations
  were updated accordingly.
- `CodingBrowserPostconditionPolicy` makes a real `browser_assert`
  PASS mandatory after the most recent `browser_click`,
  `browser_type` or `browser_eval`. This also applies when mutation
  delivery failed or may have partially succeeded; read-only DOM,
  screenshot, navigation and browser reset cannot satisfy it.
  `BrowserPostconditionPending` is saved in the durable checkpoint,
  survives user-input resume, and is marked pending for an interrupted
  mutating CDP dispatch with unknown outcome. A model `done` claim
  while pending is refused with a bounded recovery/pause.
- Desktop worker separately reads the task-owned checkpoint before
  handoff and rejects missing/pending browser postcondition evidence.
  `browserPostconditionPending` is now an explicit typed Boolean in
  the coding result. Core and user-apply policies require it to be
  present and `false`, in addition to their existing coding/test and
  Git-state evidence gates.
- A new `BrowserAttachedTargetPolicy` is wired to CDP
  `Target.setAutoAttach(autoAttach=true,waitForDebuggerOnStart=true,flatten=true)`.
  Any `Target.attachedToTarget` event means an uncontrolled child
  page, worker or out-of-process frame could have separate network/
  JavaScript authority. Rather than leaving it live, the CDP reader
  fails pending requests and kills the entire private browser process
  tree; subsequent CDP commands fail closed, and a new session is
  admitted only after verified old-process exit. Startup fails closed
  when target auto-attach is unsupported. This protects **related,
  observed** children, not arbitrary browser-wide targets/DNS
  rebinding, OS egress or connections in an unrelated browser process.
- The new opt-in `tools/test-browser-cdp-e2e.mjs` runs Node 22+
  against an operator-specified existing Edge/Chromium executable.
  It starts the nonce-scoped 127.0.0.1 fixture, launches a private
  headless profile, sets up child-target autoattach and request-stage
  interception, and requires a real navigate -> input -> click ->
  asynchronous DOM state assertion. `tools/test-agent-platform-v2.ps1`
  supports `-RunBrowserE2E -BrowserExecutable <real executable>`,
  records `browserCdpFunctional` independently and propagates FAIL.
  Do not use the fixture to navigate user credentials or remote pages.
- `CodingWorkspaceVerificationFingerprint` now independently checks
  the exit codes for Git tracked-diff and untracked-file enumeration
  before hashing; subprocess failure must never be mistaken for a
  clean workspace.

**Execution limitation observed here:** Node 22 and Chromium 144
were available in a Linux container and a local HTTP test fixture
returned 200, but a headless CDP navigation attempt (including to a
data URL) returned Chromium `net::ERR_BLOCKED_BY_ADMINISTRATOR`.
The corresponding real-browser end-to-end leg did **not PASS**;
it needs an unrestricted, disposable browser test environment on
the Windows development machine. No dotnet SDK, PowerShell, FlaUI,
Windows UIA or GPU acceptance was performed.

**Remaining implementation gaps are still software, not just PC tests:**
the optional OpenCode executor is still intentionally disabled pending
Windows AppContainer and authenticated local LLM-provider confinement;
browser DNS rebinding/OS egress enforcement and unrelated targets need
an OS-level broker; independent Core worktree-scoped signed command
receipts and cross-machine lease fencing are missing; the automatic
edit/build/native UIA/FlaUI/independent verdict/user-adoption workflow
and outstanding master BIC cases still need implementation and genuine
benchmarks. Therefore the V2 draft remains NO-GO for merge/release.


## A5 continuation — all-repository verification integrity, unchanged runner manifests and meaningful browser checks (2026-10-10)

**Implemented on the draft branch, not yet compiled or run on real Windows.**

- Corrected a false-green multi-repository verification path in
  `CodingVerificationDiscovery`: previously, for three or more Git
  children, any child with no discovered test/build command was
  silently skipped, while the remaining commands were misleadingly
  called `verify-all`. The aggregate gate now requires **every**
  direct Git authority to have recognized tests; missing/unsupported
  aliases, unexpected extra folders, >16 aliases, reparse-point
  aliases and unsafe shell characters fail closed. A model-written
  root `package.json` may not supersede the recognized child
  worktrees in an aggregate. The test suite includes two- and
  three-repo cases, an unsupported third repository, extra untrusted
  directories, unsafe Windows shell alias names, and fake root npm
  tests.
- Added independently implemented `CodingVerificationManifestPolicy`.
  The original agent could rewrite a discovered test/build runner
  such as `package.json` or `Makefile` into a trivial `exit 0`
  command, report green, and optionally restore the manifest. For
  **discovered verification commands**, the coding executor now
  checks before and after the process runs that all selected root
  verifier manifests are **tracked by Git and unmodified versus HEAD**,
  and that the same HEAD commit and manifest list were retained.
  For aggregate tasks the same policy covers every trusted child
  Git repository and rejects injected aggregate-root manifest files.
  Unknown, missing, rewritten or newly invented verification runners
  cannot supply accepted command evidence. An ordinary scope-specific
  command remains subject to the existing separate scoped verifier
  and executable-origin rules. There are focused xUnit fixtures with
  genuine temporary Git repositories for package.json, csproj,
  Makefile, untracked scripts, and multi-repo tampering/restore.
- The check intentionally examines a **bounded root-manifest set**
  (solutions/project files, package, build, test and runner config).
  It does NOT attest every imported script, nested build override,
  third-party toolchain module or build output. Such gaps still
  require independent review and OS-level sandbox verification.
  Legitimate changes to verifier manifests may be blocked from
  automatic acceptance and require an independent trusted review;
  bypassing the rule to force green is prohibited.
- `CodingBrowserPostconditionPolicy` now insists that a **strong**
  successful `browser_assert` with `text_equals` or `value_equals`
  and a concrete nonblank expected value follow the last click,
  typing or potentially mutating evaluation. Trivial observations
  such as `exists('body')`, generic visibility, an empty equality
  or a browser transport success cannot waive a pending UI action
  proof. These remain valid exploratory observations but not
  acceptance. The model's execution guidance and checkpoint tests
  were updated. Exact DOM equality is still a **state assertion**,
  not proof that a particular browser click causally produced the
  state; full end-to-end test acceptance remains necessary.

**Validation:** 15 scoped static source/contract checks PASS across
10 changed and related files. New C# xUnit tests are linked into
`PaRuX.Server.Tests` and selected by `test-agent-platform-v2.ps1`,
but no .NET SDK, Windows AppContainer, live PaRuX/FlaUI UI or
actual Edge/Chromium functional acceptance executed during this turn.
Do not mark the software or release as completed.


## A1/A5 continuation — correct worktree provenance and alias path identities (2026-10-11)

**Concrete source defects fixed in draft; Windows/.NET tests NOT RUN.**

1. `CodingAgentTools.IsTrustedGitWorkingDirectory` answers whether the
   **host can trust** a directory for Git operations, not whether the
   directory **is** a Git working tree. For ordinary non-agent folders,
   it intentionally returns true. Both
   `CodingWorkspaceVerificationFingerprint` and
   `CodingVerificationManifestPolicy` had incorrectly treated that
   as proof a plain parent directory was an actual Git repository.
   An aggregate workspace could therefore attempt Git operations in
   its parent and lose all valid child-repository evidence.
   Introduced shared `CodingWorkspaceGitLayoutPolicy.TrySelect`,
   requiring an actual `.git` marker for single-repository mode;
   otherwise requires **every** direct child (2..16) to be a trusted
   Git worktree with a validated alias and no reparse-point directory.
   Both proof gates use the same resolver. The fingerprint scope was
   aligned with the verified-all limit of 16 repositories.
2. Git state receipts now include the **HEAD commit hash** of each
   covered repository as well as its bounded tracked/untracked diff.
   Committing a code edit after a green test cannot turn the diff
   clean and reuse the prior command's accepted digest. Tests use
   genuine temporary Git repositories, verify a tracked edit and
   subsequent commit produce different fingerprints, and require all
   aggregate children to participate. The existing real Git worktree
   fixture was strengthened to assert the aggregate digest before
   and after edits under an actual `multi-worktrees` directory.
3. `MultiRepositoryWorktreeService` previously accepted arbitrary
   `^[A-Za-z0-9._-]{1,128}$` task IDs and aliases, which included
   `.` and `..`. Added `CodingRepositoryAliasPolicy` to reject
   path traversal, unsafe Windows devices, shell metacharacters,
   duplicate case-insensitive aliases, overly long identities and
   malformed task IDs. Task ID and authority names are validated
   **before** aggregate workspace creation. The apply-side authority
   review, Git layout resolver and aggregate command discovery now
   share that policy. The creation and adoption gates remain
   fail-closed on invalid/duplicated authority identities.
4. New focused unit tests cover trust vs presence, safe worktree
   selection, directory/device name attacks, duplicate aliases,
   missing Git children, real commits changing a previously clean
   receipt, and genuine multi-worktree digests. Linked both new
   policies into `PaRuX.Server.Tests` and the local Windows
   `tools/test-agent-platform-v2.ps1` acceptance suite.

**Source-only checks:** 17 focused cross-file integration checks PASS
across 12 reviewed source/test/runner files. Genuine .NET build,
xUnit, FlaUI, Edge/Chrome CDP, local UIA and AppContainer validation
are **NOT RUN** in this GitHub-only environment. These changes are
not a claim of automatic cross-PC fencing or of source-complete
self-improving PaRuX. OpenCode OS execution isolation, browser
OS egress, distributed task ownership, executable launch/UI
test acceptance and the remaining master BIC cases stay open.


### Follow-up: server-side authority names share one validator

`AgentRepositoryAliasValidation` now lives in the shared
`PaRuX.Contracts` project. Both the legacy
`AgentRepositorySetStore` and the atomic V2
`AgentRepositoryAuthoritySetStore` validate aliases with this same
contract before persisting task authority. The desktop
`CodingRepositoryAliasPolicy`, multi-worktree preparation, review,
aggregate Git detection and command discovery use that validator
rather than inconsistent local regular expressions. Windows reserved
devices such as `CON`, `NUL.txt` and `COM1`, trailing-dot alias
names and case-folded duplicate bindings no longer reach disk.
Separately, task-directory IDs cannot be dot traversal or Windows
device names. Existing server authority tests now include device-name
and unsafe-alias regressions.

**Final source audit this continuation:** 18/18 integration checks
PASS across 18 related files. The new test cases were committed and
wired for local Windows xUnit execution but **not run in this
environment**. All existing master acceptance / release caveats stand.


## A5 continuation — approved Git state enforced at the actual apply boundary (2026-10-11)

**Source changes committed; C# build, xUnit and physical Windows acceptance not run.**

The reviewed Apply chat card previously checked the worktree diff before and
after user confirmation, but the native apply services independently generated
binary patches or copied file contents **later**. That allowed an agent or
external process to modify the task worktree between UI approval and the
actual source-to-target mutation, even if the UI's earlier diff matched.

- `ChatView.NativeCodingHandoff` now uses the same
  `CodingWorkspaceVerificationFingerprint.CaptureAsync` receipt as native
  coding verification, bound to each repository's Git HEAD and bounded
  tracked/untracked diff. For aggregate tasks it checks that **every**
  selected direct Git alias matches the authority set. The exact approved
  digest is forwarded to `CodeWorktreeApplyService.ApplyAsync` or
  `MultiRepositoryApplyService.ApplyAsync` after the confirmation dialog.
- Both apply services optionally accept the approved digest. Calls from the
  chat provide it; legacy direct internal/tests without approval remain
  separate. They check source HEAD/diff identity **inside ApplyAsync** before
  preparing any patch. Because assembling binary patches requires temporary
  `git add -N`, they restore those intent-to-add staging entries and
  independently recompute the full Git receipt **after** patch preparation,
  prior to mutating any original repository. Mismatch/unreadable state is a
  definitive no-apply failure. Multi-repo admission is all-or-nothing; no
  subset may pass with an out-of-date digest.
- Non-Git project folders use a shadow Git repository. The original
  `ShadowWorkspaceService.ApplyToOriginalAsync` now receives the same
  optional approved digest and rechecks before and after computing its
  changed-file list. The original folder is not touched on a failed proof.
- Added `ShadowApplyPathPolicy` to deny absolute/traversing paths, ambiguous
  Windows device paths and existing source/destination junction/symlink
  ancestors **before** any shadow file copy. The shadow mirror now uses
  an enumeration mode that skips reparse-point trees, and applies the same
  boundary validation when importing/removing mirrored files. This prevents
  a link in the user-selected non-Git folder from silently copying files
  belonging to a different tree into PaRuX's internal shadow.
- Genuine temporary Git acceptance regression cases now cover changed
  source after review, changed HEAD after review, positive reviewed
  single-repository apply with new files, positive and negative
  multi-repository apply, positive and negative non-Git shadow copies,
  directory traversal, reserved device names and existing symlinks.
  `test-agent-platform-v2.ps1` includes these suites, and the new
  shared shadow path policy is compiled into `PaRuX.Server.Tests`.

**Limitations to preserve:** this closes the review-to-apply mismatch at
the host-service entrance and after patch preparation, but cannot make
a multi-process filesystem snapshot perfectly atomic on ordinary Windows.
A separate process can still change a file between the final check and
copy/apply, or a target can change between Git `apply --check` and
`apply`. Cross-process/repository coordination, source and destination
transactional backups, and OS-level filesystem fencing remain independent
software and Windows-acceptance tasks. Shadow folder writes are not yet
fully transactional; a write error during a multi-file shadow apply
can leave partial modifications and requires manual reconciliation.
Never label the implementation full ACID or release-ready.

**Checks performed this session:** 21/21 scoped static implementation
checks passed across 12 source/test/runner files. **NOT RUN:** .NET
compiler, xUnit, physical UIA/FlaUI, AppContainer, real Chromium CDP,
cross-process/cross-PC race tests, or GPU benchmarks. Main remains
unchanged; PR #6 remains draft; no GitHub Actions were started.


### Shadow folder copy follow-up — deny stale renamed files and unverifiable sync results

In the non-Git shadow-to-original path, preflight used to check hashes only
**if** both `git hash-object` / `git rev-parse HEAD:path` succeeded;
errors could otherwise fall through to writes. Added-file destinations
had the same weakness. Renamed files could delete a newer user-modified
old filename with no source-baseline comparison. Untracked status and
`git add -N` errors were also allowed to silently skip files.

The new checks reject errors and missing/empty hash results, compare
both the old and new sides of renames, refuse unexpected existing
files, and do not proceed if a new untracked file cannot be included
in the reviewable change list. A source file disappearing mid-apply,
an unexpected file deletion failure, or an unsuccessful shadow
`git add` / baseline `git commit` must not report a successful sync.
The response explicitly warns when previous writes may have happened,
because cross-file rollback is **not implemented yet**.

Added real-shadow tests for user-edited renamed source protection and
an unrelated user file present at an intended newly-added pathname.
Validated that `git -C <non-git-folder> hash-object <file>` is supported
without initializing Git in that original folder. These tests are
selected by the focused local Windows runner, but **not yet executed
with .NET on Windows**. Mirror scanning also has regression coverage
for an external symlink directory (conditionally skipped on Windows
systems without permission to create test symlinks).

Source-only QA in this continuation: 23/23 implementation/runner
checks passed across 11 files. Non-Git multi-file copy remains
nontransactional against mid-write errors; moving it to a
recoverable/atomic staging and rollback protocol, and testing real
Windows failure injection, remain active work rather than completed
acceptance.


## A6 continuation — recoverable non-Git adoption (2026-10-11)

**Source committed; C# build, xUnit, crash-restart recovery and Windows UI tests NOT RUN.**
The shadow-project Apply path previously copied directly to the user's original
folder without preserving preimages: failure during file 2 could leave file 1
changed, and a deletion/rename could be only half-applied. The existing helper
also invoked its trusted-working-directory-gated Git wrapper with the original
NON-Git folder as the working directory for `git hash-object`. That is an invalid
trust context, irrespective of whether raw `git -C <non-git>` supports hashing.

- `ShadowWorkspaceService` now runs hashing from its trusted shadow Git
  working directory while passing the absolute original file pathname.
- `ShadowApplyTransaction` stages approved source bytes and snapshots all
  affected original file preimages into a per-project recovery directory
  *outside* the project. It publishes a durable `PENDING.json` manifest
  before applying the first edit, writes each updated file through a
  same-directory temporary file and replacement, and verifies destination
  SHA-256 postconditions. To prevent a newer original being silently
  snapshotted between preflight and staging, each backup must also match
  the independently verified Git blob ID (SHA-1 or SHA-256 repository format);
  mismatched or missing original preimages abort without writing.
  A failed apply attempts to restore only files whose
  bytes still match the known original or PaRuX-written versions; ambiguous
  third-party edits are left intact with recovery copies preserved.
- An exclusive named OS file lease serializes **same-state-root** project
  adoption across PaRuX processes. An existing unresolved `PENDING.json`
  prevents another adoption for that folder instead of silently overwriting
  evidence after a crash. The approved Git fingerprint is checked again after
  staging but before the first write.
- Shadow baseline `git add` and commit failures can no longer be labelled
  as success; rollback is attempted and any necessary shadow Git refresh is
  disclosed. Recovery snapshots are deleted only after an accepted transaction
  or a successful rollback (barring filesystem cleanup failure).
- Cancellation is respected between staging files and between applied file
  mutations, so STOP cannot silently continue through the remainder of the
  batch; a stopped partially completed batch takes the rollback path. File
  copy itself is synchronous, so interruption of a single large file is still
  bounded by that file's I/O rather than instantaneous.
- Source and recovery backup bytes are flushed before publishing the recovery
  journal, and temporary destination file data is flushed before replacement.
  This does not guarantee directory-entry durability through power failure.
- New `ShadowApplyTransactionTests` include injected second-write failure, STOP rollback,
  created-file rollback, successful rename, changed human destination,
  duplicate-path denial, Git object preimage match/mismatch,
  same-project lock contention, and refusal to overwrite
  an unresolved recovery journal. The focused PowerShell test filter includes
  these tests. Existing `ShadowWorkspaceServiceTests` cover the non-Git
  source-to-target path.

**Important remaining limitations:** this is *recoverable staging*, not ACID,
not a verified power-loss rollback, and not an OS filesystem sandbox.
Abrupt termination leaves a recoverable journal but no automated, independently
reviewed restart recovery UI; the operator must inspect backup contents and the
original folder. Backups need actual Windows durability/fault-injection testing.
Unrelated processes may still replace path components between link checks, and
multi-PC hosts with separate state roots share no lock. A successful shadow
Git baseline commit and an unexpected later postcondition failure may still
require manual baseline repair. Cross-file atomic commit, reliable manual
recovery workflow, independent code/build/UI verifier, executing OpenCode
isolation, browser OS-level egress confinement and real Windows acceptance
remain open. No GitHub Actions, main changes, merge or release authorized.


## A7 continuation — read-only crash recovery diagnosis (2026-10-12)

**Code committed; build/xUnit/actual crash tests NOT RUN in this environment.**

- New `ShadowApplyRecoveryInspector` validates an existing non-Git `PENDING.json`
  against the caller's exact original folder, rejects malformed, oversized,
  symlinked and traversal-bearing journal contents, verifies backup SHA-256,
  and compares each current file against recorded before/after SHA-256 hashes.
- Read-only classifications: `OriginalIntact`, `PaRuXVersionPresent`,
  `ConflictingExternalEdit`, `InvalidJournal`. The inspection itself never
  edits or deletes the original, recovery manifest, or backup files.
- `ShadowApplyTransaction.Begin` now includes this diagnosis when an
  outstanding journal blocks a new write, still refusing to resume or repeat
  a possibly partial operation automatically.
- Six new focused xUnit scenarios cover intact/partly applied/externally
  modified target state, unrelated original root, corrupted backup, and
  path traversal in a journal. The new helper is linked to
  `PaRuX.Server.Tests.csproj` and selected by local
  `tools/test-agent-platform-v2.ps1`. No GitHub Actions are used.

**Still open:** no authenticated journal format, no controlled restore/reconcile
UI, no simulated real process kill/power loss, no C# build/xUnit evidence and no
physical Windows verification. This safety inspector is **not** authorization
to discard recovery backups or proof of transaction atomicity. Same-state-root
process locking does not fence independent remote hosts.
