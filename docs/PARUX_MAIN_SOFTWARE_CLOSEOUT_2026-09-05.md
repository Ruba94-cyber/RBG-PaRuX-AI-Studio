# PaRuX main software closeout — 2026-09-08

> **Historical closeout evidence — status reconciled 2026-09-23.**
> This document records the source-closeout conclusion reached on 2026-09-08. It is no longer the canonical current status. The active authority is `PARUX_EXECUTION_PLAN.md`, whose BIC-01–BIC-17 backlog reopens concrete software/evidence work. Current status is **PARTIAL — active software gaps; PHYSICAL_PC_ACCEPTANCE_REQUIRED**.

Status key used by the **2026-09-08 historical closeout**: `SOURCE_SOFTWARE_COMPLETE` and `PHYSICAL_PC_ACCEPTANCE_REQUIRED`.

**Historical meaning of `SOURCE_SOFTWARE_COMPLETE`:** at that date, the then-known implementation scope was considered closed in source. It must not be read as a claim about current `main`, nor as proof that current BIC acceptance criteria or target-PC build/test/runtime acceptance have passed.

Legacy validator compatibility: the older generic marker `PHYSICAL_ACCEPTANCE_REQUIRED` meant **only** `PHYSICAL_PC_ACCEPTANCE_REQUIRED`; it never authorized a phone, Android device, cloud CI, signing credential or other environment as an additional blocker.

The detailed statements below are preserved rather than rewritten so the repository retains the evidence and assumptions behind the 2026-09-08 closeout. Where they conflict with current code/tests or the active 2026-09-10 plan, the newer authority wins.

## Historical source status recorded on 2026-09-08 — SOURCE_SOFTWARE_COMPLETE

The 2026-09-08 `main` source was assessed as containing the software-side work that remained open in the September hardening conversations:

