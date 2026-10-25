# UX audit part: first run, modes, Model Hub, downloads, runtime, chat, media, errors, accessibility

Domain key: `ux`. Clean-room competitive audit of PaRuX AI Studio against Locally Uncensored (LU) v3.0.6.

- PaRuX reference: worktree on branch `feature/parux-competitive-integration-20261019`, HEAD `117fcf7` (only untracked `docs/audits/` at audit start). Paths below are repo-relative.
- LU reference: external clone at tag `v3.0.6` (commit `bce78481`), read only. LU paths below are relative to the clone root, and `src/components/` is shortened to `components/`.
- Date: 2026-10-19. Not committed.
- Nothing was built, run or tested. No network. No LU script or binary was executed.

## 1. Scope

In scope: first run and onboarding, Simple vs Advanced mode, Model Hub UX, download manager, runtime manager, chat, media creation and gallery, automatic model selection, error messages, notifications, progress, settings, diagnostics, accessibility, responsiveness, first-install effort, and the four golden paths (first chat, first image, first video, first coding-agent task).

Out of scope: model catalogue and licensing (other parts), engine internals, the Android client (noted only), and LU cloud products. LU cloud sign-in and credits are a non-goal for PaRuX core, which must not depend on proprietary cloud inference.

## 2. Method and evidence levels

- PaRuX: Serena symbol overviews and bodies for `OnboardingView`, `MainWindow` (constructor, workspace navigation, guided tour), `ChatView.SimpleMode`, `ChatView.ModelRouting`, `ModelHubView.RenderDownloads`, `CodeHandoffService.EnsureRepositoryAsync`; Grep for labels, AutomationIds and status strings; test method names in `tests/PaRuX.UITests/Tests/`; `tests/PaRuX.Server.Tests/FirstModelAdvisorTests.cs`; `docs/WINDOWS_MSI_RELEASE.md`; `installer/` and `packaging/` listings.
- LU: search and reading of `components/onboarding/`, `components/chat/AgentWorkspaceDialog.tsx`, `components/chat/StagedChangesPanel.tsx`, `components/layout/DownloadBadge.tsx`, `components/models/DiscoverModels.tsx`, `components/create/experimental/`; Playwright spec titles and the happy-path body of `e2e/onboarding-builtin.spec.ts` (lines 44-90); headings of `docs/guide/*/index.html`.
- Evidence levels: `static` = code read. `unit_tests` = a named test file or class exists (not executed here). `e2e_real` = not claimed for any row, because no recorded real run was inspected. LU e2e specs run against a Tauri mock (header of `e2e/onboarding-builtin.spec.ts`), so LU e2e is mock-backed evidence.
- Local `main` branch was checked by file name for gallery, onboarding, media and download UI. No UX-domain feature was found only on `main`, so `onLocalMainOnly` is false for every row.
- Manual intervention = one click or explicit choice. OS installer prompts and waiting time are excluded and noted separately.

## 3. Golden paths: manual interventions

### 3.1 First chat with a good text model

LU (happy path, `e2e/onboarding-builtin.spec.ts:44-90`, mock-backed):
1. Installer run (outside the app).
2. Welcome: Get Started.
3. Backends: built-in engine preselected; Continue.
4. ComfyUI step: Skip for now (optional for text).
5. Models: pick the starter GGUF, then Install N model. Download, SHA-256 check and engine boot run automatically and the step advances on its own.
6. Embeddings: Skip for now (optional).
7. Done: Get Started.
8. New chat, type, Send.
Total: 9 clicks, of which five are confirmations or skips (Get Started twice, Continue, Skip twice), plus one typed prompt. No runtime choice, no file choice, no separate load step.

PaRuX (static path through `Views/OnboardingView.xaml`, `Views/OnboardingView.FirstModel.cs`, `Views/ModelHubView.cs`, `Views/SettingsView.xaml.cs`):
1. Installer run (MSI, see 3.5).
2. First launch shows onboarding only until the completion flag is set (`MainWindow.xaml.cs:121-147`).
3. Components: detection is automatic. Install with PaRuX for llama.cpp is optional if a runtime is already detected or staged (open question 2).
4. Get this model: hands off to Model Hub with the repository searched (`OnboardingView.FirstModel.cs:247-264`). It does not download.
5. Model Hub: choose the quantisation file that matches the hint.
6. Download. Some models also need the licence acknowledgement (`Views/ModelLicenseConsentPrompt.cs`).
7. Wait in the Downloads list (no clicks).
8. Settings > managed models > role > Load (`Views/SettingsView.xaml.cs:3394-3406`), unless chat loads on demand (open question 1).
9. The hand-off has already set `OnboardingCompleted` (`MainWindow.Workspaces.cs:100`), so Get Started is never shown. Return to chat with New chat, because the sidebar has no Chat item.
10. Type, Send.
Total: 7-10 clicks (quantisation file, download, licence acknowledgement where required, role, Load, New chat, Send), plus one typed prompt and one installer run.

