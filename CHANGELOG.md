# Changelog

All notable changes to RBG PaRuX AI Studio are recorded here. The canonical engineering record is
[`PARUX_EXECUTION_PLAN.md`](PARUX_EXECUTION_PLAN.md); this file is the user-facing summary.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased] — UI/UX refinement (2026-10-30)

### Changed

- Model Hub and manual Chat model selection use concise labels, with detailed descriptions and quantization choices separated into dialogs (earlier changes on main).
- Model Routing focuses on role/policy selection; candidate management, capability modes, Arena and read-only backend comparison are now optional expanded sections. The registered candidate list shows compact speed/fit summaries, with quantization and supporting evidence in a dedicated Details dialog.
- Tools & Automations keeps skill and server listings in view while MCP, automation and workflow creation forms, including advanced JSON, start collapsed.
- Media Gallery has a compact two-row search/filter toolbar and a Details dialog for the complete prompt, metadata and local path.
- Runtime suggestions retain their supporting evidence in a Details dialog; advanced context tuning is collapsed by default.
- Observability displays concise metric and benchmark summaries with expandable evidence.
- Update Manager shows installed model names first and file/quantization/source only when expanded. Projects keeps inventory and artifact history available in collapsible sections.
- Settings adapts to narrow windows with a compact category dropdown, refreshes only the visible category, and offers search across existing setting labels with keyboard-aware category suggestions and empty-result feedback.
- Create's media model selector keeps the exact selected file for routing while shortening the displayed name, adding a full-identity details dialog, and moving the global content lane under optional preferences. The job-cancel action is visible only during an active operation.
- First-run onboarding uses a shorter introduction and collapses bundled-runtime and storage configuration details, without removing the manual component, model or local chat verification actions.
- Cross-PC compatibility: CPU-only systems with known system RAM now get a bounded CPU/RAM fit verdict instead of automatically showing Unknown; missing accelerator data is not converted to imaginary VRAM. The conservative model-fit RAM reserve now scales on 4–16 GiB hosts, consistently in the desktop estimator and Core profile resolver.
- GGUF fit previews with an explicit context but no parsed model header use a labelled size heuristic instead of attempting to read absent geometry. Model Hub's quantization picker no longer imposes a fixed 330px minimum width, and Gallery cards stack on narrow or high-DPI screens.
- Desktop hardware status now reconciles NVIDIA telemetry with Core-selected AMD/Intel/NVIDIA backend eligibility, keeps integrated shared RAM distinct from dedicated VRAM, and rejects invented GPU fit when Core cannot verify the backend. Native GPU and Core probes are bounded and overlap; failures have a short retry backoff for low-end/offline PCs.

### Validation status

- Added source-level tests for CPU-only, AMD/Intel UMA, small-memory and missing-metadata fit paths; they have **not been run**. The hardware-diversity matrix is documented in `docs/validation/PHYSICAL_PC_ACCEPTANCE.md`.


- Source-level regression contracts added for progressive disclosure, searchable/adaptive Settings, onboarding, contextual cancellation and retained model actions. They have **not been executed** in this environment.
- Windows WinUI build, FlaUI interaction/regression tests and real-device acceptance have **not been executed** for these UX changes. This entry is not a new release or a performance claim.

## [2.0.0] — 2026-10-28

Local media creation, Model Hub media area, automatic per-model optimization and speed work. Everything below ran on one machine (Windows 11, RTX 5080 16 GB, 27 GB RAM); other hardware is covered by automated tests only.

### Added

- **Media on a clean install, from the app:** image, video, image edit, music (ACE-Step), speech, image-to-3D and background removal each set up and run through Create and through Chat in English and Italian.
- **Model Hub media area:** engines and models for image, video, music and more, split into Standard and Uncensored groups, discovered from Hugging Face, with a "best for this PC" proposal and automatic routing by lane (Standard / Uncensored / Automatic). Image families include Z-Image, Qwen-Image (+Edit), SDXL checkpoints, FLUX.1 schnell and Chroma; video includes LTX-Video 2B.
- **Automatic optimization after every download** (text and media): hardware-aware search (GPU offload, flash attention, KV cache, batch, threads, context, media precision and memory flags). When nothing is faster the result says "Already optimal for this PC" and lists concrete next steps; it never ends in a dead end.
- **Create:** mode tabs, size presets, Music tab, mask inpainting (paint/erase/invert) and a stack of up to three LoRAs with a family-compatibility check.
- **Chat:** hands-free voice mode (listen, transcribe, answer, read aloud), media from chat, character card import (SillyTavern/Chub JSON or PNG) as personas, guarded Delete for managed models.

