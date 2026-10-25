# PaRuX vs Locally Uncensored

> Generated from the structured output of nine read-only domain audits (see `docs/audits/parts/*.md` for the narrative evidence).
> Evidence levels: **static** = code read, **unit_tests** = named automated tests exist (not necessarily executed by the auditor), **e2e_real** = a recorded real run, **none** = no evidence.
> The audit was static: nothing was built, run or fetched by the auditors. Statements about Locally Uncensored (LU) describe observable behaviour in the auditors' own words; no LU code is reproduced (LU is AGPL-3.0, PaRuX is MIT).

Compared versions: PaRuX branch `feature/parux-competitive-integration-20261019`; Locally Uncensored v3.0.6 (AGPL-3.0).

## Architectural decision

Three options were compared on features already present, code quality, performance, maintainability, dependencies, complexity, UX, security, licences, development time, regression risk and future evolution.

| | A. Continue PaRuX, integrate selectively | B. Fork Locally Uncensored | C. Hybrid with interchangeable backends |
|---|---|---|---|
| Licence | Compatible: only original code and MIT/Apache components | **Blocked**: LU is AGPL-3.0-only; a fork forces the whole product under AGPL-3.0, incompatible with PaRuX's MIT licence | Compatible if adapters stay separate processes/interfaces |
| Stack | .NET 8 + WinUI 3 + ASP.NET Core Core (kept) | Tauri/Rust + React/TypeScript (different runtime, UI toolkit and test stack) | Kept |
| Existing PaRuX value | Preserved: AppContainer coding sandbox, transactional apply with crash recovery, durable checkpoints, explicit local-first grants, Gaming hard-off, process identity checks, pinned downloads with provenance | Lost or re-implemented (about 690 commits of hardening) | Preserved |
| LU strengths gained | Re-implemented from behavioural specs where a gap is proven | Immediate but encumbered | Via adapters |
| Regression risk | Low (incremental, each slice tested) | Very high | Low-medium |
| Effort | Medium, incremental | Very high | Medium |

**Decision: A + C.** Keep PaRuX. Close the proven gaps with original, data-driven implementations behind the adapter seams PaRuX already has (managed llama.cpp, ExLlamaV3, ComfyUI, WinML, Ollama-compatible API). No fork and no rewrite, so no approval is needed under the programme rules; the licence rules require a clean-room process: auditors write behavioural specifications, implementers never open the LU source.

Where LU is genuinely ahead (verified by the audits) the plan adopts the behaviour, not the code: split-GGUF sets, projector pairing, read-only import of other stores, stall-based transfer timeouts, free-space guard, gated-repository tokens, post-health output probe, launch fallback ladder, per-family companion registry with live preflight, cancel propagation to ComfyUI, a shorter first-run path. Where PaRuX is ahead it stays ahead: lifecycle with rollback and performance-gated promotion, identity-checked processes, coding sandbox and apply recovery, provenance-pinned downloads.

## Comparative matrix

The last column shows the status after the implementation work of this programme (`not started` until updated).

### Model discovery, catalog and capabilities

PaRuX's model discovery and download layer is strong on state and safety: a bounded candidate registry, hardened Hugging Face fetch, streamed SHA-256 with provenance, licence declaration and consent, and a geometry-based fit estimate. The catalogue pipeline has three gaps against LU that matter most. Split GGUF sets are not grouped, so one part can be presented as a model (P0). Vision projectors are not paired on download and rely on two hard-coded files (P1). There is no read-only import of existing Ollama or LM Studio stores (P1). Two PaRuX concepts are duplicated (fit estimators, capability classifiers, plus two quant parsers), and on HEAD auto-assign can silently overwrite a user's role choice. The fix for that and the BIC-16 promotion gate exist only on the local main branch, and were read with git show and git diff only. Evidence is static reading plus unit-test names; no test was executed, no build was run, and nothing was checked against the network. Part file: see partFile (96 lines, not committed).

