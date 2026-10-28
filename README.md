# RBG PaRuX AI Studio

<p align="center">
  <img src="src/PaRuX.App/Assets/prx-orbit-approved.png" alt="PaRuX" width="180">
</p>

<p align="center">
  <strong>Local-first AI for Windows, with an optional secure Android companion.</strong>
</p>

<p align="center">
  Run local models, chat with your files, use coding agents and Computer Use, create media, expose OpenAI/Anthropic-compatible APIs, and keep the inference stack under your control.
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-FSL--1.1--MIT-blue.svg" alt="FSL-1.1-MIT License"></a>
  <a href="#requirements"><img src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D6" alt="Windows 10+"></a>
  <a href="docs/PARUX_ANDROID_README.md"><img src="https://img.shields.io/badge/companion-Android-3DDC84" alt="Android companion"></a>
</p>

---

## What is PaRuX?

**PaRuX** is a source-available AI workspace and runtime built around a simple idea: your local machine should be able to act as the AI workstation, not just as a client for somebody else's cloud.

PaRuX brings model management, chat, projects and knowledge, coding agents, Computer Use, multimodal creation, datasets/training/evaluation, workflows, automations and performance tuning into one local control plane.

Local inference is the default. Cloud inference is optional, explicit and disabled until you configure it.

### Why PaRuX?

- **Local-first by design** — local models are the primary runtime.
- **One place for the whole stack** — models, chats, projects, agents, media, training, APIs and automation.
- **Hardware-aware routing** — PaRuX suggests models and runtimes for your hardware and offers per-capability Automatic, Prefer and Force overrides.
- **Interoperable** — exposes OpenAI-, Anthropic- and Ollama-compatible local API surfaces.
- **Agent-ready** — integrates with coding clients such as Claude Code, Codex and OpenCode.
- **Secure remote control** — the Android companion supervises a Windows PaRuX without becoming a second execution host.
- **Gaming-aware** — Gaming Mode can stop managed AI workloads instead of competing for GPU resources.
- **Source-available** — PaRuX is licensed under the Functional Source License (FSL-1.1-MIT); each version converts to MIT two years after release.

---

## Download

> **Not yet released - no installer published yet.** There are currently no GitHub Releases, so there is nothing to download. The Windows MSI and Android APK described below are the planned distribution channel; until a signed release exists, build from source (see Quick start).

Once released, official Windows and Android builds will be distributed through **GitHub Releases**:

- **Windows** — versioned x64 MSI installer.
- **Android** — companion APK for controlling a running Windows PaRuX.
- **Source** — this repository contains the complete PaRuX source tree.

➡️ [GitHub Releases](https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases)

The Android app is a companion, not a standalone AI runtime. It pairs with PaRuX running on Windows.

---

### Verify your download

- Compare the SHA-256 of the MSI with `PaRuX-<version>-win-x64.sha256.txt` published next to it.
- The MSI is Authenticode-signed with a **self-issued** certificate (not issued by a public certificate authority), so Windows and SmartScreen warn until you decide to trust it. Certificate SHA-1 thumbprint: `F73FDA46DD3A0A7DE82C0E479AE1F1E5091416F8`; SHA-256 of the published `PaRuX-signing-certificate.cer`: `A8F7445D921A1895901DFB8E9BACD23D25F96F425EE63D82D2269B4ECA004321`. Check both through a channel you trust before importing the certificate (importing is optional).
- The in-app updater verifies a signed update envelope against a public key compiled into the app, independently of the Authenticode certificate.

## Quick start

### Windows

1. Download the latest Windows MSI from [Releases](https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases).
2. Install and launch PaRuX.
3. On first run, "Set up automatically" detects the hardware, suggests a model that fits it and shows the licence for review before any download starts.
4. Download a model and start chatting. Nothing is downloaded or switched without your confirmation.
5. Optionally enable integrations, browser access or Android pairing from Settings.

PaRuX keeps its mutable state outside the application installation directory.

### Android companion

Install the APK from the same Release, then pair it from the Windows application.

Pairing uses HTTPS, a short-lived pairing exchange, a unique revocable device token and certificate fingerprint pinning.

See [Android documentation](docs/PARUX_ANDROID_README.md).

---

## Core capabilities

### Chat, Projects and Knowledge

