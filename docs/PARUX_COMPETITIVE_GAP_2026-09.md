# PaRuX competitive reference closeout — September 2026

Current as of **2026-09-08**.

This document is the active competitive-gap summary. Historical snapshots remain in Git history. The current repository source of truth is code/tests plus `docs/PARUX_MAIN_SOFTWARE_CLOSEOUT_2026-09-05.md`, `PARUX_EXECUTION_PLAN.md` and the executable physical procedure `docs/PHYSICAL_PC_ACCEPTANCE_2026-09-08.md`.

## Status

**SOURCE_SOFTWARE_COMPLETE; PHYSICAL_PC_ACCEPTANCE_REQUIRED**

`SOURCE_SOFTWARE_COMPLETE` is an implementation marker, not a claim that the current `main` SHA already passed every target-PC measurement. No known functional or source-side performance-enablement gap in the Windows reference scope is intentionally deferred to another implementation phase. Remaining claims that depend on hardware or competitors require the final same-machine evidence.

## Competitive bar represented in source

The baseline targets the material capabilities of current local-AI workstations and coding-agent leaders rather than one product clone. The comparison includes LM Studio/Jan/Ollama/Open WebUI/Unsloth-style local-runtime patterns and Codex/Claude Code-class coding workflows. Capability presence is not treated as proof of a benchmark win.

### Local inference and performance

- PaRuX-managed llama.cpp CUDA/Vulkan runtime.
- Continuous batching, parallel serving, prompt/KV cache controls and measured tuning.
- MTP/DFlash/DSpark-style speculative discovery with evidence-driven promotion/rollback.
- Integrated ExLlamaV3 text serving for exact managed variants.
- Integrated Windows ML / ONNX Runtime GenAI text serving with provider discovery, explicit provider-alias mapping, provider-library fingerprint/runtime identity, cancellation/reload/fallback and exact-variant AUTO eligibility.
- Windows ML worker protocol v2 binds `enable_thinking` through the model template rather than ignoring it.
- ORT EP/device tuples are enumerated after provider registration. One compatible device is compatibility evidence only; actual NPU/GPU execution must be observed on hardware.
- Multiple runtime/provider identities may coexist within one engine family. A same-family Engine Race is rejected unless both exact identities are pinned.
- Engine Race promotion remains guarded by logical model/revision/variant identity, quality, TTFT, throughput, VRAM, thermal, power and reliability evidence.
- Long-context race input is a genuinely large corpus with observed prompt-token validation; sustained mode repeats real generations for a bounded duration.
- Missing performance telemetry is explicitly labelled missing instead of silently becoming an apparently real zero.

### Reference-grade benchmark rules

The benchmark harnesses have one fail-closed evidence policy:

- SSE chunks are never called tokens.
- `chars/4` or similar token estimates cannot produce a competitive decode win.
- Exact decode tok/s requires exact usage plus a non-reasoning comparable stream.
- Empty visible output, hidden-reasoning ambiguity, missing usage/finish reason and `finish_reason=length` are **INCONCLUSIVE**, not wins.
- First-request timing is not relabelled as cold model load unless model residency was independently reset/proven.
- Same-machine matrices require equivalent model/revision/quantization/context/output and quality policy.

Relevant harnesses:

- `scripts/benchmark-reference-stream.ps1`
- `scripts/benchmark-competitive-inference.ps1`
- `scripts/benchmark-local-ai-competitors.ps1`
- `scripts/benchmark-coding-agent-competitors.ps1`

### APIs and interoperability

- OpenAI-compatible Chat Completions, Responses, embeddings and local Videos create/list/retrieve/content surfaces.
- Anthropic-compatible Messages.
- Stateful `previous_response_id` continuation.
- Structured output, reasoning transport and edit/continue where the serving engine genuinely supports them.
- Function tools/tool calls/tool results across compatibility adapters.
- Images returned by tools through bounded references without leaking local-file authority.
- Bounded tool-call self-healing/retry.

### MCP and agentic tools

- stdio and Streamable HTTP/SSE MCP transports.
- OAuth 2.1/dynamic registration and protected hosted-server tokens.
- Per-server/per-skill allowlists at schema advertisement and dispatch.
- Prompt-aware schema narrowing.
- Sibling tool calls from one model round execute concurrently and return in deterministic model order.
- Built-in knowledge/research/web operations under explicit network authority.

### Chat / context / knowledge

- Durable conversation history plus reversible branch/version retention for Regenerate/Edit & Continue.
- Previous/Next retained-path navigation, active alternative counter, keyboard accelerators and full Versions dialog.
- Branch restore crosses a real SQLite durability boundary; failed persistence cannot replace the live path and claim success.
- Bounded Windows chat visual window while durable conversation/model context remain complete.
- Projects, Knowledge, Artifacts, Memory/RAG and provenance.
- Virtual Context V2 durable constraint/decision/supersession/contradiction ledger.
- Semantic Index V2 persistent/incremental/cross-repository search. Incremental sync batches changed files into a single repository reindex pass, and unresolved call-like text no longer consumes CALLS topology budget before a real edge is created.
- Repository intelligence now covers C/C++/headers/Rust in addition to C#/TS/JS/Python/Java/Kotlin/Go/Ruby, with declaration extraction rather than extension-only indexing.
- Visual/App Context and annotations.
- A text-only main model can answer visual chat through a temporary managed vision helper. The helper emits bounded literal evidence, pixels are removed before the main turn, server-owned prompt hierarchy marks OCR/helper output as untrusted data, and the normal routing layer then chooses the user's main serving engine. Computer Use keeps direct vision requirements because screenshots there authorize real actions.

