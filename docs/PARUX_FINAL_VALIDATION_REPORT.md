# PaRuX — Current Best-in-Class Validation Report

**Current status date:** 2026-09-23
**Branch:** `main`

## Status

**PARTIAL — active software gaps; PHYSICAL_PC_ACCEPTANCE_REQUIRED**

The 2026-09-10 competitive review reopened concrete correctness, compatibility, usability, release-provenance and benchmark-validity work. This current report therefore supersedes the earlier blanket `SOURCE_SOFTWARE_COMPLETE` / “sole remaining blocker” conclusion while preserving the older reports as dated evidence.

The canonical open work is BIC-01–BIC-17 in `PARUX_EXECUTION_PLAN.md` (BIC-16 was added on 2026-09-22). A feature being present in source does not establish that its current acceptance criteria, failure/recovery paths, compatibility semantics or same-machine performance evidence are complete.

## Implemented baseline

Current `main` already contains substantial reference-product foundations, including:

- bounded long-chat rendering and durable conversation/context storage;
- persistent reversible conversation branching and version navigation;
- concurrent sibling MCP tool execution and permission-aware MCP transports;
- OpenAI/Anthropic compatibility surfaces;
- local video/media job paths and bounded media retention;
- sanitized Android control-plane boundaries;
- durable agent `ask_user`/answer/resume, multi-repository coding authority, worktrees, checkpoints and verification;
- Execution Profiles, Goal Mode V2, Virtual Context V2, Semantic Index V2, Visual/App Context, VS Code V2 and observability surfaces;
- ComfyUI / managed Diffusers Engine Race paths;
- ExLlamaV3 and Windows ML / ONNX Runtime GenAI integration paths;
- speculative-decoding discovery and runtime performance evidence paths;
- local release tooling that rejects GitHub Actions workflows by project policy;
- Android JVM/lint/APK software-validation paths without making a physical phone a separate release gate.

These are baseline capabilities, not blanket DONE evidence. The active plan identifies where implementation or proof is still insufficient.

## Current software/evidence work

The active BIC backlog covers, among other items:

- complete Virtual Context active-state correctness beyond bounded history windows;
- immutable-start coding benchmark scope audits and fail-closed competitive winner rules;
- representative independent evaluation rather than a five-task smoke corpus;
- unified GPU/backend inventory and shared-memory accounting;
- explicit Ollama protocol semantics;
- live WinUI navigation reachability;
- commit-linked release provenance and usable signed update delivery;
- Windows ML cancellation/residency/capability honesty;
- coding orchestration/review performance, incremental semantic indexing and tokenizer-bound context budgeting;
- workbook, media/training/voice and browser/mobile workflow depth;
- backup-pool isolation, strict-local inference and listener-race validation.

A BIC row remains PARTIAL until its applicable implementation and tests are complete and any required executable/physical evidence has actually been collected.

## What is intentionally not a separate blocker

- A **physical Android phone** is optional exploratory QA under the current canonical boundary; emulator/paired-client workflow evidence may still be required by an applicable BIC row.
- **Authenticode/Android signing credentials** are maintainer publication credentials. Missing credentials are BLOCKED-EXTERNAL only for the corresponding publication proof, not for source checks that can be completed without them.
- **winget/public distribution submission** is not application source completeness.
- **GitHub Actions/cloud CI** remain disabled by project policy; local validation is authoritative.
- Native **macOS/Linux desktop-shell parity** is outside the Windows reference-product acceptance scope.

## Required local software gate

On the target Windows PC, use the current local gate:

```powershell
powershell -ExecutionPolicy Bypass -File tools\release-gate.ps1 -Restore
```

A green gate is necessary executable evidence. It must not be interpreted as automatically closing active BIC rows that require additional behavioral, competitor, packaging or physical validation.

## Physical Windows acceptance

After the relevant software work is implemented, execute the produced binaries and acceptance matrix on the same intended Windows workstation. Record real evidence for:

1. clean restore/build/test/publish and MSI install/upgrade/uninstall lifecycle;
2. real WinUI/tray/overlay/clipboard/file-picker/browser/Computer-Use behavior;
3. real GPU/NPU model loading and execution, VRAM/RAM pressure, cancellation/recovery, sustained load and thermals;
4. image/video/audio/3D/training and ComfyUI/Diffusers runtime behavior;
5. Windows ML resident ONNX Runtime GenAI generation, cancellation, recovery/fallback and actual execution-provider assignment;
6. same-hardware Engine Race for eligible variants;
7. same-machine competitor benchmarks using valid equivalent or explicitly documented comparison settings;
8. Android unit/lint/APK validation plus the paired-client/emulator coverage required by the active plan.

For performance claims, record cold start, TTFT median/p95, prompt/prefill throughput, exact decode tok/s where real token counts are available, RAM/VRAM, concurrency, long-context degradation, cancellation recovery, coding-agent time/quality/tool overhead and media latency/quality/failure rate. A lower-quality, failed, truncated or incomparable result cannot win because it is faster.

Any defect discovered by executable or physical validation returns to source and must be fixed before acceptance passes.

## Current pre-acceptance state

**Open blockers: active BIC-01–BIC-17 work/evidence plus `PHYSICAL_PC_ACCEPTANCE_REQUIRED`.**

Historical release/closeout documents may describe what was believed complete at their dates; they do not override this current status.
