# PaRuX Source Provenance

This file records the **origin relationship** of code and major optional runtimes used by PaRuX. It is intentionally narrower than `THIRD_PARTY_NOTICES.md`: this document answers *where code came from and whether it is incorporated*, while the notices file records redistribution/license obligations for build artifacts.

The current source tree is authoritative. Historical implementation logs may describe experiments or abandoned integrations that are no longer part of the product.

## 1. PaRuX-authored repository code

Unless a file or directory carries a separate upstream notice, the Windows application, PaRuX Core/server, CLI, Update Helper, Android application, tests, packaging glue, API contracts, orchestration/routing logic and project documentation in this repository are PaRuX project code under the repository's FSL-1.1-MIT license (each version converts to MIT two years after release).

The historical internal name **PaRuX** remains in namespaces, package ids, environment variables and `%LOCALAPPDATA%\PaRuX` paths for compatibility. That does not indicate a separate upstream project.

## 2. Vendored upstream source: llama.cpp conversion subset

`llama-cpp-conversion/` is the important exception to the general rule above. It contains a vendored conversion subset derived from the upstream **llama.cpp** project, including:

- `convert_lora_to_gguf.py`
- `convert_hf_to_gguf.py`
- the supporting `conversion/` package used by those scripts

This source is used by PaRuX's local adapter/model conversion workflow. It is **not PaRuX-authored source** and must not be represented as such.

llama.cpp is MIT-licensed. The upstream MIT license text is retained verbatim at:

`llama-cpp-conversion/LICENSE-llama.cpp`

`THIRD_PARTY_NOTICES.md` also includes this vendored subset in the Windows redistribution boundary. Any future refresh of these scripts must preserve the upstream license and should record the exact upstream revision used for that refresh.

## 3. Reference-only project: Odysseus

**Odysseus (`odysseus-dev/odysseus`) was used as a product/design reference only. No Odysseus source code is incorporated into PaRuX.**

Its ideas around skills, memory/research surfaces and local-agent workflows were useful comparative input, but PaRuX's implementation was independently designed for its C#/.NET + Kotlin architecture and its own Core-owned routing/permission model.

Odysseus is AGPL-3.0. Because its source is not copied, adapted, linked or redistributed by this repository, its AGPL source is not part of the PaRuX distribution.

## 4. Optional external runtimes — not vendored into PaRuX binaries

PaRuX can discover an existing user-managed runtime or, for selected components, download/install an isolated PaRuX-managed copy on explicit user request. `PARUX_MANAGED` describes lifecycle ownership only; it never changes upstream authorship or licensing.

### llama.cpp / llama-server

Local GGUF inference/runtime executable. PaRuX may locate a user installation or install verified upstream release assets outside the repository. Runtime binaries are not intentionally embedded in the MSI. The vendored conversion scripts described in section 2 are a separate redistribution boundary.

### ComfyUI

Optional local image/video generation backend running as a separate process. PaRuX communicates with it over localhost and may manage an installation on explicit user request. ComfyUI source is not incorporated into PaRuX assemblies.

### Unsloth and training dependencies

Training Lab creates an isolated local Python environment for local LoRA/QLoRA work. Unsloth, PyTorch, Transformers, PEFT and their transitive dependencies are installed outside the repository on explicit setup; they are not PaRuX-authored code or bundled source.

### 3D runtime dependencies

The current public 3D path uses a local Python environment for depth estimation and mesh generation. Experimental TRELLIS/TRELLIS-for-windows work is not incorporated into PaRuX source or the MSI. If a future 3D backend changes, its exact upstream identity and terms must be reflected in `THIRD_PARTY_NOTICES.md` before release.

### Avatar / lip-sync providers

PaRuX exposes local provider interfaces and optional local runtime integration. LatentSync, Duix or another user-selected avatar backend is not part of the required PaRuX distribution unless a future release explicitly changes that boundary. Provider-specific code/model/data terms remain upstream obligations.

### Agent Platform V2 external interoperability (branch-only, not release-accepted)