### Changed

- Faster text/media switching: ComfyUI prewarm, a cheaper cold start (bytecode cache), and a CUDA media assistant. Measured here: first image after text work 122 s → 20.9 s with prewarm; chat during a media job about 175 tok/s; resident 27B back at 60 tok/s after restore.
- Downloaded media checkpoints are placed into ComfyUI folders by the server, with a non-destructive migration for files that landed in the text-model folder.

### Fixed

- Automatic maintenance no longer fails when the media runtime is installed (13 consecutive failures before).
- Image jobs no longer fail with a chat model resident (RAM admission and untrustworthy slot state).
- Inpainting uses the user's own prompt; CivitAI 401 and region-edit notices are specific and localized; several Create/Hub regressions found by the UI suite.

### Known limitations

- Microphone: transcription of real speech was never proven on the test PC (its microphone only produced steady noise); voice mode is covered by unit tests and is unproven live. Barge-in is press-only.
- Not implemented: character LoRA training, Wan 2.2 14B, HunyuanVideo, CogVideoX, Qwen-Image-Edit inpainting. Inpainting runs on Z-Image at about 1 MP.
- Hardware other than the NVIDIA test PC (AMD/Intel Vulkan, CPU only, low VRAM) is exercised by automated tests only; Chroma took about 15 minutes for its first image on the test PC.
- Release gate still reports 12 active BIC rows and PHYSICAL_PC_ACCEPTANCE_REQUIRED. This release is published by the maintainer's decision, not because those items are closed.

## [1.9.6] — candidate notes (folded into 2.0.0)

Publication remains subject to the canonical BIC backlog and physical Windows acceptance; a successful software gate alone does not make this a released version.

### Added

- Model Hub and onboarding can advise on installed local models and assign models to the eight system roles.
- The local Ollama-compatible API exposes running models and embedding endpoints.
- Mobile pairing supports credential expiry, device revocation and a panic action.

### Fixed

- AppContainer tooling initialization and symlink discovery, with isolated sandbox test roots.
- Tokenizer identity selection for concurrent local model pools.
- Native workbook evaluation for additional arithmetic, ranges and math functions.
- IPv4/IPv6 listener ownership checks and database-scoped SQLite pool isolation.

### Changed

- Prepared the sanitized public source tree: removed obsolete internal agent prompts, broken archive references and historical release links; clarified security support for the latest stable release and current development branch.
- Aligned Windows, MSI and Android candidate versions to 1.9.6 (Android version code 16); 1.9.5 was never published and remains the preserved previous-version upgrade baseline.

### Validation

- Latest local release gate (2026-10-23) passed: Core Server tests **8,083 passed / 6 skipped / 0 failed**; Release x64 build and all five shipped Windows publishes passed; VS Code **58/58**; Android JVM tests, lint, Debug APK and unsigned Release APK build passed. Full log: `.ai/artifacts/release-gate-20261023.log`. The gate still reports the 12 open BIC items and required physical Windows acceptance below.
- Earlier full local release gate passed without diagnostic skips: Core 3,877/3,877, VS Code 35/35, Android JVM 33/33 Debug and 33/33 Release; Android lint had zero errors and 31 warnings. Release x64 build and all five self-contained Windows publishes passed.
- Fixed shared model/data-root leakage in the auto-role test fixture; related regressions and independent diff review passed. NuGet dependency audit and current-tree/sanitized-history secret scans passed.
- These software checks do not close the 12 open BIC items or required physical Windows acceptance. No stable release is declared by this entry.

Historical entries below describe past development and distributions. Their former tags, releases and commit history were intentionally removed during public-history cleanup and are not part of the new sanitized release channel.

## [1.9.4] — 2026-09-23

### Added

- A clear control beside the Chat code-folder selector, with matching Simple-mode state cleanup.
- More conversational code, bug, benchmark and test questions routed to normal chat unless the user requests a coding task.

### Changed

- Coding-agent patch application now tolerates line-ending, trailing-whitespace and indentation differences while refusing ambiguous matches and returning actionable nearby source on genuine mismatches.
- Runtime context sizing, transient model loads and resident hot-pool routing are more predictable for coding tasks.