- Streaming local chat.
- Image and document context.
- Project-scoped knowledge and RAG.
- Persisted conversations.
- Conversation branching ("Branch from here" and duplicate).
- Integrated chat media: image, edit and video generation inside the conversation, with retry and recovery.
- Optional media prompt enhancer, reviewed by you and run only locally before any send.
- Chat import from ChatGPT, Claude and Gemini exports.
- Durable artifacts and project context.

### Model Hub and routing

- Hugging Face discovery, organised into categories and separate Popular, New and Updated cohorts.
- "Suggested for this PC" models based on hardware detection; suggestions never download or switch a model without confirmation.
- Periodic automatic checks for suggestions and updates, configurable and can be turned off in Settings.
- Hardware-fit guidance.
- Managed local model library.
- Restart-resumable downloads.
- GGUF, EXL3 and ONNX-oriented runtime paths.
- Capability and performance benchmarking, with local benchmark history and Model Arena comparisons.
- Smart Router with automatic routing profiles and exact manual model locking.
- ComfyUI API-workflow import, and LoRA search and install that requires your consent and verifies hashes.
- Models from other local apps (Jan, KoboldCpp, LM Studio, Ollama) can be linked without copying their files.

PaRuX keeps **model intelligence evidence** separate from **runtime performance evidence**, so a fast model is not automatically treated as a more capable one.

### Local inference runtimes

PaRuX can manage local inference through supported local engines, including llama.cpp-based paths and evidence-gated alternative runtimes.

Runtime suggestions follow the hardware detected on your PC and, like model suggestions, are never installed or switched without confirmation. Other local launchers such as Ollama, LM Studio, KoboldCpp and text-generation-webui may be discovered and linked for interoperability or benchmarking, but PaRuX does not require them as its primary inference runtime.

### Agents and coding

PaRuX includes a persistent task system for:

- planning;
- research;
- coding;
- review;
- testing;
- security work;
- dependency-aware task execution;
- checkpoint/resume;
- isolated coding worktrees;
- multi-repository workflows.

Coding clients can connect to PaRuX through the local AI gateway.

### Computer Use

Computer Use runs on Windows and remains task-scoped and approval-aware.

The Android companion can request, supervise and approve an exact task, but it does not receive a raw Windows shell, filesystem or unrestricted input channel.

### Create

Local creation workflows include:

- image generation and editing (Z-Image, Qwen-Image and Qwen-Image-Edit, SDXL checkpoints, FLUX.1 schnell, Chroma), img2img, **mask inpainting** and a **stack of up to three LoRAs** with a family-compatibility check;
- video (LTX-Video 2B and others), music (ACE-Step), background removal;
- ComfyUI workflow import and LoRA install with consent;
- speech-to-text, text-to-speech and a hands-free **voice conversation mode** in Chat;
- 3D generation;
- artifact capture;
- progress and cancellation.

All of it is also available from Chat in English and Italian ("create an image of…", "crea un video di…"). The **Model Hub media area** lists engines and models for each media type, split into Standard and Uncensored groups, discovered from Hugging Face with a "best for this PC" proposal and automatic routing by lane. After every download (text or media) PaRuX tunes the model for your hardware in the background, and when nothing is faster it says so and suggests concrete next steps. Character cards (SillyTavern/Chub JSON or PNG) import as personas.

Availability depends on the local engines and models installed on the machine, and on your hardware: the media paths were verified on one NVIDIA PC; AMD/Intel, CPU-only and low-VRAM paths are covered by automated tests only.

### Dataset, training and evaluation

PaRuX includes local workflows for dataset curation, training, adapters and evaluation, with explicit promotion rather than silently replacing a working configuration. Training dependencies are installed from a hash-locked closure.

### Skills, MCP, workflows and automations

PaRuX supports permission-aware Skills, local/remote MCP servers, versioned workflows and Core-owned automations.

MCP tools are filtered by an approved-tool policy both when tools are exposed to a model and when a tool call is executed.

---

## Local-first and cloud fallback

PaRuX does **not** require OpenAI, Anthropic, Gemini or another proprietary cloud inference API for normal local operation.

Internet access is still used when you explicitly use features that need public resources, such as:

- model, dataset or runtime discovery;
- GitHub update checks;
- Internet research.

### Optional cloud fallback

Cloud fallback is **off by default**.

If you enable it in Settings and configure a provider, model and API key, PaRuX can use that endpoint only when local inference cannot serve the request.

Supported configurations include OpenAI-compatible HTTPS providers and specific provider presets.

Important boundaries:

- local inference remains preferred;
- fallback is not consulted while local inference is working;
- Gaming Mode does not silently redirect work to the cloud;
- use of the fallback is surfaced to the user;
- stored credentials are protected for the current account;
- disabling the fallback removes the stored credential.

---

## Architecture

```text
PaRuX Windows (WinUI)
        │
        │ loopback + per-process trust
        ▼
PaRuX Core / PaRuX.Server
        ├─ local inference runtimes
        ├─ model management and routing
        ├─ projects / tasks / history / knowledge
        ├─ media / speech / 3D / training engines
        ├─ workflows / automations / MCP
        └─ optional paired HTTPS private-LAN control plane
                         ▲
                         │ device token + certificate pinning
                  PaRuX Android
```

**Windows is the execution workstation. Android is the companion control plane.**

For the deeper design, see [Architecture](docs/ARCHITECTURE.md).

---

## Use PaRuX from other apps

PaRuX exposes a local AI gateway at:

```text
http://127.0.0.1:11435
```

The gateway supports local compatibility surfaces for:

- Anthropic Messages;
- OpenAI Responses;
- OpenAI Chat Completions.

PaRuX also exposes Ollama-compatible endpoints through the Core API.

Example model aliases include:

```text
parux-auto
parux-code
parux-reasoning
parux-fast
parux-vision
```

Settings → **Integrations** can configure supported coding clients, create a scoped per-client key and keep backups of configuration it changes.

More details: [Universal Gateway](docs/UNIVERSAL_GATEWAY.md).

---

## Apps and files on your PC

PaRuX can work with files already present on the machine, but nothing is connected automatically.

Supported local sources include, among others:

| Source | Local content |
| --- | --- |
| Obsidian / Logseq / note folders | Markdown and text |
| Thunderbird | Local mbox mail |
| Exported mail | `.eml` |
| Calendars | `.ics` |
| Edge / Chrome | Bookmarks |
| Office documents | `.docx`, `.xlsx`, `.pptx`, PDF, RTF |
| OneDrive | Locally synced supported documents |

The integration boundary is intentionally conservative:

- sources are opt-in;
- readers are read-only;
- each source is constrained to the selected path;
- retrieved context keeps source provenance.

PaRuX can also create Word and Excel files in a separately selected output directory. Generated files never silently overwrite an existing file.

---

## Browser client and remote access

`parux serve` can run PaRuX Core without the desktop UI and expose the built-in browser client locally.

Browser access beyond the local machine is disabled by default.

For remote use, PaRuX is designed to sit behind an HTTPS reverse proxy such as Nginx Proxy Manager, Caddy or Traefik rather than opening router/firewall access itself.

The browser boundary uses its own authentication/session protections and does not inherit privileged desktop or paired-device authority.

The browser currently supports conversations, streamed chat with STOP, model selection, runtime status and browser settings. Its conversation drawer supports narrow screens and keyboard navigation. Knowledge/project authoring, coding-job supervision, artifact browsing, automations and media workspaces remain available through the desktop and, where admitted, the paired Android client; they are not browser features.

---

## Security model

Security-sensitive capabilities are intentionally separated:

- API keys are scoped to model/provider surfaces.
- Paired Android routes are deny-by-default.
- Computer Use remains a Windows-side privileged capability.
- Browser sessions do not receive unrestricted desktop authority.
- Private release keys and signing credentials must never be committed.
- Backup restore validates archives before replacing durable state.
- Imported ZIP content is bounded during expansion.

Security issues should be reported through the private process described in [SECURITY.md](SECURITY.md).

---

## Requirements

### Windows

Reference platform:

- Windows 10 or later, x64;
- .NET 8 SDK for source builds;
- Windows App SDK prerequisites used by the solution.

Local-model requirements depend on the selected model and runtime. A dedicated GPU is strongly recommended for larger or performance-sensitive workloads.

### Android development

For building the companion:

- JDK 17+;
- Android SDK 35.

---

## Build from source

### Windows / Core

The authoritative local validation gate is:

```powershell
powershell -ExecutionPolicy Bypass -File tools\release-gate.ps1 -Restore
```

The gate builds and tests the Release solution, publishes shipped Windows executables and validates the project security/runtime boundaries.

