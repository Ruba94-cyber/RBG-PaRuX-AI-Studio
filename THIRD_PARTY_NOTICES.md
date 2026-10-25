# PaRuX Third-Party Notices

PaRuX's own source code is licensed under the Functional Source License 1.1, MIT Future License (FSL-1.1-MIT; see [LICENSE](LICENSE)) - source-available, with each version converting to MIT two years after release. This operational notice separates software that is **redistributed in PaRuX build artifacts** from runtimes, tools and model weights that PaRuX can discover or download/install on explicit user request.

The repository pins concrete dependency versions in its project files. Upstream license files and package metadata remain the authoritative terms for each dependency; this notice is maintained so a GitHub MSI/APK release does not obscure what is actually being redistributed.

## Redistributed in the Windows MSI / self-contained publish

The GitHub MSI includes the self-contained WinUI desktop application, self-contained **PaRuX Server** (`PaRuX.Server` source project), CLI and protected Update Helper. Therefore dependencies of those projects are part of the Windows redistribution boundary.

| Component | License | Used by | Purpose |
|---|---|---|---|
| .NET 8 / ASP.NET Core | MIT and accompanying .NET third-party notices | PaRuX Desktop/Server/CLI/Update Helper | Managed runtime, base libraries and local web host |
| Microsoft.WindowsAppSDK | MIT | PaRuX Desktop | WinUI 3 application runtime |
| H.NotifyIcon.WinUI | MIT | PaRuX Desktop | System tray |
| Microsoft.Data.Sqlite | MIT | Desktop/Server | Durable local stores |
| DocumentFormat.OpenXml | MIT | Desktop/Server | Office document generation/inspection |
| ExcelDataReader | MIT | Desktop/Server | Spreadsheet import |
| QRCoder | MIT | Desktop | Pairing QR generation |
| System.Drawing.Common | MIT | Desktop | Windows image/QR support |
| ModelContextProtocol | Apache-2.0 | Server | Outbound MCP client capabilities |
| ModelContextProtocol.AspNetCore | Apache-2.0 | Server | PaRuX MCP server/ASP.NET integration |
| Microsoft.ML.OnnxRuntime | MIT | Server | Local ONNX embedding inference |
| Microsoft.ML.Tokenizers | MIT | Server | Tokenization for local embedding/retrieval pipeline |
| Parquet.Net | MIT | Server | Dataset Hub Parquet reading/writing |
| SharpCompress | MIT | Server | Safe extraction support for managed runtime archives; retain any notices present in the upstream package/distribution |
| llama.cpp `convert_lora_to_gguf.py` + conversion package | MIT (llama.cpp) | Server | Vendored adapter conversion scripts under `llama-cpp-conversion/`; exact upstream text is in `llama-cpp-conversion/LICENSE-llama.cpp` |

The MSI build copies this notice, the PaRuX FSL-1.1-MIT license and `OPEN_SOURCE_PROVENANCE.md` beside the installed binaries. It does **not** intentionally bundle ComfyUI, llama.cpp runtime binaries, model weights, Unsloth, TRELLIS or other large AI engines listed below.

## Agent Platform V2 interoperability — not redistributed

The experimental V2 branch integrates with **already-installed, operator-managed** external programs using command-line or HTTP protocols. None of the following sources or executables is included in PaRuX release artifacts by this work:

| External service/tool | Upstream | License | Integration boundary |
|---|---|---|---|
| Microsoft WinApp CLI | https://github.com/microsoft/winappCli | MIT | Explicit-path, externally installed Windows UIA CLI |
| OpenCode | https://github.com/anomalyco/opencode | MIT | Optional read-only local health/session metadata observer; supervised execution backend NOT implemented |
| SearXNG | https://github.com/searxng/searxng | AGPL-3.0-or-later | User-operated loopback JSON search HTTP endpoint; no bundled server/source |

The October 2026 Agent Platform V2 comparison additionally consulted Jan
(Apache-2.0), OpenHands SDK (MIT), and Odysseus (AGPL-3.0-or-later)
as **reference implementations**, without copying their source. Their
design ideas were adapted into new PaRuX-authored C# code and tests. This
creates no new vendored-source redistribution items. See
`docs/OPEN_SOURCE_AGENT_REVIEW_2026-10.md` and
`OPEN_SOURCE_PROVENANCE.md`.

MIT obligations apply if MIT source is actually copied/distributed, not merely because PaRuX calls another independently installed executable. SearXNG is **AGPL**, not MIT: PaRuX does not incorporate its source, and any future bundling/modification must receive a separate license-compliance review. All current C# adapters and PowerShell acceptance harnesses are PaRuX-authored code, not transcriptions of these upstream implementations.

## Redistributed in the Android APK

| Component/family | License | Purpose |
|---|---|---|
| AndroidX Core / Activity / Lifecycle / Navigation / Security / DataStore / WorkManager | Apache-2.0 | Android application framework, durable pairing/state and local background supervision |
| Jetpack Compose / Material 3 | Apache-2.0 | Android UI |
| CameraX | Apache-2.0 | Camera capture for pairing QR |
| ZXing Core | Apache-2.0 | Fully on-device QR decoding for pairing |
| Media3 | Apache-2.0 | Local media playback |
| Retrofit | Apache-2.0 | Paired-Server HTTP API client |
| OkHttp | Apache-2.0 | Certificate-pinned TLS/network transport; no HTTP logging interceptor is shipped |
| kotlinx-coroutines | Apache-2.0 | Async/concurrency |
| kotlinx-serialization | Apache-2.0 | JSON contracts |
| Coil | Apache-2.0 | Authenticated image loading |