### Fixed

- Windows AppContainer ACL updates are serialized and bounded, grants are scoped to the active sandbox identity, and timed-out native/process ACL writes fail closed instead of accumulating blocked workers.
- Sandbox tests isolate PaRuX state per test and retain cleanup roots when ACL/profile revocation cannot be confirmed.

### Validation

- Release x64 build and self-contained Windows publishes passed; Core tests passed 3089/3089 and VS Code tests passed 28/28. Android tests were skipped by the requested release-gate option.
- This release-gate pass does not close the active BIC backlog or the remaining physical-PC acceptance work.

## [1.9.3] — 2026-09-20

### Added

- Workload orchestration and Windows desktop UI-automation foundations for the local validation workflow.

### Changed

- Aligned the Android companion version with the Windows `1.9.3` product release.

### Known limitations

- The published Windows MSI is unsigned and is intended for manual installation; the in-app update envelope is unavailable until the maintainer configures the private release-manifest key.
- The Android companion is distributed as a debug-signed APK while a maintainer Android release keystore is unavailable.

## [1.9.2] — 2026-09-19

### Added

- **Dynamic prompt budget & context window management**:
  - `CodingAgentPromptBudgetPolicy` dynamically calculates character limits for model prompts based on the loaded LLM's active context window.
  - Prevents prompt overflow and context exhaustion during long-running multi-turn autonomous coding tasks.
  - Safe observation truncation and artifact paging (`read_artifact`, `search_artifact`, `list_artifacts`) preserve evidence without overwhelming model context.
- **Repeat guard & loop breaking**:
  - `CodingAgentRepeatGuard` detects and prevents duplicate tool invocations with identical parameters.
  - Automatically guides the model to alternate exploration strategies (such as artifact searching and line-range inspection) instead of getting stuck in loops.
- **Enhanced Windows AppContainer sandbox**:
  - `WindowsCodingSandbox` now configures security ACLs and capability mappings for desktop worker isolation, allowing secure local command execution.
- **Structured Chat Intent & Role Routing**:
  - `StructuredChatRoleRoute` improves intent classification and confidence evaluation when delegating tasks between general chat, autonomous coding, and specialized orchestrator agents.
- **Local AI Process Management & Hardware Tuning**:
  - `LocalAiProcessManager` and `RuntimePerformanceTuner` provide automatic crash recovery, thread pool sizing, and hardware topology-aware optimizations for local model servers.

### Fixed

- **Long tool output truncation handling**: Fixed issue where truncated tool outputs could cause repeated tool calls by introducing line-range reading (`read_file_lines`) and artifact search.
- **Agent delegation commit coordinator**: Resolved race conditions in multi-repository worktree state tracking and checkpoint commits.

## [1.9.1] — 2026-09-17

### Added

- **Codex-style autonomous coding mode directly in Chat**:
  - In Simple mode, users can pick a target repository or local folder directly from the chat composer, describe desired features or fixes in natural language, and let PaRuX autonomously research, plan, modify, and test the project in an isolated workspace.
  - Native in-chat progress cards show real-time stage badges, streaming execution logs (last lines visible with full expandable trace), and inline user clarifications without switching views.
  - Diff review card provides file-by-file inspection with syntax-highlighted diffs and a one-click **Apply changes to project** action with safety drift checks.
  - Natural language intent routing for testing and bug-fixing prompts (e.g. "Fai i test su un app, correggi eventuali bug...") directly invokes the autonomous coding handoff pipeline.
  - Automatic shadow workspace creation for non-git directories with atomic, drift-checked patch application.

### Fixed

- **App startup crash on certain Windows 11 builds**: Wrapped `AppWindow.IsShownInSwitchers` in a safe try/catch block within `BubbleWindow.xaml.cs` to prevent unhandled `System.NotImplementedException` crashes in WinUI 3 / Windows App SDK.
- **Coding agent search action schema validation**: Fallback `Pattern = Text` in `CodingAgentService.TryParseAction` prevents repeated schema rejection when local LLMs emit `{ "action": "search_artifact", "text": "..." }` instead of `"pattern"`.

## [1.9.0] — 2026-09-16

Local fine-tuning and the local coding agent both worked for the first time in this release. Neither
had been blocked by hardware: each was held shut by a defect in PaRuX, and the verification tooling
that should have caught them was broken too.