### Coding / agents / Computer Use

- Durable dependency-aware task graph with bounded parallel scheduling under Execution Profiles.
- Coding worktrees, explicit multi-repository authority and checkpoint/resume from real loop state.
- Warm development environments plus dependency/build prewarming.
- Durable `ask_user` pause/answer/resume.
- Delegated-child evidence returns to the parent without loading child transcripts; independent DAG nodes and worker tasks can execute concurrently subject to the configured budget.
- Goal Mode V2 immutable/versioned acceptance criteria plus verification evidence.
- VS Code V2 selection/open-files/diagnostics plus Fix/Explain/Test, inline review and hunk feedback.
- Human selected-hunk **Accept** stages the selected Git ranges; **Reject** reverts selected ranges only after modal confirmation. These decisions do not call a write-capable AI task.
- Supervised Computer Use with Windows-owned authority and Universal STOP.
- Coding shell execution has a deterministic preflight in addition to Windows AppContainer confinement: recognized host-global/destructive and encoded operations are refused before launch, dependency/network acquisition stays in first-class authorized flows, and an unknown command is never promoted to a false “safe” classification merely because it is sandboxed.

### Coding-agent competitive measurement

`benchmark-coding-agent-competitors.ps1` can compare PaRuX with Codex, Claude Code and other non-interactive CLIs without hard-coding volatile vendor command-line flags. Each competitor receives:

- the same benchmark corpus;
- an isolated worktree from the same source commit;
- the same deterministic required-file/text/path-scope checks;
- harness-owned `verificationCommands` executed **after** the agent exits.

Agent prose and self-reported tests do not count. Deterministic task success is primary; wall time is only a tie-breaker after equal success. Publication evidence must record each CLI version, model, reasoning/effort setting, permission mode and account/tier when those change capability.

### Media / training

- Image generation, edit/img2img, upscale, video, audio STT/TTS and 3D generation.
- Independent PaRuX-managed Diffusers path alongside ComfyUI.
- Production image Engine Race with semantic quality judging; speed alone cannot promote lower quality.
- Media GPU serialization/residency coordination, cancellation and bounded payload retention.
- Android media download on demand instead of Base64 on every poll.
- Dataset → Train → Adapter → Eval / AutoLab and six supported training methods across product surfaces.

### Product / operations

- Headless `parux serve` and loopback browser client.
- Secure Android companion with sanitized Core-owned projections and remote-deny-by-default routing.
- Execution Profiles, Plugin Bundles, Workflows, Automations and observability/benchmark dashboards.
- Automations include schedule, file, Git, model, component and task events plus bounded declarative `custom_condition` triggers over allow-listed Core state. Custom conditions are false→true edge-triggered, self-loop guarded for task-count signals and never evaluate script/shell/arbitrary expressions.
- Backup/restore, storage quotas, runtime/update integrity and Gaming hard-off.
- GitHub Actions deliberately absent; local release/validation is authoritative.

## Not software gaps

- **Physical Android phone**: optional exploratory QA. JVM/lint/APK validation is software-gate work; emulator smoke may run on the final Windows PC.
- **Authenticode certificate**: maintainer publication credential, not missing application behavior.
- **winget/package-manager submission**: distribution work after a stable artifact exists.
- **macOS/Linux native desktop shells**: outside the Windows reference-product acceptance scope.
- **LAN browser replacing pairing**: intentionally not required; remote authority remains behind the paired-device boundary.
- **GitHub Actions/cloud CI**: disabled by project policy.

## What source cannot prove

Backend speed, actual GPU/NPU assignment, VRAM pressure, driver behavior, thermals, competitor versions and quality/performance tradeoffs are machine-specific. The target-PC matrix must therefore record at least:

- model load/start behavior;
- TTFT median/p95;
- prompt/prefill and exact decode throughput;
- RAM/VRAM peak and idle residency;
- concurrent scaling;
- cancellation/recovery and STOP/reload;
- long-context degradation;
- sustained/thermal/soak behavior;
- embedding/retrieval latency and quality where compared;
- coding-agent task success/time/tool overhead;
- media latency/quality/failure rate;
- Windows ML provider/device performance and actual physical execution;
- real text-only-main → vision-helper → main-route behavior and cancellation cleanup on the target runtime.

A comparable competitor win becomes an optimization defect. A quality regression cannot be hidden by speed. An unavailable/inconclusive cell is not a PaRuX win.

## Existing same-machine evidence

The 2026-09-07 reference-workstation run used AMD Radeon 780M/Vulkan and identical `Qwen3.5-4B-Q4_K_M.gguf` bounded prompts. PaRuX returned 3/3 visible answers at roughly 22.6–24.6 exact decode tok/s; LM Studio and Ollama runs exposed bounded-token/hidden-reasoning failure modes in that specific profile. This remains useful evidence but is **one model and one hardware/profile**, not the complete matrix.

The execution plan also records physical Windows work already completed on 2026-09-08, including release-gate reruns, real WinUI/runtime error recovery fixes and an MSI install/uninstall cycle. Those results remain valid evidence for their recorded SHA/configuration; they do not automatically close different provider, model, upgrade/downgrade, media, soak or competitor cells for a later SHA.

## Sole remaining blocker

**PHYSICAL_PC_ACCEPTANCE_REQUIRED**

Use `docs/PHYSICAL_PC_ACCEPTANCE_2026-09-08.md` as the blocking matrix for the exact candidate SHA. No Android phone, cloud workflow, signing certificate, package-manager submission or other device is an additional release prerequisite.