# PaRuX Best-in-Class Audit — 2026-08-31, closed 2026-09-07

This dated audit remains useful as historical competitive evidence, but its original `MISSING`/`PARTIAL` backlog is no longer current. Git history preserves the detailed 2026-08-31 matrix and proposed work.

## Current status

**SOURCE_SOFTWARE_COMPLETE; PHYSICAL_PC_ACCEPTANCE_REQUIRED**

Current source of truth:

- `docs/PARUX_MAIN_SOFTWARE_CLOSEOUT_2026-09-05.md`
- `PARUX_EXECUTION_PLAN.md`
- `docs/PARUX_COMPETITIVE_GAP_2026-09.md`
- `docs/PARUX_FINAL_VALIDATION_REPORT.md`
- `tools/release-gate.ps1`

## Closure of the original best-in-class gaps

- **Parallel inference / batching** — managed llama.cpp exposes continuous batching and measured launch/runtime tuning rather than a fixed launcher profile.
- **Speculative decoding** — MTP/DFlash/DSpark/EAGLE-family discovery and promotion are evidence gated.
- **Multi-engine inference** — llama.cpp CUDA/Vulkan baseline plus integrated ExLlamaV3 and Windows ML / ONNX Runtime GenAI serving; exact model/runtime/hardware Engine Race evidence governs AUTO promotion and fallback.
- **VRAM/residency scheduling** — execution profiles, GPU residency reservation, media/text coordination, model fit and measured runtime decisions prevent incompatible hidden residency; actual hardware pressure remains part of the final PC acceptance run.
- **Structured output** — the engine-neutral capability is explicit. Managed llama.cpp handles constrained output; alternative engines that do not genuinely implement it do not advertise it and are excluded from structured turns.
- **MCP** — stdio + Streamable HTTP/SSE + OAuth 2.1, per-server/per-skill allowlists, prompt-aware schema narrowing and deterministic concurrent sibling tool execution.
- **Agentic Knowledge** — `parux_kb_search` plus bounded read-only `parux_kb_exec`, including range/grep/find/stat-style exploration under RAG-root containment.
- **Subagents / goal completion** — durable DAG tasks, independent delegated context, narrowed capabilities, finish/verification contracts, Goal Mode V2, bounded retries, checkpoint/resume and durable `ask_user`.
- **Computer Use** — Windows authority remains explicit, supervised and stoppable; paired Android can supervise/approve without acquiring raw workstation authority.
- **Training/eval/promotion** — integrated Dataset → Train → Adapter → Eval / AutoLab with six supported training methods exposed consistently.
- **Conversation UX/context** — persistent branch versions plus user navigation, bounded Windows long-chat rendering, Virtual Context V2 and cross-repository Semantic Index V2.
- **Media** — image/edit/img2img/upscale/video/audio/3D plus real ComfyUI-vs-managed-Diffusers image Engine Race with semantic quality judging; polling/payload retention is bounded.
- **Protocol breadth** — OpenAI Chat/Responses/Embeddings/Videos and Anthropic Messages compatibility, including stateful Responses and multimodal tool results where supported.
- **Android parity** — safe sanitized control-plane projections cover current companion surfaces; physical-phone behavior is non-blocking exploratory QA.

## Registry truth

The live built-in inference registry does not advertise planned or experimental runtimes. An engine is visible as a working built-in only after a callable adapter exists; old retired ids may remain `Disabled` solely so persisted configuration/evidence is recognizable.

## Best-in-class performance rule

The software now contains the mechanisms required to compete and optimize, but a blanket performance claim still needs the target PC. Same-machine comparison must keep model/revision/quantization/context/output/quality equivalent and separately measure cold start, TTFT, prompt/prefill throughput, exact decode tok/s, RAM/VRAM, concurrency, cancellation/recovery, long-context behavior, sustained thermals, coding-agent quality/time/tool overhead and media latency/quality/failure rate.

Any measured competitor win becomes an optimization defect. A faster result with materially worse quality can never be promoted as a winner.

## Sole remaining blocker

No physical Android phone, cloud CI workflow, signing certificate, package-manager submission or other device is an additional release prerequisite.

**PHYSICAL_PC_ACCEPTANCE_REQUIRED**