The Android app does not embed a local LLM or proprietary cloud inference provider. QR decoding is local, and paired application traffic is HTTPS with certificate pinning. Android is a control plane for the user's PaRuX Server, not a cloud-AI client.

## Downloaded/installed on explicit user action — not shipped inside the MSI/APK

PaRuX can either **locate an existing USER_MANAGED installation** or create an isolated **PARUX_MANAGED** copy of supported runtimes. A managed copy is still third-party software under its upstream license; `PARUX_MANAGED` describes lifecycle ownership, not relicensing or authorship.

| Component | Upstream license / terms | Role | PaRuX relationship |
|---|---|---|---|
| llama.cpp / llama-server | MIT | Local LLM serving | Existing installation may be located, or verified builds may be downloaded as a PaRuX-managed runtime. Not embedded in the MSI. |
| uv (Astral) | MIT OR Apache-2.0 | Python environment/dependency manager for every PaRuX-owned Python runtime | Downloaded as a PaRuX-managed tool (`packaging/windows-baseline-runtime.lock.json`, pinned by version and SHA-256) into `runtime-tools/uv`. Not embedded in the MSI. **Added 2026-09-02:** uv was already being downloaded and pinned by the baseline runtime lock but had no entry in this file, so it was absent from the SBOM's licence data; the terms here are uv's own upstream dual licence and should be re-confirmed on each version bump. |
| ComfyUI | GPL-3.0 | Local image/video generation backend | Separate local process reached over HTTP. PaRuX may install/manage a copy on explicit request but does not link its GPL code into PaRuX binaries. ComfyUI remains governed by GPL-3.0. |
| TRELLIS / compatible Windows runtime | Verify the exact selected upstream/fork revision; Microsoft's TRELLIS core is MIT | 3D generation | Separate local runtime, downloaded/cloned on request; not in MSI. |
| Unsloth | Apache-2.0 | LoRA/QLoRA training | Installed into an isolated local Python environment on request. |
| PyTorch / Transformers / PEFT / sentencepiece / protobuf / gguf and conversion/training environment dependencies | Their respective upstream licenses | Adapter conversion/training | Installed into isolated local environments on demand, not committed or embedded in PaRuX MSI. |
| LatentSync (ByteDance) | Apache-2.0 for code; model/data terms must be checked for the exact selected release | Avatar/lip-sync backend | Separate optional local runtime, not in MSI. |
| Duix or other user-selected avatar backend | Provider-specific | Optional avatar backend | Bring-your-own external integration; never required by the public PaRuX distribution. |

For GPL or otherwise differently licensed external engines, PaRuX's lifecycle UI must preserve the upstream identity/license/provenance rather than presenting them as PaRuX-authored components.

## Model weights and datasets

LLM GGUF files, image/video checkpoints, embedding/reranker models, 3D model assets, training base models and user-selected datasets are **not committed to the PaRuX repository and are not shipped inside the MSI/APK** unless a future release explicitly says otherwise.

Every model/dataset can carry terms independent of PaRuX's license. Model Hub/Dataset Hub should expose source/license/provenance where the source provides it. A download being technically compatible with PaRuX does not mean its license permits every downstream use.

## Build/release tooling — not redistributed as PaRuX runtime code

| Tool | Terms | Use |
|---|---|---|
| WiX Toolset v7 | Open Source Maintenance Fee (OSMF) EULA (WiX v7 is not under the older MS-RL used by WiX v3/v4; check the exact terms of the version you install) | Build-time only: authors the Windows MSI. The repository never auto-accepts its EULA; a local builder must explicitly accept before `scripts/build-msi.ps1` runs. WiX tooling is not shipped as part of the installed PaRuX application. |
| Windows SDK `signtool.exe` | Microsoft Windows SDK terms | Optional Authenticode signing of a release MSI; not bundled. |
| `PaRuX.ReleaseTool` | PaRuX FSL-1.1-MIT source | Locally creates the PaRuX-owned detached signed MSI manifest; private release keys are never shipped. |

## Offline/local-AI boundary

PaRuX's **inference and execution design is local-first**, but first-time setup can require Internet access to obtain runtime binaries, model weights, datasets or optional tools the user explicitly selects. After required assets are present, normal local inference/workflows should not require a proprietary cloud AI provider. GitHub/Hugging Face are distribution/discovery sources, not inference backends.

Features whose purpose is explicitly online (for example checking GitHub Releases, searching Hugging Face, downloading an optional model/runtime or connecting to a user-configured remote MCP endpoint) naturally require that network resource. Those paths must fail visibly rather than silently substituting cloud inference.

## Release audit note

Because self-contained .NET and Android builds include transitive dependencies, a public release should still run the package-manager license/vulnerability inventory during local release validation and preserve any upstream NOTICE files that require redistribution. This document is the maintained human-readable summary, not a substitute for the exact upstream license texts.