Verdict: click counts are similar. LU asks fewer decisions (no quantisation file, no runtime choice, no Load step) but has more confirmations (Continue, Skip). PaRuX's larger costs are the explicit Load step and the dead end after the hand-off. The quantisation choice is a deliberate product decision (the onboarding copy says so) and should stay reachable, but the default path should not require it.

### 3.2 First generated image (Windows)

LU: ComfyUI step during onboarding (Install is automatic; Python is installed when missing, `components/onboarding/ComfyStep.tsx:88-97, 236`), or skip. In Create, pick an image model and press Download & install (`components/create/experimental/Stage.tsx:591`). One download brings the image part too (`e2e/image-part.spec.ts:93`). Then prompt and Generate. About 3-5 actions.

PaRuX: ComfyUI can be installed only in the onboarding COMPONENTS list or in the Advanced Runtime workspace (`MainWindow.xaml:80-82`). Settings has no component install (no reference to `RuntimeManagerClient` or `StartInstallAsync` in `Views/SettingsView.xaml.cs`), although the Create copy at `Views/CreateWorkspaceView.cs:200` says Runtime or Settings can install it. If onboarding was skipped: Interface toggle to Advanced, Runtime, Install ComfyUI, wait. Then Model Hub, image candidate, "1. Download checkpoint" (`Views/ModelHubView.cs:3094`), wait, "2. Activate downloaded" (`ModelHubView.cs:3098`), back to Create, prompt, "Generate image" (`CreateWorkspaceView.cs:120`). About 5-6 actions if ComfyUI was installed during onboarding, about 8-10 if it was skipped.

Verdict: LU needs fewer. The two-step activation and the Advanced-only runtime path are the main costs.

### 3.3 First generated video

LU: as 3.2, then the video tile Get (the tile text states what is fetched, including a missing text encoder, `e2e/mac-model-manager.spec.ts:236`), an "Install MP4 support?" modal with install, WebP or cancel (`components/create/experimental/VhsInstallModal.tsx:22`), then Generate. About 4-6 actions. On Mac an MLX lane is used, and a newcomer card says when no video starter runs (`e2e/mac-persona-walk.spec.ts:134`).

PaRuX: ComfyUI as in 3.2, plus a video checkpoint through the same two-step Model Hub flow, then Create > Video > "Generate video" (`CreateWorkspaceView.cs:138`). About 6 actions if ComfyUI is already installed, about 9 if not. No output-format decision was found (open question 3).

Verdict: LU needs fewer.

### 3.4 First coding-agent task

LU: Agent or Code mode. On first activation the workspace dialog asks where the agent works, Sandbox first (`components/chat/AgentWorkspaceDialog.tsx:174-219`). Type the request and Send. Approve tool calls in the approval strip. Review each staged change and Apply or Reject it (`components/chat/StagedChangesPanel.tsx:156-164`). About 4-6 actions plus approvals. The LU coding flow was not traced end to end.

PaRuX (Simple mode): choose a code folder (`Views/ChatView.SimpleMode.cs:58-76`; non-git folders are accepted through a shadow repository, `Services/CodeHandoffService.cs:350-364`); type the objective and Send; press "Start PaRuX Coding Agent" (`Services/ChatSession.cs:772`); wait; Review; Apply reviewed changes; confirm the dialog (`Views/ChatView.NativeCodingHandoff.cs:928-939`, Italian text). About 6-7 actions. The dialog states that no commit or push is made.

Verdict: LU needs fewer. PaRuX's isolation and no-commit behaviour should stay. The folder requirement and the extra start step can go.

### 3.5 Installation effort

LU: Windows setup executable; the install page explains the SHA-256 check and the SmartScreen warnings (`docs/guide/install/index.html:65-149`); signed updates (`docs/guide/install/index.html:154`). The built-in engine needs no separate install (`components/onboarding/BackendsStep.tsx:99`).