Everything below was found by installing PaRuX from its own MSI onto a cleared machine and using it
through the real interface — first run, model download, chat, media, projects, workflows, training —
on a Windows 11 workstation with an NVIDIA GeForce RTX 5080 (16 GB) alongside a Radeon 780M iGPU.

### Added

- Any chat reply can be handed to the coding agent with **Apply this as a coding task**. It builds
  the goal from what was asked and what was answered, and runs it on the configured workspace with
  the same isolated worktree, bounded repair and independent verification every coding task gets.
- Workflow creation offers **step templates** — plan/build, research/write-up, build/test/review and
  a security review — instead of starting at an empty JSON box.
- The managed training runtime can be **removed again**, freeing roughly 5 GB, with datasets,
  adapters and job history kept. Installing it was previously a one-way decision.

### Fixed

- **Installing a rebuilt release did not replace anything.** Every build of a version carried the
  same ProductVersion and FileVersion, so the upgrade registered a second PaRuX in Installed Apps
  and left the old binaries in place while msiexec reported success.
- **Local fine-tuning was uninstallable on every machine.** The runtime pinned a Python patch that
  does not exist in uv's index, and repair deleted the existing environment before discovering it —
  so trying to fix it cost you the runtime you had. The pin is a series now, and nothing is removed
  until a replacement interpreter is in hand.
- **The coding agent could not complete a single step.** Its action schema carried a length bound
  the bundled llama.cpp cannot compile into a grammar, so every request was rejected before a token
  was sampled — and Core swallowed the error, returning an empty successful stream that made it look
  like the model had nothing to say. Provider failures now surface even once streaming has begun.
- **Training ran four samples one at a time** on hardware that could take them at once: 1.77x faster
  end to end on an RTX 5080 with an identical loss trajectory, simply by batching on the GPU instead
  of accumulating. The split is planned from the card's memory, and the effective batch never changes.
- First run named the wrong graphics card — quoting one adapter's memory beside another's name — and
  produced verdicts that contradicted their own arithmetic by comparing raw weights against VRAM
  instead of weights plus runtime overhead.
- A 2.3 GB model download reported no progress at all until the workspace was reopened.
- A folder picked during onboarding was discarded unless **Save** was pressed separately.
- A global hotkey Windows had refused was still presented as configured.
- An out-of-memory refusal named the limit but no way to get under it.
- Role inference read keywords inside longer words, so "the existing tests must still pass" routed a
  code change to a role with no repository authority, where it could only fail; and a coding task
  with no authority waited forever instead of failing with what to do.
- Four Italian starter prompts sat in an otherwise English interface, and the shipped file version
  repeated its commit twice.

### Changed

- The training engine status reports what it found — Python, Unsloth, torch, the visible accelerator
  — and applies the same provenance rule the job gate uses, so it can no longer advertise a runtime
  that answers 503 when you press Start.
- `build-msi.ps1`, the Virtual Context benchmark and the competitive inference benchmark now run in a
  freshly started shell. One of them reported "3/3 (100%)" while every model answer had failed.

## [1.8.0] — 2026-09-10

PaRuX can now be reached from another computer — in a browser, or by any application that speaks
the OpenAI or Ollama API — and it tells the truth about how fast it is.

Everything below was found or verified by using PaRuX on the target workstation, an AMD Ryzen 7
8840U with a Radeon 780M and no discrete GPU, feature by feature through the real interface.

### Added

- **Open PaRuX in a browser, from anywhere you can reach it.** Off by default. Turn it on in
  Settings → Privacy & Network, set a passphrase, and put a reverse proxy — Nginx Proxy Manager,
  Caddy, Traefik — in front of PaRuX on the same machine. PaRuX opens no port, touches no firewall
  rule and configures no router. The passphrase is stored only as a PBKDF2-SHA256 hash; the session
  is an HttpOnly, Secure, SameSite=Strict cookie; every state-changing request must echo a CSRF
  token a cross-site form cannot set; five wrong guesses lock the door for fifteen minutes. A
  browser session can chat, load models and change settings, but can never change the passphrase
  that admitted it — that stays with the desktop.

  Why it is built this way rather than reusing the Android pairing path: a browser can neither pin
  PaRuX's certificate nor hold a device token, which is exactly why the LAN path refuses any request
  from one. And with the proxy on the same machine, a request that came from the public Internet
  arrives at PaRuX from 127.0.0.1, indistinguishable from a program on your own desktop — loopback
  stops being evidence of anything, so the passphrase carries the whole weight and was built for it.

