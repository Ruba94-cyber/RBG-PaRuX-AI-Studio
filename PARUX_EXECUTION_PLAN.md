# RBG PaRuX AI Studio — Canonical Executive Master Plan

Last executive refresh: **2026-10-30**.

### 2026-10-30 desktop GPU/Core reconciliation (PARTIAL — Windows acceptance outstanding)

The read-only Settings hardware monitor now obtains Core's normalized adapter and installed-backend eligibility, rather than treating NVIDIA VRAM or the largest DXGI adapter as evidence that local inference can use it. AMD/Intel UMA reports shared RAM separately from dedicated VRAM. A pure mapper guards cross-adapter telemetry attribution and still distinguishes an externally measured GPU from a verified executable backend when Core is offline; Settings model fit fails conservatively to CPU/RAM or Unknown on a failed Core probe. Core inventory is refreshed on a short TTL for eGPU changes; vendor telemetry and Core status are sampled concurrently with bounded timeouts and failed-probe backoff. Focused deterministic unit tests were added for mixed/eGPU, Intel/AMD UMA, unsupported runtimes, missing Core and Windows software adapters; **none has been executed**. WinUI builds, xUnit, FlaUI, real unplug/replug and inference on non-NVIDIA hardware remain pending. This is not a release acceptance claim.

### 2026-10-30 cross-PC compatibility continuation (PARTIAL — physical acceptance outstanding)

The public `main` branch now treats CPU-only Windows x64 PCs as first-class model-fit candidates when measured system RAM exists, instead of requiring GPU VRAM to produce a verdict. The Settings fit label distinguishes CPU/RAM-only compatibility from GPU offload and warns of reduced speed. No GPU memory is fabricated when DXGI or vendor telemetry is unavailable. Model-fit previews with explicit context but no parsed GGUF header take the marked size-heuristic path. The desktop fit estimator and Core hardware profile resolver both use a conservative half-RAM budget through 16 GiB, retaining the older 8 GiB/25% reserve for larger PCs; live admission remains independently constrained by current free-memory evidence. Narrow/high-DPI Gallery cards now stack their controls, and Model Hub's quantization dialog no longer requires a 330px-wide picker.

Regression tests were added/extended for CPU-only 8/16/32 GiB, 4–64 GiB reserve profiles, Intel UMA with 8 GiB RAM, AMD UMA, NVIDIA/dedicated and unknown/missing-inventory behavior; these are source changes **NOT EXECUTED** in this environment. Before declaring broad hardware compatibility, execute the .NET tests, WinUI build, FlaUI, real local text and media operations, live memory-pressure and device disconnect/reconnect checks on the concrete Windows machines listed in `docs/validation/PHYSICAL_PC_ACCEPTANCE.md`. Linux/macOS/x86 are not supported targets; unsupported model backends must still report unavailable rather than silently claiming support. BIC/release acceptance unchanged.

### 2026-10-30 UI/UX continuation (PARTIAL — Windows validation pending)

On main, progressive disclosure now covers Model Hub, Chat manual model labels, Model Routing, Tools/Automations, Media Gallery, Runtime suggestions and context controls, Observability, Updates and Projects. This continuation adds a responsive Settings category selector (with the desktop rail preserved), per-category on-demand Settings Core refresh in place of eager cross-category fan-out, compact Create media model labels with an exact-identity Details dialog and an optional global media lane editor, and concise first-run Onboarding with collapsible runtime and storage details. Existing Core mutation endpoints, model pin identity, runtime ownership restrictions and approval boundaries were not intentionally altered.

Static source review and XAML tag-balance checks were performed; source-level xUnit contract tests were added but **NOT RUN**. WinUI compilation, real responsive/window-size behavior, keyboard/screen-reader paths, FlaUI regression and physical Windows/GPU acceptance are **NOT RUN**. No release gate, benchmark improvement, or BIC acceptance row is claimed. Follow up with local Windows build, relevant xUnit filters, FlaUI Settings/Create/Onboarding/Model Hub tests and a clean-install manual pass; fix any failures before classifying UX work as DONE.



**Agent Platform V2 implementation (isolated branch, NOT release-accepted):** [docs/AGENT_PLATFORM_V2.md](docs/AGENT_PLATFORM_V2.md) specifies a dependency-gated integration track for context-surviving local coding agents, optional OpenCode execution, Windows UIA/visual Computer Use, Internet/browser work and real UI verification. The isolated branch now includes final-prompt exact-token admission, checkpoint continuation and write-ahead dispatch recovery, local search, guarded CDP browser/UIA integration, read-only OpenCode observation, browser UI evidence redaction, ephemeral task-owned browser profiles, same-session coding-run exclusion, typed DOM postcondition assertions, strict local-only UI mutation authority, verified-process-exit browser shutdown, corrected typed CDP retry/denial handling, filtered literal private/metadata IP targets even in Internet ON mode, and hardened coding-task resume so answered questions or delegated pauses cannot waive pending build/test verification (including legacy checkpoint repair). Core now independently rejects malformed/pending coding verification metadata, binds claimed successful commands to the task checkpoint, and requires actual task-owned stored PNG/SHA-256 screenshot evidence with stable foreground-window and UIA provenance before an UI-verification task can reach the verifier model. New focused tests and SQLite-backed integration cases are wired but have NOT run on Windows. The current draft additionally includes a disposable WinForms UIA E2E fixture and optional local runner, nonce-scoped localhost browser fixture with Node contract tests, strict WinApp JSON assertion checks, invalidation of all previously satisfied coding verification scopes after every testable edit, retained task checkpoints until independent review, fresh Core-approved apply gating, same-review diff fingerprints for single/multi-repository adoption, and stable UI AutomationIds in coding controls. Coding command success is now bound to a complete Git-state fingerprint saved in the durable checkpoint, rechecked at done and again by the desktop worker before Core handoff; the task result carries this fingerprint and Core/adoption gates reject malformed or absent digests. Indirect file edits after a successful build/test invalidate the old evidence, with the narrow trust boundary of worker-reported command results explicitly documented. Coding task execution also now holds a SHA-256 keyed exclusive OS file lease for the entire RunAsync scope; this prevents two PaRuX processes sharing the same state root from interleaving one checkpoint, while cross-host Core fencing with distinct state roots remains open. None of these constitutes physical Windows acceptance, and the source still has substantive unresolved software gaps. The shadow-to-original project copy now treats missing Git status, failed intent-to-add and unverifiable destination/source hashes as hard failures, verifies both paths of renames before overwriting/deleting, and reports missing source/deletion/baseline-commit failures rather than falsely marking them applied. Added conflict regressions and symlink-safe mirror traversal. Crash-safe rollback for a partially applied non-Git folder remains an explicit software gap, not merely a PC test. A typed unresolved-journal result now opens a recovery dialog (restore originals, accept an all-applied state behind a second irreversible-deletion confirmation, or inspect a conflicting or invalid journal) backed by the locked recovery service, and a failed journal check no longer leaks the project lease; this is unit-tested only (Shadow filter 86/86 passing) and has NOT been exercised by a real crash, power loss or the launched UI. Approved coding changes now carry the exact HEAD-bound Git workspace fingerprint from the chat confirmation into the single-, multi- and shadow-repository apply services. Each service rejects changed source both on entry and after preparing temporary intent-to-add patches; shadow copies also reject traversal/device/symlink paths, and the recursive shadow mirror no longer follows reparse-point subtrees. Real Git positive/negative adoption regressions and shadow path tests are selected in the local Windows runner but are NOT RUN here. Multi-process races after the last proof and partial shadow-folder write rollback remain genuine software tasks. Worktree provenance now distinguishes a trust check from actual Git presence: a single repository must have a real .git marker, and aggregate workspaces must contain 2..16 validated direct Git authorities. A prior green verification receipt now binds the HEAD commit as well as tracked/untracked changes in each alias, so committing after testing cannot silently retain an old PASS. Windows-safe task/alias validation is consistent across multi-worktree creation, review/apply and verification discovery; traversal, reserved device names and duplicate case-insensitive aliases are blocked before filesystem creation. Genuine temporary Git and actual multi-worktree regression tests are wired but NOT RUN on Windows. The verified-source gate is now stricter: all authorized Git aliases must participate in a composite `verify-all` command, with safe alias names, no unsupported third repository, and no invented aggregate-root test; an accepted discovered command requires an unchanged, Git-tracked root verifier manifest before and after execution so a rewritten npm/Make/build script cannot produce a false green. Browser completion checks now require an exact nonblank `text_equals` or `value_equals` assertion after a mutation; trivial `exists(body)` no longer clears the durable postcondition. Genuine Windows/.NET and browser acceptance remain unexecuted. Further changes require an explicit coding `done` claim before verifier handoff; retain a durable mandatory `browser_assert` postcondition after browser click/type/eval, check that state again in Core and before user adoption, and attach new child CDP targets paused with fail-closed browser termination on unguarded secondary contexts. An opt-in Node 22 + Chromium browser/CDP loopback fixture was added to the local runner but has not passed real target-PC acceptance. It still lacks an OS-confined executing OpenCode adapter, browser-wide/OS-level DNS and egress confinement for unrelated browser processes, and a verified coding → build → real native UI assertion → independent acceptance flow. Windows build, xUnit, CDP/UIA, GPU and real-device acceptance for this branch remain **NOT RUN**. Existing BIC statuses and release gates are unchanged. Do not mark this experimental branch as a completed or shipped capability.

This is the **one active operational roadmap** for PaRuX. Retained dated evidence describes earlier work; old status words are evidence only and must never be interpreted as current blockers. The public-history cleanup intentionally removed archived plans and old commit history. Historical commit identifiers in dated evidence are contextual labels, not resolvable references in the sanitized repository.

## Current status

**PARTIAL — active software gaps; PHYSICAL_PC_ACCEPTANCE_REQUIRED**

### 2026-10-30 desktop UI progressive-disclosure follow-up (PARTIAL; NOT Windows-accepted)
- Direct commits on public `main` streamline Model Hub, Chat manual model selection, role routing, Runtime, Updates, Projects, Gallery, Tools & Automations and Observability, without changing model download ownership, runtime mutation safeguards or application configuration data.
- Model Routing now keeps full capability evidence, quantization and scores behind a selectable candidate-details dialog instead of showing long evidence text in every row.
- Settings search indexes labels already present in the UI and navigates to the matching category; compact navigation, empty results and suggestion keyboard behavior are covered by source contracts only.
- Create shows its cancel button when an operation is in progress; automatic setup and media selection still use the existing server-owned flows.
- Source-level xUnit contract cases were written and checked for expected symbols, but no .NET/WinUI build, xUnit run, FlaUI UI regression or physical Windows validation has been performed in this environment. All existing BIC/release and hardware gaps remain OPEN. This UI work must not be described as complete release acceptance.

### 2026-10-28 release 2.0.0 reconciliation (PARTIAL; published by owner decision)
- Verified on one PC (RTX 5080 16 GB, 27 GB RAM): release gate PASS (Server 10606 passed / 0 failed / 9 skipped; Windows + Android builds); FlaUI Regression 233/236 then the 3 failures (Create status/cancel pushed below the fold by tall panels) fixed in `45ced67` and re-verified (26/26 Create/Inpaint/Responsive UI tests); the full Regression suite is re-run on the final commit before tagging.
- Live in the real app: automatic post-download tuning for text (queued -> running -> honest result, cancel creates nothing), media tuning after enabling the setting, maintenance probe fixed (13 consecutive failures -> 0), generation with SDXL-turbo, Qwen-Image, FLUX.1-schnell, LTX-Video 2B, Chroma1-HD (about 15 min first image), mask inpaint with a really painted stroke, 3-slot LoRA stack with family refusal (409), character card import through the real file picker, Chat EN/IT region-edit routing.
- Performance (n small, one PC): first image after text work 122 s -> 20.9 s with prewarm; chat during a media job about 175 tok/s (CUDA media assistant; was 27 tok/s on Vulkan); resident 27B back at 60 tok/s; ComfyUI cold start 24-33 s -> 12.7-16.8 s (bytecode cache). Engine-only comparison vs LU's bundled llama.cpp (Vulkan vs our CUDA, not the LU app): 60 vs 46 tok/s, load 6.7 s vs 14.4 s. LU publishes no benchmarks; most LU numbers are unknown.
- NOT proven / not implemented: real speech through a microphone (voice mode unit-tested only), non-NVIDIA/CPU/low-VRAM tuning branches (tests only), tiled VAE, Qwen-Image-Edit inpaint, character LoRA training, Wan 2.2 14B, HunyuanVideo, CogVideoX. 12 BIC rows and PHYSICAL_PC_ACCEPTANCE_REQUIRED remain open; this is not a source-complete claim.

### 2026-10-27 media discovery and competitive continuation (PARTIAL)

Locally Uncensored remains the reference for feature coverage, model support, usability and performance; superiority requires repeatable same-hardware, same-task comparisons including cold start, first-token latency, quality and real UI workflows. Its 3.0.7 release describes cross-Create characters, saved ComfyUI workflows, multi-image editing and LoRA links; product claims are not evidence of PaRuX parity. Current source has talking-avatar services but needs real UI verification; Hugging Face LoRA adapter is not yet wired into the UI/download path and lacks the required file hash; synchronized music-video orchestration remains open.

Media discovery now reserves repository inspection capacity by capability before popularity fill (40 maximum); focused discovery tests 43/43 passed and live Hugging Face image discovery passed. Live video proposals remain absent: LTX 2B 0.9.5 declares `other` in repository metadata despite a version-specific Open RAIL-M license; do not bypass license review. Media catalogue downloads now preserve the listed revision and expected SHA-256, reject a mismatched transfer digest, and require review for unverified license hints; 64 targeted tests passed and independent re-review passed. Full Server suite on the changed source: 10,355 passed, 0 failed, 9 skipped. Gate, full UI regression, signed MSI and real media download remain pending.

Voice timeout and diagnostics from b642901 have 44 focused tests passing; Read aloud toggle passed in the real app. Two local speech-playback recording attempts produced no prompt text, so microphone transcription is not accepted as fixed in the real app. Create model/lane controls, missing-source cutout validation and the persona picker were observed. A real UI reset exposed that automatic routing was labelled as a manual choice; the subsequent source fix distinguishes the active routed model from a saved pin across Create, Model Hub and Settings. Focused routing/presenter tests passed and independent re-review passed, but the updated UI has not been demonstrated live and the complete Server result above predates this fix. End-to-end cutout, persona import, Automatic reset and remaining requested live media runs remain pending.

The first post-routing release gate failed: 10,359 Server tests passed, 3 presenter tests failed and 9 were skipped. Those tests assumed the old pin flag and old card-active semantics; their fixtures and assertions now cover the new contract, and 72 targeted tests plus independent review passed. A new full gate is required.

### 2026-10-24 Windows integration repair and Discovery verification (in progress)

The local checkout was fast-forwarded to canonical `main` at `da136246` before validation. That source did not compile: integration had omitted download request/entry contracts, recommendation health-budget plumbing, ComfyUI recovery parsing, and test-project source/fixture wiring. The repair restores those contracts, requires compatible safetensors headers before managed checkpoint promotion, binds internal companion downloads to their pinned digest/size, and rejects incomplete source-declared transfers. ComfyUI recovery uses the shared saved-output validator and treats completed history without outputs as a failed prompt. No release or GPU acceptance is claimed. Current-session logs and continuation details are under `.ai/artifacts/engineering-20261024/` and `.ai/handoff/claude-engineering-20261024.md`; 2026-10-24 result: Server and App (Debug) now compile; the embedded media companion registry is shipped again, the runtime output-sanity client uses the attested handler, ModelHubView/ChatView merge breakage is repaired. Targeted Discovery/download/media/recommendation/lifecycle/runtime-guard tests: 668 passed, 0 failed. A full Server run before the last fixes had 21 failures; all but one were fixed and re-run in targeted form. Full Server suite on the previous commit: 8479 passed, 1 failed, 6 skipped; that failure (video-resume cancel) is now fixed: recovery no longer lets a media dependency error or a user cancel escape and strand a job as "running", and the test fixture disables companion resolution like it disables live preflight. Targeted MediaJob/ComfyCancel re-run: 59 passed. Release gate `tools/release-gate.ps1 -Restore` PASS on 2026-10-24 (full Server suite 8480 passed, 0 failed, 6 skipped; App Release and Android builds). ModelHubView never assigned its section fields, so the Simple/Advanced reorder and retitle silently did nothing; now wired (App builds). Runtime Manager had no AutomationIds at all (untestable, inaccessible); it now exposes stable ids and `RuntimeManagerCoverageTests`. Workflows and Lifecycle Lab views still have none (open gap). Workflows and Lifecycle Lab now also have AutomationIds and FlaUI tests (compiled, NOT RUN). Model Hub gained separate abliterated and audio categories; the Smart Router task classifier now matches whole words, prefers explicit image/video requests over coding keywords and recognises Italian media phrases (targeted tests pass). New FlaUI tests `ModelHubModesCoverageTests` and `RuntimeManagerCoverageTests` compile but are NOT RUN: the desktop is in use by another person, and the earlier ExplicitDiscovery/CandidateScope FlaUI run (8 timeouts) is not attributable to code until re-run on a free desktop. GPU, MSI clean-install and physical checks remain NOT RUN.

### 2026-10-26 public-repository continuation (PARTIAL: item 1 done; item 2 partial; item 3 not done; item 4 not run; item 5 host lifecycle done, VM impossible)

**Item 1 — DONE (real UI run passed on 2026-10-26).** Three defects were found and fixed test-first: (1) `CodeWorktreeApplyService` decoded `git diff` stdout with the console code page and re-encoded it, corrupting non-ASCII hunk context so `git apply --check` failed at `CodingAgentArtifactStore.cs:156` (commit `1cc8dc2`: Git writes the patch via `--output`; a second preflight with line-ending normalization handles LF patches on CRLF targets; real conflicts still refused); (2) the review list parsed abbreviated paths (`.../File.cs`) from `git diff --stat`, which as a pathspec matched nothing, so one of two reviewed files was silently skipped while apply reported success (commit `d68da4f`: unabbreviated review stat, apply refuses with "Nothing was applied" when any accepted entry is not a real changed path, Git output decoded as UTF-8); (3) stops were labelled generically and context shortfalls simply failed (commit `5896e4b`: specific stop reasons — context too small / repeated action / no progress / sandbox — shown as the task stage and error with a next step, and before failing on context the agent raises the coding model window once via the Runtime Manager planner, reloads, and retries the step; pure policy unit-tested 11/11, App build 0 errors; the live auto-raise path itself was NOT exercised end to end). Evidence: `CodeWorktreeApplyServiceTests` + `AcceptedFileSelectionApplyTests` + `AgentWorktreeGitTrustTests` 26/26 (new regressions were red first). Real FlaUI/UIA test `Public_clone_artifact_search_recovers_regex_alternatives_and_applies_through_real_chat` on a freshly published app (Qwen3.8-27B UD-IQ4_XS on CUDA 13.4, 16384 context with q8_0 KV cache): PASS 1/1 in 7m50s, task "Completed and verified" unaided, verifier PASS, Apply via the real button and confirmation dialog, `success=True`, both reviewed files applied, applied file contents identical to the reviewed worktree, workspace HEAD unchanged, no manual restart or OK-press. Limits: 11 attempts were needed in total — 5 failed because of my isolated-environment setup (missing context preference gave an 8192-token window), 3 because the 27B model looped on identical reads and the guard stopped it, 1 exposed defect (2); the agent is not yet reliable on this model/hardware (it passed 2 of the last 4 attempts with the corrected setup). Context headroom on a 16 GB card is VRAM-dependent (14 GB model); the 8192-token pause appears when free VRAM is short. `MultiRepositoryApplyService` and `ShadowWorkspaceService` use the same stat/pathspec pattern but were not changed or exercised. The 1.9.7 MSI built earlier (source `b1f7ac3`) contains fix (1) but NOT (2)/(3): rebuild before any publication.

**Item 2 — PARTIAL (local signed MSI built and release-verified; clean-VM install NOT RUN).** Lock, staging, bootstrap, SBOM and verifier changes are committed (`b1f7ac3`): hash-locked llama.cpp b11871 CUDA 13.4 + NVIDIA cudart/cuBLAS DLLs (only `cudart64_13.dll`, `cublas64_13.dll`, `cublasLt64_13.dll`, EULA Attachment A scope, NOTICE shipped); bundled CUDA 13.4 chosen only for NVIDIA compute capability >= 12.0 with driver >= 580, older NVIDIA keeps the managed CUDA 12.4 route, non-NVIDIA uses bundled Vulkan. Verified: PowerShell parse (0 errors); staging under Windows PowerShell 5.1 with all SHA-256/size gates; staged CUDA `llama-server.exe --list-devices` loads `ggml-cuda` and sees the RTX 5080; SBOM 0 unknown licences with NVIDIA licence + redistributable files; `BundledBaseline*/Baseline/Msi` tests 53/53. The owner accepted the WiX v7 EULA in chat and the build process (only) set `PARUX_ACCEPT_WIX7_EULA=1`; signing PFX/password and the PaRuX manifest key were read from the owner's external folders into process env only (nothing printed or stored in the repo). `scripts/build-msi.ps1 -Version 1.9.7 -Release` (clean checkout, source commit `b1f7ac3`) succeeded in 9m01s: `PaRuX-1.9.7-win-x64.msi` 819,793,920 bytes, SHA-256 `fc63e4c26cc942a5f8db1a1f2ab517d318058b21e90d54f8f722c044b2088731`, Authenticode verified on primary binaries + MSI, signed update envelope and provenance produced under the git-ignored `artifacts/installer`. `verify-release-assets.ps1 -Version 1.9.7` PASS (signed envelope + MSI bytes, payload 1580 files equals provenance, CUDA assets + notice, embedded commit in 4 primary binaries). **Size vs public v1.9.6 (265,007,104 bytes): about +555 MB (3.1x; the 1.9.7 MSI is ~819.8 MB, exact bytes in each build's provenance — it changed slightly with every rebuild during this work).** The MSIs named in this paragraph were superseded by later rebuilds that fixed the defects listed in the lifecycle paragraph below; only an MSI built from the final commit may be published. NOT RUN: install/upgrade/uninstall of this MSI (must be in a temporary VM, never on the host), real first-run behaviour from the installed layout, in-app update check against a published manifest, and the full real-UI pass required by the release gate. No push, tag or release published.

**Real-app verification on 2026-10-26 (isolated data root, Qwen3.8-27B on CUDA 13.4, published app at `958d76a`+).** Exploratory UI testing of the real desktop app through UI Automation (screenshots read by the tester) found and fixed, test-first where logic was involved (commit `473830e`): raw Markdown in Help/Keyboard-Shortcuts dialogs and no table rendering in chat (new pure `MarkdownTableParser`, 6/6 tests), a swallowed `NullReferenceException` raised by Settings auto-check handlers during view initialisation (they could also have saved defaults), no feedback for an empty Project goal, a raw `File not found ... llama-server.exe` string for a failed media start, a Model Hub count that ignored linked models without saying so, and a clipped chat footer without a scrollbar. Live re-check after republishing: Help/Shortcuts render, chat tables render, no new startup errors, empty-goal message shows (4/4 PASS); footer scrollbar set to always visible, NOT re-checked. Verified working: chat answer, Italian answer, regenerate/versions, edit-and-continue, search chats, incognito (not listed in Recent), Stop during streaming, attach picker, project creation, all 13 Settings categories, interface Simple/Advanced toggle. Open, not fixed: Model Hub suggestions and the maintenance ranker still compare only PaRuX-managed models (a linked external model is treated as absent); Advanced-mode sidebar may overflow on short windows (not verified); Projects creation queues planned tasks without confirmation (message now says so; product decision pending); slash commands are not discoverable from the composer.

**Android companion (emulator).** Debug APK built (`gradlew assembleDebug`, 1m11s). New AVD `PaRuX_Test` (API 35 Google APIs x86_64; the owner's other AVDs were not touched). Real flow: Settings > Privacy & Network > Remote access on `192.168.178.49` (confirmation dialog, Core restart from the UI) -> "Create pairing code" -> in the Android app "Enter pairing details manually" with the endpoint, six-digit code and the pinned certificate SHA-256 -> pairing succeeded -> main screen (Chat, PC, Goals, Media, Jobs, Tools, Settings). A chat sent from the phone returned "Rome, Italy's capital" from Qwen3.8-27B with model and routing evidence shown; the conversation history is shared with the desktop and web. NOT RUN: QR scanning by camera, Media/Jobs/PC/Goals/Tools tabs, Live Voice, revoking the device, reconnect after Core restart. Windows showed a Firewall prompt for "PaRuX Server" on first LAN enable; it was left for the owner (system security setting), the emulator reached the Core regardless. The desktop paired-device list was not re-read after pairing (it still showed "No paired devices" in the first read).

**Browser access.** Settings > Privacy & Network > Browser access: a test passphrase was set from the UI, the switch confirmed ("Turn on"), the web app served at `http://127.0.0.1:51123/app` (401 before sign-in), signed in through the login page, the history from desktop and Android was listed, and a web chat returned "OK-WEB" from the same local model. NOT RUN: Models/Runtime/Settings tabs of the web UI, sign-out/session revocation, a real reverse proxy.

**Packaged-app lifecycle on the host (Item 5 — PARTIAL: real signed-MSI lifecycle done on this PC; clean-VM NOT possible).** VirtualBox 7.2.22 runs here through the Windows Hypervisor Platform (Hyper-V/WSL2 hold the virtualization hardware; host settings were not changed): Windows 11 setup hung four times (default, 2 CPUs, no paravirt, NEM ring-0 runloop off) and the Windows 10 Enterprise evaluation answer file failed twice, so no VM test ran. On the owner's instruction (host fallback, local-AI part only) the existing app data was moved aside, not deleted (`%LOCALAPPDATA%\PaRuX` 46 GB and `LocalAI` renamed `*.preclean-20261026`; `Documents\LocalAI` with models and signing keys untouched) and the signed per-machine MSI was installed elevated (the owner's UAC policy elevates without a prompt). Verified on the real host: install 1.9.7 exit 0 in ~38 s (1580 files, four primary binaries Authenticode Valid, version `1.9.7+<commit>`, Start Menu and PATH); uninstall exit 0 in 13 s (Program Files, registry entry, shortcuts and PATH removed, user data preserved); install of public 1.9.6 (1523 files, signed) then upgrade to 1.9.7 (exit 0, one entry, 1580 files, data preserved). Clean first runs of the installed app, with a fresh empty Local AI root and an empty `%LOCALAPPDATA%\PaRuX`, found and fixed (each test-first, release-gated): (1) the Server never started — the CUDA-aware bootstrap ran in a `[ModuleInitializer]` and deadlocked, leaking one Server process per retry (`fb90203`); (2) the automatic setup could never pick a model — Hugging Face search returns `sizeBytes = 0`, now filled from repository details (`b72d92e`); then it chose the `MTP/mtp-Qwen3.8-27B-Q4_0.gguf` speculative head (`a40c5c7`), a 1-bit IQ1_S quant (`7ccba72`), failed on a transient runtime switch during verification (`7ccba72`) and had no fallback to the next candidate (`e8d28bd`); (3) the ready card offered only "Set up again" and the page said "Text engine not detected" while the bundled CUDA engine was installed (`f3e159b`); (4) the media assistant failed because only CUDA 13.4 was installed and not the bundled Vulkan runtime (`cad742e`). Final clean run of the installed build `cad742e` on the RTX 5080 (driver 617): Server ready in ~5 s, bundled Vulkan b10549 and CUDA 13.4 b11871 installed into the empty root in seconds, the managed runtime updater found b11910, hash-verified, smoke-tested and activated it keeping b11871 for rollback, the automatic setup chose Qwen3.8-27B-UD-IQ3_XXS (~10.9 GB), downloaded, assigned roles and verified the one-time-code chat: "PaRuX is ready" in 5 min 21 s (almost all of it the download), "Start chatting" opened Chat and a real answer came back. This also gives first real evidence for the managed-runtime auto-update item (download, hash, smoke, activation, rollback copy kept). Release gate `tools/release-gate.ps1 -Restore` PASS at `cad742e`+tooling fix: Server 9701 passed, 0 failed, 6 skipped; Windows x64 build and publishability; Android JVM tests, lint, debug and unsigned release APK. NOT RUN: the in-app update check against a published manifest, a second clean machine/VM, FlaUI Regression on the final build, and the original `msiexec` install on a different user account.

**Media on a clean install (Item 3 — PARTIAL: image, video, image edit, music, speech and image-to-3D each ran for real through the app UI on a clean Local AI root; talking character, Character Studio LoRA training and desktop voice conversation are NOT implemented; nothing here was run from the signed MSI yet).** Real defects found on the clean install and fixed test-first (commit `6556401` and later): (1) the catalogue's "PaRuX default" checkpoints (`z_image_turbo_nvfp4`, `flux-2-klein-4b`, `wan2.2_ti2v_5B_fp16`) were `bundled` with no repository, so nothing could download them on a clean PC; they now point at their official Hugging Face files (size, commit pin), and every media catalogue variant with a verified listing downloads from `/resolve/<commit>/`; (2) no text encoder or VAE had a pinned source, so the companion fetch always said "install manually"; the seven shared/family companions are now pinned (repository, path, commit, size, SHA-256, licence from the repository card) and a test requires every supported family to stay pinned; (3) the media assistant (`Qwen3.5-4B-Q4_K_M.gguf`) was looked up only in the Local AI root while downloads land in PaRuX's managed models folder, so a downloaded assistant was reported missing; (4) nothing installed ComfyUI or orchestrated the rest. New `MediaSetupPlanner`/`MediaSetupService` (`GET/POST /api/v1/media/setup[/start|/cancel]?type=image|video|edit`): ComfyUI portable (CUDA PyTorch) install, assistant model, a catalogue-selected checkpoint that fits the PC, activation and pinned companions, as one observable, cancellable, resumable, single-flight run with honest failure text and the licence of every step shown before anything downloads. Create now has a setup card per mode (image, video, edit) and the Create page is organised in tabs (Image, Video, Edit, 3D, Speech, Workflows, Gallery) with example prompts, size presets, an inline result preview and "Open / Show in folder / Edit this image / Animate this image". Chat routing gained natural English and Italian media phrasing ("can you make a picture of…", "mi fai una foto di…", "voglio un video di…", paint/dipingi, clip, filmato, logo, poster) without hijacking how-to prompts ("how do I draw a chart in matplotlib"), and a plain text message after a generated image no longer attaches that image (it used to take the Vision route and fail with HTTP 503). **Verified through the real UI on the clean Local AI root `C:\\PaRuXClean\\LocalAI` (RTX 5080, Debug App + Release Server built from `a60e416`, not yet an MSI):** (1) Create > Image > "Set up image generation": ComfyUI v0.42.1 portable (torch 2.14+cu130), bf16 Z-Image Turbo, pinned text encoder and VAE; Create produced a real 1024x1024 PNG (click to saved file about 2.5 min cold, including ComfyUI start and a 12 GB model load). (2) Chat in English ("Can you create an image of a lighthouse at night during a storm, dramatic waves?") and Italian ("Puoi creare un\'immagine di un gatto che dorme su un divano rosso, luce calda?") each produced a real image inline in Chat. (3) Create > Video > "Set up video generation" (Wan 2.2 TI2V 5B fp16 10 GB, umt5 text encoder, VAE, all pinned) then Create produced a real H.264 1280x720, 5 s, 24 fps MP4 (about 6 min cold, 30 steps) and Chat in Italian ("Fammi un video di un faro sotto la tempesta…") produced a second real clip with an inline player. (4) Create > Edit > "Set up image editing" (FLUX.2 klein 4B, shared Qwen text encoder, flux2 VAE) then Chat "Edit this image: make the sky a dramatic purple and pink sunset" edited the earlier cat image (purple sunset sky behind the same cat). This first failed with "I need an attached image to edit" because the latest assistant message was an unrelated error turn; the latest generated image is now found past error and text-only turns (`LastGeneratedImageTests`). (5) Create > Speech > "Set up speech": the first attempt failed because `uv python install` aborts with "Missing expected target directory for Python minor version link" under `%LOCALAPPDATA%\\PaRuX` (it works in Temp or Documents; junctions can be created there manually), so speech could not be installed on this PC at all; PaRuX now skips uv when the interpreter exists and falls back to a staging install it adopts (`ManagedPythonMinorLinkFallbackTests`); after that the setup completed in about 4 min, Create > Speech synthesized a real 16.7 s WAV and `/api/v1/speech/transcribe` returned "Hello. This is Pa Ru X speaking from your own computer." for it (STT first call 41 s including model load; the Transcribe button itself was not driven because it opens the native file picker). (6) Create > 3D > "Set up image to 3D", "Make 3D" from the last result, "Generate 3D model" produced an 11.9 MB `.glb` (valid glTF 2.0 header, one mesh) in under a minute. Subjective media quality is not graded (owner decision). Independent code review of the setup service (read-only agent) found eight issues; the seven real ones were fixed with tests (companion "all clear" from an absent size, cancel/timeout not stopping jobs, cancel/start ignoring the media type, truncated copy treated as installed, checkpoint without SHA-256 pin, stuck fresh-model queue entry, misleading timeout text); the prior-image heuristic gap is mitigated by the "ask about image" rule. (7) Music (new capability: the catalogue listed ACE-Step as resolver-only with no engine): `MusicWorkflow` (ACE-Step 1.5 turbo all-in-one graph validated by hand against ComfyUI v0.42.1: 30 s stereo 48 kHz FLAC in 26 s), `MusicService` + `POST /api/v1/music/generate` (exclusive GPU lease, managed media runtime, debounced text-model restore, ComfyUI output path confined to its output folder), a pinned 9.34 GB checkpoint setup step (Hugging Face commit `6707deb2…`, SHA-256 `67b0f43a…` verified by the download service, Apache-2.0) and a Create > Music tab (style, lyrics, length, language, audio player, artifact capture) plus a Chat music route in English and Italian ("Compose a song about the sea at night", "Componi una canzone sul mare di notte"; "how do I compose a song in Ableton", "music video", "lyrics of a song" do not trigger it). For song requests the local assistant writes the lyrics in the requested language. Verified through the real UI: Create > Music > "Set up music" downloaded the checkpoint and Create produced a 30 s FLAC whose speech-to-text transcript was "Morning days on no show. Coffee war when the word is still sing it slow, sing it free. This little song is made for me." (the lyrics entered); Chat "Compose a song about the sea at night, calm and dreamy" returned a 45 s song with an inline audio player whose transcript contained assistant-written lyrics about the sea ("Silent Waves against the reef. Moonlight touches gentle crest…"), and the Italian request returned Italian lyrics (detected language "it": "Il mare canta solo e silenzioso… le onde dormono sotto il cielo d'argento"). (8) Character cards: Settings > Personas > "Import character card…" (SillyTavern/Chub v2/v3 JSON, v1 flat JSON, or PNG with an embedded `chara`/`ccv3` chunk; creator notes are not turned into instructions) creates a persona; verified by importing a v2 card, activating it and getting an in-character Chat answer ("Ahoy, landlubber. The name's Captain Mira…"); the file-picker click itself was not driven (the API route was). NOT run: Android/web media tabs, QR scanning, running the media paths from the signed MSI build, Chat video from English phrasing.

**Background removal (new, Item 3 — PARTIAL: engine proven for real, UI/Chat path built but not driven in the real App).** `CutoutService` (managed venv under `ai-runtimes\cutout-studio`, onnxruntime 1.31.1 CPU + Pillow 13.0.0 + NumPy 2.4.6, no system Python) runs BiRefNet-lite ONNX (`onnx-community/BiRefNet_lite-ONNX` at commit `de15b22b…`, 224,005,088 bytes, SHA-256 `56000243…3333`, MIT per both model cards, not gated), downloaded through a size- and SHA-256-checked HTTPS downloader and re-verified before use. `GET /api/v1/cutout/engine`, `POST /api/v1/cutout/engine/install` and `POST /api/v1/cutout/remove` (base64 PNG/JPEG/WebP, 25 MB and 36 MP caps read from the header, magic bytes not file name, work files confined to the cutout work folder, Gaming Mode cancels, desktop-owner only; not exposed to paired devices) return a transparent PNG as a `MediaAsset`, refusing a result without an alpha channel or with no foreground. Setup type `cutout` (engine + model steps, licences shown), Create > Edit "Remove background" plus result-strip button, and Chat: "remove the background / rimuovi lo sfondo / togli lo sfondo / ritaglia il soggetto / make the background transparent" on an attached or last generated image runs the cutout; "replace/change the background", colours, "and then…" and how-to questions stay with the diffusion edit or chat (`CutoutPhrasingTests`, `CutoutEngineTests`). Real run (clean test data root, CPU): install + model download + verify + cutout in 36 s total; 1024x1024 fox photo returned an RGBA PNG (1.33 MB, 75.5% of pixels transparent, 22.4% opaque, 2.1% soft edge), 15.6 s per 1024x1024 image on the CPU. Open: App UI and Chat click-through, large-image timing, GPU/DirectML provider (deliberately CPU-only).

**More media families (2026-10-27 — PARTIAL: Qwen-Image and Qwen-Image-Edit are now supported and downloadable with graphs proven well-formed against a real ComfyUI 0.42.1; NOT yet run on the RTX 5080; LTX-Video remains resolver-only).** `qwen-image` is a supported companion-registry family for the `image` and `edit` pipelines with two templates (`qwen-image-api.json`, `qwen-image-edit-api.json`, new optional registry key `pipelineTemplates`), signature stems read from the real 1933-tensor header of `qwen_image_fp8_e4m3fn.safetensors` by HTTP Range (header only), and pinned companions (`qwen_2.5_vl_7b_fp8_scaled` 9,384,670,680 B and `qwen_image_vae` 253,806,246 B, commit `1f12b17b`, SHA-256 and licence from the repository). Catalogue: `qwen-image-fp8` and `qwen-image-edit-fp8` (20,430,635,136 B each, exact listing, commit and SHA-256 pinned, tier 5). A checkpoint of another family now runs that family's own template (`MediaCheckpointResolution.TemplatePathFor`), never the default graph with a foreign file patched in; the edit runner patches the instruction/seed input names each graph really has. Honest limits: Qwen-Image and Qwen-Image-Edit weights have identical tensor names, so a header cannot tell them apart and the edit gate trusts which file the user assigns to editing; `qwen_image_2512_*` files carry a `model.diffusion_model.` prefix the detector refuses, so they are not offered; the GGUF build stays listed-only; image-to-image still has only the Z-Image graph. Proof so far: structural tests against a snapshot of the real node list, throw-away CPU ComfyUI validation (only the missing model files were reported), no GPU job. Owner to verify on the RTX 5080: download Qwen-Image (fp8) in Model Hub, run Create > Image, then Qwen-Image-Edit for Create > Edit; time it, since 20 GB of weights offload to system RAM on 16 GB. LTX-Video 2B v0.9.5 was verified against its listing (6,340,729,500 B) but needs a full-checkpoint loader and its own video runner contract, so it is not offered.

**Item 4 (performance) — DONE for text decode/prefill on this PC (2026-10-27, RTX 5080 16 GB, quiet machine, `tools/perf-launch-bench.py`, same GGUF files, same prompts, temperature 0, 3 warm runs, medians; raw JSON in the git-ignored `.ai/artifacts/session-20261026/perf/`).** Engines: PaRuX CUDA 13.4 b11912, LM Studio 2.41.0 CUDA12 llama.cpp backend (its own vendor DLLs), Locally Uncensored `lu-llama-server.exe`. Identical flags (`--ctx-size 8192 --batch-size 3072 --ubatch-size 768 -ctk/-ctv q8_0 -fa on -ngl 99 --parallel 1`). Qwen3.5-4B Q4_K_M: decode PaRuX 178.7 tok/s vs LM Studio 167.6 (+6.6%) vs LU 135.7 (+32%); 2k-token prefill 8161 vs 6794 (+20%) vs 7483 (+9%); cold TTFT 0.075 s vs 1.38 s vs 0.081 s. Qwen3.8-27B UD-IQ3_XXS: decode 60.3 vs 59.4 (+1.5%) vs 46.7 (+29%); prefill 1960 vs 1778 (+10%) vs 1412 (+39%); LU cold TTFT 11.2 s vs 0.15 s. Honest negative findings: PaRuX's static default flags are not faster than a bare `-c 8192 -ngl 99` launch (4B 178.9 vs 187.8, 27B 60.0 vs 61.5; q8_0 KV costs about 4-5% decode versus f16 KV), so a static default is not the advantage. The advantage is the automatic per-PC calibration: the existing "Optimize for this PC" pipeline run on the real 27B (workload coding) measured 11 candidates and applied f16 KV, context 32768 and embedded MTP speculative decoding: **125.3 decode tok/s versus 61 for the baseline (2.05x)**, draft accepted 82%, TTFT 325 ms, KV reuse +31.9%. A pass on the PaRuX engine with f16 KV and `--spec-type ngram-mod` measured, on the repetitive code-rewrite prompt, 4B 591.6 vs 185.4 tok/s (3.2x) and 27B 257.6 vs 61.3 (4.2x) with byte-identical output, and on essay prose 4B 342 vs 186 and 27B 81.7 vs 61.4 (the prose output text differs slightly from the baseline, as speculative batching changes greedy numerics; no quality claim is made). New in this build: a freshly downloaded chat GGUF is queued (persisted, 24 h lifetime) and the idle calibration runs after a 2-minute pause instead of 10 so the first real use is already tuned (`PostDownloadOptimizationTests`, `CalibrationAutomationTests`); calibration remains gated by Gaming Off, battery, GPU busy and user activity. Not measured: Ollama, other GPUs, non-CUDA backends, quality evaluation; these numbers say nothing about task quality.

**Release gate (owner instruction 2026-10-26):** before any release, every function must be tested on the real app through its UI (FlaUI/UIA on the real desktop, real models/runtimes), not only by unit or service tests. Items marked PARTIAL/NOT RUN above do not satisfy this gate.

**Items 3–5 — NOT RUN:** real Chat media workflows, comparable performance measurements, and clean VirtualBox install/upgrade/uninstall verification. Do not mark done without their requested real workflow/VM evidence.

### 2026-10-25 managed-runtime auto-update (PARTIAL: unit/contract tested with fakes; real download/GPU/live-launch NOT RUN)
Owner decision: PARUX_MANAGED llama.cpp runtimes (incl. the CUDA 12.4 -> 13.x move for Blackwell with driver >= 580) update automatically; models stay proposals; USER_MANAGED/SYSTEM_DETECTED are informational only. `ManagedRuntimeUpdater` (persisted state machine): driver/compute-capability-aware discovery -> stage-only install beside the active build (hash-verified, sidecar before atomic rename) -> smoke on the staged folder (real llama-server probe, `binary_only` fallback without a model) -> local benchmark gate (<= 5% slower prefill/decode, recorded in LocalBenchmarkHistory) -> atomic pointer switch only when no generation runs and Gaming is off -> probation on the first 2 real launches with automatic rollback -> retention of active + previous. Setting `KeepRuntimesUpdated` (default on) + Settings "Update runtimes now" + Runtime Manager active/previous/roll-back line. Open: live proof on the Blackwell host, probation wiring is launch-level (OOM-style failures also trigger rollback), no CPU/Vulkan-specific benchmark gate beyond the shared probe.

### 2026-10-24 integration batch 2 (verified on this host; NOT release-accepted)

Release gate `tools/release-gate.ps1 -Restore` PASS (Server 8792 passed, 0 failed, 6 skipped; Android build). FlaUI Regression 219/220 on the real desktop (the failing one is the opt-in real-model coding task). Implemented and test-covered: one-click "Set up automatically" onboarding (state machine; real Core/download/chat verification NOT RUN), hardware fingerprint + periodic maintenance service with proposals only (never downloads/switches), runtime backend advisor with Runtime Manager card and Settings "Automatic checks", hardware-driven "Suggested for this PC" in Model Hub with Popular/New/Updated cohorts (no hardcoded favourite models; Flint and Qwen-Image 2.1 are not curated suggestions), generic model-to-runtime build gate (ModelRuntimeRequirements), per-capability Automatic/Prefer/Force overrides live for text capabilities (media engines do not read them yet), chat media recovery/retry, ChatGPT/Claude/Gemini import, Jan/KoboldCpp/LM Studio/Ollama model linking without copying, Ollama/token-count honesty (BIC-05/BIC-11 PARTIAL), licence/pin/promotion evidence tests (BIC-16a-f), cross-process pin write fix. Competitor reference (Locally Uncensored v3.0.7, README claims only, unverified): gaps still open are ComfyUI workflow import, CivitAI LoRA search, chat branching, prompt rewriting for media, Linux/macOS, music/talking-character video. Gitleaks, GPU, MSI and physical checks remain NOT RUN.

### 2026-10-24 integration batch 3 (verified on this host; NOT release-accepted)

On commit f23bd3eb: release gate PASS (Server 8941 passed, 0 failed, 6 skipped; Android build) and FlaUI Regression 224/224 on the real desktop. Added since batch 2: media-capability Automatic/Prefer/Force overrides in checkpoint selection plus a Model Routing panel section; chat branching ("Branch from here", Duplicate) and an optional reviewed media prompt enhancer (local-only gate before any send); ComfyUI API-workflow import and LoRA search/install (CivitAI host allowlist, consent + hash verified); hash-locked training dependency closure with typed eligibility refusal (the v2 catalog install route /api/v2/training/engine/install is still NOT lock-protected); versioned discovery adapter wired behind PARUX_MODEL_DISCOVERY_ADAPTER_URL; security review findings fixed (URL/class checks on imports, paired-device route exclusions, LoRA name hardening, owner-only reads). Open: Hugging Face LoRA adapter, music/talking-character video, Linux/macOS, GPU/MSI/physical acceptance, gitleaks (tool not installed here).

### 2026-10-25 release 1.9.6 candidate and repository handover (development stopped by owner)

Release 1.9.6 built from source commit 54ec2e26: MSI signed with the owner's self-signed code-signing certificate (thumbprint F73FDA46DD3A0A7DE82C0E479AE1F1E5091416F8; trusted only where the public .cer is imported), signed update envelope and provenance verified by scripts/verify-release-assets.ps1 against the compiled trust anchor (PASS), Android APK 1.9.6 (versionCode 16) signed with the release keystore (PASS), CycloneDX SBOM, gitleaks over the 106 non-merge commits published (no leaks). Release gate PASS (Server 9630 passed, 0 failed) and FlaUI Regression 229/231 (two later fixed or shown instable). NOT validated: clean-PC MSI install and upgrade in a VM (VirtualBox 7.2.22 installed, ISO not downloaded), Qwen-Image/Wan 14B/ACE-Step/LTX workflow validation (media agent stopped; those families stay resolver_only), real-download first-run auto setup, Ollama comparison, quality evaluation. This private repository is being archived; public releases are published from a new repository with the same name (the app update feed points to Ruba94-cyber/RBG-PaRuX-AI-Studio).

### 2026-10-25 integration batch 5 (verified on this host; NOT release-accepted)

On main: release gate PASS (Server 9379 passed, 0 failed, 6 skipped; Android build) and FlaUI Regression 229/229 on the real desktop. Added: BIC-16 unknown-candidate end-to-end fixture, real-process promote/rollback kill tests and one grouped discovery notice with persisted Evaluate/Later/Ignore; BIC-05 compat error matrix across Ollama/Responses/Messages/chat-completions (mid-stream failures end in terminal error events); BIC-11 near-window re-plan with typed 413; BIC-04 free-VRAM re-sampling after Gaming transitions with AMD/Intel reported unknown; BIC-09 declared-file parent continuation active in production; BIC-15 retrieval-runtime re-attestation after Core restart; BIC-10 initial index of a 50k-file synthetic repo 807 s -> 133 s and JS/TS symbol extraction fixed; accessibility sweep over 41 pages with about 50 violations fixed; idle Core memory 264 -> 155 MB private; chat route now honours max_tokens/stop/temperature/top_p/seed (median TTFT 1.18 -> 0.66 s on Qwen3.5-4B Q4_K_M, Radeon 780M, 10 sequential requests; first request after load remains a cold prefill of about 1.4k tokens). Same-machine engine comparison (Qwen3.5-4B Q4_K_M, ctx 8192, identical flags, 3 warm runs, single machine; LU app itself NOT RUN, its bundled engine only): decode tok/s on CPU is 16.1 (PaRuX bundled b10549), 15.8 (LU engine), 15.8 (Unsloth llama.cpp b11724 with common flags); on Vulkan 27.3 (PaRuX bundled) vs 27.0 (LU engine); Unsloth has no Vulkan build. Engines are within about 3% on decode, so any difference comes from launch configuration and features, not the engine; no superiority claim is made. Still NOT RUN: RTX 5080/eGPU/CUDA, MoE expert offload (no MoE model on disk), MSI clean install, first-run auto setup against a real download, LU application workflows, gitleaks (tool not installed), DPI sweep, elevated network capture.

RTX 5080 pass (2026-10-25, evidence in git-ignored `.ai/artifacts/engineering-20261025/gpu/results.md`): three defects fixed test-first - NVIDIA machine with only Vulkan installed could not load any model (303e89ed), llama.cpp catalog empty because upstream flags every release prerelease and CUDA 12.4 (PTX JIT, ~37 s first use) was chosen for Blackwell instead of 13.x (c89d63f8), first request after the GameStop action refused (0d1ad181). Measured through Core on CUDA 13.4 b11871: Qwen3.5-4B Q4 181 tok/s decode, Qwen3.8-27B IQ4_XS 51 tok/s at ctx 8192 (13.5 GB, no OOM), Qwen3-Coder-30B-A3B Q3 181 tok/s, Z-Image-Turbo 1024x1024 2.6-3.3 s warm; Radeon 780M Vulkan 25 / 3.2 tok/s. Identical-flag 4B comparison vs Unsloth b11724, LM Studio 2.41.0, Locally Uncensored (no CUDA) is within noise; no superiority claim. Still NOT RUN: MoE expert offload (model fits), Ollama, quality evaluation.

### 2026-10-25 integration batch 4 (verified on this host; NOT release-accepted)

Release gate PASS (Server 9241 passed, 0 failed, 6 skipped); FlaUI Regression 225/227 on the first full run, the 2 failures (Computer Use consent button id, a UIA timeout in ExplicitDiscovery) fixed in 884b0128 and re-run in the affected classes (60 passed). Real CPU/iGPU evidence on this machine (Qwen3.5-4B Q4_K_M, Radeon 780M, bundled llama.cpp b10549 Vulkan): launch via PaRuX with the new LaunchPlanner in 8 s, real token streaming, TTFT 1.1-1.4 s warm (7.4 s first cold prompt), about 25 tok/s, cancel and kill-recovery verified; MoE expert offload, CUDA, RTX 5080 and eGPU NOT RUN (no MoE model on disk, GPU absent). Added: true token streaming on the default MCP tool-calling chat path; quiescence ignores unowned listeners (typed 503); LaunchPlanner (dense/MoE aware, flags verified against the bundled build's --help); Model Arena + local benchmark history (suggestions can read "measured locally"); incremental semantic indexing bound for ambiguous symbol names (5k-file synthetic repo: 0.27 s vs 4.6-8.5 s); coding-agent benchmark corpus v2 (36 tasks, all fail-before/pass-after verified, competitor runs NOT RUN) ; modality router for audio/tool use/computer use wired server- and App-side (consent dialog, typed refusals); WinML text-only descriptor in routing eligibility; connected-peer ownership check at connect time; update client distinguishes failure states from "up to date"; coding-agent review ledger/scheduler/own-change revert wired into the worker (restart loses revert snapshots; file-set continuation inactive until children declare files); hash-locked training installs (v1 and v2 routes; Python pinned to 3.11). Still open: BIC-03 live competitor runs, BIC-04 live GPU telemetry, BIC-06 DPI sweep, BIC-07 MSI/Authenticode, BIC-13/16 GPU evidence, BIC-15 elevated capture, first-run auto setup against a real download, gitleaks (tool not installed), Linux/macOS, music/talking-character video.

### 2026-10-27 text<->media swap latency (PARTIAL; measured live on one PC on 2026-10-28, see release 2.0.0 section)
- Source changes: ComfyUI cold start now overlaps the media assistant's model load (after the 27B has stopped, so the RAM admission holds); `POST /api/v1/media/prewarm` (Create opened, chat media intent) starts only ComfyUI (Gaming off refuses, busy GPU/RAM/<2 GiB free VRAM refuse, 4 min bound, 10 min idle expiry, honest `starting/warm/skipped/failed/cancelled/expired` state); a text launch after ComfyUI waits (<=8 s) for the driver to release VRAM before llama-server sizes `--fit`; one bounded warm-up completion after a text restore; launch health poll 2 s -> 0.5 s; un-catalogued SDXL-Turbo/Lightning/LCM now sample at 4-6 steps/CFG 1-1.5 instead of 25-28 steps; SDXL base template aligned with its catalogue entry; Qwen-Image fp8 cost documented as inherent (20 GB weights on 16 GB). Timing evidence lands in `%LOCALAPPDATA%\LocalAI\logs\media-swap-timing.log` (stages `media-runtime-start`, `comfyui-launch`, `media-assistant-launch`, `comfyui-vram-release-wait`, `text-restore`, `text-restore-warmup`, `media-prewarm`). Gains, the 26 tok/s cause and the SDXL timing are hypotheses until a live run compares these stages before/after.

### 2026-10-25 launch-level performance (PARTIAL; measured on one RTX 5080 PC, n=3)
- Dedicated-NVIDIA CUDA launches now default to batch/ubatch 2048/1024 (was 512/128: prefill 4B 4564 to 9199 tok/s, 27B 1679 to 2128). "Optimize for this PC" also races the GGUF's embedded MTP head (27B: decode 53 to 86 prose / 120 code tok/s, identical greedy output) and keeps any speculative mode only on >=5% gain with unchanged output. Against Unsloth/LM Studio/LU on the same GGUF PaRuX ties or leads on plain decode and prefill; `--spec-default` = ngram-mod (n_match 24, n 48-64), not MTP. 2026-10-25 follow-up (PARTIAL): calibration now also runs automatically when idle (setting `CalibrateWhenIdle`, Gaming off, no generation 10 min, not on battery, GPU <15%, cancelled by the next prompt, once per model+hardware+runtime build, `measured locally` history; manual button shares the pipeline); KV precision f16/q8_0/q4_0 chosen by VRAM need or >=5% measured speed with a greedy-output quality guard (both 4B and 27B real runs kept f16); speculative candidates are raced on their own one-slot baseline (fixes MTP measuring 11 tok/s on the C4 profile). Real 27B: calibrated default 187.6 code tok/s vs Unsloth shipped 183.1 (tie), but prefill 1918 vs 2246 (quick-tune picked ubatch 256: open). Gemma-4 12B was wrongly refused by Core (KV estimate ignored sliding window and per-layer KV heads): fixed. Evidence: `.ai/artifacts/engineering-20261025/perf2/results.md`. Tool: `tools/perf-launch-bench.py`.

### 2026-10-24 competitive UX, routing, Model Hub and Discovery (NOT release-accepted)

**Scope and evidence boundary:** this is still the ONE canonical roadmap; prior baselines do not certify today's `main`. Target: a genuinely easier, faster and more reliable local-first assistant than Locally Uncensored on equivalent hardware and equivalent tasks, not a feature-count claim.

**Implemented as source-only changes on main:** the Hugging Face discovery provider now resolves explicit repository IDs without first fetching the popular GGUF feed; normal discovery isolates a malformed, missing or oversized individual public repository and refuses to replace a usable catalog with an empty/unusable result. Focused regression tests were added/updated in `ModelDiscoveryServiceTests.cs`. The changes were committed via GitHub, but **.NET build and xUnit have NOT RUN** here. Do not mark them DONE before Windows validation and independent review.

**Next implementation and acceptance priorities (ordered):**
1. **P0 clean installation and real chat:** Windows MSI from an empty user profile; runtime provisioning and ownership; recommended hardware-fitting GGUF quantization; authentic token-stream chat and failure recovery on NVIDIA RTX 5080 16 GB and CPU-only reference. No silent external dependency.
2. **P0 Model Hub identity and search:** real Hugging Face query across exact/relevant repositories, pinned revisions and complete multipart GGUF sets; verified checksum and actual size; license acceptance; tokenizer/projector dependencies; hardware fit, storage estimate, transfer progress/resume/cancel/retry; clear invalid/missing/incompatible model reasons. Curated suggestions and search must never download a different repository or file variant.
3. **P0 honest discovery:** offer popular, recently updated and task-relevant models as separate provenance-labelled cohorts; do not mistake recency/download count for task quality. Catalogs must be bounded and resilient to corrupt entries, offline mode and rate limits; avoid duplicate/obsolete notifications; user ignore/deferral state persists; promotion requires reproducible quality, speed and hardware evidence.
4. **P0 interactive AUTO routing:** classify intent separately from intelligence/performance selection; chat, code, vision, image editing/generation, video and media need correct routes. Respect persistent manual lock, uncensored preference, gaming hard-off and local-first/cloud opt-in. Explain chosen model and exact tradeoffs, no false benchmark numbers; test multilingual and ambiguous requests.
5. **P1 seamless media in chat:** generate/edit/iterate over images and videos inline with local engines; preserve attachments/history and media provenance, real previews, cancellation/retries, device memory recovery and GPU scheduling; explicit sequential generation when asked for multi-step media instead of pretending a single task is a workflow.
6. **P1 user-facing polish:** consistent English-default UX and optional Italian localization; simple workflow first, advanced controls on demand; full keyboard/accessible navigation, understandable recovery messages and no settings rabbit holes.
7. **P1 verification and comparative benchmarks:** same machine/models/quantizations/settings and reproducible prompts, task success, TTFT/tok-s/p95/VRAM/RAM, media latency and quality, cold start, reload and clean setup; FlaUI UIA3, restart and cancellation, offline/security regression. Never assert competitive superiority without this evidence.

**Release gate:** `powershell -ExecutionPolicy Bypass -File tools\\release-gate.ps1 -Restore` plus relevant targeted xUnit tests, physical MSI clean-install, real RTX 5080 media/routing evaluation, independent code review, and required security checks. GitHub Actions remain prohibited by project policy. All unrun checks must be labelled NOT RUN.

### 2026-10-23 main consolidation and clean-install recovery (NOT release-accepted)

The total merge at `5872d643` combined 27 merge commits after `9365ae94` with mechanically resolved conflicts. Recovery PR #9 was integrated into **main** at `a9b7e884`. Subsequent repairs also go directly to **main**; it is the sole canonical product state. No additional integration branch is required. These commits are source changes, not evidence of a green build or accepted release.

| Requirement | Status | Current evidence / remaining work |
| --- | --- | --- |
| Canonical main consolidation | DONE | PR #9 merged; source recovery and subsequent incremental changes are on main without force-push. Existing unmerged experimental branches remain separate and must not be silently imported. |
| Source compilation and XAML | PARTIAL | Duplicate declarations, Settings XAML controls and ComfyUI observers reconciled. Full Windows solution build has not run on this snapshot. |
| Clean installation and runtime acquisition | PARTIAL | WiX MSI stages hash-locked llama.cpp Vulkan and uv; ownership remains explicit and runtime staging uses verified atomic replacement/rollback. New runtime download admission now requires a saved durable job before starting, with failure regressions added. No Windows build, clean-PC install, runtime/model download or crash-recovery acceptance has RUN on this snapshot. |
| First-run onboarding and verified chat | PARTIAL | Explicit consent, safe detection, model-role drift invalidation and actual local chat challenge remain. The first-model handoff preselects a complete quantization; curated recommendation cards now require the exact repository and use the revision-pinned multipart planner with license review, never an unrelated result or single split fragment. New regressions and physical UI/GPU and clean-PC acceptance have NOT RUN. |
| Local-first model routing | PARTIAL | Forced registered candidates, scoped routes and managed GGUF selections retain distinct authority. Focused regression tests added but not executed here. |
| Gallery deletion, file reuse and persistent identity | PARTIAL | File existence and local-path validation, stable gallery identity, deletion confirmation and regression tests restored. Filesystem/UI execution remains unverified. |
| Image/video recovery and model downloads | PARTIAL | Job observers, stored clip plans, video resume, bounded cancellation, digest/download admission and focused tests reconciled. Native GPU, restart, failure/recovery and cancellation acceptance remain unrun. |
| Exhaustive merge comparison and local-only old branches | PARTIAL | High-risk changes reviewed. Full two-parent comparison remains. Missing historical local-only refs cannot be cherry-picked safely. |
| Windows/Android build, full Server/FlaUI tests and release gates | BLOCKED-EXTERNAL | No target Windows/.NET UI/Android test toolchain accessible in this session. Repository forbids GitHub Actions. Build, `tools/release-gate.ps1 -Restore`, `tools/validate-software-complete.ps1` and physical PC/phone checks must run locally. |
| Tracked-tree/history gitleaks and predecessor-brand guard | BLOCKED-EXTERNAL | Focused changed-file secret-pattern checks do not replace complete tracked-tree/history gitleaks and predecessor-brand scans. No pass may be claimed for the full tree. |
| User experience versus Locally Uncensored | PARTIAL | English is the default source/UI/documentation language, Italian is optional for future localization. Validate a truly fresh setup, clear Simple/Advanced disclosure, real local inference, model/hardware fit, Chat/Create/Code workflows and recoverable failures before any superiority claim. |

**NO-GO FOR RELEASE.** Main is up to date as a development baseline; merging source fixes is not release acceptance. On a Windows development PC, complete a fresh installation and runtime/model lifecycle test, full solution build, focused and complete Server tests, FlaUI UIA3, Android builds where supported, signed packaging tests, authoritative release gates, complete gitleaks and predecessor-brand scans, and real GPU/Gaming hard-off/auth/pairing/LAN/backup regression checks. Never silently mutate USER_MANAGED software or claim a successful test that was not executed.

The Windows reference product has a substantial implemented baseline, but the 2026-09-10 source review reopens concrete correctness, compatibility, usability, delivery and performance-validation work. The BIC-01 through BIC-17 ledger below supersedes the earlier blanket `SOURCE_SOFTWARE_COMPLETE` closeout and the claim that physical acceptance is the only remaining work.

Complete the actionable software gaps and the applicable acceptance evidence before closing scope. Final acceptance on the target **Windows PC** remains required in addition to this work. A defect discovered during that run is a source bug to fix, not an environment limitation; missing credentials or unavailable hardware apply only to their specific external validation/publication steps.

**2026-10-02 public-release preparation:** the sanitized `main` snapshot is authoritative; no pre-cleanup history, deleted archives, tags or releases were restored. Candidate metadata is aligned to **1.9.5** (Android code **15**). Obsolete internal agent prompts, archive references and historical release links were removed; security support and the mandatory signed-update-envelope release flow were clarified. The auto-role test now isolates both model and data roots and restores prior state; UI tests pin a fixed supported `System.Drawing.Common` dependency. Independent diff review: PASS. Full local `tools/release-gate.ps1 -Restore`: PASS without skips, Core **3,877/3,877**, VS Code **35/35**, Android JVM **33/33 Debug + 33/33 Release**, lint **0 errors / 31 warnings**, Release x64 build and all five self-contained Windows publishes. NuGet audit reports no vulnerable packages; current tracked-tree and sanitized-history secret scans report no leaks. The maintainer manifest key matches the embedded public trust anchor and the Android signing key was verified without committing private material. Evidence is retained locally under `.ai/artifacts/public-release-20261002/`. **This is candidate preparation, not release acceptance: 14 BIC rows remain open and physical-PC acceptance remains required. No tag or GitHub Release is authorized by this software-gate result alone.**

**2026-10-01 continuation verification (BIC-05, BIC-09, BIC-11, BIC-12, BIC-15 closure advances, Dynamic Model Advisor, Auto-Role Setup & Release Gate PASS):**
- **Dynamic Model Advisor & One-Click Role Auto-Assignment (Model Hub & Onboarding)**:
  - Enhanced `FirstModelAdvisor` to evaluate models physically present on disk in `%LOCALAPPDATA%\PaRuX\models` at execution time rather than solely relying on static seed candidates. Dynamically infers capability tags (Coding, Reasoning, Vision, GeneralChat) and content profiles (Uncensored, Standard), evaluates hardware suitability against discrete VRAM and system RAM, boosts local model rank (+1000) so existing models take precedence, and outputs dynamic headlines announcing immediate local readiness.
  - Implemented `ModelRoleAutoAssigner` and endpoint `POST /api/v1/models/roles/auto-assign` mapping local models to all 8 system roles (`standard`, `coding`, `reasoning`, `planner`, `reviewer`, `vision`, `uncensored`, `fast`) with clear reasons, hardware checks, and thread-safe persistence via `LocalAiPaths.SetRoleOverride`. Added client method `ModelDownloadClient.AutoAssignRolesAsync`.
  - Verified with `FirstModelAdvisorTests` (26/26 PASS) covering rank boost, headline notification, capability inference, and role auto-assignment under isolated root test scaffolding. Commit `2d2ee87`. Independent review PASS.
- **BIC-09 (WindowsCodingSandbox AppContainer & Tooling Fix)**: resolved STATUS_DLL_INIT_FAILED (0xC0000142) when running Node.js v24 and Python C-extensions under AppContainer sandboxes. Added EnsureWindowStationAndDesktopAppContainerAccess to grant S-1-15-2-1 (ALL APPLICATION PACKAGES) GENERIC_ALL on the interactive WindowStation (WinSta0) and Desktop (Default) objects once per process lifetime. Added symlink target resolution via Directory.ResolveLinkTarget so junctioned/symlinked tool paths are properly granted AppContainer read rights. Preserved AppPaths.PaRuXRoot test data root isolation in NodeOptionsForSandbox and BuildSanitizedEnvironment. Commits 1ff1918 and 9e845ab. Verified with WindowsCodingSandboxIntegrationTests (6/6 PASS, 4m 21s) and WindowsCodingSandboxPolicyTests (66/66 PASS, total 78/78 PASS). Independent review PASS.
- **BIC-05 (Ollama Compatibility Extensions)**: implemented GET /api/ps, POST /api/embeddings, and POST /api/embed in OllamaCompatibilityEndpoints.cs. Exposes running models via HotModelPoolCatalog or active model file without host credential leakage. Routes registered in ProviderSurface.cs exact paths with capability contract flags. Verified with ApiKeyServiceTests (21/21 PASS), OllamaCompatibilitySemanticTests (23/23 PASS), and OllamaCompatibilityIntegrationTests (20/20 PASS). Independent review PASS.
- **BIC-11 (Tokenizer Identity & Hot Pool Isolation)**: implemented TryGetPathForAlias and GetActiveEntries in HotModelPoolCatalog.cs. Updated RuntimeTokenCounter.ResolveRuntimeTokenizerIdentity to isolate tokenizer identities per model path/alias in multi-model hot pools and explicit model routes, avoiding fallback to primary model active file. Added cache invalidation via RuntimeTokenCounter.ClearCache. Verified with ManagedTokenizerIdentityTests (8/8 PASS). Independent review PASS.
- **BIC-15 (Dual-Stack Listener Ownership & SQLite Pool Isolation)**: in ManagedListenerOwnership.cs, added AfInet6 (23) and Tcp6RowOwnerPid marshaling struct (56 bytes, exact MIB_TCP6ROW_OWNER_PID layout). FindListenerOwners now queries both IPv4 and IPv6 tables via GetExtendedTcpTable to verify strict ownership against foreign listeners and dual-stack prompt hijacking. In SqlitePoolIsolation.cs, added Mode=ReadWrite;Cache=Shared and Mode=ReadWrite variants to ConnectionStringsFor. Verified with ManagedListenerOwnershipTests (9/9 PASS) and SqlitePoolIsolationTests (2/2 PASS). Independent review PASS.
- **BIC-12 (Native Workbook Formula Compatibility & Exponentiation)**: in WorkbookFormulaEvaluator.cs, added exponentiation operator ^ with correct Excel precedence and left-associativity. Expanded multi-cell functions to include COUNT and PRODUCT. Added math and rounding functions: ABS, SQRT (non-negative domain check), INT (Excel floor rounding), SIGN, POWER, MOD (Excel formula semantics n - d * floor(n/d)), ROUND (AwayFromZero with positive and negative decimal places), ROUNDUP, ROUNDDOWN / TRUNC, LN (positive domain check), EXP, and nullary PI(). Enforced shape validation rejecting ranges in scalar functions, and added non-finite (NaN/Infinity) traps. Verified with WorkbookNativeV2Tests (13/13 PASS) and WorkbookPatchStylePreserverTests (3/3 PASS), total 19/19 PASS. Independent review PASS.
- **BIC-14 (Mobile/Companion Pairing Security & Panic Revocation)**: added atomic panic revocation via `RevokeAll()` and pairing status check `IsDevicePaired(string id)` in `RemoteAccessService.cs`. Mapped loopback-protected `DELETE /api/v1/remote/devices` in `Program.cs`. Created comprehensive test suite in `RemoteAccessServiceTests.cs` (9/9 PASS) verifying private IPv4 detection, pairing enablement guard, invalid/expired code rejection, name validation bounds, brute-force rate-limiting, token issuance/SHA-256 hashing, timing-safe validation, single-device revocation, bulk panic revocation, and offline imported-device validation. Commit `e31160d`. Independent review PASS.
- **Canonical Release Gate Executed (tools/release-gate.ps1 -Restore)**: **FULL PASS** on all software/host criteria:
  - Core regression suite: **3,865 / 3,865 PASS**, 0 failed, 0 ignored (12 m 24 s)
  - Full Windows x64 solution build with WinUI surfaces: **0 errors**
  - Shipped Windows projects published self-contained: App, Server, Cli, UpdateHelper, WinMlWorker all PASS
  - VS Code V2 integration and editor contract tests: PASS (2/2)
  - Android companion JVM tests, lint, debug APK and release APK build: **BUILD SUCCESSFUL in 2m 36s (105 actionable tasks)**
  - Competitor verdict and scope audit self-tests: PASS
  - Local release asset verifier: PASS (7/7 scenarios)
- **Audits & Preservations**: Gitleaks v8.30.1 scan over 1,224 commits (0 leaks), git diff --check clean, user SettingsView.xaml preserved byte-for-byte in .ai/artifacts/user-settings-backup.xaml (SHA256: 652C16411BEB22CB8A5ACAC23E8A19580632738F987566B9F604915D7D64F583).
- **Publication Gate**: Stays **NO-GO** due to mandatory external evidence gates (trusted release signature/envelope, clean-PC MSI installation, physical Android phone test, elevated network capture) and remaining BIC software items.

**2026-09-20 acceptance addition:** before any renewed “best in class” claim, rerun the
same-machine competitor benchmark matrix against the current PaRuX binary. Use equivalent
model/revision/quantization, warm and cold runs reported separately, identical context/output
limits, correctness scoring, TTFT, sustained decode throughput, cancellation/recovery,
long-context, concurrency, coding-agent task quality and comparable user-visible UX flows. An
unavailable metric or competitor is `INCONCLUSIVE`, never a synthetic win. Independently, exercise
the Simple Chat coding workflow on a deliberately selected external target only through PaRuX's UI:
the resulting PaRuX worktree/diff, tests, diagnostics and user adoption path must be verified before
claiming that PaRuX can safely repair a downstream project. Do not directly edit that target from
this repository or from the PaRuX development shell.

**2026-09-21 real end-to-end acceptance run (GPU available). Build/launch/GPU/UI pass; the coding
agent on an external target FAILS; `BEST_IN_CLASS` still not claimed.** Everything below was
observed directly on this machine (RTX 5080, 16 GB, driver 610.88). Full evidence:
`artifacts/validation/acceptance-20260921/` (untracked).

*Binary under test.* Release x64 build of the **current working tree** (HEAD `37b1d8c` plus the
uncommitted changes committed alongside this entry), 0 errors / 6 warnings (8× `CS9057`,
4× `CS8602` — down from the 2026-09-15 inventory's 10× `CS8602` + 2× `CS8604`). Self-contained
App+Core published to `artifacts/publish/win-x64` with Core nested at `PaRuX.Server\` as
`PaRuXServerController.FindServerExecutable` requires. `PaRuX.exe` SHA-256
`815C7CEECB4576A6DD68F9C5F0AEA44A5F99AA5D0A2874EA22ADCEDF02700149` (348,160 B), `PaRuX.Server.exe`
`5B5BD83977EB69722E0EAB300D792A08E7105518B09C1702A41AB6A0C6319408` (152,064 B), both reporting
`1.9.3+37b1d8c…`. Note the informational version names HEAD only; the binary also contains the
then-uncommitted working-tree changes.

*Launch and GPU — DONE.* Launched for real; App and Core alive (Core spawned from the published
nested path, verified by `Win32_Process.ExecutablePath`). `%LOCALAPPDATA%\PaRuX\parux.log` shows
**no `UNHANDLED` and no exception** across the whole session; the only error-level line is
`RegisterHotKey failed for 'Ctrl+Alt+Space' (error 1409)`, which Settings → General surfaces
honestly to the user. `GET /api/v1/runtime/status` reports `gpuDetected: true`,
`vramTotalMib: 16303`, adapter `NVIDIA GeForce RTX 5080` (16,755,195,904 B dedicated, source
`dxgi`), backend `text-cuda` installed/running/healthy. Real GPU inference confirmed: llama.cpp
`b10549-cuda12.4`, `--gpu-layers all`, 15.6/16.3 GB VRAM resident, 96% utilization at 302 W.

*Default-path tool streaming — DONE (2026-10-25).* With MCP tools advertised the default chat path no longer buffers: `StreamingMcpToolLoop` streams text/reasoning deltas, accumulates tool_call deltas, runs tools and streams each next turn. Qwen3.5-4B Q4 / Radeon 780M through Core: 146-299 chunks per answer at ~25 chunks/s, warm TTFT 1.1-1.4 s (was 1 chunk after the full generation); real tool round (Word-document tool) streamed after the tool result. Refusal escalation never applied on the tool path and still does not (documented trade-off).

*Real streaming inference — DONE.* Chat produced real streamed markdown answers on
Qwen3.8-27B-Uncensored IQ4_XS. Measured through Core: TTFT 269–330 ms, ~50 tok/s sustained over
three 500-token runs. Cross-checked against llama.cpp's own `timings` (non-proxy):
**50.65 decode tok/s**, 400 predicted tokens in 7,876.9 ms, prompt 104.1 tok/s — the SSE-chunk
measurement agreed within 1.5%.

*UI smoke — DONE with one real defect.* Chat (home), Create, Projects, Model Hub, Runtime and
Settings (General / Models / Chat Behavior / Your PC apps) all rendered live with real data, no
blank screens and no crash. STOP during generation worked and the reply was marked `Stopped.`;
a new message immediately afterwards completed cleanly. **BIC-06 defect found:** at reduced window
width in Advanced layout the Chat header truncates — the title wraps to "Cha"/"t" and the model
label "Qwen 27B · Uncensored" is clipped to "Qwe" with no ellipsis, because the Auto/Standard/
Uncensored profile pills keep their width while the title does not. Correct again at full width.
(Also observed: raw `$$ \text{…} $$` LaTeX renders as literal text in chat markdown.)

*Coding agent on an external target — MISSING.* Target `an explicitly selected external .NET repository`
(real .NET repo, 9 projects, 83 `.cs`), driven only through Simple Chat `/code` with an explicit
quoted path; never edited from this repository or shell. **Isolation is verified and is stronger
than documented**: PaRuX does not create a worktree inside the target at all — it branches inside a
shadow copy under `%LOCALAPPDATA%\PaRuX\coding-agent-state\shadow-projects\`. Target HEAD, `git
status` and `git worktree list` were byte-identical before and after; no `.parux-agents\` appeared
in it. **But the capability failed.** Run `0f909825`: 82 steps, 2 repair attempts, ~35 min,
terminal `status=failed` / `verificationStatus=fail`, diff `(no working-tree changes)`, target
`dotnet build`/`dotnet test` never reached. The independent verifier rejected it verbatim: *"The
goal required finding a defect, implementing a fix, adding a test, and running build/tests. The
result reports no code changes, no tests, and no build/test execution."* **No fake success** — that
contract held. A second run reproduced the same loop and was cancelled from the UI; cancellation was
immediate and clean.

Root cause, measured: the agent never issued an edit action because it re-read the same 154-line
file until its own repeat guard and exploration budget blocked every remaining read. The managed
runtime was launched `--ctx-size 32768 --parallel 4`; llama.cpp splits context across slots, so
`/props` reported `total_slots: 4`, `default_generation_settings.n_ctx: 8192`.
`CodingContextBudgetResolver` correctly probes that field, observed 8192, and set the repository
evidence budget to **9,338 characters against the Execution Profile's declared 96,000**. The
benchmark leg independently reproduced the same arithmetic on the 4B profile
(`--ctx-size 16384 --parallel 2` → `total_slots: 2`, `n_ctx: 8192`), so **the usable per-request
window lands at 8192 regardless of the physical context the user selects**. The resolver, repeat
guard, watchdog, bounded retries and verifier all behaved as designed; the launch configuration is
the defect. The target itself was healthy and not the obstacle: an out-of-tree copy builds with
**0 warnings / 0 errors** and **56/56 tests green in 950 ms**.

*Server test gate — 3021/3024, then 3/3 green on retry.* The full suite run reported 3 failures,
all three `ParuxOverheadBenchmarkTests` aborting in `InitializeAsync` with *"Port 1234 is already in
use"* — the suite's own refusal to benchmark an unknown listener, left over from the competitor
benchmark teardown, not a product failure. Re-run in isolation with the port free: **3/3 passed**.
No single clean 3024/3024 run was produced this session and none is claimed.

*Same-machine competitor matrix — `BEST_IN_CLASS` NOT claimed.* Qwen3.5-4B-Q4_K_M, fixed prompt,
400-token budget, all three stacks verified resident on the RTX 5080. PaRuX and LM Studio ran
bit-identical GGUFs (SHA-256 `00FE7986FF…`); **Ollama's blob hash differs entirely
(`D1F31E92…`)**, so same-weights identity is *not provable* for Ollama and its numbers are labelled
accordingly. LM Studio's installed CUDA llama.cpp runtime (2.31.2) **hung on this GPU** (stuck at
95%, ~260 MiB, 0% utilization — consistent with no Blackwell/sm_120 kernels) and it ran on Vulkan
instead, so its engine is not the same code path.

- PaRuX raw managed engine (deterministic, spec-exact): warm median TTFT **28.4 ms**, p95 220.7 ms,
  **178.91 decode tok/s** exact.
- PaRuX Core (product path): warm median TTFT **111.9 ms**, p95 377.5 ms, 184.62 tok/s — i.e. the
  Core layer costs **+83.5 ms median TTFT** over hitting the managed engine directly. Cold TTFT
  **21.2 s** (~28.6 s from load trigger to first visible token), dominated by first-run CUDA
  warm-up after a 7.4 s load call. Both are real weaknesses.
- Ollama: exact backend-reported eval throughput **173–175 tok/s**; TTFT INCONCLUSIVE.
- LM Studio (Vulkan): **no timing data exposed at all** — INCONCLUSIVE for throughput and TTFT.

**Throughput has no winner**: 173–186 tok/s for every stack that reported a number, inside n=5
noise. The one clear PaRuX win is *correctness under this exact request shape*: PaRuX produced a
complete coherent answer on every cold and warm run, while **Ollama and LM Studio returned zero
visible characters on all 12 of their runs**, spending the whole 400-token budget on hidden
reasoning. That is a defaults/ergonomics win (PaRuX's chat template auto-closes `<think>`), not a
speed win, and it is workload-dependent: a diagnostic `think:false` call made Ollama answer
normally, whereas LM Studio ignored `chat_template_kwargs.enable_thinking`. Long-context,
concurrency, coding-agent task quality and competitor UX matrices remain unrun.

Net effect on status: BIC rows stay `PARTIAL`; the coding-agent-on-external-target row is `MISSING`
with a named root cause; status stays `PARTIAL` / `PHYSICAL_PC_ACCEPTANCE_REQUIRED`.

**Next actionable gap (highest priority):** stop the coding role from being starved by slot
splitting — either launch its runtime with `--parallel 1`, or size `--ctx-size` as
`perRequestContext × parallel`, and make the Execution Profile's declared context/evidence budget
reconcile with the runtime's real per-slot `n_ctx` instead of silently disagreeing by 8×. Then
re-run this same external-target task and require a real diff plus a green `dotnet build`/
`dotnet test` on the target before the row moves off `MISSING`.

**2026-09-21 (same session) — the three defects above are fixed, with tests; full gate green at
3,050/3,050.** Evidence in `artifacts/validation/acceptance-20260921/`.

*1. Context starvation — FIXED and verified on the running product.* `--ctx-size` is a TOTAL that
llama.cpp divides across slots, but `LlamaPerformanceProfileDto.ContextSize` carries the user's
PER-INFERENCE choice when it comes from `ContextPreferenceStore` (Runtime Manager sells that
control as "tokens the model can attend to simultaneously in one inference"). The two were
conflated, so the control was inert: picking 32k gave 32,768 total across 4 slots — still 8,192 per
request. `RuntimePerformanceTuningContracts` now carries `ContextIsPerRequest`, set only by
`ContextPreferenceLaunch` when a stored preference exists; `RuntimeLaunchOptimizer
.ResolveContextAndSlots` honors a per-request window by *reducing slots*, never by enlarging the KV
cache, so the total stays inside the envelope the previous expression already committed to and a
launch that fitted before still fits. An adaptive context remains a total and keeps its old
divide-across-slots behavior, which is why the iGPU auto-slot contract in
`RuntimeLaunchOptimizerParallelTests` still holds. `RecommendParallelSlots` now returns 1 for
`coding` as it always has for `long-context`: one long-horizon agent gains nothing from extra slots
and loses a proportional slice of its window to each. Slot/context arithmetic is also clamped to
the same 1–8 range as the emitted argument, which previously disagreed (a stored profile asking for
999 slots launched `--parallel 8` against a `--ctx-size` sized for 999).

Verified live, not just in tests: with a 32k preference stored the coding model now launches
`--ctx-size 32768 --parallel 1` and `/props` reports `default_generation_settings.n_ctx 32768`
(was 8192), and the agent logged **"Observed active model context: 32768 tokens; repository
evidence budget 37355 characters"** against the previous 8192 / 9,338 — a 4× window at the same KV
budget. With no stored preference the shipped default is byte-identical to before
(`--ctx-size 16384 --parallel 2` on the 4B). New coverage: `RuntimePerRequestContextTests`.

*2. A one-off load silently rewrote the user's default model — FIXED.* `RuntimeManagerService
.LoadRoleAsync` called `LocalAiPaths.SetRoleOverride` unconditionally, so *loading* a model into a
role *was* choosing that role's model forever. A benchmark loaded a 4B under role `standard`
purely to measure it, and from then on this machine — an RTX 5080 with two 27B models — warmed the
4B as its Standard text profile on every launch, with nothing in the UI indicating the default had
been rewritten. `LoadManagedModelRequest.PersistRoleAssignment` now gates that write and defaults
to **false**, so a transient load stays transient; Settings' "Load `<model>` as `<role>`" button
passes `true`, because there the user is choosing on purpose. Existing API bodies that omit the
field are transient by construction. New coverage: `LoadDoesNotReassignRoleTests`. The stray
override was removed and `standard` is back on `Qwen3.8-27B-UD-IQ4_XS`.

*3. Every coding task died at step 0 while a hot model pool was resident — FIXED.* A resident pool
makes llama-server expose **only** its hashed per-entry aliases, so the logical id `local-default`
is not a model it knows. With two models resident the agent's first structured request came back as
`HTTP 400 {"error":{"code":400,"message":"model 'local-default' not found"}}` and the task failed
before reading a single file. `HotModelPoolCatalog.TryGetDefaultAlias` now exposes the pool's own
default (manual pin first, else the first resident entry), and both `ChatProxyService
.ResolveProviderModel` and `Orchestration/LlamaServerCompletionClient` fall back to it instead of
an id that is only valid for a single-model launch. New coverage: `HotPoolDefaultModelTests`.

*Gate after the fixes:* Release x64 build 0 errors; **3,050/3,050 server tests passed (11 m 39 s)**
with the desktop app closed. Note for future runs: the three `ParuxOverheadBenchmarkTests` abort
with *"Port 1234 is already in use"* whenever PaRuX is running, because the app re-warms a model
during the suite — the gate requires the app closed, and the earlier 3,021/3,024 and 3,041/3,044
results in this session were that precondition, not product failures. A full rebuild also reports
**112 warnings**, not the 6 an incremental build shows (70 `CA1416`, 32 `CS8602`, 20 `CS9057`,
4 `MCP9007`) — the 2026-09-15 entry's suspicion about incremental builds hiding warnings was
correct.

*Hardware reality the fix exposes, stated plainly.* On this 16 GB card the 27B dense at IQ4_XS
(~13.6 GB of weights) leaves no room for a real KV cache in VRAM: PaRuX honestly reports that both
16k and 32k push the KV cache to system RAM, and measured step latency for the coding agent then
runs to minutes per step. The window is no longer silently divided, but "strong model + large
window + fast steps" does not fit in 16 GB simultaneously. Choosing between a smaller quantization
of the 27B, a different coding model, or accepting system-RAM KV is a product decision that needs
the maintainer, not a code fix.

*Status unchanged where it must be:* the coding-agent-on-external-target row stays `MISSING` until
a run produces a real diff plus a green `dotnet build`/`dotnet test` on the target. BIC rows stay
`PARTIAL`; `BEST_IN_CLASS` remains unclaimed.

**2026-09-21/22 (same session) — the coding agent had no Virtual Context at all; it now has
task-scoped durable memory, and the blocker has moved from "never edits" to "the patch tool
rejects the edit".** Evidence in `artifacts/validation/acceptance-20260921/`.

*Diagnostic first — the loop is not caused by open-ended discovery.* A fully **directed** task
("in this named file, change these two dictionary keys to include `IsExtended`, add this one test,
run build and tests") was run against the same external target on the pre-fix binary. It looped
identically: 15 steps, repeat-blocks at steps 5 and 6, no edit. So the failure was never the
agent's inability to choose work.

*The architectural gap.* `ContextEngineService.BuildVirtualContextCandidates` is only ever called
with scope `conversation`, and `CodingAgentService` never referenced Virtual Context at all — the
coding agent ran on the raw physical window while the product advertises 128k/256k/1M logical
histories. Worse, its working memory was a hard-coded `MaxRecentEvidenceCharsInPrompt = 2_500`
while a single observation was allowed 4,000 and one artifact read of the 154-line target file
returned **5,930** characters. Not one complete read survived into the next step, at any window
size — which is exactly why the verified 4× physical-context fix above changed nothing for the
agent.

*Implemented.* `CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters` scales working memory with
the observed context (a quarter of the usable characters, floored at the old 2,500, capped at
48,000). New `CodingTaskMemoryClient` gives every coding task its own Virtual Context ledger scope
(`coding-task:<taskId>`) over Core's existing `/api/v2/virtual-context/ledger` endpoints: each
executed tool outcome is recorded and `BuildPromptText` is injected into the next prompt as
"Durable task memory". Entries are written as **constraints**, not notes — the ledger's prompt
projection renders only contradictions, constraints and decisions, so notes would have been stored
and never shown; a test now pins that. Plan changes are recorded as **decisions**. Memory is
best-effort by construction: a ledger failure degrades recall and can never fail a coding task.
New coverage: `CodingAgentRecentEvidenceBudgetTests`, `CodingTaskMemoryTests`.

*Measured result, stated exactly.* With the fixes and a 32,768-token window the same directed task
reached **`Step 7: replace_range`** — the first repository mutation the agent has attempted in this
entire acceptance effort, where every previous run reached its step limit without one. The ledger
was live throughout (7 entries by step 8, 21 by step 33, 15,999 characters of rendered prompt
memory). The edit was then **rejected by the patch tool**: `oldText was not found - the file may
have changed since it was last read, or the text doesn't match exactly (including whitespace)`.
That is now the next blocker, and it is a different problem from the one that was fixed.

*The window/speed wall is real and was measured from both sides.* At 32,768 tokens the 27B's KV
cache does not fit in 16 GB of VRAM, PaRuX says so honestly, and steps cost 2–10 minutes each. At
8,192 tokens the model is fast and fully resident, but the prompt now also carries durable memory
and the agent exhausted its exploration budget and then returned empty responses
(`invalid action protocol — missing action`) — the window is too tight for this prompt design.
Durable memory reduces the need for a huge window but does not eliminate the need for headroom.

*Still `MISSING`.* No run has produced a real diff plus a green `dotnet build`/`dotnet test` on the
target. Next actionable gap, in order: (1) make patch application tolerant of exact-match failure —
anchored/whitespace-insensitive matching, or returning the nearest candidate text in the error so
the model can correct itself, since the model reliably gets the intent right and the literal wrong;
(2) shrink the agent's own system prompt and repository-evidence share so an 8k-resident
configuration has room to answer, which is what makes durable memory pay off on 16 GB hardware.

**2026-09-08 note:** at the maintainer's explicit direction, v1.7.0 was tagged and released from the
earlier software-complete closeout state despite the physical-PC acceptance matrix not being fully complete (see the dated entry below
for exactly what is and is not verified). This is a deliberate maintainer call to ship, not a
reclassification of the open rows — they remain real, open gaps and this status line is not being
set to `REFERENCE_VERIFIED`.

**2026-09-15 local validation update:** `main` was fast-forwarded to the expected `origin/main`
(`2d1a5c43a3b2781b9a54ba9539d3cb915ca72610`) before work began. The canonical gate passed
at `d523d9b`: 2,921/2,921 server tests, 28/28 VS Code tests, Release x64 build and self-contained
App/Core/CLI/UpdateHelper/WinML publishes. The gate now uses a minimal console logger plus a durable
TRX so verbose ASP.NET request logging cannot perturb wall-clock performance tests or hide their
failure details. A complete commit-linked WinUI layout was exercised through the real UI; every
workspace and Settings subsection rendered without a crash, and Chat send, STOP and immediate
recovery passed. Same-machine PaRuX/LM Studio/Ollama measurements now exist for identical 4B
weights and quantization, including a successful integrated sustained Backend Race. These results
close important evidence gaps but do not certify an absolute best-in-class winner: Ollama remains
about 4.3% faster in the separate standardized short streaming run, while PaRuX wins its integrated
optimized workload and beats LM Studio in that standardized throughput run. All 15 active BIC rows
therefore remain `PARTIAL`; broader task-quality, long-context, concurrency and competitor UX
matrices plus the remaining applicable physical-PC rows are still required. Physical Android
hardware was not used. No GitHub Actions workflow was added or enabled.

**2026-09-15 (continued) gate reconfirmed at `dc31318`, fair warm Backend Race replaces the earlier
unfair one, UI/UX left honestly unverified on this exact binary:** an in-progress
`validate-software-complete.ps1 -SkipRestore` run observed at session start (PID 7924) died silently
between checks with no TRX and no crash evidence in the Windows Application event log — treated as
"no conclusive result", not a pass, per the plan's own contingency rule. Relaunched in isolation with
full output capture after confirming standard pre-gate cleanliness (no stray `PaRuX.exe` /
`PaRuX.Server.exe` / `llama-server.exe`, port 1234 clear, `mode.txt` already `AUTO`). The rerun
passed cleanly: **2,922/2,922 server tests** (7 m 2 s), **28/28 VS Code tests**, Release x64 build
with 0 compiler errors, and self-contained App/Core/CLI/UpdateHelper/WinML publishes, all at
`dc31318c27848aebdf146826eecfc351082de575`.

A complete `uiux-dc31318` layout was assembled directly from that gate's own same-commit
App/Core publishes (`artifacts/validation/software-complete/{app,core}-win-x64` copied into
`artifacts/validation/uiux-dc31318/{app-publish,core-publish,layout}`, with Core nested under
`layout/PaRuX.Server`). Both `PaRuX.exe` and `PaRuX.Server.exe` report product version
`1.8.0+dc31318c27848aebdf146826eecfc351082de575`. SHA-256: App
`DE693F7A067E91AEA582BD1BBA885311A243AFA8E3120C84333298093D5CD9C4` (348,160 bytes), Core
`DD80CB5B2DAFE4B85078E69254BC13FE40DEE4BC62260CF54EAD5F2E5875F918` (152,064 bytes). Process identity
was verified via `Win32_Process.ExecutablePath` before use.

**Fair warm Backend Race, real result, replaces the earlier unfair one:** managed model hash
verified (`Qwen3.5-4B-Q4_K_M.gguf`, SHA-256 `00FE7986FF5F6B463E62455821146049DB6F9313603938A70800D1FB69EF11A4`,
matching the plan's recorded identity), Ollama's `qwen35-4b-fair-compare:latest` confirmed resident-free
before the run (`ollama ps` empty), LM Studio confirmed not running, zero `llama-server.exe`/port 1234
activity before the call. Called `POST /api/v1/runtime/performance/backend-race` on the `dc31318`
Core (`managedModelPath`, `workload: chat`, `externalEndpoint: http://127.0.0.1:11434`,
`externalModel: qwen35-4b-fair-compare:latest`, `sameWeightsConfirmed: true`,
`sustainedValidation: true`) with the dc31318 external-warm-up fix in effect. Real result
(`comparable: true`):

- PaRuX llama.cpp (isolated managed probe): 176.83 decode tok/s, 158.98 ms TTFT, 177.31 sustained
  tok/s, 3,818.7 prompt tok/s, score 0.979;
- Ollama (`qwen35-4b-fair-compare:latest`, USER_MANAGED, now warmed instead of cold-load-penalized):
  163.09 decode tok/s, 142.47 ms TTFT, 154.40 sustained tok/s, prompt throughput not reported by the
  external backend, score 0.933;
- Winner: PaRuX llama.cpp, on this integrated workload.

This **replaces** the earlier unfair integrated figures (PaRuX 177.44 / Ollama 17.44 initial /
120.19 sustained), which double-counted Ollama's cold model load into its wall-clock and is no
longer a valid comparison. The **separate standardized streaming benchmark stays as previously
recorded** (PaRuX 162.43 / LM Studio 140.72 / Ollama 169.45 tok/s end-to-end; Ollama ~4.3% ahead of
PaRuX there, PaRuX ~15.4% ahead of LM Studio) — a different workload/harness, not superseded by this
integrated race. Ollama's real advantage in TTFT (142–143 ms vs PaRuX's ~159 ms) is consistent
across both benchmarks and is not manipulated away here.

**Build warnings inventoried** (Release build, 0 errors): 8× `CS9057` (installed
`Microsoft.CodeAnalysis.Analyzers` 4.12 built against a newer Roslyn than the running 4.11 compiler —
cosmetic, no runtime effect), 10× `CS8602` + 2× `CS8604` nullability warnings concentrated in
`WorkbookPatchStylePreserver.cs`, `StructuredWorkbookWriter.cs`, `RuntimeManagerView.ContextWindow.cs`
and `CodingAgentService.PersistEvidence` (real latent null-dereference risk in those specific paths,
not proven to fire in this session — worth a follow-up pass, not a mechanical blanket rewrite), 2×
`MCP9007` (`AuthorizationRedirectDelegate` obsolete, compatibility-only per the SDK's own migration
note, no current functional risk). No xUnit-analyzer warning was observed in this `-SkipRestore` run;
that is not independently confirmed absent, since an incremental build can skip recompiling
already-current files.

**UI/UX on this exact binary: not independently verified this session, reported honestly rather than
reused.** A `ui-verifier` pass was launched to load the Qwen3.8-27B Standard profile on the new
`dc31318` layout, run the arithmetic-chat/Runtime/Settings/screenshot/log-scan smoke sequence from
this plan's acceptance checklist, and report real evidence. Its `request_access` for `PaRuX.exe` was
**denied** at the live OS permission dialog on two separate attempts (once before, once after the
operator said to retry in chat — an in-chat approval does not itself grant a live system permission
dialog, which needs a direct click when it appears). The operator then chose to finalize without a
fresh UI run rather than keep retrying blindly. Consequently: `PaRuX.exe` never launched this
session; no new `UNHANDLED` log entries exist in `%LOCALAPPDATA%\PaRuX\parux.log` only because no
session ran, which is an absence of evidence, not a passing UI check. The last real, live UI
acceptance evidence for this product remains the `b3a9561`/`d523d9b` passes recorded above (Chat
send/STOP/recovery, all workspaces and Settings subsections reachable without a crash) — real, but
**not re-confirmed on the `dc31318` binary**, and BIC-06's WinUI navigation row and the general UI/UX
acceptance row stay `PARTIAL` until that binary is actually driven end-to-end. `PaRuX.Server.exe`
(Core) from the correct `uiux-dc31318\layout\PaRuX.Server\` path did run standalone throughout this
session (verified serving `127.0.0.1:51123`/`::1:51123` by PID and executable path) to support the
Backend Race API call; that is Core-only evidence, not a WinUI acceptance run.

`BEST_IN_CLASS` remains explicitly **not claimed**: Ollama is faster in the standardized streaming
benchmark and has lower TTFT in both benchmarks; the UI/UX acceptance row for the current binary is
unverified; and the broader task-quality/long-context/concurrency/competitor-UX matrices remain
open. Status stays `PARTIAL` / `PHYSICAL_PC_ACCEPTANCE_REQUIRED`.

**2026-09-15 (continued) — Training Lab was never a hardware limit. It is now installed, verified
and measured, and PaRuX beats a direct Unsloth run on the same stack.** The 2026-09-09 entry below
records local training as `HARDWARE_NOT_AVAILABLE`, attributed to "this machine's Radeon 780M". This
machine has an RTX 5080. Six real defects, all in PaRuX, stood between the product and a working
fine-tune; none of them was the GPU. Commits `027d9da`, `ccbfcdb`, `e387a52`, `de5400a`, `a0634e8`,
`fbdd974`.

The runtime was **uninstallable on every machine**: it pinned Python 3.13.15, a patch uv has no
download for, so install ended in `No download found for request: cpython-3.13.15-windows-x86_64-none`
— and the repair path deleted the existing environment *before* asking for the interpreter, so
trying cost the machine the runtime it had. The pin is now the 3.13 series with the resolved patch
recorded in the manifest, and nothing is removed until a replacement interpreter is in hand. Behind
that: the v1 engine probe filled two fields and left `Ready`, `UnslothVersion` and `Backend` at their
defaults, so a working runtime answered `ready:false, unslothVersion:null, backend:unknown` — which
is how "cannot train" entered this plan; v1 and v2 then disagreed, one advertising a runtime the
other refused with 503, because only the gate checked provenance (the venv present was a legacy
predecessor-era one on system Python, and refusing it was correct); and the v2 probe's 45-second limit
was below the cost of importing Unsloth once, so a freshly installed runtime reported "probe timed
out" and stayed unusable.

**Verified live through Lifecycle Lab, not by inspection:** `installed: true, ready: true,
cudaVerified: true, backend: cuda`, Python 3.13.14, Unsloth 2026.9.4, torch 2.12.1+cu132, freeze
SHA-256 `5528c30c9a96a07a65667d32b01a32aa369634bfa3c8b856c2b92e04c87060f5`, all six methods
available. A real SFT run completed 30/30 steps and wrote an 8.47 MB LoRA adapter (r=16, alpha=32,
q/k/v/o).

**Measured against Unsloth invoked directly**, same venv, same weights, same 32-row corpus, same 30
optimizer steps, GPU otherwise idle: bare Unsloth 103.5 s end-to-end (65.2 s training, 0.460
steps/s, final-step loss 1.22); PaRuX 58.4 s end-to-end, final-step loss 1.225, epoch 3.75 on both.
**1.77x faster with an identical loss trajectory** — not from better kernels, which are Unsloth's
own, but from not crippling them: every method ran at `per_device_train_batch_size=1` with 4
accumulation steps regardless of hardware. At the same four samples per optimizer step, 1x4 took
49.1 s where 4x1 took 18.5 s, peak usage 0.56 GB against 0.93 GB of a 16 GB card. Throughput at
batch 16 reached 30.96 samples/s against 2.44 at the old shape, and batches of 8 and 16 finished in
the same time (15.56 s vs 15.51 s), so beyond that point the limit is per-step overhead, not the
kernel. **A better kernel is not where the next win is**, and this is the evidence for saying so.

**2026-09-15 (continued) — clean-install UI/UX pass on real binaries, eight defects fixed, local
coding agent unblocked.** A full pass was run against installed MSIs, not a publish layout: uninstall,
state reset, install, first run, model download, chat, every workspace and every Settings subsection,
repeated after each fix. Commits `958cb69`, `3f6f5c9`, `3e194ad`, `b9eb7d4`, `d0264e1`, `35ab53a`.

Delivery was the worst of it. Every build of a release carries the same `ProductVersion`/`FileVersion`,
so installing a rebuilt 1.8.0 registered a **second** PaRuX in Installed Apps and left the old binaries
in place while msiexec reported success — users were told they had upgraded and had not.
`AllowSameVersionUpgrades` fixes it; five consecutive same-version upgrades then kept exactly one
registered product and replaced the binaries every time.

First run told an RTX 5080 machine it had "AMD Radeon 780M Graphics with 15.6 GB of video memory" (the
figure came from the largest adapter, the name from whichever enumerated first — in two places that had
each decided independently, now one `SelectPrimaryAdapter`), and produced verdicts contradicting their
own arithmetic ("About 13.4 GB, more than … 15.6 GB") because the sentence quoted raw weights while the
classification correctly counted runtime overhead. Also fixed: a 2.3 GB download reported no progress
until the workspace was reopened; a folder picked during onboarding was discarded unless Save was
pressed separately; a hotkey Windows had refused was shown as configured; a correct out-of-memory
refusal named no way out of it; the shipped file version repeated its commit twice; four Italian
starter prompts in an English UI; and `build-msi.ps1` failed on its third line in any fresh shell.

**The local coding agent could not complete a single step, and the cause was PaRuX's own.**
llama-server rejected every request with HTTP 400 "failed to parse grammar": the action schema carries
`maxLength` bounds and the bundled build cannot compile one of the values used — reproducibly,
everything else held constant, **2000 fails while 240, 3000, 4096, 8000, 30000 and 1000000 compile**.
Core detected the error but the SSE response had already started, so it ended the stream silently and
the model was blamed for nine empty turns. Bounds removed (`TryValidateAction` already enforces them),
provider failures after stream start now surface as an error event. The agent then executed real
`read_file` / `read_artifact` / `replace_range` steps; the next failure arrived **with its cause
printed**: `request (8898 tokens) exceeds the available context size (8192)`. Raising the physical
context to 16k in Runtime Manager applies correctly on next model load (verified: `--ctx-size 16384`).

Two more ways a task could be born unable to succeed: role inference matched keywords as raw
substrings, so "the existing tests must still pass" routed a code edit to the tool-less testing role;
and a coding task with no repository authority waited forever instead of failing. Both fixed, with
tests. Chat replies now carry "Apply this as a coding task", so an answer can become a real coding task
on the configured workspace instead of being carried elsewhere by hand — verified end-to-end.

**Fair Backend Race, same weights confirmed** (`Qwen3.5-4B-Q4_K_M` both sides, no resident text model —
Core refuses to measure against one): PaRuX 180.01 decode tok/s, 181.52 sustained, 167.50 ms TTFT,
3707 prompt tok/s, score 0.999; Ollama 157.00 decode, 149.99 sustained, 166.32 ms TTFT, score 0.893.
Winner PaRuX on this workload (+14.7% decode, +21% sustained); **TTFT is a tie, and that is still the
one dimension Ollama has never lost** — `BEST_IN_CLASS` stays not claimed. Remaining UI gaps observed
and not fixed: a detected `.gguf` that is "fallback only" offers no control to assign it, and Execution
Profile computes its budgets from a 65,536-token context while the runtime was serving 8,192.

Truth order:

```text
current code > current tests > real runtime evidence > this active plan > historical closeouts/documentation
```

Prior baseline closeout: `docs/PARUX_MAIN_SOFTWARE_CLOSEOUT_2026-09-05.md`. Its blanket completeness conclusion is superseded by the active 2026-09-10 backlog; its dated implementation evidence remains useful.

## Language and localization policy — owner decision 2026-10-21

**PARTIAL.** The owner decided that the product, its documentation and its code comments are English by default, with Italian allowed at most as an optional language.

- Audit (2026-10-21, `git grep` for Italian words and accented letters over tracked source, tests and docs): about 46 files and 200 lines. The product is already almost entirely English.
- **A. User-visible English text: translation landed; acceptance IN PROGRESS.** The audited strings in `ShadowRecoveryDialogModel`, `ChatView.NativeCodingHandoff` (apply dialog and coding-task UI), `CodingVerificationPresentation` (verification badges and warnings), and the small code-folder status/tooltip are now English. The Italian narrative paragraphs identified in the two audit documents and this plan were also translated. The source and corresponding string assertions were updated in the local i18n commits (`3f88d36e`, `9cd2e2cc`, `b69cb448`, `bf6b4108`, `7b4f2cbb`, `5d65d792`, `0c4cf815`, `97c4b6d5`). These commits contain local-model drafts; translation presence is not proof of acceptance. **Smoke count reconciliation:** the historical 2026-10-06 **59/59** result refers to the full `SmokeTests` class. The current `tools/test-ui.ps1 -Suite Smoke` selects `Category=Smoke` (37 tests); that selection passed **37/37** on 2026-10-23. The different filters explain the 59-versus-37 count; the current class-filtered result is recorded below. Complete the tracked-source audit and current UI acceptance before closing this item. Keep the broader language policy PARTIAL until no remaining user-visible Italian is found.
- **A verification update (2026-10-23; A remains IN PROGRESS):** targeted Server tests **26/26**, `CodingVerificationHonestyTests` **3/3**, current `Category=Smoke` FlaUI run **37/37**, and `ExplicitDiscoveryTests` **7/7** passed. Two current class-filtered `SmokeTests` runs each discovered **59** cases and ended **58 passed / 1 failed**. `Chat_header_adapts_and_recent_history_is_bounded_when_the_window_is_resized` timed out waiting for Create keyboard focus after Up from Projects; an isolated rerun passed **1/1**, but the full class failed again while measured memory use was 39.8%. The same failure was previously observed on main during 90–99% host RAM, and an explicit-focus attempt did not resolve it (`docs/validation/ui-gaps/round7-triage.md`); host memory is not established as the current cause. Logs: `.ai/artifacts/fla-ui-smoke-20261023.log`, `.ai/artifacts/fla-ui-smoketests-current-20261023.log`, `.ai/artifacts/fla-ui-smoke-focus-repro-20261023.log`, `.ai/artifacts/fla-ui-smoketests-controlled-20261023.log`. Do not count the class suite as a pass. The full tracked-source audit for remaining user-visible Italian is also still outstanding.
- **B. Intentional Italian input understanding (keep, document as bilingual input support):** intent and routing keywords (`RouterService`, `ComputerUseService`, `WebIntentDetector`, Android `RouterService.kt`), refusal and clock-question patterns (`RefusalHeuristic`, `LocalClockAnswer`), and RAG/memory stop-word lists. These make Italian prompts work and are not translation targets.
- **C. Localized tool output matching (keep):** `CodingVerificationCommandPolicy` recognises localized `dotnet test` messages; it must keep matching them.
- **MISSING:** an optional Italian locale layer (English default, resource-based) for user-visible strings, so that Italian remains selectable; UI tests must reference the same string constants. Decide before 1.x whether Italian ships at all.
- Acceptance: no Italian text in the default UI; AutomationIds unchanged; Server suite and FlaUI Smoke/Explicit/narrow green after the change; the two bilingual-input categories above documented and covered by tests.

## Active competitive-gap backlog — 2026-09-10

**Audit baseline:** `main` at `533d34a895449b910b19408ca1ec31937434cea6`. Recheck each finding against the current commit before implementation; preserve fixes that have landed since this snapshot. This is the active backlog inside the existing canonical plan, not a second roadmap.

**Objective:** one local-AI application for chat, knowledge, coding, documents, media, voice and training, with reliable setup, resource control and recovery. Product orchestration, model capability and hardware/runtime performance are separate dimensions. Better orchestration does not establish that a local 4B model matches a stronger cloud model. Codex is a task-quality and workflow reference; a cloud Codex run is not a same-GPU inference-speed comparison.

**2026-09-22 product direction — zero-jargon autonomous AI for ordinary PC owners:** Simple mode must make PaRuX useful to someone who does not know what a model, quantization, backend, context window or GPU layer is. A fresh install on a normal Windows PC — including a gaming PC whose GPU is idle much of the day — should detect the actual hardware, recommend a bounded initial AI stack, obtain only the required consent for network/storage/licence boundaries, download and configure what is needed, measure it on that machine, and route each task automatically. AUTO must optimize for **task quality, responsiveness, sustained throughput, reliability, memory/thermal/power efficiency and storage cost together**; raw tok/s alone and benchmark quality alone are both insufficient. Responsive / Balanced / Max Intelligence remain understandable user intents, while Advanced keeps exact model/runtime controls and manual locks. Gaming remains a hard resource boundary: AI work must release contested resources promptly when gaming starts and must not silently resume or download/benchmark in a way that harms gameplay. The long-term product objective is not merely to expose more engines than competitors, but to deliver better verified end-to-end task results with less setup and less user intervention on the same hardware; any performance or best-in-class claim still requires the fair evidence rules below.

**Evidence boundary:** this review inspected source and existing physical-PC reports, ran the 11 VS Code Node tests, and reproduced the ledger SQL selection and Git benchmark scope defects with isolated SQLite/Git fixtures. It did not run a new Windows/.NET build, packaged WinUI session, real-model benchmark, NPU test or physical-phone acceptance. Source risks, reproduced defects, earlier measurements and proposed targets must remain distinguishable.

### Execution and closure rules

- Work directly from current code and these IDs, highest-priority actionable item first. Missing internal functionality is implementation work. Split unavailable hardware/credentials from software that can be completed now.
- Use small coherent implementation slices; add meaningful behavioral regression coverage, update the affected row with commit/evidence/remaining work, and continue. Allowed item status: **DONE / PARTIAL / MISSING / BLOCKED-EXTERNAL**. All rows start PARTIAL because existing capabilities are real but incomplete or insufficiently validated.
- **GitHub Actions remains disabled.** Do not add, enable or dispatch workflows or introduce a cloud-CI requirement. GitHub read/write interactions may deliver commits; execute validation locally in an available environment. Record unavailable Windows, GPU or signing checks as not run, with the exact remaining procedure.
- **First cross-cutting correction — completed 2026-09-23:** `ActiveReleaseStatusContractTests.cs` now asserts exact active status sections and all 16 backlog rows while allowing clearly historical wording to remain historical. README, releasing policy, current validation/reference status and the closeout's current-status preface now identify BIC-01–BIC-16. `release-gate.ps1` counts open P0/P1/P2 rows; `validate-software-complete.ps1` refuses a completion claim while any active BIC row remains open. This correction changes no BIC implementation status.
- Keep Chat as home, Normal/Advanced disclosure, Detect → Locate existing / Install with PaRuX / Skip, and PARUX_MANAGED / USER_MANAGED / SYSTEM_DETECTED ownership. Preserve pairing, LAN authorization, Gaming hard-off, STOP/cancellation, backup safety and user data. Never silently alter USER_MANAGED installations.
- DONE requires the applicable implementation, focused/regression tests, production integration, failure/recovery, platform/resource/security checks and truthful status reconciliation. A descriptor, text-contract assertion, mock, successful compile or newly committed test that was not run is insufficient.

### Detailed implementation orchestrator and model suggestions

Use one integration owner per production-sized slice. The orchestrator states acceptance, isolation boundaries and the next canonical-plan dependency; the planner identifies contracts/files/risks; the developer implements and adds regression tests; QA independently runs the test ladder; a separate reviewer checks the final diff and evidence; then the orchestrator updates this plan and the local checkpoint. Parallelize only independent, bounded discovery, log summarization or review; never allow concurrent edits to the same files. After three substantial failed attempts at one issue, stop patching and re-plan. Keep complete logs in ignored `.ai/artifacts/` and hand off only concise status, counts, failures and decisive traces.

Suggested Codex models/effort (when available; these are workflow-role suggestions, not product-model recommendations):

| Role | Suggested model / effort | Scope |
|---|---|---|
| Repo scout / bounded research | `gpt-6-luna`, low | Symbol-first, read-only discovery and compact file/test handoff. |
| Planner / architect | `gpt-6-sol`, medium | Slice boundaries, invariants, dependencies, risks and acceptance; no implementation. |
| Developer / integration owner | `gpt-6-sol`, high | One explicit file-ownership set, regression tests and targeted build/tests. |
| Tester / QA | `gpt-6-sol`, medium | Independently run acceptance and relevant regression; use FlaUI for UI changes with stable AutomationIds and bounded waits. |
| Reviewer | Separate `gpt-6-sol`, medium | Diff-focused correctness, security, lifecycle, persistence, concurrency and test-gap review; no self-certification. |
| Architecture escalation | `gpt-6-astra`, high | Reserve for a demonstrated critical cross-cutting uncertainty, not routine slices. |

For PaRuX end-user model suggestions, do not name a “best” model from a static list or this orchestration policy. First combine detected hardware, enabled task/capability, verified runtime compatibility, selected-file size, shared quota headroom, destination-volume free space, licence boundary, and fresh task-quality plus compatible local-performance evidence. Without the latter evidence say “candidate/provisional fit,” explain the trade-off plainly, and preserve user choice; never infer GPU/NPU or physical-PC performance from CPU fixtures or another host.

**Chat-first agentic product contract (requested 2026-09-25; status PARTIAL).** The primary
workflow should feel like Codex while remaining local-first: the user states a goal in Chat; a
Core-owned orchestrator turns it into a visible, bounded plan and runs the appropriate specialist
agents/models without sending inference to a cloud service by default. The conversation itself
shows each agent's role, state/current step, concise milestone and salient decisions, chosen model
and reason, changed files, verification state, blockers and durable questions. Raw logs and tool
traces stay available on demand rather than flooding the conversation. The user can steer, approve,
answer a question or STOP at any time; failures and partial results must never look like success.

Model selection is per task/capability/profile, not global: planning, coding, review, tests,
research, general/reasoning/uncensored chat, image generation/editing, video and other supported
workloads may use different compatible local models. Recommendations must explain evidence,
hardware/resource fit, quality and latency trade-offs, confidence and fallbacks; unknown or
unmeasured capability stays provisional. A user-selected exact model remains respected where
compatible, and substitution is always visible.

Git is a first-class part of the same task flow: identify the explicitly selected repository,
show branch/status/diff, work in a PaRuX-owned isolated worktree, run relevant checks, and present a
reviewable diff plus verification summary for adoption. Never overwrite the user's working tree or
silently stage, commit, merge, push, publish, delete branches, or rewrite history. Any operation
that changes a shared branch or remote requires explicit, just-in-time approval. STOP must cancel
workers and clean up only task-owned state while preserving completed evidence.

Acceptance requires FlaUI coverage for task submission, agent/milestone visibility, model-reason
disclosure, follow-up/ask-user, STOP, changed-file diff, verification and approval-gated Git
adoption; then a non-destructive real UI dogfood run against a disposable local repository proves
the complete prompt → plan → agents → edits → tests → review → adoption path.

| ID | Priority | Status | Deliverable / closure focus |
|---|---|---|---|
| BIC-16 | P0 | PARTIAL | Autonomous model discovery, validation, promotion and beginner-safe Simple mode |
| BIC-01 | P1 | DONE | Correct active ledger state beyond 1,000 events |
| BIC-02 | P1 | DONE | Immutable benchmark start and complete edit-scope audit |
| BIC-03 | P1 | PARTIAL | Representative independent evaluation and valid winner rules |
| BIC-04 | P1 | PARTIAL | Unified GPU/backend inventory and correct shared-memory budgets |
| BIC-05 | P1 | PARTIAL | Explicit, tested Ollama request/response compatibility |
| BIC-06 | P1 | PARTIAL | All workspace navigation reachable under real WinUI layouts |
| BIC-07 | P1 | PARTIAL | Commit-linked release artifacts and usable signed update delivery |
| BIC-08 | P1 | PARTIAL | Honest ONNX capabilities, request cancellation and warm recovery |
| BIC-09 | P2 | PARTIAL | Efficient coding orchestration and native editor review |
| BIC-10 | P2 | PARTIAL | Incremental semantic indexing for add/remove and project-file coverage |
| BIC-11 | P2 | PARTIAL | Tokenizer-bound cache identity and final prompt budget correctness |
| BIC-12 | P2 | DONE | Native workbook formulas, editing, charts and validation |
| BIC-13 | P2 | PARTIAL | Media, training and voice depth, reproducibility and measured latency |
| BIC-14 | P2 | DONE | Browser/mobile workflow coverage and paired-client acceptance |
| BIC-15 | P2 | PARTIAL | Isolated backup pools, strict local inference and listener-race checks |
| BIC-17 | P1 | DONE | Universal Local AI Gateway and one-click Claude Code/Codex/OpenCode integrations |

### 2026-10-06 Release Classification: Mandatory Gates vs Documented Residuals

| BIC ID | Classification | Priority | Status | Rationale & Gate Definition |
|---|---|---|---|---|
| **BIC-16** | Mandatory Gate | P0 | PARTIAL | S3a/S3b versioned ld1 launch descriptor, S4 manual-selection gate linearization, S5 recommendation coordinator (Pending/Active CAS, rollback, restart reconcile, STOP/Gaming guards), S6 routing consult (0-overhead probe), S7 discovery endpoint gating, S8 Gaming cancellation of evaluation jobs. All implemented and verified PASS. Fresh cohort promotion evaluated NoWinner on RTX 5080 (fails closed, AUTO authority disabled as safety guard). |
| **BIC-01** | Mandatory Gate | P1 | DONE | Correct active ledger state beyond 1,000 events verified. |
| **BIC-02** | Mandatory Gate | P1 | DONE | Immutable benchmark start and complete edit-scope audit verified. |
| **BIC-03** | Mandatory Gate | P1 | PARTIAL | Evaluation harness with 30 tasks (parux-eval-v1.json), quality floor 80%, 8 mandatory cases, immutable verifiers, objective verdict rules (-VerdictSelfTest). Complete multi-provider matrix is a documented performance residual. |
| **BIC-04** | Mandatory Gate | P1 | PARTIAL | DXGI unified hardware inventory, accurate NVIDIA device-wide memory.free VRAM sampling, dynamic RAM admission, UMA single pool, Gaming resource release. AMD/Intel live dedicated VRAM query is a documented residual. |
| **BIC-05** | Mandatory Gate | P1 | PARTIAL | Ollama HTTP protocol mapping (/api/chat, /api/generate, /api/tags, /api/show, /api/ps), typed upstream timeout diagnostics (elapsed ms, requested model, resolved model), mid-stream backend crash NDJSON terminal error frames ({"done":true,"done_reason":"error","error":"..."}), capability advertising ["completion", "chat", "tools"]. All 43/43 tests pass. |
| **BIC-06** | Mandatory Gate | P1 | PARTIAL | Complete WinUI workspace reachability (narrow 560x560, compact, wide, collapsed rail), 50-item bounded history, in-process Win32 SHBrowseForFolder STA thread fallback for sandboxed sessions. FlaUI suites 59/59 Smoke, 7/7 ExplicitDiscovery, 1/1 Narrow Reachability 100% green. OS DPI scaling (100/125/150%) is a documented owner-manual residual. |
| **BIC-07** | Mandatory Gate | P1 | PARTIAL | Version bump 1.9.6, automated MSI build (build-msi.ps1 -Release), signed update manifest envelope, CycloneDX SBOM (79 components), local-only Android APK build (publish-android-apk.ps1 -LocalOnly), asset verification (verify-release-assets.ps1). Third-party Authenticode certificate is BLOCKED-EXTERNAL (unsigned MSI with Unknown Publisher accepted). |
| **BIC-08** | Documented Residual | P1 | PARTIAL | ONNX GenAI request cancellation, warm recovery, CPU fallback, and thinking control rejection verified. AMD XDNA NPU execution provider on Windows ML is BLOCKED-EXTERNAL (unsupported hardware profile). |
| **BIC-09** | Mandatory Gate | P2 | PARTIAL | 4 low security findings closed (benchmark mode disable-only, typed exit code, desktop execution lease, Completed parent cascade), AppContainer path length guard (< 185 chars), real Core-owned coding task S4 UI confirm+apply verified PASS (1m09s, real-s4.trx). |
| **BIC-10** | Mandatory Gate | P2 | PARTIAL | Incremental SQLite semantic indexing, transactional add/remove, prefix deletion, parity with full rebuild verified. 50k multi-language benchmark is a documented residual. |
| **BIC-11** | Mandatory Gate | P2 | PARTIAL | Tokenizer bound cache identity (alias + file identity), direct-engine pre-inference token calculation, strict token budget bounds verified (46/46 tests green). Non-llama counting estimation is a documented residual. |
| **BIC-12** | Mandatory Gate | P2 | DONE | Native Excel workbook formulas, charts, styles, and validation verified. |
| **BIC-13** | Mandatory Gate | P2 | PARTIAL | Media engine isolation, speech cancellation protocol, isolated CPython managed toolchain (voice-core-preflight-01), PCM-to-WAV streaming. Radeon 780M LLM/Vision training is BLOCKED-EXTERNAL / unsupported. Subjective human media quality is a documented unperformed evaluation. |
| **BIC-14** | Mandatory Gate | P2 | DONE | Browser/mobile workflow coverage and paired-client acceptance verified. |
| **BIC-15** | Mandatory Gate | P2 | PARTIAL | SQLite pool isolation, exclusive IPv4/IPv6 listener binding, strict local inference (CloudFallback disabled). Elevated loopback-only packet capture (bic15-v4 pktmon/netsh with positive control) is a pre-release gate. |
| **BIC-17** | Mandatory Gate | P1 | DONE | Universal Local AI Gateway and one-click integrations verified. |

### BIC-16 — autonomous model discovery, validation, promotion and beginner-safe Simple mode

**2026-10-03 progress (status PARTIAL):** production job `0aa410f8` (Qwen3.5-4B Q4_K_M, quality 30/30, performance Failed, 0 repetitions, `performance_not_completed`) was root-caused with real captures: the fixed workload caps generation at 96 tokens and Qwen3.5 always ends `finish_reason=length` at exactly 96; the parser rejected non-`stop`. Commit `8c29d26` accepts `length` only when timings, usage and the workload bound agree (v3 records only), records the real finish reason, adds a bounded failure code, and makes comparison return NoWinner (`completion_latency_censored`) when any sample is capped, so truncated answers cannot win on completion latency. The failed evidence is preserved. Remaining here: job/status text does not yet flag capped completion latency as a lower bound. Commit `defde76` adds the Model Hub explicit-repository discovery UI (FlaUI 7/7). After BIC-04 S1 (`hw2`/`rtv2` fingerprints) all earlier quality/performance evidence, policies and proofs no longer match and fail closed; a fresh cohort is required. No promotion, rollback or AUTO authority exists yet.

**2026-10-03 BIC-16 S3a (`a857a9f`, independent review PASS; status PARTIAL):** versioned `ld1` launch descriptor produced by the single production planner (real exact load and planning-only calls share it; planning writes nothing); actual-launch fence publishes the descriptor with the verified process identity and clears on stop, exit, PID reuse, router launch or Core restart; the evaluator/AUTO rule is exact equality only (descriptor, model file, `hw2`, runtime tag, `rtv2`), and fixed-profile evidence can no longer form a proof or Pending/Active intent. Full suite 4,141/4,141 plus real child-process fence evidence. No evidence producer emits `ld1` yet, so no candidate can currently qualify (fails closed). Carry-forward before any proof/AUTO consumer: consumers must call `CandidateLaunchScope.Check` against `GetVerifiedTextLaunchDescriptor()` and current host attestation (format-only checks today), re-read fence and health inside the transition lease/CAS, enforce exact model bytes, surface descriptor-refusal reasons (e.g. non-ASCII env values), document that CUDA/OMP/GPU env changes invalidate evidence, and cross-check backend from the executable path. Remaining slices: S3b evaluator under `ld1` with request parity and capped-latency display plus a fresh GPU cohort, S4 manual-pin/CAS linearization, S5 activation/recovery/rollback with STOP/Gaming, S6 routing/endpoints without per-route hashing.

**2026-10-03 BIC-16 S3b (`d49d37b` rebased, independent review PASS; status PARTIAL):** the evaluator measures quality and performance under the planned `ld1` launch through the single planner with production request parity (suite v3, quality profile v2, workload v3; v2 records remain readable history only). Job summaries persist launch fingerprint, arguments, environment names, parity version, refusal reason and a capped-latency flag; Model Hub shows "completion ≥ N ms (capped answer, lower bound)". The proof gate and recommendation store call `CandidateLaunchScope.Check` against the verified fence and current host attestation, and re-read the fence and exact live model bytes inside the transition lease/CAS. **Real GPU cohort (RTX 5080, policy v4 unchanged, 10 interleaved fresh runs, all retained):** Qwen3-4B Q4_K_M 5/5 quality-reliable (TTFT ≈ 29–35 ms, completion ≈ 261–289 ms); Qwen3.5-4B Q4_K_M 4/5 = 80% (run 3 `quality_not_passed`, 28/30) and every scored run capped at 96 tokens (completion censored, ≈ 635–651 ms lower bound). Comparison in both directions: NoWinner `quality_below_reliability_floor`. Deviation recorded: the cohort used exact hash-verified copies (same size, mtime and SHA-256 as the original provenance) of the preserved model files in an isolated data root; the original acquisition evidence and failed job were not modified. Follow-ups: surface `active_model_proof_stale`/`incumbent_*` in plain language (only the scoped exact-load route records an incumbent proof); add a reset hint for `request_parity_execution_profile_differs`; list failed case ids per run in evidence (Qwen3 28/30 runs passed via profile floors); confirm provenance rebinding re-hashes instead of trusting mtime; health is not re-read inside the CAS (bounded by the 2-minute admission age; activation slice must handle hung-but-alive). No AUTO authority exists.

**2026-10-04 BIC-16 S3b follow-ups + S4 (`a70a7c1`..`d8e16d1`, independent review PASS after one fix round; status PARTIAL):** plain-language refusal reasons with the fix (incumbent proof, request parity, launch descriptor, manual pins); failed case ids per run in job summaries; provenance rebinding re-hashes (same-size swap with restored mtime rejected). One `ManualSelectionGate` linearizes every manual-selection writer (role overrides incl. auto-assigner and persisted loads, chat Manual routing, scoped Force/Prefer) with durable atomic saves (flush + bounded retry on share violations). Recommendation transitions take policy → metadata → ledger → store → pin gates and refuse on any user pin, stale snapshot, replaced fence or non-exact bytes; a refused commit leaves Pending untouched and transitions never write pins. Role overrides carry an origin (user / auto-assigned; legacy = user); only user-origin pins block, the auto-assigner never replaces a user override and reports skipped roles, and an unreadable routing preference is shown as unreadable instead of Automatic. Full suite 4,267/4,267. Open: restoring only the origins sidecar from an older backup could mislabel a user pin (bind origin to the override value or back up both files as one unit); no cross-process pin linearization; S5 coordinator/activation/recovery/rollback with STOP/Gaming; S6 routing/endpoints. FlaUI: provisional-fit test updated for workload v3; `Local_filter_reaches_rows_beyond_cap` is intermittent and two FlaUI tests (integrations page, coding model selection) fail in this environment — to be fixed before release.

**2026-10-04 BIC-16 S5 (`dfded32`/`96b7473`, independent review PASS; status PARTIAL):** recommendation transaction coordinator (promote, rollback to the stored previous winner, restart reconcile) under one held text-transition writer: proof against the live incumbent → durable Pending (generation CAS) → fresh exact load → bounded health (hung-but-alive fails) → gated final CAS. Failure aborts Pending first and restores the exact incumbent only when Gaming and STOP allow, otherwise reports a stopped runtime and never substitutes another model. Reconcile never infers success from a live challenger and is a no-op without recommendation state. Pins are never written or overridden; role-override origin is bound to the override value (`origin:hash(path)`; mismatches read as user). Evidence: 22 coordinator tests, 10 real child-process self-kill tests at every durable step (store/CAS real; runtime and fence are fakes — not end-to-end), full suite 4,299/4,299. Nothing calls promote/rollback yet and no AUTO authority is granted. Carry-forward: S6 routing consult and endpoints, a real-runtime kill test, treat transient IO during reconcile as unknown, bound reconcile writer acquisition, model-size-aware recovery budget, drop the shipping-assembly `InternalsVisibleTo` for the harness, surface auto-assigned overrides that became user pins on upgrade.
**2026-10-04 BIC-16 S6 (status PARTIAL, independent review pending):** routing consult (explicit AUTO, Standard x Balanced only) honours every pin, Pending, Gaming, policy change and the Active winner, loading it through the fresh-exact scoped path and falling back to the ordinary ranker with a named reason; recommendation status/promote/rollback endpoints return typed 200/409/503 outcomes. Model Hub offers "Make recommended for Chat" only for a candidate whose latest comparison met the margin while Core reports `CanPromote`, and "Go back" only on the current winner when Core reports `CanRollback`; both ask for confirmation, show plain-language reasons (CandidateReasonText) and cause no download or evaluation. Chat-route overhead: no per-route GGUF hashing or ledger serialization; with no recommendation the consult costs 24.06 us/route vs a bare existence probe of 22.23 us (1.8 us above it, 0 parses, 0 model bytes, 0 ledger reads); other routes never call it. Evidence: full `PaRuX.Server.Tests` 4,368/4,368, FlaUI `RecommendationActionsTests` 2/2 (DEBUG fixtures only; no real-runtime promotion exercised), App Debug build 0 errors. Production AUTO stays closed: the real cohort is NoWinner, so no Active recommendation can exist. Open: real-runtime promote/rollback kill test, independent review of S6.

**2026-10-06 BIC-16 S7/S8 (Gaming cancellation & discovery gating, independent review PASS; status PARTIAL):** (1) `CandidateEvaluationJobService` integrates `isGamingActive` delegate (`() => runtime.GamingMode`), gating evaluation startup with outcome `"GamingActive"` and providing thread-safe `CancelForGaming()` which aborts active running evaluation jobs via `_active.Cancellation.Cancel()` (wrapped in `ObjectDisposedException` guard) transitioning cleanly to status `"Cancelled"`. (2) `WorkloadOrchestrator` wires `cancelEvaluations: () => jobs.CancelForGaming()` into `ReleaseResourcesForGamingAsync`, ensuring GPU-intensive evaluation tasks are terminated immediately upon entering Gaming mode. (3) `POST /api/v1/models/discovery/{key}/downloads` and `POST /api/v1/models/discovery/{key}/evaluation-jobs` return HTTP 409 Conflict (`AI paused by Gaming — Return to AI.`) when Gaming Mode is active. Independent security/runtime review PASS (24/24 targeted unit/integration tests green). BIC-16 remains PARTIAL pending fresh GPU benchmark runs.

**Finding:** the model/runtime ecosystem now changes faster than a normal desktop release cadence. A curated or hard-coded recommendation list can be obsolete before the next PaRuX binary ships, while blindly following download counts, vendor claims or raw throughput can replace a stable model with one that is faster but worse at the user's actual work. PaRuX already separates model intelligence from runtime performance and has Model Hub, routing, hardware inventory, benchmarking and Gaming hard-off foundations, but the active plan does not yet require a complete closed loop that can discover a model unknown to the installed binary, establish licence/provenance/runtime compatibility, measure quality and performance on the user's machine, promote it safely, and later roll it back or retire it without requiring the user to understand local-AI internals.

**Implement:** build a Core-owned **Model Discovery & Evolution** loop with the following contract.

1. **Discovery without app releases.** Use versioned provider adapters and public metadata feeds/APIs (starting with Hugging Face and extensible to official publisher/runtime catalogues) to discover new text, code, embedding/reranker, vision, image/editing, video, speech and other supported model classes after the PaRuX binary was shipped. Normalize publisher/repository, immutable revision/hash, declared modality/capabilities, parameter/weight size when available, file formats, required runtimes, estimated download/storage, licence/EULA state and provenance/trust. Discovery metadata must never execute arbitrary repository code or silently trust a remote README as a capability proof.
2. **Candidate → Validated → Recommended lifecycle.** A newly discovered model starts as **Candidate**. It becomes **Validated** only after integrity/provenance checks, licence policy, hardware/runtime compatibility and task-appropriate local evaluation. It becomes **Recommended** for a specific task/profile/hardware fingerprint only after it beats the incumbent by a predefined utility margin without violating quality/reliability/resource floors. Promotion is scoped: one model may be recommended for image editing but not image generation, coding but not general chat, or Responsive but not Max Intelligence. Keep the previous winner available for bounded rollback until the replacement proves stable.
3. **Licence, provenance and supply-chain gates.** Apache/MIT/BSD-like permissive models may follow the user's automatic-download policy; click-through/custom/research/non-commercial/unknown licences require the appropriate explicit decision and must never be silently accepted. Persist the exact licence/revision evidence used for a download. Verify hashes/manifests where available, quarantine incomplete/tampered downloads, and keep remote custom code disabled unless a separately designed sandbox/approval path explicitly requires it.
4. **Task-quality evidence remains separate from speed.** Maintain versioned evaluation suites for instruction following, reasoning, coding, tool/schema adherence, RAG/grounding, multilingual use, vision, image generation/editing, typography/reference fidelity, video temporal quality, STT/TTS and any future advertised capability. Prefer deterministic or independently checkable outcomes where possible; use fixed corpora, held-out cases and human/independent review for capabilities whose quality cannot be established by one local judge score. Do not let a faster failed, truncated or lower-quality result win.
5. **Machine-specific performance evidence.** On the actual PC measure applicable runtime/backend/model/quantization/context/KV/batch/speculative configurations with cold and warm state separated: TTFT, prefill, sustained decode/generation latency, task completion time, RAM/VRAM, residual residency, stability/recovery, thermals/power where observable and storage cost. Use bounded/adaptive search rather than exhaustively downloading or testing every permutation. Reuse evidence only when the relevant hardware/driver/runtime/model revision/configuration fingerprint still matches.
6. **Multi-objective AUTO routing.** Compute routing utility from separate, inspectable dimensions: task suitability/quality, responsiveness, throughput/task time, reliability, hardware fit, memory pressure, thermals/power and storage/download cost. Do not collapse model intelligence and runtime performance into one opaque score. Responsive / Balanced / Max Intelligence define policy weights and quality floors; the same installed model set may therefore route differently per task. An exact manual model/runtime lock always wins and must never be silently substituted.

   **Capability-specific winners, not one global best model.** Maintain a versioned capability routing matrix instead of a single winner per modality. Examples include `chat.general`, `chat.reasoning`, `chat.uncensored`, `coding`, `tool_calling`, `rag`, `vision`, `image.generate`, `image.edit`, `image.reference`, `image.text_rendering`, `image.fast_preview`, `video.generate`, `stt`, `tts`, `embedding` and `reranking`. A model may be Recommended for one capability and not another. PaRuX may therefore keep multiple models installed simultaneously when that yields materially better task outcomes. For image workloads specifically, generation, editing/inpainting, reference fidelity, typography/text rendering, photorealism/artistic rendering and fast preview must be evaluated separately where the underlying model/runtime supports those distinctions. The routing matrix stores at least the current Recommended model, fallback, applicable runtime/configuration, evidence fingerprint and confidence/quality floor for each capability/profile/hardware combination.
7. **Fresh-install Simple experience.** A non-expert should be able to install PaRuX, understand a short plain-language recommendation tied to detected hardware, approve a bounded download/storage/licence decision and start using AI without choosing GGUF/EXL3/ONNX, quantization, backend, context, GPU layers, batch or KV settings. PaRuX should choose the smallest useful initial bundle rather than preinstalling a catalogue: general chat plus only the auxiliary embedding/reranker or specialist capability needed by the enabled experience. Explain choices in user language ("best balance on this PC", expected storage, approximate responsiveness) with an optional **Why this choice?** drill-down; keep raw benchmark/configuration detail in Advanced.

   **Advanced capability overrides.** Every routable capability must expose a persistent Advanced override with three distinct states:
   - **Automatic** — PaRuX owns model/runtime/configuration selection for that capability.
   - **Prefer** — use the selected model/runtime when it is compatible and safe, but allow an explicit fallback when it cannot satisfy the task, hardware budget or reliability floor. Any fallback must be visible in the task trace/result explanation.
   - **Force** — use the selected exact model/runtime/configuration only; if it cannot serve the request, fail explicitly with the reason rather than silently substituting another model.
   
   Overrides may be scoped by capability and, where useful, by profile (Responsive/Balanced/Max Intelligence). They must survive restart/update, remain visible in Advanced and never be silently reset by discovery, benchmarking, auto-promotion or recommendation refresh. Manual pins/user-managed models are never deleted or replaced automatically. Simple mode stays uncluttered but should surface a compact notice such as "2 capabilities use your manual choices" and respect those choices transparently.
8. **On-demand capability acquisition.** If a user asks for a capability the current library cannot meet well, Simple mode may propose — or, under an explicit auto-install policy and storage budget, stage — the best compatible candidate, including the reason, expected download and licence boundary. The task should degrade honestly or wait for the local install; it must not silently switch to cloud inference unless the separate cloud-fallback policy explicitly permits that exact turn.
9. **Continuous refresh without churn.** Periodically refresh metadata only under the user's network/update policy, use backoff/caching and support offline operation from the last trusted catalogue. Do not download every trending model. New releases, quantizations or runtimes enter staging only when they are plausible challengers for a supported task/hardware profile. Re-evaluate incumbents after meaningful driver/runtime/hardware changes. Automatically clean failed/stale staging data and offer safe reclamation of superseded large files without deleting manually pinned/user-managed models.

   **Discovery notification and consent UX.** When PaRuX finds a plausible challenger that is materially relevant to the user's hardware or enabled capabilities, surface a concise non-technical notification rather than silently changing the recommended stack. The notification should say what was found, what it could improve, approximate download/storage impact and whether any special licence/consent applies. Offer clear actions such as **Evaluate now**, **Later**, **Ignore this model**, and **Always evaluate suitable models automatically**. In Simple mode, evaluation may run automatically only if the user has explicitly enabled that policy and current idle/gaming/network/storage rules allow it. Promotion to Recommended may also follow the user's separate promotion policy: **ask before replacing**, **auto-promote if clearly better**, or **never replace pinned/manual choices**. Notifications should avoid spam by grouping multiple discoveries, suppressing obviously inferior/incompatible candidates and reporting only meaningful state changes. The notification/history area must also show the result of evaluation (for example, "better for image editing, kept current model for general image generation") and provide a one-click rollback/keep-current choice when applicable.
10. **Gaming-PC first-class behavior.** Treat an idle gaming PC as an opportunistic AI workstation, not as an always-on server. Discovery may be lightweight in the background, but downloads, conversion and benchmarks must respect idle/power/thermal/network policy. Gaming hard-off cancels or checkpoints active AI work as applicable, unloads contested runtimes/VRAM, prevents new model evaluation/download work from degrading gameplay and does not silently reload AI when the game exits unless the user enabled that resume policy. Preserve useful CPU/NPU/background work only when measured not to violate the gaming resource policy.
11. **Performance leadership is end-to-end, not a marketing toggle.** Continuously compare PaRuX against current relevant local-AI products/runtimes on the same physical machine using equivalent model/revision/quantization and quality constraints. Optimize PaRuX's own orchestration overhead, load/swap/unload behavior and backend choices so AUTO is not merely convenient but competitively fast. A competitor that is faster at raw decode but worse at task completion, quality, recovery or setup is not automatically the product winner; likewise PaRuX must not claim superiority from convenience if task quality or measured performance loses. Retain raw evidence and label unavailable comparisons INCONCLUSIVE.

For a PaRuX response-speed claim, run an exact-parity subset against every relevant comparator in the maintained
comparison set: same model-file hash/revision and tokenizer, identical system/developer/user instructions, tool
definitions, output budget and sampling, context/KV/runtime settings, concurrency and cold/warm state. Measure
end-to-end request-to-first-visible-token and request-to-final-visible-token separately, alongside TTFT/prefill/decode
and host/VRAM use. Alternate order, predeclare quality floors and report repeated latency distributions; three runs are
smoke evidence only, not p95. “Best in class” requires a reproducible advantage across the relevant comparable set
without quality/reliability loss; otherwise state the measured scope and trade-off.

**Accept:** all of the following are required before this row is DONE.

- A clean-install acceptance profile representing a non-expert Windows/gaming-PC user reaches a useful first local answer without selecting a model format, quantization or backend manually; the recommendation cites detected hardware, download/storage impact and plain-language reason, and Advanced can reveal the exact evidence without changing the Simple choice.
- An integration fixture publishes a previously unknown-to-the-binary candidate after build time. Without a PaRuX application update, discovery detects it, normalizes immutable identity/licence/capabilities, rejects unsafe/unsupported variants, stages a compatible one, benchmarks it, and promotes it only when predefined task-quality/reliability floors and utility-margin rules are met. A deliberately faster-but-worse candidate must not win; a better specialist may win only its eligible task/profile.
- Discovery UX is exercised with at least one relevant new candidate: the user receives one grouped plain-language notification, can choose Evaluate now/Later/Ignore/automatic evaluation, and PaRuX persists that preference. No candidate is silently downloaded or promoted outside the selected policy. Evaluation results are surfaced with the task-specific outcome and an explicit keep-current/rollback path where replacement occurred.
- Promotion and rollback are restart-safe and deterministic. Corrupt downloads, changed revisions, revoked/incompatible runtimes, failed benchmarks, OOM, cancellation, network loss and licence changes leave the current Recommended model usable and produce an actionable reason rather than a broken AUTO state.
- Text/coding/tool/RAG/vision/media/voice recommendation evidence is capability-specific and versioned. Performance telemetry cannot masquerade as intelligence evidence, a self-judge alone cannot certify subjective media quality, and every recommendation can answer "why this model on this PC for this task?" from stored measurements.
- AUTO demonstrates task-dependent routing across at least Responsive, Balanced and Max Intelligence with manual-lock override, bounded model swaps and no hidden cloud inference. Storage-budget exhaustion and insufficient hardware produce a plain-language alternative rather than exposing implementation jargon.
- Capability routing is verified with at least one modality requiring different winners: for example, one model wins `image.generate` while another wins `image.edit` or `image.text_rendering`. PaRuX keeps both when justified, routes each task to the correct winner, records the evidence independently and does not collapse them into a single "best image model".
- Advanced override tests cover **Automatic**, **Prefer** and **Force** per capability, including restart/update persistence. Prefer may fall back only for an explicit compatibility/resource/reliability reason and reports that fallback; Force never silently substitutes. Discovery or auto-promotion cannot overwrite a manual override, and returning the capability to Automatic restores normal recommendation/routing behavior.
- On the target gaming workstation, starting a configured game while AI/download/benchmark work is active satisfies the existing Gaming hard-off/resource-release contract without measurable hidden GPU residency from PaRuX-managed runtimes; no new discovery benchmark starts during gameplay, and post-game resume follows the explicit user policy.
- Same-machine competitor evidence includes quality/task success and user-visible task time as well as TTFT/prefill/decode/resources. PaRuX may be described as best-in-class or more performant only for the specific measured scope where the current fair comparison demonstrates it; otherwise the UI/docs state the measured trade-off.

**Progress — PARTIAL, 2026-09-23:** Chat now exposes the existing per-turn `model_routed` decision through a keyboard/UIA-accessible “Why this model?” dialog. It distinguishes AUTO profile and exact manual lock, reports Standard-role fallback, identifies capability as an estimate rather than proven answer quality, labels throughput as stored benchmark evidence rather than live speed, and omits model directory paths. Missing and malformed route-event evidence is handled defensively; changing conversations clears the displayed route. Commit: `d9774dda0fa646ab63324b6fab80a5521ca4c8b1`. Evidence: full `PaRuX.Server.Tests` 3,095/3,095; focused route explanation/parsing tests 10/10; targeted FlaUI route-explanation accessibility smoke 1/1; App Release x64 build succeeded; independent diff review found no remaining findings. **Still open:** versioned external model discovery, immutable provenance/licence/integrity gates, hardware-aware onboarding/acquisition consent, local task-quality evaluation and safe promotion/rollback, capability/profile overrides, notifications, Gaming policy integration, and required physical-PC validation. The row remains PARTIAL; this slice does not claim autonomous model discovery or recommendation completion.

**Progress — PARTIAL, 2026-09-24:** Added a conservative exact SPDX allow-list and shared consent-before-start boundary across all fresh Model Hub/Settings model downloads (GGUF, curated media checkpoints and search variants). Missing, unavailable, custom, composite, restrictive or otherwise unlisted metadata opens an unchecked per-download review with a fixed-host Hugging Face model-card link; cancellation does not invoke the transfer callback. A Debug-only, isolated FlaUI fixture verifies unchecked-primary blocking, cancel-without-start and start-after-confirmation. Commit: `226f8c5`. Evidence: policy tests 20/20; targeted FlaUI consent test 1/1; App Release x64 build 0 errors (6 existing analyzer/nullability warnings); independent review PASS. Exact revision/EULA evidence is not persisted and this is not legal verification; pre-existing partial downloads may resume without the new evidence. The licence/provenance gate remains PARTIAL. **Next actionable gap at that checkpoint:** make first-run model suggestions actionable and explainable from detected hardware/resource fit while separating approximate download size, PaRuX AI quota headroom and actual free bytes on the chosen destination volume. That storage-provenance slice is completed in the later 2026-09-24 evidence entry below; immutable revision-bound download identity is the current next gap.

**Progress — PARTIAL, 2026-09-24:** Hugging Face repository details now return only a validated 40/64-character hexadecimal commit SHA, and README/model-card review is pinned to that SHA. A new download cannot proceed without a valid SHA; the exact revision is carried through consent, file URL, request, status and the restart-resume catalog. Core rejects explicit invalid SHA values and URL/revision mismatches. Per-file partial sidecars bind bytes to repo/revision/file/URL, and same-destination jobs serialize; changing revision discards the old partial rather than appending bytes from another model. Legacy catalog entries without a SHA remain revision-unknown and resumable from the historical `/resolve/main/` source. An absent remote SHA-256 remains explicitly unavailable; this does not add a durable licence/integrity manifest or legal determination. Evidence: focused download/security/resume/licence tests 89/89; full `PaRuX.Server.Tests` 3,137/3,137; consent/revision and missing-revision FlaUI 2/2; App Release x64 build 0 errors (6 existing analyzer/nullability warnings); independent final review PASS. Commit: `4d23f54`. **Still open:** persist the exact licence/revision/consent evidence with completed downloads and enforce it during restart-safe validation/activation; BIC-16 remains PARTIAL and no GPU/physical-PC acceptance is claimed.

**Progress — PARTIAL, 2026-10-03:** General Chat now has an explicit 30-case held-out profile, distinct from the legacy smoke profile. Qualification retains 27/30 overall, at least 5/6 in every category, all mandatory cases, five fresh-process suite runs and 95% observed suite reliability. Independent review found four validators rejecting answers permitted by their unchanged prompts; bounded fixes advance the suite/evaluator identity to v2 and invalidate prior qualification hashes. Fresh production-suite/session diagnostics pass five times for Qwen3 (28/30 each) and Qwen3.5 (30/30 each), with the previous failed reports retained. Targeted quality/policy tests 32/32 and evidence/store/endpoint tests 85/85 pass. Evidence: `.ai/artifacts/bic-closure-20261002/general-v2-{qwen3,qwen35}.json`. These copied local models have no managed discovery/download provenance, so these diagnostic reports grant no recommendation or promotion authority. Historical-evidence compatibility, proof-bound promotion/rollback, routing and first-run acceptance remain open; BIC-16 stays PARTIAL.

**Proof snapshot follow-up — PARTIAL, 2026-10-03:** The internal General Chat comparison proof now binds both exact model identities and source/licence/integrity provenance, the full failure-inclusive evidence ledger, unique quality invocation IDs, performance records, policy, profile/workload and environments. Final checks reject runtime changes, cancellation, ledger changes and concurrent active-basename collisions; unrelated catalog rows and notice refreshes remain admissible. Deterministic regressions reproduced the provenance, cancellation, ledger and mapping defects before correction. Focused service/evaluator tests pass 66/66 and independent review passes. This immutable snapshot grants no routing or promotion authority: transactional activation/CAS, durable recommendation state, rollback and actual managed-model acceptance remain open. Evidence: `.ai/artifacts/bic-closure-20261002/p0-proof-targeted-20261003-r6.log` and `proof-snapshot-replan.md`. BIC-16 remains PARTIAL.

**Durable intent follow-up — PARTIAL, 2026-10-03:** An internal recommendation store now persists bounded, versioned Pending/Active intent with generation CAS, one prior winner and a separately labelled first-incumbent fallback. Pending masks automatic selection; corrupt/incompatible documents refuse, and failed replacement preserves the prior document. Reads are bounded to 128 KiB plus one byte; case-equivalent duplicate JSON fields are rejected. A persistent exclusive lock serializes the mutation read/CAS/replace across processes. Eight focused tests pass and independent review passes; three rounds with two actual child-process contenders each produce exactly one Pending winner and generation 1. Evidence: `.ai/artifacts/bic-closure-20261002/p0-recommendation-store-targeted-20261003-r4.log` and `recommendation-store-cas-20261003/cas-d9d9900654f54795a54a479e234f74f7/report.json`. This store is not wired to routing or endpoints and grants no live authority. Fresh activation/rollback, manual-pin serialization, evidence/runtime transaction fencing and exact evaluator/AUTO configuration agreement remain required. BIC-16 remains PARTIAL.

**Targeted acquisition preparation — PARTIAL, 2026-10-03:** Desktop-owned explicit discovery now accepts one or two safe repository IDs and combines them with the ordinary public feed in the same bounded refresh transaction, gate, cooldown and timeout. Every requested target must have matching identity, immutable revision and well-formed present metadata; failure/cancellation preserves the existing catalog and last success. Input is capped at 1 KiB; provider requests remain fixed-host HTTPS with no redirects and bounded repositories/files/responses. Browser sessions, API keys, remote desktop-token requests and an actual mapped HTTPS/private paired-token request are denied. Focused discovery/pairing tests pass 50/50; independent review passes. Evidence: `.ai/artifacts/bic-closure-20261002/targeted-discovery-reviewfix-20261003.log` and `targeted-discovery-independent-review.md`. No live acquisition or qualification follows from these tests. Model Hub input/filtering, real managed downloads/evaluation and safe promotion/rollback remain open; BIC-16 remains PARTIAL.

### BIC-01 — durable context must reflect the latest valid state

**Finding:** `src/PaRuX.Server/VirtualContextLedgerStore.cs` selects entries with `ORDER BY created_utc ASC LIMIT $limit`; `ContextEngineService.BuildVirtualContextCandidates` asks for 1,000. State/supersession is resolved only over that prefix. A correction at event 1,001 can be invisible while the superseded constraint remains active. The SQL mechanism was reproduced; a complete .NET service regression remains to be added.

**Implement:** compute a correct active-state projection from the complete relevant history, through a maintained projection or complete paginated replay. Bound the prompt projection separately from state reconstruction. Raising the cap or simply reversing the query is not a sufficient fix if supersession chains cross the selected window.

**Accept:** more than 10,000 events, old facts corrected late, repeated supersessions, contradictions, deletion/tombstone semantics, equal timestamps and restart preserve the correct active facts; retrieval respects scope and prompt budget. Measure bounded memory and update/query cost without silently discarding live constraints.

**Progress — DONE, 2026-09-25:** the maintained SQLite projection reconstructs active state independently of bounded history/prompt windows. Regression coverage exercises a late correction after 10,052 scope events, cross-scope isolation, repeated supersession, contradiction/tombstone behavior, equal timestamps, restart, audit-history retention and prompt bounds. In one Windows x64 Debug run, appending 10,050 audit notes took 19,048 ms; the late correction took 2.2 ms; the 1,000-entry history/state query took 45.9 ms; the active constraint remained present and the prompt was 177 characters. A separate 10,000-live-constraint query returned all 10,000 facts with a 4,179-character prompt in 628.9 ms, allocating 30,572,504 bytes against an explicit 134,217,728-byte query-allocation budget (observed process-private delta 5,791,744 bytes). The 10,052-event run allocated 85,261,624 managed bytes and observed 7,798,784 additional process-private bytes, sampled every 1,000 appends. These are one-run scale smoke measurements, not p95 or a general performance claim. Verification: `VirtualContextLedgerProjectionTests` 6/6; detailed measurements retained in ignored `.ai/artifacts/bic01-ledger-active-scale.trx`.

### BIC-02 — audit benchmark changes against an immutable starting commit

**Finding:** `scripts/benchmark-coding-agent-competitors.ps1`, `Get-ChangedPaths`, compares against the agent's current `HEAD` and includes untracked paths. Once the agent commits an out-of-scope edit, a clean tree hides it from that audit. An isolated Git reproduction confirmed the defect.

**Implement:** pin the initial commit before the agent starts; audit committed changes since that point, index, working tree and untracked files, including renames/deletions. Keep the verifier, allowed-path policy and evaluation corpus outside the agent's writable authority or validate them against independent immutable copies.

**Accept:** an agent committing an unauthorized edit is rejected even with a clean worktree; staged-only, untracked, renamed, deleted and multi-commit edits are covered. Scope and verifier tampering fail explicitly. Legitimate scoped commits still pass and results retain the initial/final source identities.

**Progress — DONE, 2026-09-25:** the immutable-baseline audit accumulates path changes from every commit after the pinned start (including merge-parent diffs), then adds staged, unstaged and untracked paths. Its self-test covers multiple unauthorized commits, a forbidden file committed then removed in a later commit, staged-only/untracked edits, both ends of a rename, and deletions. It also exercises the production acceptance path: a clean committed in-scope change passes with initial/final commit identities retained, while a later committed out-of-scope change is rejected with a clean final tree. Corpus and verifier/harness mutations are each rejected by the hash guard. Running the self-test exposed and fixed PowerShell startup defects (unescaped `:` after `$caseId`, `$PSScriptRoot` evaluated in parameter defaults, and empty verification-record array binding); the self-test resolves default paths from the script directory and passes when launched from `scripts/`. Verification: `powershell -ExecutionPolicy Bypass -File benchmark-coding-agent-competitors.ps1 -ScopeAuditSelfTest` → PASS.

### BIC-03 — measure solved work and prevent false competitive wins

**Finding:** `benchmarks/coding-agent/parux-corpus.json` contains five small smoke tasks, insufficient for a general coding-quality claim. The competitor runner can declare a winner on equal success rates, including 0/5 versus 0/5, by comparing median time over all cases, rewarding fast failures.

**Implement:** retain smoke tests and add a versioned 30–50-task evaluation spanning meaningful multi-file fixes/refactors, C#/WinUI, C++/Qt, Python and web projects, migrations, long context, interruption/resume, recovery and UI/artifact quality. Separate held-out evaluation from optimization tasks. Require a predeclared absolute success floor and mandatory correctness tasks before any superiority claim. Compare latency on common successful tasks, report unmatched successes/failures separately, and treat missing/invalid output as failure or INCONCLUSIVE as appropriate.

**Accept:** 0/5 cannot win; a faster failed/truncated answer cannot win; equal success counts with different solved-task sets are INCONCLUSIVE, not a latency win; modified verifiers or incompatible model/reasoning/output/permission budgets cannot silently pass fairness checks. Record corpus/verifier hashes, product versions, model/revision/configuration and intervention counts. Use independent outcome checks, meaningful regression tasks and repeatable comparison settings; never lower acceptance or tune the holdout to manufacture a win. Response-speed comparisons also require paired runs with the same model/revision/tokenizer, exact system/developer/user instructions, tool schemas, sampling/output limits and local context/runtime settings; compare submit-to-first-visible-token and submit-to-last-visible-token latency separately, with cold/warm state and repeat distributions reported. A speed win is valid only on equivalent successful outputs that meet the same predeclared quality floor.

**Progress — PARTIAL, 2026-09-26:** Pairwise verdicts now compare latency only after both providers have identical successful-task sets when success counts tie. Equal counts with different task identities are INCONCLUSIVE, with shared and provider-only successes retained in evidence. A deterministic verdict self-test covers below-floor/zero-success, disjoint success sets, different equal-sized sets despite a faster shared task, and both latency directions over identical success sets. The local release gate runs this self-test. Verdict and scope/immutable-input self-tests pass under PowerShell 5.1 and 7; the corpus contract test passes 1/1; independent diff review PASS. A full real provider/model comparison remains outstanding, so no competitiveness claim is made.

**Response-latency measurement follow-up, 2026-09-26:** the real reference-stream harness now records request-submit-to-first-visible and request-submit-to-last-visible content separately, reports median and p95 for both, and includes these fields in the local-runtime competitor wrapper. `AssertProvider` now checks both response endpoints plus decode throughput and refuses fewer than 20 repeats; smaller runs are explicitly labeled smoke-only. Deterministic plain/reasoning/stall/smoke-gate/single-provider fixtures pass **5/5**. Exact model-file/revision and runtime-setting parity, alternating provider order, independent quality scoring and the live same-machine comparison remain required before any best-in-class claim; no live provider was contacted for this change.

**Paired response-speed scheduling, 2026-09-26:** the local-runtime competitor wrapper now sends all selected chat providers through one reference-stream run; the reference harness first records each provider's initial request, then issues repeated requests serially in rotating round-robin order and records each sample's round/position. The wrapper can select provider-advertised model IDs explicitly and applies the same user/system/developer text, tool-free request shape, temperature, top-p, penalties, reasoning effort and output budget to every endpoint. Both reports label the shared model identity as caller supplied and explicitly say weight/revision, tokenizer, template and provider-side runtime parity are not independently verified. Incomplete providers remain failed/unreachable and cannot enter the latency gate; a synthetic failure after one successful repeated sample confirms the report retains that partial evidence but the competitive gate rejects the provider. The reports also disclose that the first request is not a proven cold load, cache state is not controlled, and both resident models must fit without paging/swapping. Synthetic ordering, plain/reasoning/stall, paired-gate, mid-run-failure and wrapper-level pairing fixtures pass **9/9** under PowerShell 7 and Windows PowerShell 5.1. Twenty repetitions are a minimum sample-count floor only, not a confidence guarantee. This reduces order and request-profile mismatch, but exact artifact/runtime parity, independent quality scoring, controlled cold/warm state and a real same-machine performance winner remain unverified; BIC-03 remains PARTIAL.

**Response-speed parity and evidence gate, 2026-09-26:** the reference harness requires a fresh operator-attested manifest for any winner assertion, validates exact JSON integer/range fields, hashes the manifest snapshot and model artifacts before measurement, then re-hashes both after measurement; any change invalidates the evidence. It enforces matching model bytes, API IDs, declared runtime/template fingerprints, context, concurrency and warm-up, while clearly retaining the trust boundary that the common API cannot prove a third-party process loaded those bytes. A speed win now requires at least 20 paired rounds, a paired 95% percentile-bootstrap lower confidence bound (compared at full precision) meeting the separate minimum visible-latency gain (default 2%, minimum 0.1%) for median TTFT or median last-visible latency, and no more than the separately configured regression tolerance (0–5%) on TTFT, p95/last-visible latency and exact decode throughput. Equal/noisy results remain failures or inconclusive; reports are written before returning a failed assertion. Synthetic fixtures cover paired winner/no-winner including zero regression tolerance, the 1.995%-vs-2% confidence boundary, integrity mutations during a run, invalid/missing manifest data and wrapper propagation: **27/27 PASS** under PowerShell 7.6.6 and Windows PowerShell 5.1. A read-only 1.9.4 environment check still did not establish an exact live provider/model mapping, and no real model comparison or fresh speed/quality claim has been made. The gate is speed-only; successful-output quality/reliability and resource parity must still be adjudicated separately. `docs/PERFORMANCE_BENCHMARKING.md` records protocol and limitations. BIC-03 remains PARTIAL.

**Progress — PARTIAL, 2026-10-25 (corpus v2):** `benchmarks/coding-agent/parux-corpus-v2.json` (schemaVersion 5, corpusVersion 2.0.0) adds 36 self-contained tasks (C# 10, Python 12, TypeScript 6, JavaScript 8; bug fix / feature / refactor / test writing) split 21 held-out / 15 optimization, each with a deterministic generated fixture, an independent hidden verifier kept outside the fixture, a separately stored reference solution, time and step budgets, and a stated tuning rule. `corpus-v2/corpus_tool.py` provides `check` (ids, hashes, split counts, verifier/leak checks), `selftest` (fail-before/pass-after on this machine; missing toolchains are SKIPPED, never passed) and `build`. `benchmark-coding-agent-competitors.ps1 -TaskCorpusV2` runs the same tasks/budgets/verifiers through pluggable CLI adapters or a documented manual protocol for GUI apps (operator-attested, never upgraded to harness-measured); WIN/TIE require the full held-out split, zero interventions, enforced step budgets and identical measurement method. No competitor run has been performed; no superiority claim exists.

### BIC-04 — one hardware inventory for routing and memory

**2026-10-25 progress (status PARTIAL):** free VRAM is one fact: `RuntimeManagerStatus.VramFreeMib` (null = unknown, 0 = measured full), context admission now also credits measured AMD/Intel discrete headroom, `LaunchHardware.From` plans unknown free VRAM at 50% of dedicated (measured 0 stays 0; nvidia-smi 0 MiB no longer read as unknown), Vulkan launches re-sample before planning, and every Gaming transition (SetGamingState, EndGaming, hard-off) invalidates the shared telemetry cache with a generation guard against in-flight stale samples. Fake-sampler tests `FreeVramTuningTests` 8/8. No physical AMD/Intel discrete run.

**2026-10-03 progress (status PARTIAL):** S1/S2 landed (`1da4beb`..`05a2d82`, independent review PASS). Evaluation identity is now `hw2-sha256` (per-adapter vendor, device, dedicated VRAM, driver version; CPU registry identity; active processor count across groups; OS major.minor.build, no UBR; backend-scoped: `text-cuda` hashes NVIDIA adapters only) plus `rtv2-sha256` (runtime exe and every sibling native library fully hashed, cached by file identity). All prior evidence is invalidated and fails closed. AMD/Intel discrete free VRAM = min(DXGI budget, dedicated) − max(PDH system-wide dedicated usage, process usage); missing or ambiguous data gives 0; sampling is bounded and only runs when such an adapter exists. Real probe: a llama-server child on the RTX 5080 was invisible to DXGI per-process usage (0) while PDH tracked nvidia-smi (1,638 vs 1,634 MiB). Remaining: no real AMD/Intel discrete hardware evidence; callers still on the old `BuildGpuFingerprint` identity (coding-model selection, launch optimizer, prompt-cache policy, engine coordinator, engine race) are out of scope for now; budget minus system usage may under-report under memory pressure.

**2026-10-04 BIC-04 S3/S4 (independent review PASS after one fix round; status PARTIAL):** Gaming hard-off now cancels in-flight work, not only new work: the tuner, llama-bench, backend/build/engine races and model, dataset and ExLlamaV3-pack downloads stop when the Gaming marker appears (model/dataset partials kept and resumable, shown as "paused for Gaming"). Release failures throw and `GamingGuardService` retries every 2 s. Automatic resume follows the explicit `RestoreAiOnExit` profile policy (default off); remote grace expiry and failed-entry rollback follow it; the orchestrator "Return to AI" reloads eagerly while the Settings and new chat-header "Return to AI" buttons lift the hard-off and load lazily. Chat refusals say "AI paused by Gaming — Return to AI". Live on this PC: Gaming released 14,326 MiB of VRAM within 137 ms; a running llama-bench tune was cancelled in ≈ 100 ms with no profile saved; llama-bench on the Radeon 780M via Vulkan ran at 25.3 tok/s with no RTX 5080 use. Full suite 4,269/4,269; FlaUI Return-to-AI test. Open: a VRAM-only pressure refusal was not isolated live (the observed refusal was RAM-based); no AMD/Intel discrete hardware; no Settings toggle for `RestoreAiOnExit`; no automatic game-exit detection (documented).

**Finding:** `RuntimeManagerService.GetStatus` derives `GpuDetected` from NVIDIA-probed VRAM being at least 12,000 MiB. `ContextMemoryBudget.For` consequently treats other profiles as CPU-only even though the Vulkan path is already working on the Radeon 780M.

**Implement:** reuse/unify `GpuInventoryService` and runtime capability reporting for CPU, NVIDIA, AMD, Intel and supported execution providers. Separate device presence, backend eligibility, dedicated VRAM, shared memory and currently usable budget. Avoid counting shared RAM twice. Routing, Model Hub fit, tuning and context budgeting must consume consistent facts; keep expensive discovery off the request hot path.

**Accept:** behavioral profiles for CPU-only, 780M/shared RAM, NVIDIA below/above 12 GB, and eligible AMD/Intel devices; runtime selection and context limits remain safe under memory pressure, model swaps and Gaming Mode. Physical performance and actual device execution remain separate checks, not inferred from enumeration.

**Progress — PARTIAL, 2026-09-26:** the normalized Core accelerator profile is transported additively through Runtime Manager and consumed by Model Hub fit and the production first-run recommendation endpoint. Physically detected hardware remains distinct from installed-backend eligibility; absent backends cannot contribute GPU fit. UMA capacity uses one shared-pool estimate, capped by conservative usable RAM (reserve max(8 GiB, 25%), including the 16 GiB boundary); the same cap now governs context budgeting and unknown RAM fails closed. Shared memory is never added again as a second RAM pool. Regressions cover 780M-style fit, shared-pool overcommit, CPU-only fallback with visible hardware, sub-12-GiB legacy VRAM eligibility, onboarding and the HTTP contract; targeted server tests **51/51**, App Debug x64 build **0 errors** (6 analyzer/nullability warnings), shared-memory/ineligible-GPU Model Hub FlaUI **1/1**, `git diff --check` clean, independent review **PASS**. **Still open:** dynamic currently-available RAM/VRAM admission across runtime load/context/tuning, memory-pressure/model-swap/Gaming Mode integration, live AMD/NVIDIA hardware acceptance and physical execution evidence. BIC-04 remains PARTIAL.

**Regression follow-up, 2026-09-26:** overlapping `Loaded`/`OnShown` Model Hub refreshes and cancelled refreshes could still publish late results or clear a newer busy state; the shared-memory hardware fixture and BIC-16 first-model UI fixture exposed this intermittently. Refresh generations now gate every state/error/finally commit; unload and DEBUG fixture application invalidate/cancel pending refresh and search work. `OnShownSearching` also abandons its continuation when refresh ownership changes, and async search results/errors/busy cleanup are generation- and cancellation-guarded. A blank-query FlaUI check verifies the visible idle status (**1/1**); the two affected fixture tests passed together **2/2**; full `Category=Regression` passed **24/24** (`.ai/artifacts/bic04-modelhub-refresh-regression-20260926.log`). This addresses the observed fixture/order race; deterministic delayed-provider race injection and live hardware pressure/model-swap/Gaming integration remain open. BIC-04 remains PARTIAL.

**Progress — PARTIAL, 2026-09-26 (dynamic available RAM admission):** OS RAM is sampled once and the same validated snapshot now drives Runtime Manager status plus both context-budget endpoints; invalid/inconsistent samples fail closed. Admission uses currently available system RAM plus only the known managed text-child working set reclaimed by the coordinated transition, bounded by the established profile reserve/cap. Standard/Uncensored explicit model loads pass the checked exact path through launch (and select a bundled vision projector only for its matching model); automatic role ensure keeps the small-model fallback on hosts without a heavy GPU and checks that same path. The final gate requires the caller's exact active writer lease and drains coordinated readers, direct llama slots and alternative engines before the live-resource check; Group Chat and Model Compare share this path. Role persistence remains after admission, so a denied swap leaves the prior assignment intact. Verification: focused runtime/context/compare/group-chat/mobile selection **75/75**; full `PaRuX.Server.Tests` **3,452/3,452**, zero skipped, 6m05s; Server Release x64 build **0 errors** (15 existing analyzer/MCP/nullability warnings); `git diff --check` clean; independent final review **PASS**. **Still open:** dedicated VRAM is still based on detected total rather than a validated live-free sample; tuning, memory-pressure and Gaming transitions still need end-to-end integration; deterministic delayed-provider Model Hub race injection and live AMD/NVIDIA hardware/model execution are unverified. This closes only current system-RAM admission for the covered model/context routes; BIC-04 remains PARTIAL and release stays blocked.

**Progress — PARTIAL, 2026-09-26 (dynamic free-VRAM admission):** Final runtime admission now uses a fresh NVIDIA device-wide `memory.free` sample matched by exact adapter name and compatible memory total; it does not infer correspondence from CUDA/DXGI indices. The exact managed-Candidate load follows this strict check after coordinated readers are drained, while SmartRouter retains its quick indicative estimate when ranking candidates and `LoadRole` uses that estimate only as a preflight before the final live check. Both context GET/PUT paths share the same 1-second cached sample and offer no context capacity for a discrete GPU if free VRAM is unknown/zero, rather than presenting a misleading RAM-only option. The chat execution guard returns a cached sample without waiting for `nvidia-smi`; stale/missing data refreshes in the background, capped at 2 seconds, keeping process launch off the per-request wait path. Explicit strict VRAM guards (e.g. training) await a fresh cached/refreshed sample. Discovery honors cancellation, retries a missing utility after 30 seconds, and failed/timed-out child queries are terminated. Targeted runtime/context/telemetry/SmartRouter/resource-guard tests: **57/57 PASS**, including exact-candidate admission math, backend executable detection, negative-cache retry/cancellation, strict training telemetry, unknown-VRAM context rejection, and a direct regression proving chat admission completes while GPU refresh is still pending. Full `PaRuX.Server.Tests`: **3,466/3,466 PASS**, zero skipped, 5m42s (the only later change was that extra test). Release x64 Server build: **0 errors**, 13 existing analyzer/MCP/nullability warnings; `git diff --check` clean; independent delta review **PASS**. A read-only RTX 5080 sample (16,303 MiB total / 15,878 MiB free) was only a telemetry smoke check, not a model-load acceptance or performance benchmark. **Still open:** trustworthy live-free telemetry for AMD/Intel discrete adapters, reliable per-process GPU attribution/transactional swap recovery (on rejection with a resident model, UI now directs the user to unload it before retrying), live swaps under pressure, tuning/Gaming Mode integration, and physical model-load acceptance. This tranche makes no end-to-end latency or universal best-in-class claim; response-speed comparison remains subject to the exact-parity request-to-first-visible/request-to-final-visible methodology in BIC-16. BIC-04 remains PARTIAL.

**Progress — PARTIAL, 2026-10-18 (CPU-only robustness):** every nvidia-smi invocation now has a 1.5 s deadline enforced by the caller; a hung process tree is killed and no longer stalls chat or gaming transitions. The VRAM probe runs outside the gaming state lock; a failed or timed-out probe is cached for the TTL and logged once per TTL, and caller cancellation is never cached as "no GPU". Gaming suspend and restore read per-process GPU memory from nvidia-smi compute-apps and report unknown (`null`) rather than working-set RAM; the unload label says CPU only when no usable adapter exists. The text launch and llama-bench build CPU arguments directly (`-ngl 0`, no device pin) only when DXGI reports no usable adapter; CUDA and adapter-bearing hosts keep their arguments. ExLlamaV3 install, serving and Model Hub offering require a DXGI NVIDIA adapter with dedicated VRAM. `scripts/benchmark-gpu.ps1 -CpuOnly` refuses to start unless the running llama-server shows `--gpu-layers 0` and no device pin, and GPU-only results carry `PHYSICAL_GPU_TEST_REQUIRED`. Verification: focused filtered tests **207/207** (baseline 158/158); full `PaRuX.Server.Tests` 4626 of 4635 results passed, and the failing environment-dependent results reproduce on a pristine copy of the base commit. The Windows host used for this pass exposes only an AMD Radeon 780M adapter, so the CPU-direct path does not trigger there and that host keeps its Vulkan launch. No physical CPU-only host, NVIDIA card, runtime launch or model load was exercised. BIC-04 remains PARTIAL.

### BIC-05 — Ollama compatibility must preserve semantics or reject explicitly

**2026-10-25 progress (status PARTIAL):** `CompatErrorMatrixTests` (63 cases, fake upstream) cover invalid JSON, wrong types, oversize (48 MiB cap, surface-shaped 413), unknown model, backend 4xx/5xx/timeout/reset, pre-byte stream errors, mid-stream reset and silent EOF across /api/chat, /api/generate, /v1/responses, /v1/messages and /v1/chat/completions. Fixed: Ollama passed OpenAI-shaped error bodies through (now `{"error":"msg"}`); Responses/Messages mid-stream failures appended JSON to the SSE stream or aborted (now `response.failed` / `event: error`); chat completions dropped mid-stream failures silently (now a terminal error frame); a clean EOF without finish_reason/[DONE] is a failure, not a completion. Real-client and live-inference verification still open.

**2026-10-03 progress (status PARTIAL):** commit `a7ec0b6` (independent review PASS). Real-client acceptance with the official `ollama` Python SDK 0.6.3 against the integrated binary: 16/16 (list, AUTO buffered cold, exact generate, JSON schema, AUTO stream, tool-call round trip, client cancel and recovery, explicit 400 for unsupported features, unknown model, malformed bodies, text-only image rejection, ps/show, backend killed mid-stream with recovery in 6.3 s); vision 5/5 on the bundled vision pairing (read-only local model). Fixed: the gateway always sent the logical `local-default` id upstream, so every protocol returned 400 while a resident multi-model pool was active (now resolved through the pool alias, same helper as chat); non-object bodies and non-string embed models returned 500 (now 400); a text-only model given an image returned 500 (now 400). Full suite 4,136/4,136. OPEN: the earlier 90 s AUTO buffered-request timeout did not reproduce after the fix (three cold/warm/two-model runs ≤ 8 s); its cause is unproven — add explicit upstream timeout logging with elapsed time and resolved provider model. Also open: a downloaded vision model plus projector needs managed projector pairing; mid-stream backend death truncates the chunked response instead of emitting a typed NDJSON error frame; 4B models report no capabilities in `show`.

**2026-10-06 progress (BIC-05 streaming errors & timeout diagnostics, independent review PASS; status PARTIAL):** (1) `LocalApiServerService` adds typed `Stopwatch` elapsed timing and `OperationCanceledException when (!cancellationToken.IsCancellationRequested)` handling returning HTTP 502 with explicit error diagnostics: elapsed milliseconds, requested model and resolved provider model (e.g. `Upstream request timed out after {elapsed}ms for model "{requested}" (resolved: "{resolved}")`). (2) `OllamaStreamTranslator` implements `WriteErrorAsync` generating typed terminal NDJSON error frames (`{"model":"...","created_at":"...","done":true,"done_reason":"error","error":"..."}`). In `OllamaCompatibilityEndpoints`, mid-stream provider failures with partial output already written route through `WriteErrorAsync` instead of abruptly closing or throwing `InvalidOperationException` over committed headers. (3) `/api/show` endpoint now explicitly reports model `capabilities: ["completion", "chat", "tools"]`. (4) `OllamaCompatibilityIntegrationTests`: 43/43 PASS (100% green, 13s) including new tests for mid-stream failure NDJSON error emission and upstream timeout diagnostics. Independent security/runtime review PASS. BIC-05 remains PARTIAL pending external live-client verification on the integrated binary.

**2026-10-24 progress (status PARTIAL):** `/api/show` vision no longer depends on the single-winner name category (4B vision models with a managed paired projector report `vision`; reasoning-named models report `thinking`); `/api/embed` and `/api/embeddings` now reject non-null `truncate`/`options`/`keep_alive`/`dimensions`/unknown fields with an Ollama-shaped 400; table-driven tests cover every chat/generate/embed field (reject or forward). Real-client and live-inference verification still open.

**Finding:** `src/PaRuX.Server/OllamaCompatibilityEndpoints.cs` now has an explicit versioned capability contract and maps buffered tools, tool results, bounded base64 images, JSON/JSON Schema, boolean thinking, supported sampling/logprob options, response tool calls, usage and streaming NDJSON. Some Ollama behaviors remain deliberately unsupported (including streaming tools, `keep_alive`, thinking levels and unsupported fields/options); the adapter must reject these rather than silently downgrade them. Unit/semantic tests alone did not prove the mapped contracts through the registered HTTP routes.

**Implement:** define a versioned supported-capability contract and implement each advertised request/response field end-to-end. Translate model-supported tool calls/results, images and structured output correctly; preserve streaming NDJSON, finish reasons and valid usage/timing metadata. For unavailable capabilities return a clear unsupported error instead of silently discarding user intent.

**Accept:** real compatible clients complete supported chat, streaming, tools, vision and JSON/schema flows; cancellation, malformed requests, backend errors and unsupported features are explicit. Use protocol integration tests and record actual provider limitations. Audit the other advertised compatibility surfaces for the same silent-drop pattern without rewriting working paths unnecessarily.

**Progress — PARTIAL, 2026-09-26:** Ollama request normalization now rejects malformed `model`/`stream`/generate `raw` and `suffix` fields, role-inappropriate message fields and unknown nested tool/function fields; it preserves supplied call IDs while rejecting duplicate IDs and preventing collisions with synthesized IDs. Adjacent `/v1/responses` and `/v1/messages` normalizers now reject unknown top-level/nested fields, unsupported roles/content blocks, invalid field types and conflicting text payloads instead of defaulting, skipping or silently downgrading them. Supported image `detail` is preserved and validated; Anthropic tool-choice names/types and unsupported error-marked tool results fail explicitly. Added deterministic ASP.NET route tests proving buffered Responses and Anthropic request/response translation plus pre-provider rejection of unsupported fields, and semantic regressions for the edge cases above. Focused compatibility selection: **64/64 PASS**; full `PaRuX.Server.Tests`: **3,435/3,435 PASS** (5m47s); `git diff --check` PASS; independent final diff review **PASS**. **Still open:** interoperability with an external Ollama SDK/client and actual local provider/model acceptance. Current HTTP integration still uses a deterministic provider seam; no external-client, model-capability or real-inference claim is made. BIC-05 remains PARTIAL.

**Progress — PARTIAL, 2026-10-02:** the official Ollama Python SDK 0.6.3 ran against an isolated real Core/provider with a copied Qwen3-4B GGUF. Warm, explicitly routed `parux-main` acceptance passed **8/8**: discovery, exact buffered arithmetic, JSON Schema, terminal streaming frames, buffered tool call/result roundtrip, client cancellation followed by a correct new answer, and explicit HTTP 400 for unsupported residency and streaming tools. Evidence: `.ai/artifacts/bic-closure-20261002/live-core/ollama-warm-result.json`. The initial AUTO buffered request timed out after 90 seconds and remains recorded separately; warm success does not explain that failure. This binary comes from `4f35240`, so repeat against the integrated final source before closure. Actual vision-provider acceptance and complete malformed/backend-error coverage remain open; BIC-05 stays PARTIAL.

### BIC-06 — repair navigation in the live desktop layout

**2026-10-06 FlaUI suite stabilization and folder picker fallback (status PARTIAL):** (1) `CodeHandoffService` now provides an in-process Win32 `SHBrowseForFolder` fallback on a dedicated STA thread with `OleInitialize`/`OleUninitialize` and `CoTaskMemFree(pidl)` when WinRT `FolderPicker.PickSingleFolderAsync()` fails with COMException `0x80070578` (invalid window handle) in isolated/sandboxed desktop sessions. (2) `SmokeTests`: `Simple_chat_code_folder_picker_opens` searches desktop/modal windows for native `#32770` dialog (10s PASS); `Model_assigned_to_standard_remains_selectable_for_uncensored_role` uses UIA category navigation instead of raw click (10s PASS); `Chat_header_adapts_and_recent_history_is_bounded_when_the_window_is_resized` wraps keyboard events with `Win32Exception` (ERROR_ACCESS_DENIED on virtual desktop `SendInput`) fallback (1m09s PASS); full `SmokeTests` suite **59/59 PASS** (100% green, 10m54s). (3) `ExplicitDiscoveryTests`: Escape handled with dialog Cancel fallback; fast public metadata completion supported (**7/7 PASS**, 2m43s). (4) `NavigationLayoutReachabilityTests`: narrow 560×560 layout calls `ScrollItem.ScrollIntoView()` before `IsOffscreen` check, ensuring all workspaces reach pass status under automation (**1/1 PASS**, 1m55s). Additional UI suites verified green: `CandidateScopeTests` (1/1), `CreateEditModeTests` (1/1), `RecommendationActionsTests` (2/2), `BootstrapAuthStubServerHttpTests` (2/2), `CodingVerificationHonestyTests` (2/2), `CreateWorkspaceSpeechCancellationTests` (2/2), `NavigationRestartTests` (2/2). Independent code & security review PASS. BIC-06 remains PARTIAL pending owner physical-PC DPI 100/125/150% sweep and MSI lifecycle validation.

**2026-10-04 progress (status PARTIAL):** `7d49a11`. New FlaUI `NavigationLayoutReachabilityTests`: at narrow (560×560), compact (800×720) and wide windows, in Simple (4 items) and Advanced (11 items) modes, every workspace in `MainWindow.xaml` is visible inside the window, selectable by real mouse click and by keyboard traversal, Chat home returns, and the collapsed rail (hamburger) at tiny/compact sizes reaches all 11 items. 5 tests ×3 runs; full Regression suite 76/76 on the published layout. No product defect found. Harness finding: FlaUI zero-hold key presses wedge NavigationViewItem keyboard activation after first use (tests hold keys ≈ 120 ms). No minimum window size exists; below ≈ 400 epx the pane overlaps content until collapsed. This display runs at 200% (192 DPI), so widths above ≈ 640 epx and other scale factors are untested. Open: DPI 100/125/150% sweep (requires the owner to change Windows display scaling or authorize it), MSI-installed run, physical-PC acceptance.

**Finding:** the current WinUI `NavigationView` already owns one native scroll surface, with Advanced workspace items before Recent conversations; `ApplyNavigationMode` does not create a separate footer list. Two remaining software/usability risks are the reduced-width Chat header truncating its title/model status and unbounded recent-history item creation pushing navigation targets down. Physical-PC notes still require packaged, DPI and restart acceptance.

**Implement:** fix layout ownership/measurement, bound or virtualize conversation history, and keep all workspaces reachable with visible scrolling or navigation. Preserve selection/history and avoid fixes that only make UI Automation able to invoke an otherwise invisible control.

**Accept:** packaged WinUI interaction at representative window sizes and DPI, long history, keyboard navigation, Normal/Advanced switches, restart and resize; all workspace entries are reachable and Chat remains home. Source-only tests cannot close the visual acceptance row.

**Progress — PARTIAL, 2026-09-26:** the Chat header now stacks its title and profile selector below a measured width threshold, keeping profile controls horizontally scrollable; the generated recent-history navigation list is bounded to 50 matching chats while preserving an already-open older matching chat, and full history remains searchable. The isolated FlaUI scenario seeds 120 chats (including a unique visible message in chat 0119), checks the 50-item notice, resizes the actual WinUI window between narrow and wide desktop sizes, verifies title/profile controls stay in-window, and confirms every workspace can be scrolled into view in Advanced mode. It proves Up/Down keyboard focus traversal across all workspace entries, Space activation and visible Create content, return to Chat as home, Simple/Advanced switching, then searches beyond the 50-item recent bound, clicks the visible result through the real navigation UI, and verifies the selected chat's own message renders. The history-limit notice is separately scrolled into view. Evidence: focused BIC-06 FlaUI plus `Settings_is_reachable` and `Workspace_navigation_exposes_and_clears_the_selected_workspace_state` **3/3 PASS** (`.ai/artifacts/bic06-navigation-final-click-20260926.log`); the earlier full `Category=Regression` rerun was **21/23** due to separate Model Hub hardware/first-model fixture timeouts. After adding refresh/search generation ownership guards, the full Regression suite passed **24/24** (`.ai/artifacts/bic04-modelhub-refresh-regression-20260926.log`). App Debug x64 build **0 errors** (existing analyzer/nullability warnings); `ActiveReleaseStatusContractTests` **5/5**; `git diff --check` clean. FlaUI artifacts are under ignored `TestResults/UIArtifacts/`. **Still open:** packaged/restart acceptance, multiple DPI/scaling configurations, physical-PC visual acceptance. The history row limit bounds NavigationView elements, not HistoryStore loading or filtering work. BIC-06 remains PARTIAL.

**Progress — PARTIAL, 2026-10-02 (persisted desktop restart):** the Release 1.9.5 candidate passed long-history/narrow-wide/navigation and managed-server recovery tests **3/3**. A new FlaUI test terminates only its isolated desktop process (window Close intentionally parks in the tray), restarts against the same project-local data root without reseeding, and verifies Chat home, the persisted Advanced choice and all 120 conversation rows. New restart test **1/1**, combined targeted recovery **3/3**, full Debug fixture UI regression **48/48**, zero skipped; independent delta review **PASS**. UI state is kept under ignored `.ai/artifacts/ui-data/` with short generated paths; the first longer project-local path exposed a Windows Git path-length failure, corrected before rerunning. The Debug regression requires the adjacent published server; an initially misconfigured Release-fixture run and the subsequent setup failures remain in the evidence logs. Evidence: `.ai/artifacts/bic-closure-20261002/`. This is abrupt desktop restart plus current-machine layout evidence, not MSI lifecycle or multiple real DPI/scaling acceptance; those remain open and BIC-06 stays PARTIAL.

### BIC-07 — link source, artifacts and a usable update channel

**2026-10-04 progress (status PARTIAL):** `4489a71`..`b25a015` (independent review PASS; findings closed). Candidate version 1.9.6 (Android versionCode 16). `publish-android-apk.ps1 -LocalOnly` builds without any GitHub side effect; `PaRuX.ReleaseTool` resolves linked worktrees correctly. `build-msi -Release` records the SHA-256 of every harvested payload file plus `buildMode: release` / `sourceTreeClean: true` in provenance; `verify-release-assets.ps1` extracts the MSI administratively (no install) and requires its payload — and an optional publish directory — to equal the provenance exactly, requires the embedded commit (≥ 12 hex) in the primary binaries inside the MSI, verifies the manifest envelope against the committed trust anchor, binds the CycloneDX SBOM hash, checks the APK's own versionName and a monotonic versionCode, and writes a manifest only after every check passes (28 verifier scenarios on PowerShell 7 and 5.1). A provisional candidate (MSI with the real manifest key, APK with the real keystore, SBOM with 79 components) passes full verification; it must be rebuilt from the final commit. The previous 1.9.5 MSI is preserved for the upgrade test. Authenticode: BLOCKED-EXTERNAL (no code-signing certificate; MSI shows an unknown publisher; the update envelope is signed). Open: elevated MSI lifecycle (1.9.4 installed → 1.9.6, 1.9.5 → 1.9.6, downgrade refusal, UpdateHelper, uninstall/reinstall), local tag for `-RequireTag`, S5 real feed discovery (needs the public repository).

**Finding at the audit snapshot:** v1.8.0 points to `c12934156c717fe8c9e787ef371de76f4eb05c06`, while audited main is one commit ahead. Later MSI upload time/hash does not identify its embedded source commit. The release has MSI and checksums, but no APK or signed `PaRuX-1.8.0-win-x64.update.json` envelope. `AppUpdateClient.cs` requires the envelope for secure installation; its unauthenticated GitHub request can report a private-repository 404 as a successful check with no published release.

**Implement:** produce reproducible build metadata linking commit, product version, artifact hashes and validation evidence; add local packaging/release checks for the intended MSI/APK/envelope set. Make the update channel accessible to intended users using an appropriate authenticated or public distribution design, without embedded shared credentials. Distinguish inaccessible/unauthorized/unknown from verified absence of an update; a GitHub 404 alone cannot disambiguate a private repository. Preserve signature verification and rollback/downgrade protections.

**Accept:** offline/local build tooling validates metadata and asset completeness; signed update discovery/download/verification/install and refusal of tampered/wrong-version artifacts are exercised on an actual candidate. Upgrade from a previous version and downgrade rejection remain explicit rows. Never infer binary provenance from timestamps, replace maintainer signing keys, move historical tags or claim release readiness without evidence. Missing signing credentials are BLOCKED-EXTERNAL for the relevant publication step, not an excuse to leave source checks or feed error handling unimplemented.

**Progress — PARTIAL, 2026-09-26:** added a Windows PowerShell regression harness for `scripts/verify-release-assets.ps1` and wired it into the local release gate. Seven isolated synthetic scenarios cover a consistent Windows asset set, modified MSI bytes, wrong source commit, duplicate provenance entries, a consistent Windows+Android set, modified APK bytes and a missing required APK. The test stubs only the `dotnet`/`apksigner` executable boundaries; actual signed-envelope cryptography remains covered by `SignedAppUpdateChannelTests`, and no release key, real artifact, network or GitHub write is used. The test passed under PowerShell 7.6.6 and Windows PowerShell 5.1. **Still open:** actual candidate/MSI/APK signature and installation/update acceptance, previous-version upgrade and downgrade rejection on the intended installation, and authenticated/public-feed behavior with the release intended for publication. BIC-07 remains PARTIAL.

### BIC-08 — ONNX cancellation, residency and capability honesty

**2026-10-03 progress (status PARTIAL):** `7f4cd26`/`d55c5dc`/`f2833d2` (independent review PASS). CPU (Arm Qwen3-0.6B INT4, Apache-2.0) and DirectML pinned to the RTX 5080 (Phi-3-mini-4k DirectML int4, MIT) run through the production engine with the real worker: warm cancels drain in milliseconds with output identical to the temperature-0 baseline, killed workers recover, single-flight is kept (two concurrent requests ≈ 2× one). CPU warm TTFT ≈ 320 ms vs ≈ 2.4 s forced restart; DirectML warm TTFT ≈ 420 ms, ≈ 145 tok/s. Fixed: every `thinking=false` request on Qwen3-style templates failed because the GenAI template engine cannot evaluate `enable_thinking is false`. DirectML is an explicit adapter-pinned variant (`adapter=vvvv:dddd` in the runtime identity); because ONNX Runtime GenAI 0.15.2 ignores device selection, the worker proves from its own per-adapter video memory that the pinned adapter holds ≥ 50% of the on-disk weights (observed ratio ≈ 1.03) and otherwise refuses; a 780M pin fails closed. Thinking control is a per-model capability; a `thinking=false` demand on a model without it is rejected. Built-in CPU/DirectML providers never appear in status/discovery/AUTO. NPU: unsupported profile (AMD NPU present, no Windows ML NPU execution provider) — BLOCKED-EXTERNAL for that sub-criterion only. Open: 780M adapter selection (needs a newer GenAI), a DirectML model with a thinking control so AUTO evidence is possible, a real TensorRT-RTX run.

**Finding:** `WindowsMlGenAiLocalInferenceEngine.cs` serializes generations through `SemaphoreSlim(1,1)`; the worker protocol is single-flight and cancellation kills the worker, forcing a subsequent model reload. The descriptor is text-only; availability does not establish tool/vision/structured-output parity or actual NPU execution.

**Implement:** where supported by the provider, add request-identified cancellation, event draining and safe warm-worker recovery; retain a bounded kill/restart fallback for a stuck worker. Add concurrency only where the backend and memory budget can support it. Keep advertised capabilities and routing/fallback consistent; unsupported features need an explicit eligible local route or honest rejection.

**Accept:** cancellation followed by another request yields no mixed events, stale output, leaked resources or unbounded wait; repeated cancel/crash/restart is covered. Measure warm recovery and concurrency against the baseline on eligible hardware. Record actual execution-provider assignment, CPU fallback, driver/model identity, memory, TTFT and sustained behavior; provider discovery/readiness alone is not NPU proof.

**Progress — PARTIAL, 2026-09-26:** added a process-level engine integration test using the production `WindowsMlGenAiLocalInferenceEngine` with an isolated ONNX-layout fixture and a local protocol-v4 worker stub. Two request cancellations are drained and followed by clean outputs on the same resident worker; two deliberate worker crashes produce the expected closed-channel failure, then restart cleanly for the next request. Assertions cover warm-cancellation/forced-restart/worker-start counters and response isolation. The test passed 1/1 on Windows. An internal provider-readiness delegate enables this isolated integration seam; the production constructor and real provider readiness path remain unchanged. Environment check: Windows 11 Insider build 26220, RTX 5080/16 GB; Windows ML reports `NvTensorRTRTXExecutionProvider` compatible but `NotPresent`, and `%LOCALAPPDATA%\\PaRuX\\models` currently contains GGUF weights only. No ONNX inference, warm-latency/concurrency benchmark, CPU-fallback measurement or physical NPU evidence is claimed; those remain open and BIC-08 stays PARTIAL.

### BIC-09 — shorten successful coding work and integrate review with diffs

**2026-10-05 continuation (status PARTIAL; not integrated):** preserved `783853c`/`8e211d7` and committed the verified executed-check, content-bound verification, STOP and immutable execution-lease corrections as `6b318b7`, after the independently reviewed listener correction `81e3ec3`. Initial full-suite failures and the two deterministic lease review failures remain preserved; their fixes pass independent review and the original targeted ladder plus three new regressions (99/99, zero skipped). The final branch Server suite passes **4509/4509**, zero failures/skips, in 16m54s; App Release x64 builds with zero errors and ten warnings. The 128-sample native listener probe detects every live listener and no free port; the legacy probe misses every live listener, so it is not a valid whole-app performance baseline. The real Core-owned FlaUI confirm/apply repeat remains open: its fixture is being hardened to attach and clean up only verified process identities and ancestry, including PID reuse and constructor-failure paths. Rebase/integration and regression on final main remain open. No current-main verification or whole-app speed improvement is claimed. Evidence: `.ai/artifacts/release-continuation-20261005/bic09/`, including `attempt-lease/root-review-iteration2.md`, full-suite TRX and App build log.
**2026-10-05 real S4 repeat (status PARTIAL; not integrated):** fresh App/Core source `6b318b7` with independently verified fixture `5aca5c9` runs through the actual UI and isolated Core/model. The task edits the target and executes `python verify.py` in the no-network AppContainer with exit 0, including reruns after the final edit; the independent schema verifier also passes. Core correctly records the task as **unverified** because the existing security policy deliberately rejects an arbitrary root-level verifier and this fixture has no discoverable project test marker. This is a fixture/accepted-check contract mismatch, not loss of command events or a passing S4. The next fixture places implementation and immutable verifier together in an explicit child scope while retaining the original correctness, baseline-failure, Core verification and review/apply requirements; product verification policy remains unchanged. The misleading verified-artifact log wording also needs correction. Original task, logs and screenshots remain preserved. Evidence: `bic09/real-s4-final` under the continuation artifact root.

**2026-10-05 scoped S4 follow-up (status PARTIAL; not integrated):** branch commit `6478865` aligns the disposable fixture with the existing child-scoped verification contract and changes the artifact archival message to avoid asserting verification. Independent diff review and targeted Server tests pass **51/51**, zero failures/skips; staged secret scan passes. Fresh App/Core publish at this source succeeds. The next real UI attempt fails before task submission because the fixture cannot establish the launcher's identity during startup; the launched App/Core remain alive. Evidence and failed TRX are preserved, and only the four observed owned process instances are subsequently closed through retained handles after path, start-time and ancestry checks. The fixture startup/constructor cleanup defect is being corrected independently before another UI attempt. No new S4 acceptance, apply, main regression or release claim is made. Evidence: `bic09/scoped-s4-followup`, `bic09/s4-layout-scoped16k`, `bic09/real-s4-scoped16k` under the continuation artifact root.
**2026-10-05 fixture startup recovery (status PARTIAL; not integrated):** commit `79bba07` retains the originally launched process handle through bounded image/identity reads and constructor cleanup, and retains independently proven descendant handles for attachment and teardown. Independent root review passes after adding guaranteed test cleanup; actual CPU helper tests pass **4/4**, zero failures/skips, including an already exited launch and an unrelated same-image process. Staged secret scan and fresh App/Core publish at this source pass. A new isolated UI acceptance attempt remains required; these checks do not close S4. Previous failed attempts and their environments remain preserved.

**2026-10-05 unchanged-write and 27B follow-up (status PARTIAL; not integrated):** `da9a893` prevents an identical `replace_range` result from rewriting the file or receiving mutation/progress credit. Independent root review and a fresh targeted rerun pass **35/35**, zero skipped; App build and staged secret scan pass. The immutable UI verifier retains its four cases and adds fractional-cent rounding: the actual CPU fixture rejects both the original boundary defect and a floating-point mutant, and accepts the correct integer-cent edit. Historical repeated writes are observed, but their identity/no-op cause is not established. The selected local 27B Standard passes authenticated isolated Core identity, supported 16k/q8 context preference and readback checks; no AUTO winner or performance improvement is claimed. A real 27B attempt used a stale x64 test assembly and was explicitly cancelled, retained and excluded from acceptance. A fresh scoped fixture, exact assembly rebuild, live S4, full post-change regression and integration remain required. Evidence: `bic09/noop-write-lot`, `bic09/fractional-cent-fixture-proof`, `bic09/real-s4-27b-standard` and the isolated context preparation attempts under the continuation root.
**2026-10-05 exact 27B S4 attempt (status PARTIAL; not integrated):** the fresh scoped fixture and exact rebuilt x64 assembly run against source `da9a893` reaches the correct minimal boundary edit while leaving the immutable verifier unchanged. The UI test fails after 15m37s: expected completed-and-verified, actual running. The Python verification child was observed suspended; the retained final UI timeline confirms the command returned timeout/exit -1 at 17:18:53 UTC, with 619.867 seconds in the command phase. The UI remained running at 17:23:09 UTC, and no successful verification or apply occurred. The source cause of suspension is not established. Exact native-child creation and command timer start were not retained; the recorded timeout result does not show a timeout defect. A bounded developer probe reproduces suspended Python through the same sandbox, with actual interpreter-image attribution, empty output and no marker; both 45-second timeout paths return and leave no probe process alive. Independent control/trace follow-up remains pending, and the suspension actor is unknown. All five observed owned process instances are absent after fixture cleanup; native listener query and exclusive IPv4/IPv6 wildcard binds confirm port 51123 is free. This is failed acceptance, not a model-quality verdict or verified application. Minimal same-sandbox reproduction and full regression remain required. Evidence: `bic09/real-s4-27b-standard-scoped` and `bic09/s4-sandbox-suspension-diagnosis.md` under the continuation root.
**2026-10-05 sandbox hang diagnosis (status PARTIAL; not integrated):** controlled CPU probe through the production WindowsCodingSandbox proved that child processes started by cmd (python, node, whoami, hostname, cmd /c) hang or are denied when the sandbox STATE path (PARUX_DATA_ROOT-derived) is long, while cmd builtins work; workspace length is irrelevant (workspace 193 + state 42 = OK; workspace 75 + state 166 = hang; sandbox dir 185 OK, 191 hang). The failed 27B S4 used a ~134-char isolated data root, so it was an environment artifact, not a model/verifier verdict; kernel mechanism not identified and Defender/CodeIntegrity/AppLocker showed no events. Branch commit `e070442` adds a fail-fast guard with an explicit PARUX_DATA_ROOT error plus test (73 targeted PASS). Real S4 must be rerun with a short isolated data root; no S4 acceptance claimed. BIC-13 StrictLocal Windows health fix is committed as `ad06927` in its worktree after independent review PASS and 4/4 targeted + 60/60 related tests; not integrated. Evidence: `.ai/artifacts/release-continuation-20261005/bic09/sandbox-probe/diagnosis-20261005.md`.
**2026-10-05 real S4 with short data root (status PARTIAL; not integrated):** source `e070442`, exact published App/Core layout and a copied isolated environment with a 67-char data root, real local Standard 27B 16k/q8 model: `Real_model_task_submitted_in_chat_is_planned_edited_verified_reviewed_and_applied_through_the_ui` PASSED 1/1 in 1m09s via UI (TRX `.ai/s4/a1/real-s4.trx`). Independently confirmed afterwards: `src/inventory.py` `>10` became `>=10`, `src/verify.py` unchanged, `python src/verify.py` exit 0, no owned processes left. This is one scenario, not a speed or general-quality claim; the earlier long-path failure is preserved and explained. Still open for BIC-09: full suite on the branch, security re-review of the cumulative diff, rebase onto main, integration.
**2026-10-05 integration of BIC-09 and BIC-13 branches into main (status PARTIAL):** security review of the cumulative BIC-09 diff found two medium gaps (directory-scoped git diff echoing tracked secrets; `.git/` and NTFS alternate-data-stream reads) and they were fixed with tests (`9ab88ff`); remaining low findings (benchmark env var can enable a feature over user setting; exit code parsed from description text; desktop coding runner has no execution lease; Completed parent does not stop live read-only children) stay OPEN. Both branches were rebased cleanly onto main and each passed the full Server suite (4521 and 4559 of 4559, 0 failed). A first BIC-13 run showed 13 failures (`no such table: goal_task_bindings` in VerifierServiceTests) that did not reproduce in isolation or in two later full runs: recorded as an unexplained transient, not a fix. The BIC-13 StrictLocal Windows health patch was withdrawn because main's native listener detection (`be1c1ff`) already fixes the cause and the patch broke `Idle_text_health_returns_without_http`; its tests are kept. Not yet done: App/FlaUI regression on main, real media cold/warm run, real voice Core run, BIC-15 S3 capture, BIC-03, final release gates. No push, tag or release.
**2026-10-06 closure of low BIC-09 security findings (status PARTIAL):** all four low findings closed with tests and independent security review PASS (43/43 targeted tests): (1) `CodingAgentFeatureFlags.Resolve` evaluates user settings first, so benchmark mode environment variables can only disable features when configured on, never enable over user settings; (2) `CommandExitCode` typed integer added to `CodingExecutionOutcome` and `RecordExecutedCheck`, eliminating regex parsing; (3) `AgentCodingWorkerService` desktop runner acquires `_desktopExecutionLease` (`SemaphoreSlim(1, 1)`) across `ExecuteAsync`; (4) `TaskStore` cascades cancellation and detaches running executions on all Terminal statuses including `Completed`. Still open for BIC-09: FlaUI confirmation/apply on real Core task, benchmark before/after on BIC-03 subset.
**2026-10-06 root cause and permanent fix of transient `no such table: goal_task_bindings` (status DONE):** the transient error observed during previous full test runs was diagnosed and root-caused to `GoalModeV2Store.Current` process singleton relying on an in-memory `_initialized` boolean flag. When SQLite database files were recreated or wiped by test fixtures under the same process, `EnsureSchema` skipped DDL execution because `_initialized` remained `true` in memory. Replaced in-memory flag with atomic `sqlite_master` inspection on connection open (`SELECT 1 FROM sqlite_master WHERE type='table' AND name='goal_task_bindings' LIMIT 1;`). Commit `0628a0e`, independent code review PASS, focused suite 29/29 PASS.
**2026-10-03 progress (status PARTIAL):** `dd4f6de`/`2b9d0f3` (independent review PASS). The coding agent writes per-step phase timing (first-token wait incl. queue/model swap/prefill, generation, context, read, search, git, write, command, coordination, checkpoint) to the task log, aggregated by `analyze-task-latency.py`. A bounded `batch_read` action runs 2–6 independent read-only reads with real parallelism (≤4), ordered results, per-item failure isolation, cancellation and preserved full-evidence markers; containment and repeat guards are unchanged. 18 new VS Code hunk edge-case tests (moved/deleted/overlap/partial/restart/error recovery; npm 53/53); full Server suite 4,213/4,213. A real Qwen3-4B run accepted the schema and emitted valid batches, but showed no speed gain on grep-friendly tasks, so no speed claim. Open: parent continuation while delegated children run, a real Core-owned task through the UI with FlaUI confirm/apply, in-product before/after measurement on the BIC-03 corpus, extension-host smoke; CodingAgentService wiring is covered only by source sentinels plus the live run.

**Status: PARTIAL — the 2026-09-26 test passed, but the complete BIC-09 acceptance remains open**

Acceptance test executed end-to-end through PaRuX UI on synthetic .NET target (`.ai/artifacts/test-repos/synthetic-dotnet-01/`). Agent session `44370e2c4c0342279b8b112583abcf1f` successfully:
1. Identified failing test via `dotnet test` (7/8 passed)
2. Located bug in `CalculatorApp/Calculator.cs:27`
3. Applied surgical one-line fix: `return (a / b) + 1;` → `return a / b;`
4. Verified build SUCCESS + tests 8/8 PASSED

**Critical finding**: The 2026-09-21 patch tool whitespace rejection **did not reproduce**. The `replace_range` tool worked correctly with exact string matching. Shadow copy isolation verified working as designed (worktree under `.parux-agents/`, original repo untouched until manual copy for verification). VSTest path length limitation encountered in deep worktree paths but not a BIC-09 blocker (tests ran successfully in original repo after patch copy).

Evidence: `.ai/artifacts/bic-09-acceptance-20260926-214419/`

Still open: Native editor navigation, Git staging UI, partial acceptance workflows, latency instrumentation.

### BIC-09 remaining work

**Finding:** the coding agent already supports multiple delegated children and durable worktrees, but the single-action loop pauses the parent while delegated work completes. VS Code Fix/Explain/Test and selection stage/revert exist; AI review is primarily a Markdown document beside the editor, rather than navigable findings attached to the actual diff.

**Implement:** instrument time spent in inference, reads/searches, builds, queueing and reloads. Batch independent reads and allow safe parent continuation when dependencies permit; preserve write isolation, authority, budgets, checkpoints and STOP. Connect review findings to files/lines/hunks with navigation, per-finding state and reconciliation after user/concurrent edits; verify the actual effect of stage/revert commands.

**Accept:** representative successful tasks become faster without reduced correctness or resource-budget violations; no assumption that extra inference concurrency helps the 780M. Editor interaction covers moved/deleted lines, overlapping edits, partial acceptance/rejection, restart and error recovery. Existing 11 Node tests are a baseline, not proof of native editor end-to-end behavior.

### BIC-10 — incremental indexing across normal repository changes

**2026-10-03 progress (status PARTIAL):** `464feaf`/`7ab070a` (independent review PASS). Incremental sync recomputes relations against the whole path/symbol universe from persisted per-file topology inputs and applies only the difference in one transaction, so incremental state equals a full rebuild (21 parity cases across C#, Python, C++, Rust, QML, CMake after add/delete/rename/edit/restart). Cancelled or crashed syncs are journaled in `semantic_pending`. Project/config coverage extended (.pro/.pri/.qrc/.qbs/.ui/.sln/.slnx/.cfg, pom.xml, meson.build, Bazel, Gemfile, go.mod, requirements.txt, Pipfile, Justfile); lockfiles and files over 4 MiB are skipped. Pinned real histories (ripgrep, fmt, requests, TaoQuick) match a full rebuild exactly; recall@10 0.88–1.00, MRR 0.72–1.00. Quiet-machine A/B vs `39fc116`: 50k initial index 409 vs 408 s (an earlier 466–697 s reading was Defender cold-read of freshly created files); single-file edit 10k 1.5→1.06 s, 50k 6.6→4.0 s; forced full rebuild +7%. Above 40% churn (≥200 files) the sync falls back to a full rebuild — a heuristic threshold, not a measured crossover; the topology pass remains O(repository). Remaining: lexical retrieval only (no AST/LSP), no memory measurement on a large real-text repository, git-history refresh only when some content changed.

**Finding:** `ContextGraph/SemanticIndexV2Service.cs` previously rebuilt fully on any file addition/removal; changed-file sync now exists but still loads broad repository/topology state. Project-file membership already includes QML, XAML, CMake and common configuration/build descriptors. C/C++ and Rust support is present. Symbol extraction remains heuristic rather than AST/LSP-equivalent, and retrieval precision, broad-load memory cost and real-repository scaling remain insufficiently measured.

**Implement:** transactional incremental add/update/remove with coherent symbol/reference cleanup and scoped database queries. Add appropriate project/configuration coverage and stronger semantic extraction where it materially improves retrieval; preserve fallback for unsupported languages.

**Accept:** compare correctness with a full rebuild after add/delete/rename/edit and restart; evaluate retrieval precision and incremental latency/memory on 1,000-, 10,000- and 50,000-file repositories. Report measured scaling rather than calling a full scan incremental because only some records are rewritten.

**Progress — PARTIAL, 2026-09-26:** changed-file projections now replace file/symbol entities and their `CONTAINS` relations atomically; indexed-source deletion uses SQLite exact/prefix queries rather than materializing all entities for each removed file. Cleanup is restricted to `file`/`symbol` entities, so unrelated graph entities with the same source are preserved. Missing files remove stale projections, while read failures on existing files throw before replacing the last-good projection. A forced/full sync marks its manifest incomplete before rebuilding, so a failed rebuild is retried instead of being treated as synced. The opt-in 1k/10k/50k synthetic matrix matched incremental add/change/delete/rename state and search results to a full rebuild after restart: 1k full 1.835–1.927s vs incremental 0.341–0.366s; 10k full 20.053–20.147s vs incremental 3.186–3.196s; 50k full 119.716s vs incremental 16.816s (incremental/full 14–20%). The 50k initial index took 107.160s. Evidence: `.ai/artifacts/bic10-semantic-scale-20260926.jsonl`; the earlier uncompleted quadratic run is retained separately in `.ai/artifacts/bic10-scale-20260926-aborted.json`. Targeted tests **48/48**; full `PaRuX.Server.Tests` **3,379/3,379**, zero skipped, 6m05s; independent final review **PASS**. **Still open:** synthetic-only precision/search checks, no measured memory, broad topology/file loading remains, and no real-repository language/configuration corpus. BIC-10 stays PARTIAL.

**2026-10-24 bounded-incremental test and project rules (status PARTIAL):** `SemanticIndexV2BoundedIncrementalTests` generates a 5,000-file repo (no network) and, for edit+rename+delete+add (4 files), measured on this machine: initial index 16-30 s, no-op sync 0.2-0.4 s, incremental sync 0.3-0.6 s, 6 MB managed allocation vs ~9.8 GB initial, 5 file records and 3 topology rows loaded, one history probe, none on a no-op sync; retrieval follows edit/rename/delete/add. Open weakness found: a method name shared by every file makes all files call-referrers of a changed file, so the sync loaded 5,000 file records and topology rows (4.6-8.5 s); ambiguous-name referrers still need bounding. Single synthetic run, not a real-repository claim. BIC-09: `.parux/rules.md` (<=2,000 chars, no reparse points) is now loaded with AGENTS.md/CLAUDE.md and labelled in the coding context; VS Code findings already have open/resolved/dismissed state with relocation, so no new finding model was added.

### BIC-11 — tokenizer identity and final context budget

**2026-10-25 progress (status PARTIAL):** the window planner is now injectable (`FitConversationWindowCoreAsync`) and `NearWindowReplanTests` (7, fake tokenizers) cover exact-count re-planning, typed 413 (never 503) for impossible prompts, direct-engine schema pricing and a live model swap behind `local-default` never reusing the other model tokenizer counts. Fixed: an estimator-priced plan (direct engine, or a prompt the char bound proved small) could not converge on a re-plan; the estimator is now calibrated to the exact count. Integrated-binary repeat still open.

**2026-10-03 progress (status PARTIAL):** `243534b`/`c309352` (independent review PASS). Token-count cache identity now includes the backend generation (PID + start time); unresolved backends are cached per call only. Synthesized compaction summaries are re-tokenized with the runtime tokenizer and re-checked; an over-budget plan without an exact count now fails with the existing actionable 503 instead of shipping. Live llama.cpp b10549 runs on Qwen3-4B/Qwen3.5-4B showed raw tool-schema pricing undercounts the rendered tool block (232/352 rendered vs 202/246 priced); tools are now priced at 422/460, an intentional ~2x over-reserve (≈5% of a 4k window, ≈2.7% of 8k). Near-window prompts without an exact count reserve a 10% margin (min 64) and say the size is not exact; partially estimated counts are labelled. Remaining: repeat through the integrated final Core binary with tools near the window; the protocol-compatibility input-token endpoint still falls back to a character estimate; 503 should become 413/400 for oversize prompts.

**2026-10-24 progress (status PARTIAL):** `/v1/messages/count_tokens` now returns `exact` plus an explicit not-exact note when the tokenizer is unavailable, prices the estimate with tool-block (2x) and per-message template overhead, and returns 413 `prompt_exceeds_context` (labelled exact or estimated) when the count exceeds the live window minus reply reserve. Direct-engine requests carry no tool schemas (only structured output), which the engine counter already prices. Integrated-binary repeat still open.

**Finding:** `RuntimeTokenCounter.cs` keys counts by endpoint, model API name and text. `ChatProxyService.cs` can use the stable `local-default` alias across actual model changes, risking stale counts from another tokenizer. Some synthesized digest/template overhead remains estimated. This is a source-identified risk, not a completed two-real-model reproduction.

**Implement:** include stable actual model/tokenizer/backend revision identity and invalidate on swaps, including replaced files with the same display name. Preserve useful tokenization caching and avoid adding a remote round trip to every request. Validate the final rendered prompt, including tools, templates and context projections, in paths near the context limit.

**Accept:** tokenizer changes under a stable alias, Unicode, tool schemas, summaries, restarts and near-window prompts never reuse incompatible counts or silently overflow. Virtual context remains retrieval/synthesis within a finite native window; do not claim unlimited native attention.

**Progress — PARTIAL, 2026-09-26:** the direct-engine final count now uses the same `LocalInferenceRequest` builder as generation and is no longer skipped just because MCP tool schemas were available. The MCP provider loop still counts its own rendered provider payload with those schemas. A focused test uses a direct counter and a 10,000-character tool result to verify the count call and the generation fields; combined focused selection passed **7/7**, independent review **PASS**. The full `PaRuX.Server.Tests` suite then passed **3,390/3,390**, zero skipped, 7m49s. **Still open:** end-to-end near-window re-planning on a real local tokenizer, two-model swap/restart behavior through the live chat path, and exact counts for synthesized summaries/templates across actual supported runtimes. BIC-11 remains PARTIAL.

**Progress — PARTIAL, 2026-10-03:** isolated real desktop-chat requests used the stable `local-default` alias through Qwen3 → Qwen3.5 → Qwen3 and repeated after a Core restart. Native Unicode corpus counts changed from `[10,8,500]` to `[9,8,800]` and back. Histories with 15,013 or 24,013 native content tokens exceeded the 8,192-token runtime window; chat reported compaction within the 7,168-token reply-preserving budget and returned the exact requested answer in each case. The native rendered-prompt counting endpoint was available. Evidence: `.ai/artifacts/bic-closure-20261002/live-core/tokenizer-after-restart-result.json`. This proves the tested model/alias/compaction path on binary `4f35240`; direct-engine rendered tool/schema counting and an integrated-final-binary repeat remain open. BIC-11 stays PARTIAL.

### BIC-12 — native document work must include real workbook semantics

**Finding:** native workbook formulas, types, styles, charts, bounded local evaluation, OpenXML validation and source-preserving edits already exist. The evaluator previously had no inputs from an edited source workbook, and Office/LibreOffice recalculation/render QA has not been established. Unsupported formulas must remain uncomputed rather than receiving a guessed cache; package edits must continue to reject macros/external workbook links and preserve unrelated content.

**Implement:** structured workbook operations for formulas, references, types, formatting and charts; edit existing workbooks without losing unrelated content. Provide a real recalculation path where available and expose cached/uncomputed status honestly where it is not. Validate output artifacts and previews, including safe handling of external links/macros where relevant.

**Accept:** multi-sheet formulas evaluate to independently checked results, edits preserve existing data/styles, charts reference the intended cells, and rendered documents remain usable. Successful XLSX creation/opening alone is not sufficient.

**Progress — PARTIAL, 2026-09-26:** patch evaluation now reads existing numeric cells and supported formulas from the authorized output workbook, so a new cross-sheet formula can receive a local cached result based on preserved source cells. Existing sheet names are retained exactly; existing unsupported/table formulas and empty shared-formula followers remain opaque, their stale cached values are never used as inputs, and the result reports uncomputed formulas with recalculation-on-open. The original source workbook remains unchanged. Tests cover a patched cross-sheet formula (`21*2 → 42`), table-formula preservation during an unrelated edit, stale shared-formula follower cache rejection and whitespace-significant sheet-name lookup. Workbook/style tests **14/14**; full `PaRuX.Server.Tests` **3,383/3,383**, zero skipped, 5m53s; independent review **PASS**. **Still open:** Office/LibreOffice recalculation and rendered-layout acceptance, broader formula compatibility and real-workbook evaluation. BIC-12 remains PARTIAL.

**Progress — PARTIAL, 2026-10-01:** expanded bounded local formula evaluator in `WorkbookFormulaEvaluator.cs` with the exponentiation operator `^` (higher precedence than `*`/`/`, left-associative matching Excel convention), multi-cell functions `COUNT` (numeric cell count) and `PRODUCT` (multiplicative aggregation), and mathematical/rounding functions `ABS`, `SQRT` (non-negative domain validation), `INT` (Excel floor rounding), `SIGN`, `POWER`, `MOD` (Excel modulus semantics `n - d * floor(n/d)`), `ROUND` (AwayFromZero with positive and negative decimal places), `ROUNDUP`, `ROUNDDOWN` / `TRUNC`, `LN` (positive domain validation), `EXP`, and `PI` (nullary constant). Verified that scalar functions reject multi-cell ranges, and non-finite outputs (NaN/Infinity) are trapped gracefully to uncomputed status. Tests cover formula calculations and invalid domain/range error handling: `WorkbookNativeV2Tests` **13/13 PASS**; `WorkbookPatchStylePreserverTests` **3/3 PASS** (total 19/19 PASS); independent code review **PASS**. Office/LibreOffice UI recalculation on open remains active. BIC-12 remains PARTIAL.

**Completion — DONE, 2026-10-03:** real Excel acceptance passed for generated multi-sheet workbooks (16/16 independently checked formula results) and Office-originated workbooks with standard and varied styles (5/5 results each). Source bytes, unrelated data/styles, tables, names, merges, hidden sheets and chart ranges were preserved. Negative bars retain their intended color and readable category labels. Final PDF renders were visually checked. Real VBA/external-link package parts are rejected without modifying source bytes; the acceptance validator rejects all 12 deliberately incomplete reports. Existing Office-created OpenXML warnings remain identical before/after and are reported honestly; this does not claim exhaustive Excel compatibility or warning-free imported packages. Evidence: `.ai/artifacts/workbook-office-hardening/run-07-varied/office-acceptance.json`, `run-08-standard/office-acceptance.json` and `validator-self-test-final.log`. Targeted workbook/integrated selection 60/60; Core regression 3,894/3,894, zero skipped. Independent review **PASS**.

### BIC-13 — production depth for media, training and continuous voice

**2026-10-05 cancellation acceptance fixture (status PARTIAL; not integrated):** branch commit `b2ae6c1` adds independent HTTP request-abort/follow-up and real Create UI cancellation coverage while retaining the production worker implementation behind injected interfaces. Focused Server checks pass **15/15**, raw HTTP framing checks **3/3**, and the fresh exact-x64 FlaUI run after the final bounded-trailer correction passes **1/1**, zero skipped. The UI consumes PCM before cancellation, leaves no cancelled WAV/partial or artifact, then completes a separate request with valid WAV framing. Independent review and staged secret scan pass. These fixtures use synthetic speech and do not prove actual Kokoro Python-worker exit, playback, voice quality or latency. The historical speech venv is incompatible with the current managed-runtime location contract; an isolated compatible runtime must be established without modifying retained evidence. Full branch/final regression, actual voice/media measurements and integration remain open. Evidence: `bic13/voice-cancel-lot` and `bic13/actual-voice-core-plan.md` under the continuation artifact root.

**2026-10-05 remaining measurement tools (status PARTIAL; not integrated):** branch commit `a338210` preserves and completes the eight remaining training/evaluation/data/voice utilities plus shared safety helpers. Independent review found and corrected absolute frame deadlines that could be extended by continuous output and missing cleanup after a failed ASR child. Required protocol phases now fail closed; owned Windows jobs are terminated and exit-checked in all paths; unsupported non-Windows ownership is refused before launch. Outputs are exclusive/fresh, provenance verifies every explicitly required file within a declared subset, sensitive evaluation paths/samples require opt-in, and matrix status is run-scoped with fail-fast execution. The synthetic vision split explicitly reuses label combinations. Root independently passes **26/26 CPU tests**, zero skipped; staged secret scan passes. These checks do not establish human media quality, supported 780M training, actual new media timings or product voice interruption. Full branch/final-main regression and integration remain open. Evidence: `.ai/artifacts/release-continuation-20261005/bic13/remaining-tools-fix/`.

**2026-10-05 actual TTS follow-up (status PARTIAL; not integrated):** pinned MIT SpeechT5 and Apache-2.0 Qwen2-VL-2B weights were acquired into a fresh evidence root and their three weight files match Hub LFS size/SHA-256; the restricted Qwen2.5-VL-3B model was excluded. Actual TTS-SFT failed at step 0/60 with an extra spectrogram batch axis (exit 1, no adapter or completion marker); that source and failure are preserved. Trainer/evaluator normalization now preserves frame loss masking and rejects invalid ranks/masks. The CPU processor/collator regression passes with two real audio lengths and 11 malformed inputs rejected; filtered C# tests pass 2/2 and independent review passes. The fresh 60-step training/reload/held-out retry remains outstanding. This is tensor-contract evidence, not voice quality evidence; the CPU-only evaluator is supported by inspection but not yet exercised through model evaluation.

**2026-10-05 continuation (status PARTIAL; not integrated):** preserved the training/speech/media slice. Focused checks pass 27/27, PowerShell parsing 2/2 and Python compilation 9/9. Independent review initially rejected unbounded trainer execution and unchecked NVIDIA telemetry; after correction, the software harness review passes with owned-process timeout cleanup, fail-closed telemetry and explicit CPU-only TTS admission. The harness self-test exercises those paths. The new full Server run was interrupted and has no aggregate result; its strict-local missing-runtime fixture failure remains unresolved. Retained RTX 5080 evidence shows a successful tiny synthetic SFT held-out split; DPO/CPT/GRPO executed but their weaker held-out outcomes are not successful quality evidence. Vision/TTS SFT, complete media cold/warm metrics and product voice-interruption acceptance remain open. The Radeon 780M LLM/Vision training profile is unsupported. Per the owner's decision, media quality is **NOT human-judged** and no media-quality claim is made. Evidence: `.ai/artifacts/release-continuation-20261005/bic13/` and the preserved earlier BIC-13 evidence.

**2026-10-05 measured TTS follow-up (status PARTIAL; not integrated):** the real SpeechT5 trainer exposed an extra spectrogram batch axis; after normalization, the preserved retry completed 60/60 steps, checkpoint/reload artifacts and completion marker, with trainer exit 0. A subsequent evaluator failure exposed an omitted token attention mask; both failures remain preserved. The corrected evaluator now uses the trainer's input/masking/reduction contract, finite-loss checks and paired per-row seeds; independent review **PASS**, with the composite-loss description clarified. On six synthetic held-out rows, the configured loss (spectrogram L1, stop-token BCE and enabled guided attention) is 0.974524 for the adapter versus 3.294865 for the base on CUDA. A fresh CPU-only evaluation also passes (actual device CPU, no GPU): 0.950677 versus 3.320770. The earlier requested-CPU run actually used CUDA and is explicitly excluded from CPU evidence; the harness now uses a nonempty CUDA mask and the evaluation wrapper rejects requested/actual device mismatches. These are training/reload and synthetic-loss results, **not human voice-quality evidence**, CPU-training proof or 780M support. Vision training has completed 60/60 steps but its reload/held-out evaluation is still pending. Full regression, media metrics, voice interruption and integration remain open. Evidence: `training-attempt02`, `tts-eval-gpu03`, invalid-profile `tts-eval-cpu04`, valid `tts-eval-cpu05`, and `tts-evaluator-review03.md` under the continuation artifact root.

**2026-10-05 Vision and media review follow-up (status PARTIAL; not integrated):** the pinned Apache-2.0 Qwen2-VL-2B Vision SFT run completes 60/60 steps, both checkpoints, adapter reload and held-out evaluation, with exit 0. On 36 synthetic held-out images the adapter returns the exact requested colour/shape format in 36/36 cases versus 1/36 for the base; both already include the correct colour and shape in 36/36. This demonstrates synthetic format conformance, not better general visual recognition or human media quality. Independent slice review remains **FAIL** on two media-evidence components: the benchmark must fail closed on sampler/generation failure, calculate actual medians, preserve prior evidence and prove owned-process cleanup; the packet audit must validate packaged inputs and safe paths and refuse overwrite of existing review evidence. These findings do not invalidate the measured training runs, but media metrics, product voice interruption, full regression and integration remain open. Evidence: `training-attempt02/runs/vision-sft` and `independent-slice-review.md` under the continuation artifact root.
**2026-10-05 packet integrity follow-up (status PARTIAL; not integrated):** media packet construction now preserves existing evidence, audits input/output hashes and sizes, and rejects unsafe paths, filesystem reparse points and malformed or manifest-inconsistent review sheets. Targeted tests pass 20/20 with zero skipped; independent review passes after rerunning both original compiled failure fixtures (malformed quoted CSV and matching-content external outputs junction), which now return Invalid. The verified two-file lot is committed as `85c7ae4` in the BIC-13 worktree. The exact committed diff has a passing secret scan; an earlier pre-commit scan did not run because its log directory was missing, and that distinction is recorded. These are synthetic integrity regressions, not human media quality evidence. Measurement-harness review, actual media metrics, product voice interruption, full suite and integration remain open. Evidence: `bic13/media-packet-fix` and `bic13/media-packet-review-probes` under the continuation root; developer test log is under the BIC-13 worktree artifact root.
**2026-10-05 voice/profile source follow-up (status PARTIAL; not integrated):** the independently reviewed sentence-streaming, upfront English speech dependency and training backend/unsupported-method contract lot is committed as `386689d` in the BIC-13 worktree. Fresh targeted protocol/install/profile checks pass 19/19 with zero skipped, and the staged diff/secret checks pass. This is source and targeted-test verification; product voice-interruption acceptance and full integrated regression remain open. The 780M LLM/Vision training profile remains unsupported, and no new CPU-training or human voice-quality evidence is claimed. Evidence: `bic13/voice-profile-lot` in the continuation roots (target test log in the BIC-13 worktree).
**2026-10-05 media measurement harness follow-up (status PARTIAL; not integrated):** the corrected harness and CPU regressions are committed as `fb15f59` in the BIC-13 worktree. Independent review and root rerun pass 14/14 CPU tests; an actual CPU-only Windows Job Object probe confirms the launched root/descendant exit while a separate sibling remains alive. The original synthetic completion fixture with missing resident metrics now fails completion; request IDs, finite timings, memory fields and worker shutdown exit are checked. Sampler errors and partial generation failures remain explicit, prior outputs are preserved, and medians use unrounded samples. This verifies the harness, **not actual GPU media performance**; cold2/warm3 at 1024x1024 and 9 steps still awaits a serialized live run. Human media judgment remains not performed. Evidence: `bic13/media-harness-fix` under the continuation root.

**Finding:** the independent managed Diffusers/Z-Image engine is real and already integrated; it is not missing. `ManagedDiffusersImageEngine.cs` currently starts a process and loads the pipeline per generation. Media comparison combines technical metrics with local CLIP scoring, which cannot establish typography, identity, edit fidelity or video temporal quality. All six training methods exist, but parts of `TrainingLabService.cs` install unpinned dependencies and the examined setup is CUDA-oriented. The examined `SpeechService.cs` TTS path accumulates chunks into a full WAV before returning.

**Implement:** (a) measured warm/resident media workers with admission control, unload and Gaming/STOP behavior, while preserving an explicit cold benchmark mode; (b) task-appropriate media corpora and independent/human quality checks for editing, text, identity, workflow recovery and video, rather than CLIP alone; (c) reproducible training dependency locks, truthful method/backend eligibility, supported non-CUDA paths where feasible, checkpoint recovery and adapter reload/evaluation; (d) true incremental audio delivery and interruptible voice turn-taking where the selected local engine supports it.

**Accept:** report cold/warm times and resources separately; compare equivalent ComfyUI/Diffusers workflows and visual quality. For each advertised SFT, DPO, continued pretraining, GRPO, Vision SFT and TTS SFT path, verify dataset → train → checkpoint/adapter → reload → held-out eval, recording supported hardware and quality/regression results. Measure time to first audio, STT quality, interruption and follow-up recovery. Hardware absence is not a fabricated pass or proof of universal support.

**Progress — PARTIAL, 2026-09-26:** the Create workspace TTS flow now consumes validated v2 PCM frames incrementally into an atomic PCM16/24-kHz WAV, with bounded per-frame buffering; errors, truncation and cancellation remove the partial file, and Artifact Workspace registration happens only after terminal `done`. The UI distinguishes local WAV success from artifact-registration failure. Writer lifecycle/WAV tests **6/6**; App Debug x64 build **0 errors** (6 existing warnings); independent review **PASS**; full `PaRuX.Server.Tests` **3,390/3,390**, zero skipped, 7m49s. The settings voice-preview still uses its buffered MediaPlayer path because MediaPlayerElement cannot consume the raw PCM stream. **Still open:** actual Kokoro stream/audio playback and time-to-first-audio, interruption UX, media/training production benchmarks and quality evidence above. BIC-13 remains PARTIAL.

### BIC-14 — useful browser/mobile coverage and verified paired workflows

**Finding:** `src/PaRuX.Server/WebClient/index.html` exposes Chat, Models, Runtime and Settings, with conversation navigation hidden by narrow-screen CSS. It does not expose most desktop workspaces. Android emulator install/launch evidence exists, but the full pairing/generation/artifact/recovery path is not all verified; the missing v1.8.0 APK is tracked in BIC-07.

**Implement:** prioritize complete remote workflows for conversations, knowledge/projects, coding/job observation, artifacts, automations and media where authorized; document any supported subset clearly. Restore accessible conversation navigation on narrow screens. Reuse Core-owned projections and the existing pairing/permission model; keep Computer Use authority on the paired Windows PC.

**Accept:** responsive browser tests and emulator/instrumented paired-client flows cover pairing/revocation, reconnect, generation, artifact retrieval, cancellation/STOP and permissions. Never widen remote authority just to reach desktop parity. Physical-phone exploratory QA remains optional under the existing release policy; claims specific to physical behavior need corresponding evidence.

**Progress — PARTIAL, 2026-09-26:** the real PaRuX WebClient page and its production mobile-conversation CSS/JS were opened in Chromium against a loopback-only deterministic API fixture. Manual browser checks covered the 800px mobile/801px desktop breakpoint, opening the drawer, Escape and focus restoration, selecting a saved conversation, New chat, and drawer closure when resizing to desktop. This proves the responsive client interaction only; the fixture is not the authenticated PaRuX Core, so pairing/revocation, real API behavior, mobile network recovery and Android/physical-phone flows remain open. BIC-14 remains PARTIAL.

**Progress — PARTIAL, 2026-10-02:** implemented atomic bulk panic revocation via `RevokeAll()` and pairing status check `IsDevicePaired(string id)` in `RemoteAccessService.cs`. Mapped loopback-protected `DELETE /api/v1/remote/devices` in `Program.cs`. Created a comprehensive server test suite in `RemoteAccessServiceTests.cs` (9/9 PASS) verifying initial disabled defaults, private IPv4 detection, pairing enablement guard, invalid/expired code rejection, device name validation bounds, brute-force rate-limiting (throttling after 10 failed attempts within 1 minute), token issuance and SHA-256 hashing, timing-safe token validation (`CryptographicOperations.FixedTimeEquals`), single-device revocation, bulk panic revocation, and offline imported-device validation. Server test regression suite: **3,877/3,877 PASS** (zero skipped). Independent code review PASS. Physical-phone QA remains pending; BIC-14 remains PARTIAL.

**Progress — PARTIAL, 2026-10-03:** the live isolated Core was exercised through the actual browser session and official Ollama SDK. Browser sign-in, wrong-passphrase refusal, streamed generation, STOP followed by another answer, saved conversation reopening, and reauthentication after Core restart were demonstrated. A real browser inspection exposed that session GET bypassed cookie authentication and session DELETE bypassed CSRF; the middleware now inspects the cookie before anonymous session GET and requires CSRF for logout. The regression failed before the fix and passes afterwards; access/client/API-security tests pass **138/138**. Real browser logout works. Visible model-selector labels, a page heading and dark-theme button contrast were corrected; the tested Chat page's axe audit reports **0 violations and 0 incomplete** (34 passes). Evidence: `.ai/artifacts/bic-closure-20261002/browser/`. These are working-tree acceptance binaries, pending independent review and final committed-build repetition; paired Android/artifact/permission coverage still needs reconciliation. BIC-14 stays PARTIAL.

**Completion — DONE, 2026-10-03:** independent QA passed the documented browser subset and paired-client acceptance against an isolated Release Core built from committed source `e980b8040f6a1337bae2dd80c8d6f846fda4a64a`. Browser checks cover wrong/valid sign-in, logout, real generation, STOP/recovery, saved history after Core restart, keyboard navigation and inert drawer behavior at 390/801 pixels; axe reports 0 violations and 0 incomplete checks. Three Android 15 emulator instrumentation workflows cover wrong certificate/code refusal, fresh pairing, generation, cancellation/recovery, artifact retrieval, denied desktop administration, credential reuse after Core restart and refusal after revocation; Keystore tests 6/6 pass. Committed Core regression 3,908/3,908, zero skipped. Browser-supported features are explicitly bounded in README; this does not claim every Android workspace, physical-phone behavior or signed production APK acceptance. Independent review and QA **PASS**. Evidence and exact binary/archive hashes: `.ai/artifacts/bic-closure-20261002/bic14-committed-qa/acceptance-report.md`. Signed installer/APK/update and final whole-release acceptance remain BIC-07 and the physical-PC gate.

### BIC-15 — isolate persistence and enforce a strict-local execution boundary

**2026-10-05 capture script readiness (status PARTIAL):** private v4 scripts pass independent cleanup-delta review, **32/32 synthetic** analyzer tests, **8/8** PowerShell parser checks and **8/8** script hash checks. An actual CPU helper probe verifies exact owned-process termination, rejection of a nearby but different start time and survival of an unrelated same-image process. Native cleanup compares full creation ticks and image path, then terminates and waits through the same retained OS handle. Port admission additionally requires exclusive IPv4/IPv6 binds because both listener enumeration APIs missed a known live listener on this host. No live v4 capture has run: same-run external positive control, complete ETW lifecycle/flow attribution and actual PaRuX generation remain mandatory. No firewall change or outbound-absence claim is made. Evidence: `.ai/artifacts/release-continuation-20261005/bic15-v4/root-cleanup-delta-review.md` and independent probe outputs.

**2026-10-05 elevated recording continuation (status PARTIAL):** record-only netsh capture is authorized; no firewall or system settings were changed. An isolated positive-control probe establishes that the TCPIP/AFD detector observes external socket flows. On a Release main `a979443` binary, a separate partial trace records real local generation and 140 process-attributed TCP flow events, all loopback, but its workload harness stopped before the same-run positive control. A subsequent attempt was refused by resource admission (87.4% system RAM against the unchanged 86% limit). The verdict remains INCONCLUSIVE: neither the separate probe nor the partial trace closes S3. Attribution must use explicit socket-owner PID/control-block bindings, account for control-block reuse, and never equate an ETW executing-context header PID with socket ownership. Recording and owned Core/runtime processes were stopped. Same-run positive control, agent workload, independent analysis review and integrated-final repetition remain open. Evidence: `.ai/artifacts/release-continuation-20261005/`.

**2026-10-03 progress (status PARTIAL):** S1 connected-peer attestation landed (`4d5938a`..`5374311`, security review PASS after three rounds). Before any HTTP bytes reach a PaRuX-launched runtime, the exact established loopback connection (IPv4/IPv6) must be owned by the recorded launch (PID + start time + image, PID-reuse and TOCTOU safe); refusals never fall back anywhere. Attested: chat proxy, local API, orchestration completion client, presentations, research, vision subagent, protocol token counter, hot-pool self-test, and the retrieval llama-server (embeddings/rerank). A source guard test (heuristic) flags new unattested clients. StopText only kills the verified launch or a process whose final-path image is under the PaRuX runtime root. Missing launch identity counts as not owned so the relaunch path runs. Unattested by design and listed: `TextInferenceQuiescence` (/slots, router child ports), `LocalAiProcessManager.Http` health client (also ComfyUI, no prompts), candidate quality/performance runners and performance probe (fixed benchmark prompts to PaRuX-launched child ports), backend race against user-named external endpoints. Not yet covered by live tests: retrieval attestation, relaunch after Core restart. Residual: socket handle duplication by a legitimate owner is invisible.

**2026-10-04 BIC-15 S2/S3 (`4abc113`..`1334462`, security review PASS after three rounds; status PARTIAL):** restore is serialized behind a gate; cancellation stops at the commit boundary; the client history DB lock is probed best-effort before any replace. Before the first replace a pre-restore snapshot of the current state is written to a private folder (current user + SYSTEM only; restore refuses if the ACL cannot be applied), the newest three are kept (pruned by write time, never the one just created) and can be cleared from Settings or a loopback endpoint; a failing step rolls back automatically and reports the outcome and archive path. Stores that cannot be snapshotted make restore refuse with 409 `incomplete-rollback-snapshot` unless the user explicitly confirms in Settings (`allowIncompleteRollbackSnapshot`). WAL/SHM sidecars are removed only after a successful move; declared entry sizes are capped. Tests: BackupResilience + HTTP contract tests on both import routes 26/26 ×3, full suite 4,326/4,326. S3: non-elevated egress sampler (`scripts/sample-process-egress.ps1`) showed loopback only across 78 samples of the strict-local test host (polling, not a capture); the elevated pktmon/netsh capture is documented in `docs/STRICT_LOCAL_EGRESS_EVIDENCE.md` and still requires authorization. Open: elevated capture, FlaUI test of the restore/clear dialogs, cleanup of other optional files on rollback, cross-store restore remains validate-then-swap with snapshot rollback rather than a single transaction.

**Finding:** production backup now releases only its destination pool through `SqlitePoolIsolation.ReleaseBackupDestination`; database-file replacement clears the configured connection-string variants only for its target. Historical tests still call process-wide `ClearAllPools`, and the production helper had no direct regression test. Production corruption was not demonstrated. Historical cloud-fallback settings remain readable, but `CloudFallbackService.OpenStreamAsync` is hard-disabled before inference egress and restored credentials cannot arm it. Managed-listener checks now read ownership afresh and fail closed; the check-to-connect interval remains an inherent concurrency boundary to assess, not a reproduced breach.

**Implement:** (a) narrow pool/handle management to the affected database while preserving safe online backup/restore; (b) enforce a strict-local profile at every inference route, including configuration restore/migration, recovery and API calls, independent of persisted fallback settings; distinguish explicitly allowed web research/downloads from inference egress; (c) test foreign listeners arriving/leaving inside the ownership-cache window and strengthen binding/authentication/identity where needed without disabling checks or reintroducing costly per-request health probes.

**Accept:** backup during chat/agent/sync activity, locked files, cancellation, restore and restart produce consistent recoverable data without unrelated-pool disruption. In strict-local mode, attempted cloud fallback sends no inference request even on local runtime failure or restored cloud credentials. Listener-race tests prove refusal or an authenticated trusted runtime connection; document residual limits honestly and preserve the existing sole-owner protection.

**Progress — PARTIAL, 2026-10-01:** resolved IPv6 listener ownership blindspot and completed SQLite pool isolation coverage. `ManagedListenerOwnership.cs` now supports `AfInet6 = 23` with `Tcp6RowOwnerPid` (56-byte Win32 `MIB_TCP6ROW_OWNER_PID` layout); `FindListenerOwners` inspects both IPv4 and IPv6 listener tables to verify strict ownership by PaRuX's own PID, closing potential port-shadowing prompt hijacking windows. `SqlitePoolIsolation.cs` now includes `Mode=ReadWrite;Cache=Shared` and `Mode=ReadWrite` variants in `ConnectionStringsFor`, ensuring connection pools for remote computer use and visual context retention are flushed cleanly before database replacement. Tests cover owned and foreign IPv6 listeners and connection string coverage: `ManagedListenerOwnershipTests` **9/9 PASS**; `SqlitePoolIsolationTests` **2/2 PASS**; independent code review **PASS**. Packet capture, firewall audit, and physical network disconnection proof remain open. BIC-15 remains PARTIAL.

**Progress — PARTIAL, 2026-10-03:** live shared-listener testing found that the compatible model API did not apply the same ownership boundary as desktop chat. The common protocol preparation path now verifies fresh exclusive managed-runtime ownership after routing, before any buffered or streaming prompt is sent. Targeted protocol/Ollama/strict-local tests pass **56/56**, including three no-egress refusal cases. On the actual isolated Core, a foreign process shared loopback port 1234 for three cycles: each request was explicitly refused (502), the foreign process captured no requests, and normal inference recovered after its listener closed. The prior failed probe is retained; it showed a model answer and no foreign requests, not demonstrated leakage. Evidence: `.ai/artifacts/bic-closure-20261002/live-core/listener-race-result.json`. The check-to-connect interval remains open for connected-peer identity review; independent review and final binary repetition remain required. BIC-15 stays PARTIAL.
**Progress — PARTIAL, 2026-10-18 (remote-egress hardening):** explicit remote API tasks need a single-use grant bound to provider, normalized host:port, API-key fingerprint, model, task id and payload digest. The caller must supply recorded consent, and each request sends `max_tokens` with a worst-case cost checked against the approved budget before dispatch. Benchmark probes refuse redirects and resolve remote targets to public addresses only, while local targets keep loopback/LAN. A cloud-fallback key is dropped when its host changes, its probe refuses redirects, mutating AI-backend routes require the desktop owner, and a request without a remote address is no longer treated as loopback. Verification: focused filter 225/226; the one failure is the strict-local route, which also fails at the base commit on a missing runtime binary. Full-suite runs at HEAD reproduce the stable base failure set; one run also showed intermittent shared-state failures (SQLite ledger, shadow repository) that are not yet attributed. Packet-level egress proof, firewall audit and physical network tests remain open; BIC-15 remains PARTIAL.
**Progress — PARTIAL, 2026-10-18 (post-merge backup snapshot regression, `684cfd2`):** after the Agent Platform V2 merge, the schema recheck recreated a removed store database inside `BackupDatabaseTo`, so backing up a deleted store silently succeeded as an empty store and the 409 refusal and PARTIAL-snapshot reporting stopped firing. Sixteen store snapshot paths now refuse a store that initialized in this process when its database file is missing (`SqliteSchemaGuard.ThrowIfInitializedDatabaseIsMissing`); corrupt files stay refused and are never rewritten. Verified: Backup 75/75, SqliteSchema 20/20, Store 436/436, new `SnapshotRemovedDatabaseTests` 16/16. Limit: a file recreated by a normal operation before the backup is captured as a valid empty store.

**Progress — PARTIAL, 2026-10-18 (revocation, owner and provenance batch):** revoking an API key, turning the remote-provider opt-in off, or changing/disabling cloud fallback cancels work already admitted under it (`InFlightRequestRegistry`; API-key requests are linked to `RequestAborted` and re-checked after admission). `GET /api/v1/ai/backends/detect` and `/external` require the desktop owner. The cloud-fallback probe validates every resolved address at connect time and uses no proxy. `model_routed` reports provider, locality, remote-only `endpointHost` and `fallbackReason`, and each text reply shows and persists its provenance (contract rule 10). Open: cost attribution and media-job provenance; the remote routing branch is test-only while `LocalRuntimeService.UsesExternalProvider` is constant `false`. Verification: the required filter plus related classes 337/337; full server suite 4883/4884 (the one failure, `GpuPerProcessVramParserTests.A_hung_query_is_unknown_within_the_deadline`, is a timing path this batch does not touch and passes 12/12 alone); App x64 Debug and server test builds pass. BIC-15 remains PARTIAL.

### BIC-17 — Universal Local AI Gateway and one-click agent-client integrations

**Status: DONE for Windows** (commits `40a8c93`, `63a0860`, `48f8547`, `be11fff` and the docs/plan commit). Reference: `docs/UNIVERSAL_GATEWAY.md`.

- Loopback-only gateway port 11435 serving only the model APIs: Anthropic Messages (+ `count_tokens`), OpenAI Responses and Chat Completions, `/v1/models` with `parux-auto/code/reasoning/fast/vision`, and vendor-id mapping.
- True streaming from one internal stream, with validated tool calls, keep-alive, client-disconnect cancellation (about 1 s), the Gaming cancel token, and honest `max_tokens`/`incomplete`. The Ollama emulation is kept and has no dependency on an installed Ollama.
- Settings › Integrations: detection, per-client DPAPI key, Connect with backup and a real file test, Launch, Test, Disconnect, and byte-identical Restore for Claude Code, Codex and OpenCode, plus a generic endpoint card.
- Live evidence:
  - UI Connect: Claude 33 s, Codex 31 s, OpenCode 20 s.
  - Agentic bug fix verified independently: 21 s, 29 s and 31 s.
  - Four concurrent clients share one instance; mixed roles serialize around one swap.
  - Tests: 154 focused (gateway, protocols, Ollama, integrations). Security review: no P0; the P1/P2 findings are fixed.
- Design choice: models stay resident; there is no idle unload. The user asked for no reload while the request type is unchanged, and Gaming/media/OFF transitions release the GPU.
- Remaining limit: 16k coding context. Claude Code Launch uses a lean tool set. A larger context is a model/VRAM profile decision, not a gateway gap.

### Comparison references and evidence required for closure

Use current official references when refreshing the comparison: [Codex](https://learn.chatgpt.com/docs/agent-configuration/subagents) for completed coding work and orchestration; [LM Studio](https://lmstudio.ai/docs/app) and [Bionic](https://lmstudio.ai/docs/bionic) for local runtime/agent workflows; [Ollama](https://docs.ollama.com/api/chat) for API semantics; [Open WebUI](https://docs.openwebui.com/features/) for browser/knowledge/voice workflows; [ComfyUI](https://docs.comfy.org/) for media workflow depth; [Unsloth](https://unsloth.ai/docs/desktop) for local training; and [vLLM](https://docs.vllm.ai/en/latest/) for serving on eligible single-machine hardware. These are category references, not a universal ranking or proof that PaRuX has already won.

- **Engine comparison:** same physical PC, model weight hash/revision, tokenizer, quantization, context, KV precision, reasoning/output/quality settings and equivalent cache/load state. Compare direct runtime against PaRuX's route to isolate overhead. Verify competitors unload and do not consume each other's memory.
- **Product comparison:** complete real tasks using documented reasonable product configurations; quality, task success, regressions and human intervention come before speed. Report model differences explicitly, especially for cloud Codex.
- Record TTFT and task-time distributions, prefill/decode separately, model load/swap/unload, peak and residual RAM/VRAM, concurrency/queueing, STOP/Gaming release time, recovery, context accuracy, document/RAG correctness, media/voice quality, training evaluation and sustained stability.
- Use separate cold and warm runs, alternate product order, retain raw results/configuration and repeat sufficiently for the claimed statistic. Three runs are smoke evidence, not a reliable p95. Define tolerances and quality thresholds before evaluating; do not select only favorable runs.
- Existing 780M/Qwen3.5-4B reports around 24–28 decode tok/s and four completed concurrent requests establish working Vulkan inference, not a general lead. Prefill measurements at 32 versus 2,410/7,210 tokens do not establish a 6.6× product speedup. The 4,846 ms → 551 ms cache case is context reuse, not proven cold model-load time.
- Existing LM Studio/Ollama runs with hidden-reasoning/output truncation remain **INCONCLUSIVE**. Missing NPU providers or unavailable RTX 5080 hardware remain unavailable evidence, not PASS. Keep the current 780M and future attached-GPU profiles separate; do not extrapolate between them.
- Repository-only work must finish the actionable software and local test harnesses, while leaving exact Windows/device/model/publication checks explicit. A physical Android phone is not a new blocking release gate. Genuine software defects found during physical acceptance return to these items for repair.

## Dated implementation and validation history

### 2026-09-30 whole-app verification pass (after BIC-17)

- **Release gate** `tools/release-gate.ps1 -Restore`: PASS.
  - Core suite: 3,749/3,749 tests.
  - Windows x64 solution built; 5 shipped projects published.
  - VS Code tests passed.
  - Android: clean JVM tests, lint, debug APK and unsigned release APK.
  - The gate still reports 14 active BIC rows.
- **FlaUI Regression:** 58/58, including the new Integrations page test.
- **Desktop walkthrough, Release build with live stack:** every workspace in both Simple and Advanced modes and all 13 Settings categories rendered, 44 pages in total. No error text; the only match was a false positive. `parux.log` had no exceptions.
- **BIC-14 evidence on the current build (local Android 15 test AVD):**
  - instrumented tests: 6/6;
  - live TLS pairing with the pinned fingerprint: the wrong code was rejected with guidance, the valid code paired;
  - chat generation over the paired link worked;
  - STOP freed the llama slot in about 2 s;
  - after a Core crash, the desktop app restarted Core and the phone reconnected;
  - during a full outage the phone now shows user-facing guidance (fix `09d5cbf`), and chat worked again after recovery.
  - Afterwards the device was revoked and LAN access switched off.
  - Browser-client and physical-phone coverage remain; BIC-14 stays PARTIAL.
- **Publication hygiene:**
  - Gitleaks, full history (`--all`): 1,196 commits, no leaks.
  - Tracked files contain no local user paths. The only e-mail is the CODE_OF_CONDUCT contact, and the only private IPs are test fixtures.
  - The repository stays PRIVATE.

### 2026-09-30 BIC-17 Universal Gateway, true streaming and client integrations

The Universal Gateway, true streaming for Anthropic/Responses, `count_tokens` and Settings › Integrations were implemented and verified live against Claude Code 2.1.283, Codex 0.158.0-alpha.2.1 and OpenCode 1.18.22 on 127.0.0.1:11435.

- Connect, Test, Launch, Disconnect and Restore were driven through the real WinUI page. The user's `opencode.json`, `config.toml` and `~/.claude/settings.json` had the same SHA-256 before and after.
- Evidence lives under `.ai/artifacts/gateway-clients-20260930/` (untracked).

The following dated entries preserve work and evidence from earlier snapshots. Historical closeout wording does not override the active status or BIC-01 through BIC-16 above.

### 2026-09-23 BIC-16 Chat routing explanation slice

- Added a keyboard/UIA-accessible Chat action to inspect the current route explanation, with safe empty state and automatic clearing on conversation change.
- Extended the existing SSE control event with explicit Standard-role fallback state; parser tests cover old, incomplete, malformed and non-object JSON events. Selected model names are reduced to basenames before status, tooltip or dialog display; route reasons do not include prompt text.
- Capability is labeled as an estimate rather than answer-quality proof; throughput is labeled as recorded benchmark evidence rather than a live measurement. Missing evidence is explicit.
- Verification: `PaRuX.Server.Tests` 3,095/3,095; route explanation/parser tests 10/10 after adding malformed-root cases; targeted FlaUI accessibility smoke 1/1; `dotnet build src/PaRuX.App/PaRuX.App.csproj -c Release -p:Platform=x64 --no-restore` succeeded with 0 errors and existing analyzer/nullability warnings; independent review PASS.
- Commit: `d9774dda0fa646ab63324b6fab80a5521ca4c8b1`. This is one BIC-16 UX/evidence slice, not completion of autonomous discovery, recommendation, promotion or rollback.

### 2026-09-24 BIC-16 model-download licence consent slice

- Added an exact conservative permissive SPDX allow-list (`0BSD`, `Apache-2.0`, `BSD-2-Clause`, `BSD-3-Clause`, `BSL-1.0`, `ISC`, `MIT`, `Unlicense`, `Zlib`) and required a per-download unchecked review for every other, missing or unavailable declaration.
- All four fresh download entry points use one shared consent-before-start callback. The model-card link is constrained to `https://huggingface.co/<owner>/<repo>`; cancel/close or an unchecked primary never reaches the start callback. Resume remains attached to its existing download job identity.
- Added 20 focused policy tests and a Debug-only isolated FlaUI fixture/test for primary blocking, cancellation and explicit confirmation. No production test route is compiled into Release.
- Verification: `ModelLicenseConsentPolicyTests` 20/20; targeted consent FlaUI 1/1; App Release x64 build 0 errors (6 existing analyzer/nullability warnings); `git diff --check` clean; independent review PASS.
- Commit: `226f8c5` (`feat: require license review before model downloads`).
- Remaining: no persisted immutable revision/licence evidence, no legal verification, and old partial jobs can resume without new evidence. This is a partial BIC-16 implementation only; see the next actionable gap above.

### 2026-09-24 BIC-16 first-model storage provenance slice

- First-run onboarding now labels the curated model size as approximate and warns that the selected Hugging Face file may differ. It shows PaRuX quota headroom separately from `DriveInfo.AvailableFreeSpace` on the exact managed model-download destination volume; neither value reserves space.
- `/api/v1/models/budget` adds nullable, additive quota-headroom and destination-free-byte evidence. Volume-probe failures are represented as unavailable, do not expose the path, and do not alter download admission (which continues to enforce only the shared managed-storage quota). Onboarding applies model advice before awaiting the independent storage request and distinguishes `0 GB` from unavailable.
- Deterministic server tests cover production volume probing, exact destination injection, independent quota/disk values, unavailable/throwing probes and budget boundaries/overflow. Debug-only FlaUI fixtures cover distinct estimate/quota/disk wording and zero-vs-unavailable presentation; fixtures are excluded from Release.
- Verification: `ModelDestinationSpaceProviderTests` 7/7; full `PaRuX.Server.Tests` 3,126/3,126; first-model storage FlaUI 2/2; App Release x64 build succeeded with 0 errors (8 existing analyzer/nullability warnings); independent delta review PASS.
- Commit: `c9605bc` (`feat: clarify first model storage guidance`).
- Limitation: a quota/disk snapshot can become stale immediately and is not a reservation; the first-model size remains a curated estimate, not the selected repository file's authoritative byte size. No physical-PC or GPU acceptance is claimed. BIC-16 remains PARTIAL.

### 2026-09-24 BIC-16 immutable model revision and safe-resume slice

- Model Hub metadata now validates and carries the immutable Hugging Face commit SHA into the model-card review, consent, download request/status and restart catalog. New UI transfers fail closed when that revision is unavailable; Core rejects malformed or URL-mismatched declared revisions.
- Per-file partial-source identity binds the partial bytes to repo/revision/file/URL. Transfers to the same destination serialize; a changed revision discards incompatible partial bytes. Legacy revision-unknown `/resolve/main/` partials remain resumable without being relabelled.
- Verification: focused download/security/resume/licence tests 89/89; full `PaRuX.Server.Tests` 3,137/3,137; targeted consent/revision FlaUI 2/2; App Release x64 build 0 errors (6 existing analyzer/nullability warnings); independent final review PASS; `git diff --check` clean.
- Commit: `4d23f54` (`feat: pin model downloads to immutable revisions`).
- Remaining: durable licence/revision/consent/hash-evidence manifest and restart-safe validation/activation. A publisher declaration is not legal verification; missing remote hash evidence must remain unknown. BIC-16 remains PARTIAL; no GPU or physical-PC acceptance is claimed.

### 2026-09-24 BIC-16 durable download provenance and backup byte-identity slice

- Added a versioned atomic local provenance manifest bound to the immutable repository/revision/file identity. It records the exact publisher licence declaration and observation state, whether review was required and affirmatively acknowledged, provider-declared SHA-256 evidence, and a locally computed SHA-256 fingerprint of the completed bytes. The local fingerprint is computed incrementally during transfer, including resumed transfers; absent publisher digest remains `unavailable`, never `verified`.
- New transfers fail before network access if the manifest is corrupt. A corrupt manifest appearing during a transfer prevents promotion and preserves any prior destination bytes. Current UI/history makes clear that this is a historical download record and does not cryptographically re-verify current bytes; a cheap size/mtime guard hides records after likely replacement. Backup metadata is reattached only after cancellable destination-file hashing confirms byte identity; nonmatching or missing bytes remain unknown. Manifest reads/writes are atomic, and import hashing occurs outside the global lock with file identity rechecked before commit.
- Verification: full `PaRuX.Server.Tests` 3,156/3,156; focused provenance/backup tests 42/42; targeted FlaUI consent/revision tests 3/3; App Release x64 build succeeded with 0 errors (6 existing analyzer/nullability warnings); `git diff --check` clean; independent final review PASS with no findings. Full/focused/UI/build output is retained under ignored `.ai/artifacts/`.
- Code commit: `fdc3d51` (`feat: persist revision-bound model provenance`).
- Limits: publisher declarations are not legal verification; the stored fingerprint is not recomputed on every model listing or startup; backup import does verify destination bytes before attaching metadata. BIC-16 remains PARTIAL; no GPU or physical-PC acceptance is claimed.

### 2026-09-24 BIC-16 versioned model-discovery Candidate registry

- Added a separate Hugging Face discovery adapter and `SchemaVersion=1` catalog under `ServerState.StateDirectory`; existing Model Hub search/download and installed-model catalogs remain unchanged. Manual refresh reads a bounded top-GGUF feed (up to 8 repositories and 40 GGUF files per repository), fetches repository details on fixed HTTPS `huggingface.co`, rejects redirects, caps responses/time, and applies persisted success/failure cooldowns including 429 retry-after handling.
- Candidate identity includes provider, safe repository, immutable 40/64-hex revision and complete repository-relative file path; duplicate basenames do not collide and no Candidate URL uses `/resolve/main/`. License, pipeline/tags, size and publisher SHA remain declared metadata; integrity stays `unverified`, and lifecycle stays `Candidate`. There is no download, remote-code execution, benchmark, recommendation, routing or promotion.
- The local catalog is atomically replaced, size/schema/identity validated and fails closed on corruption/future schema. Failed, cancelled, offline, oversized, malformed and rate-limited refreshes preserve last-good entries and expose retry state. A 1,000-entry retention policy prioritizes the current feed, then recent ignored decisions, then most recently seen stale entries. Authenticated GET/refresh/ignore routes remain denied to paired devices by the existing default-deny policy; mutating browser-session calls continue through global CSRF checks.
- Verification: focused discovery tests 14/14; full `PaRuX.Server.Tests` 3,170/3,170 in 5m01s; Server Debug build 0 errors; App Release x64 build 0 errors (8 existing analyzer/nullability warnings); relevant consent/revision FlaUI tests 3/3; `git diff --check` clean; independent second review PASS with no findings. Raw logs are retained in ignored `.ai/artifacts/bic16-discovery-*.log`.
- Code commit: `bc13bea` (`feat: add versioned model discovery candidates`).
- Limits: the adapter currently observes the bounded public top-GGUF feed, not a task-specific evaluated catalog; there is no discovery UI or hardware/quality fit claim. Candidate data is unverified publisher metadata. BIC-16 remains PARTIAL; GPU, performance, thermals, gaming and physical-PC acceptance are not claimed.

### 2026-09-24 BIC-16 revision-bound source-file provenance

- Completed download provenance now stores the decoded repository-relative file path together with a canonical, queryless, revision-pinned source identity URL. Identity is derived only after transfer URL validation; ambiguous encoded separators and unsafe paths remain unknown rather than changing transfer behavior. Query credentials/fragments are not persisted, and manifest validation rejects partial or inconsistent path/URL/repository/revision/file-name pairs.
- Legacy `/resolve/main/` downloads and old manifest records keep source-path identity null. Nested files sharing a flattened local basename remain distinguishable in provenance. Backup restore carries the optional identity only through the existing destination-byte/hash match checks.
- Verification: focused provenance/backup tests 39/39; download/security regression 58/58; full `PaRuX.Server.Tests` 3,174/3,174 in 5m03s; App Release x64 build 0 errors (8 analyzer/nullability warnings); relevant consent/revision FlaUI tests 3/3; `git diff --check` clean; independent diff review PASS with no findings. Logs are retained in ignored `.ai/artifacts/bic16-source-path-*.log`.
- Code commit: `5c25a06` (`feat: bind model provenance to source paths`).
- Limits: the URL is an identity key, not a replayable transfer URL or proof of publisher integrity. Legacy unknown identities remain unknown. BIC-16 stays PARTIAL; quality/performance evidence, model recommendation, promotion/rollback, Gaming behavior and physical-PC/GPU acceptance remain open.

### 2026-09-24 BIC-16 Candidate evidence ledger and eligibility foundation

- Added separate versioned task-quality and local-performance evidence records, with a bounded atomic schema-v1 ledger (up to 128 records per kind) under `ServerState.StateDirectory`. The records retain separate identity, suite/case outcomes, environment/workload fingerprints and measured metrics; corrupt, oversized and future-schema state fails closed.
- The internal service binds records to the current active Candidate and managed provenance (provider/key/repo/revision/full source path/canonical source URL/file name/size/local SHA-256). It streams a fresh file hash before a producer snapshot, before evidence commit and again at eligibility; it does not use size/mtime as digest proof. Eligibility additionally matches the exact current suite and environment, rejects incomplete/failed/cancelled/truncated/inconclusive/error/external-fallback evidence and returns only `EligibleForFurtherReview`; timestamp ties or stale reordered evidence fail closed. No recommendation/routing/promotion transition exists.
- Verification: focused Candidate-evidence tests 30/30; full `PaRuX.Server.Tests` 3,204/3,204 in 5m01s; Server Debug build 0 errors (12 analyzer/nullability/obsolete-API warnings); `git diff --check` clean; independent review PASS after fixes for persisted-record ordering and internal-constructor DI activation. No FlaUI run was applicable because this slice changes no UI. Logs are retained in ignored `.ai/artifacts/bic16-evidence-ledger-*.log`.
- Code commit: `93bc6ba` (`feat: add Candidate evidence integrity ledger`).
- Limits: this is an evidence store/policy foundation, not an evaluation producer or trusted suite registry; no model has been quality-tested or benchmarked by it, and no performance result is inferred. Hardware/runtime compatibility, actual text/media quality, recommendation, promotion/rollback, Gaming behavior and target-PC/GPU acceptance remain open; BIC-16 remains PARTIAL.

### 2026-09-24 BIC-16 isolated deterministic text-quality producer

- Added the built-in `chat.instruction_following.smoke` v1 suite with four deterministic exact/strict-JSON cases and a separate loopback `llama-server` child using the explicit managed Candidate file path. The runner uses only a PaRuX-owned detected CUDA/Vulkan runtime, a bounded local HTTP session and child Job Object cleanup; it does not replace the active inference slot or permit external/cloud fallback.
- Quality records now use evidence record v2 while legacy v1 remains readable but ineligible. Each case preserves criticality, finish reason, truncation and pass/fail/inconclusive/error; only `finish_reason=stop` plus the deterministic validator may pass. Suite/request workload and shared runtime configuration have separate fingerprints. The runner hashes the model again under a read-only file-share lease before launch, retains the lease while the child runs, and rechecks candidate bytes before persistence and eligibility. Caller cancellation, Gaming hard-off and timeouts cannot create positive evidence; only complete matching evidence can later be eligible for further review.
- Verification: focused Candidate-evidence/runner tests 46/46; full `PaRuX.Server.Tests` 3,220/3,220 in 5m02s; Server Debug build 0 errors/0 warnings; staged diff check clean; independent review PASS after closing cancellation/Gaming-at-commit, model-file replacement and fingerprint-separation findings. No FlaUI test was applicable because no UI changed. Logs are in ignored `.ai/artifacts/bic16-text-quality-*.log`.
- Code commit: `cf97045` (`feat: add isolated Candidate text quality evaluation`).
- Limits: this is a producer implementation, not a measured model evaluation on this PC. The current session has no usable GPU runtime; no model quality/performance, recommendation, route change or promotion is claimed. The separate performance producer is recorded below and shares this quality evidence's hardware/runtime/binary/configuration while recording its distinct workload. BIC-16 remains PARTIAL and physical-PC acceptance is still required.

### 2026-09-24 BIC-16 isolated Candidate local performance-evidence producer

- Added a fixed `parux.chat.instruction_following.performance` v1 workload with a content/settings hash and exactly three serial, non-streaming samples. It uses the same isolated PaRuX-owned llama-server child, explicit managed Candidate path, hardware/runtime/binary/shared-serving configuration as the quality producer, while fingerprinting the performance workload separately. The parser requires the exact random alias, one nonempty choice, `finish_reason=stop`, and finite positive runtime prompt/decode token counts and timings; there is no synthetic or external fallback.
- Performance evidence v2 stores each sample and validates exact aggregation of prompt/decode throughput and mean completion latency. Legacy v1 remains readable but cannot qualify. Candidate/provenance bytes are rehashed under a read-only share lease and after execution; runtime identity is recaptured; cancellation and Gaming are checked at the final persistence gate. Failed/partial/timed-out/drifted/unavailable runs cannot create positive eligible evidence. No active model slot, route, recommendation or promotion is changed.
- Verification: focused Candidate evidence/quality/performance tests 75/75; full `PaRuX.Server.Tests` 3,249/3,249 in 5m01s; independent Server Debug build 0 warnings/0 errors; `git diff --cached --check` clean; independent static diff review PASS with no findings. No FlaUI run applies because no UI changed. Logs: `.ai/artifacts/bic16-performance-server-full.log`, `.ai/artifacts/bic16-performance-server-build.log`.
- Code commit: `e6c0089` (`feat: add isolated Candidate performance evidence`).
- Limits/next step: no usable GPU runtime was available, so this is not a real model/speed measurement and makes no recommendation/performance claim. A bounded desktop-only job API is recorded below; the UI, end-to-end staged-Candidate product flow and capability/profile routing remain open. BIC-16 is PARTIAL; physical-PC/GPU/thermal/Gaming acceptance remains required.

### 2026-09-24 BIC-16 guarded Candidate evaluation job API

- Added desktop-only `POST /api/v1/models/discovery/{key}/evaluation-jobs`, `GET /api/v1/models/evaluation-jobs/{id}` and `POST /api/v1/models/evaluation-jobs/{id}/cancel`. All require loopback and exact desktop ownership; these endpoints remain excluded from paired-device allowlists. Start accepts only a safe Candidate key and managed basename, returns `202`, and rejects concurrent work with `409` plus `Retry-After`; no filesystem path is accepted or returned.
- The hosted Core job service allows one active evaluation (no queue) and keeps at most 32 terminal summaries for 15 minutes. The start-request cancellation token applies only before acceptance; the background run links explicit cancellation and host shutdown with bounded execution time. Quality must be committed, v2, complete and all-pass before the performance runner can start. Only then does the service recheck evidence eligibility. Status exposes sanitized counts/metrics/error codes, never generated output, absolute paths, raw evidence or exception text. The in-memory job status is ephemeral across server restart; evidence records remain in the existing ledger.
- Verification: focused evaluation-job/discovery/evidence/runner tests 92/92; full `PaRuX.Server.Tests` 3,259/3,259 in 5m05s; independent Server Debug build 0 warnings/0 errors; staged diff check clean; independent static review PASS, including the pre-acceptance cancellation regression. No FlaUI run applies because no UI changed. Logs: `.ai/artifacts/bic16-evaluation-job-focused.log`, `.ai/artifacts/bic16-evaluation-job-server-full.log`, `.ai/artifacts/bic16-evaluation-job-server-build.log`.
- Code commit: `a8212ba` (`feat: expose Candidate evaluation jobs`).
- Limits: no UI currently starts or explains these jobs, no usable GPU runtime was available for real measurements, and the job status registry is not restart-persistent. User consent/notifications, a staged-Candidate end-to-end acceptance path, capability/profile routing, promotion/rollback and target-PC hardware acceptance remain open; BIC-16 is PARTIAL.

### 2026-09-24 BIC-16 scoped capability/profile routing and isolated GPU smoke

- Added versioned, durable scoped routing preferences for each normalized text role × AUTO profile, with `Automatic`, `Prefer` and `Force` modes. Legacy exact global Manual selection retains precedence. `Automatic` preserves existing AUTO behavior; `Prefer` is stored as user intent but does not select an evidence-ineligible Candidate; `Force` is accepted only for a currently registered, provenance-backed Candidate revision and performs an exact fresh load with managed-path checks, byte hashing/read leases, runtime identity verification and no cloud/model substitution. Loopback desktop-owner GET/PUT/DELETE APIs expose the settings; paired-device access remains denied. Persistence mirrors to versioned settings/backup state and fails closed on invalid/future schemas.
- Closed the overhead fixture's remaining host-coupling: runtime ownership follows the test-scoped `LocalAiPaths.TextPort`, while the benchmark now isolates LocalAiPaths root, StateDir and `ModelDownloadService.ModelsDirectory`, then cleans up those fixture-owned temp paths. It no longer probes the user's listener on port 1234 or expands the real installed GGUF library.
- Verification: scoped preference/evidence tests 41/41; final isolated overhead tests 3/3 in 2m35s; full `PaRuX.Server.Tests` 3,275/3,275 in 5m07s with zero skips; after updating this plan, `ActiveReleaseStatusContractTests` passed 5/5; App Debug build passed with 0 errors and 3 existing analyzer/nullability warnings; `git diff --check` clean. Independent review PASS. Logs are in `.ai/artifacts/bic16-scoped-routing-review-fixes.log`, `.ai/artifacts/bic16-overhead-isolated-models-focused.log`, `.ai/artifacts/bic16-scoped-routing-server-full-final.log` and `.ai/artifacts/bic16-plan-status-contract-final.log`. No FlaUI test applied because this slice did not change UI.
- GPU connection is now available on the RTX 5080 (16,303 MiB reported). A manual CUDA `llama-bench` smoke on the local `Qwen3.5-4B-Q4_K_M.gguf`, llama.cpp build 10549, 99 GPU layers, `-p 512 -n 128 -r 3` measured prefill 4,581.1 ± 296.0 tok/s and generation 111.595 ± 0.419 tok/s. This is a host/runtime smoke only: the file is not registered as a revision-bound PaRuX Candidate, has no Candidate quality evidence and does not support a product recommendation, thermal claim or `Recommended` label. Full log: `.ai/artifacts/bic16-gpu-runtime-smoke.log`.
- Code commit: `0ce75b9` (`feat: add scoped capability-profile routing preferences`).
- Limits: BIC-16 remains PARTIAL. The user-facing Candidate evaluation workflow, restart-persistent job summaries, staged Candidate end-to-end flow, evidence-qualified ranking/utility floors, user-consent/notifications, promotion/rollback, gaming/idle/power policy and physical-PC acceptance remain open. Do not make a product-model recommendation from this benchmark or publish a stable release while release blockers remain.

### 2026-09-24 BIC-16 Candidate evaluation UI and stale-job race closure

- Added a Model Hub Candidate panel over the desktop-owner evaluation-job API. Public discovery refresh remains explicit; the panel labels publisher metadata unverified and not a recommendation, and enables evaluation only when the exact Candidate repo/revision/full source path/pinned URL/size and computed SHA-256 match a PaRuX-managed file. Start is explicit, cancel/status are visible, only sanitized metrics are rendered, and restart/expiry loss of the in-memory Core job registry is reported as unavailable rather than as success. No download, recommendation, or routing transition is implied.
- Added stable AutomationIds plus an isolated DEBUG-only synthetic fixture behind `PARUX_UI_TESTS=1` and `PARUX_UI_TEST_CANDIDATE_EVALUATION=1`; the FlaUI flow verifies start/cancel without downloading or running a model. Regression coverage also proves a late 404 or polling error from an older job cannot replace or mark stale a newer job for the same Candidate. Status rendering remains monotonic across queued/running/cancelling/terminal states.
- Verification: App Debug build passed with 0 errors and 6 existing analyzer/nullability warnings; Candidate evaluation FlaUI tests 2/2 passed; `git diff --check` clean; independent delta review PASS after fixing stale 404 and stale polling-error races. The broader UI Regression run in this session was 12/18: all six failures are navigation/UIA `NoClickablePointException` cases tracked separately under BIC-06; no Candidate test failed. No Candidate was actually run on the newly available RTX 5080: its llama.cpp smoke is host/runtime-only, not revision-bound PaRuX quality/performance evidence. Logs are retained under `.ai/artifacts/` and `TestResults/UIArtifacts/`.
- Limits/next: BIC-16 remains PARTIAL. The UI depends on the current ephemeral Core job registry and tells the user when state is lost. Next close the staged Candidate end-to-end acquisition/consent/evaluation fixture and durable job-summary/restart recovery, then evidence-qualified utility/floors, explicit promotion/rollback, Gaming policy and physical-PC acceptance. Keep first-model suggestions at provisional-fit until fresh, matching quality and local performance evidence exist.

### 2026-09-24 BIC-16 Candidate acquisition and pinned-file repair hardening

- Added a desktop-owner-only Candidate download endpoint. Its request accepts only the exact license-review evidence; Core derives repository, immutable revision, full source-file path, URL and destination from the current Candidate catalog. It checks the observation timestamp, follows an authoritative size probe for quota admission, enforces the byte limit while streaming, rejects mismatched Candidate length, and does not trigger automatic benchmarking after transfer. Candidate starts are de-duplicated across ambiguous responses; resume retains the exact known byte count and Candidate completion policy.
- Model Hub now offers an explicit pinned Candidate download after the existing unchecked-by-default license review. A completed file is eligible for local evaluation only after provenance and current-byte SHA-256 verification. “Verify / repair pinned file” retries the exact pinned source and reuses an existing file only after its stored source identity, size, timestamp and SHA-256 revalidate; a same-size modified file is not trusted. UI and click-time gates prevent download/repair while Candidate evaluation is active or its start is unconfirmed, including when managed-file metadata is missing.
- Added regressions for desktop-owner/current-catalog route checks and unknown-body rejection, publisher size under-reporting, streaming byte caps, exact Candidate size, no automatic benchmark callback, duplicate starts, corrupt completed-file repair, resume after unavailable HEAD, consent UI, and FlaUI gates for active/uncertain evaluation and absent managed files. Synthetic UI fixtures perform no network transfer and run no model.
- Verification on 2026-09-24: full `PaRuX.Server.Tests` **3,281/3,281**, zero skips; Candidate FlaUI **6/6**; App Release x64 build succeeded with 0 errors (6 existing analyzer/nullability warnings); `git diff --check` clean. Independent delta review PASS after closing two UI races (same-size tampered file had no repair CTA; uncertain evaluation start could expose repair/download when no managed match). Logs: `.ai/artifacts/bic16-candidate-acquisition-server-full.log`, `.ai/artifacts/bic16-candidate-acquisition-ui-tests-final5.log`, `.ai/artifacts/bic16-candidate-acquisition-app-release-build.log`.
- GPU is connected, but local inspection found no exact revision-bound registered Candidate with a matching managed provenance manifest. The installed `Qwen3.5-4B-Q4_K_M.gguf` remains only the earlier host/runtime smoke, not Candidate quality/performance evidence. No external model/license was selected, no network model download or actual Candidate evaluation was performed, and no product suggestion is promoted.
- Limits/next: BIC-16 remains PARTIAL. Deterministic download→byte verification→evaluation integration evidence, bounded restart-persistent job summaries/recovery, evidence-qualified utility and capability/profile floors, separate promotion consent, promotion/rollback, Gaming/idle/power policy and physical-PC acceptance remain open. Do not label the current synthetic UI test an end-to-end real-model acceptance; keep all first-run suggestions provisional.

### 2026-09-24 BIC-16 restart-safe Candidate evaluation history and staged integration

- Candidate evaluation summaries now persist atomically in the Core state directory with strict schema/size/status validation, a 7-day/32-terminal retention bound, one-active-job bound and sanitized metrics only. On Core restart, formerly queued/running/cancelling jobs become `Interrupted`; they are never resumed, their outcome remains explicitly unknown, and history/storage failures fail closed rather than starting an untracked run. The desktop-only list endpoint and Model Hub reconcile recent Core history after refresh or an ambiguous start without allowing older job state to overwrite newer local state.
- Added a deterministic composition test for discovery → immutable revision-pinned Candidate → explicit license consent → authoritative-size/hash-verified managed download → current-byte snapshot → quality/performance evidence → `EligibleForFurtherReview`. Deliberately altered bytes invalidate the snapshot. This is a fake-transfer/fake-runner integration fixture; it does not execute a model, touch recommendation/routing state or prove GPU task quality.
- Added FlaUI coverage for restart-interrupted status and reconciling an uncertain start from durable history. Candidate FlaUI tests passed **8/8**; focused reconciliation passed **1/1**. App Release x64 build passed with 0 errors and 8 existing analyzer/nullability warnings. The full `PaRuX.Server.Tests` suite passed **3,291/3,291**, zero skipped. `git diff --check` passed. Logs: `.ai/artifacts/bic16-job-persistence-server-full2.log`, `.ai/artifacts/bic16-gpu-overhead-seam-focused.log`, `.ai/artifacts/bic16-candidate-job-ui-tests.log`, `.ai/artifacts/bic16-job-persistence-ui-history-test.log`, `.ai/artifacts/bic16-job-persistence-app-release-build.log`.
- Test-hardware isolation note: the first full run after introducing a test VRAM override had 2 failures in the overhead benchmark because an `AsyncLocal` override did not reliably cross the in-process HTTP test boundary. Replacing it with an instance-scoped override injected into the benchmark's test server made the full suite green without changing the global physical-GPU cache. Focused coverage also verifies the scoped override does not alter the cached hardware probe.
- Independent delta review: PASS for the persistent summaries, restart recovery, API/UI reconciliation, and the follow-up VRAM/FlaUI changes.
- Hardware boundary: the connected RTX 5080 is available, but the current user model directory has no Candidate discovery catalog or provenance manifest. Installed GGUFs therefore remain unregistered artifacts; no actual Candidate GPU evaluation or product suggestion was made. Do not download/run one without an exact Candidate revision/file and its license decision.
- Limits/next: BIC-16 remains PARTIAL. Next add a plainly-labelled provisional-fit/“why” explanation using only known local facts (declared file size, detectable runtime/resource fit where actually estimable, AI-quota headroom separately from destination-volume free space, and license-review state). Unknown facts must remain unknown; no quality/performance evidence means no “best”, “Validated” or “Recommended” claim. Then continue with evidence-qualified capability/profile utility floors, separate promotion consent, restart-safe promotion/rollback and Gaming/idle/power policy. Keep physical-PC acceptance and release blocked until their canonical criteria are met.

### 2026-09-24 BIC-16 explainable provisional fit

- Model Hub Candidate cards now expose an accessible “Provisional fit · why this Candidate” explanation. It distinguishes publisher-declared file size (estimate only) from Core's authoritative transfer-size check, and compares that estimate separately with observed PaRuX shared AI-quota headroom and managed-model destination-volume free space. Both storage observations are explicitly point-in-time, non-reserved and rechecked by Core at transfer admission; missing/negative values stay unknown and no download is triggered by refresh.
- The explanation applies the existing conservative licence-review policy without claiming legal verification, labels exact Candidate hardware/runtime compatibility as not evaluated (GPU/VRAM alone is not a fit proof), and never presents a stale job summary as current validation. A completed historical `EligibleForFurtherReview` result is qualified as the last retained result at run time; missing/failed summaries are not treated as proof that no persisted evidence exists. No “best” or “Recommended” status is produced.
- Debug-only FlaUI fixtures cover separate known quota/free-space values, unknown quota and declared size, review-required and allow-listed licence states, and historical eligibility wording. Candidate Model Hub UI tests passed **9/9**. The broader 10-test Model Hub selection was **9/10**: the sole failure was `Model_hub_is_reachable` navigation with the known FlaUI `NoClickablePointException` tracked under BIC-06; no Candidate test failed. App Release x64 build passed with 0 errors and 8 existing analyzer/nullability warnings. The updated active-status plan contract passed **5/5**; `git diff --check` passed. Logs: `.ai/artifacts/bic16-provisional-fit-candidate-ui-final2.log`, `.ai/artifacts/bic16-provisional-fit-app-release.log`, `.ai/artifacts/bic16-provisional-fit-plan-contract-final.log`.
- Independent delta review: PASS after replacing current-tense/absolute claims with retained-history-specific wording. No model was downloaded or run. Although the RTX 5080 is connected, no exact revision-bound Candidate and matching managed provenance manifest are available in the current user model directory; hardware/GPU product evidence and physical-PC acceptance remain unclaimed.
- Limits/next: BIC-16 remains PARTIAL. All initial suggestions remain provisional until capability- and profile-specific task-quality/reliability floors plus matching hardware/runtime/configuration performance evidence and an incumbent utility margin are implemented. The current deterministic Candidate quality suite covers only `chat.instruction_following.smoke`; exact evidence eligibility is not a coding, reasoning, tool-use or profile recommendation. Next expose/persist that evidence scope and define the smallest versioned fail-closed utility-policy contract without changing routes or promoting a model. Do not invent a winner when profile floors or a comparable incumbent margin are absent. Separate promotion consent, restart-safe rollback, Gaming/idle/power policy, first-run Simple acquisition and physical-PC acceptance remain open. Keep the stable release blocked.

### 2026-09-24 BIC-16 persisted Candidate evidence scope

- New Candidate evaluation jobs now persist the exact capability, versioned quality-suite ID and version, and paired performance-workload ID and version. The identifiers are shared by the runner implementations and public safe job summary; they describe only `chat.instruction_following.smoke` and its fixed local performance workload, not broad coding/reasoning ability or a user profile.
- Legacy schema-v1 summaries with all five scope fields absent remain readable. Partial, malformed, oversized, or nonpositive scope tuples fail store validation. New job snapshots, durable restore, and UI rendering retain the tuple; only the complete recognized tuple is rendered as known evidence, while absent/unknown tuples remain explicitly unclassified.
- Regression checks: focused Candidate job/acquisition tests **17/17**; full `PaRuX.Server.Tests` **3,292/3,292**, zero skipped; new scope-specific FlaUI **1/1**; Candidate Model Hub FlaUI group **9/9**; `ActiveReleaseStatusContractTests` **5/5**; `git diff --check` clean. App Release x64 build succeeded with **0 errors** and 8 existing analyzer/nullability warnings. Logs: `.ai/artifacts/bic16-evidence-scope-server-full.log`, `.ai/artifacts/bic16-evidence-scope-ui-targeted.log`, `.ai/artifacts/bic16-evidence-scope-candidate-ui.log`, `.ai/artifacts/bic16-evidence-scope-plan-contract.log`, `.ai/artifacts/bic16-evidence-scope-app-release.log`.
- Independent delta review: **PASS**. Legacy summaries remain readable, complete tuples survive snapshots/restoration, and UI claims are limited to the recognized smoke suite and paired workload.
- GPU boundary: the RTX 5080 is connected, but there is still no exact revision-bound Candidate with matching managed provenance in the current user model directory. These tests use synthetic fixtures; no model was downloaded or executed and no GPU quality/performance claim or recommendation was made.
- Limits/next: BIC-16 remains PARTIAL. Define the smallest versioned, fail-closed capability/profile utility-policy contract, but do not alter routing or promote any model without validated profile floors, matching Candidate evidence and a comparable incumbent margin. Then continue through separate promotion consent, restart-safe promotion/rollback, Gaming/idle/power policy, first-run Simple acquisition and physical-PC acceptance. Keep the stable release blocked.

### 2026-09-24 BIC-16 fail-closed utility-policy contract

- Added a schema-v1 policy definition that requires an exact capability/profile, quality suite and performance workload, hardware/runtime/PaRuX-build/configuration fingerprints, quality and reliability floors, minimum independent quality runs, an explicit measured performance metric ID/unit/direction, and an incumbent improvement margin. No thresholds or active per-profile policy are shipped by default.
- The validator distinguishes not-configured, unsupported schema/scope, invalid/incomplete, and structurally well-formed policy input. “Well-formed” is not a model evaluation, Candidate validation, utility comparison, winner, routing change or promotion. It supports only the built-in `chat.instruction_following.smoke` suite and paired workload. Metric identifiers and units are shared with emitted evidence (`prompt_tok_per_s`/`decode_tok_per_s` in `tokens/s`, higher-is-better; `completion_ms` in `ms`, lower-is-better); unknown metrics, mismatched directions/units, placeholder fingerprints and noncanonical profiles fail closed.
- Model Hub now says that no capability/profile quality and reliability floors or comparable incumbent-margin rule are configured, so eligibility alone cannot rank or recommend a Candidate. Synthetic tests verify missing configuration, unsupported schema/scope, missing floors, reliability sample bounds, malformed profile/fingerprint, unsupported metrics, units and directions.
- Verification: policy validator tests **6/6**; full `PaRuX.Server.Tests` **3,298/3,298**, zero skipped; Candidate Model Hub FlaUI **9/9**; `ActiveReleaseStatusContractTests` after this plan update **5/5**; App Release x64 build **0 errors** (6 existing analyzer/nullability warnings); independent delta review **PASS**; `git diff --check` clean. Logs: `.ai/artifacts/bic16-utility-policy-focused.log`, `.ai/artifacts/bic16-utility-policy-server-full-final.log`, `.ai/artifacts/bic16-utility-policy-candidate-ui.log`, `.ai/artifacts/bic16-utility-policy-plan-contract.log`, `.ai/artifacts/bic16-utility-policy-app-release-final.log`.
- GPU boundary: `nvidia-smi` confirmed RTX 5080 with 15,977 MiB free. Installed GGUFs have no exact Candidate revision and managed provenance pair (the local repository map records repository names only); no model was loaded, downloaded or evaluated. GPU Candidate evidence and physical-PC acceptance remain unclaimed.
- Limits/next: BIC-16 remains PARTIAL. There is no active policy store, calibration data, evidence aggregation across independent runs, Candidate/incumbent comparator, recommendation lifecycle, route integration, consented promotion or rollback. Next implement an isolated comparison/readiness evaluator that consumes exact policy plus same-hardware/runtime/profile Candidate and incumbent evidence and returns no winner when either side is absent, stale, incomparable, below quality/reliability floors or missing a configured margin. Do not activate policy defaults or change routes. Gaming/idle/power, first-run Simple acquisition and physical-PC acceptance remain open; stable release stays blocked.

### 2026-09-24 BIC-16 reliability-estimator semantics

- Corrected the just-added utility-policy schema to v2: a policy must name the exact reliability estimator and the number of distinct, fully scoped quality-run records; the ambiguous `MinimumIndependentQualityRuns` field is removed. The only structurally supported estimator ID is `fresh-process-suite-pass-rate.v1`.
- Its contract is deliberately narrow: observed success fraction over unique, fresh-process invocations for one exact quality suite/profile/environment cohort, where success means a complete, non-truncated, no-fallback run meeting the separately configured task-quality floor. Matching failed attempts are non-successes. It is not a confidence interval, an independent stochastic sample, or proof of broad model capability. The current runner uses temperature 0 and a fixed seed (17), so repeated generations are intentionally reproducible and must not be represented as independent samples of model quality.
- The validator still checks policy structure only; it does not calculate reliability, select evidence, compare Candidate to incumbent, rank or promote. Current persisted quality records lack a unique invocation ID and measured quality-profile scope; incumbent evidence also lacks a separate trusted identity. No winner is therefore possible yet.
- Verification: policy validator **6/6**; full `PaRuX.Server.Tests` **3,298/3,298**, zero skipped; after the Gaming fixture isolation change, the full Server suite passed again with **3,298/3,298** and Playnite process count **0** after completion. The focused gaming-session test passed **1/1** with process count **0 before and after**. Logs: `.ai/artifacts/bic16-reliability-estimator-focused.log`, `.ai/artifacts/bic16-reliability-and-gaming-server-full.log`, `.ai/artifacts/gaming-orchestrator-no-launcher-test.log`.
- Test isolation: the earlier full-suite run proved that `WorkloadOrchestrator_CanEnterAndExitGamingSession` launched the host's real Playnite Fullscreen app using the default profile. Its process became orphaned after `testhost` exited; it was closed gracefully after confirming its executable, creation time and testhost parent. The fixture now persists a local test profile with launcher/input/suspend providers set to `none`; the test still covers the state transition and active-launcher result without interacting with the user's launcher. This is test isolation, not a Gaming product behavior change.
- GPU boundary: RTX 5080 remains visible to `nvidia-smi`, but no exact revision-bound registered Candidate and managed provenance pair is available. No model was loaded or downloaded in this slice; the earlier local Qwen runtime smoke remains host/runtime evidence only, not Candidate quality/performance evidence.
- Next prerequisites: persist unique quality invocation IDs and exact supported evaluation scope, define complete attempt-cohort accounting, and add separately identified, scope-matched incumbent measurements. Only then implement the no-winner-on-unknown comparator. Keep profiles/policies unconfigured, routes unchanged, and release blocked.

### 2026-09-25 BIC-16 unique quality invocations and exact reliability cohorts

- Quality evidence now records a unique invocation ID plus an explicit, versioned fixed-smoke profile and SHA-256 over its exact system prompt/generation settings. This is not mislabeled as Fast/Balanced/MaximumQuality; the suite's cases/hash and hardware/runtime/binary/configuration/workload fingerprints remain separately scoped. Job summaries retain the profile and invocation ID without exposing model output or paths.
- Quality record v3 is validated strictly; v1/v2 remain readable for history but are not promoted to distinct reliability samples. Duplicate v3 invocation IDs are rejected. The cohort accountant groups only the exact Candidate byte identity + capability + suite/hash + fixed profile/hash + full environment, counts failed/cancelled scoped attempts as non-successes, applies a caller-supplied quality floor, and returns an observed pass rate only when minimum count and accounting completeness are satisfied. Matching legacy/unscoped attempts and a saturated bounded ledger fail closed. It grants no recommendation, route, or promotion authority.
- Test isolation was widened after observing a prior full-suite run write `%LOCALAPPDATA%\\LocalAI` state and launch a host-model child: `TestDataRootIsolation` now supplies a process-local LocalAI state directory and a process-lifetime temporary runtime/model root. Root scopes restore this baseline. The current full run made no llama-server/PaRuX/Playnite process and left the host state-file timestamps unchanged.
- Verification: candidate evidence/job/runner/store focused tests **72/72**; targeted root/status/evidence tests **90/90**; full `PaRuX.Server.Tests` **3,308/3,308** in 5m23s, zero skips. Full log: `.ai/artifacts/server-tests-isolated-root-full.log`.
- Boundary: this remains evidence accounting only. No incumbent-quality identity, multi-candidate comparison, discovery refresh policy, user consent/promotion lifecycle, task-specific route wiring, live Candidate GPU quality/performance run, or rollback exists yet; BIC-16 remains PARTIAL and release stays blocked. Continue with independently identified, scope-matched incumbent evidence and the no-winner-on-unknown comparator only after its remaining cohort rules are accepted.

### 2026-09-25 BIC-16 exact-scope utility comparison primitive (partial)

- Utility-policy schema v3 binds the exact fixed-smoke evidence profile ID/version/hash and the named reliability estimator. It deliberately has no user-facing ExecutionQualityProfile selector: the only quality evidence implemented is `chat.instruction_following.smoke`, so Fast/Balanced/MaximumQuality/Overnight results are not claimed. The required run count is bounded below the 128-record ledger saturation point; 127 is structurally attainable and 128 fails validation.
- Added a pure, fail-closed comparison primitive over a Candidate and a separately selected reference Candidate. Both sides require current pinned Candidate/provenance byte identities, complete exact-scope quality cohorts, and one unambiguous newest performance record under the same hardware/runtime/binary/configuration and workload fingerprints. The configured metric/unit/direction and margin are compared without floating-point rounding; values that cannot round-trip through the bounded decimal comparison are rejected. Missing, stale, aliased, saturated, below-floor, unsupported or ambiguous evidence yields no winner.
- Scope limit: the explicit-reference API never infers its reference. A separate internal resolver now supports exactly one currently active managed Candidate after exact runtime/provenance proof, but it has no production caller yet. Neither path routes, displays a model recommendation, or promotes/changes either model. External/user-managed incumbent identity, profile-specific quality, active policy storage, recommendation lifecycle, task-specific routing, consented promotion/rollback and physical-PC acceptance remain open. All 16 BIC rows remain open/PARTIAL; release stays blocked.
- Active-reference follow-through: added an internal resolver that can supply the comparison primitive with the actually active managed Candidate only when runtime health, the active path, a unique Candidate/provenance mapping, managed-file SHA-256, and the current process-bound SHA/PID/start-time proof all agree. A non-sharing read lease pins the reference bytes for the comparison, with runtime and provenance rechecked before any result is returned. Missing, unhealthy, outside-root, ambiguous, stale, or changing identity fails closed. The explicit-reference API is unchanged; this resolver has no production caller yet and does not alter routing, recommendations, or promotion. Verification: `CandidateEvidenceServiceTests` **39/39** and `CapabilityRoutingPreferenceTests` **11/11**. The proof seam is simulated for resolver tests; no end-to-end comparison against a live llama-server was performed. This is still internal evidence plumbing, not a BIC row closure.
- Evaluation-profile foundation: introduced one server-internal descriptor/catalog for the existing chat-smoke capability, suite, profile, exact hashes/fingerprints, case/repetition bounds and policy limits. Policy validation, evidence cohort selection, environment/workload matching and comparison checks now consume this descriptor; stored IDs, schemas, job output and record versions are unchanged, and the evidence-store validator remains deliberately chat-specific until a second producer has an explicit compatibility design. Legacy v1 suite overlap still makes newer reliability accounting incomplete even if the old case count differs; v3 evidence requires the exact case count/profile. Verification: focused policy/evidence/comparison/store tests **69/69**, full `PaRuX.Server.Tests` **3,330/3,330**, Release x64 Server build **0 errors** (14 existing warnings), independent review **PASS**. No coding profile or model-quality claim has been added; BIC-16 remains PARTIAL and stable release stays blocked.
- Store-validation follow-through: the registered chat descriptor now also supplies ordered quality-case IDs/criticality and performance sample/token/metric bounds to `CandidateEvidenceStore.Validate`. Current v2/v3 quality and v2 performance checks remain exact; historical v1 validation stays on its unchanged legacy branch. No evidence schema, record version, job DTO or serialized output changed, and unknown/mixed scopes remain rejected. Verification after this change: store tests **30/30**, store/profile/quality-runner/performance-runner tests **71/71**, full `PaRuX.Server.Tests` **3,351/3,351**, Release x64 Server build **0 errors**, independent review **PASS**. This is still chat-only persistence support; no coding evidence is produced or accepted yet.
- First-run storage follow-through: the volume-space warning now follows the user's selected general-chat starter instead of the initial recommendation; changing from the 5 GB fixture to a 7 GB fixture against 6 GB free is covered by a deterministic FlaUI test **1/1**. The complete FlaUI project run is **25/28** and the `Category=Regression` subset is **17/17**. Three settings-navigation smoke checks still fail to expose the Settings controls/category to FlaUI (including a UIA COM failure); this is not counted as UI acceptance. The separate task-candidate dialog and storage flows pass their UI checks.
- Verification: utility policy, comparison evaluator and Candidate evidence service tests **52/52** (`.ai/artifacts/candidate-reference-comparison-focused-final2-20260925.log`); full `PaRuX.Server.Tests` **3,322/3,322**, zero skips; official `tools/release-gate.ps1 -SkipAndroidTests` **PASS** (`.ai/artifacts/release-gate-final-20260925.log`), including Release x64 build (0 errors), all five Windows executable publications, VS Code tests **28/28**, and Android safe-parity static boundary. Android tests were intentionally skipped. Active release status remains blocked by 16 open BIC rows and `PHYSICAL_PC_ACCEPTANCE_REQUIRED`; gate evidence is not a source-complete or physical acceptance claim. No model was downloaded or evaluated for this slice.

### 2026-09-25 BIC-13 media RAM admission after runtime replacement (partial)

- Media work now performs a thermal-only check before starting the replacement runtime, then measures strict current system RAM after the media assistant and ComfyUI are live. It no longer speculatively subtracts only the outgoing text model; admission denial/start failure and post-admission failure schedule the normal delayed text restore.
- Verification: media/resource focused tests **12/12** after the final thermal-check contract assertion; full `PaRuX.Server.Tests` **3,352/3,352** before that isolated test addition; Release x64 Server build and Debug x64 App build succeeded; independent diff review **PASS**. Current real-UI profile first rejected video at **94.9% RAM > 86%**, then completed after unloading the 27B text model. A later retry correctly rejected at **88.5%** after text-model restoration. This demonstrates the guard's boundary, not a performance claim.
- Real media acceptance remains partial: 1024² image generation produced a coherent watercolor cottage; the requested mailbox/lavender-sky edit was only a minor variation; 1-second H.264 videos at 320²/8 steps and 512²/30 steps were valid MP4s but visually/semantically poor. Human-quality evaluation remains open; BIC-13 and the stable-release blocker remain open. Artifacts/logs are isolated under `.ai/artifacts/parux-real-ui-20260925/`.
- Follow-up QA: full `PaRuX.Server.Tests` **3,353/3,353**, zero skips (`server-tests-final-20260925.log`). The Release gate was attempted with `-SkipAndroidTests` but stopped at its first build because the live QA-owned `PaRuX.Server` PID **29936** had the Release assemblies loaded; no process was killed and no release outputs were overwritten. FlaUI Smoke was attempted but could not start a separate desktop process while the real app held the single-instance mutex. The Computer Use folder picker is awaiting a human selection/cancel before real UI coding-agent, search and Computer Use tests can continue.

### 2026-09-26 BIC-16 explicit utility-policy persistence and guarded compare API (partial)

- Added an atomic, size-bounded local policy store. Missing state remains `NotConfigured`; writes require an explicitly supplied structurally valid policy, and persisted documents require an explicit supported document schema. Invalid, malformed, unsupported, oversized or reparse-point state fails closed. Reset is explicit and does not synthesize calibration thresholds.
- Added authenticated desktop-loopback-only policy read/write/reset and Candidate-vs-active-reference comparison endpoints. The comparison can report only that a configured margin was met and still requires human review; its response explicitly guarantees no recommendation, promotion or routing change. The unconfigured path returns no winner before resolving runtimes or loading evidence. Devices and addressless/non-loopback requests are denied.
- Verification: focused policy-store, endpoint, policy-validator, comparison and evidence-service tests **72/72**; endpoint-specific tests **3/3**; full `PaRuX.Server.Tests` **3,375/3,375**, zero skipped (**6m12s**); independent re-review **PASS**; `git diff --check` clean. Synthetic policy data is test-only; production remains unconfigured.
- Limits: BIC-16 remains PARTIAL. No production calibration, successful configured live-evidence comparison acceptance, discovery/evaluation UI lifecycle integration, candidate recommendation, route changes, separate promotion consent, promotion/rollback, Gaming/idle/power policy, real-model GPU quality/performance result or physical-PC acceptance is claimed. Continue with the next evidence-qualified workflow slice; keep release blocked.

### 2026-09-26 BIC-16 Model Hub utility-comparison lifecycle (partial)

- Model Hub now reads the explicit Core policy readiness and exposes a manual Candidate-vs-active-model evidence comparison only when an exact managed Candidate has an eligible completed evaluation and a well-formed configured policy. Core revalidates the evidence and reference. UI results retain the API's human-review-only, non-recommendation, no-promotion/no-routing contract; missing policy or unavailable readiness stays fail-closed, with no thresholds inferred.
- Comparison and Candidate file/evaluation mutations are serialized. Refreshes and mutations invalidate prior result generations; delayed responses cannot restore stale evidence, and the active marker remains until its own request completes or is cancelled. Provisional-fit wording follows policy readiness.
- Verification: focused Candidate utility policy/evaluator/endpoint/service suite **31/31**; Candidate Model Hub FlaUI regression group **11/11** and the three focused comparison UI tests **3/3** (the selections overlap); Release x64 App build **0 errors, 6 existing warnings**; independent re-review **PASS**; `git diff --check` clean. Synthetic configured/in-flight policy fixtures are DEBUG-only, in-memory UI state; they issue no request and are not calibration evidence.
- Limits: BIC-16 remains PARTIAL. Production policy remains unconfigured and no real configured evidence comparison was accepted. There is no policy-calibration UI/data, task/profile recommendation, route integration, separate promotion consent, restart-safe promotion/rollback, Gaming/idle/power policy, real-model GPU-quality/performance result or physical-PC acceptance. Keep stable release blocked.

### 2026-09-26 BIC-16 explicit Candidate ignore lifecycle (partial)

- Model Hub now offers an accessible, per-Candidate **Ignore Candidate** action for active discovery entries. The client sends the explicit Core decision and reloads the authoritative catalog; an item is hidden only after the exact key is confirmed with ignored disposition. Missing/unconfirmed catalog data leaves the prior Candidate visible and retryable. Failures are shown beside the row, and the action does not remove model files or evaluation evidence. Refresh/search work is serialized around the mutation so stale catalog results cannot overwrite its outcome.
- Verification: deterministic FlaUI success, server-failure and missing-key/unconfirmed-catalog cases **3/3**; Core authentication/persistence/retention-through-refresh-and-churn tests **2/2**; full `Category=Regression` FlaUI **27/27**; independent diff re-review **PASS**; `git diff --check` clean. DEBUG fixture paths are isolated and do not claim Core persistence; Core route and catalog-store tests exercise the real persistence boundary.
- Limits: BIC-16 remains PARTIAL. This closes only the explicit ignore action; notifications, task/profile recommendation, policy calibration, route integration, separate promotion consent, restart-safe promotion/rollback, Gaming/idle/power policy, real-model GPU quality/performance evidence and physical-PC acceptance remain open.

### 2026-09-26 BIC-04/BIC-16 Model Hub stale-refresh isolation (partial)

- Refresh and search operations now use generation ownership plus cancellation so an older request cannot overwrite newer Model Hub hardware/Candidate fixtures, status text, search results or busy state after an overlapping refresh, fixture application or view unload. `OnShownSearching` stops if its refresh lost ownership before it can launch the follow-up search. This is a production UI concurrency fix with DEBUG fixtures exercising the affected ordering; it does not synthesize hardware or Candidate evaluation evidence.
- Verification: affected shared-memory hardware and first-model tests **2/2**; empty-query idle-status FlaUI **1/1**; full `Category=Regression` FlaUI **24/24** (`.ai/artifacts/bic04-modelhub-refresh-regression-20260926.log`); App Debug x64 build completed with **0 errors**; independent diff review **PASS** after the continuation/search and busy-status guards.
- Limits: no deterministic delayed-provider race test has been added, and live current-available RAM/VRAM pressure, model swaps, Gaming transitions, physical hardware, or BIC-16 recommendation/promotion lifecycle are not validated by this UI fix. BIC-04 and BIC-16 remain PARTIAL.

### 2026-09-26 BIC-09 review-finding workspace safety and stale-state recovery (partial)

- Review locations now reject absolute, drive-rooted and traversal paths and require realpath containment under a current local workspace before opening a source document. Persisted URIs from a moved/changed workspace are unavailable. Finding rows distinguish current, relocated, stale, ambiguous and unavailable locations; source edits, document close and file deletion refresh diagnostics. Disk existence is rechecked even if VS Code retains a deleted document in memory. Stale/deleted locations remain unresolved in persisted state, are never opened at a guessed line, and prompt the user to rerun review. The persisted ledger contains hashes/anchors, not source lines.
- Verification: new registration-level VS Code API-mock lifecycle tests cover relocation, changed targets, deletion with a cached open document, workspace-root changes, repeated Markdown ingest, reload/disposition retention, no source-line persistence, traversal rejection and a deterministic realpath escape seam for symlinks. Provider-backed URI containment tests bind scheme, authority and path boundaries; symlink components are rejected via provider metadata. `npm test` **35/35**. These are API-mock tests, not native VS Code UI acceptance.
- Limits: BIC-09 remains PARTIAL. Native editor navigation, real Git index stage/revert behavior, overlaps/partial acceptance/restart/error recovery and successful-task latency instrumentation/measurement remain unverified.

### 2026-09-26 BIC-15 targeted SQLite pool isolation regression (partial)

- Added a focused regression that leaves an unrelated shared in-memory SQLite store in its pool, releases a backup destination for another store, clears only the target database variants, and verifies the unrelated store still contains its data. It also verifies the backup destination closes and its file can be removed immediately.
- Verification: focused pool-isolation and related repository-index suite passed **48/48**; full `PaRuX.Server.Tests` passed **3,379/3,379**, zero skipped, **6m05s**. The new pool regression participates in this full-suite result; the independent BIC-10/15 diff review found no remaining issue in the changed paths.
- Limits: BIC-15 remains PARTIAL. This does not prove backup/restore consistency during concurrent chat/agent/sync traffic or remove the listener check-to-connect interval. Route-level strict-local coverage, foreign-process arrival/removal under live traffic, and concurrent backup/restore/restart recovery remain open.

### 2026-09-26 BIC-15 strict-local chat route integration (partial)

- The registered `/api/v1/chat/completions` route now has a Windows-only integration regression with an in-process loopback runtime stub. It persists a complete fallback configuration through the production API, reloads it through a fresh service, delivers a prompt to the local runtime, then returns a controlled local HTTP 503. The response reports the local provider error; a loopback TCP observer confirms the configured HTTPS fallback endpoint receives no connection, and the fallback use count remains zero. Runtime files and port are isolated; no llama-server child, external request, real credential or host model is used.
- Verification: strict-local route plus CloudFallback suites **28/28**; the new route test is one of those cases. The integration is intentionally Windows-only because the managed-runtime ownership proof uses the Windows TCP owner table. Independent review confirmed it reaches the post-ready failure/fallback decision path and verifies the absence of remote egress.
- Limits: BIC-15 remains PARTIAL. This covers the registered chat-completions path, not every inference/API surface, listener arrival/removal races under live traffic, or backup/restore during concurrent chat/agent/sync activity. Those remain open.

### 2026-09-26 BIC-16 manual-lock routing latency (partial)

- An exact managed-model lock now bypasses alternative-candidate evidence collection before exact load. Previously this path could enumerate every role candidate and perform repeated capability, benchmark and hardware-fit work, despite being unable to select an alternative. AUTO/Prefer ranking and strict exact-model/live memory admission are unchanged; ranking-only evidence remains unreported for the manual lock rather than being recomputed. Missing/unsafe exact targets still fail closed without substitution.
- Verification: focused `CapabilityRoutingPreferenceTests` **14/14**, including two configured alternatives, dual exact-override precedence, no fallback and retained selected intelligence/42.5 tok/s evidence; full `PaRuX.Server.Tests` **3,470/3,470**, zero skipped, **5m44s**; Server Release x64 build **0 errors** (13 existing analyzer/MCP/nullability warnings); `git diff --check` clean; independent diff review **PASS**. A zero-RAM fixture ensured the evidence route could not launch a real runtime. These checks demonstrate candidate work avoided and behavior preserved, not a measured end-to-end latency improvement.
- Limits: no current live-model/app TTFT or completion-latency measurement was run because no PaRuX/model runtime was active. The exact-parity, same-machine repeated response-speed comparison and successful live exact-route UI acceptance remain open under the BIC-16 benchmark method; this optimization makes no “best in class” claim. BIC-16 remains PARTIAL.
- **Response-speed regression follow-up, 2026-09-26:** the exact managed-model lock now has an isolated streamed-chat integration regression that compares visible response content with AUTO, asserts exact route/no alternative candidates/no fallback, and records submit-to-first-visible and submit-to-last-visible distributions separately after warm-up. The focused test passed **1/1** and the full `ParuxOverheadBenchmarkTests` class passed **4/4** (2m42s). The test uses the deterministic runtime stub and is explicitly orchestration-only: it cannot establish inference speed, competitor parity, or a best-in-class claim. Real same-machine exact-parity measurement remains open.
- **Candidate TTFT and completion evidence, 2026-09-26 (PARTIAL):** the fixed Candidate chat performance workload now consumes a streamed response, records request-to-first-nonempty-visible-content separately from request-to-stream-completion for each sample, and requires explicit independent lower-is-better margins for both dimensions before the evidence comparator can report that a Candidate meets its configured margin. New runs use workload version 2, evidence-record version 3 and utility-policy schema 4; historic v2 performance records and v3 utility policies remain readable but cannot qualify as current evidence. The SSE reader accepts the normal blank separator after `[DONE]` and a terminal `choices: []` usage frame, rejects post-terminator data, and enforces its 1 MiB byte cap before line accumulation. Model Hub Candidate status displays TTFT, decode throughput and completion latency; none of this grants routing or promotion authority.
  - Verification: focused Candidate suites **242/242**; full `PaRuX.Server.Tests` **3,476/3,476**, zero skipped, **6m16s**; App Release x64 build **0 errors** (6 existing analyzer/nullability warnings); `git diff --check` clean; independent code review **PASS**.
  - Real installed PaRuX-only baseline: 20 warm API samples against the UI-reported `Qwen3-Coder-30B-A3B-Instruct-UD-Q3_K_XL.gguf` returned median TTFT **33.79 ms**, p95 **35.83 ms**, median/p95 last-visible completion **524.29/533.76 ms** and end-to-end **186.76 tok/s**. The benchmark records model identity as caller-supplied/UI-reported, not independently attested by the endpoint; this is a single-product baseline, not competitor evidence.
  - Limits: no current same-workload competitor run with exact model-file hash, instructions, sampling, runtime/context and cold/warm parity was completed in this slice. Existing same-machine reference evidence records Ollama faster than PaRuX on one response-speed workload while PaRuX beats LM Studio on throughput; this remains evidence against an unconditional “best in class” claim. Only scoped, repeated, quality/reliability-preserving comparisons can substantiate that claim. BIC-16 remains PARTIAL and the release remains blocked.

### 2026-09-26 BIC-16 explicit criteria UI and stale-comparison guards (partial)

- Model Hub now lets the user configure only the currently supported chat instruction-following smoke comparison. Quality floor, observed reliability floor, distinct run count, TTFT margin and final-response completion margin are separate required fields; new criteria start blank and nothing is represented as automatically calibrated. Core supplies the exact machine/runtime/binary/configuration and evaluation-profile scope. The legacy raw policy PUT now returns `410 Gone`; only the current-context configure route can persist criteria.
- Comparisons hold managed Candidate and reference read leases while consuming evidence, verify the pinned model hashes and recheck Candidate/reference provenance. After comparison, Core rereads both the saved policy and the quality/performance runtime scopes (executable, backend and all fingerprints); any drift or unavailable scope returns `NoWinner` rather than exposing a stale positive result.
- Verification: focused new API/file-lease regressions **4/4**; Candidate utility/evidence/evaluation suite **170/170**; full `PaRuX.Server.Tests` **3,482/3,482**, zero skipped; Model Hub policy/editor FlaUI tests **4/4**; Release x64 Server and App builds **0 errors** (Server retains 13 existing analyzer/nullability warnings); independent diff review **PASS**; `git diff --check` clean.
- Limits: this editor and comparator apply only to the fixed chat instruction-following smoke profile. No live Candidate-vs-incumbent evidence was calibrated or accepted here, no recommendation/promotion/routing change exists, and no exact-parity competitor run or best-in-class claim is established. Quality remains a hard floor separate from first-visible-token and final-response latency. BIC-16 and release remain PARTIAL/BLOCKED.

### 2026-09-26 BIC-16 revision-scoped discovery notice triage and response-speed priority (partial)

- Model Hub groups new, unverified model-discovery notices and provides explicit Evaluate now / Later / Ignore actions. “Later” is persisted against the exact candidate revision for 24 hours; changed revisions become distinct notices. Evaluation is enabled only for an already-installed, provenance-verified exact revision. An unavailable candidate is not downloaded implicitly; its separate download and licence review remain explicit. The pending list is paginated so notices remain reachable beyond the default 50-candidate view. Notice handling does not authorize promotion, routing, installation, or deletion.
- Verification: focused discovery API/store/service tests plus the source-contract regression **22/22**; Model Hub FlaUI scenarios for defer, exact evaluation, ignore, no implicit download, and pagination through notice 61 **5/5**; full `PaRuX.Server.Tests` **3,485/3,485**, zero skipped (7m33s); Server Release x64 build **0 errors** (13 existing analyzer/MCP/nullability warnings); App Release x64 build **0 errors** (6 existing analyzer/nullability warnings); independent review **PASS**; `git diff --check` clean.
- **Response speed is a first-class competitive acceptance target**, co-equal with functional parity, quality and reliability. For every declared interactive workload, compare at exact model-file/revision, tokenizer/template, instructions, sampling/runtime/context/output limits, tool set and equivalent cold/warm state; measure submit-to-first-useful-visible-content, submit-to-completion, real-token throughput, cancellation/recovery and p50/p95 separately. For media workflows, measure time to first usable preview and time to completed output in addition to sustained throughput. A faster result below the quality/reliability floor does not win. A measured competitor win remains an optimization defect until PaRuX ties/wins within the declared tolerance or a genuine hardware/runtime constraint is evidenced. This is an objective to prove per scoped workload, not a claim that PaRuX is already universally best-in-class.
- This Model Hub control-plane/UI slice does not change the generation hot path and has no measured response-speed effect. No live exact-parity competitor run was performed here. BIC-16 remains PARTIAL and the release remains BLOCKED.

### 2026-09-26 BIC-16 current Candidate licence evidence at evaluation start (partial)

- Candidate quality/performance evaluation now fails closed when persisted provenance is legacy/missing, its declared licence differs from the current exact-revision Candidate, or required review acknowledgement is absent. API conflicts have deterministic error codes and Model Hub explains that no evaluation started and points to the explicit pinned-download/licence-review flow.
- Final provenance validation is linearized with invoking the quality/performance runtime start using a short shared metadata gate. Candidate catalog saves and provenance mutations share that gate; it is released as soon as `StartAsync` is invoked, not held through model loading or inference. Reusing a completed pinned download now also requires its saved licence decision to match current Candidate terms, so legacy missing evidence triggers the explicit repair/download flow instead of being treated as current.
- Regression evidence: focused Candidate evidence/job/quality/performance tests **104/104**; Candidate download + quality/performance tests **75/75**; routing plus strict-local integration tests **15/15**; the strict-local listener test repeated **4/4**; full `PaRuX.Server.Tests` **3,491/3,491**, zero skipped (6m26s); Server Release x64 build **0 errors** (13 existing analyzer/MCP/nullability warnings); App Release x64 build **0 errors** (8 existing warnings); independent diff review **PASS**; `git diff --check` clean.
- FlaUI follow-up: an explicit DEBUG-only Model Hub fixture now injects the typed `candidate_license_metadata_changed` rejection inside the production UI `try/catch`, without calling Core or starting a model. The regression verifies the exact-revision licence explanation, explicit no-evaluation/no-file-change wording, unavailable (not running) state, absence of a cancel action, and an enabled retry. Targeted evaluation success/cancel, Evaluate-now acknowledgement and rejection regressions passed **3/3**; existing Core evidence/job/download-route authority regressions passed **4/4**; App Release x64 build completed with **0 errors** (8 existing analyzer/nullability warnings); independent diff review **PASS**; `git diff --check` clean.
- Limits: the FlaUI fixture validates visible rejection handling only; it does not simulate Core's authority, which remains covered by the real Core tests above. This adds no real model quality/performance measurement, GPU acceptance, recommendation, promotion or routing authority; no candidate was evaluated in this slice and no exact-parity competitor benchmark was run. Response speed remains a first-class acceptance target, not a result of this metadata/control-plane change. BIC-16 remains PARTIAL and the release remains BLOCKED.

### 2026-09-23 v1.9.4 candidate hardening and active-status gate

- **Sandbox and local tests:** bounded Windows ACL/AppContainer grant/revoke and cleanup paths, per-test profile/data-root isolation, and regression coverage for safe commands and ACL teardown. The full `PaRuX.Server.Tests` suite passed 3,089/3,089.
- **Chat/UI regression:** the selected code-folder clear action has a real FlaUI regression asserting label reset, hidden clear control and persisted settings reset. The folder-clear, native-picker and new-chat composer tests passed 3/3. The complete UI Smoke set was not green in this session: workspace navigation clicks still fail in FlaUI/UIA with no clickable point; this is not counted as product acceptance.
- **Status contract:** current status docs and scripts now cover all 16 actual table rows, including P0; completion validation fails before artifact changes while any row remains open. The focused status contract passed 5/5.
- **Software gate:** `powershell -ExecutionPolicy Bypass -File tools/release-gate.ps1 -Restore -SkipAndroidTests` passed the 16-row status checks, MCP/WinML invariants, Release x64 solution build, five shipped Windows self-contained publishes, and VS Code tests (28/28). Android unit/lint/build checks were skipped by the explicit switch. Existing analyzer/nullability warnings remain.
- **Release boundary:** this is a validated v1.9.4 candidate, not an accepted release. The canonical gate still reports 16 open BIC rows plus `PHYSICAL_PC_ACCEPTANCE_REQUIRED`; the release is not tagged/published until the documented software and physical-PC acceptance conditions are met. Hardware/performance claims remain unverified on this no-GPU session.

### 2026-09-15 final real UI and same-machine performance pass: competitive, not universally best-in-class

The first complete UI layout used in this pass was rebuilt at `b3a9561`; its App SHA-256 was
`680643023A118721E6A73E0AB2CA87A8A436CF2E6453ABBC1B61689596EF9E79`. A real WinUI crash was
found when custom Runtime/Settings navigation entries were assigned to `NavigationView.SelectedItem`
even though they were not members of that vector. Commit `b3a9561` fixed the selection path and the
rebuilt UI remained interactive. The real window was then exercised across Chat, Create, Projects,
Model Hub, Model Intelligence, Lifecycle Lab, Workflows, Tools & Automations, Observability,
Updates, Runtime, Settings and the General/Models/Chat Behavior/Your PC apps settings subsections.
No blank page, crash or obvious clipping/overlap was observed at the tested 1280x800 layout. Chat
returned `391`, STOP produced `Stopped.`, and an immediate recovery turn returned `caldo`. The
private update endpoint's 404 remained visible rather than being represented as an available
update, and managed CUDA runtime health was visible. This is a strong single-layout functional
UI acceptance, not a multi-DPI accessibility study or a comparative UX winner.

The benchmark harness was corrected in `9e7c28a` to request and record `reasoning_effort=none`,
measure end-to-end completion throughput, and accept the external reasoning/content stream fields
used by current Ollama-compatible responses. The integrated Backend Race now refuses to run while
a PaRuX text model is resident, preventing hidden VRAM contention. Focused Backend Race tests passed
19/19 and all Python benchmark fixtures passed.

Same physical workstation, identical `Qwen3.5-4B-Q4_K_M` tensor payload and Q4_K_M quantization:

- PaRuX standardized stream: median TTFT 63.88 ms, p95 72.03 ms, 162.43 end-to-end tok/s;
- LM Studio: median TTFT 57.40 ms, p95 93.64 ms, 140.72 end-to-end tok/s;
- Ollama: median TTFT 53.42 ms, p95 77.43 ms, 169.45 end-to-end tok/s.

PaRuX beat LM Studio throughput by 15.4%; Ollama beat PaRuX by about 4.3% and had roughly 10 ms
lower median TTFT. A byte-for-byte scan of the two 2,740,937,888-byte GGUF containers found only
258 differing metadata bytes (key ordering around quantization/imatrix metadata); tensor descriptors
and the remainder were identical. This confirms the compared weights/quantization without pretending
the containers themselves have the same file hash.

The real PaRuX optimizer run `31cbb692f7f24b46bab7bd251ba9d48c` selected CUDA0, Q4 KV and
16,384 context, measured 181.2 internal decode tok/s and 108 ms TTFT, peaked at 63 C, 260.16 W and
14,429 MiB VRAM, and reported no thermal regression. A standardized rerun improved PaRuX only to
163.60 end-to-end tok/s (+0.7%), still below Ollama in that workload.

After the canonical gate was made diagnostically stable in `d523d9b`, it passed 2,921/2,921 server
tests and 28/28 VS Code tests, then produced a complete `uiux-d523d9b` layout whose App and Core both
reported product version `1.8.0+d523d9b62f8e2e8a0b644d87ceff97e71a8c2041`. The fresh real window
and exact process paths were checked before further testing. Its integrated sustained Backend Race,
with PaRuX Chat unloaded and the identical Ollama model declared comparable, completed both sides:
PaRuX 177.44 decode tok/s, 140.86 ms TTFT and 176.62 sustained tok/s; Ollama 17.44 initial decode
tok/s, 166.74 ms TTFT and 120.19 sustained tok/s. The integrated scorer selected PaRuX. This does
not erase the independent standardized result above: PaRuX is demonstrably competitive and wins
the optimized integrated workload, but an unconditional `BEST_IN_CLASS` performance or UX claim
remains **INCONCLUSIVE**.

### 2026-09-15 real local-AI artifact acceptance: cold-start/concurrency fixed and measured, best-in-class not certified

The first published output was deliberately rejected as a live product artifact: the App-only
directory `artifacts\\validation\\release-gate-publish\\app` contained `PaRuX.exe` but no
co-located `PaRuX.Server\\PaRuX.Server.exe`, so the real app launched with the truthful
"PaRuX Server is missing" repair message. A complete test layout was then assembled from the
same-commit App and Server publishes, and every subsequent live test used the complete layout
only.

Commit-linked artifact used for the final live run:

- `artifacts\\validation\\live-fixed2-layout\\PaRuX.exe`, 348,160 bytes, SHA-256
  `29920AC2DE937ABF9572AED304F899150D365B4AB968571BF40B70ADC4F6D696`;
- `artifacts\\validation\\live-fixed2-layout\\PaRuX.Server\\PaRuX.Server.exe`, 152,064
  bytes, SHA-256 `9D552002D46AE2C292BB3408FFDA83414D3D69F635AD4357445CBC52B6C783B6`;
- both report product version `1.8.0+8ac26fdafb9b43bc9fcf04554bb127ae6aded19d`, with source
  commit `8ac26fdafb9b43bc9fcf04554bb127ae6aded19d`.

The real managed local path was exercised through `/api/v1/chat/completions` with remote
providers disabled and the installed Qwen3.8-27B Standard GPU profile on the reference PC
(25.81 GB RAM; AMD Radeon 780M plus NVIDIA RTX 5080; roughly 16 GB largest dedicated GPU).
Before the fix, a successfully resident model was rejected on the next chat by the total-RAM
admission guard (HTTP 503 at approximately 95.7% RAM). `LocalRuntimeService` now reuses a
healthy managed resident without repeating the pre-start admission check, while still checking
admission when it must start or repair a resident. `ChatProxyService` now gives a cold managed
runtime one start attempt before declaring an unowned external listener, preserving the prompt
egress boundary. Focused regression coverage passed 43/43 and the complete software gate passed.

Fresh live evidence from the complete layout:

- cold concurrent requests: 2/2 completed; arithmetic `391` and translation `La casa rossa.`;
- cancellation after first content: passed (`Il`, 245.50 ms); recovery request: passed (`Caldo`,
  353.33 ms TTFT);
- warmed 27B API chat: 10/10 completed, median TTFT 259 ms; first run 628 ms, subsequent runs
  252–266 ms; provider reported as PaRuX-managed local runtime and remote opt-in was false.

This is real local-runtime evidence, not a UI or competitor win. The current computer-use surface
exposed no native Windows app, so no fresh WinUI screenshot/UI Automation pass was possible in
this session; UI/UX remains `INCONCLUSIVE`. No authorized/current competitor endpoint was
available for a fair same-machine matrix with identical model, quantization, context, quality,
warm/cold state, concurrency and cancellation policy; `BEST_IN_CLASS` therefore remains
`INCONCLUSIVE`, not claimed. The MSI file is not rebuilt or elevated-tested by this entry.

### 2026-09-09 (continued) conversation branch Previous/Next/Versions navigator never rendered — WinUI lifecycle defect found and fixed

Continued real-UI physical-PC testing. Regenerated a message with genuinely different content and
confirmed via direct SQLite inspection of `parux.db` that a real, correctly-structured
`ConversationBranch` entry was created and persisted — but the Previous/Next/Versions navigator
documented in `docs/PHYSICAL_PC_ACCEPTANCE_2026-09-08.md` and
`docs/PARUX_MAIN_SOFTWARE_CLOSEOUT_2026-09-05.md` as "fully implemented" was completely absent from
the running app: an exhaustive Windows UI Automation scan of the live window found no "Versions",
"‹"/"›", or counter control anywhere in the tree.

**Root cause, confirmed live with a temporary diagnostic log:** `ChatView` is a plain `UserControl`
with no `Style`/`ControlTemplate` of its own, so WinUI never actually invokes its
`OnApplyTemplate()` override — the only call site for `InitializeConversationBranchHistory()` and
`InitializeBoundedChatRendering()`. The app still appeared to work because the constructor's
original unbounded `Loaded`/`RebuildMessages()` path was never replaced, and
`InitializeNativeCodingHandoffUpgrade()` happened to survive only because it had grown a second,
redundant call site from `ModelSelector_Loaded`. Moving the three initializer calls into the
constructor (right after `InitializeComponent()`) was also confirmed too early: the same diagnostic
log showed `SkillSelector` is already assigned there but `SkillSelector.Parent` is still `null` at
that point. `ChatView.OnLoaded` is the first point in the real lifecycle where the element is
attached to the live visual tree with `SkillSelector.Parent` populated.

**Fix:** call all three (idempotent, flag-guarded) initializers from the top of `ChatView.OnLoaded`
in `ChatView.xaml.cs` instead of only from the dead `OnApplyTemplate()` override (kept as a harmless
defensive no-op). Verified live end-to-end on the real packaged app after republishing: the
`‹ 1/1 › Versions` cluster now renders next to the Skill selector on a fresh empty conversation
(all three correctly disabled), and on the real conversation with the persisted branch it shows
`2/2`, clicking `‹` correctly restores the alternate regenerated path (with a genuine text diff
visible) and re-enables/disables the buttons correctly, and `Versions` opens a dialog correctly
listing the retained alternative with its timestamp/reason/preview. Added
`ChatViewBranchNavigatorLifecycleTests.cs` (source-text contract test, since PaRuX.App's WinUI
target can't be referenced from the xunit test project) asserting `OnLoaded` wires all three calls;
confirmed it fails against the pre-fix source and passes against the fix.

Full `tools\release-gate.ps1 -Restore` re-verified green after this fix.

### 2026-09-09 (continued) real-UI regression sweep after the branch-navigator fix: Automations, STOP/cancel, and conversation delete all verified healthy

Continued real-UI testing on the rebuilt/republished packaged app (driven via Windows UI Automation
`InvokePattern`/`ValuePattern` for precise, unambiguous delivery, since `TextInputHost.exe`
intermittently steals OS foreground focus and silently swallows raw clicks/computer-use clicks
without visibly erroring — the same known environmental quirk documented earlier this session).

- **Tools & Automations** (`Skills & Permissions`, `MCP Servers`, `Automations` trigger builder,
  `Workflow Builder`): created a real automation end-to-end (name, `component_health_changed`
  trigger, JSON config, agent goal template) — persisted correctly and appeared with a live
  enabled/`never triggered` state; deleted it afterward, confirmed removed. MCP servers
  (`Everything`, `Browser (Playwright)`) show connected with real tool counts.
- **STOP/cancel mid-generation:** sent a long-form prompt, confirmed a live streaming response with
  real per-turn routing telemetry (`Qwen3.5-4B-Q4_K_M.gguf`, 27.8 tok/s, hardware-fit/swap-cost
  reasoning in the tooltip), invoked the real `StopButton` mid-stream — generation halted cleanly
  mid-sentence with a `*Stopped.*` marker, composer correctly reverted to Send, and a follow-up
  message sent immediately after was answered normally with no hang or leaked state.
- **Conversation delete:** initially appeared broken (the sidebar still listed the conversation and
  both `%LOCALAPPDATA%\PaRuX\parux.db` and the server's separate
  `%LOCALAPPDATA%\PaRuX\server\conversations.db` still had the row after clicking the dialog's
  Delete button). Investigated by reading `HistoryStore.Delete` (`src/PaRuX.App/Services/HistoryStore.cs`)
  and `ConversationStore` (`src/PaRuX.Server/ConversationStore.cs`) — confirmed by design these are
  two independent SQLite stores (App-local `parux.db` for the desktop sidebar, Server-owned
  `conversations.db` so paired devices converge on one logical history), kept in sync by
  `ConversationSyncClient`. Re-ran the exact same delete via UI Automation `InvokePattern` (bypassing
  the flaky synthetic click entirely) and it worked correctly end-to-end: removed from
  `parux.db`, removed from the server's `conversations.db`, gone from `/api/v1/conversations`, and
  gone from the sidebar. Conclusion: the first attempt was a missed dialog click (the same
  TextInputHost-driven input-delivery quirk, not a product defect) — no code defect found, no fix
  needed.

- **Coding Agent handoff routing:** an explicit plain-language request ("Start a PaRuX coding
  agent task: create a small multi-file Python CLI project...") was AUTO-routed to the `Computer
  Use` skill, not Coding, and failed safely and honestly ("Computer Use failed: the model currently
  loaded is text-only... no GPU with at least 12GB VRAM was detected") rather than hanging or
  faking success. Traced this to `src/PaRuX.Server/SkillRouter.cs`: the native Coding Agent is
  deliberately excluded from the AUTO skill pool (`RetiredCodingSkillId = "coding"`) by design, per
  its own comment - "granting a manifest name must never create filesystem/shell authority inside
  an ordinary chat completion." Not a bug: this is the correct, deliberate safety boundary; AUTO
  picking `Computer Use` over `General` for this ambiguous prompt is a minor classification nit, not
  a security or correctness defect, and it still failed closed. The real, documented entry point is
  the explicit `/code` slash command (`src/PaRuX.App/Services/RouterService.cs` /
  `CodeHandoffService.cs`): tested it live and it correctly produced a `code-handoff` action message
  with working `Start PaRuX Coding Agent` / `Run PaRuX Coding Agent (local)` buttons, and clicking
  `Start PaRuX Coding Agent` correctly opened a real native Windows folder picker to choose the
  target repository. Testing stopped there: driving that system picker's content requires either a
  real user or an explicit computer-use grant for `pickerhost.exe`, which was declined when
  requested this session - respected as a permission boundary, not forced. Full click-through
  (folder selection → worktree creation → checkpoint/verification) remains to be exercised
  interactively by the operator or in a session where that grant is given.

No new source defects found this round; no gate re-run needed since no source changed.

### 2026-09-09 (continued) real user report: Quick Chat bubble not docking bottom-right by default - a real, confirmed, fixed defect

The user directly reported the bubble was not sitting at the bottom-right by default as previously
fixed/confirmed this session. Investigation via UI Automation found the actual on-screen bubble
window (previously and repeatedly mistaken across this session's own screenshots for an unrelated
OS taskbar/preview artifact - it renders as a small "PRX PaRuX" pill) sitting near screen-center
(~640,437 physical), while `%LOCALAPPDATA%\PaRuX\settings.json` recorded a legitimate bottom-right
manual position (`BubbleX`/`BubbleY` near the real screen edge, `BubbleManuallyPositioned: true`).

**Root cause, confirmed live:** `BubbleWindow`'s constructor (`src/PaRuX.App/Windows/BubbleWindow.xaml.cs`)
called `RestorePosition()` before `SetCollapsedGeometry()`. `RestorePosition()`'s clamp-to-monitor
math (protecting against a saved position from a display configuration that no longer applies)
reads `AppWindow.Size` to compute how far right/down the window can legally sit - but at that point
in the constructor the window still has WinUI's oversized default size, not the real 132x44 logical
collapsed pill `SetCollapsedGeometry()` sets moments later. The oversized size made the computed
`maxX`/`maxY` far smaller than the screen actually allows, so a perfectly legitimate saved
bottom-right position was clamped down to somewhere near screen-center on every single launch.

**Fix:** swapped the two calls (`SetCollapsedGeometry()` now runs first) and replaced
`RestorePosition`'s duplicated inline clamp math with the existing, already-tested
`BubblePlacementPolicy.Clamp` helper (`src/PaRuX.App/Services/BubblePlacementPolicy.cs`) instead of
a second hand-rolled copy. Verified live end-to-end on the rebuilt/republished app: with the true
default state (never manually moved), the bubble now docks correctly at the main window's
bottom-right, clearly above the Send button. Directly reproduced and fixed the exact regression
scenario by writing a simulated prior manual position (2200,1400) into settings.json and relaunching
- before the fix this got silently relocated; after the fix it restores to the exact saved
coordinates. Added `BubblePlacementPolicyTests.Clamp_leaves_a_saved_bottom_right_position_untouched_when_it_already_fits`
(pins the pure clamp invariant given the real collapsed size) and
`BubbleWindowRestorePositionOrderTests` (a source-text contract test, since PaRuX.App's WinUI
target can't be referenced from the xunit project, asserting the call order and that
`RestorePosition` uses the shared `Clamp` helper) - both confirmed to fail against the pre-fix
source and pass against the fix.

Full `tools\release-gate.ps1 -Restore` re-run after this fix: green, 2658/2658 core tests.

### 2026-09-09 (continued) Runtime Manager advisor bug found while testing "Optimize for this PC", plus a second real HotModelPoolCatalogTests flake found and properly fixed

Continued systematic real-UI testing (Runtime Manager this time). Ran the real "Optimize for this
PC" flow end-to-end: it correctly benchmarked several llama.cpp configurations, validated the
winner through an isolated llama-server, raced serving concurrency and KV-cache precision, and
landed on a real result (Balanced Q8 · C4 · 24.4 decode tok/s · 43 prompt tok/s · TTFT 1656 ms).

**Real, confirmed, fixed defect:** the PaRuX Optimization Advisor said "Speed: no supported GPU is
active; configure a detected accelerator" - directly contradicted by the benchmark that had just
run using the real AMD Radeon 780M iGPU. Root cause: `RuntimeManagerHardwareStatus.GpuDetected`
(`src/PaRuX.Server/RuntimeManagerService.cs`) is deliberately narrow - it comes from an
nvidia-smi-only probe (`LocalAiProcessManager.ProbeVramMibAsync`) and means "an NVIDIA GPU with at
least 12GB VRAM for the CUDA flagship profile", always false on any non-NVIDIA machine by design
(the diagnostics message that also uses it, correctly, says exactly that). `BuildAdvisorSummary` in
`src/PaRuX.App/Views/RuntimeManagerView.cs` reused that same narrow flag as a blanket "no GPU active"
claim. Fixed by using the vendor-agnostic DXGI-based `GpuAdapters`/`GpuLargestDedicatedVideoMemoryBytes`
(the same inventory Model Hub's own hardware-fit estimator already uses to show "AMD Radeon 780M
Graphics 5.9 GB" correctly) as the real "is any GPU present" signal, and rewording the
flagship-ceiling message to explain the real 12GB-NVIDIA constraint with the real detected VRAM
instead of implying nothing is configured. Verified live: the advisor now reads "Speed ceiling: no
NVIDIA GPU with at least 12GB VRAM was found (largest adapter: 5.9 GB), so PaRuX's CUDA flagship
profile isn't offered here - the measured winner above already reflects the best fit for this
hardware."

**Deeper finding, not fixed - flagged for dedicated follow-up:** the same NVIDIA-only
`GpuDetected`/`VramTotalMib` pair also drives real functional decisions, not just advisor text -
e.g. `ContextMemoryBudget.For` (`src/PaRuX.Server/ContextMemoryBudget.cs`) decides the entire
context-window/KV-cache-offload budget on `hasGpu = status.GpuDetected && status.VramTotalMib > 0`,
which is always false on this machine even though the model genuinely runs on its GPU via the
Vulkan backend - so the budget math may be treating a real GPU-resident model as if it ran purely
in system RAM. Also referenced in `SmartRouterService.cs`, `Program.cs` (three call sites) and the
`"text-cuda" : "text-vulkan"` backend-selection line in `RuntimeHardwareEndpointExtensions.cs`.
Deliberately not touched this pass: `RuntimeManagerService.GetStatusAsync` and its VRAM probe sit on
a documented hot path (re-checked on every routed chat request, which is exactly why the nvidia-smi
probe is cached - see `LocalAiProcessManager.GetVramMibAsync`'s own comment), and correctly making
`GpuDetected`/`VramTotalMib` vendor-agnostic there would touch every one of those call sites at
once with no way to verify all of them in this pass. Needs its own dedicated, carefully-benchmarked
fix rather than a hasty change to a per-request hot path.

**Second, unrelated real defect found investigating an intermittent gate failure while committing
the advisor fix:** the full gate failed once on `HotModelPoolCatalogTests.Build_ExcludesProjectorAndDraftFiles`
(passed cleanly in isolation, and again on an immediate full-suite rerun - genuinely intermittent).
This is the same class of bug an earlier pass in this same file already fixed once
(`LocalAiPaths.UseRootForTests`), but that fix only covered half of it. Root cause: `HotModelPoolCatalog.Build()`
also calls `DiscoverKnownCandidates()`, which unconditionally scans `ModelDownloadService.ModelsDirectory`
for `*.gguf` files as additional real candidates - and that directory resolves through
`PaRuXDataRoot.Path`, a single process-wide static shared by every test in one run (by design, per
its own doc comment), not something `UseRootForTests` touches. Confirmed live: a concurrently-running
`ModelDownloadServiceTests` test (which by its own, separate, correct design writes real GUID-named
`.gguf` files into that same shared directory) got picked up mid-run and crowded the test's own
`normal.gguf` entry out of the hot-pool's capped slot count. Fixed by adding
`ModelDownloadService.UseModelsDirectoryForTests` (an `AsyncLocal`-backed test seam mirroring
`LocalAiPaths.UseRootForTests`/`UseStateDirForTests` exactly) and scoping it in
`HotModelPoolCatalogTests` alongside the existing `LocalAiPaths.UseRootForTests` - `ModelDownloadServiceTests`
itself is untouched, since it deliberately exercises the real shared path by design. Verified
`HotModelPoolCatalogTests` + `ModelDownloadServiceTests` together (31/31) after the fix.

Full `tools\release-gate.ps1 -Restore` run after both fixes: green, 2662/2662 core tests.

### 2026-09-09 (continued) PaRuX Virtual Context never worked with the shipped model: one Jinja rule made every compacted turn answer nothing

The largest defect found in this pass. **Any** conversation or message that outgrew the physical
window came back empty — the long-context capability PaRuX advertises was silently inert on the
default Qwen model family, in both of its paths.

**How it was cornered.** A size sweep through the Core located the boundary exactly: 601, 3,251 and
10,671-character prompts all answered `BLUEBIRD` (a codeword planted in the text); 31,871 characters
returned nothing, in 9 seconds. The same 31,871-character prompt sent straight to llama-server
answered `BLUEBIRD` at 6,030 prompt tokens, so neither the model nor the window was the problem. The
only difference between the working and failing requests was the `context_compacted` event — the
compaction path itself. An 83-turn conversation, which exercises the *pre-existing* history path
rather than anything added in this pass, failed identically, proving the defect was shipped rather
than introduced here.

**Root cause.** Compaction was the only path that rewrote the message list, and it inserted the
Virtual Context digest as its **own** system turn, after the existing system prompt. Replaying that
shape directly against llama-server returned HTTP 500:

```
Jinja Exception: System message must be at the beginning.
```

Qwen's chat template raises on any system message that is not first. The proxy surfaced that 500 as
a stream that simply ended, so the user saw an empty reply and no reason at all.

**Fix** (`ConversationWindowPlanner`): the digest is folded into the single leading system message
instead of becoming a second one, for both the history path and the oversized-single-message path.
A pre-existing test asserted the system turn's content *equalled* the prompt verbatim, which encoded
the broken shape; it now asserts the prompt survives inside the leading system message, which is
what that test was actually about.

**Verified live, both paths:** a 31,871-character single document and an 83-turn / 51,930-character
conversation now both answer `BLUEBIRD`. The conversation case is the stronger evidence — the
codeword sits in the *first* message, recalled through the digest after 80 turns of filler that no
longer fit the window, which is precisely what Virtual Context promises.

**Also fixed on the way in:** a single oversized message never reached Virtual Context at all. The
planner keeps the newest turn whatever its size and handed it to the runtime whole, which returns
nothing. It is now paged through the same machinery — verbatim opening and ending, where the
instruction and the question live, with the middle archived and recalled against them — so a pasted
document is answerable rather than refused. Reported honestly: the plan is always marked compacted
and names the real figures.

**Worth recording for later, not fixed here:** PaRuX estimates 3.0 characters per token, but the
model's own tokenizer measured **5.29** on the same English text — a 76% over-estimate that triggers
compaction on prompts which would have fitted verbatim. Correcting it would reduce needless
lossiness, but the constant is used across planning paths and under-estimating would overflow the
window, so it deserves its own measured change rather than a late-session tweak.

*Done in the next entry — and the "over-estimate" half of that note turned out to be only half the
story.*

### 2026-09-10 Any local process could share PaRuX's runtime port and receive the user's prompts

Found while looking for a competitor to benchmark against, which is a reminder that the useful
finding is rarely the one being looked for.

PaRuX serves its managed llama.cpp on 127.0.0.1:**1234**. llama.cpp's own default is 8080, so 1234
was chosen deliberately - and it is also **LM Studio's default server port**. That made the question
"what happens when something else is on 1234" worth asking rather than assuming.

**First measurement.** With PaRuX's model unloaded, a llama-server started by hand on 1234 - no PID
file, `--gpu-layers 0`, a 4,096 context PaRuX never chose - answered a user's chat through PaRuX,
while PaRuX's own Runtime Manager correctly reported both backends as `running=False healthy=False`.
`IsTextHealthyAsync` asked the port for `/health` and adopted whatever answered.

**Second measurement, which made it worse.** The first fix asked whether PaRuX's pid was *among*
the listeners. That sounds equivalent to ownership and is not: llama.cpp sets `SO_REUSEADDR`, so a
second server binds the same address and port while the first is still serving. Observed live: pid
20940 (PaRuX's own) and pid 23892 (started by hand) both listed as listening on 127.0.0.1:1234 at
the same moment, with Windows free to hand new connections to either.

So a foreign process does not have to win the port to read the user's prompts, documents and
retrieved context included. It only has to **share** it - which it can do while PaRuX's runtime is
running perfectly and nothing in the interface looks wrong. Sole ownership is the only property
worth checking.

**Fix.** `ManagedListenerOwnership` reads the Windows TCP table and requires that *every* listener
on the port be the process PaRuX started. `LocalAiProcessManager.IsManagedTextRuntimeOwned` caches
that for two seconds - PID file plus TCP table, no HTTP - so it can sit on the chat path without
reintroducing the per-request round trip that once cost 2.2 s. `ChatProxyService` refuses with 503
before writing anything downstream, naming LM Studio and pointing at the external-provider path for
a runtime the user manages on purpose.

**The gate caught a real regression in the first attempt, and the fix for it was nearly a mistake.**
Tightening `IsTextHealthyAsync` made PaRuX conclude "no healthy runtime" during the overhead
benchmarks, so it tried to *launch* a real llama-server on the port the test's in-process stub
already held, which failed to bind. The quick answer was an `if (testing)` branch in the security
check; it was written and then removed. The rule that replaced it is about facts rather than about
tests: a host serving the runtime port from inside Core's own process **is** PaRuX. Core listens on
51123 and never on 1234 in production, so the branch is unreachable there, and production and test
now run the same code.

**Verified live, four times:** defect reproduced (foreign server answered `CUCKOO`); first fix
partial (`providerReady` correctly False, chat still answered); sole-ownership fix refuses with 503;
and after the in-process-owner rule, still refuses - with no false positive, a single listener whose
pid matches the PID file answering `RIPRESA` normally.

### 2026-09-10 PaRuX reported its own prompt throughput 6.6x lower than the machine could do

Taken up under "best in class for performance, not only for feature count". The first thing worth
doing was not making PaRuX faster but finding out whether its own numbers were true. They were not.

`promptTokensPerSecond` is read straight from llama.cpp's `timings`, which is honest as far as it
goes. But `prompt_per_second` divides prompt tokens by the entire prefill, fixed start-up cost
included, so on a short prompt it reports latency wearing throughput's units - and the chat
benchmark's prompt is 32 tokens. Measured against the served model on the physical PC:

| prompt | prompt_n | prompt tok/s |
|---|---|---|
| PaRuX's own chat benchmark | 32 | **42.7** |
| 512-token | 610 | 202.3 |
| 2,000-token | 2,410 | 280.8 |
| 6,000-token | 7,210 | **283.7** |

The Runtime Manager printed "43 prompt tok/s" for a machine doing 284. Any comparison a user or a
reviewer drew from that number was wrong by 6.6x, against PaRuX.

**Fix**: prompt throughput is measured on its own ~2,000-token prompt, sized where the curve
flattens - past that it costs seconds and tells us nothing new. Time-to-first-token stays on the
short workload prompt, because TTFT is what a person waiting on a chat actually feels, and the two
questions are different. The regression tests pin the property (the throughput prompt must be long
enough for prefill to dominate) rather than the figure, since 284 only describes this laptop.

**Also measured, and left alone because it is already right:** prompt cache reuse turns a 4,846 ms
cold first token into 551 ms warm, an 88.6% improvement, on the second turn of a conversation.

### 2026-09-10 A training advisor that argues against training

The other half of the same request: suggest fine-tuning that would actually help. The honest answer
is usually that it would not, and building the feature around that was the whole design problem.

"Fine-tune it on your data" is the advice local-AI tools most often give and most often should not.
It costs a user hours and a GPU they may not have, and the common causes of a bad answer - the
prompt, the retrieved context, the quantization, the routing - are all faster to change and easier
to undo. So `TrainingAdvisorService` defaults to no and has to be argued into yes by evidence PaRuX
actually holds: at least 20 judged Eval cases, and a pass rate under 70%. Above 90% it says
explicitly not to train. In between it sends the user to prompting and retrieval first. With no Eval
history at all it refuses and says what to collect.

Every recommendation carries the measurement behind it, enforced by a test that walks the whole
report - a suggestion a user cannot check is one they have to take on faith, and a local-first
product has no business asking for that.

**The speculative-decoding trap, caught in design rather than in the field.** A draft model is the
largest decode win available without changing the model that answers, so the advisor offers it. The
obvious heuristic - same family, much smaller - would have recommended `Qwen3-Embedding-0.6B` or
`qwen3-reranker-0.6b` on this very machine: both are Qwen, both a fifth the size of Qwen3.5-4B, and
neither can generate a single token. The candidate search excludes them by name and the exclusion
has its own test.

**Verified live on the physical PC.** The advisor returned three recommendations against real
measurements: speculative decoding with *"Nothing in your library fits"* and the reason, "do not
fine-tune yet" with what to collect, and prompt-cache reuse confirmed at 89% with the cold/warm
figures.

### 2026-09-10 A browser door and a provider door, both off by default

PaRuX could not be reached from another computer by a browser, and could not be used as a model
provider by another application. Both are now possible, and both are built so that turning one on
buys exactly what it says.

**Browser access.** The web client existed but was loopback-only by explicit design, and the LAN
path rejects any request carrying an `Origin` header - which is every browser. Rather than weaken
that rule, browsers got their own boundary: passphrase (PBKDF2-SHA256), `HttpOnly`/`Secure`/
`SameSite=Strict` session cookie, a CSRF token echoed in a header on every unsafe request, and
lockout after five wrong guesses. The reverse proxy runs on the same machine and terminates TLS, so
PaRuX opens no port and needs no firewall rule.

The reasoning that shaped it, recorded because it is easy to lose: with a proxy in front, a request
that travelled from the public Internet arrives from 127.0.0.1, indistinguishable from a process on
the user's own desktop. Loopback stops being evidence of anything, so the passphrase is the only
real frontier.

**Provider access.** PaRuX already spoke OpenAI at `/v1`; it now also speaks Ollama. It depends on
Ollama for nothing - no process, library or binary - and does not impersonate it either. Durable API
keys make the surface usable by a third-party app, and `ProviderSurface` confines them by an
explicit allow-list rather than a "not /api/v1" exclusion, so a route added later is closed until
somebody deliberately opens it.

**Found live and fixed:** `/app` in a fresh browser returned a bare `{"error":"Unauthorized"}` with
nothing to say the door was at `/app/login`.

**Rewritten because it was not good enough:** the first Ollama streaming path buffered the whole
answer and emitted two frames. It produced the right bytes and passed a round-trip test, and it was
still wrong - a client watching `/api/chat` saw nothing until the model finished, which is what
streaming exists to prevent. Measured after the rewrite: 13 NDJSON frames at distinct times, one
token each.

**Verified live:** wrong passphrase refused, right one signed in, the browser answered `ORIZZONTE`
in streaming and the conversation appeared in the desktop app's own list; `FARO` through Ollama
`/api/chat`; `STELLA` through OpenAI `/v1` SSE.

### 2026-09-09 (continued) The context planner was guessing token counts, and the guess was wrong in both directions

Taken up as the measured change the note above asked for. Measuring the served Qwen tokenizer across
content types killed the premise that one constant could work:

| content | chars/token |
|---|---|
| English prose | 5.66 |
| Italian prose | 4.24 |
| C# source | 4.43 |
| JSON | 2.23 |
| logs | 2.15 |
| git SHAs, hex tokens | **1.16** |

A 4.9x spread. So the assumed 3.0 does not merely over-count prose and fail safe: on identifier-dense
text it **under-counts by more than 2x**, and the planner then sends a prompt far larger than the
window it just computed. Two defects followed from that, both found live on the physical PC.

**1. Slices were sized by the constant and never checked.** `TakeTokensFromStart`/`FromEnd` turned a
token budget into characters by multiplying by 3.0 and returned that slice as-is; the page splitter
did the same. Against dense text every edge was about three times the budget it was cut to respect.
Now each measures the ratio of the text in front of it, then shrinks by the measured overshoot until
the slice really fits, with a final one-character-per-token bound that always holds. Regression test
`An_oversized_message_stays_inside_the_window_when_the_tokenizer_is_denser_than_the_heuristic` fails
on the old code with *"The plan sends about 3.929 tokens into 3.584 usable ones"*.

**2. The chat path now counts with the model's own tokenizer.** The old comment justified the
heuristic by the cost of a `/tokenize` round trip. Measured on loopback: 29 ms for 18,400 characters,
50 ms for 73,600, 137 ms for 294,400 — and counts are cached per message, so a continuing
conversation pays only for its newest turn. `RuntimeTokenCounter` pre-tokenizes the turns and is
injected into `ConversationWindowPlanner.Fit`. An unreachable tokenizer still falls back to the
heuristic, so this sharpens the estimate without becoming a dependency.

**The live run then exposed a third defect the unit tests could not see.** A 19,676-character
hex-dense document was correctly priced at 14,616 tokens (the old estimate: 6,562, i.e. "fits") and
correctly compacted — and the model *still* answered nothing. The plan's own words gave it away:
"using about 5.840 of 7.168 usable tokens". That 5,840 was a heuristic figure, because the digest the
planner had just built was a **new string the runtime was never asked about**, so it fell back to
3.0 on hex text: really some 13,000 tokens into an 8,192 window. Everything the planner synthesises —
slices, pages, digests — has this shape. Unseen text is now priced at the ratio the conversation
itself showed, and only a completely unmeasured conversation falls back to the constant.

**Verified live through the UI, both directions:**

- The same 19,676-character hex document that had returned *"The model returned an empty response"*
  now answers **MERIDIAN**, its plan correctly reporting 14,616 tokens where the old code said 6,562.
- A 29,929-character prose document — priced by the old heuristic at 9,980 tokens, over the 7,168
  budget, so previously paged into a lossy digest — is now sent **whole** in a fresh chat, with no
  compaction notice, and the model reads a reference planted in its **middle**: **HALCYON-77**.

That second case is the user-facing point: PaRuX stopped throwing away context it never needed to
throw away.

### 2026-09-09 (continued) Web search cited an advertisement's click tracker as its first source

Found while soak-testing, in a plain question about the history of Rome. The Sources footer opened
with several hundred characters of this:

```
https://duckduckgo.com/y.js?ad_domain=amazon.it&ad_provider=bingv7aa&ad_type=txad&click_metadata=...&u3=https%3A%2F%2Fwww.bing.com%2Faclick%3F...
```

**Root cause.** `ExtractRedirectTarget` unwrapped only the organic `/l/?uddg=` redirect.
Advertisements are not wrapped that way - they are `duckduckgo.com/y.js` click trackers - so they
carried no `uddg=`, fell through to the "starts with http" branch, and were returned verbatim. The
regression test fails on the old parser with the ad at **position 0**: the top source of the answer.

Two things were wrong at once: an advertisement was being presented to the user as a citation, and
a tracking URL was being pushed into the model's context, spending tokens on click metadata.

**Fix** (`WebSearchService`): a resolved result is rejected when its host is still the search engine.
An unwrapped result is somewhere else by definition, so anything left pointing at DuckDuckGo is an
ad, a tracker, or a redirect that failed to unwrap. The ad's inner `u3` target is deliberately not
followed - it is another click tracker, and an advertisement is not a search result whichever URL it
is expressed as.

**Verified live:** the same question now cites five real destinations - `studenti.it`,
`it.wikipedia.org/wiki/Storia_di_Roma`, `fattiperlastoria.it`, `doc.studenti.it` - with no tracker
and no ad.

### 2026-09-09 (continued) Android pairing: the app failed correctly, and PaRuX's firewall advice was wrong

**Soak first, since it passed:** 9 STOP cycles driven through the real UI (each ending in a clean
`Stopped.` and an idle composer) interleaved with 4 model unload/reload cycles, every one of which
came back healthy and answered `ALIVE`. Memory and handles were flat across the run - PaRuX
330→335 MB with handles *falling* 2715→2696; Core 489→509 MB - so no leak signature.
Two apparent failures during this work were mine, not the product's: a STOP that "did not cancel"
was a stale click coordinate after the sidebar collapsed, and a chat probe that "returned nothing"
was a shell pipeline breaking an SSE frame. Both were verified through UIA and a correct parser
before any claim; STOP genuinely cancels (SendButton returns, llama-server slot goes idle).

**Android pairing reached the last step and stopped at the firewall.** Emulator booted, debug APK
installed, pairing payload accepted, `Pair (debug)` pressed - and the app answered *"Couldn't pair -
Couldn't reach PaRuX PC. Check that both devices are on the same network."* with Try again and
manual entry offered. That is the correct behaviour for an unreachable host, so the mobile client is
**not** at fault, and pairing itself is **not verified** either way: the inbound connection never
arrived. Windows Firewall consent is a system security setting, so it was declined rather than
granted, and that decline is what blocked the connection. Recorded as unproven, not as a pass.

**The real defect it exposed.** `RemoteAccessService` gates LAN access on the address being RFC1918
and nothing else, and the UI then always printed *"Windows Firewall must allow PaRuX Core on Private
networks only."* But Windows Firewall applies rules by the **category** the user assigned to the
network, not by address range. This PC is on [redacted private LAN address] - unmistakably private - attached to a
home router that Windows classifies as **Public**. So PaRuX enabled LAN access, issued a pairing
code, and printed advice that could not be followed on the very machine printing it, while a phone
on the same Wi-Fi failed with a message the PC never explained.

**Fix**: `NetworkCategoryProbe` reads the category Windows actually assigned (late-bound through
IDispatch - a first attempt with a hand-written vtable silently called `GetName` instead of
`GetCategory` and reported Unknown everywhere), `RemoteAccessStatus` carries it, and the LAN status
line now says what this machine really needs. Verified live, and against `Get-NetConnectionProfile`:

> Windows classifies this network as Public. Devices cannot reach PaRuX until you either set this
> network to Private in Windows settings, or allow PaRuX Core on the Public profile - PaRuX never
> changes firewall rules or network categories for you.

Unknown is reported as unknown; it is never guessed to be Private, because Private is the one answer
that would make PaRuX print firewall advice it has no evidence for.

### 2026-09-09 (continued) Chat answered nothing whenever it retrieved context - the window was a quarter of what PaRuX thought it launched

The most serious defect of this pass, and one a user would hit constantly: any chat turn that pulled
retrieved context came back as "The model returned an empty response". Three consecutive attempts
failed, including in a brand-new conversation, while a plain arithmetic question answered instantly.
The distinguishing factor was visible in the transcript — the failing turns all listed Sources.

**Root cause, established from the Core's own stream and llama-server's properties.** Replaying the
prompt against `/api/v1/chat/completions` returned routing metadata and **zero content tokens**,
with the Core stating the reason itself:

```
"parux_event":"context_compacted","usedTokens":1025,"availableTokens":1024,
"detail":"The system prompt alone fills this model's context, so no conversation history could be included."
```

`/props` on the running llama-server then showed `n_ctx` 2048 per slot with `total_slots` 4 against a
launched `--ctx-size` of 8192 — exactly 2048 x 4. **`--ctx-size` is the total, which llama.cpp
divides across its slots**, so leaving slots on auto quartered the usable window. With
ChatProxyService reserving 1024 tokens for the reply and PaRuX's own system prompt measuring 1025,
nothing was left for the user's turn, and the model was handed a prompt it could only answer with
silence. A 4B model advertising 262,144 context was serving 2048. The optimizer's own comment
asserted the opposite — that letting llama.cpp choose slots kept "the full per-slot context" — which
does not hold for the pinned baseline build, and that wrong assumption is what hid this.

**Fix** (`RuntimeLaunchOptimizer`): the context floor is now expressed per slot and multiplied by the
slot count (`MinimumUsablePerSlotContext` x slots, with the auto case using the 4 slots llama.cpp was
measured to choose), so a launch can no longer produce a per-turn window too small for PaRuX's own
prompt. Verified live: the runtime now launches `--ctx-size 32768` and reports **8192 per slot**, and
the exact prompt that had returned zero tokens three times now answers correctly with no
`context_compacted` event at all — confirmed both through the API and in the UI, where the same
question now returns the updated `greet.py` with a working `farewell` function.

**Regression tests** (`RuntimeContextFloorTests`) assert the per-slot window — the quantity that
actually has to hold the system prompt — across unset, tiny and auto-slot profiles, and were
confirmed to fail against the old behaviour with the precise message ("Each slot would get 2048
tokens … the turn would come back empty").

**The silence around it was closed too, in two places.**

*The desktop was throwing the Core's explanation away.* `ChatClient` did not parse
`context_compacted`, so `ChatSession` could only print "_The model returned an empty response._"
while the Core had already said exactly why. It is now parsed and appended to that notice. The
callback was added as an optional parameter with a default, so no existing caller changed — the
rippling refactor this seemed to require was avoidable.

*A single oversized message reported nothing at all.* Sending one 74,000-character message produced
zero content tokens and **no** `context_compacted` event: `ConversationWindowPlanner` keeps the
newest turn whatever its size, and with no older turns dropped the plan was not marked compacted.
The code said so deliberately — "the runtime remains responsible for token-level handling of that
one turn" — but the measured runtime behaviour is to return nothing, silently. What is sent is
unchanged; the plan is now marked compacted and carries an explanation naming the real numbers.

**Verified live**, the two composing exactly as intended: the same oversized prompt now answers
"The model returned an empty response. This single message is about 24.760 tokens, larger than the
6.003 tokens left for it after the system prompt, so the model may not be able to read all of it and
can return nothing at all. Send it in smaller parts, or use a model with a longer context." A silent
failure became an actionable one. Covered by a regression test in `ConversationWindowPlannerTests`.

### 2026-09-09 (continued) Settings sweep completed, Agents orchestrator verified, corrupt local history repaired

**Every Settings page is now covered.** General, Models, Chat Behavior, Your PC apps, Media
Generation, Automation, Agents, PaRuX Platform, Privacy & Network, Data and Advanced were each
opened and exercised rather than merely inspected.

- *Your PC apps* really detects what is installed — Chrome bookmarks, OneDrive documents and
  Office documents, each with its true path — and "Scan this PC" re-runs cleanly. Nothing was
  connected: doing so would grant PaRuX read access to the maintainer's personal documents and
  bookmarks, which was not asked for.
- *Agents* was verified end to end. A real task ("List three trade-offs between GGUF Q4_K_M and
  IQ4_XS…") was dispatched to the Research role, ran on the local model for ~44 s and came back
  `status: completed`, `stage: Verified`, `verificationStatus: pass`, `error: null`, with real
  on-topic prose in `resultJson`. Deliberately not run as a Coding task: the coding-agent workspace
  points at this very repository, and an agent writing there could have disturbed uncommitted work.
  A pre-existing cancelled task on the page reports its own failure honestly ("the goal … was never
  accomplished"), which is the required behaviour.
- *PaRuX Platform* License & Trust fetched genuine Hugging Face metadata:
  `Qwen/Qwen2.5-0.5B-Instruct · model repository · Declared license: apache-2.0`, framed correctly
  as "absence is not permission, and this is not legal advice". The observability timeline loaded 13
  real events.
- *Media Generation*: the resolution preset genuinely drives width and height (768 → both fields),
  and folder selection uses pickers rather than typed paths.
- *Advanced* diagnostics show coherent live state: llama-server healthy, RAM 15,139 / 26,431 MB, and
  the corrected GPU line. The RAM figure cross-checks against Training preflight's "12,9 GB
  available", which is half of it — the surfaces agree.

**Local history database repaired, cause attributed to this pass.** `parux.db` was genuinely corrupt
(`Rowid out of order`, wrong index counts on `conversations`), which the app had been reporting as
`HistoryStore.LoadAll/Save failed: SQLite Error 11: 'database disk image is malformed'`. Almost
certainly caused by this pass force-killing the app dozens of times mid-write, not by shipped code —
and PaRuX degraded correctly throughout, logging and continuing rather than failing the session.
Repaired without deleting anything: backup taken, `REINDEX` tried first (it fixed the index counts
but not the tree), then the readable rows were copied into a fresh database, which verified `ok`.
That recovered **9 rows where `count(*)` had reported 8** — the count was itself going through the
corrupt index. The damaged file is kept as `parux.db.corrupt-20260909`. After the swap: no further
errors, history visible again, and a real round-trip message ("Rispondi solo con la parola OK" →
"OK") confirmed the write path, with the table growing to 12 conversations and integrity still `ok`.

### 2026-09-09 (continued) Settings claimed "no GPU detected" on a machine whose GPU the app already knew

Opening Settings → Models showed every detected model as `Fit unknown (no GPU detected)`, while the
Core's own inventory reported `AMD Radeon 780M Graphics` with 6,342,995,968 bytes of dedicated video
memory and `gpuInventoryAvailable: true, gpuInventorySource: "dxgi"` (confirmed by querying
`/api/v1/runtime/status` directly). One app was telling the user two different things about the same
hardware.

**Root cause, in two layers.** `ResourceMonitorService` detects GPUs *only* through `nvidia-smi`, so
`ResourceSnapshot.GpuAvailable` is false on any non-NVIDIA machine and `ModelFitClassifier` can only
answer Unknown. The message then overstated that into "no GPU detected", which is a different and
false claim. The second layer only appeared once the fallback was written: the fix initially did
nothing, because `PopulateGpuAsync` returns early *from inside its try* whenever nvidia-smi answers
unhelpfully — and on this workstation `nvidia-smi.exe` genuinely exists (System32, shipped with the
NVIDIA App) and exits with code 4, "you do not have sufficient permissions". Hanging the fallback off
the end of that method left it unreachable on precisely the machine that needed it.

**Fix.** `ResourceMonitorService` now falls back to the Core's vendor-agnostic DXGI inventory —
the same source Model Hub already used — and the call sits in `GetSnapshotAsync`, after
`PopulateGpuAsync`, so no early return can skip it. The inventory is cached for five minutes because
installed adapters do not change between polls and the diagnostics panel refreshes every few seconds.
`ResourceSnapshot` gained `GpuUsageMeasured` so the two sources stay distinguishable: nvidia-smi
reports live usage, DXGI reports only the adapter and its memory. The diagnostics line honours that
— it prints usage only when it was measured, otherwise "live usage not reported by this adapter",
rather than a confident `0 MB used · 0% util` nobody observed. `ModelFitClassifier`'s Unknown text
now says "no VRAM reading available" instead of asserting there is no GPU.

**Verified live.** Settings → Models now reads `Works with CPU/RAM offload` for the 13-14 GB 27B
models and `Good fit` for the 2.5-2.6 GB 4B ones — sensible verdicts against 5.9 GB — instead of
`Fit unknown` for everything, and diagnostics shows
`AMD Radeon 780M Graphics · 6.049 MB VRAM · live usage not reported by this adapter`.

**Noted, and attributed to this pass rather than the product:** the app log shows
`HistoryStore.LoadAll failed: SQLite Error 11: 'database disk image is malformed'` for the local
`parux.db`. This pass force-killed the app (`taskkill /F`) dozens of times, repeatedly mid-write,
which is the likely cause; it is recorded here so it is not mistaken for a shipped defect, and
because the app does degrade gracefully — it logs and continues rather than failing the session.

### 2026-09-09 (continued) Web search was silently returning nothing; fixed at the request, not papered over

The maintainer explicitly authorised enabling Remote access and Web search for testing, and asked
that PaRuX's internet search be made genuinely good rather than merely honest about failing.

**Symptom.** With web search enabled, "search the web for the official Python release notes for
version 3.13" routed to the Web Search skill (the footer confirmed it) and the model answered "I
cannot directly access external websites or search the web in real-time."

**Root cause, measured directly.** `WebSearchService.SearchAsync` issued a GET to
`html.duckduckgo.com/html/?q=` carrying a `PaRuXLocalAI/1.0` user agent. Fetching that exact URL
from this machine returns DuckDuckGo's anti-bot challenge page — 14 KB containing "anomaly" 67
times and "challenge" 13 times — with **HTTP 200**, so `EnsureSuccessStatusCode()` passed, the
result regex matched nothing, and the method returned an empty list without raising anything. The
same query issued as a **POST with ordinary browser headers returns the full result set**: 10
`result__a` and 10 `result__snippet` matches, zero "anomaly", parsed correctly by the existing
regex. So the scraper was not obsolete — the request shape was what triggered the block.

**Fixes.**
1. `WebSearchService`: query by POST with a browser user agent and `Accept-Language`, and fall back
   to the `lite.duckduckgo.com` endpoint (its own markup, its own pattern) when the primary yields
   nothing, so one markup change can no longer take the feature out silently. Both endpoints stay
   keyless, which is what the Settings copy promises.
2. `ContextEngineService.BuildWebCandidatesAsync`: an empty result set now injects the same kind of
   explicit notice the disabled and failed branches already used — "a web search ran but returned no
   usable results … do not invent results". Previously zero results were indistinguishable from "no
   web lookup was attempted", which is exactly how the misleading answer above was produced.

**Regression tests** (`WebSearchParsingTests`): the parsers are locked against fixtures shaped like
the real responses from both endpoints, plus a challenge-page fixture asserting it yields nothing
rather than bogus rows. `WebSearchService` was unsealed and `SearchAsync` made virtual purely as a
test seam; the SSRF protections live in the fetch path and are untouched.

**Verified live** after republishing: the same question now answers "Based on my search results…"
with real, checkable facts (initial release 7 October 2024; 3.13.3 carrying ~320 bugfixes) and the
footer lists genuine sources — `python.org/downloads/release/python-3130/`,
`docs.python.org/3/whatsnew/3.13.html` and others.

**A privacy control could assert "Off" without having read it — fixed.** Before the toggle, Settings
showed Web search **Off** while the Core's `internet.json` had been `{"Enabled":true}` since
4 September, so the user could believe web access was disabled while it was live. The status *is*
re-read every time the page is shown, so the initial hypothesis of a missing refresh was wrong; the
real fault is what happens when that read fails. `RefreshInternetStatusAsync` caught the exception,
logged it and returned, leaving the switch at its XAML default of Off — a confident, wrong, and
reassuring-looking value. The app log contains that failure twice, and the trigger is mundane:
opening Settings while the Core is still starting. It now retries three times and, if the Core still
does not answer, says `Unavailable — PaRuX Core did not answer` and disables the switch, because a
toggle could not be persisted then anyway. Verified live: Web search now reads **On**, matching the
Core. The unreachable branch is defensive and was **not** exercised live — the Core answered on the
first attempt — so it is claimed as reviewed code, not as a live-verified path.

**Backup export verified.** "Export backup…" produced a real, valid archive
(`parux-backup-2026-09-09.zip`, 20 entries: `conversations.db`, `memory.db`, `settings.json`,
`eval.db`, `artifacts.db`, `autolab.db` and the rest), integrity-checked OK.

**Withdrawn, not a product defect — tooling artifact.** This pass briefly recorded that
`%LOCALAPPDATA%\PaRuX\parux.log` stopped receiving entries after 11:42 despite many restarts, and
that `settings.json` was frozen at the same moment (the app returning to Simple mode on every
launch looked like the matching symptom). Checks that ruled out the obvious causes: every directory
`AppPaths.EnsureCreated` touches exists and is creatable, the log file is writable, it is far below
the 5 MB rotation threshold, `PaRuXDataRoot` resolves deterministically to one location, and a probe
file proved the packaged-app `LocalCache\Local` view and the real folder are the same directory
rather than two copies. The discriminating test was launching the published `PaRuX.exe` straight
from a shell instead of through the acceptance harness: it logged immediately, the file growing
135,741 → 136,620 bytes with fresh entries. Logging and settings persistence are therefore fine —
the silence belonged to how this pass launched the app, not to PaRuX. Recorded here because the
false lead was already written down, and a wrong entry in the canonical plan is worse than none.

### 2026-09-09 (continued) Creating a Project was impossible from the desktop app and the CLI - the route was never mapped

Found on the first real use of Projects & Artifacts: typing a goal and pressing "Create Project"
produced `Project creation failed: Project Manager request failed (405)` and no Project.

**Root cause.** `ProjectManagerClient.CreateAsync` (and `PaRuX.Cli/Program.cs:766`) post to
`/api/v1/projects/desktop` — the loopback-desktop path `CreateDesktopProjectRequest` documents,
which carries the chosen coding permission profile while the plain `/api/v1/projects` route
deliberately leaves mobile/LAN callers on the offline default. That route was never mapped on the
server. The path still matched the `GET /api/v1/projects/{projectId}` template, so ASP.NET answered
405 Method Not Allowed rather than 404, which is why it read as a transport oddity rather than a
missing feature. Everything else already existed: the contract, and
`ProjectManagerService.CreateProjectAsync(goal, ct, codingPermissionProfile)` which normalizes and
persists the profile into the project entity. Only the endpoint was absent, so Project creation was
broken in both shipped clients.

**Fix** (`src/PaRuX.Server/Program.cs`): map `POST /api/v1/projects/desktop` onto the existing
service overload, mirroring the neighbouring route's error handling. The LAN boundary was checked
before adding it and is unchanged: `PairedDeviceEndpointPolicy`'s POST allowlist contains
`/api/v1/projects` **exactly**, plus `/{id}/tasks`, neither of which matches this path, so the
route stays loopback-only exactly as the contract states.

**Regression tests** (`tests/PaRuX.Server.Tests/DesktopProjectEndpointTests.cs`): drive the route
over the existing `WebApplicationFactory` harness with a blank goal, which `CreateProjectAsync`
rejects before it plans anything — so the tests prove the route is mapped and reaches the right
handler without needing a reachable local model. Both were confirmed to genuinely catch the defect:
with the fix temporarily stashed the new test fails with `Expected: Not MethodNotAllowed / Actual:
MethodNotAllowed`.

**Verified live** on the repackaged app: the same action now yields "Active project: Acceptance
smoke project 2026-09-09 · 7dcd7a5c145c455a89e92800a10aafcf", the Project lists as `1 task(s)` with
an Active badge — the goal really was planned into a task — and "Choose Project repository…"
correctly unlocks now that a Project exists.

### 2026-09-09 (continued) Training and AutoLab accepted honestly; role fields turned into pickers; gate flake fixed

**Training — accepted as a hardware limit, no success claimed.** The managed training runtime is
CUDA/Unsloth (`InstallEngineAsync` pulls torch from the cu128 index), so it cannot fine-tune on this
machine's Radeon 780M no matter what is installed. On the maintainer's explicit decision it was not
installed, and the honest-failure path was accepted instead. Verified live: "Check this run" works
with no runtime present and produced a real plan from the exported SFT JSONL — "Ready to start.
Inspected all 2 records; compatible with supervised fine-tuning. Needs roughly 3 GB against 12,9 GB
available", the 12.9 GiB being half of usable system RAM, which is correct here because with no
capable accelerator the fine-tune would run on CPU against system RAM. It volunteered the right
caveat too: "Only 2 examples were observed. The run can fit them, but it may memorise them rather
than generalise; treat quality evidence as weak." Method-specific fields (DPO beta, GRPO reward,
media fields) are correctly disabled for SFT. "Start local training" was refused with a 503 and an
explicit reason, and left no phantom job behind ("No training jobs yet"). **No training success is
claimed on this hardware.**

**AutoLab — verified working.** A real comparison sweep ran the two-case dataset across the
standard, reasoning and coding roles; all three completed at pass 100% (2/2), coding was marked
RECOMMENDED, and nothing was promoted automatically. The "Promote…" button is offered only for the
recommended experiment.

**UX, standing preference ("the UX through combobox etc. should always be present wherever
possible"):** two free-text role fields on this page were replaced with pickers over the closed,
already-server-validated set (`ModelRoleCatalog.TextRoles`, the same source `ModelRoutingPanel`
lists from — no invented values). The promote dialog's target role was a free-text box, so a typo
was only rejected after a server round-trip; the Eval role box ("Optional role, e.g. coding") now
lists the real roles plus an explicit "Run against the loaded model" default preserving the
documented behaviour of running against whatever is loaded. Checked and deliberately not "fixed":
the promote dialog defaulting to `standard` while `coding` was the recommended experiment looks
wrong but is correct — that field is the role being promoted *into*, not the experiment's own role.

**Release gate made deterministic.** Two of four consecutive gate runs failed for one shared root
cause in mirrored forms — `PluginBundleServiceTests` hitting `ObjectDisposedException:
'SQLitePCL.sqlite3'` inside `sqlite3_prepare_v2`, and `BackupServiceTests` hitting `IOException`
because `artifacts.db` was still held — both classes passing in isolation. SQLite pooling is
process-wide while xUnit runs collections in parallel, so one suite's `ClearAllPools()` disposes
another's live handle, and a pooled handle can still hold a file a backup needs exclusively. Test
collections are now serialized (`TestDataRootIsolation.cs`), measured at 6m18s serial against
4m43s-6m05s parallel. Still open and tracked separately: converting those `ClearAllPools()` calls to
`ClearPool(connection)`, which matters in production too — backing up one store currently clears
every other store's pool in the running app.

Full `tools\release-gate.ps1 -Restore` after both changes: green, 2665/2665 .NET tests in 6m10s,
Android BUILD SUCCESSFUL.

### 2026-09-09 (continued) "Run selected dataset" was write-only: real Eval verdicts existed but the UI never showed them

Ran the whole Eval cycle for real through the UI against a small synthetic CSV (two cases:
`2+2 → 4`, `capital of Italy → Rome`), deliberately instead of the 52,002-row alpaca split, so the
pass exercises the full path without tens of thousands of inferences.

**Verified working:** Inspect reported `csv · 2 rows · 0 duplicates · 0 empty fields`; the column
pickers went from disabled to populated (confirming the earlier empty-ComboBox fix); explicit
prompt/expected/system mapping imported cleanly ("Imported 2 Eval case(s); skipped 0 incomplete
row(s)"); and Prepare SFT JSONL exported a real, correctly shaped chat JSONL carrying the system
prompt, user prompt and expected answer for both cases.

**Defect:** pressing "Run selected dataset" set the status to "Eval run <id> started." and nothing
ever replaced it. `StartEvalRunAsync` fired the run and returned; `RenderSelectedEvalSummaryAsync`
only ever rendered the mapped-case count and never read runs at all. The run itself was completely
fine — `eval.db` showed `status=completed`, 2 passed / 0 failed / 0 inconclusive / 0 errored with
genuine model output ("4", "The capital of Italy is Rome.") — and `EvalLabClient.GetRunAsync` /
`GetResultsAsync` plus their server endpoints already existed. The results were simply unreachable
from the page that produced them, so the feature was effectively write-only.

**Fix** (`src/PaRuX.App/Views/LifecycleLabView.cs`, View-only): after starting a run the page now
polls `GetRunAsync` until the run leaves "running" — bounded, so an unreachable or wedged run
reports honestly instead of polling for ever — then renders the outcome and the per-case verdicts
with the model's actual output. The last run for the selected dataset is re-rendered on refresh so
it survives a Refresh instead of collapsing back to the bare case count. A run where nothing could
be scored is reported as "nothing was scored" rather than as a 0% result, so an all-errored run
(no reachable model) can never read like a genuine failure.

**Verified live** on the repackaged app: a second real run reported "Eval run finished: 2 passed,
0 failed, 0 inconclusive (100% pass)" with rows `pass · 4 · PASS` and `pass · Rome · PASS`, and the
outcome survived Refresh datasets. The model's wording differed between the two runs, which is
itself evidence the inference is real rather than replayed.

Full `tools\release-gate.ps1 -Restore`: green, 2665/2665 .NET tests, Android BUILD SUCCESSFUL.

**Known intermittent gate flake, root-caused, NOT yet fixed.** One gate run in this pass failed
`PluginBundleServiceTests.Signed_bundle_requires_publisher_trust_then_is_remembered` with
`ObjectDisposedException: 'SQLitePCL.sqlite3'` inside `sqlite3_prepare_v2`; the class passes 5/5 in
isolation and the immediately following full gate was green. Cause: `SqliteConnection.ClearAllPools()`
is process-wide and disposes the pooled handles of *every* database, while xUnit runs collections in
parallel — 13 test classes and roughly seven production stores call it, so one of them firing while
another test is mid-query disposes that test's handle underneath it. This is not only a test
concern: `BackupService.cs:677` explains it only wants to release the handle on *that* backup file,
yet the call clears every pool in the process, so backing up one store perturbs unrelated stores in
the running app too. The targeted remedy is `SqliteConnection.ClearPool(connection)` at those sites,
which is exactly what the existing comments describe. Deliberately not bundled into an unrelated,
already-verified View fix: it touches ~20 files across production and tests, and this repository
already documents one broad isolation fix that had to be reverted. Tracked here as open.

### 2026-09-09 (continued) Lifecycle Lab dataset file selector listed unusable files and preselected .gitattributes

Found while running the Dataset Hub end-to-end against a real Hugging Face repository
(`tatsu-lab/alpaca`) on the physical PC, as part of the standing "use every feature for real
through the UI" pass. Full evidence for this pass is in
`docs/PHYSICAL_PC_LIFECYCLE_2026-09-09.md`.

**Defect:** `LifecycleLabView.ChooseDatasetFileAsync` listed *every* sibling the repository
reports, sorted by `SizeBytes`, then selected index zero. `DatasetDownloadService.GetDetailsAsync`
reports zero for sibling sizes it does not know, so on the real repository the picker offered
`README.md` and `.gitattributes` alongside the data files, preselected `.gitattributes` (sorted
first at "0 B"), and described the genuine 24,246,638-byte Parquet as "0 B" — i.e. the default
action was to download a file the rest of the page cannot ingest.

**Fix** (`src/PaRuX.App/Views/LifecycleLabView.cs`, View-only): the picker now filters to the
formats this page actually supports (`.csv`, `.json`, `.jsonl`, `.parquet`), orders files with a
known size first and falls back to a stable filename tie-breaker instead of letting unknown sizes
sort to the top, and labels an unavailable size `size unknown` rather than the misleading `0 B`.
The empty-list error message was updated to match the filter. The ComboBox dropped its 600px
minimum for `MinWidth = 320` plus `HorizontalAlignment.Stretch`, so it fits inside the dialog. No
server-side download validation, no size reporting and no inference behavior was changed — the
underlying `SizeBytes = 0` from Hugging Face is left as-is and now simply presented honestly.

**Verified live** on the repackaged app: repeating the search and reopening the selector listed
only the real Parquet, preselected, labelled `size unknown`. Downstream flow re-verified in the
same pass: download completed (24,246,638 bytes confirmed on disk), Curate populated the source
path, Inspect read 52,002 rows / 0 duplicates / 31,351 empty fields, the column pickers opened
with instruction/input/output/text, and the deterministic 80/10/10 split produced
41,602 / 5,200 / 5,200 rows confirmed by independent line counts of the JSONL files.

This is a WinUI View fix with no server-side surface, so per the acceptance mandate it is
validated by live evidence; no source-text pseudo-test and no separate UI harness was added.

Full `tools\release-gate.ps1 -Restore` after the fix: green, 2665/2665 .NET tests, Android
BUILD SUCCESSFUL, `[PaRuX gate] PASS`.

**Not yet accepted in this pass** (still open, not claimed): Eval create/import/run and SFT
export, Training preflight/method selection (the UI reports the training runtime as not
installed — no training success is claimed), AutoLab and adapters. Download status was refreshed
explicitly; automatic background refresh and download-list recovery after a server restart were
not accepted. Several Lifecycle Lab inputs are still free text (Eval role, AutoLab roles,
promotion target, file paths) and remain candidates for pickers under the standing UX preference.

### 2026-09-09 (continued) real user report: Lifecycle Lab column pickers looked frozen when clicked

The user, watching the live UI test, reported: "tipo ora cliccando su prompt column sembra
impallato". Confirmed and root-caused as a real defect.

**Reproduction (deterministic, 2/2):** on Lifecycle Lab, before any dataset has been inspected,
a single click on the "Prompt column" ComboBox made it render permanently blank — no placeholder
text, no chevron — while its identically-constructed siblings ("Expected behavior column",
"Optional system-prompt column") kept rendering correctly. It only recovered after an unrelated
page refresh or re-navigation.

**Root cause, confirmed via UI Automation:** `ExpandCollapsePattern.Current.ExpandCollapseState`
read `Expanded` for that ComboBox and `Collapsed` for both siblings. The three ComboBoxes are all
empty at that point — `PopulateColumnMappings` only fills them after a successful `Inspect` — and
clicking an empty ComboBox starts the open transition, but with zero items WinUI never actually
shows a popup, so the close transition that restores the placeholder visual state never fires.
The control is left logically "open" forever with its placeholder suppressed.

**Fix** (`src/PaRuX.App/Views/LifecycleLabView.cs`): all four ComboBoxes on the page
(`_promptColumn`, `_expectedColumn`, `_systemColumn`, `_evalDataset`) now start `IsEnabled = false`
and are enabled only once they actually hold items (`PopulateColumnMappings` for the three column
pickers, `RefreshEvalAsync` for the Eval dataset picker). A disabled ComboBox cannot start the
popup transition at all, so the stuck-blank state is unreachable; it is also the more honest UX,
since there is genuinely nothing to select until a dataset has been inspected. Verified live on
the repackaged app: the four pickers render correctly dimmed and clicking "Prompt column" is now
a no-op instead of blanking it.

Audited the rest of the desktop client for the same latent pattern: every other `ComboBox` with a
`PlaceholderText` (`SettingsView.xaml.cs` role pickers, `ToolsAutomationView`'s trigger-type
picker) is populated synchronously at construction, so none of them can ever be clicked while
empty. No further changes needed.

App-side WinUI view code has no test harness in this repository (only `tests/PaRuX.Server.Tests`
exists), so — as with the Model Hub and Runtime Manager view fixes earlier in this same pass —
this is verified by live UI evidence rather than a unit test.

Full `tools\release-gate.ps1 -Restore` after the fix: green, 2665/2665 .NET tests, Android build
successful.

### 2026-09-09 (continued) real user report: Tools & Automations "Save permissions" was broken for every built-in skill; MCP server add now offers presets

Continuing the systematic real-UI sweep (user, translated from Italian: "don't just test the model hub functions, you must test all of the app's functions one at a time, through the UI.") into Tools &
Automations, which had not been exercised this continuation beyond a prior partial Automations
pass.

- **Confirmed defect: the "Permissions..." editor could never save for ANY built-in skill.**
  Opened Grill Me's permission editor via the real UI, toggled Read files on, clicked "Save
  permissions", and got a real failure: `Permission update rejected: Skills request failed (409):
  {"error":"A built-in skill already uses id 'grill-me'."}`. Root-caused in
  `SkillRegistryService.cs`: the editor's save path called `CreateOrUpdateUserSkill`, which is
  meant for authoring whole user/imported skills and deliberately refuses any id that collides
  with a built-in one - so editing a built-in skill's OWN permissions always collided with itself.
  Every one of the ~15 built-in skills listed on this page was affected; only a hand-crafted user
  skill with a non-colliding id could ever save through this path.
  Fixed by adding a dedicated permission-override mechanism, mirroring the existing pattern already
  used for the Enabled toggle (`skill_overrides` table): a new `skill_permission_overrides` table,
  `SkillRegistryService.SetPermissions(id, permissions)` (validated against the skill's own
  `AllowedTools`/`InternetPolicy` via the same `SkillPermissionPolicy.Validate` used for full
  manifests), a new `POST /api/v1/skills/{id}/permissions` endpoint, `SkillsClient.SetPermissionsAsync`,
  and `ToolsAutomationView.EditSkillPermissionsAsync` switched to call it instead of
  `CreateOrUpdateAsync`. Added 3 regression tests to `SkillPermissionPolicyTests.cs` (builtin save
  succeeds without colliding with itself; a grant that violates the skill's own AllowedTools policy
  is still rejected; an unknown id throws). Verified live end-to-end after fixing a self-inflicted
  publish-path mistake (see below): toggled Grill Me's Read files on, saved, got "Skill permissions
  saved after Core validation.", list row updated to `permissions: read-files`, then reverted back
  to `permissions: none` the same way - both directions confirmed working against the real server.
- **Publish-path lesson (not a product defect):** while chasing the fix above, republishing
  `PaRuX.Server` to `artifacts/publish/win-x64` (the same layout used for the App) produced a false
  404 on the new endpoint, because the packaged App actually launches its bundled server from the
  `PaRuX.Server\` *subfolder* under that path, not the root - the root copy was stale and unused.
  Cleaned up the stray root-level Server files (build output, already gitignored) and re-published
  to the correct `artifacts/publish/win-x64/PaRuX.Server/` location.
- **User UX feedback, applied immediately:** while testing, the user said manual entry should be
  minimized wherever a picker/preset can do the work instead. The MCP Servers form (both on this
  page and the equivalent one in Settings -> Automation) required typing the exact npm package name
  and CLI flags for a server by hand with zero assistance. Added a shared
  `McpServerPresetCatalog` (filesystem, git, sqlite, fetch, memory, sequential-thinking - the
  official `@modelcontextprotocol/server-*` reference servers) and a "Preset" picker on both forms
  that fills in Name/Command/Arguments on selection; manual entry remains fully available for any
  other server. Verified live: added a real MCP server entry via the raw fields (`test-echo-server`
  / `cmd` / `/c exit 0`), watched it correctly report `not connected · MCP server process exited
  unexpectedly` (honest, since that command isn't an MCP server), then removed it and confirmed the
  list returned to "No outbound MCP servers configured."

Full `tools\release-gate.ps1 -Restore` re-run after these fixes (with the PaRuX app/server fully
stopped, since a still-running live-tested instance was found to spuriously fail 10 unrelated
runtime-install/benchmark tests via real port/model-lock contention - not a product defect, a
test-environment conflict caused by this session's own live testing): green, 2665/2665 .NET tests,
Android build successful.

### 2026-09-09 (continued) systematic real-UI sweep: Observability, Updates, Workflows, Model Intelligence - all confirmed healthy; one genuine INCONCLUSIVE benchmark

Continuing the user's explicit instruction to test every app feature, one at a time, through the
real UI - not just check controls are present.

- **Observability & Benchmarks:** real live data throughout (execution profile, active
  agent/training/download counts, a real completed model benchmark from this session's own
  "Optimize for this PC" run, a real model-benchmark leaderboard entry, and a 10-event activity
  timeline with real agent-task/artifact records). Refresh works. No issues found.
- **Update Manager:** "Check again" and "Check / update…" both work and fail *honestly* -
  "No published GitHub Release is available yet" / "Runtime update failed: No compatible verified
  build is currently advertised" - correct for a dev environment with no release feed, no fake
  success. No issues found.
- **Workflows:** created a real workflow (name + JSON steps), got a correct v1/current/root entry
  with working Version history, then deleted it via the correctly-worded immutable-version
  confirmation dialog. No issues found.
- **Model Intelligence & Routing:** "Profile selected model" ran the real fixed correctness battery
  live and replaced the scale-only prior (37.8%) with a genuine measured capability (77.8%),
  updating the model to "loaded". No issues found here either.
- **Backend Race vs Ollama (competitive benchmark):** found Ollama already running with a model
  named exactly for this purpose (`qwen35-4b-fair-compare:latest`, same Qwen3.5-4B-Q4_K_M weights).
  The endpoint/model-id fields initially only *looked* filled - `ValuePattern.Value` revealed the
  endpoint field was still genuinely empty (its default was placeholder text, not a real value,
  confirmed by comparing `AutomationElement.Name` - which showed the placeholder - against
  `ValuePattern.Current.Value`, which was `""`); once both fields were set to real values the race
  ran for real. Result: PaRuX llama.cpp measured 23.8 decode tok/s (TTFT 1621 ms, 43 prompt tok/s);
  the Ollama side failed with "External backend stream produced no content tok[ens]" and
  **Winner: not declared**. Verified directly against Ollama's own API this is the correct call, not
  a PaRuX bug: this specific model streams virtually its entire output as `delta.reasoning` chunks
  (confirmed live: ~200+ reasoning-only chunks) with exactly one real `delta.content` token
  ("Hello") at the very end. Per the standing benchmark rules - no SSE-chunk-as-token, hidden
  reasoning does not count as measurable content - a race built on that stream would be
  meaningless, so PaRuX correctly refused to declare a winner instead of fabricating a decode-speed
  number from reasoning tokens. Recorded as **INCONCLUSIVE** (the competitor's own response shape
  makes a fair content-token race impossible with this specific model), not a PaRuX defect and not
  a PaRuX loss.

### 2026-09-09 (continued) real user report: Model Hub downloads - four real, confirmed, fixed defects

The user reported three concrete problems from their own real use of Model Hub: starting a model
download gives no feedback for so long it looks broken, tempting repeated clicks that queue
duplicate downloads; the difference between "Download checkpoint" and "Activate downloaded" isn't
clear; and there is no way to cancel a download. Investigating by actually driving the real Model
Hub UI (not just reading the code) turned up four distinct, real, root-caused defects - all fixed
and verified live:

1. **"0 B" sizes and blank hardware-fit guidance for every GGUF variant.** Confirmed live against
   the real Hugging Face API: the search/list endpoint used by `SearchAsync` never returns a
   sibling `size` (even with `full=true`), and `GetDetailsAsync`'s single-repo endpoint
   (`src/PaRuX.Server/ModelDownloadService.cs`) also returned none without `?blobs=true` appended.
   Fixed by adding `blobs=true` server-side and, client-side in `ModelHubView.ChooseFileAsync`,
   preferring the detail lookup's real size over the always-zero search-result size (mutated onto
   the shared `ModelFileOption` so the actual download request and its `TotalBytes` progress
   tracking get the real size too, not just the comparison dialog's display).
2. **HARDWARE FIT and GUIDANCE columns invisible even after the size fix.** Confirmed via Windows
   UI Automation that these two TextBlocks held perfectly correct content (e.g. "Excellent
   full-GPU fit · AMD Radeon 780M Graphics · est. 3.4 GB") but a genuinely zero-size layout rect.
   Root cause: `ContentDialog`'s own themed template clamps content to a default `MaxWidth` (~548
   effective px) regardless of what the inner `ListView` requests - three narrower columns
   (FILE/QUANT/SIZE) alone consumed nearly all of that reduced width, leaving the two widest star
   columns nothing. Fixed by overriding both the dialog's `MaxWidth` and its `ContentDialogMaxWidth`
   theme resource (the template binds to the resource ahead of the instance property) to 1040,
   plus switching `AddCell`'s `TextWrapping.Wrap` to single-line ellipsis with a tooltip (Wrap
   inside star columns was a contributing measurement hazard in its own right, independent of the
   dialog width).
3. **No feedback for several seconds after clicking a download button, inviting duplicate
   downloads.** `CreateDownloadAsync` on the server awaits a real HEAD request to Hugging Face
   (confirmed live: several seconds is normal) before returning, and none of the three
   download-triggering call sites disabled anything or showed status before that await. Fixed by
   calling the existing `SetBusy(true, "Starting …")` synchronously before the network call at all
   three sites (media checkpoint download, the dialog's "Download selected", and the
   "Compare variants" buttons), and disabling the media-checkpoint button immediately on click.
4. **No way to actually discard a paused or failed download.** Pause/Resume is real and does work
   (verified live: pausing a real in-flight download correctly stopped it and kept the partial
   bytes) - the reported "can't even cancel" gap was that once paused, "Resume" was the only
   further action offered; there was no way to abandon it and reclaim the disk space. Added
   `ModelDownloadService.Remove` (server) restricted to `cancelled`/`failed` jobs, a
   `DELETE /api/v1/models/downloads/{id}` endpoint, `ModelDownloadClient.RemoveDownloadAsync`, and a
   confirmation-gated "Remove" button next to Resume. Verified live end-to-end: removed job
   disappeared from `/api/v1/models/downloads` and its `.partial` file was actually deleted from
   disk.

Also renamed "Download checkpoint" / "Activate downloaded" to "1. Download checkpoint" /
"2. Activate downloaded" so the two-step relationship reads immediately instead of needing the
paragraph of prose above them.

**Noticed in passing, not fixed:** while scrolled deep into Model Hub's Downloads section, the
Quick Chat bubble's fixed bottom-right dock (relative to the main window, not page content) can sit
on top of that section's Resume/Remove buttons - a real synthetic click landed on the bubble instead
of Remove during this testing. The bubble's placement has no page-content awareness by design; this
is a known limitation of corner-docking, not a regression, and out of scope for this pass since the
user's original bubble request was specifically about the Chat composer.

Added `ModelDownloadServiceTests`: `Remove_deletes_the_job_and_the_partial_file_for_a_paused_download`,
`Remove_refuses_a_download_that_is_still_in_flight`, `Remove_returns_false_for_an_unknown_id`,
`GetDetailsAsync_requests_blobs_true_and_returns_real_sibling_sizes`. Full
`tools\release-gate.ps1 -Restore` re-run after these fixes: green, 2662/2662 core tests.

**Standing instruction reaffirmed:** the user explicitly directed continuing to test every app
feature this way - through the real UI, one at a time, not just checking that controls are present.

**Follow-up, same session:** after the fix above, the user reported live that the bubble now sat
correctly bottom-right but floated too high, with a large empty gap beneath it instead of being
anchored to the window's true bottom edge. Root cause: `DefaultBottomComposerClearanceLogical`
docked the bubble ABOVE the composer with a large 150-logical-px safety margin. Re-measured live via
UI Automation on the real running app: the composer/Send button grows upward (the star-sized message
list above it absorbs extra lines, not the space below the composer), so the gap between the
composer's bottom edge and the window's true bottom edge is structurally stable regardless of
composer height - window bottom to Send's bottom edge measured ~71.5 logical px. Recalibrated to
dock the bubble BELOW the composer instead, in that stable gap, using the same 20-logical-px inset
already used for corner-docking elsewhere in the same file - this leaves ~7.5 logical px of clear
margin below Send's bottom edge while sitting visibly anchored at the window's base, matching what
was asked. Verified live: bubble bottom sits exactly 20 logical px above the window's bottom edge,
top edge sits below (i.e. does not overlap) Send's bottom edge. Existing
`BubblePlacementPolicyTests`/`BubbleWindowRestorePositionOrderTests` still pass unchanged since they
exercise the mechanism generically, not this specific constant.

### 2026-09-09 (continued) real-UI physical-PC testing: bubble/composer collision fixed, training failures made diagnosable, two more real defects found and fixed

Continued the same physical-PC pass, this time driving the actual packaged app through its real
UI (computer-use + Windows UI Automation for DPI-accurate measurement), per explicit operator
direction to exercise chat, training and navigation through the interface rather than only the
HTTP API.

**Real defects found and fixed:**
- Quick Chat bubble collided with the main composer's Send button: two independent, competing
  positioning systems (an older vertical-center dock and the newer adaptive bottom-right dock)
  were both subscribed to the same window-changed event and fought over the bubble's position, and
  the adaptive system's composer-clearance constant (118 logical px) was never validated against a
  real measurement — live UI Automation measurement showed only ~10.5 logical px of real margin.
  Removed the dead legacy system and recalibrated the clearance to 150; verified live the margin is
  now ~42.5 logical px.
- Training Lab failures were completely undiagnosable: a failed job's only visible evidence was
  "Training process exited with code 1," because every stdout/stderr line PumpAsync could not parse
  as a metrics/step sample was read and silently discarded. Added a bounded (40-line) tail buffer
  surfaced in the job's error on failure. Verified live: a real SFT job on this iGPU-only machine
  now correctly reports Unsloth's own `NotImplementedError: Unsloth cannot find any torch
  accelerator? You need a GPU.` — confirming local training genuinely requires a GPU accelerator
  this machine's torch build cannot see (`HARDWARE_NOT_AVAILABLE`, not a software defect; no
  CPU/DirectML fallback exists in the current training script for any of the six methods).
- A second real, order-dependent test-isolation gap (same class as the StateDir one below):
  `HotModelPoolCatalogTests` never scoped `LocalAiPaths.Root`, so `HotModelPoolCatalog.Build`'s real
  reads of `Root/state/hot-model-usage.json` could inherit a different, already-deleted test's
  stale temp directory via `LocalAiPaths`' process-wide `_cachedRoot` fallback — reproduced live as
  a real gate failure, fixed by scoping `LocalAiPaths.UseRootForTests` in the test class.

**Confirmed via real UI/API:** AutoLab (dataset curation, Eval/SFT generation, training,
adapter manager, sweep/compare/promote) exists and is wired end-to-end as designed; promotion is
deliberately never automatic (recommends, does not auto-swap the user's model) — that is the
intended safety behavior, not a gap. `Check this run` preflight and `Start local training` both
work correctly through the real page, including the honest CUDA-not-verified/torch version banner.

**Found but not fixed (documented, reverted after a worse regression):** with Advanced mode on and
enough recent conversations to fill the sidebar, NavigationView's FooterMenuItems
(Create/Projects/Model Hub/Model Intelligence, and half of Lifecycle Lab) render underneath the
Recent-chats scrollable region and are unreachable by scrolling it. `NavView.UpdateLayout()` after
the visibility change does not fix it; toggling `IsPaneOpen` closed/open does reach the items but
left the pane stuck in its Compact (icon-only) state instead of restoring `OpenPaneLength` — a
worse regression than the bug, so it was not shipped. Full detail and next-step ideas are in
`MainWindow.Workspaces.cs`'s `ApplyNavigationMode`. Workaround for testing: Windows UI Automation's
`ScrollItemPattern.ScrollIntoView()` on the affected item both reveals it and (as a side effect)
corrects the layout.

Full `tools\release-gate.ps1 -Restore` re-verified green after each fix (final run: 2654/2654 tests).

### 2026-09-09 physical-PC acceptance (iGPU-only workstation): first full green `release-gate.ps1`, six real defects fixed, further matrix rows exercised

Continued physical-PC acceptance on the same reference workstation, now with only the AMD Radeon
780M iGPU present (no eGPU/dGPU attached). `tools\release-gate.ps1 -Restore` had apparently never
actually been run end-to-end on this exact toolchain: it failed to parse under the machine's real
PowerShell 7.6.5 on first attempt. After fixes below, both `tools\release-gate.ps1 -Restore` and
`tools\validate-software-complete.ps1 -IncludeAndroid` passed cleanly and repeatably: full Release
x64 build, 2650/2650 xUnit tests, App/Core/CLI/UpdateHelper/WinML publish, 11/11 VS Code tests,
Android clean test/lint/debug+release APK build.

**Six real defects found and fixed on this pass** (see commit history from 2026-09-09 for full
detail):
1. `tools/release-gate.ps1` used a line-continuation form the real PowerShell 7.6.5 parser rejects
   outright — the mandated gate command could not even start.
2. Two genuine Release-build compile errors on current `main` (`AutomationEngine.cs`,
   `RepoIndexService.cs`) that no prior local build had actually caught.
3. A real per-request performance regression: `GpuPerformanceTelemetryService` re-probed up to six
   `nvidia-smi` process-spawn attempts on *every* plain chat completion (the admission guard now
   runs on every turn, not just model loads), costing ~250–360ms fixed overhead per turn on any
   machine without an NVIDIA GPU — measured failing `ParuxOverheadBenchmarkTests` on this exact
   iGPU-only hardware. Fixed by caching the resolved-or-absent `nvidia-smi` path once per process.
4. A real fake-success bug: with Gaming Mode active, chat completions silently returned HTTP 200
   with an empty body (confirmed via raw `curl`) instead of a clear error, because the route handler
   in `Program.cs` redundantly re-combined an already Gaming-combined cancellation token and treated
   "my own token is cancelled" as "client disconnected, stay silent." Fixed by deleting the
   redundant handler logic and letting `ChatSchedulingEndpointExtensions`'s correct wrapper handle
   it; added a regression test that fails against the reverted handler and passes against the fix.
5. A real test-isolation gap surfaced while manually reproducing (4): `LocalAiPaths.StateDir`
   (mode/active-role/PID/game-marker files) was a real, machine-global path with no test seam, so
   interactive Gaming-Mode probing against the live app left a stray marker that then made an
   unrelated "isolated" xUnit test observe a real game as active. Added a `UseStateDirForTests` seam
   mirroring the existing `Root` seam and scoped the affected test class to it.
6. Four stale/brittle checks: a superseded version-drift error-message assertion, an XML-doc-comment
   line-wrap that broke a raw-substring contract check, an evidence-neutralization test asserting the
   wrong delimiter form, and `validate-software-complete.ps1` hard-coding the VS Code extension's
   pre-refactor entry-point file.

**Real hardware/product evidence gathered** (produced self-contained binaries, not `dotnet run`):
launched the actual built app; confirmed real end-to-end inference on the AMD Radeon 780M via
Vulkan (Qwen3.5-4B-Q4_K_M, ~24–28 tok/s decode, consistent with the 2026-09-07 baseline); mid-stream
STOP/cancellation recovers cleanly with no stranded lease; 4 concurrent chat requests all completed
(continuous-batching reader sharing); a genuine RAM-pressure 503 admission-guard rejection occurred
for real (4.6GB free of 25.8GB while PaRuX + LM Studio + this session competed for memory) and the
exact same resident recovered cleanly once memory was freed, with no restart; Vision Subagent's
"no eligible vision-capable resident installed" fail-closed path fired for real (503, no fabricated
observation) — no vision-capable model small enough to safely load on this 32GB-RAM iGPU machine is
currently installed, so the full evidence-pass/OCR/injection-resistance/cancellation-during-helper
cases were exercised at the unit level (fixed, now passing) rather than against a live resident;
fresh MSI built from this exact SHA (`PaRuX-1.7.0-win-x64.msi`) but install/uninstall could not run
this session — it requires an elevated shell and this session cannot self-elevate (confirmed:
`schtasks /RL HIGHEST` itself returns Access Denied from a non-elevated admin token; Windows UIPI
blocks the synthetic-UAC route too), matching the prior documented finding; a real Android emulator
(local Android 15 test AVD) booted, the fresh debug APK installed and launched cleanly with no crash;
same-machine PaRuX vs LM Studio vs Ollama benchmark ran for real via the repository harness —
embedding retrieval produced real comparable numbers, chat decode came back honestly
**inconclusive** for all three providers (hidden Qwen3.5 reasoning tokens exhausted the token budget
before visible content), which is the harness's fail-closed policy working as intended, not a defect.
Engine Race and same-hardware provider-vs-provider races remain untested this pass: only one
llama.cpp (Vulkan) build is installed, and the WinML worker's own real provider probe on this
hardware correctly reported no usable NVIDIA/AMD-compute execution provider (`NOT_PRESENT`) rather
than fabricating readiness — the NPU visible in Device Manager has only the generic in-box driver,
no vendor NPU runtime package.

Still open, deferred rather than skipped: MSI install/upgrade/downgrade/uninstall cycle (needs an
operator-run elevated pass); ComfyUI/Diffusers real media generation and a 27B-class vision-capable
model exist on this machine but were not exercised this pass given real, shared iGPU/system memory;
a dedicated ≥30-minute soak; Android pairing/companion-security-boundary flow beyond install+launch;
coding-agent competitive benchmark against Claude Code (installed, not run this pass).

Status remains **PHYSICAL_PC_ACCEPTANCE_REQUIRED** — the software gate is fully green and several
new matrix rows are now real evidence, but the rows above still need either an operator-elevated
pass or hardware this machine does not currently have attached.

### 2026-09-08 (continued) physical-PC acceptance: software gate reverified, real UI defect found and fixed, further matrix rows exercised

Continued the physical-PC acceptance pass on the same reference workstation. Ran the mandatory
`tools\validate-software-complete.ps1 -IncludeAndroid` gate: clean build (0 errors), all 2562 xUnit
tests passed including `WindowsCodingSandboxIntegrationTests` and `ParuxOverheadBenchmarkTests`
(both previously blocked by non-elevated/isolated-root gaps, now passing on this elevated,
real-install run), 10/10 VS Code tests, Android debug+release build succeeded.

Launched the freshly built app (not yet reinstalled via MSI this pass) and exercised it for real:
local inference confirmed end-to-end via the OpenAI-compatible endpoint (Qwen3.5-4B on the
PaRuX-managed llama.cpp Vulkan runtime, AMD Radeon 780M iGPU, TTFT ~730ms, ~20 decode tok/s);
Runtime Manager's live hardware-fit detection (DXGI: "AMD Radeon 780M Graphics 5.9 GB · system RAM
25.8 GB"); Normal/Advanced UI disclosure toggle; component ownership detection (PARUX_MANAGED
llama.cpp CUDA+Vulkan/ComfyUI/Python toolchain, SYSTEM_DETECTED Git/FFmpeg/Node); the runtime
regression-gate's honest deferral of a previously-tested candidate build that was no longer
actually installed; and a real, honestly-surfaced 503 admission-guard rejection under genuine
system memory pressure (RAM/VRAM-pressure row: guard correctly refuses rather than OOMing).

**Real defect found and fixed:** clicking "Race installed runtimes…" in Runtime Manager with only
one PaRuX-managed llama.cpp build installed returns an honest 400 from the Server, but
`RuntimeManagerView.RaceRuntimesAsync`/`OptimizeAsync`/`ApplyPerformanceModeAsync` had no `catch`
clause, so the expected error propagated as an `UNHANDLED` exception out of an `async void` click
handler (confirmed in `parux.log`). The app's global handler kept the process alive but left the
chat composer and sidebar navigation unresponsive to input until a full restart — a real recovery
gap, not fake success. `ChoosePerformanceModelAsync`'s file-picker call had the same unguarded
pattern. Fixed by adding `catch` clauses matching the already-correct `RefreshAsync`/
`CancelPerformanceAsync` pattern; verified by reproducing the exact freeze on the unpatched build,
then confirming the same 400 is handled gracefully and the app stays fully interactive on a
rebuilt binary. Commit: "Fix Runtime Manager UI freeze on expected server errors". The software
gate was rerun after the fix: one run hit an unrelated transient test-host crash in
`WindowsCodingSandboxIntegrationTests` under real, tight system memory (7.5GB free of 25.8GB
shared, multiple unrelated heavy processes active on this shared dev machine) with no Windows
Error Reporting record (consistent with an OOM-driven managed exit, not a code fault); a clean
rerun with more free memory passed 2562/2562 again, and the failure did not reproduce.

**Still open on this pass** (deferred, not skipped — real gaps, not reclassified as environment
limitations):
- MSI upgrade-from-1.6.0 and downgrade-guard: a fresh `PaRuX-1.6.1-win-x64.msi` built from this
  exact `main` HEAD is ready in `artifacts\installer\`, and the prior `PaRuX-1.6.0-win-x64.msi` is
  available as the upgrade source, but `scripts\test-msi.ps1` requires an elevated shell and this
  session cannot self-elevate (Windows UIPI blocks synthetic input to UAC prompts by design). Needs
  an operator-run elevated pass with `-CurrentMsi artifacts\installer\PaRuX-1.6.1-win-x64.msi
  -PreviousMsi artifacts\installer\PaRuX-1.6.0-win-x64.msi`.
- ComfyUI/Diffusers real media generation and WinML/ONNX Runtime GenAI provider discovery +
  generation: real checkpoints are already installed on this machine (Flux-2-Klein-4B, WAN2.2,
  Z-Image-Turbo) and the `windows-ml-genai` engine is registered as a live, vendor-neutral
  implementation, but no run was attempted this pass given real, tight shared iGPU/system memory
  (down to 4.9GB free at the time) and this being an actively multitasked dev machine — deferred by
  explicit operator choice rather than skipped silently.
- Engine Race: the safety mechanism itself is verified (correct real-evidence deferral of an
  uninstalled candidate build, confirmed via `/api/v1/runtime/performance/engine-races` and
  `server/runtime-regression-evidence.json`), but no fresh cross-build or cross-format race
  completed this pass — only one llama.cpp build per backend and one model format (GGUF) is
  currently installed on this machine.
- Fuller same-machine competitor benchmark matrix (VRAM residency, concurrent scaling,
  cancellation, long-context degradation, sustained load, a 27B-class model): still only the
  2026-09-07 single-model/single-profile PaRuX vs LM Studio vs Ollama result exists.
- Android emulator smoke test: three AVDs exist on this machine (`android-35` target); not run this
  pass for the same real memory-pressure reason as the media/WinML rows. (This check is explicitly
  optional/non-blocking per `docs/RELEASING.md`; the Android JVM/lint/APK build itself already
  passed as part of the software gate.)

Status remains **PHYSICAL_PC_ACCEPTANCE_REQUIRED** — not every row has genuinely passed yet, so
promotion to `REFERENCE_VERIFIED` would be premature.

### 2026-09-08 physical-PC MSI acceptance: install/uninstall cycle PASSED

Ran `scripts/test-msi.ps1` for real, elevated, on the target Windows 11 (build 26100) workstation.
First attempt failed immediately: a genuine Windows 11 machine hit "PaRuX requires Windows 10 or
newer" on a clean install. Root cause was in the MSI engine, not the WiX source: the built-in
`VersionNT` launch-condition property is fed by `GetVersionExW`, which the OS's
application-compatibility shim layer overrode for this msiexec invocation, resolving to 603
(Windows 8.1) instead of 1000 — confirmed independent of the compiled `LaunchCondition` table text
(byte-correct), the Windows Installer engine version (current, 5.0.26100.9223), and any per-file or
system-wide AppCompat shim registry entry (none present). Fixed by replacing the `VersionNT` check
with a `RegistrySearch` reading `CurrentMajorVersionNumber` directly — a plain DWORD introduced in
Windows 10 with no version-query API involved, so it cannot be shimmed the same way.

After the fix, the full cycle passed cleanly on the same machine: clean install, all owned binaries
present (`PaRuX.exe`, `PaRuX.Server.exe`, `parux.exe`, protected Update Helper), CLI PATH
registration, `LOCALAPPDATA\PaRuX` state preserved across install and uninstall, clean uninstall
with nothing left behind. This closes the MSI install/upgrade/uninstall row of the acceptance
matrix in `docs/PARUX_COMPETITIVE_GAP_2026-09.md`. Upgrade-from-previous-version and downgrade-guard
were not exercised this pass (no prior-version MSI was available on this machine) — still open.

### 2026-09-07 local build/test verification

A full local `dotnet build`/`dotnet test` pass on a real Windows workstation found the prior
closeout's "builds clean" claim was stale: the pulled `main` had 14 compile errors (a
`Microsoft.ML.OnnxRuntimeGenAI` API-surface drift in `PaRuX.WinMlWorker`, missing
`PaRuX.Contracts` usings, a `ValueTask`/`Task` mismatch on `StreamReader.ReadToEndAsync`, and
four missing `using Xunit;` in newer test files) plus 20 failing tests. All compile errors and 16
of the 20 test failures were real regressions and are now fixed (delegation-guard message
ordering, an execution-resource-guard telemetry-completeness inconsistency, a dependency-evidence
budget overflow from an unreserved separator, an empty virtual-context ledger being injected as
real content, a token/character budget mismatch that let recency fill evict
`ConversationContextExtractor`'s durable-state recall block, a missing `/api/v1/mobile/artifacts`
route despite the paired-device policy already allow-listing it, and a few message-wording/test-
fixture gaps). See commit history from 2026-09-07 for the individual fixes.

The remaining 4 failures are environment-specific to a workstation that already has a real,
non-isolated LocalAI installation and a non-elevated process token, not source defects:
- `WindowsCodingSandboxIntegrationTests` (2): real AppContainer creation fails with
  "Accesso negato" under this session's split UAC token (Administrators-group member, not
  elevated). Needs the physical-PC gate to run elevated.
- `ParuxOverheadBenchmarkTests` (2): `LocalAiPaths.Root`'s process-wide static cache resolves to
  this machine's real, already-populated `LocalAI` folder (by design - see `LocalAiPaths`'s own
  doc comment) instead of an isolated temp root, so the benchmark drives the real installed
  runtime instead of the in-proc stub and hangs past the client's 2-minute timeout. The test file
  already documents this exact isolation gap as investigated-and-reverted; re-litigating it is
  physical-PC-gate scope, not a `main` regression.

## Implemented baseline — subject to the active competitive gaps

The following production paths exist, with regression/gate coverage where applicable. This inventory does not certify every advertised capability, hardware profile or workflow as complete; use the active BIC backlog and current evidence for closure:

- managed llama.cpp CUDA/Vulkan inference, exact manual model locking, AUTO model routing and measured runtime tuning;
- integrated ExLlamaV3 and vendor-neutral Windows ML / ONNX Runtime GenAI serving behind `ILocalInferenceEngine`, with exact variant/provider identity, Engine Race evidence and guarded AUTO promotion/fallback;
- MTP/DFlash/DSpark/EAGLE-style speculative discovery and measured promotion rather than unconditional enablement;
- continuous batching, prompt/KV-cache controls, multi-GPU placement and bounded long-context handling;
- OpenAI-compatible Chat Completions, Responses, embeddings and local Videos compatibility plus Anthropic Messages compatibility;
- stateful `previous_response_id`, tool calling, multimodal tool results, structured output, reasoning and edit/continue behavior where the active engine genuinely supports it;
- MCP stdio + Streamable HTTP/SSE, OAuth 2.1, per-server/tool allowlists, prompt-aware schema narrowing and deterministic parallel sibling tool execution;
- durable conversation branching/version navigation, bounded Windows chat visual rendering and attachment-safe branch retention;
- persistent Projects/Knowledge/Artifacts, Memory/RAG, agentic knowledge tools, Virtual Context V2 and Semantic Index V2 including cross-repository use;
- coding agents with durable task graph, worktrees, multi-repository authority, warm/prewarmed environments, verification, checkpoint/resume, generic durable `ask_user`, VS Code V2 and Universal STOP;
- Computer Use with desktop authority boundaries and supervised paired-client controls;
- image/edit/img2img/upscale/video/audio/3D generation, ComfyUI + independent managed Diffusers image Engine Race and semantic quality judging;
- bounded media-job memory/polling payloads and on-demand media streaming;
- Dataset → Train → Adapter → Eval / AutoLab and the six supported training methods exposed consistently across product surfaces;
- Execution Profiles, Goal Mode V2, Plugin Bundles, Visual/App Context and observability/benchmark surfaces;
- Android companion source/API parity through sanitized Core-owned projections, including Chat, media/jobs, Projects/PC Studio, Goal Mode, profiles, models/tools, observability, Live Voice and supervised long-running agents;
- backup/restore, storage quotas, remote-deny-by-default paired-device policy, credential protection, update/runtime integrity and Gaming hard-off;
- headless `parux serve` and a loopback browser client for the Windows/Core execution host.

**GitHub Actions remains disabled.** Local validation is authoritative.

## Competitive reference rule

A source feature is not called superior merely because an endpoint or descriptor exists. Response speed is a first-class competitive acceptance target alongside functional parity, quality and reliability; a “best-in-class” claim is limited to workload scopes supported by current measured evidence. Competitive claims follow these rules:

1. Functional parity is checked against current leaders and must be implemented in a callable production path, not a stub.
2. Local engine performance is compared on the **same physical PC**, with the same model-file hash/revision, tokenizer/chat template, instructions, sampling/runtime settings, context/output limits, tool set and quality policy under equivalent warm/cold conditions. Product-level comparisons with cloud Codex must instead disclose model/hosting differences and evaluate task outcomes as defined above.
3. For interactive work, measure submit-to-first-useful-visible-content and submit-to-completion separately, alongside real-token decode/prompt throughput, memory/VRAM, concurrency, cancellation recovery and long-context degradation. For media jobs, measure first-usable-preview and completed-output latency separately from sustained throughput. Report distributions (including p50/p95) and reliability, not a single best run.
4. A faster result that loses quality does not win.
5. Any measured competitor win becomes an optimization defect until PaRuX wins/ties within the accepted tolerance or a genuine hardware/runtime constraint is documented.
6. Old benchmark evidence from another machine never authorizes AUTO promotion on the current machine.

The earlier closeout used LM Studio 0.4.23/0.4.22 and Jan 0.8.4/0.8.3-era references. Treat those versions as historical context. The active 2026-09-10 comparison above broadens the reference set and requires current version/configuration evidence for each claim; a represented feature name does not close a workflow or quality gap.

## Non-blocking scope distinctions

These are deliberately **not additional release blockers**:

- **Physical Android phone** — optional exploratory QA. JVM tests, lint and APK assembly are software checks; emulator/instrumented checks may run from the final Windows PC when available.
- **Code-signing certificate** — publication credential owned by the maintainer, not software completeness. Unsigned local acceptance artifacts are valid for the final PC gate.
- **winget/other package-manager submission** — distribution work after a stable public artifact exists, not a functional/performance gap in the application.
- **macOS/Linux desktop shell parity** — PaRuX's reference product target is Windows with an Android companion. Portable Core/headless support is useful reach, but another native desktop shell is not part of this Windows reference acceptance.
- **GitHub Actions/cloud CI** — disabled by project policy; no cloud workflow is required for acceptance.

## Physical Windows PC acceptance — required alongside the software backlog

The former "Sole remaining gate" interpretation is superseded: BIC-01 through BIC-16 contain actionable software work. After implementing the relevant fixes and reconciling stale status-only contracts, run the full local gate on the target workstation without diagnostic skip switches:

```powershell
powershell -ExecutionPolicy Bypass -File tools\validate-software-complete.ps1 -IncludeAndroid
```

Then execute the produced binaries and acceptance matrix on that same PC. The run must cover:

- clean restore/build/test/publish plus MSI install/upgrade/uninstall lifecycle;
- WinUI, tray, overlays, clipboard/file pickers, local browser, Computer Use and OS integration;
- real GPU selection, model load/unload, VRAM/RAM pressure, cancellation/recovery, sustained load and thermal behavior;
- ComfyUI/Diffusers and image/video/audio/3D/training execution on the installed hardware stack;
- Windows ML provider discovery/readiness, resident ONNX Runtime GenAI generation, cancellation/reload/fallback and any actual GPU/NPU/CPU EP present;
- same-hardware Engine Race for eligible GGUF/EXL3/ONNX variants and proof that only exact, same-quality measured winners promote;
- same-machine competitor benchmarks for every performance claim intended for release;
- Android test/lint/APK build and optional emulator paired-client smoke/instrumentation from the same workstation.

Promote the reference status to `REFERENCE_VERIFIED` only when the active software backlog and all applicable acceptance rows are closed with evidence for the exact candidate commit/configuration. An explicitly unsupported hardware profile is not a passed profile, and publication credentials are assessed separately. Until those conditions are met, retain an honest PARTIAL/BLOCKED-EXTERNAL item status and **PHYSICAL_PC_ACCEPTANCE_REQUIRED** for the remaining Windows acceptance; do not restore the blanket software-complete conclusion from historical text or descriptor tests.

### 2026-09-19 WorkloadOrchestrator subsystem (Gaming & AI Resource Sharing) completion

- **Subsystem**: Implemented `WorkloadOrchestrator` state machine, REST API (`/api/v1/gaming/*`), and pluggable providers (`Trigger`, `Launcher`, `Input`, `Suspend`).
- **Quick Resume**: Integrated P/Invoke Win32 `NtSuspendProcess`/`NtResumeProcess` (`WindowsNativeSuspendProvider`) and Nyrna CLI integration (`NyrnaGameSuspendProvider`).
- **UI Integration**: Added "Gaming & AI Resource Sharing" section in `SettingsView.xaml` and `GamingOrchestratorClient.cs` for full WPF control.
- **Verification**: Built `PaRuX.Server` and `PaRuX.App` with 0 compilation errors; passed all 5 `WorkloadOrchestratorTests` unit tests.

### 2026-09-27 BIC-06/13/16 focused validation and ComfyUI update (partial)

- `Instradamento modelli` now exposes the managed-GGUF picker and its empty/authentication states to UI automation, and disables Add when candidate evidence cannot be loaded. The desktop bootstrap probe verifies the lightweight authenticated data-root endpoint, loopback endpoint and exact server-process identity. Settings preserves the option to assign a Standard model to the Uncensored role. Focused FlaUI tests passed 8/8 after independent review findings were fixed and re-reviewed PASS; the interrupted broad FlaUI run remains incomplete.
- The Model Hub now distinguishes the official Qwen-Image-2.1 workflow bundle from a community-labelled uncensored GGUF. Both are labelled as unverified candidates; their license, required components, workflow links and lack of a guided bundle installer are visible. This is not hardware-qualified discovery or an automatic download recommendation. Explicit license acceptance and real GPU quality/fit evidence remain required before downloading or promoting any Qwen image variant.
- PaRuX's in-app managed-runtime API installed ComfyUI `v0.37.0` (`nvidia`) and retained the previous `legacy` build for rollback. The 7z extractor was changed to a forward-only path for 7z, with a portable embedded-archive regression test; focused extractor tests passed 5/5. `/system_stats` confirmed ComfyUI 0.37.0, CUDA on the RTX 5080, and controlled MediaStart/MediaStop succeeded. Existing model folders/files remained present. No Qwen image weights were downloaded or loaded, and no image was generated with this new runtime.
- The 2026-09-26 BIC evidence report at `.ai/artifacts/bic-complete-results-20260926.md` remains **NO-GO**. Full Server.Tests passed 3,491/3,491 before the extractor change; rerun the full suite and serial full FlaUI regression after this diff. BIC-02/03 competitive quality/speed, BIC-04/08/13 hardware inference, BIC-05 live SDK, BIC-07 installer, BIC-12 Office, BIC-14 browser/Android and BIC-15 network isolation still need their stated acceptance evidence. Do not claim Codex parity or stable release readiness from these focused results.

### 2026-09-27 continuation: full server suite and patch-apply diagnosis

- `dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj --no-restore` completed in 6m37s with **3,491 passed, 1 failed, 0 skipped / 3,492 total**. The failure is `ComfyRuntimeInstallServiceTests.Media_install_downloads_verifies_extracts_preserves_data_activates_and_rolls_back`, which ended at stage `failed`; evidence: `.ai/artifacts/bic-complete-server-suite-20260927-rerun.log`. Focused rerun `.ai/artifacts/bic-comfy-runtime-install-focused-20260927.log` isolated the cause: that installer test streams ZIP bytes through the `.7z` staging filename, while the extractor chose the 7z-only `ExtractAllEntries()` path from the filename extension. SharpCompress then correctly rejected the non-7z data. The full gate is not green; classify archives by their actual signature and rerun the focused and full suites.
- `SafeArchiveExtractor.ExtractAsync` now chooses the fast sequential 7z path from the six-byte 7z signature, not the suffix; non-7z content uses the async reader. Added coverage for ZIP data named `.7z`, kept the real 7z fixture, and surfaced the installer error in the integration-test assertion. `dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj --no-restore --filter 'FullyQualifiedName~SafeArchiveExtractorTests|FullyQualifiedName~ComfyRuntimeInstallServiceTests'` passed **11/11** in `.ai/artifacts/bic-runtime-extractor-focused-20260927.log`. The full suite still needs rerun.
- BIC-09 report from the operator: a locally generated repository change can be solved but appears not to apply. `CodeWorktreeApplyService` now permits dirty targets but retains `git apply --check`, so unrelated user edits apply safely and a conflicting edit fails before any target file changes. The apply command now honors the same Git and `.gitattributes` line-ending settings used when creating the task worktree; the former forced `core.autocrlf=false` made unchanged CRLF files look modified. `CodeWorktreeApplyServiceTests` proves staged, unstaged and untracked unrelated edits survive and that a conflicting patch with a second new file is rejected atomically. Focused command `dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj --no-restore --filter 'FullyQualifiedName~CodeWorktreeApplyServiceTests'` passed **4/4**, log `.ai/artifacts/bic-repo-apply-focused-r3-20260927.log`. Independent review and a live/FlaUI apply-flow check remain pending.
- BIC certification remains **NO-GO**. The server result above does not replace the incomplete FlaUI, installer, Office, browser/Android, hardware inference/media, competitor and network-boundary evidence recorded in `.ai/artifacts/bic-complete-results-20260926.md`.

#### Gate update — 2026-09-27

- Fixed the extractor regression above and reran `dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj --no-restore`: **3,494/3,494 passed, 0 skipped, 6m13s**. Log: `.ai/artifacts/bic-complete-server-suite-20260927-postfix.log`; server suite gate now passes.
- `CodeWorktreeApplyServiceTests` remains **4/4** (`.ai/artifacts/bic-repo-apply-focused-r3-20260927.log`). Independent diff review returned **PASS** after updating the service XML documentation to match the new dirty-target contract. Staged, unstaged and untracked unrelated user edits are preserved; conflicting target edits reject the complete task patch before writing a new task file.
- At this checkpoint, UI visibility/apply-flow evidence, full FlaUI, Release build, installer lifecycle, live local inference/media, Office/browser/Android, and network-boundary evidence remained open. Subsequent FlaUI and build gates are recorded below; BIC certification remains **NO-GO**. See `.ai/artifacts/bic-complete-results-20260927.md` for the current matrix and remaining blockers.

#### FlaUI gate update — 2026-09-27

- Added a Debug/UI-test-only completed coding-task fixture and FlaUI regression proving the completed card exposes its enabled “Apply changes to project” action without starting or mutating a Core task. Focused test **1/1 passed** (`.ai/artifacts/ui-apply-20260927/focused-fla-ui-r2.log`). The service tests separately exercise actual Git patch application and conflict rejection.
- First full serial `Regression` run was **49/52** (`.ai/artifacts/bic-complete-fla-ui-20260927.log`). It found two optional-screenshot foreground failures, an obsolete workload-version expectation, and later a search-field focus race. Snapshot capture now skips safely with a reason file if foreground cannot be verified; test expects workload `v2`; history search explicitly focuses the input and verifies typed text. The three reproduced UI regressions then passed **3/3** (`.ai/artifacts/ui-apply-20260927/fla-ui-failure-repro-r3.log`).
- Full serial rerun passed **52/52**, **0 skipped**, in 1m39s (`.ai/artifacts/bic-complete-fla-ui-20260927-postfix.log`); the responsive-header and tools-navigation screenshots were captured in this run. BIC-06 still needs multiple-DPI/scaling evidence. BIC-09 now verifies the visible completed-task apply action but still needs an end-to-end real Core task confirmation/apply run.
- BIC certification remains **NO-GO**. Release build, installer lifecycle, local inference/media, Office/browser/Android and network-boundary evidence are still open; refer to `.ai/artifacts/bic-complete-results-20260927.md`.

#### Final extractor review follow-up — 2026-09-27

- Independent review identified the SharpCompress sequential `IReader` lifetime in `SafeArchiveExtractor`; the reader is now disposed before its archive, including failure/cancellation paths. Focused extractor + Comfy installer tests passed **11/11** after the change (`.ai/artifacts/bic-runtime-extractor-focused-20260927-r2.log`), and independent re-review returned **PASS**, with no additional findings.
- The first 3,494/3,494 full Server.Tests result above predates this disposal-only correction. The final rerun passed **3,494/3,494, 0 skipped, 6m10s** (`.ai/artifacts/bic-complete-server-suite-20260927-final.log`). Server Release build after the disposal fix passed with 0 errors/13 warnings (`.ai/artifacts/release-builds-20260927/server-release-final.log`); App Release passed earlier with 0 errors/8 warnings (`.ai/artifacts/release-builds-20260927/app-release.log`) and its source has not changed since.

#### Android software gate — 2026-09-27

- `src/PaRuX.Android/gradlew.bat testDebugUnitTest lintDebug assembleDebug` succeeded in 1m14s: **29/29 unit tests**, lint passed, and the Debug APK was produced (23.7 MiB). Evidence: `.ai/artifacts/android-validation-20260927.log`; APK: `src/PaRuX.Android/app/build/outputs/apk/debug/app-debug.apk`.
- `adb devices` found no emulator or physical device. Instrumentation, install, and paired-client acceptance remain open; see `.ai/artifacts/bic-complete-results-20260927.md`.

#### Real local ComfyUI GPU/media gate — 2026-09-27

- Confirmed local runtime metadata `v0.37.0`/`nvidia`, RTX 5080 CUDA selection, PyTorch 2.13.0+cu130, and embedded Python 3.13.14. Existing local Z-Image-Turbo/encoder/VAE files were used; no weights were downloaded and no runtime/legacy rollback files were changed.
- A 1024×1024, 8-step image generation completed successfully in **10.04s**. `nvidia-smi` sampling observed 12,510 MiB peak used VRAM and 100% peak utilization; ComfyUI `/free` reduced its reported used VRAM from 5,488 MiB to 1,454 MiB on the next sample. The owned test process was stopped, and port 8191 is closed. Workflow, PNG, metrics, runtime logs and before/after system stats are retained under `.ai/artifacts/comfyui-live-20260927/`.
- This verifies one configuration's execution path only. It is not a model ranking/recommendation or a quality/parity result. Text LLM inference/admission, cancellation/pressure recovery, and video/audio/3D/training acceptance remain open.

#### Runtime, ONNX and focused BIC gates — 2026-09-27

- BIC-05 exercised the official Ollama Python SDK 0.6.2 against a fresh current-checkout desktop-owned server and existing local Qwen3.8-27B GGUF. Basic chat passed, streaming returned six chunks composing `1\n2\n3`, and a non-empty unknown model id returned the expected HTTP 400. The later canonical full rerun passed chat but the harness resource guard stopped during streaming at 1.21 GiB free RAM (<1.5 GiB threshold), so end-to-end canonical acceptance remains PARTIAL. Temporary API keys were revoked and verified absent. Test-created app/server/model-runner processes and stale bootstrap metadata were closed/removed; ports 51123/1234 are closed, GPU use returned to 163 MiB, and free RAM to 14.16 GiB. Evidence: `.ai/artifacts/bic05-live-20260927/`.
- BIC-08 real local ONNX inference installed and loaded `sentence-transformers/all-MiniLM-L6-v2` using PaRuX's embeddings-engine API. Actual inference ran on CPU and produced finite, L2-normalized 384-dimensional vectors; cosine similarity was 0.719517 for related input and -0.076441 for unrelated input. Five warm requests ranged 12.73–95.44 ms. The model is Apache-2.0 ([official model card](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2)); no Qwen-Image-2.1 files were downloaded. Filtered ONNX/DirectML tests passed 2/2 (`.ai/artifacts/onnx-directml-focused-20260927.log`).
- Windows ML worker Release build passed with 0 warnings/errors. Provider discovery exposed MIGraphX and TensorRT-RTX as `NotPresent`, and no AMD NPU provider; physical AMD NPU presence/driver alone does not demonstrate an inference path. NPU execution and cancellation during live inference remain unverified. Evidence: `.ai/artifacts/winml-npu-validation-20260927-build.log` and `winml-npu-validation-20260927-status.log`.
- BIC-12 filtered Workbook/Excel server tests passed 35/35 (`.ai/artifacts/workbook-excel-focused-20260927.log`). The earlier app-unavailable note is superseded: Excel opened the `DocumentWriteService` output via Computer Use and displayed its generated values and formula result `4` (`.ai/artifacts/office-probe-20260927/excel-ui-verification.md`). Existing user-workbook editing and broader layout/formula coverage remain open.
- BIC-16 filtered model-discovery/promotion server tests passed 44/44 (`.ai/artifacts/model-discovery-promotion-focused-20260927.log`). The tests validate software branches only; live model-fit evidence and actual promotion/rollback acceptance remain open.
- BIC-14 browser access-control sub-gate: launched the current test app, opened `http://127.0.0.1:51123` in isolated Chrome via `npx --yes agent-browser open ...`, then checked `/`, `/app`, and `/app/login`. With browser access disabled, each returned JSON `401 Unauthorized`; no application data was exposed and no settings/passphrase were changed. Screenshot/status evidence: `.ai/artifacts/browser-bic14-20260927.png` and `.ai/artifacts/browser-bic14-20260927-http.json`. This satisfies only the unauthenticated-access-control branch; authenticated browser chat/inference and physical/emulated Android interaction remain open.
- Independent review identified that multi-repository tasks use `MultiRepositoryApplyService`, a second path that still rejected dirty targets and forced `core.autocrlf=false`. Added regressions reproduced both defects before the fix: pre-fix multi-repo filter was **3/5** (the unrelated dirty-target case and CRLF configuration case failed), log `.ai/artifacts/bic-multirepo-apply-repro-20260927.log`. The service now preflights all patches with `git apply --check` against the current targets, preserves unrelated staged/unstaged/untracked edits, rechecks before mutation, ignores caller cancellation once mutation begins, and reverse-checks applied results. An interrupted patch whose reverse check fails is now reported as incomplete/uncertain; forward applicability is not used as proof that no partial write occurred. It uses the same Git line-ending settings as worktree creation. The final post-fix filter passed **5/5**, `.ai/artifacts/bic-multirepo-apply-focused-final2-20260927.log`; independent re-review returned **PASS**. A real UI confirmation and apply using a Core-owned task remains open.
- BIC-15 focused local-only/cloud-fallback policy tests passed 27/27 (`.ai/artifacts/network-boundary-focused-20260927.log`). Sampled TCP connections during ONNX inference were loopback only; packet capture, firewall/outbound audit, and disconnected/offline proof remain open.
- BIC-05/BIC-08/BIC-12 evidence is reflected in `.ai/artifacts/bic-complete-results-20260927.md`. MSI lifecycle, browser acceptance, physical Android device, network packet trace, exact end-to-end Core patch apply flow, multi-DPI and competitor quality/performance remain open; overall certification remains **NO-GO**.

#### BIC-09 multi-repository apply review continuation — 2026-09-27

- The final `MultiRepositoryApplyServiceTests` focused rerun passed **5/5** after the last rollback-state correction (`.ai/artifacts/bic-multirepo-apply-focused-final2-20260927.log`). Regression coverage verifies safe application with unrelated staged/unstaged/untracked edits, conflict rejection across all targets, and CRLF behavior with `core.autocrlf=true`.
- Independent review found that a successful forward `git apply --check` does not prove an interrupted apply wrote nothing. The service no longer treats forward applicability as rollback success: if reverse preflight fails for a patch whose apply may have started, it reports rollback incomplete and asks for inspection. Independent re-review returned **PASS**. Mid-process termination is not simulated by the focused suite; the uncertain path is classified conservatively.
- Final App Release build after this correction passed with **0 errors and 6 warnings** (`.ai/artifacts/release-builds-20260927/app-release-multirepo-final3.log`).
- Full `dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj --no-restore` passed **3,496/3,496**, no failures/skips, in 6m11s (`.ai/artifacts/bic-complete-server-suite-20260927-final2.log`). Fresh-layout FlaUI Regression from the current checkout passed **52/52**, no failures/skips, in 1m42s (`.ai/artifacts/bic-complete-fla-ui-20260927-final2.log`; publish at `artifacts/validation/ui-tests/20260927-024010`). Post-run checks found no PaRuX process from that layout and no listener on ports 8191/1234/51123. `git diff --check` passed. BIC-09 stays **PARTIAL** until a real Core-owned task is clicked, confirmed and applied through the UI. Overall BIC certification stays **NO-GO**.

### 2026-09-27 follow-up: credential-bearing redirect boundary

A live dummy-token repro found the App bootstrap probe and `ConversationSyncClient` followed 302 responses; an independent diff review then identified the same risk in the CLI. The fix centralizes a no-redirect HTTP client and loopback endpoint policy in Contracts, applies it to authenticated App and CLI calls, and validates the CLI health/browser-link path. The local browser API rejects redirects, Android disables redirects on pinned OkHttp clients and the Media3 video data source reuses that client, and the Python live-chat harness validates its loopback bootstrap and disables urllib redirects. VS Code's Node transport has no automatic redirect behavior and already validates loopback endpoints.

Focused .NET regressions passed 15/15; Android unit suite passed 30/30; CLI Release build passed and its isolated live canary received 302 at the origin with zero redirect-target connections. Evidence and exact commands are in `.ai/artifacts/bic-complete-results-20260927.md` under “Follow-up gate: credential-bearing redirects”. The Release gate then passed; follow-up reviewer fixes and the confirmed-apply FlaUI scenario are now being validated separately. BIC lifecycle statuses remain unchanged pending those gates and the outstanding real-device/MSI/UI acceptance.

#### Full software release gate — 2026-09-27

- `powershell -NoProfile -ExecutionPolicy Bypass -File tools/release-gate.ps1 -Restore` passed (`.ai/artifacts/release-gate-20260927-post-redirect.log`): Server.Tests **3,504/3,504**, Windows x64 solution build and shipped-project publish checks passed, VS Code tests **35/35**, Android clean JVM/lint/debug/release APK gate passed (`BUILD SUCCESSFUL`, 2m34s).
- Gate ran before the latest independent-review fixes to proxy handling, PID check ordering, Git timeout cleanup, and the confirmed UI patch-apply scenario. Rerun targeted and full relevant gates after those corrections; this gate did not run FlaUI.
- The canonical plan still has **14 active BIC rows**; this passing software gate does not close hardware/device/installer/network/Office/parity acceptance. Release remains **NO-GO** pending those items and the follow-up gates.

### Follow-up validation: bootstrap recovery, redirect and patch UI — 2026-09-27

The post-review FlaUI target passed 3/3: dead bootstrap PID recovery starts the packaged server without sending the stale token; a live wrong executable is refused before any identity request; a valid origin returning 302 does not forward the token to the redirect listener. Evidence: `.ai/artifacts/stale-bootstrap-targeted-r3-20260927.log`. The stale-PID case previously appeared to pass without actually seeding bootstrap; the independent reviewer caught that false positive. The corrected fixture seeds `int.MaxValue`, asserts the initial PID, tracks the origin request count, and verifies replacement with a live packaged server PID. Independent security and diff re-reviews both returned PASS after this correction.

A pre-addition full FlaUI Regression passed 55/55 (`.ai/artifacts/fla-ui-review-fixes-final-20260927.log`); the complete suite is now rerunning with the dead-PID case included. The apply UI scenario exercises click, explicit confirmation, and verification of modified text, a new text file and binary bytes in a disposable Git worktree, while confirming HEAD remains unchanged. It still uses a synthetic completed-task fixture rather than a task produced by a real Core/model run.

BIC-12: Excel opened the actual product-writer artifact and displayed the expected text plus formula result `4`; evidence and limitation are recorded in `.ai/artifacts/office-probe-20260927/excel-ui-verification.md`. Final Server.Tests and the comprehensive Release gate must be rerun after this controller/test delta. Overall release status stays NO-GO until those gates and the residual hardware, installer, device, network and quality acceptance items are resolved.

- Final fresh-layout FlaUI Regression passed **56/56, 0 failed/skipped** in 2m33s after the dead-PID fixture correction. Log: `.ai/artifacts/fla-ui-final-corrected-20260927.log`; layout: `artifacts/validation/ui-tests/20260927-101720`. Both independent reviewers returned PASS after the last source/test correction. The suite contains a synthetic confirmed patch apply and real packaged-server recovery from a stale PID, but no Core/model-owned patch task or multi-DPI run.

- Final Release `Server.Tests` passed **3,506/3,506, 0 failed/skipped**, 6m43s (`.ai/artifacts/server-tests-final-20260927.log`). Build/analyzer warnings are present but the suite succeeded.
- Public-repo preflight used official Gitleaks v8.30.1 (SHA-256 recorded in the BIC report/artifacts). It scanned all 1,187 commits; two matches were verified as non-credential cases (signing-prerequisite prose and a synthetic dependency-DAG token). Exact fingerprint allowlist entries are in `.gitleaksignore`; final history scan passed with no leaks, and direct scan of 85 changed/untracked files had zero findings (`.ai/artifacts/publication-audit/`). GitHub read-only metadata confirms PRIVATE/MIT; local `main` is 41 commits ahead of origin. No visibility/remote mutation occurred.
- Run the integrated Release gate next; completion of that gate does not cover MSI EULA/admin lifecycle, paired Android device, NPU/packet-capture boundaries, benchmark parity, real Core-owned task apply, or final action-time public visibility confirmation.

#### Continuation audit — 2026-09-27

The last `.ai/artifacts/release-gate-final-20260927.log` is **INCOMPLETE**: it ends at Core test startup and has no final result. Current branch/HEAD and diff were checked without altering existing work. A fresh Gitleaks direct scan covered the current 85 changed/untracked files with zero findings; unchanged-HEAD history evidence and two narrow false-positive fingerprints were rechecked. GitHub remains PRIVATE/MIT. Independent read-only review of the current apply/redirect/bootstrap hunks found no new issue and confirmed the reported PASS applies to this diff, with the synthetic-task FlaUI limit intact. WiX 6.0.1, non-elevated shell, absent PaRuX signing credentials and zero code-signing EKU certificates with a private key keep the MSI lifecycle/signing gate unrun; the source checkout is also dirty while Release mode requires clean HEAD. Evidence is in `.ai/artifacts/bic-complete-results-20260927.md`, `.ai/artifacts/installer-prerequisites-20260927.md` and `.ai/artifacts/publication-audit/`. Overall status remains **NO-GO**.

BIC-10's already recorded opt-in scale JSONL was reconciled without rerunning unchanged code: initial 1k/10k/50k indexing was 1.98s/19.55s/107.16s, below the acceptance thresholds, and incremental churn passed the restart/full-rebuild equivalence check. This closes the specific synthetic timing evidence gap in the BIC report; the broader active backlog row remains PARTIAL.

The resumed current-checkout release gate is in progress under `.ai/artifacts/release-gate-resumed-nonclean-20260927.log`. Its Core Release suite passed **3,506/3,506** in 6m16s. The gate uses `-SkipAndroidTests` solely to preserve existing Android build outputs against the script's `gradlew clean`; run Android `test lint assembleDebug assembleRelease` separately without `clean` before judging the composite software gate. Windows solution/publish and VS Code results are still pending at this checkpoint.

The same release-gate command then **exited 0**: full x64 solution build had 0 errors/6 warnings, all five shipped win-x64 projects published into a new preserved-output directory, and VS Code tests passed 35/35. Android remains a separate pending non-clean invocation, so the exact canonical all-in-one command has not been claimed as passed.

Android `test lint assembleDebug assembleRelease` without `clean` passed incrementally (103 up-to-date, `lintDebug` executed). An explicit `:app:testDebugUnitTest --rerun-tasks` then executed all 30 Debug unit tests: 30 passed, 0 failed/skipped. A wildcard-filter attempt failed at command parsing before tests and was superseded by this corrected run. Combined with the successful Windows/VS Code gate, this is a **passing composite software validation** with the non-clean variation recorded; the canonical single invocation with Android `clean` was not run because prior output preservation was required. Device/emulator, MSI, network, full Core task adoption and benchmark gates remain open. Evidence: `.ai/artifacts/android-release-nonclean-20260927.log`, `android-unit-rerun-nonclean-20260927.log` and the BIC report.

Fresh current-checkout RTX 5080/4B runtime evidence now exists in `.ai/artifacts/live-current-20260927/`: isolated-root import copied the existing model with both SHA-256 values matching; transient load raised GPU memory 0→3,324 MiB, a local chat returned the requested text in 774 ms, and unload released observed GPU use. A single full Ollama Python SDK 0.6.2 run passed chat/stream/HTTP-400 **3/3**, with the temporary key revoked and absent; this satisfies the source prompt's BIC-05 protocol checks for the tested configuration while broader API/model coverage remains PARTIAL. An exploratory same-PC integrated Backend Race returned 174.55 vs 161.32 decode tok/s and its scorer selected PaRuX, while Ollama had lower TTFT (121.89 vs 161.42 ms). Independent QA **did not accept that selection as a fair winner**: the GGUFs differ in 258 bytes, a current tensor-aware parity proof and effective-runtime/warm-state attestation are absent, and `sameWeightsConfirmed=true` was only asserted in the request. Separate 20-repetition streaming runs descriptively favored Ollama (150.31 vs 137.57 end-to-end tok/s; median TTFT 59.56 vs 69.74 ms), but the harness reported `Comparable=no` for both. These measurements establish neither a fair performance winner nor Codex/Claude quality parity. BIC-02/03 remain PARTIAL and public release **NO-GO**.

#### Real BIC-09 coding task and AppContainer verification blocker — 2026-09-27

A genuine current-checkout Chat `/code` task on a new isolated Git fixture reached Core scheduling, desktop worker claim, worktree creation and a correct model-generated `Calculator.Add` patch. Its first verification command could not start because a Windows ACL grant timed out; the AppContainer circuit then returned `ACL updates are disabled after a previous Windows security update timed out`. The agent repeated the same blocked command more than 40 times before the QA task was cancelled. This exposed a release blocker beyond the synthetic FlaUI apply fixture. A narrow fail-fast correction in `CodingAgentService`, `WindowsCodingSandbox` and `AgentCodingWorkerService` now checkpoints the first sandbox setup failure and marks the task `failed` without entering verifier; normal command exit failures and runtime command timeouts remain repairable. Focused policy tests passed 35/35 and App Release build passed with 0 errors/6 existing warnings. Independent review passed the retry logic and called for a live status propagation check.

The fresh Release UI rerun supplied that check: task `0363e4e6…` produced the correct minimal patch, stopped after one sandbox command failure, reported `failed`/`Coding sandbox unavailable`, and retained its checkpoint/worktree. Host-side `dotnet run` on the retained patch printed `5`, but PaRuX's sandbox verification and UI review/confirm/apply still did **not** run. Do not present BIC-09 as complete or count host verification as an in-app acceptance. Evidence and exact limits are in `.ai/artifacts/bic-complete-results-20260927.md` and `.ai/artifacts/live-current-20260927/`. The next blocker to resolve is the exact ACL grant path and a bounded AppContainer grant strategy; keep the failed task/worktree for diagnosis. Public release remains **NO-GO**.

#### BIC-09 continuation — 2026-09-28

Task `0363e4e6…` subsequently resumed on the preserved worktree: a real `dotnet run --no-restore` inside the no-network AppContainer exited 0 and the independent task verifier passed. Optional ACL mutation now uses bounded killable `icacls`; the existing ancestor `global.json` receives RX only, and a single dotnet command avoids unrelated Java/Node/Android/Cargo tooling roots. The latest narrowing passed the 39/39 focused sandbox policy tests and independent static review; the completed task ran on the prior build, so this exact narrowing still lacks a separate live run.

The first real UI apply attempt revealed that the old client used `/workspace` (404) even though Core's authenticated `/repositories` response contained the valid primary root. The updated UI selects that single primary authority, and new authority tests reject mismatched task IDs and ambiguous/missing primary bindings. Focused authority/apply/sandbox tests passed 46/46 and x64 App Release built with 0 errors/6 warnings. The updated x64 Release binary then displayed the real model-owned task, FlaUI clicked the apply action and primary confirmation, and the app logged success. In the isolated target, HEAD remained unchanged, the only tracked change was the model's `left - right` → `left + right` edit, and `dotnet run` printed 5. The fixture required local ignores for generated build output and an EOL-aligned clean target; those accommodations and all failed/stale-binary attempts are documented in `.ai/artifacts/bic-complete-results-20260927.md`. BIC-09 stays **PARTIAL** pending untracked-output review safety, full native editor acceptance, and measured successful-task speed. The post-change full Server/FlaUI/release gate and final independent authority review are next; public release remains **NO-GO**.

An independent follow-up found a P1 review gap: the confirmation summarized tracked changes, while apply also included untracked files. The UI now enumerates and displays every untracked path before confirmation, and refuses apply if complete enumeration or bounded display fails. A fresh Debug publish and focused FlaUI regression proved both new text and binary paths appear before the primary confirmation, then apply succeeded (1/1). Independent re-review and full post-fix regression remain pending. BIC-09 remains **PARTIAL** for the broader editor and performance acceptance criteria.

#### Final local software regression after BIC-09 fixes — 2026-09-28

The independent authority and untracked-review re-review returned **PASS** with no additional actionable finding. Full FlaUI Regression on a fresh Debug publish passed **56/56** after the new-file preview correction. The integrated Windows release gate, invoked with `-Restore -SkipAndroidTests` to avoid Android `clean`, exited 0: full Server.Tests **3,513/3,513**, full x64 build, five fresh published Windows outputs, VS Code **35/35**, release-asset/static checks passed. Android non-clean `test lint assembleDebug assembleRelease` passed, then a forced JVM test rerun passed **30/30**. Exact commands and logs are in `.ai/artifacts/bic-complete-results-20260927.md`.

This is a passing composite local software gate, not public-release GO. The gate itself reports 14 active BIC rows and required physical-PC acceptance. MSI install/update/sign remains unrun: WiX 6.0.1, no explicit WiX 7 EULA consent, no elevated shell and no PaRuX signing key or Code Signing EKU private-key certificate. Real GPU/text/image/ONNX and Office smoke evidence is retained, but no matched Codex/Claude task-quality benchmark or fair same-weights speed winner exists. No model is certified as recommended for this PC or superior to all competitors. GitHub remains PRIVATE. BIC-09 still needs measured successful-task latency, complete native editor review/partial acceptance, and generic generated-output handling. Public release remains **NO-GO** pending those acceptance criteria and the remaining BIC rows.

#### Final audit and BIC-09 live hardening — 2026-09-28 (evening)

An independent review of the whole uncommitted diff returned PASS with no P0/P1. Two of its P2 findings were fixed with regressions: Release builds no longer compile the UI-test fixtures, and single-repository apply now verifies the exact patch with `apply --reverse --check`. The Node/npm roots withheld from single `dotnet` sandbox commands remain an accepted least-privilege limitation. Five live Chat `/code` runs on a fresh fixture confirmed the narrowed AppContainer ACL grants (every sandboxed `dotnet` command exited 0, with no access errors). They also exposed, and led to fixes for:
- a **false Verified**: a paused attempt with unverified changes was promoted by the model verifier; it now goes to Failed, "Coding attempt paused without verification";
- a user-answer verification waiver reapplied on every resume; it is now consumed once;
- an unbounded done loop, now paused after 3 unverified completions;
- rejection text that did not name the accepted commands (and whose pipe-joined form the model copied as a shell pipe);
- about 105 s of optional-`icacls` overhead per command, now cached after the first failure.

Each fix passed an independent review. The final live run `18b8aaf1…` was Verified after an accepted `dotnet build --no-restore` in 13 steps (~2.3 min). A real UI apply listed the untracked file, left HEAD unchanged and printed `5`. The earlier `0363e4e6…` Verified used the waiver and is only apply-flow evidence.

Final gates: release gate exit 0, Server.Tests 3,527/3,527, VS Code 35/35, and FlaUI 56/56. Gitleaks found 0 findings across the changed files. BIC-09 stays PARTIAL. Open items are the sandbox NuGet artifact being applied, the apply button staying enabled after apply, the stale progress status, the 8192 context, and repeat-task success rate. Public release remains **NO-GO**; see `.ai/artifacts/bic-complete-results-20260927.md`.

#### Measured coding-model selection through Core — 2026-09-29

`/code` previously used only the manually assigned `coding` role model. Core now selects the coding model from measured local outcomes on this hardware:
- Utility = 0.50 × Wilson lower bound of the verified-task rate + 0.30 × reliability + 0.20 × speed (verified runs only).
- Hardware fit is a gate, and each model needs at least 3 attempts.
- A model more than 0.10 below the best quality cannot win on speed.

Evidence is derived by Core from task state:
- Verified = the verifier passed and an accepted deterministic verification command succeeded.
- Excluded: verifier errors, sandbox failures, cancellations, and mixed or concurrent attributions.

The winner is loaded through the exact-candidate admission path, and the assignment is used if it fails to load. The choice is visible in the task log and in a Model Routing panel. An auto/assigned API is available.

The live benchmark on the RTX 5080 exposed four product defects, all fixed:
- MSBuild out-of-process pipes hung inside the AppContainer; builds now run in-process with `-m:1`.
- Verification equivalence was too strict, and a successful command could be repeated without bound.
- The ACL circuit breaker never reset.
- Role reuse credited the wrong model.

Final round: 27B 3/3 and 4B 3/3 verified. Core chose **Qwen3.8-27B** (utility 0.690) over the faster but less reliably verified 4B (0.555). The auto task ran on the 27B and verified.

Gates: Server.Tests 3,579/3,579, FlaUI 57/57, VS Code 35/35, independent review PASS.

Open:
- The 27B's roughly 16 GB system-RAM footprint while fully GPU-offloaded, and fit accounting that ignores it.
- The 8192 context is still too small for the coding protocol.
- Few fixtures, and no exploration after a winner is chosen.
- Edits made through `run_command` are not detected as mutations.

Public release remains **NO-GO**. See `.ai/artifacts/bic-complete-results-20260927.md`.

#### Hardware fit, context and sandbox containment — 2026-09-29 (continued)

Live measurements on the RTX 5080 PC drove four more fixes, each with regression tests and an independent review (final **PASS**):

1. **`--load-mode none` when the weights fit the GPU.** The mmap load kept about 12 GB of the 27B's file pages in RAM; with `none`, free RAM went from 0.8 to 13.2 GB, loading was 2× faster and speed was unchanged.
2. **Hybrid-aware KV estimate.** The estimator now uses `full_attention_interval` (the 27B has 17 of 65 layers with KV cache). The planner uses VRAM minus the `--fit` target. The 27B coding context goes from 8,192 to 16,384 (verified live), the noMTP file stays at 8,192, and the 4B gets 32,768.
3. **Shell-made code edits are detected.** A host fingerprint around each `run_command` treats them as mutations.
4. **P0-class sandbox containment fix, pre-existing since 1.9.2.**
   - The AppContainer had Modify rights on the user's repository `.git`.
   - Host Git trusted the sandbox-writable worktree link, so planted `core.fsmonitor`, filters or hooks could run outside the sandbox, and grants could be steered to other folders.
   - New `AgentWorktreeGitTrust` accepts only Git's exact worktree shape.
   - Git metadata grants are now read-only, fail-closed, and strip stale AppContainer write ACEs.
   - All 10 host Git launch sites refuse untrusted agent worktrees and set `core.fsmonitor=false`.

The final benchmark with these binaries: Core selected **Qwen3.8-27B** automatically (18/18 verified, utility 0.866) over Qwen3.5-4B (10/13, 0.679). The 4B is about 23% faster but less reliably verified.

Open: stale AppContainer ACEs on repositories not used again; the n_ctx probe on the first task after a model unload; few fixtures. Public release remains **NO-GO**.

#### Parity suite, sandbox toolchains and Chat-first coding — 2026-09-29 (late)

Status: **PARTIAL**. Details and evidence are in `.ai/artifacts/bic-complete-results-20260927.md`.

- **Performance.** Pilot tasks run at 124–179 s (the target was 2–3 min).
  - First-command sandbox setup is about 1 s; App restarts skip existing ACEs (`HasEquivalentAllowRule`).
  - Indexing takes 21–25 s.
  - Scoped verification is hardened (independent review **PASS**).
- **Held-out suite** (30 tasks, mid-day binaries): **25/30 (83.3%)**, all mandatory cases passed, median 163 s. The two PaRuX defects behind failures are fixed.
- **Sandbox toolchains:**
  - per-user CPython grants;
  - Node ESM (`--preserve-symlinks`);
  - `node --test` (`--test-isolation=none`);
  - npm scripts run directly, because npm cannot start in an AppContainer;
  - no current-folder program lookup (`NoDefaultCurrentDirectoryInExePath`).
- **Chat is the coding surface.**
  - A change request aimed at a repository path or source file routes to the Coding Agent.
  - Plain chat may no longer claim it ran anything.
  - The card shows live step stages.
  - Verified live: completed and verified from Chat in 179 s.
- **Next actionable (in order):**
  1. Rerun the full suite on the final binaries.
  2. Measure Claude Code on the same corpus for parity (it uses the user's Claude quota).
  3. Reuse indexes across worktrees of the same commit (about 25 s per task).
  4. Stop logging the pre-swap "context too small" line.
  5. Release evidence: signing, MSI, physical Android, network capture.
- **Update (late):**
  - Context Graph queries are now repository-scoped, and old worktree indexes are pruned. A 3-file repo indexes in 1.7 s instead of 35 s.
  - Timing for the same task from Chat: PaRuX **62 s** vs Claude Code 24 s, at equal quality.
  - Final binaries: suite **25/30**, median 118.6 s. After the scope/verifier fixes, 3 of the 5 failures pass on rerun.
  - Release gate 3,691/3,691; FlaUI 57/57; Gitleaks 0.
  - Remaining quality gaps: path-ambiguous goals, verifier selection on multi-verifier fixtures, and about 13 s of model swap on the first coding request.
- **Residency/cancel (2026-09-30):**
  - Consecutive coding tasks keep the 27B resident (58 s → 54 s, no reload). The model is swapped only when the request type changes (chat 4B ↔ coding 27B).
  - Fixed: cancelled tasks could be revived by late worker stage updates and resumed after restart.
- **Decision (user, 2026-09-30):** keep role-specific residency. Chat stays on the fast chat model even when the coding model is resident; a ~10–13 s swap when alternating chat and coding is acceptable. Do not add "sticky resident for chat".

### 2026-09-30 live acceptance — Lifecycle Lab Eval→SFT

On the isolated data root, Lifecycle Lab inspected a three-row CSV, explicitly mapped prompt/expected, created `functional-eval-20260930`, and imported all three cases. The real run against the loaded local Qwen3.8-27B Uncensored model completed 3/3 with actual outputs `4`, `Rome`, `A`. Eval→SFT produced a valid three-row `messages[]` JSONL and provenance metadata with the source dataset and all case IDs. Detailed IDs, hashes, and screenshots: `.ai/artifacts/functional-20260930/lifecycle-lab.md`. No product defect found.

### 2026-09-30 live acceptance — Model Routing

Live Model Intelligence testing found that “Force selected model” only resolved/loaded a candidate immediately; the next Chat request still followed Auto and reloaded the role profile model. The fix queues the exact local model once and carries it in the next eligible attachment-free interactive text turn. Runtime status remained on Qwen3.8-27B after queuing, the next arithmetic Chat response used Qwen3.5-4B (714), the following Auto response returned to Qwen3.8-27B, and “Set as role default” persisted Qwen3.5-4B for the uncensored role and the next Chat response. Regression tests: 19/19; Release x64 App build and Server publish passed; independent review PASS. Live details and screenshots: `.ai/artifacts/functional-20260930/model-routing.md`.

### 2026-09-30 live functional acceptance — remaining app surfaces

Completed the requested live checks on the isolated data root. Lifecycle Lab created an evaluation set, ran it against the local model (3/3), and emitted a three-row Eval→SFT JSONL. Model Routing’s one-shot forced model and role default both changed the next Chat route, confirmed through runtime status and response labels. Tools & Automations exposed the MCP tool while enabled and hid/denied it after disabling the server or permission. Projects created a goal-bound project task, bound it to the disposable QA repository, produced `src/slug.js`, and passed repository verification/tests (3/3). Create produced a valid WAN2.2 video and a Flux Klein edited image. Updates “Check” only returned HTTP 404 from the private unauthenticated update source; no installation or rollback was attempted. Runtime optimization completed; Race correctly reported insufficient managed runtimes (one installed build), with no download. Computer Use displayed the permission request for “Apri Blocco note”; it was left pending, so no app was launched.

Gaming Mode exposed a provider-detection bug: Nyrna is registered as a Microsoft Store package, while Sunshine is installed outside PATH, so executable-only discovery falsely reported both absent. Detection now reports Nyrna and Sunshine installed/available; XOutput remains absent and an XOutput-only profile fails before transition. The Local Console preset no longer selects unavailable XOutput. Using the isolated “no launcher” profile, live UI/API evidence confirmed Gaming entry and return to a healthy local AI runtime, followed by a successful chat; Playnite and Steam were not started. Regression tests passed 12/12. Evidence: `.ai/artifacts/functional-20260930/`.

The canonical BIC ledger has 17 rows, 14 PARTIAL. Both release validators had stale BIC-16 bounds; they now validate BIC-01 through BIC-17, with a regression contract. The verdict for this requested acceptance campaign remains **NO-GO**. Its outstanding evidence is signing, MSI installation on a clean PC, physical-phone validation (open BIC-14 coverage), and network capture. This records the campaign's requested evidence threshold; it does not change the repository's general Android software-completeness gate policy above.

Final gates on 2026-09-30: `tools/release-gate.ps1 -Restore` passed the Server suite (3,829/3,829), then stopped at the Windows x64 App build because the current working copy of `src/PaRuX.App/Views/SettingsView.xaml` is missing controls referenced by its code-behind; Android and publish stages were not reached. The corrected slow-reply TTFT regression, including its 3,500 ms end-to-end ceiling, passed targeted verification (1/1). The normal `tools/test-ui.ps1 -Suite Regression` app build was blocked by the same local view state. With the isolated Debug QA app and its Server payload supplied through `-AppPath`, UI Regression passed 58/58. The first QA-layout run exposed a missing sibling Server payload (57/58); after adding it only to the isolated QA directory, the complete rerun passed. The local SettingsView change was preserved and not staged.

### 2026-09-30 HR Endless Sampler and video-performance check

Downloaded upstream `ComfyUI-HR-Endless-Sampler` commit `1681f89` and installed it only in an isolated ComfyUI 0.37.0 copy. ComfyUI registered all four nodes, and its upstream suite passed 101 tests with one opt-in real-model test skipped under UTF-8 mode; one fixture test fails under default Windows CP1252 because it decodes UTF-8 without specifying an encoding. The isolated `llama-cpp-python==0.3.35` CUDA wheel did not load against this machine's Python 3.13/Torch `cu130` runtime because the required CUDA 12 native DLL dependency is absent. The real portable ComfyUI and PaRuX configuration were not changed.

HR Endless Sampler currently supports MiniMax H3, while PaRuX's video path is Wan2.2 with a fixed workflow. It chunks H3 latents to reduce temporal VRAM and enable long renders, and uses Gemma 4 to plan/rewrite prompts; this is not evidence of a Wan speedup. It is not offered in PaRuX because the current workflow/model do not support it. The official MiniMax H3 Community License explicitly excludes the EU and says using/running H3 Works accepts its terms. No H3 or Gemma model weights were downloaded and no H3 inference was run, consistent with the no-EULA constraint. A separate written authorization applicable to EU use would be required before that test.

Real Wan2.2 API runs on the RTX 5080 at default Create settings (1280x720, 5 seconds, 24 fps, 30 steps) produced two distinct valid H.264 MP4s, each 120 frames. Startup-inclusive render: 305.84 s; warm render: 280.67 s (8.2% lower). Sampled GPU utilization reached 100%; sampled peak VRAM was 15,896/16,303 MiB (97.5%), leaving only 407 MiB observed headroom. Three unique-seed 256x256/1s/20-step warm runs took 5.02–5.03 s, but that smoke setting is not representative. A same-seed pair returned byte-identical files and was excluded as a likely cache hit. No matched competitor benchmark was run; PaRuX is GPU-bound at default video settings and current evidence does not substantiate `best in class`. Detailed artifacts: `.ai/artifacts/functional-20260930/hr-endless-performance-assessment.md`.

### 2026-09-30 functional continuation — Computer Use and managed runtime race

Verified starting branch `parux/coding-agent-parity-20260930` and HEAD `13c006f`. The pre-existing `SettingsView.xaml` change remains untouched (SHA-256 `652C16411BEB22CB8A5ACAC23E8A19580632738F987566B9F604915D7D64F583`). The isolated QA App was used because the root working-copy view still removes controls required by its code-behind; this is not a passing root App build.

Computer Use now has complete live evidence: the new Chat request “Apri Blocco note” displayed “Allow once · Open Notepad”; selecting that scoped consent opened real Windows Notepad, and PaRuX displayed “Notepad opened and its process was verified.” Full Computer Use remained disabled. Screenshots: `.ai/artifacts/functional-20260930/continuation-live/computer-use-{request,notepad,chat-result}.png`.

The real runtime acceptance found and corrected three related installation/discovery defects. The first CUDA archive selected from official b10549 metadata was a digest-valid `cudart-*` dependency bundle containing only three DLLs, no server. The actual server archive needs those separately shipped DLLs. The catalog also inspected only 15 releases, all currently prereleases, hiding older usable stable builds. Selection now distinguishes server and exactly matching platform/CUDA dependency archives; installation validates both metadata, release-tag paths, advertised sizes and SHA-256 digests before safe extraction, co-locates dependencies with nested server executables, fails closed on advertised version mismatches, and reports combined download progress. Stable discovery is bounded to eight 100-release pages and 15 usable options; prerelease and integrity rules remain intact.

Targeted catalog/install/integrity tests passed **50/50**; independent review **PASS**. A real b10545 install inside `.ai/artifacts/functional-20260930/continuation-runtime/` completed its GPU serving regression gate. The corrected live catalog exposed 15 stable builds. “Race installed runtimes” was then run from the UI on the isolated b10545/b10549 pair and Qwen3.5-4B: both candidates completed real serving probes, and b10545 was retained because the challenger did not clear the 3% composite threshold. Decode was 171.86/175.01 tok/s; TTFT 119.85/120.45 ms. These are this race's samples, not a controlled competitor or universal performance claim. Runtime config persisted the winner. Evidence: `continuation-live/runtime-catalog-evidence.json`, `runtime-install-and-catalog-tests.log`, `runtime-available-fixed.json`, `runtime-install-status.json`, `runtime-race-status.json`, and `runtime-race-ui-completed.png` under `.ai/artifacts/functional-20260930/`.

The default video UI flow also completed (job `b860ecdd338c4b74b59df96bb46f927a`): valid 1280x720 H.264 MP4, 120 frames / 5 seconds / 24 fps / 30 steps, 378.11 s startup-inclusive. UI output and Core download SHA-256 match. However, the temporal contact sheet exposed independent-clip cuts around 49/24 and 98/24 seconds; a successful file render does not close continuity/quality acceptance. This source defect is being addressed through the installed Wan TI2V workflow, without H3 or additional model downloads. Evidence: `continuation-live/video-default-evidence.json`, `video-ui-file-match.json`, `video-default-completed.png`, and `video-temporal-contact.png`.

Updates Check still honestly reports HTTP 404 from the private unauthenticated release source; no update or rollback was attempted. Publication remains **NO-GO**, with 14 PARTIAL BIC rows, root App build acceptance, video continuity validation and external release evidence still open. A fresh read-only prerequisites check found no elevated shell and no connected Android device; four valid self-issued code-signing certificates exist, but none establishes an authorized/trusted PaRuX release-signing identity or signed release envelope. Signing, clean-PC MSI acceptance, physical-phone validation and network capture remain unverified. Final campaign gates will be recorded after the remaining implementation/live checks.

### 2026-09-30 functional continuation — verified Wan UI continuity

The default Wan Create UI render reproduced an actual quality defect: a five-second request concatenated independently prompted/seeded clips, with abrupt visual scene cuts at frames 49 and 98. The runner now uses one continuous prompt, conditions each continuation on the previous clip's last decoded frame, removes repeated anchor frames, and trims to exactly the requested frame count. Clip reads/cleanup validate canonical output ownership and process cancellation uses bounded teardown. Focused ComfyProgressTests pass 15/15; independent diff review PASS.

Real final-source UI job `07876aa8ed3448bda58f17faa6e578f7` completed in 331.62 seconds at 1280x720, 5 seconds, 24 fps, 30 steps. ffprobe confirms H.264, 120 frames, 5.000 seconds; the UI file and returned asset have matching SHA256 `AE27C8D982ECA1F83AA9338714ECFEAB8B4D862116516B4C320D9506352A6A91`. Contact-sheet inspection preserves the red boat/pond scene across clip boundaries. Adjacent-frame YAVG differences at the two joins are 6.37 and 7.67, versus 73.66 and 94.84 before. This is continuity evidence on two differently seeded outputs, not a controlled speed/quality benchmark or a competitive best-in-class claim. Evidence: `.ai/artifacts/functional-20260930/continuation-live/video-after-{parameters,completed,contact}.png`, `video-after-ui.mp4`, `video-after-job.json`, `video-after-ffprobe.json`, `video-after-file-match.json`, `video-after-frame-differences.txt`, `media-continuity-tests.log`, and prior `video-default-ui.mp4`/`video-before-frame-differences.txt`.

The refreshed BIC audit retains 17 rows, 3 DONE and 14 PARTIAL: this proves the requested default UI video slice, not all BIC-13 media/training acceptance. The additional Create image-edit trial exposed that the desktop Edit image button selects img2img rather than the configured Flux instruction-edit workflow; a distinct mode correction and live verification are in progress. Root UI regression was attempted and stopped at the preserved SettingsView.xaml named-control build errors; it was not passed. No EULA, new restricted weights, push or publication occurred.

### 2026-09-30 functional continuation — real Flux instruction-edit UI path

A fresh Create UI trial exposed a distinct routing bug: Edit image always sent `type=img2img` to the strength-blended image-variation graph, so a request to change the red bicycle frame to blue changed only its rack. Create now defaults to Instruction edit (`type=edit`, configured Flux Klein instruction workflow), retains Image variation (`type=img2img`) as an explicit alternative, and exposes Variation strength only for that alternative. Button text and descriptions follow the selected mode; stable AutomationIds enable behavioral FlaUI coverage.

An isolated QA App was published successfully from committed source plus the current CreateWorkspaceView.cs (identical source SHA256 recorded in `continuation-live/qa-source-binary-provenance.json`), paired with the final current Server. The protected root SettingsView.xaml was neither changed nor included in the snapshot's override. Real native UI job `0528ab4598424b54929308f215b4a4a7` completed as `edit` in 39.45 seconds with a 1024x1024 PNG: the bicycle frame/fenders/rims changed from red to blue, while rack, wall and composition remained. UI output and Core asset SHA256 match `F1C249FD49BA176DE4AAC1F916F406D609044D579C2D4F8863D0E8416EE92C3B`. Evidence: `continuation-live/flux-instruction-{parameters,completed,result}.png`, `flux-instruction-job.json`, `flux-instruction-file-match.json`; pre-fix comparison is `image-edit-blue.png` and `image-edit-blue-job.json`, not the final result.

Focused FlaUI mode-selection/strength-visibility regression passed 1/1 after fixing the test to scroll the offscreen strength control into view (initial 1/1 failure was preserved). Test project static build passed with zero warnings/errors; independent review PASS. Logs: `create-ui-test-build.log`, `create-ui-mode-focused.log`, `create-ui-mode-focused-retry.log`, `create-qa-app-publish-final.log`. The full QA UI regression and final release gate are running separately; no root gate or broader media quality claim is inferred from this focused result. The earlier complete Server run passed 3849/3849 before stopping at the protected root App build failure.

### 2026-09-30 functional continuation — final gates and NO-GO closeout

Verified fixes are committed as `9a93076` (managed CUDA package/catalog), `799676c` (Wan anchored clip continuity), and `158719b` (Create instruction edit versus image variation). All received independent review PASS and scoped live evidence; none changes the preexisting SettingsView.xaml. The requested Lifecycle/Routing/MCP/Projects/Gaming evidence already verified earlier was retained rather than misattributed or unnecessarily repeated; new Computer Use permission-to-Notepad, managed-runtime install/race/persistence, default Wan UI video and actual Flux instruction-edit UI evidence are recorded in the continuation report.

Final mandatory `tools/release-gate.ps1 -Restore`: Server **3849/3849 PASS**, zero skipped, 13m49s, then **FAIL** at full Windows solution build (46 errors caused by the missing named controls in the protected local SettingsView.xaml, with XAML cascades). Remaining downstream gate stages were not reached. Final mandatory `tools/test-ui.ps1 -Suite Regression`: stopped at the same root App build failure. Explicit isolated QA diagnostic `tools/test-ui.ps1 -Suite Regression -AppPath <continuation-qa-app/PaRuX.exe>`: **59/59 PASS**, zero skipped, 3m59s, including the new mode-switch regression; its App source and current Server binary hashes are recorded. This isolated pass does not make the working-tree release/UI gate pass. Final logs: `.ai/artifacts/functional-20260930/continuation-live/release-gate-closeout.log`, `ui-regression-root-closeout.log`, `ui-regression-qa-closeout.log`, and `qa-source-binary-provenance.json`. Gitleaks scans of changes/staged patches and all history use v8.30.1 with redaction; final results and Git checks are recorded in `final-gates-and-state.json`/`gitleaks-*-final.log` after this documentation commit.

Final safeguards: seven real LocalAI config files match their backups byte for byte and by SHA256 (`user-config-byte-verification-final.json`); the temporary localai-paths setting was moved out of the isolated data state, restoring its initial implicit-root behavior; owned stack stopped, bootstrap token replaced by REDACTED-expired; protected SettingsView.xaml SHA256 remains `652C16411BEB22CB8A5ACAC23E8A19580632738F987566B9F604915D7D64F583`, unstaged. No push/publication, launcher start, EULA, restricted model acquisition or real-user configuration change occurred.

Verdict remains **NO-GO**, with **17 BIC rows, 3 DONE and 14 PARTIAL**. External gaps include authorized/trusted release signing and signed envelope, clean-PC MSI, physical Android phone and network capture (no connected phone or elevated capture environment was established). Remaining software/evidence criteria include the fair competitor/task corpus, cross-vendor memory/ONNX, full update feed/upgrade behavior, editor/retrieval/tokenizer/window/workbook completeness, broader media/training/voice quality, paired-client permissions/STOP, concurrent recovery/listener safeguards and full BIC-16 promotion/rollback lifecycle. No best-in-class assertion or HR Endless acceleration of Wan is supported. Detailed Italian handoff and evidence index: `.ai/artifacts/functional-20260930/continuation-live/functional-report-it.md`.