PaRuX: MSI with an embedded, verified llama.cpp Vulkan baseline that is staged into the user runtime folder on first server load (`docs/WINDOWS_MSI_RELEASE.md`; `src/PaRuX.Server/BundledBaselineRuntimeBootstrap.cs:8-18`). Python and uv are provisioned on demand (`src/PaRuX.Server/ComponentInventoryService.cs:30`). WinGet manifests exist under `packaging/winget`. Release builds are signed locally per the doc. No published MSI was verified here.

## 4. Feature table

LU paths are relative to the clone root. Rows are ranked by priority. The same rows, with the same IDs, are returned in the structured output.

| ID | Feature | LU behaviour | PaRuX status and evidence | Gap | Recommendation (priority) |
|---|---|---|---|---|---|
| R01 | First-run onboarding | Six-step wizard (welcome, backends, ComfyUI, models, embeddings, done); each optional step has Skip for now; built-in engine preselected (`components/onboarding/*.tsx`, `e2e/onboarding-builtin.spec.ts`) | Single page with runtime location, components Detect/Locate/Install/Skip, first-model advice and ready check (`Views/OnboardingView.xaml:19-119`); forced on first run (`MainWindow.xaml.cs:121-147`, `ShowOnboarding` at 714-721). Component lifecycle covered by `tests/PaRuX.Server.Tests/ComponentLifecycleAcceptanceTests.cs`. No UI test drives first run end to end; only a start-page fixture (`tests/PaRuX.UITests/Infrastructure/UiTestApplication.cs:170`) | Partial: the first-model path ends outside onboarding; no single success state; ready check speaks only about the text runtime | improve (P0) |
| R02 | Chat home and navigation | Not traced in depth (luPresent unknown) | No Chat entry in the sidebar (`MainWindow.xaml:53-85`; no chat tag). Return to chat is New chat (`MainWindow.xaml:135-154`) or a history item added at runtime (`MainWindow.xaml.cs:289-377`). CLAUDE.md names chat as home | Dead end after the Model Hub hand-off | improve (P1) |
| R03 | First-install effort | Setup executable with hash guidance and SmartScreen explanation; signed updates; built-in engine (`docs/guide/install/index.html`) | MSI with embedded verified llama.cpp Vulkan baseline (`BundledBaselineRuntimeBootstrap.cs:17-18`); Python and uv on demand; WinGet folder; public-release signing configured (`docs/WINDOWS_MSI_RELEASE.md`) | Staged baseline not reflected in first-run copy (open question 2); first run still asks for several decisions | improve (P1) |
| R04 | Media checkpoint activation | One Install action; installed tile has Use (`components/models/__tests__/installed-tile-has-a-use-button.test.ts`); image part fetched in the same download (`e2e/image-part.spec.ts:93, 160`) | "1. Download checkpoint" (`Views/ModelHubView.cs:3094`), wait, "2. Activate downloaded" (`ModelHubView.cs:3098`); queued status at 3244; `ActivateMediaCheckpointAsync` at 3255-3266 | Two manual steps and a wait for a deterministic follow-up | improve: auto-activate on completion (P1) |
| R05 | Download manager | Floating tray on every page with progress bars, pause, resume, cancel, retry, bundle grouping, permanent vs transient error split, completion pulse (`components/layout/DownloadBadge.tsx`) | Downloads list inside Model Hub: last 30 jobs, raw status words, bytes "X / Y", SHA-256 status; Pause keeps partial bytes; Resume; Remove behind a confirmation (`Views/ModelHubView.cs:3978-4053`); polling at 561-577. No progress bar, no ETA, visible only inside Model Hub | Human state labels, progress bars, global indicator, completion notice for text downloads | improve (P1) |
| R06 | Runtime manager and component lifecycle | Built-in engine preselected; Ollama and LM Studio install steps with installer output (`components/onboarding/BackendsStep.tsx:258, 373`); ComfyUI auto-detect and skip (`ComfyStep.tsx:88-97, 634`) | Detect, Locate existing, Install with PaRuX, Skip in onboarding (`Views/OnboardingView.xaml.cs` 19-392; `InstallManagedComponentAsync` 245-291 picks the recommended build and streams stage and bytes). Runtime workspace is Advanced only (`MainWindow.xaml:80-82`). Settings has no component install, but the copy at `OnboardingView.xaml:119` and `CreateWorkspaceView.cs:200` says it does | Runtime health invisible in Simple mode; copy promises a path that does not exist | improve (P1) |
| R07 | Error messages and recovery | Failures carry installer output and a readable reason; permanent vs transient download errors are separated (`DownloadBadge.tsx:3, 8`); calm failure header test (`components/chat/__tests__/a-fixed-failure-keeps-the-header-calm.test.tsx`) | Good honesty in places: "Core did not confirm a complete policy. No success was assumed." (`Views/ModelHubView.cs:794`). But about 436 `ex.Message` occurrences in `Views/`, many concatenated into status text: `ModelHubView.cs:2977`, `ChatView.NativeCodingHandoff.cs:689, 776, 1019`, Pause and Resume failures at 3978-4053 | Raw exception text, no cause, state or next action; Italian mixed in | improve (P1) |
| R08 | Language consistency | English UI; German appears in test names and comments only | English UI with Italian production strings: apply confirmation title, body and buttons (`Views/ChatView.NativeCodingHandoff.cs:928-939`), coding status lines (535, 588, 689, 776, 850, 886, 897, 1019), `Views/ChatView.SimpleMode.cs:55` | Safety-relevant apply dialog in a different language; no resource files | improve (P1) |
| R09 | First coding-agent task | Sandbox default in the workspace dialog; approval strip in plain words; per-change Apply and Reject (`components/chat/StagedChangesPanel.tsx:156-164`) | Code folder required in Simple mode (`ChatView.SimpleMode.cs:58-76`); "Start PaRuX Coding Agent" action (`Services/ChatSession.cs:772`; route copy at `Services/RouterService.cs:283`); review and confirmed apply without commit (`ChatView.NativeCodingHandoff.cs:928-939`); UI tests `SmokeTests.cs:71, 104, 158` (not executed) | 6-7 actions; folder required even for a first try; Italian dialog | improve (P1) |
| R10 | Media gallery and outputs | Gallery items reusable as Edit source (`e2e/create-edit-gallery-source.spec.ts:39`); video lightbox (`e2e/create-save-character.spec.ts:105`); gallery download saves the file (`e2e/gallery-download-saves-the-file.spec.ts`) | No gallery view. Outputs are "registered in Artifact Workspace" (`Views/CreateWorkspaceView.cs:292, 344, 367-368`) and shown as a text list in Projects (`Views/ProjectsWorkspaceView.cs:288-304`), with versions and restore (360-376). No gallery-named file on local `main` either | Generated images are hard to find; no thumbnails (not verified in code) | implement (P1) |
| R11 | Model Hub discovery and search | Discover view with fit and download in one place (`components/models/DiscoverModels.tsx`); not traced further | 3,708-line `Views/ModelHubView.cs`. Search placeholder "Search Hugging Face GGUF models" (line 24); Search and Refresh (168-172); "Compare variants & download" (3016); "Compare quantizations + hardware fit" (3032); evaluation criteria and candidate vocabulary (29-31 and later) | Too many concepts for a first-time user; the curated starter path is buried | improve (P1) |
| R12 | Automatic model selection and visibility | Per-chat selector with capability notes, e.g. tool use through the prompt when there is no native channel (`components/models/ModelSelector.tsx:594`); completed bundled download is activated (`components/models/DiscoverModels.tsx:847`) | Auto routing profiles and route explanation (`Views/ChatView.ModelRouting.cs:51-82, 92-164, 194-252`); selector hidden in Simple mode (`Views/ChatView.SimpleMode.cs:17`), so the user never sees which model answered | No model label on answers in Simple mode; text models are not auto-activated after download (see 3.1) | improve (P1) |
| R13 | Simple vs Advanced mode | No global switch found; disclosure per surface (Create "Expert" section closed by default, `e2e/create-expert-section.spec.ts:152`; `components/create/experimental/AdvancedDrawer.tsx`) | Global Interface toggle in the pane header (`MainWindow.xaml:103-115`); Advanced adds seven workspaces (`MainWindow.xaml:62-82`, `MainWindow.Workspaces.cs:47-72`) and chat model, skill and profile selectors (`ChatView.SimpleMode.cs:13-26`); default Simple (`Models/AppSettings.cs`, `StudioModeEnabled`); test `SmokeTests.cs:1953` (not executed) | Simple mode hides the only recovery surfaces (runtime, routing) and shows no runtime health | keep and improve (P1) |
| R14 | Progress and waiting feedback | Working beacon and anchor; local lane wait line (`components/chat/WorkingBeacon.tsx`, `components/chat/LocalLaneWaitLine.tsx`); embeddings progress mirrored (`components/onboarding/EmbeddingsStep.tsx:122`) | Onboarding install shows stage and bytes as text; its ProgressBar is indeterminate and collapsed (`OnboardingView.xaml:62`); downloads are text only; Create shows status text | No determinate bars for installs and downloads; no ETA | improve (P1) |
| R15 | Create workspace | Experimental Create surface: composer, intent bar, prompt improvement, batch of up to 50 images, lightbox, mask editor, model chips with tiers (`components/create/experimental/*`; `e2e/create-batch-edit.spec.ts:78, 131`) | One workspace for Image, Video, Edit (instruction edit and variation), 3D, speech and transcription (`Views/CreateWorkspaceView.cs:71-242`); one active job with "Cancel active job" (95); no batch; `tests/PaRuX.UITests/Tests/CreateEditModeTests.cs:13` (not executed) | No batch, no prompt helper, no readiness card; 3D engine must be installed elsewhere (line 200) | improve (P2) |
| R16 | Notifications | Tray and layout banners: storage quota toast, update badge, stale models banner, background shutdown banner (`components/layout/*.tsx`); background work does not prompt (`e2e/hintergrund-fragt-nicht.spec.ts`) | Windows tray notifications for media job completion and session state (`MainWindow.xaml.cs:861-913`, `TrayIcon.ShowNotification` at 897 and 904); none for downloads or runtime installs | Long jobs finish silently inside the app | improve (P2) |
| R17 | Settings structure | Topic components: hardware, permissions, memory, speech, remote access, chat backup, log file, MCP, content policy (`components/settings/*.tsx`) | `Views/SettingsView.xaml` 1,822 lines and `Views/SettingsView.xaml.cs` 5,959 lines plus nine partial files; runtime location and managed models (`SettingsView.xaml:1486-1539`); cloud fallback, off by default (313); "Advanced gateway settings" expander (877) | Hard to change safely and to navigate; no topic navigation | improve (P2) |
| R18 | Diagnostics and logs | Log file settings, memory debug panel, troubleshooting guide (`components/settings/LogFileSettings.tsx`, `components/chat/MemoryDebugPanel.tsx`, `docs/guide/settings-and-troubleshooting/`) | File logging through `Services/Log.cs`; Advanced and diagnostics section (`SettingsView.xaml:1706-1707`); Observability and Updates are Advanced only (`MainWindow.xaml:74-77`); no one-click support bundle found | Simple user has no diagnostics export | improve (P2) |
| R19 | Accessibility | Contrast and focus tests (`components/__tests__/wcag-contrast.ts`, `components/__tests__/fokusring-und-press.test.ts`); shortcut sheet (`components/layout/ShortcutsModal.tsx`); overlay Escape test (`e2e/escape-closes-overlays.spec.ts`) | 171 AutomationProperties, AutomationId or SetName uses in 19 App files; accessible name for the icon-only code folder button (`ChatView.SimpleMode.cs:38-40`); UI tests select by AutomationId (`tests/PaRuX.UITests/README.md:32`); no contrast or focus test found | No contrast or focus automation; keyboard map not verified | improve (P2) |
| R20 | Responsiveness | Narrow and low-window tests across widths (`e2e/mac-model-manager-narrow.spec.ts:60`, `e2e/create-model-picker-fits-the-window.spec.ts`, `e2e/code-composer-narrow-keeps-stop.spec.ts`) | Chat header adapts on resize and recent history is bounded (`tests/PaRuX.UITests/Tests/SmokeTests.cs:580`; `Views/ChatView.ResponsiveHeader.cs`); window maximised on launch (`MainWindow.xaml.cs:160-184`) | One resize test; no width matrix | improve (P2) |
| R21 | Guided tour and help | Eleven guide pages under `docs/guide/` (install, first start, chat, create, code, agent, cloud, troubleshooting, FAQ); in-app docs button (`components/chat/DocsButton.tsx`) | Guided tour with TeachingTips over sidebar controls (`MainWindow.Tour.cs:32-79, 136-152`), started only through Get Started (`MainWindow.xaml.cs:723-729`); the Model Hub hand-off never starts it (`MainWindow.Workspaces.cs:97-104`) | Tour skipped on the recommended path | improve (P2) |
| R22 | Hardware fit and first-model advice | Starter list with VRAM advisory from a GPU probe; starter flagged when full offload may not fit (`e2e/onboarding-builtin.spec.ts:24-42`; probe order in `components/onboarding/ModelsStep.tsx`) | Server-side advice with capability groups for chat, coding, vision, image and video, and uncensored preference (`src/PaRuX.Server/FirstModelAdvisor.cs:71-294`); 22 tests in `tests/PaRuX.Server.Tests/FirstModelAdvisorTests.cs`; storage advice and grouping UI tests (`SmokeTests.cs:1828-1932`) | Honest copy (fit is not quality); no gap of substance | keep (P2) |