- **The browser client became a real client.** It was a 9.5 KB chat box. It now has saved
  conversations, model and role pickers, streaming with Stop, compaction notices, the model library
  with load, runtime state with unload, and settings — all driven by the same API the desktop app
  uses, so a chat started in the browser is the same chat in the desktop app.

- **Use PaRuX from other apps: OpenAI *and* Ollama.** PaRuX already answered OpenAI at `/v1`; it now
  also answers Ollama at `/api/chat`, `/api/generate`, `/api/tags`, `/api/show` and `/api/version`,
  so tools written for either work unchanged. This depends on Ollama for nothing — no process, no
  library, no binary — and does not impersonate it: `/api/version` reports PaRuX's own version, and
  `pull`/`push` are not implemented, because silently accepting a pull PaRuX would not perform is a
  lie told to a script. Ollama streaming is genuine newline-delimited JSON, one frame per token as
  the model produces it.

- **API keys, scoped to inference.** Created in Settings, shown once, stored as a hash. A key is
  confined by an explicit allow-list to the provider endpoints, so one pasted into a third-party
  application buys inference and can never reach the control plane that changes the LAN boundary,
  connects folders on your PC, drives Computer Use or reads your saved conversations. The desktop
  token could not do this job: it is regenerated every time PaRuX Core starts.

- **PaRuX now says what to do next to get faster or more accurate**, from what it has measured on
  your machine rather than from generic advice. Every recommendation names the measurement behind it
  so you can check it instead of trusting it.

  The advice about fine-tuning is deliberately reluctant. "Train it on your data" is the suggestion
  local-AI tools most often give and most often should not: it costs hours and a GPU you may not
  have, and the usual causes of a bad answer — the prompt, the retrieved context, the quantization,
  the routing — are all faster to change and easier to undo. So PaRuX refuses to recommend training
  until it has seen the same weakness fail repeatedly on a real Eval dataset, and tells you what to
  collect instead. When a draft model for speculative decoding would help, it names one from your
  own library or says plainly that nothing there fits — and it knows that an embedding or reranker
  model of the right family and size cannot generate a single token.

### Fixed

- **PaRuX reported its own prompt throughput 6.6× lower than the machine could do.** llama.cpp's
  prompt-per-second divides prompt tokens by the whole prefill, fixed start-up cost included, so on
  a short prompt it reports latency wearing throughput's units — and PaRuX's benchmark prompt was 32
  tokens. Measured on the target PC: 32 tokens reported 42.7 tok/s, while 610 gave 202, 2,410 gave
  281 and 7,210 gave 284. The Runtime Manager printed "43 prompt tok/s" for a machine doing 284, and
  any comparison drawn from that number was wrong by the same factor. Throughput is now measured on
  a prompt long enough for prefill to dominate; time-to-first-token is still measured on the short
  one, because that is what a person waiting on a chat actually feels.

- **Any local process could share PaRuX's runtime port and receive your prompts.** PaRuX serves its
  managed llama.cpp on 127.0.0.1:1234 — also LM Studio's default server port — and decided a runtime
  was healthy by asking that port whether it was alive, adopting whatever answered. Demonstrated on
  the target PC: a llama-server started by hand on 1234, with a GPU layout and context PaRuX never
  chose, answered a chat while PaRuX's own Runtime Manager correctly showed nothing running.

  It is worse than a collision. llama.cpp sets SO_REUSEADDR, so a second server binds the same
  address and port while the first is still serving — two were observed listening on 127.0.0.1:1234
  at the same moment, with Windows free to hand connections to either. A foreign process therefore
  does not have to win the port to read your prompts, documents and retrieved context; it only has
  to share it, while PaRuX looks perfectly healthy. PaRuX now requires that every listener on that
  port be the process it started, and refuses to send your message anywhere else — naming the likely
  culprit rather than failing vaguely.

- **Opening the browser client without signing in produced a bare `{"error":"Unauthorized"}`** with
  nothing to say where the door was. Browser navigations are now sent to the sign-in page; a script
  or CLI still gets the 401 it can act on.

## [1.7.1] — 2026-09-09