MSI creation is intentionally local:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-msi.ps1
```

See [Releasing](docs/RELEASING.md) and [Windows MSI release](docs/WINDOWS_MSI_RELEASE.md).

### Windows UI automation

```powershell
powershell -ExecutionPolicy Bypass -File tools\test-ui.ps1 -Suite Smoke
powershell -ExecutionPolicy Bypass -File tools\test-ui.ps1 -Suite Regression
```

The FlaUI/UIA3 suite runs against the real desktop process using an isolated PaRuX data root.

### Android

```powershell
cd src\PaRuX.Android
.\gradlew.bat --no-daemon clean test lint assembleDebug assembleRelease
```

A physical Android device is useful for exploratory camera/OEM/radio/background-management QA, but the source validation path includes JVM tests, lint and APK assembly.

---

## Repository layout

```text
src/
  PaRuX.App/            Windows desktop application
  PaRuX.Server/         Core / server
  PaRuX.Cli/            CLI
  PaRuX.Contracts/      Shared contracts
  PaRuX.Intelligence/   Intelligence / routing logic
  PaRuX.Android/        Android companion

tests/                  Automated tests
tools/                  Validation and release tooling
scripts/                Packaging / build scripts
docs/                   Architecture and feature documentation
benchmarks/             Benchmark assets and fixtures
```

---

## Project status

PaRuX is under **active development**.

**Release acceptance: PARTIAL — active software gaps; PHYSICAL_PC_ACCEPTANCE_REQUIRED.** A successful software build alone does not establish stable-release acceptance.

**Version 2.0.0** adds local media creation from Create and Chat, the Model Hub media area, automatic per-model optimization and faster text/media switching (see [CHANGELOG.md](CHANGELOG.md)). Known limitations: real speech through a microphone is unproven on the test PC; character LoRA training, Wan 2.2 14B, HunyuanVideo, CogVideoX and Linux/macOS support are not implemented; hardware other than the NVIDIA test PC is covered by automated tests only; physical-PC acceptance remains open.

Windows is the reference product and acceptance platform. The source tree contains working implementations alongside ongoing benchmark, packaging, behavioral and physical-PC acceptance work.

The canonical project roadmap is:

➡️ [PARUX_EXECUTION_PLAN.md](PARUX_EXECUTION_PLAN.md)

For project truth, the repository follows this order:

```text
current code
  > current tests
  > real runtime evidence
  > PARUX_EXECUTION_PLAN.md
  > historical documentation
```

Older closeout documents are retained as historical evidence and should not override the current code, tests or active execution plan.

---

## Documentation

Useful entry points:

- [Architecture](docs/ARCHITECTURE.md)
- [Local AI setup](docs/AI_LOCAL_SETUP.md)
- [Android companion](docs/PARUX_ANDROID_README.md)
- [Android API](docs/PARUX_ANDROID_API.md)
- [Universal local AI gateway](docs/UNIVERSAL_GATEWAY.md)
- [Performance benchmarking](docs/PERFORMANCE_BENCHMARKING.md)
- [Workload orchestrator](docs/WORKLOAD_ORCHESTRATOR.md)
- [Skills](docs/skills.md)
- [Releasing](docs/RELEASING.md)
- [Windows MSI release](docs/WINDOWS_MSI_RELEASE.md)
- [Contributing](CONTRIBUTING.md)
- [Security](SECURITY.md)

---

## Contributing

Contributions are welcome.

Before opening a pull request, read [CONTRIBUTING.md](CONTRIBUTING.md) and run the relevant local validation for the area you changed.

Please keep security boundaries, local-first behavior and user-controlled privilege escalation explicit in new features.

---

## License

PaRuX source is licensed under the **Functional Source License, Version 1.1, MIT Future License (FSL-1.1-MIT)**: it is source-available, and each version converts to the MIT license two years after it is released. The Functional Source License is not an OSI-approved open-source license.

Third-party runtimes, models and datasets keep their own licenses and terms.

- [LICENSE](LICENSE)
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
- [OPEN_SOURCE_PROVENANCE.md](OPEN_SOURCE_PROVENANCE.md)

---

## Support PaRuX

PaRuX is free to use under the FSL-1.1-MIT license and has no telemetry-based business model or mandatory paid tier.

Useful ways to support the project include:

- filing clear bug reports;
- opening focused pull requests;
- improving documentation;
- sharing reproducible benchmark results;
- starring the repository.

If PaRuX is useful to you, you can also support its development with a donation:

[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-00457C?logo=paypal&logoColor=white)](https://www.paypal.com/donate?business=paolo.ruba.1913%40gmail.com&no_recurring=0&item_name=Support+PaRuX&currency_code=EUR)

Donations are voluntary and unlock nothing: every feature stays free.