## 5. Top 10 proposals (ranked)

1. P0, R01, R02, R21: one first-run path to chat. After the starter is chosen, download, auto-activate and land in chat with a readiness card. Keep the quantisation choice under details. Do not set `OnboardingCompleted` on hand-off without a return path, and start the tour after the first answer.
2. P1, R04, R12: auto-activate completed text and media checkpoints. Keep Load and Activate as fallbacks for files the user copied in, and label the answering model in Simple mode.
3. P1, R02, R06, R13: a Chat entry at the top of the sidebar and a runtime health line visible in both modes. Fix the Settings copy so it matches what Settings does.
4. P1, R08: move all user-facing strings into resource files; translate the apply confirmation and coding status lines first.
5. P1, R07: one error helper that states the cause, what changed on disk and one next action. Stop concatenating raw `ex.Message` into status text.
6. P1, R05, R14, R16: a download tray on every workspace with determinate bars, human state labels and a completion notice.
7. P1, R09: first coding task without a folder choice (sandbox or shadow default), start the agent after one send, keep the apply confirmation and the no-commit statement.
8. P1, R10: a media gallery with thumbnails, open, reveal in folder, reuse as edit source, and confirmed delete.
9. P1, R03: first-run copy that shows the bundled baseline runtime as ready when it is, and a measured first-install checklist.
10. P2, R19, R20: accessibility and width test matrix (contrast, focus, keyboard, narrow windows) in the UI suite.