A patch release written almost entirely from one long session of using PaRuX on the target
workstation — an AMD Ryzen 7 8840U with a Radeon 780M and no discrete GPU — feature by feature,
through the real interface. Everything below was found that way, not by reading code, and every fix
was confirmed in the running app before it was written down.

The headline is that PaRuX's long-context promise did not work at all on the model it ships with.

### Fixed

- **Long conversations and pasted documents came back empty.** Any conversation or message that
  outgrew the physical window returned nothing — no answer, no error, no reason. The compaction path
  was the only code that rewrote the message list, and it added the Virtual Context digest as a
  *second* system message; the Qwen chat template rejects any system message that is not first, so
  llama.cpp answered HTTP 500 and the stream simply ended. The digest is now folded into the single
  leading system message. An 83-turn conversation now recalls a codeword from its own first message.
- **A pasted document larger than the window was handed to the model whole**, which answered with
  nothing. It now goes through Virtual Context like any other oversized history: the opening and the
  ending stay verbatim — that is where the instruction and the question live — and the middle is
  paged and recalled against them.
- **The context window was launched at a quarter of the size PaRuX asked for.** `--ctx-size` is the
  *total* across llama.cpp's parallel slots, not the size of each, so a request for 8k with automatic
  slots served 2k per conversation.
- **PaRuX guessed how many tokens your text was, and guessed wrong in both directions.** It assumed
  3.0 characters per token. Measured against the model's own tokenizer, the real figure runs from
  5.66 for English prose down to 1.16 for git hashes and hex — so ordinary prose was compacted when
  it would have fitted whole, and logs, JSON and identifiers *overflowed* the window the plan had
  just been built to respect. The chat path now counts with the model's real tokenizer, cached per
  message and skipped entirely when the text obviously fits, and falls back to the estimate if the
  tokenizer cannot be reached.
- **An empty reply now explains itself** instead of leaving you to guess, naming the real figures
  when a message was too large to send verbatim.
- **Web search cited an advertisement as its first source.** A question about the history of Rome
  opened its sources with several hundred characters of `duckduckgo.com/y.js?ad_domain=…` — an ad
  click tracker, presented as a citation and pushed into the model's context. Results that still
  point at the search engine after unwrapping are now rejected.
- **Web search returned nothing at all**, and the Internet switch could show "Off" while never
  having read the setting.
- **PaRuX said your PC had no GPU when it had one.** Detection went through `nvidia-smi`; on a
  machine with only an integrated Radeon that returns nothing, so the diagnostics claimed no GPU and
  no VRAM. It now reads the adapters the app had already enumerated.
- **The firewall advice was wrong on the machine printing it.** Enabling local-network access always
  said "allow PaRuX Core on Private networks only" — but Windows applies firewall rules by the
  category assigned to the network, not by address range, and a private 192.168.x.x address on a
  router Windows classes as *Public* meant the advice could not be followed. PaRuX now reports the
  category Windows actually assigned and says what that machine really needs. It still never changes
  a firewall rule or a network category for you.
- **Gaming Mode silently returned an empty, successful-looking chat response** instead of saying it
  had cancelled the request.
- **Conversation branches had a Previous/Next/Versions navigator that never appeared.**
- **The Quick Chat bubble** collided with the composer's Send button, and did not dock where it said
  it would.
- **Four defects in Model Hub downloads**, and a saved permission change on a built-in skill failed
  with a conflict against itself.
- **Lifecycle Lab**: column pickers stuck blank when opened before their data arrived, an Eval run
  sat on "started" and never showed its result, and the dataset picker offered files the page could
  not ingest.
- **Creating a Project from the desktop returned 405** — both shipped clients called a route the
  Core had never mapped.
- **A failed training job reported nothing useful**; the real error is now surfaced.

### Added

- **Pickers instead of typing.** Model roles, MCP servers and Lifecycle Lab columns are chosen from
  the real values PaRuX knows about rather than typed by hand and validated afterwards.
- **The local-network status line reports how Windows classifies your network**, so a device that
  cannot reach your PC has an explanation on the PC.

### Changed

- The test suite runs serially. Two of four release-gate runs had been failing at random because a
  process-wide SQLite pool reset raced parallel test collections. Measured cost: about a minute and
  a half.

## [1.7.0] — 2026-09-08

Closing the gaps the September competitive audit found. The suite and an MSI build have now been
verified on the target Windows workstation, including a real, previously-undetected UI defect found
and fixed during that pass (see Fixed, below).

### Added