- **Microsoft WinApp CLI** — https://github.com/microsoft/winappCli (MIT upstream). PaRuX-authored WinUI/.NET integration invokes an **already installed** operator-selected executable to inspect Windows UIA controls and, after existing Computer Use approval, deliver strictly typed actions. No WinApp CLI source or binary is copied into the repository. The real Windows behaviour remains unverified.
- **OpenCode** — https://github.com/anomalyco/opencode (MIT upstream). PaRuX-authored, desktop-owner-only loopback adapters can read health and bounded session/todo/diff metadata. The native execution backend router deliberately rejects unsupervised OpenCode execution. No OpenCode SDK/source/binary has been imported, and an actual OS-isolated execution backend is not yet implemented.
- **SearXNG** — https://github.com/searxng/searxng (**AGPL-3.0-or-later**, not MIT). PaRuX only calls the standard JSON HTTP API of an already running, explicitly configured loopback service. No SearXNG source or binary is copied, linked, automatically installed, or redistributed by PaRuX. Do not vendor SearXNG without an explicit AGPL compliance review.
- **Browser Use**, **UI-TARS**, **Microsoft UFO**, **Playwright** and other candidates in `docs/AGENT_PLATFORM_V2.md`: architectural research only as of this branch. No corresponding source vendored or executable distributed.

These are external interoperability boundaries and do not imply PaRuX owns or maintains any upstream project. Before an actual third-party source import or redistribution, pin the upstream revision and preserve applicable license, copyright, NOTICE, distribution and model-weight terms.

### Agent Platform V2 source comparison — reference designs, not vendored

- **Jan** — https://github.com/janhq/jan (**Apache-2.0**, reviewed commit `f2cd837fabe99cd6b8bbe985891bab8782e4034b`). Its proactive context high-watermark strategy and deterministic hierarchy of project instructions informed independently authored C# implementations. No Jan source files, Rust/TypeScript implementation text, binaries or assets were copied into PaRuX.
- **OpenHands Software Agent SDK** — https://github.com/OpenHands/software-agent-sdk (**MIT**, reviewed commit `1cfba2117e92730b0fe1c32520a39aad8611bb8c`). Its distinction between transient stream events and committed durable steps informed a small change to PaRuX's existing ordered progress reporter. No SDK source or binary was copied.
- **Odysseus** — AGPL-3.0-or-later, reviewed `a8c147b238db01dbd00b57774ed8b453f1e43971`; comparison-only. No AGPL code ported or linked.
- **OpenCode** — MIT, reviewed `055d95bb7e278c94baf06235a52cac79dd13ba67`; external observer only, not an unattended executor.

See `docs/OPEN_SOURCE_AGENT_REVIEW_2026-10.md` for what was inspected,
what was implemented, and what still needs real validation. Design
inspiration does not create a third-party source redistribution boundary;
copying actual upstream code later would require precise license and
copyright notices.

## 5. Package dependencies

NuGet and Gradle dependencies are consumed through their normal package mechanisms rather than copied into PaRuX source unless explicitly stated otherwise above. The maintained human-readable redistribution summary is `THIRD_PARTY_NOTICES.md`; project files/lockfiles are authoritative for exact dependency versions.

Self-contained .NET and Android build outputs naturally redistribute applicable runtime/package binaries. Release validation must therefore retain required upstream notices and perform a current package license/vulnerability inventory.

## 6. Model weights and datasets

GGUF models, diffusion checkpoints, speech/embedding/reranker weights, training base models and user-selected datasets are separate artifacts with their own licenses/terms. They are not PaRuX-authored merely because PaRuX can download, manage or execute them.

Model Hub and Dataset Hub should preserve source/provenance/license information whenever the upstream service provides it. Technical compatibility is not a license grant.

## 7. Provenance rules for future changes

Before adding third-party source to the repository:

1. Prefer an existing PaRuX implementation or a normal permissive package dependency when practical.
2. If source must be vendored, record the upstream project, license and retained license/NOTICE file in this document and `THIRD_PARTY_NOTICES.md`.
3. Record the upstream revision when refreshing vendored source.
4. Keep external runtimes external where that is the intended architectural boundary; never describe lifecycle-managed third-party software as PaRuX-authored.
5. Do not copy source from reference projects whose license/integration model is incompatible with the intended PaRuX distribution.

That distinction — **PaRuX-authored / vendored upstream / external runtime / reference-only** — is the provenance model used for release review.