| Pri | Feature | LU | PaRuX now | Gap | Action | After this work |
|---|---|---|---|---|---|---|
| P0 | Sharded (split) GGUF set handling | yes | missing/static | A single part can be downloaded and then shown as a usable model, a silent broken success. Large split models cannot be installed correctly. | implement | not started |
| P1 | Vision projector (mmproj) pairing for downloads and imports | yes | partial/static | A downloaded vision model answers as text-only without warning. The pairing table is code rather than data and covers only two named files. | implement | not started |
| P1 | Role auto-assignment must not overwrite user-owned choices | partial | partial/unit_tests | On HEAD, auto-assign can silently replace an explicit user role and hides write failures. The fix exists only on local main. | improve | not started |
| P1 | Candidate promotion and rollback gate with crash reconciliation (BIC-… | no | partial/unit_tests | HEAD has evidence and evaluation but no promotion transaction with generation, rollback and crash reconciliation. That work is on main only and was not verified here. | keep | not started |
| P1 | Import of models already on disk from other applications' stores | yes | partial/static | A copy doubles disk use for large models. Existing Ollama and LM Studio stores are never discovered. The control sits in Settings, although CLAUDE.md assigns models to Model Hub. | implement | not started |
| P1 | Hardware fit verdict for a model file | yes | duplicated/unit_tests | Two estimators with different constants and vocabulary can give one model two verdicts on two screens. | improve | not started |
| P1 | Capability and category taxonomy (coding, vision, embedding, reasonin… | partial | duplicated/unit_tests | The same name gets different answers (deepseek-v3 is reasoning in one classifier and general in another). Substring matches hit unrelated words. Search, roles and routing do not share one taxonomy. | replace | not started |
| P2 | Hugging Face token for gated repositories | partial | missing/static | Gated Hugging Face models cannot be searched or downloaded. Users see a generic failure. | implement | not started |
| P2 | Hugging Face keyword search with sort and grouping | yes | implemented_working/unit_tests | No sort control and no grouping by repo or quant. Rows show raw files, including parts. | improve | not started |
| P2 | Discovery feed and Candidate registry | no | implemented_working/unit_tests | The feed is fixed and unfiltered, so each refresh sees only eight repos. No user-steerable query. | improve | not started |
| P2 | Quantisation recognition from file names | yes | duplicated/static | Two parsers disagree. Coarse labels hide the difference between Q4_K_M and Q4_0 in the library. | replace | not started |
| P2 | Uncensored and abliterated content tagging | yes | partial/unit_tests | dare is a merge method, not an uncensoring marker. Repo id and tags are ignored for the content profile. | improve | not started |
| P2 | Curated seed list and verification | yes | partial/static | Seed repo ids are not checked automatically, so a wrong id is a dead end for the user. Ids cannot be checked offline in this audit. | improve | not started |
| P2 | First-model onboarding recommendation | yes | implemented_working/unit_tests | Recommendations come from seeds and are not yet linked to verified candidates. | keep | not started |
| P2 | Local model removal and partial-download cleanup | yes | implemented_working/static | No reference check (role overrides, pool entries, active model) and no undo. The control sits outside Model Hub, which CLAUDE.md assigns models to. | improve | not started |
| P2 | Multi-file media bundles with VRAM tiers | yes | partial/static | Not compared in depth. Needs the media-domain audit. | improve | not started |
| P3 | Hugging Face fetch hardening | partial | implemented_working/unit_tests | Hostile-response cases (oversize body, truncated JSON, redirect chains) are not individually confirmed by tests. | keep | not started |
| P3 | Download integrity, provenance and file identity | yes | implemented_working/unit_tests | None significant. Confirm that the same-name, different-size case has a test. | keep | not started |
| P3 | Resumable downloads and permanent versus transient failure classes | yes | implemented_working/unit_tests | Failure classification was not traced in this pass. | keep | not started |
| P3 | Local GGUF header reader with cache | yes | implemented_working/unit_tests | None significant. | keep | not started |
| P3 | Licence review and consent before download | no | implemented_working/static | Consent flow not exercised in this pass. | keep | not started |
| P3 | CivitAI model source | yes | missing/none | No CivitAI source. Whether this is intentional is a product decision. | drop | not started |

### Download manager

Clean-room audit of the downloads domain completed. LU v3.0.6 was read-only: no build, no tests, no network, no file of the installed app opened. PaRuX was read on the current worktree; tests were inspected by name and by transport, not executed. Part file: docs/audits/parts/downloads.md (not committed). Main PaRuX gaps: P0 the total 30-minute timeout on the transfer client (ModelDownloadService.cs:70) aborts long downloads as cancelled or failed; P1 no physical free-space check before and during the download, no token support for gated repositories, no Gaming hard-off gate in the worktree (the feature exists only on the main branch). PaRuX strengths: commit pinning, provenance manifest, licence review, partial identity for resume, cancellation that preserves bytes. LU strengths: stall timeout instead of a total limit, space reservations, pause, shared bandwidth limit, tokens limited to the correct hosts, orphan partial scan. Neither has evidence of a real transfer: the conclusions are static or from unit tests not executed.

| Pri | Feature | LU | PaRuX now | Gap | Action | After this work |
|---|---|---|---|---|---|---|
| P0 | Transfer deadline and stall policy | yes | partial/static | Any single attempt longer than 30 min is aborted and reported as cancelled or failed; there is no stall timer and no test covers the cap. Large models on ordinary links cannot finish in one attempt. | improve | not started |
| P1 | Free-space guard before and during downloads | yes | partial/unit_tests | No physical free-space check before or during a transfer. A multi-gigabyte download can fill the system drive, and the only signal is a write error. | implement | not started |
| P1 | Gated or private repositories and tokens | yes | missing/static | Repositories that require a token or licence acceptance cannot be downloaded, and the user gets no guidance about why. | implement | not started |
| P1 | Gaming hard-off gate for model downloads | unknown | missing/static | The product rule requires downloads to yield to gaming, but the worktree has no download gate, so a transfer can keep running during play. | implement | not started |
| P2 | Completeness check for non-candidate downloads | yes | partial/unit_tests | Manual downloads can complete with no size or digest guard once the HEAD probe fails. | improve | not started |
| P2 | Quota admission when the size is unknown | partial | partial/unit_tests | A manual start with no size from HEAD or the caller is admitted with zero extra bytes, so the managed budget can be exceeded while bytes arrive. | improve | not started |
| P2 | Replacement, collisions and publish | yes | partial/unit_tests | A completed managed model can be silently replaced by another source with the same file name, the publish step has a moment with no file, and resumable bytes of another source are discarded at start. | improve | not started |
| P2 | Existing final file: short-circuit and repair | yes | partial/unit_tests | Complete files are downloaded again on request; there is no cheap verified answer based on the stored digest. | improve | not started |
| P2 | Redirect and host checks during transfer | yes | partial/static | Redirects from the hub are followed without a host allow-list. No credential is sent today, so the impact is defence in depth only. | improve | not started |
| P2 | Failure classes and actionable messages | yes | partial/static | A user cannot tell a dead address from a temporary failure, so Retry can loop with no exit. | improve | not started |
| P2 | Automatic retry with backoff | no | missing/static | Transient network errors need manual action; neither product retries transfers automatically. | implement | not started |
| P2 | Pause as an explicit state | yes | partial/unit_tests | A user cannot park a transfer without ending it; resume is offered only after cancel or failure. | implement | not started |
| P2 | Speed and ETA | partial | partial/static | Users see no transfer speed and no time remaining. | implement | not started |
| P2 | Shared bandwidth limit | yes | missing/static | Transfers cannot be capped next to other work or games. | implement | not started |
| P3 | Orphan partials after restart | yes | partial/unit_tests | Partials from sources that were never journaled, or copied in by hand, are not discovered. | improve | not started |
| P3 | Integrity: streamed SHA-256 and resume re-hash | yes | implemented_working/unit_tests | No material gap. The expected digest comes from a live HEAD probe, unlike LU's stored catalog digests; the probe failure case is covered under the completeness row. | keep | not started |
| P3 | Revision pinning, provenance manifest, update detection and backup | partial | implemented_working/unit_tests | No material gap. | keep | not started |
| P3 | Licence-consent gating | no | implemented_working/unit_tests | No material gap; the permissive list is short and should be reviewed by product. | keep | not started |
| P3 | Resume identity and partial reuse | partial | implemented_working/unit_tests | No material gap. | keep | not started |
| P3 | Cancel, clear, remove and clean delete | yes | implemented_working/unit_tests | Test 656 is named for a paused download, but no paused state exists; the name should be corrected when the pause state lands. | keep | not started |
| P3 | Concurrency bounds | no | implemented_working/unit_tests | The cap is fixed in code and is not user-configurable. | keep | not started |
| P3 | Shared cache and deduplication | no | missing/static | Duplicate multi-gigabyte files across engines consume both quota and disk. | implement | not started |

### Runtime and engine management

PaRuX is ahead on the managed lifecycle: digest-gated installs with side-by-side tags, rollback, performance-gated promotion, identity-checked processes that never adopt foreign listeners, a managed ExLlamaV3 pack, and a VRAM-aware hot pool. LU is ahead on failure handling: a post-health output probe, CPU retry rungs, illegal-instruction diagnosis, candidate ports, pinned and verified installers, and a PyTorch channel chosen from GPU capability. LU also installs and controls Ollama, LM Studio, ComfyUI and Python; PaRuX does not manage competitor engines, by design. No P0 blocker was found. The P1 gaps are: no output sanity probe; launch tier chosen only from nvidia-smi; no CUDA-to-Vulkan fallback; fixed port 1234 (the LM Studio default); a hard-coded cu128 PyTorch index; and archive digests that come from the same GitHub channel as the asset. Nothing was built or run; LU was read only; no network was used. The part file is 110 lines and is not committed.

| Pri | Feature | LU | PaRuX now | Gap | Action | After this work |
|---|---|---|---|---|---|---|
| P1 | Post-health output sanity probe | yes | missing/none | A live but garbage-producing runtime is reported healthy and serves chats. | implement | not started |
| P1 | Backend selection (CUDA, Vulkan, CPU) | partial | partial/unit_tests | The tier comes only from nvidia-smi, so AMD or Intel cards get the 4B model regardless of DXGI-reported VRAM. Only one fallback direction exists. | improve | not started |
| P1 | Launch failure fallback ladder | yes | partial/unit_tests | No CUDA-to-Vulkan retry and no flash-attention rung; the failure message is honest but there is no recovery. | improve | not started |
| P1 | Port management | yes | partial/unit_tests | No candidate ports and no user-selected port. A foreign process on 1234 blocks the launch until it stops. | improve | not started |
| P1 | PyTorch and CUDA wheel channel selection | yes | partial/static | No capability check and no AMD or CPU channel; older NVIDIA cards or AMD machines get a wheel that may not run. | improve | not started |
| P1 | Download and archive trust | yes | implemented_working/unit_tests | The digest comes from the same GitHub release metadata that supplies the URL, so it proves transfer integrity, not publisher authenticity. No compiled-in pins. Extracted executables are not Authentic… | improve | not started |
| P1 | VRAM probing and offload planning | yes | implemented_working/unit_tests | Launch tier uses nvidia-smi while admission uses DXGI, so the two can disagree for non-NVIDIA cards. | improve | not started |
| P2 | Illegal-instruction and CPU-feature diagnosis | yes | missing/none | On older CPUs a crash shows an opaque exit with no cause. | implement | not started |
| P2 | Start idempotence, serialization and stop gating | yes | partial/unit_tests | Reuse keys on profile label or model path, not the effective argument list; whether context or KV changes always force a restart was not confirmed. | improve | not started |
| P2 | Install job lifecycle (progress, cancel, gate) | partial | implemented_working/unit_tests | Jobs live in memory only, so progress is lost on server restart; a failed download restarts from zero (no byte-range resume seen). | improve | not started |
| P2 | Runtime update flow (Update Manager) | partial | partial/unit_tests | No unified runtime update list across llama.cpp, ComfyUI, ExLlamaV3 and uv; GitHub API errors surface as generic failures. | improve | not started |
| P2 | Performance-gated promotion of new builds | no | implemented_working/unit_tests | Runs only when the measured backend equals the installed backend; otherwise deferred. One representative model; narrow coverage; noise tolerance not specified. | keep | not started |
| P2 | Race orchestration (build, backend, engine, image) | no | implemented_working/unit_tests | Five race-related classes with overlapping contracts and no single documented entry point. | improve | not started |
| P2 | Component ownership and setup flow | partial | implemented_working/unit_tests | The on-screen Detect, Locate, Install, Skip path was not exercised in this audit. | keep | not started |
| P2 | ExLlamaV3 and other engines (vLLM, SGLang, TabbyAPI, KoboldCpp) | no | partial/unit_tests | No end-to-end run found for ExLlamaV3; the pack uses the fixed CUDA torch index (see the PyTorch row); vLLM, SGLang, TabbyAPI and KoboldCpp are not managed. | keep | not started |
| P2 | Python environment provisioning and verify or heal | yes | partial/static | No verify-and-heal pass for managed Python environments found; no disk-pressure check seen. | improve | not started |
| P2 | Engine provisioning and version pinning | partial | implemented_working/unit_tests | No live download evidence in this audit; no curated tag matrix or default-tag test policy found. | keep | not started |
| P3 | Rollback, reset and side-by-side activation | partial | implemented_working/unit_tests | No retention or pruning policy seen for superseded tags; each build is a full copy. | keep | not started |
| P3 | Process identity and ownership | yes | implemented_working/unit_tests | None significant. Do not import the port-kill pattern. | keep | not started |
| P3 | Hot model pool and multi-model residency | no | implemented_working/unit_tests | CUDA-only: hasCuda true and the text-cuda tag are hard-coded; no pool on the Vulkan path. | keep | not started |
| P3 | Lifecycle of competitor engines (Ollama, LM Studio) | yes | missing/none | Users who want these products must install them separately; no guided Locate flow beyond the probe. | drop | not started |
| P3 | ComfyUI lifecycle (install, update, repair) | yes | implemented_working/unit_tests | No custom-node management; no in-place repair or update of an installed portable build. | keep | not started |

### Image generation and dependency resolution

Static-only clean-room audit of image generation and ComfyUI (nothing built, run or networked). LU declares each family's companion set as data (loader kind, separate VAE and encoder flags, CLIP type, folder, file, repository, size), detects the family from the safetensors header, checks the live ComfyUI enums before queueing, and offers a consented fetch of only the missing files. PaRuX has no companion model: each default path is a hand-written ComfyUI template that names its VAE and encoder, nothing verifies those files, and failures surface after queueing. PaRuX strengths are a pinned, offline, independent Diffusers Z-Image engine, consented and revision-pinned downloads, an explicit refusal for Qwen-Image 2.1 rather than silent failure, and a race design that never fakes a second candidate. Top gaps: the companion manifest and consented fetch (P0); then header-based family gating, preflight, typed failures, default-path verification, Qwen-Image 2.1 support and warm workers (P1). Evidence is static, unit-test names or a referenced run whose artifacts are not retained in the worktree; the cited live commits (158719b, 799676c, 9a93076) are not in HEAD.

| Pri | Feature | LU | PaRuX now | Gap | Action | After this work |
|---|---|---|---|---|---|---|
| P0 | Companion registry and resolver (roles, folders, loader kind, CLIP ty… | yes | missing/unit_tests | No place declares a companion, so no feature can reuse the data. A missing VAE or encoder is seen only when ComfyUI rejects the graph. | implement | not started |
| P0 | Consent-gated fetch of only the missing companions | yes | partial/static | Users must find and place encoders and VAEs by hand. The error text asks only for files to be added to the matching folder. | implement | not started |
| P1 | Architecture detection from the safetensors header | yes | missing/none | A wrong-family file can become the image, edit or video model; the failure appears later inside ComfyUI. | implement | not started |
| P1 | Live preflight against ComfyUI before queueing | yes | missing/none | A missing folder entry or unsupported enum value is discovered only after queueing; the race path shows raw text. | implement | not started |
| P1 | Failure reporting and remediation | yes | partial/unit_tests | No folder, repository or size in the message; no offer to fetch; race errors are not user language; no retry path. | improve | not started |
| P1 | Family-specific companion selection and quantisation pairing | yes | missing/none | Adding a second VAE or encoder family breaks the single-file assumption, and quant-tier pairing does not exist. | implement | not started |
| P1 | Checkpoint override scope and status honesty | partial | partial/unit_tests | The reported and the loaded checkpoint can differ, and a wrong-family override fails late inside ComfyUI. | improve | not started |
| P1 | Warm and resident media workers | yes | implemented_unwired/unit_tests | Every Diffusers image pays a process start; race timings are cold by design; the resident code is not used by normal jobs. | implement | not started |
| P1 | Qwen-Image 2.1 generate, edit and prompt enhancer | yes | missing/none | A major open image and edit family is absent from the product. | implement | not started |
| P1 | Default-path alignment: Z-Image CLIP type, FLUX.2 encoder and variant… | yes | partial/e2e_real | Two default paths carry unresolved type and provenance questions. | improve | not started |
| P2 | Engine Race and quality judging | unknown | partial/unit_tests | CLIP scoring cannot judge text, identity, edit fidelity or motion (plan line 764). | improve | not started |
| P2 | Independent Diffusers Z-Image engine | no | implemented_working/unit_tests | No edit or image-to-image in this engine; per-job process start (row 8). | keep | not started |
| P2 | Diffusion LoRA add-ons | yes | missing/none | No style or character adapters for images. | implement | not started |
| P2 | Inpainting and outpainting | partial | missing/none | A common local-edit workflow is absent. | implement | not started |
| P2 | SDXL and SD1.5 single-file checkpoints | yes | missing/none | The largest family of community checkpoints cannot be used. | implement | not started |
| P2 | Quantised and GGUF diffusion models | yes | missing/static | Community quantised models cannot load, and there is no pack gate. | implement | not started |
| P2 | VRAM estimation and memory policy | yes | partial/unit_tests | One formula for every family; no measured per-family tiers; companion files are not counted. | improve | not started |
| P2 | Prompt refinement and enhancer models | yes | implemented_working/static | Language and family are not matched, and the chat model must be loaded. | improve | not started |
| P2 | Custom-node packs and upscaler assets | yes | partial/static | Packs and upscalers are installed by hand. | implement | not started |
| P3 | Create edit modes (instruction edit and image variation) | yes | implemented_working/static | Only the provenance of the evidence; no code gap found. | keep | not started |
| P3 | FLUX.1 pipeline (schnell and dev) | yes | missing/none | Older family; FLUX.2 Klein covers PaRuX's edit path. | drop | not started |
| P3 | ComfyUI runtime versions, minimum-version gates and shared model fold… | yes | implemented_working/unit_tests | No per-model minimum version; user model folders are not mapped. | keep | not started |

### Video, audio and voice

Clean-room audit of the video-audio domain: 22 rows ranked P0 to P3, full table and ten prioritised proposals in the part file. The P0 gap is cancellation: PaRuX's main ComfyUI path cancels only its own HTTP waits and never tells ComfyUI to drop a queued prompt or interrupt a running one (only the Ref2VA executor sends /interrupt), while LU does a per-prompt queue delete then interrupt. Continuous voice exists only as an Android Live Voice loop with untested endpointing and barge-in and no device run; the Windows App has no microphone path. Video is one fixed Wan 2.2 TI2V-5B family with real multi-clip continuity but no user start image, no component download and no family registry, where LU covers about ten families with unavailable reasons. Speech has an unpinned, CUDA-only install, an unused prewarm, resident workers with no idle release, and a default-split Kokoro call that delays first audio. Local main already fixes the sentence split and the pinned spaCy English wheel, and holds the barge-in cancellation test and BIC-13 measurement scripts that this branch lacks (117 commits not in HEAD). PaRuX strengths are real and tested: coordinator leases, bounded job retention, managed ComfyUI runtime with rollback, strict streaming validation, Wan continuity, Gaming hard-off and an engine race. Evidence is static reads plus named test classes; plan and LU docs are cited as claims. No build, test, LU execution or network access was used. The part file is 101 lines and is not committed.

| Pri | Feature | LU | PaRuX now | Gap | Action | After this work |
|---|---|---|---|---|---|---|
| P0 | Cancel propagation to the running ComfyUI prompt | yes | missing/static | GPU work keeps running after PaRuX reports cancellation; a following job can start while ComfyUI is still busy, which breaks the admission model. | implement | not started |
| P1 | Desktop (Windows) voice conversation loop | yes | missing/static | No desktop microphone loop, endpointing, barge-in or spoken chat. | implement | not started |
| P1 | Android Live Voice conversation loop | yes | partial/static | Endpointing and barge-in have no automated tests and no physical-device run; Italian voice is chosen only from the device locale. | improve | not started |
| P1 | Barge-in, first-word protection and endpointing | yes | partial/static | Each barge-in restarts the resident speech worker (cost unmeasured); no pre-roll on the capture after a barge-in; no neural voice-activity or turn model; the detector is untested. | improve | not started |
| P1 | Spoken-reply TTS: engine, voices, chunking and read-aloud | yes | partial/e2e_real | HEAD has no sentence-level split, so first audio waits for the whole paragraph; no chat read-aloud on any PaRuX surface; no thinking filter before synthesis; Create does not play audio; the spaCy Eng… | improve | not started |
| P1 | Speech engine lifecycle, warm-up, idle release and hardware path | yes | partial/static | Installs are not reproducible; no CPU path; resident workers hold memory until Gaming Mode or shutdown; prewarm is unused; model files have no integrity check. | improve | not started |
| P1 | Text-to-video, default path (Wan 2.2 TI2V-5B) | yes | implemented_working/e2e_real | Works for one model and one configuration; near-zero VRAM headroom at default settings; quality judged by humans only; no frame-count tests for other families. | improve | not started |
| P1 | Video model family coverage and workflow strategy detection | yes | partial/unit_tests | No family registry, no per-family unavailable reason, no audio-with-video family, and no second family in the default build. | implement | not started |
| P1 | Start-image and image-to-video input | yes | missing/unit_tests | Users cannot give a start image for video, and the compatibility API refuses the same input. | implement | not started |
| P1 | Component dependencies and automatic component download | yes | missing/static | No install action for video components, no pinned sources, and no actionable missing-component error. | implement | not started |
| P2 | STT engine, language coverage and dictation | yes | partial/e2e_real | No chat dictation; CUDA device zero is hard-coded; the documented Whisper fallback has no code; no transcription test file identified; no language policy in the API. | improve | not started |
| P2 | Speech-driven talking head and audio-driven video | yes | partial/unit_tests | No generation evidence; not in the Create workspace; the engine's licence is not recorded in repo docs. | improve | not started |
| P2 | Music and sound generation | yes | missing/none | Entire feature missing. | implement | not started |
| P2 | Media job queue, state and bounded retention | yes | implemented_working/unit_tests | Job state is lost on restart; the ComfyUI side is not cancelled (see the P0 cancel row). | keep | not started |
| P2 | Media engine race and quality evidence | no | implemented_working/unit_tests | Image candidates only; no video or audio race; the quality judge is weak for text, identity and video. | keep | not started |
| P2 | Create workspace, progress and result gallery | yes | partial/unit_tests | No start image, no music entry, no video gallery view, a fixed ComfyUI port, and no live preview. | improve | not started |
| P3 | Resume and crash recovery of media jobs | no | missing/static | An interrupted render cannot resume or be reattached after a restart. | implement | not started |
| P3 | Admission: VRAM and RAM reservation, leases, text-model handoff | partial | implemented_working/e2e_real | Reservation numbers are static conservative values, not measured; speech workers are not visibly in the ledger; no pre-download fit tier for video models. | keep | not started |
| P3 | ComfyUI managed runtime: install, update, rollback | partial | implemented_working/e2e_real | Host and port fixed at 127.0.0.1:8188 (ComfyWorkflowRunner.cs:475, 693); live evidence only on NVIDIA. | keep | not started |
| P3 | TTS fine-tuning in LifecycleLab | no | partial/unit_tests | No held-out evaluation or reload proof; training dependencies are not locked. | improve | not started |
| P3 | Ref2VA reference-to-video accelerator | no | implemented_unwired/unit_tests | Unreachable on the 16 GB test GPU; no user path. | drop | not started |
| P3 | OpenAI-compatible video jobs API | no | implemented_working/unit_tests | Text-to-video only; rejects the reference input rather than supporting it. | keep | not started |

### Agents and tools

PaRuX's coding and computer-use stack is more defensible than LU's in isolation: an AppContainer sandbox with no network by default, a transactional user-driven apply with crash recovery, durable checkpoints with a dispatch journal, single-use computer-use approvals, and a patch tool that tolerates whitespace but refuses ambiguity. It is not yet a validated coding agent. The 2026-09-21 external-target failure has its named causes fixed in code and unit-tested, but no external-target acceptance is recorded, the 8k resident window is still open, and the coding path has no Ask or Plan mode and no approval for arbitrary commands. LU leads on operator-facing controls (Ask, Plan and Bypass presets, a documented consent model for computer use, epoch-based repeat detection, an all-or-nothing edit contract, default-off remote permissions) and on the breadth of its built-in tool set. LU has no OS-level sandbox for its shell, no rollback journal and no independent verifier. Top actions: external-target acceptance, usable 8k operation, Ask and Plan modes, a sensitive-action gate for computer use, and a threat review of remote approve-once.

| Pri | Feature | LU | PaRuX now | Gap | Action | After this work |
|---|---|---|---|---|---|---|
| P0 | Coding agent on an external .NET repository (2026-09-21 real-run fail… | no | partial/e2e_real | No external-target acceptance after the fixes. The 8,192-token resident regime is still open. Only a synthetic target has passed. | implement | not started |
| P0 | Long-horizon coding memory and context budget for small resident wind… | partial | partial/unit_tests | The 8,192-token resident configuration is still too tight; the fixed system prompt and evidence share are still large. | improve | not started |
| P1 | Agent execution loop and step protocol | yes | partial/e2e_real | External target not re-run. Small resident window fails. Loop control works but outcome quality depends on rows 20 and 11. | improve | not started |
| P1 | Model action protocol and tool-call robustness for small local models | yes | partial/static | No repair layer for near-JSON. No fenced-block extraction. No bounded retry for empty output found in the step loop. | improve | not started |
| P1 | Permission model for coding actions | yes | partial/unit_tests | No Ask or Plan (read-only) mode for coding runs. No approval for arbitrary commands before they run. | implement | not started |
| P1 | Shell and process isolation for agent commands | partial | partial/unit_tests | Sandbox covers coding runs only, not chat run_command or computer use. Elevated validation gate not run, so there is no real-run pass. | keep | not started |
| P1 | Loop, repeat and stall detection | yes | implemented_working/unit_tests | No epoch-scoped rule for identical reads; detection is count-based only. No narration-stall nudge. The 2026-09-21 loop ran about 35 minutes before the stop. | improve | not started |
| P1 | Completion verification and claim gating | partial | partial/unit_tests | Direct /code runs bypass the Core verifier. Worker receipts are unauthenticated. | improve | not started |
| P1 | Computer use: consent, approvals and safety boundaries | yes | partial/e2e_real | No sensitive-action confirmation (send, buy, delete, permission change). No never-touch list. No password-field guard. Remote approve-once path needs review (row 19). | implement | not started |
| P1 | Remote and mobile control | yes | partial/unit_tests | Remote one-action approval of desktop computer use needs a threat review (device binding, per-action hash, desktop-visible prompt). No default-off switches for remote agent use verified. | improve | not started |
| P2 | Diff review, checkpoint, rollback and apply-back to the user's tree | no | implemented_working/e2e_real | Apply button stays enabled after apply (open item, plan line 2746). Filesystem journal is not an OS transaction and has a residual TOCTOU window. No PR-resume flow. | keep | not started |
| P2 | Durable checkpoint, dispatch journal and crash recovery | no | implemented_working/unit_tests | No end-to-end crash-recovery run recorded. | keep | not started |
| P2 | Sub-agent delegation and fan-out | yes | partial/static | Parent blocked while children run. No per-child budget or parallel cap verified. | improve | not started |
| P2 | Browser automation | no | partial/unit_tests | No recorded real run against a local web app. Only reachable from coding runs, not from chat. | keep | not started |
| P2 | MCP client (external tool servers) | yes | implemented_working/unit_tests | Not reachable from the coding agent (no mcp reference in CodingAgentService.cs). Built-in name-collision refusal not verified. | keep | not started |
| P2 | Multi-step workflows | yes | partial/static | No prompt, condition, loop or user-input step types. No per-step tool approval. CustomWorkflowService is for ComfyUI media, a separate concern. | improve | not started |
| P3 | Edit primitive for source files | yes | implemented_working/unit_tests | No all-or-nothing multi-hunk batch action found; one range per call. Tolerant matches can re-indent silently (the result note reports it). | keep | not started |
| P3 | MCP server (PaRuX as a tool provider) | no | partial/static | No test class or real client run verified. Tool scope needs review under the permission model. | keep | not started |
| P3 | Document RAG | yes | implemented_working/unit_tests | No retrieval quality numbers from a real corpus in this audit. | keep | not started |
| P3 | Persistent cross-chat memory | yes | partial/static | No write-decision or de-duplication path verified. No memory test class checked. | improve | not started |
| P3 | Error recovery and cancellation | yes | implemented_working/unit_tests | None major. | keep | not started |
| P3 | Web search and page retrieval for agents | yes | partial/static | No lightweight page-fetch action for coding runs; browser actions are the only route to page content. | improve | not started |

### User experience

UX audit completed (ux domain) on PaRuX (worktree feature/parux-competitive-integration-20261019, HEAD 117fcf7) and LU v3.0.6, with static read-only and test names; no build, no execution, no network. In the four golden paths the counts are similar for the first chat (LU about 9 clicks, five of which are confirmations or skips; PaRuX 7-10 clicks with more decisions and a Load step), while LU requires fewer interactions for the first image (3-5 vs 5-10), the first video (4-6 vs about 6-9) and the first coding task (4-6 plus approvals vs 6-7). PaRuX is stronger on explicit consent, apply confirmation without commit, tested hardware-fit advice and resumable downloads. The priority gaps are: onboarding does not return to the chat after the handoff to Model Hub, manual checkpoint activation, the missing gallery, the apply confirmation in Italian inside an English interface and error messages with raw text. UI tests and FirstModelAdvisor are present but not executed.

| Pri | Feature | LU | PaRuX now | Gap | Action | After this work |
|---|---|---|---|---|---|---|
| P0 | First-run onboarding and golden path to first chat | yes | partial/unit_tests | The recommended first-model path hands off to Model Hub and never returns to onboarding; there is no success state tied to a working chat; the ready check speaks only about the text runtime. | improve | not started |
| P1 | Chat home and sidebar navigation | unknown | partial/static | After the Model Hub hand-off the only ways back to chat are New chat or a history item; CLAUDE.md names chat as the home workspace. | improve | not started |
| P1 | First-install effort and bundled runtime | yes | partial/static | The bundled baseline is not reflected in the first-run copy, so the user may still be asked to install a runtime; no hash-check guidance is in the product docs reviewed. | improve | not started |
| P1 | Image and video checkpoint activation | yes | partial/static | Two manual steps and a wait for a deterministic follow-up; the user must return to Model Hub after the download. | improve | not started |
| P1 | Download manager (tray, pause, resume, remove) | yes | partial/static | Raw job states (queued, downloading, cancelled, failed) are shown; no progress bar, ETA or speed; visible only inside Model Hub; no completion notice for text downloads. | improve | not started |
| P1 | Runtime manager and component lifecycle (Detect, Locate, Install, Ski… | yes | partial/unit_tests | Runtime health is invisible in Simple mode; after onboarding, install and locate actions exist only in the Advanced Runtime workspace; copy promises a Settings path that does not exist. | improve | not started |
| P1 | Error messages and recovery guidance | yes | partial/static | Raw exception text with no cause, state or next action; the same failure is worded differently across views; Italian strings mixed in. | improve | not started |
| P1 | Language consistency of user-facing strings | yes | partial/static | The apply confirmation, a safety-relevant dialog, is in a different language from the rest of the UI; strings are hard-coded instead of resource-based. | improve | not started |
| P1 | First coding-agent task (golden path 4) | yes | partial/unit_tests | About 6-7 actions against about 4-6 in LU; a folder is required even for a first try; the confirmation is Italian; the start step is an extra decision. | improve | not started |
| P1 | Media gallery and output browsing | yes | missing/static | No visual gallery; generated images are hard to find; no thumbnail or reuse-as-source action found. | implement | not started |
| P1 | Model Hub discovery and search | yes | partial/unit_tests | Too many concepts for a first-time user (candidate, evaluation, criteria, utility policy); the curated starter path is buried in a very large view. | improve | not started |
| P1 | Automatic model selection and visibility of the answering model | partial | partial/static | Simple mode gives no label of the answering model and no visible route explanation; routing is invisible to the default user. | improve | not started |
| P1 | Simple vs Advanced interface mode | partial | implemented_working/unit_tests | Simple mode hides the only recovery surfaces (runtime, routing) and shows no runtime or model health; the mode explanation is a single line. | improve | not started |
| P1 | Progress and waiting feedback for long operations | yes | partial/static | No determinate bars for installs and downloads; no ETA; waiting states differ between workspaces. | improve | not started |
| P2 | Create workspace (image, video, edit, 3D, speech) | yes | implemented_working/unit_tests | No batch, no prompt helper, no readiness card; the 3D engine installation is pointed at elsewhere. | improve | not started |
| P2 | Notifications for completed or failed background work | partial | partial/static | No notification for downloads, runtime installs or failures outside media jobs. | improve | not started |
| P2 | Settings information architecture | yes | implemented_working/static | One very large page with no topic navigation; hard to change safely and hard to find things in. | improve | not started |
| P2 | Diagnostics export and logs | yes | partial/static | No one-click diagnostics bundle found; a Simple-mode user has no way to send useful diagnostics. | improve | not started |
| P2 | Accessibility (names, contrast, focus, keyboard) | yes | partial/static | No contrast or focus automation found; keyboard map not verified; screen reader behaviour not tested. | improve | not started |
| P2 | Responsiveness at narrow and low window sizes | yes | partial/unit_tests | One resize test; no width matrix; no check that controls stay unclipped in Model Hub or Settings. | improve | not started |
| P2 | Guided tour and in-app help | partial | partial/static | The tour is skipped on the recommended first-model path; no replay from help was found. | improve | not started |
| P2 | Hardware-fit advice and first-model recommendation | yes | implemented_working/unit_tests | No gap of substance found in this audit; the advice is shown only during onboarding and in Model Hub. | keep | not started |

### Licences and compliance

LU v3.0.6 is AGPL-3.0-only with consistent metadata, per-package notices for its voice and computer-control stack that are tested against its lock files, and an executable npm licence gate. Its own gaps: the installed folder ships MIT llama.cpp DLLs with no licence text; its notices omit ComfyUI (GPL-3.0), llama.cpp, piper-tts (installed unpinned), the OFL fonts, FFmpeg wheels and the front-end npm closure; it ships 491 presenter preview images that its own code comment attributes to HeyGen, with no licence record. PaRuX is MIT with a real provenance model, a CycloneDX SBOM, and a model-licence consent gate with unit tests. Its release-facing gaps: the MSI notice labels WindowsAppSDK MIT (it is Microsoft terms) and says llama.cpp and uv are not embedded (the staging script embeds them); the NuGet notice omits four shipped packages; the Full Offline bundle redistributes model weights with licence strings only; the Android build excludes META-INF Apache and LGPL licence texts. No LU identifiers appear on HEAD or main, so no LU code is in PaRuX. Line-referenced detail is in the part file, which is 154 lines.

| Pri | Feature | LU | PaRuX now | Gap | Action | After this work |
|---|---|---|---|---|---|---|
| P0 | R1: Microsoft runtimes redistributed in the Windows MSI | no | partial/static | A Microsoft-terms component is labelled MIT, Windows ML and OnnxRuntimeGenAI.WinML have no notice row, and section 3 distribution conditions are not recorded. | improve | not started |
| P1 | R2: llama.cpp and uv archives staged into the MSI | yes | partial/static | The notices contradict the build. MIT and Apache texts are not in the payload unless the archives carry them (not verified: no download). | improve | not started |
| P1 | R21: Full Offline bundle redistributing model weights and runtimes | no | partial/static | Model weights are redistributed with licence strings but no licence or attribution texts. The notices do not describe the redistribution, and the licence of the curated model is not in the repository. | implement | not started |
| P1 | R20: Android APK licence texts | no | partial/static | Apache-2.0 section 4 asks that recipients receive a copy of the licence, and the exclusion can remove those copies. The artifact responsible for the exclusion was not identified because the Gradle gr… | improve | not started |
| P1 | R3: Notice coverage of the shipped NuGet graph | partial | partial/static | The notice table and the SBOM can drift; no test compares them; transitive packages are covered only by family wording. | implement | not started |
| P1 | R4: ExLlamaV3 managed runtime pack | no | partial/static | A third-party runtime with an owner, wheel index and CUDA build has no licence, version record or notice; its upstream licence was not read. | implement | not started |
| P1 | R5: Upstream identity and licence in the lifecycle UI and component i… | no | partial/static | The notice's own rule is not implemented. Users are not told that ComfyUI is GPL-3.0 software that PaRuX does not author. | implement | not started |
| P1 | R6: Model and dataset licence review, consent and credit | yes | implemented_working/unit_tests | No attribution or credit field exists in model or dataset contracts (the only Attribution property is SkillContracts.cs:19). The consent flow has no end-to-end run. | improve | not started |
| P1 | R7: AGPL-3.0 reuse boundary and provenance register for LU | yes | partial/static | LU is not registered as a reference-only AGPL project with the spec-only rule, and the Odysseus identifier differs between files. | improve | not started |
| P2 | R9: Licence capture for managed Python environments and FFmpeg | yes | partial/static | No record per environment; the licence of the FFmpeg build behind imageio-ffmpeg is unrecorded; no GPL or LGPL check runs before install. | implement | not started |
| P2 | R13: Transitive dependency licence gate (NuGet, Gradle, Cargo, PyPI) | yes | partial/static | PaRuX has an SBOM but no executable licence test; the notice and the SBOM can drift; the Gradle graph has no licence check. | implement | not started |
| P2 | R19: Speech and voice model licences and credits | yes | partial/static | Speech models have no notice row or credit path, and the Parakeet licence is unrecorded. | implement | not started |
| P2 | R10: Contributor inbound licence | yes | missing/static | Contributions are licensed only implicitly. | implement | not started |
| P2 | R11: Provenance register for reference-only and vendored sources | partial | partial/static | No LU entry; the vendored commit is missing; the Odysseus identifier differs between files. | improve | not started |
| P2 | R8: SearXNG search backend | yes | implemented_working/unit_tests | None for licence; optionally label AGPL-3.0-or-later where the endpoint is configured. | keep | not started |
| P2 | R12: Non-commercial or community image-model licences | yes | missing/static | Missing by design. If a non-commercial model is ever offered, no labelled consent path exists yet. | keep | not started |
| P3 | R14: Vendor installers and external engines fetched at run time | yes | implemented_working/static | None. PaRuX keeps vendor installers out of its install path. | keep | not started |
| P3 | R15: Brand, logo and trademark boundary | no | partial/static | No written rule says that third-party names or logos are not reused; the status of identity assets is implicit. | improve | not started |
| P3 | R16: Vendored llama.cpp conversion subset | no | implemented_working/static | The upstream commit of the vendored subset is not recorded (OPEN_SOURCE_PROVENANCE.md:13-27). | keep | not started |
| P3 | R17: Licence presentation in the product and installer | partial | implemented_working/static | No in-app viewer for the shipped notices; the presence check was not run. | improve | not started |
| P3 | R18: Build and signing tooling licences | yes | partial/static | The policy is documented; its enforcement was not verified. | improve | not started |