- **First run now answers "which model do I download?"** PaRuX reads your GPU and memory, ranks the
  models it curates against that machine, and recommends one — with a sentence naming your actual
  hardware instead of a VRAM figure to interpret. Models your PC cannot run stay listed and marked,
  so the limit is visible. Overhead is counted, so a 16 GB model is not promised as fitting a 16 GB
  card; an uncensored build is never the first suggestion; and a coding model never leads for
  somebody who has not yet had a conversation.
- **A guided walkthrough of the sidebar**, shown once after setup and replayable from
  Settings → *Show me around*. It points at each section and says what it is for.
- **A Linux or macOS PaRuX can install its own inference runtime.** Asset selection was hardcoded to
  Windows, so a non-Windows Core could run a llama.cpp build you supplied but never fetch one. It now
  picks the build for the machine it is on, restores the executable bit the extractor used to drop,
  and — when a release genuinely has no build for your platform — says so and lists what that release
  did publish, instead of installing the wrong one.
- **PaRuX writes Word and Excel files, not just reads them.** Ask for a report or a spreadsheet and
  a real `.docx`/`.xlsx` appears — headings, bullets and tables in Word, numbers typed as numbers in
  Excel — produced by OpenXML directly, with no Office installation needed and no macro in the file.
  It writes only into one folder you choose (never the folders it reads from), never over a file
  that already exists — a taken name gets " (2)" like a download — and it tells you the path it
  wrote. Settings → *Apps on this PC* → **Choose folder**.
- **Word, Excel and PowerPoint** joined the connectable apps: text from `.docx`, `.xlsx` and `.pptx`
  in a folder you choose, plus PDF and RTF, and a separate entry for files already synced to OneDrive
  locally. Read-only — PaRuX never edits a document in place.
- **Your own PC apps as a source of answers.** Settings → *Apps on this PC* connects Obsidian and
  Logseq vaults, any notes folder, Thunderbird mail, a folder of exported `.eml`, `.ics` calendars,
  and Edge/Chrome bookmarks. Everything is read locally and read-only, nothing is connected until
  you connect it, each source is limited to the one folder or file you pick, and every answer cites
  the file it came from. "Show me what it reads" previews a source before you rely on it. Browsing
  history is deliberately not read.
- **MCP servers over HTTP, including hosted ones.** Streamable HTTP with SSE fallback, bearer tokens,
  and OAuth 2.1 with dynamic client registration; tokens are cached encrypted so you approve a server
  once rather than after every restart. Each server carries its own approved tool list, enforced both
  when tools are offered to the model and when one is called.
- **A browser client.** `parux serve` prints a loopback URL that opens a full streaming chat client
  served from inside PaRuX Core, loading nothing from the network. This is what makes a Core with no
  desktop app usable by a person.
- **Web search when the question needs it.** Previously PaRuX searched only if you typed "search the
  web". It now recognises questions whose answers go stale — the latest release, today's news, a
  pasted link, a current price — while leaving questions about your own machine alone. The Internet
  switch is unchanged and still off by default.
- **winget packaging.** `packaging/winget/` and `scripts/build-winget-manifest.ps1`. Submission waits
  on a published, signed installer.

### Fixed

- A generated document could be given a name like `../escape` or `sub/report` and PaRuX would write
  it as `escape.docx` and `report.docx` rather than refusing: the name check inspected a
  path-stripped version of the name instead of the name itself. It now refuses.

Running the Core suite on Linux for the first time surfaced defects Windows had been hiding:

- Credentials could not be stored at all without Windows DPAPI. They now use DPAPI on Windows and
  AES-256-GCM under an owner-only key file elsewhere, failing closed if that file is readable by
  others.
- Path-traversal guards were weaker off Windows, where `..\..\evil.gguf` is a legal filename and
  `C:/Windows/win.ini` is not a rooted path. Containment checks also compared case-insensitively on
  case-sensitive filesystems, treating `/srv/models` and `/srv/Models` as one directory.
- Two models differing only in case were reported as the same model, which on Linux they are not.
- Agent prompts embedded the host's line endings, so the same task produced different prompt bytes —
  and so a different prompt-cache key — depending on which OS assembled it.
- Four API strings formatted numbers with the host's locale, so the same result described itself
  differently on different machines.
- LoRA convert/merge reported "the engine is not installed" for a blank base model id, sending you to
  fix the wrong thing.