Backlog (P2): R15 batch and readiness card for Create; R17 split Settings into topic pages; R18 redacted diagnostics export available in Simple mode.

## 6. Risks

- Auto-download and auto-activation can consume disk. Keep the storage budget check (`Views/SettingsView.xaml:1512` says downloads are checked against the AI storage budget) and show it before the download starts.
- Licence acknowledgement must not be bypassed by automation (`Views/ModelLicenseConsentPrompt.cs`; `SmokeTests.cs:1684`).
- LU cloud onboarding and credits (`e2e/cloud-onboarding.spec.ts`) must not be copied into core. PaRuX's optional fallback (`Views/SettingsView.xaml:313`) is off by default and must stay that way.
- Evidence gap: the PaRuX UI suite (59 test methods, `tests/PaRuX.UITests/Tests/`) and `FirstModelAdvisorTests` were not executed here, so pass status is unknown.
- Large files: `Views/ModelHubView.cs` (3,708 lines) and `Views/SettingsView.xaml.cs` (5,959 lines) make UX changes costly and risky.
- Simple mode hides recovery paths, so a failed runtime or model is harder for a Simple user to fix.
- The default starter is a product and content-policy decision. LU's default starter in its e2e fixtures is an uncensored community GGUF. PaRuX should make content preferences explicit rather than copy a default.

## 7. Open questions

1. Does single-model chat auto-load a downloaded text model without the Settings Load step? `src/PaRuX.Server/LocalAiProcessManager.cs:1319-1326` uses `--models-autoload` only in the hot model pool router. The single-model path needs a real run to confirm.
2. Does the MSI-staged llama.cpp Vulkan baseline appear as detected in onboarding (`src/PaRuX.Server/ComponentInventoryService.cs:49-50`)? If yes, golden path 3.1 loses its runtime step.
3. Do video checkpoints need a separate text encoder or VAE in PaRuX, as in LU? Does PaRuX offer an output-format choice?
4. Is there a diagnostics export outside the Advanced views?
5. Which LU Create surface is the default on Windows, experimental or classic?
6. Are the PaRuX UI tests green at HEAD? A test run is in progress in another worktree; not checked.

## 8. Clean-room statement

The behaviour above is written in our own words from reading LU source. Only identifiers, file names, short UI labels and flags are reproduced. No LU code, prompt or table is copied. No LU script was run and no network was used.