- Execution Profiles and Plugin Bundles are exposed through Core/desktop UI and the shared profile policy is enforced across the relevant execution surfaces.
- Goal Mode V2 uses immutable/versioned acceptance criteria and durable independent verification evidence.
- Virtual Context V2 has durable constraint/decision/supersession/contradiction state and is integrated with long-context execution.
- Semantic Index V2 is persistent, incremental and cross-repository; repository navigation now covers C, C++, headers and Rust in addition to C#/TypeScript/JavaScript/Python/Java/Kotlin/Go/Ruby, with language-specific bounded declaration extraction instead of merely whitelisting extensions. Incremental sync sends the complete changed-file set through one batched reindex pass, and CALLS topology budget is consumed only when a resolvable call edge is actually created rather than by unresolved textual matches.
- VS Code V2 carries editor selection/open-file/diagnostic context plus Fix/Explain/Test, inline review and hunk feedback contracts. Human review now also has explicit selected-hunk Accept (Git stage selected ranges) and Reject (confirmed Git revert selected ranges) actions that do not delegate the human decision back to the AI.
- Visual/App Context, coding warm environments and dependency/build prewarming are implemented in the source-side product boundary.
- Ordinary visual chat no longer requires the user's main resident itself to be multimodal. When the main resident is text-only, Core temporarily loads a proven managed vision-capable profile, produces bounded literal observation evidence, restores a known text baseline and lets normal model routing choose the main serving engine again. Image payloads are removed before the main turn, helper delimiters are neutralized, and a server-owned system guard treats helper/OCR text strictly as untrusted observations rather than instructions. Direct Computer Use keeps its stronger resident-vision requirement because screenshots there drive real input actions.
- Coding `run_command` now has deterministic capability preflight ahead of the existing no-network Windows AppContainer. Known read-only/build operations are classified, host-global/destructive operations and hidden encoded PowerShell are refused, package/network acquisition stays behind first-class authorized paths, and unknown developer commands are never labelled safe merely because they are sandboxed.
- PaRuX-managed Diffusers is a real second media engine and production Engine Race is wired as ComfyUI vs Diffusers with semantic quality judging and release-gate invariants.
- Execution Profile resource/context/reasoning/tool/delegation controls are represented and enforced rather than being UI-only fields.
- Model Hub/runtime/media/security/storage/release hardening and the project-wide nullable sweep were assessed as closed at source level.
- Windows chat rendering is bounded independently from persistent conversation length: recent turns are materialized first, older history expands on demand, and the durable conversation/context remain complete.
- Retained conversation branches have a real user-facing previous/next path navigator, active alternative counter, keyboard accelerators and full Versions dialog. Restore is fail-safe at the SQLite durability boundary: a failed durable write cannot reset the live conversation or report a false successful restore.
- MCP sibling tool calls emitted in one assistant turn execute concurrently and are reinserted deterministically in model order under the existing bounded tool-result budget.
- OpenAI-compatible local video create/list/retrieve/content routes reuse the durable PaRuX media job system instead of introducing a duplicate generation runtime.
- Media-job polling is metadata-only and terminal media payload retention is bounded; Android downloads media only on demand rather than transporting Base64 on every poll.
- Windows ML is no longer readiness-only: PaRuX ships a resident ONNX Runtime GenAI WinML worker, an `ILocalInferenceEngine`, exact execution-provider identity/fingerprint binding, a production-path Engine Race adapter and guarded AUTO promotion/fallback. The worker protocol is versioned, `enable_thinking` is bound through the model chat template instead of being silently ignored, and ORT execution-provider/device tuples are enumerated after provider registration.
- Windows ML promotion is fail-closed when device evidence is ambiguous. A single compatible ORT device is recorded as compatibility evidence only; the product deliberately does **not** mislabel that as proof that the graph executed on a physical NPU/GPU. Actual device execution remains part of the final Windows acceptance run.
- Windows ML catalog names are mapped explicitly to ORT GenAI provider aliases (`NvTensorRtRtxExecutionProvider` → `NvTensorRtRtx`, `VitisAIExecutionProvider` → `VitisAI`, `OpenVINOExecutionProvider` → `OpenVINO`, `QNNExecutionProvider` → `QNN`). Unsupported GenAI providers are rejected rather than advertised.
- A Windows ML AUTO winner must reference the same logical model/revision and an exact serveable ORT GenAI ONNX variant plus the measured provider runtime identity. Several providers/builds within the same `windows-ml-genai` engine family can coexist in Engine Race; an ambiguous same-family race is rejected unless both exact identities are pinned. An execution-provider update invalidates old evidence and forces a new race.
- Runtime benchmark workloads are evidence-aware: long-context sends a genuinely large corpus and validates observed prompt tokens; sustained validation repeats real generations for a configurable duration; missing telemetry is labeled as missing instead of being indistinguishable from a genuine zero.
- External inference benchmark gates no longer count SSE chunks as tokens or fabricate `chars/4` token estimates. Empty visible output, missing usage, hidden reasoning and `finish_reason=length` are inconclusive and cannot win.
- Coding-agent competitive benchmarking uses the same repository-changing corpus for PaRuX and external CLI agents (including Codex/Claude when configured), isolated worktrees from the same source commit and a harness-owned deterministic verifier. Required build/test commands are stored in the corpus and run by the benchmark after the agent exits; self-reported success is ignored.
- Server-side planner/research/review/documentation/testing/security delegation and desktop Coding Agent delegation share one Core-owned prepare → repository-authority inheritance → atomic parent/child commit protocol. Single-child and parallel 2–6-child fan-out therefore cannot expose a runnable child before required repository authority is durable or pause a parent without the matching runnable children.
- Context Graph has bounded multi-hop traversal plus versioned export surfaces under the desktop/local authority boundary; paired Android remains explicitly denied those workstation context-administration APIs.
- Automations cover schedule, file, Git, model, component and task events plus bounded declarative `custom_condition` triggers over allow-listed Core task/download/component state. Custom conditions are first-observation seeded, fire only on false→true edges, re-arm after false, exclude same-automation task outputs from task-count signals and never evaluate script/shell/arbitrary expressions.
- Android companion parity includes Chat, media/jobs, Live Voice, Projects/PC Studio, Goal Mode V2, shared Execution Profiles, sanitized Observability V2, Models, Tools and supervised Computer Use while preserving desktop-only security boundaries.
- Android uses dedicated sanitized `/api/v2/mobile/*` projections and cannot self-grant workstation capabilities or acquire direct Windows filesystem/shell/input authority.
- Android source/API parity, JVM tests, lint and APK assembly are software validation. Emulator/instrumented coverage may run on the Windows development PC; a physical Android phone is optional exploratory QA and is **not a release blocker**.
- The old `RunManagedImageRaceAsync` integration concern was assessed as closed: the production endpoint calls the managed two-engine path and the release gate checks that wiring.
- The old predecessor-brand update-channel repository reference is superseded by the current `Ruba94-cyber/RBG-PaRuX-AI-Studio` release repository. Legacy-name occurrences that remain are migration/compatibility or archived historical evidence, not active product identity.
- GitHub Actions were and remain disabled by project policy; authoritative build/test/release evidence is produced locally.

