# PaRuX reference-grade audit — 2026-09-06, closed 2026-09-07

This dated audit is retained as evidence of the hardening pass that started on 2026-09-06. Its original `REFERENCE_CANDIDATE`/source-hardening status has been superseded by later commits on `main`.

## Current status

**SOURCE_SOFTWARE_COMPLETE; PHYSICAL_PC_ACCEPTANCE_REQUIRED**

The authoritative current state is defined by:

- `docs/PARUX_MAIN_SOFTWARE_CLOSEOUT_2026-09-05.md`
- `PARUX_EXECUTION_PLAN.md`
- `docs/PARUX_COMPETITIVE_GAP_2026-09.md`
- `docs/PARUX_FINAL_VALIDATION_REPORT.md`
- `tools/release-gate.ps1`

## Closure of the gaps identified by this audit

The source-side gaps that were open when this audit began are now closed:

- **Conversation branching** — durable branch snapshots, path-independent attachment identity, branch-aware attachment retention/pruning and user-facing version navigation are implemented. Long histories also use bounded Windows visual rendering without truncating durable conversation/context state.
- **Benchmark correctness** — the reference streaming benchmark no longer treats SSE chunks as tokenizer tokens; exact decode tok/s is reported only from real usage counts, with TTFT/other metrics separated.
- **Windows ML** — no longer readiness-only. The current source includes a resident ONNX Runtime GenAI WinML worker, `WindowsMlGenAiLocalInferenceEngine`, exact provider alias/fingerprint/runtime identity, a benchmark/Engine Race adapter and guarded AUTO selection/fallback for exact serveable ONNX variants.
- **Generic human-in-loop** — durable agent `ask_user`/answer/resume is implemented in the coding-agent protocol/task ledger and exposed through desktop plus the safe paired-client response surface.
- **Tool concurrency** — sibling MCP tool calls from one model round execute concurrently through a deterministic batch executor and are reinserted in model order.
- **Modern compatibility** — Responses/Anthropic tool and multimodal-result translation, local Videos compatibility and stateful response continuation are present in the current Core path.
- **Media scalability** — job-list polling is metadata-only, retained terminal payloads are bounded and Android fetches media on demand.
- **Runtime truthfulness** — built-in inference registry entries exposed to users are integrated engines or disabled legacy identities; non-existent experimental engines are not advertised.

## Release truth rule

A source capability is counted only when its production path is callable and guarded by the corresponding tests/gates. A descriptor or documentation entry alone is not implementation evidence.

Performance supremacy is different: it cannot be proved from source. It requires same-machine evidence using equivalent model/revision/quantization/context/output/quality conditions. Any competitor win found by that run becomes a source optimization defect and must be fixed before final acceptance.

## Sole remaining acceptance

There is no mandatory Android-phone test, GitHub Actions run, signing certificate, package-manager submission or second implementation phase in the current release plan.

The only blocking step is the final acceptance and same-hardware benchmark procedure on the target Windows workstation:

**PHYSICAL_PC_ACCEPTANCE_REQUIRED**