- **Runtime Manager could freeze the whole app.** Racing installed llama.cpp builds, running
  "Optimize for this PC", or applying a performance mode while the Server returned an ordinary
  expected error (for example, only one build installed) threw out of an `async void` click handler
  uncaught. The app kept running but the chat composer and sidebar navigation stopped responding to
  input until a full restart. Found by reproducing it live on the physical acceptance workstation;
  fixed by handling the error in place, the same way the page's other actions already did, and
  verified by reproducing the exact freeze on the old build and confirming it is gone on the new one.

### Changed

- **The interface mode switch says what it does.** It read "Auto"/"Studio" beside the product name
  with only a tooltip, so the one control that decides how much of PaRuX you can see looked like a
  preference about routing. It is now labelled *Interface*, reads *Simple*/*Advanced*, and states
  underneath exactly which workspaces it adds or removes.

### Removed

- The GitHub Actions workflows added on 2026-09-02. Releases are built locally; see
  [`docs/RELEASING.md`](docs/RELEASING.md).

### Known limitations

Verified on the physical target Windows workstation for this release: a clean local software gate
(full build, the complete test suite, VS Code tests, Android build), and real, working local
inference, Runtime Manager, and component detection. Not yet exercised on this exact release build:

- **MSI upgrade-from-1.6.0 and downgrade-guard** were not run this pass — the harness for it
  requires an elevated shell that was not available during this verification session. Clean
  install/uninstall was verified.
- **ComfyUI/Diffusers image generation and Windows ML/ONNX Runtime GenAI provider discovery** were
  not exercised this pass, deferred under real, tight shared GPU/system memory on the verification
  machine rather than run under conditions likely to fail or disrupt other work on it.
- **Only one llama.cpp build and one model format (GGUF) are currently installed** on the
  verification machine, so no fresh cross-build or cross-format Engine Race was produced this pass;
  the safety mechanism itself (correct refusal to promote an unverified or missing build) was
  confirmed against real historical evidence.
- **The same-machine competitor benchmark** against LM Studio and Ollama covers one model and one
  hardware profile (2026-09-07); the fuller matrix (VRAM residency, concurrent scaling, cancellation,
  long-context degradation, sustained load, a 27B-class model) is still open.
- **Android emulator smoke test** was not run this pass for the same memory reason; it is optional
  and non-blocking, and the JVM/lint/APK build itself did pass.

## [1.6.0] — 2026-09-02

First public release of RBG PaRuX AI Studio.

### Added

- **Optional cloud fallback.** Settings → Cloud fallback lets you nominate an OpenAI-compatible API
  (OpenRouter, OpenAI, Groq, DeepSeek, Mistral, Together, or a custom HTTPS endpoint) that PaRuX may
  use **only when local inference cannot serve a turn at all**. Off by default. It never runs while
  local inference is working, never during Gaming Mode, announces every turn it serves before the
  first token, and stores the API key encrypted with Windows DPAPI. Turning it off deletes the key.
  The Android client shows the same notice above the answer, so the guarantee holds on both clients
  and not just on Windows.
- **Windows MSI and Android APK are now published as GitHub Releases**, built by CI from a tagged
  commit with the Core test suite as the gate.
- **README now documents the OpenAI-compatible local server** (`/v1/models`,
  `/v1/chat/completions`, `/v1/embeddings`) and `parux serve`, so existing tools can be pointed at
  PaRuX's local models.

### Documentation

- `docs/PARUX_COMPETITIVE_GAP_2026-09.md` — audit against the current local-AI field, separating
  what was verified in this repository from what competitors report.

### Known limitations

- **Windows x64 only.** There is no macOS or Linux build. PaRuX Core is `net8.0` and portable in
  principle; the desktop shell, DPAPI credential storage and the installer are not.
- **The installer is not Authenticode-signed**, so a fresh Windows machine shows a SmartScreen
  "Unknown publisher" prompt. The signing path exists in `scripts/build-msi.ps1` and is unused
  because no certificate has been obtained.
- **The Android APK is release-signed only if the repository owner has configured a keystore.**
  Without those secrets CI publishes the debug-signed APK, which installs but is not the production
  signing identity.
- **MCP servers are stdio-launched only** — no remote transport and no OAuth, so hosted MCP servers
  that require a login cannot be used yet.
- **No browser client.** The clients are the Windows desktop app and the paired Android companion.
