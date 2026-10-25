# PaRuX vs. Odysseus — current scope closeout

Original comparison: August 2026 · refreshed **2026-09-07**.

The original detailed feature-by-feature audit remains available in Git history. It was valuable for finding concrete product ideas, but many rows became stale as PaRuX evolved. This file now records the current conclusion rather than preserving old `missing` labels as if they were active backlog.

## Current PaRuX status

**SOURCE_SOFTWARE_COMPLETE; PHYSICAL_PC_ACCEPTANCE_REQUIRED**

Odysseus remains a useful reference, but it is not the product definition. PaRuX is a **single-owner, Windows-reference local-AI workstation with a secure Android companion**, not a multi-user personal-productivity web server. Feature parity is required for capabilities that improve local AI inference, agents, coding, knowledge, media, training, interoperability, security or operation; unrelated CRM/PIM breadth is not added merely to match a checkbox.

## AI-relevant gaps from the original audit that are now closed

- **Model Manager** — managed model discovery/download/resume/integrity/hardware-fit, multi-format identities, benchmarks and explicit lifecycle are current product surfaces.
- **MCP** — outbound stdio plus Streamable HTTP/SSE, OAuth 2.1, encrypted tokens, allowlists, prompt-aware schema narrowing and deterministic concurrent sibling calls are implemented.
- **Deep Research / web** — bounded search/fetch/research paths plus current/perishable intent handling exist under explicit Internet policy.
- **Semantic Memory/RAG** — local embeddings, hybrid retrieval, Projects/Knowledge/Artifacts, agentic `parux_kb_search` and bounded read-only `parux_kb_exec` are implemented.
- **Computer Use** — real Windows GUI automation is task-scoped, approval/authority bounded and covered by Universal STOP/Gaming hard-off.
- **Blind Model Compare** — the current repository contains `ModelCompareService`, Core endpoints/client UI and regression tests; the old “does not exist” row is obsolete.
- **Group model interaction** — current source additionally contains Group Chat support for multiple local models, so the comparison is no longer limited to a single-response launcher model.
- **Installer/bootstrap** — current release path publishes self-contained shipped executables and builds an MSI locally; lack of a public signed artifact is distribution/credential state, not missing application code.
- **Browser/headless use** — `parux serve` plus loopback browser Chat exists while private-LAN remote use remains on the pinned paired-device security model.
- **Notes/mail/calendar knowledge** — PaRuX can connect read-only local notes, Thunderbird/EML mail and ICS calendar sources and retrieve from them as local knowledge. This deliberately does not grant the model arbitrary write/send authority.
- **Media gallery/use** — generated image/video jobs are browsable in the Android Media surface and Windows creation/artifact paths; payloads are fetched on demand rather than repeatedly copied through polling.
- **Security** — single-owner process token + pinned paired-device credentials is the intended trust model; multi-user passwords/TOTP are not omitted authentication for the supported deployment model.

## Deliberate scope differences, not PaRuX gaps

Odysseus includes personal-information-management/server features such as multi-user accounts, CalDAV-style calendar management, IMAP/SMTP mailbox operations and general notes/tasks management. PaRuX intentionally does not become a mail/calendar/CRM server to claim numerical feature parity.

PaRuX instead keeps connected desktop data read-only unless a separately designed, explicit write surface exists. This is important for a local agent product: “can read my calendar/mail as context” does not silently become “can send mail or alter events.”

Likewise, a general-purpose multi-user web deployment is not the Windows reference target. PaRuX uses loopback browser access for local use and its authenticated/pinned Android companion for private-LAN control rather than weakening the remote boundary to imitate a browser-first server.

## Areas where current PaRuX differentiates

- measured intelligence-aware model routing independent from simple tok/s ranking;
- exact model/runtime/hardware Engine Race evidence and guarded AUTO promotion;
- managed llama.cpp CUDA/Vulkan plus integrated ExLlamaV3 and Windows ML / ONNX Runtime GenAI alternatives;
- persistent agent DAG, worktrees, multi-repository authority, verification, checkpoint/resume and durable `ask_user`;
- Virtual Context V2 and cross-repository Semantic Index V2;
- VS Code V2/editor context and Computer Use under one Core authority model;
- image/edit/img2img/upscale/video/audio/3D plus independent managed Diffusers-vs-ComfyUI image Engine Race with semantic judging;
- Dataset → Train → Adapter → Eval / AutoLab with six training methods;
- Gaming hard-off and explicit GPU/media/text residency coordination;
- modern OpenAI/Anthropic-compatible local protocol surfaces including Responses state, multimodal tool results and local Videos compatibility;
- native WinUI Windows shell plus native Kotlin/Compose Android companion rather than requiring a web wrapper.

## Performance comparison rule

No dated comparison can establish permanent performance superiority. The final target-PC run must compare equivalent model/revision/quantization/context/output/quality conditions and measure cold start, TTFT, prompt/prefill throughput, exact decode tok/s, memory/VRAM, concurrency, cancellation/recovery, long-context behavior, sustained thermals, coding-agent quality/time/tool overhead and media quality/latency/failure rate.

Any competitor win in that matrix returns to PaRuX as an optimization defect. A lower-quality result cannot win because it is faster.

## Sole remaining blocker

A physical Android phone, multi-user PIM features, public signing certificate, package-manager submission and GitHub Actions are not additional release blockers for the Windows reference product.

**PHYSICAL_PC_ACCEPTANCE_REQUIRED**