The sentence “no source feature is intentionally left in an intermediate implementation state” belonged to this dated assessment. The 2026-09-10 review supersedes that conclusion with specific BIC gaps; those gaps must be closed rather than reclassified as environment limitations.

## Historical software gate contract

At the time of this closeout, `tools/release-gate.ps1` and `tools/validate-software-complete.ps1` defined the repository/software boundary. Current gate behavior is now reconciled to the canonical BIC backlog; the list below is retained as dated scope evidence:

- canonical Release x64 solution build and Core regression tests;
- Windows shipped-project publishability, including the Windows ML worker, before installer harvesting;
- static security/runtime/coding/multi-repository/Engine Race invariants;
- deterministic coding-command preflight plus the no-network AppContainer boundary;
- delegated visual-chat routing, bounded/untrusted vision evidence and anti-deadlock scheduling order;
- Semantic Index batched incremental reindex and topology-budget regression coverage;
- MCP parallel-call regression coverage and bounded transcript handling;
- Windows ML GenAI layout/protocol/provider-alias/runtime-identity/device-evidence/AUTO-selection regression coverage;
- retained conversation-branch durability/navigation source contracts;
- native C/C++/Rust repository-intelligence coverage;
- VS Code V2 tests, including hunk Accept/Reject authority boundaries;
- atomic specialist/coding delegation recovery, fan-out and authority-inheritance regression coverage;
- bounded custom-condition automation validation, edge-trigger/re-arm and self-loop regression coverage;
- fail-closed competitive benchmark source contracts and harness-owned coding-agent verification;
- Android sanitized V2 boundary checks, clean JVM tests, lint, debug APK and unsigned release APK build;
- absence of active GitHub Actions workflows by project policy.

## Physical acceptance evidence that remained required

Even under the 2026-09-08 source-closeout conclusion, target **Windows PC** acceptance remained unperformed. The required evidence included:

- full local software gate success against the exact intended `main` SHA;
- installer clean install/upgrade/uninstall lifecycle on Windows;
- real Windows desktop UI, retained-conversation previous/next/history interactions, tray, overlays, clipboard/file pickers, Computer Use and local browser/network/device-I/O behavior;
- text-only-main visual chat using the managed vision helper, including exact model/routing recovery, cancellation/STOP cleanup and image-text prompt-injection resistance on the real runtime;
- real GPU runtime selection, VRAM pressure, cancellation/recovery, context, sustained load and thermal behavior;
- real ComfyUI/Diffusers/media/training/image/video/audio/3D execution on the intended GPU stack;
- Windows ML execution-provider discovery/readiness, resident ORT GenAI generation, thinking on/off behavior for a compatible model, STOP/cancellation/reload/fallback and observation that the intended physical GPU/NPU/CPU actually executes the workload;
- cross-vendor Windows ML/Engine Race where the target hardware exposes eligible providers, with exact provider/device evidence and proof that only measured same-quality winners are promoted;
- same-hardware Engine Race for eligible GGUF/EXL3/ONNX variants;
- same-machine inference and coding-agent competitor benchmarks needed for any measured best-in-class claim, recording competitor versions/models/settings and treating inconclusive evidence as no win;
- sustained/thermal/soak execution long enough to expose throttling, memory leaks or cancellation/reload degradation;
- Android build/lint/unit validation and, where an emulator is available, paired-client smoke/instrumentation from that same PC. Physical-phone camera, OEM background-management and radio-specific behaviour are non-blocking QA rather than release acceptance.

Owner-controlled code signing remains a publication credential step, not a substitute for source or device validation.

## Current interpretation after 2026-09-10 reopening

The historical `SOURCE_SOFTWARE_COMPLETE` marker above is retained for auditability only. **Current project status is `PARTIAL — active software gaps; PHYSICAL_PC_ACCEPTANCE_REQUIRED`**, and active BIC-01–BIC-17 rows in `PARUX_EXECUTION_PLAN.md` are the software/evidence work to close before a new completeness claim can be made.
