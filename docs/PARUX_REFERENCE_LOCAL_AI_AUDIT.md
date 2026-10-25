# PaRuX — Reference Local-AI Product Audit

Original audit date: **2026-08-28** · current-status reconciliation: **2026-09-23**

This file is retained as the named reference-local-AI audit. Its original August findings and later source-closeout claims remain historical evidence; the active status is defined by current code/tests and `PARUX_EXECUTION_PLAN.md`.

## Current status

**PARTIAL — active software gaps; PHYSICAL_PC_ACCEPTANCE_REQUIRED**

The active BIC-01–BIC-17 backlog (opened 2026-09-10 and extended 2026-09-22) supersedes prior blanket `SOURCE_SOFTWARE_COMPLETE` and “sole remaining blocker” wording. Older closeouts remain useful for evidence about what was implemented at their dates, but they are not authority for current `main`.

The current operational sources of truth are `PARUX_EXECUTION_PLAN.md`, current code/tests and `tools/release-gate.ps1`. `docs/PARUX_MAIN_SOFTWARE_CLOSEOUT_2026-09-05.md` is dated historical closeout evidence after the 2026-09-10 reopening.

## Implemented baseline retained from the earlier audit

The following capabilities are real baseline implementation, not proof that every competitive gap is closed:

- runtime tuning uses measured workload/model/hardware evidence rather than a single fixed global profile;
- context, batch/ubatch, KV, GPU placement and speculative choices are represented by the performance engine;
- prompt/prefill and TTFT evidence are separated from decode throughput;
- multi-GPU/topology and residency decisions have guarded paths;
- MTP/DFlash/DSpark/EAGLE-family speculative paths are discovery/evidence gated;
- Engine Race can compare exact managed variants and includes quality gating;
- ExLlamaV3 and Windows ML / ONNX Runtime GenAI serving exist behind `ILocalInferenceEngine` in addition to managed llama.cpp;
- structured-output capability is explicit and unsupported routes can fail closed;
- routing combines model intelligence/capability evidence with performance/hardware fit and exposes route evidence;
- MCP supports stdio plus Streamable HTTP/SSE and OAuth 2.1, with schema narrowing and deterministic concurrent sibling calls;
- durable agents have multi-repository worktrees, warm/prewarmed environments, verification, checkpoint/resume and durable `ask_user`;
- Goal Mode V2, Virtual Context V2, Semantic Index V2, VS Code V2, Visual/App Context and Execution Profiles have substantial implemented paths;
- conversation branching/version navigation and bounded long-chat rendering are implemented;
- media includes image/edit/img2img/upscale/video/audio/3D plus a Diffusers-vs-ComfyUI image Engine Race path;
- Android uses sanitized Core-owned projections and is not allowed to widen Windows authority.

These baseline statements must be read together with the active BIC rows. In particular, open correctness, compatibility, navigation, release provenance, Windows ML cancellation/recovery, benchmark validity and performance-validation work must not be hidden by the existence of the baseline feature.

## Performance truth

Source implementation does **not** prove that PaRuX is faster or more capable than every competitor on every machine. Hardware/runtime performance, model capability and product orchestration are separate evidence dimensions.

The target Windows PC still needs equivalent and explicitly documented comparisons for cold load, TTFT median/p95, prompt/prefill throughput, exact decode tok/s where real token counts exist, RAM/VRAM, concurrency, cancellation/recovery, long-context degradation, sustained thermals, coding-agent quality/time/tool overhead and media latency/quality/failure rate. The active BIC backlog also contains software-side benchmark and accounting fixes that must be completed before those measurements can support a best-in-class claim.

A speed win with lower correctness is not a win. Missing/incompatible evidence is INCONCLUSIVE, not success.

## Android / publication clarification

A physical Android phone is not a separate blocking release prerequisite under the current plan. Android JVM tests, lint and APK assembly are software validation; emulator/paired-client coverage remains part of the open workflow-quality evidence where applicable. A real phone remains useful non-blocking QA unless the canonical plan explicitly changes that boundary.

Authenticode/package signing credentials are external publication material. GitHub Actions remain disabled by project policy and must not be introduced as a substitute for local validation.

## Open blockers

Current blocking status is the **active BIC-01–BIC-17 software/evidence backlog plus `PHYSICAL_PC_ACCEPTANCE_REQUIRED`**. Each row must retain its own DONE/PARTIAL/BLOCKED-EXTERNAL truth; no blanket completeness marker may override it.
