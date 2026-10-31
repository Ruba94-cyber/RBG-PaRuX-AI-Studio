# Physical PC acceptance checklist (DRAFT)

Manual procedures for the owner to run on a real Windows PC and phone. Every procedure ends with a status line that stays NOT RUN until a person records a result. Drafted from the repository by a local model; each section must be checked by a human before use.

## Computer Use across Windows applications (NOT RUN)

### 2026-10-31 bounded GUI self-repair and native Windows UIA recovery (NOT RUN)

This is **two distinct tests**. The native fixture CLI test is deterministic but is NOT a live PaRuX agent/autonomous Core acceptance. Do not combine their PASS statuses.

**Source syntax first:** From the repository root on Windows PowerShell, run:

```powershell
powershell -ExecutionPolicy Bypass -File tools\test-agent-validation-syntax.ps1
```

This parses five local validation scripts and fails if the main runner has duplicated structural blocks. It is a syntax check, not functionality. The old duplicated runner was repaired on 2026-10-31.

**Opt-in real WinApp fixture regression:** On an interactive disposable Windows x64 development PC with .NET 8 SDK and an already installed WinApp CLI executable:

```powershell
powershell -ExecutionPolicy Bypass -File tools\test-agent-platform-v2.ps1 -RunNativeUiRecovery -WinAppCli winapp
```

The runner also performs its normal xUnit and Node.js browser test gates; they must pass independently. The xUnit selection includes the hermetic `ComputerUseGuiRecoveryContractTests` linking repair budget, UIA verdict, GUI proof and redacted receipt, which does not run native UI. The new `nativeUiRecovery` gate must remain NOT_RUN when the switch is absent and FAIL when any step cannot be executed. The fixture must be compiled, started with a per-run nonce, bound to its native PID/HWND, and reveal distinct Run/Reset/Result selectors in a fresh inspect tree. Required physical observations:

1. `initialNegative`: a wait-for ACCEPTED times out with structured found:false/timedOut:true; errors or non-JSON are never a valid negative.
2. `firstPositive`: a real Run UIA invoke updates the result and an independent wait-for proves ACCEPTED.
3. `resetPositive`: a separately observed Reset UIA invoke changes the result back to PENDING; a new assertion proves it.
4. `postResetNegative`: another independent negative probe proves ACCEPTED was invalidated by the reset, not carried over.
5. `recoveryPositive`: a new Run UIA invoke and new assertion prove recovery; no previous successful readback may be reused.
6. `finalReadback`: a final independent wait-for still confirms ACCEPTED on the original HWND/PID. Fixture is then stopped by the test script. Review the ignored local `artifacts/validation/.../native-ui-recovery/native-recovery-status.json` status file. `realAgentOrchestration` stays NOT_RUN by design.

**Actual PaRuX GUI agent acceptance is separate:** Enable Computer Use only on a disposable test profile, explicitly approve the fixture app, then ask a supported local model to test it. Make the first control assertion fail, permit a safe corrective UI action, require another exact assertion and a separate completion-time readback, and inspect the redacted receipt. Confirm at most two mismatches may consume recovery slots; repeated checks without delivered mutations, ambiguous selectors, invalid observation, STOP, revoked approval and step exhaustion must not produce a false PASS. Record the exact commit and real WinApp/FlaUI results, not a simulated/model-reported PASS.

**Current status:** all executable code and scripts committed; PowerShell AST parsing, .NET/xUnit, native UIA recovery and full agent-on-PC acceptance **NOT RUN** in this environment. UAI-07 remains PARTIAL.



### 2026-10-31 Core-side Computer Use receipt gate and Agents task card (NOT RUN)

1. Create a disposable, user-approved GUI test task and make its desktop worker submit a valid v1 receipt. Verify `VerifierService` admits it for **independent** evaluation, but the task remains `Verifying` until the actual verifier responds.
2. On an isolated Core test instance, intentionally replace the receipt with missing/malformed fields, inconsistent counts, a false `readyForIndependentVerifier`, an incorrect `guiTestRequired` value, or an incorrect root readback flag. Core must reject the task **before** asking its LLM verifier to vote PASS.
3. Confirm an original goal such as "Testa questa applicazione" is classified as GUI-testing by both desktop and Core. Add untrusted delegated context containing "test UI" to an ordinary "Open Notepad" goal and verify it cannot change the mandatory proof rule.
4. In Settings → Agents, inspect a `Verifying` task, a `Failed` task and a `Completed` task. Only the actual independent `verificationStatus=pass` and completed Core state may cause the UI to say the verifier reported PASS. The receipt itself must never be displayed as a goal-level PASS, and private raw ResultJson/text must not appear in the receipt line.
5. Test a mixed-version old desktop worker that submits no v1 receipt: Core must refuse verification, not silently accept it. Record the compatibility/migration impact. Run `ComputerUseResultVerifierGateTests` and `ComputerUseGuiReceiptPresenterTests` with the local xUnit suite; capture commit-specific PASS/FAIL/NOT RUN.

**Status:** Core gate/UI/schema/fixture source only, Windows/WinUI/FlaUI/executable acceptance NOT RUN. Metadata integrity checking is not a signed native UI-event attestation.

### 2026-10-31 redacted GUI run receipt, truth-in-verification (NOT RUN)

On a disposable Windows test profile and exact checked-out commit, configure a fake WinUI application with non-sensitive test controls and independently run the UIA/FlaUI acceptance scenarios:

1. Run an authorized GUI test with one delivered mutation, a verified `ui_assert`, and successful fresh final readback. The `guiRunReceipt` should show the actual bounded attempted/delivered action counts, assertions passed and final readbacks; its `outcome` must be `ready_for_independent_verifier` **not** `passed`. The orchestration task should transition to `Verifying` rather than `Completed` on the strength of this receipt alone.
2. Have Windows change the control between the first assertion and final readback. The receipt must count a failed final readback and the GUI task must not reach `Verifying` unless a new assertion and successful final readback occur using remaining authorized steps. On exhaustion, the status must be `Failed`.
3. Force an incorrect UIA value, duplicate selector, missing property, unavailable WinApp CLI, denied input, STOP and Gaming Mode. Inspect the typed receipt for `incomplete`, `unverified` or `cancelled` and nonzero failure counts only when an actual comparison was performed; a refused command is attempted but never delivered.
4. With a model summary intentionally containing synthetic secrets, paths, selectors, HWND/PID and window titles, check the `guiRunReceipt` JSON itself contains none of those values. Also review the **surrounding task result and logs separately**, since these legacy fields may contain model summaries; do not confuse redaction of the receipt with redaction of all task content.
5. Verify an ordinary non-testing task is not newly forced through UIA final readback. Compare worker receipt counts with the physical action log. Test two simultaneous tasks: their receipts must not share counters.
6. Execute `ComputerUseGuiRunTraceTests`, `ComputerUseGuiFinalProofTests`, the completion-gate tests and real FlaUI GUI tests; save per-suite PASS/FAIL/NOT RUN and commit provenance.

**Status:** source and hermetic policy tests committed, no Windows .NET compilation, xUnit, WinUI/FlaUI or physical acceptance run. A worker-produced receipt is **not** signed by a separate attestor and is not a full goal verdict.



### 2026-10-31 completion-time GUI assertion acceptance (NOT RUN)

Use a disposable, non-sensitive Windows WinUI test control and a configured WinApp CLI, with the agent held to an explicit user-approved Computer Use task:

1. Set a test control to a known value; run a fresh `ui_assert` and then `done`. The final new UIA observation must match the same HWND/PID, selector, property and value. Inspect the worker's redacted `guiFinalReadbackVerified` flag, and ensure the independent verifier receives the task only after a positive final readback.
2. Between a successful assertion and `done`, change the control externally without any PaRuX action. The earlier assertion count must not allow completion. With steps left, a new exact assertion is required; with the budget exhausted, the task must remain incomplete.
3. Repeat with a different foreground application, same-named control in another window, duplicated selector, missing property, failed WinApp CLI and disconnected/closed target. Nothing may pass based only on a screenshot or previous control evidence.
4. Cancel the task or turn Gaming Mode on during the final WinApp process. No post-cancellation completion, input or verifier admission is permitted. No raw expected/observed values or selectors may appear in task logs or the worker's serialized result.
5. Check an ordinary non-GUI task with no assertion: its existing completion/verifier route must remain intact. Run `ComputerUseGuiFinalProofTests`, `ComputerUseGuiTestEvidenceGateTests` and `ComputerUseCompletionGateTests`, plus a real GUI/FlaUI readback fixture. Record commit-specific PASS/FAIL/NOT RUN.

**Status:** implementation and hermetic tests added; Windows compilation, real .NET tests, FlaUI, WinApp CLI, cancellation and physical-PC checks **NOT RUN**. The control readback is independent of the model but is not independent evidence of full goal correctness.



### 2026-10-31 process-bound focus and app-lease acceptance (NOT RUN)

Run on a **disposable Windows test profile** using two harmless Win32 applications, separate top-level windows of one of them, and test fixtures only:

1. With only App A approved under Computer Use Settings, ask for `focus_window` on an unapproved App B whose title matches or contains the same words. Verify no focus change; confirm neither the title nor a window's claimed process name grants access.
2. Approve App B and repeat. Only the unique, OS-identified B window may focus; two approved windows with the same title must refuse as ambiguous. A second window from the current foreground PID may focus when it has an accessible, valid EXE identity.
3. Launch approved App A from the agent, then deliberately change the foreground to an unrelated app before the next `ui_set_value`, pixel action or `ui_assert`. The agent must stop without delivering the action or reporting GUI-test success. Repeat after a user retitles the correct window: handle, PID and EXE must still match.
4. Remove or replace an approved registration during an app launch, then attempt to continue. The task must fail closed; app processes already started must **not** be silently killed. Exercise STOP, Gaming and a cancelled launch as separate cases.
5. Open an approved single-instance application that relaunches through a stub or hands off to another process, plus a packaged UWP-style app. Record honest limitations and refused child-window transitions. Do not infer acceptance from screenshots.
6. Run the targeted `ComputerUseWindowFocusPolicyTests`, `ComputerUseTaskAppLeaseTests`, `ComputerUseAppLaunchPolicyTests` and real Windows/FlaUI integration tests. Record the exact HEAD, build result and PASS/FAIL/NOT RUN with redacted logs.

**Status:** policy/agent source committed; physical Windows build, tests, UI automation and security review **NOT RUN**. This is a per-run executable restriction, not yet a full process-tree sandbox or OS-enforced application-grant system.



### 2026-10-31 multi-step application launch and GUI-test completion gate (NOT RUN)

The exact `main` candidate must additionally prove, using a disposable Windows test installation:

- Register a test app in PaRuX's existing Computer Use allowlist. Ask Chat to **open that registered app, enter a safe test value, and verify the GUI**. The agent should use `open_app` (exact app name, no executable path) then UIA actions and fresh `ui_assert`; no green completion based only on `done`.
- Attempt unknown, duplicate alias, or injected command-like app names; verify **no process launched**. Attempt the same against an app installed but **not** approved in Settings; verify refusal. Test Notepad/Calculator built-in separately.
- Request `done` before a fresh assertion and immediately after another UI mutation: the test must remain incomplete; the model must either run a successful fresh assertion or exhaust its bounded steps with an unverified result.
- Confirm a non-test task is not accidentally blocked by the app-test gate. Compare actual UIA tree labels and fresh HWND/PID with the screenshot. Revoke the task while an app starts; verify no additional privileged input. Document that already launched user apps may remain open.
- Run `ComputerUseAppSelectionPolicyTests`, `ComputerUseGuiTestEvidenceGateTests`, `ComputerUseActionParserTests`, `WindowsUiAssertionPolicyTests`, `WindowsUiActionServiceTests` and the real FlaUI/WinApp app tests; retain pass/fail/NOT RUN by exact commit. Neither static checks nor unit fixtures alone are production acceptance.
- With WinApp CLI unavailable, show the blocked UIA capability truthfully; do not claim screenshot-only click/type is permitted when the current sensitive-action policy refuses unlabelled targets.

**Status:** implementation source + hermetic tests committed; actual .NET compilation, WinUI, FlaUI, WinApp CLI, app launch, Windows 10/11 and enterprise-app acceptance **NOT RUN**.



**Objective:** prove a user-approved local AI agent can *actually test* one or more disposable Windows applications, not merely press keys, produce screenshots or claim a successful result.

1. On a clean Windows x64 development install with **WinApp CLI explicitly configured by the operator**, launch a disposable WinUI test app and Notepad. Confirm per-task Computer Use approval, then collect the actual foreground HWND/PID and UIA control selectors.
2. Invoke and set one authorized test control, then use `ui_assert` on its exact `value`, `name` or `text`. Require a fresh matching foreground observation after inference; record real success with expected data redacted.
3. Deliberately change the control value: `ui_assert` must report failure, never `GoalReported` success. Place a duplicate selector in the same window, and an identically named control in another window: both the selector authorization and sensitive-action label lookup must refuse inappropriate matches.
4. Switch foreground process/window between screenshot and action, place a modal/overlay on top, disconnect UIA CLI, cancel/STOP, enable Gaming Mode, and close the target app: no unintended keystrokes, clicks or false verification. Test a second display with nonzero/negative screen coordinates.
5. Repeat on user-approved versions of Outlook, Excel, Teams and a browser **with test data and no real recipients** after their app-specific integration is implemented. Observe protected login/MFA/DLP/UAC and enterprise restrictions without bypass.
6. Run xUnit `WindowsUiAssertionPolicyTests`, `WindowsUiActionServiceTests`, `ComputerUseSensitiveActionPolicyTests`, `ComputerUseActionParserTests`, then app build and relevant FlaUI/real GUI assertions. Record PASS/FAIL/NOT RUN independently. A unit-test pass alone does not prove live Computer Use.

Status for the 2026-10-30 assertion/foreground-binding slice: **SOURCE IMPLEMENTED; all real Windows, WinUI/xUnit execution, FlaUI and physical-app checks NOT RUN**.

## Hardware diversity and small-screen acceptance (NOT RUN)

**Scope:** PaRuX desktop currently targets Windows 10/11 x64. "All PCs" means no hard-coded NVIDIA/16 GB VRAM assumptions across supported Windows hardware; it does not mean Linux, macOS, x86 or every model family will run on every machine. No row below is physically accepted merely because a unit test exists.

| Physical test PC | Minimum required evidence |
| --- | --- |
| CPU-only, 8 GB RAM | Small GGUF fits conservative RAM budget; no CUDA or Vulkan requirement; CPU-only speed warning; successful chat and clean stop |
| CPU-only, 16 GB RAM | Model selection and runtime detection without a dedicated GPU; download, load, chat and cancellation |
| AMD integrated/UMA (8–16 GB system RAM) | Shared GPU aperture is not added to RAM; Vulkan eligibility checked; correct smaller-model fallback and memory-pressure rejection |
| Intel integrated/Arc | Hardware discovery, Vulkan backend selection only if installed/eligible, fallback to CPU if not available |
| NVIDIA 8–16 GB | CUDA if eligible, memory-fit estimate, ordinary chat and media capability checks |
| AMD dedicated 8–16 GB | Vulkan eligibility and fit rather than assuming NVIDIA-only telemetry; no silent runtime installs |
| Multi-GPU or eGPU | Separate VRAM pools, explicit split only when supported, hot-plug/unplug recovery, no fabricated combined VRAM |
| eGPU disconnected while Settings is open | After cached hardware discovery expires, the selected backend and displayed VRAM reflect current hardware; a stale NVIDIA usage sample is not attributed to an AMD/Intel backend |
| Core stopped or text backend missing | UI still reports physical GPU identity if independently measured, but model fit falls back to CPU/RAM or Unknown; no false promise of GPU acceleration |
| NVIDIA plus AMD integrated GPU | Settings reports the Core-selected adapter, never assigns NVIDIA usage to Radeon or counts Radeon shared system memory as dedicated VRAM |
| Low-memory / low-VRAM / no GPU telemetry | Honest Unknown/CPU warnings, safe download choices, no automatic large-model allocation |
| Narrow / high-DPI laptop window (400–800 logical pixels) | Model Hub quantization picker and Gallery actions visible; navigation, Settings search and Create cancellation keyboard reachable |

For **each** platform: record OS build, memory, GPU identity/backend and *actual* detected capacity (not just model name), selected quantization, Core status, real inference success or refusal, cancellation, app restart and logs. Use a temporary test profile and redact paths, identifiers, access tokens and user data from any public issue. Test the same model at a normal and a constrained context length. Record `PASS`, `FAIL`, `SKIP` or `NOT RUN` per machine; do not infer hardware support from test fixtures.

Source-only regressions cover CPU/RAM fit, context-only metadata, shared UMA budgets and narrow layout. WinUI build, xUnit execution, FlaUI and physical hardware runs remain **NOT RUN** for these changes.

## 1. Clean-PC MSI installation and uninstall

### Purpose

Verify that the versioned x64 MSI installs PaRuX as a single per-machine product on a clean Windows 10/11 PC, and that uninstall removes every MSI-owned file, the Start Menu shortcut, and the machine PATH entry without touching mutable state under `%LOCALAPPDATA%\PaRuX`.

### Preconditions

- Windows 10 or 11, x64 build, with an administrator account.
- PaRuX is not already installed (no `C:\Program Files\PaRuX`, no "PaRuX" entry in Apps & features).
- The built MSI is staged locally (see `docs/WINDOWS_MSI_RELEASE.md` and `scripts/stage-msi-runtime.ps1` for the build/staging workflow).
- Elevated PowerShell 7 available for the install/uninstall commands.

### Steps

1. Build (or reuse) the current release MSI and confirm the staged file path, e.g. `artifacts\installer\PaRuX-<version>-win-x64.msi`.
2. In an elevated PowerShell, record a sentinel that proves the MSI must not own user state:

   ```powershell
   $sentinelDir = Join-Path $env:LOCALAPPDATA "PaRuX\_acceptance"
   New-Item -ItemType Directory -Path $sentinelDir -Force | Out-Null
   $sentinel    = Join-Path $sentinelDir "sentinel.txt"
   Set-Content -LiteralPath $sentinel -Value ("clean-pc " + [guid]::NewGuid())
   ```

3. Install silently (per-machine MSI):

   ```powershell
   msiexec /i "<path-to-MSI>" /qn /l*v "$env:TEMP\parux-install.log"
   ```

   Expected exit code: 0 or 3010 (restart required).

4. Verify installed layout under `C:\Program Files\PaRuX`:

   - `PaRuX.exe`
   - `PaRuX.Server\PaRuX.Server.exe` and `PaRuX.Server\PaRuX.Server.dll`
   - `Cli\parux.exe`
   - `UpdateHelper\PaRuX.UpdateHelper.exe`
   - `LICENSE.txt` and `THIRD_PARTY_NOTICES.md`
   - Start Menu shortcut: `%ProgramData%\Microsoft\Windows\Start Menu\Programs\PaRuX\PaRuX.lnk`
   - Machine PATH contains `C:\Program Files\PaRuX\Cli` (new shell session).
   - Sentinel file still exists with unchanged content.

5. Launch PaRuX from the Start Menu shortcut; confirm the desktop app starts. Close it.

6. Uninstall silently:

   ```powershell
   msiexec /x "<path-to-MSI>" /qn /l*v "$env:TEMP\parux-uninstall.log"
   ```

   Expected exit code: 0 or 3010.

7. Verify removal:

   - `C:\Program Files\PaRuX` is gone (or empty of MSI-owned files).
   - Start Menu shortcut removed.
   - Machine PATH no longer contains the `Cli` folder (new shell session).
   - Sentinel under `%LOCALAPPDATA%\PaRuX\_acceptance` still exists with unchanged content — the MSI must not remove legacy user state.
   - "PaRuX" no longer appears in Settings → Apps & features.

### Expected result

One clean per-machine install followed by one clean uninstall. All MSI-owned files, the Start Menu shortcut, and the PATH registration appear after install and disappear after uninstall. No user state under `%LOCALAPPDATA%\PaRuX` is created, modified, or removed by the MSI. MSI logs under `%TEMP%` show no fatal errors.

### Evidence to record

- MSI file name and ProductVersion used.
- Windows build (`winver`).
- Install log path and exit code.
- Uninstall log path and exit code.
- Screenshot of Apps & features showing PaRuX installed, and again after uninstall.
- Sentinel content before install and after uninstall (must match).
- `PATH` value in a fresh elevated shell after install and after uninstall.
- Date/time, operator, and machine identifier.

Status: NOT RUN

## 2. Signed update delivery, verification and rollback

### Purpose

Verify that a signed update envelope drives a correct MSI upgrade through `PaRuX.UpdateHelper`, and that a failed or tampered envelope is rejected before any file is touched.

### Preconditions

- PaRuX is already installed (see section 1).
- A newer release MSI and its signed `.update.json` envelope are staged locally.
- The release was built with a configured `PARUX_RELEASE_PRIVATE_KEY`; the matching public trust anchor is embedded in the build.
- Administrator access is available on the target PC.

### Steps

1. Confirm the installed version, then place the new MSI and its envelope file in a user-writable folder.
2. From an elevated PowerShell, launch the update helper:

   ```powershell
   & "C:\Program Files\PaRuX\UpdateHelper\PaRuX.UpdateHelper.exe" `
       --msi "<path>\PaRuX-<new-version>-win-x64.msi" `
       --envelope "<path>\PaRuX-<new-version>-win-x64.update.json" `
       --wait-pid 0
   ```

3. Observe the staged worker process verify the envelope signature against the embedded public key, re-verify the MSI size and SHA-256 against the manifest, confirm the version is strictly newer, then run `msiexec /i <staged-msi> /norestart`.
4. After the helper exits, open the staging directory under `C:\Program Files\PaRuX Update Staging\stage-<guid>\` and read `result.txt`. A successful run records `success msiexec=<code> version=<new-version>`.
5. Launch PaRuX and confirm the new version is reported.
6. Negative test: modify one byte of the MSI or the envelope and re-run step 2. The helper must exit non-zero and write `PaRuX.UpdateHelper.last-error.txt` under `%TEMP%` with the rejection reason; the installed version must be unchanged.
7. Negative test: supply an envelope signed with the wrong key, or with a `PackageUrl` pointing outside the exact GitHub release channel; the helper must reject it before staging.

### Expected result

A valid signed envelope upgrades PaRuX to the manifest version through Windows Installer with no manual MSI interaction. Any envelope that fails signature verification, manifest validation, size/hash checks, or the URL trust boundary is rejected before the MSI is copied into the protected staging area, leaving the installed product untouched.

### Evidence to record

- Installed version before the update and the manifest `Version` / `SourceCommit` from the envelope.
- Contents of `result.txt` from the staging directory after a successful run.
- Exit code of the helper for the success case and for each negative case.
- Contents of `PaRuX.UpdateHelper.last-error.txt` after a failed verification.
- Screenshot of the PaRuX version string after the update.
- Date/time, operator, and machine identifier.

Status: NOT RUN

## 3. First run, onboarding golden path and first chat

### Purpose

Verify that a fresh install launches into the guided onboarding screen, that the user can confirm or change the local-AI root, complete onboarding via "Get Started", and immediately start a first chat without any manual file-system setup.

### Preconditions

- PaRuX is installed (section 1) and no `%LOCALAPPDATA%\PaRuX` settings file exists yet (true first run).
- The default local-AI root (typically under `%LOCALAPPDATA%\PaRuX\LocalAI`) is writable by the current user.
- Internet access is available if the user intends to download a first model; offline is acceptable if only verifying the onboarding flow itself.

### Steps

1. Launch PaRuX from the Start Menu.
2. The main window appears with the navigation pane hidden and the `OnboardingView` visible (`MainWindow.xaml.cs` calls `ShowOnboarding()` when `SettingsStore.Current.Data.OnboardingCompleted` is `false`). Confirm the onboarding screen is the first content shown, not the chat page.
3. Observe the component-detection status. `OnboardingView` runs `RefreshComponentsAsync` on load, which performs a read-only detection of local components. The status text should read "Detection never installs or changes software." No files should be created or modified by detection alone.
4. Optionally change the local-AI root using the folder picker. The chosen path is stored in `LocalAiRootBox`. This does not apply immediately.
5. Optionally interact with the First Model section: `OnboardingView.FirstModel.cs` shows a recommended model with `FirstModelDetailText` and `FirstModelStorageText`. Clicking "Install" (`FirstModelInstall_Click`) raises `FindModelRequested` with the model's Hugging Face repo ID, which opens Model Hub. Clicking a candidate button (e.g. `FirstModelUseCandidateButton_<id>`) selects that candidate. You may skip this step if only verifying the golden path to chat.
6. Click the "Get Started" button (`GetStartedButton`, `OnboardingView.xaml` line 125). `GetStarted_Click` in `OnboardingView.xaml.cs:500` checks whether the shown local-AI root differs from the effective root; if so, it calls `ApplyLocalAiRootAsync` to apply the chosen folder. If the apply fails, an error message is shown in `LocalAiRootText` and onboarding stays open.
7. After a successful apply (or if no root change was made), `GetStarted_Click` sets `SettingsStore.Current.Data.OnboardingCompleted = true`, saves settings, and raises the `Completed` event.
8. `MainWindow.OnOnboardingCompleted` calls `ShowChat()` and enqueues `MaybeStartGuidedTour`. Confirm the navigation pane is visible again, the chat page is shown, and a guided tour (teaching tips) may appear pointing at sidebar controls.
9. Type a short message into the chat input and submit it. Confirm the message appears in the conversation. If no model is loaded, the app should display a prompt to select or install a model rather than crashing.
10. Close and relaunch PaRuX. Confirm the app goes directly to the chat page (not onboarding), because `OnboardingCompleted` is now `true`.

### Expected result

First launch shows onboarding with navigation hidden; component detection is read-only; "Get Started" applies any changed local-AI root, persists `OnboardingCompleted = true`, and transitions to the chat page with the guided tour. A first chat message can be submitted. Subsequent launches skip onboarding entirely.

### Evidence to record

- Screenshot of the onboarding screen on first launch (navigation pane hidden).
- Screenshot showing the component-detection status text.
- Screenshot of the chat page immediately after clicking "Get Started".
- Screenshot of a first chat message in the conversation.
- Screenshot of the second launch showing the chat page directly (no onboarding).
- Path of the settings file under `%LOCALAPPDATA%\PaRuX` and confirmation that `OnboardingCompleted` is `true`.
- Date/time, operator, and machine identifier.

Status: NOT RUN

## 4. Chat, model routing explanation, answering-model label and STOP

This check verifies that a chat turn in AUTO mode shows which local model answered, explains the routing decision in plain language without overstating it, and can be stopped mid-reply.

Preconditions

- PaRuX is running on the physical PC with at least one local model loaded and the chat page open.
- The model selector (`src/PaRuX.App/Views/ChatView.xaml`, `AutomationId=Chat_ModelSelector_ComboBox`) is set to AUTO.
- No cloud provider is required for the turn under test.

Steps

1. Enter a prompt that exercises local inference and send it.
2. While the reply streams, confirm the answering-model label updates: `src/PaRuX.App/Views/ChatView.ModelRouting.cs` subscribes to `ChatClient.Current.ModelRouted` (`OnChatModelRouted`) and renders the sanitized selected model name for the turn.
3. Click the "Why?" button (`src/PaRuX.App/Views/ChatView.xaml`, `AutomationId=Chat_ModelRoute_Explain_Button`). The panel is built by `ChatModelRouteExplanationBuilder.Build` (`src/PaRuX.App/Services/ChatModelRouteExplanationBuilder.cs`) from the `model_routed` control event emitted by `src/PaRuX.Server/ChatProxyService.cs` (`BuildModelRoutedEvent`) after `src/PaRuX.Server/SmartRouterService.cs` selects the model.
4. Read the explanation lines and confirm each one is framed as evidence rather than a quality claim: "Selected model", "Routing" (e.g. `AUTO · Balanced`), "Fallback", "Capability fit estimate" ("a router estimate, not a verified score"), "Speed evidence" ("stored benchmark evidence, not a live speed measurement"), and "Why it was chosen".
5. Start another turn that produces a long reply and, while it is still streaming, click STOP (`src/PaRuX.App/Views/ChatView.xaml`, `AutomationId=Chat_Stop_Button`; the button is only visible while busy, `src/PaRuX.App/Views/ChatView.xaml.cs`). `Stop_Click` calls `ChatSession.Current.Cancel()` (`src/PaRuX.App/Services/ChatSession.cs`), which cancels the session's `CancellationTokenSource` and halts the stream.
6. Send one more short turn in AUTO mode and repeat steps 2-4 to confirm the label and explanation track the selected model per turn.

Expected result

- After each local turn, the chat shows the model that actually answered, not a generic or stale label.
- The "Why?" explanation renders the routing mode/profile, fallback state, capability-fit and speed evidence, and the core reason, all worded as router estimates and stored benchmark evidence rather than verified quality scores.
- If no route has been recorded yet, the panel shows the "No route yet" guidance instead of inventing a reason.
- STOP immediately ends the in-progress reply; the UI returns to its idle state and a subsequent turn starts cleanly.

Evidence to record

- Screenshot of the chat after a turn showing the answering-model label.
- Screenshot of the open "Why?" explanation panel with all lines visible.
- Screenshot or short video showing the STOP button pressed mid-stream and the reply halting.
- Date/time, operator, machine identifier, and the selected AUTO profile.

Status: NOT RUN

## 5. Model Hub: search, download, pause and resume, Hugging Face token, split sets, import

Purpose: verify that the operator can browse Hugging Face models, control download lifecycle, and manage split or imported models.

Preconditions:
- PaRuX.App and PaRuX.Server are running.
- The machine has internet access to huggingface.co.
- A Hugging Face token is available for gated or private repositories.

Steps:
1. Open the Model Hub view.
2. Enter a known Hugging Face repository ID or search term and trigger a search via `ModelHubView.cs`.
3. Start a download for a suitable GGUF model.
4. While the download is running, click the Pause action (identified by `ModelDownloadPresentationPolicy.GetActionAutomationId("ModelHub", job.Id, "Pause")`).
5. Verify the download status changes to paused.
6. Click the Resume action (identified by `ModelDownloadPresentationPolicy.GetActionAutomationId("ModelHub", job.Id, "Resume")`).
7. Verify the download resumes and completes.
8. If the model requires authentication, configure the Hugging Face token in Settings and verify that private/gated repositories are visible.
9. Open a model with multiple parts (split set) and verify that the incomplete split sets section in `ModelHubView.cs` displays the part count and file paths.
10. Click "Import existing models…" (AutomationId: `ModelHub_ImportExternalModels_Button`) and select a local GGUF file to import into the managed library.
11. Verify the imported model appears in the managed library.

Expected results:
- Search returns matching repositories.
- Pause stops the download and updates the UI status.
- Resume restarts the download from the paused position.
- The Hugging Face token is correctly applied to authenticated requests.
- Split sets display correct part counts and file paths.
- External models are imported and listed in the library.

Evidence to record:
- Screenshots of search results, pause/resume states, and the imported model in the library.
- Log excerpts from `ModelDownloadService.cs` showing download start, pause, resume, and completion.
- Any error messages related to token authentication or split set rendering.

Status: NOT RUN

## 6. Runtime Manager, GPU backends, launch fallback ladder and Gaming hard-off

Purpose: verify that the operator can inspect and manage PaRuX-owned runtimes, that the text launch falls back through the bounded recovery ladder when the primary engine fails, and that Gaming Mode hard-off stops every local AI workload reliably.

Preconditions:
- PaRuX.App and PaRuX.Server are running.
- A GPU is present and detected (CUDA or Vulkan), or the machine is CPU-only.
- At least one text model is installed.
- Gaming Mode integration is available on the machine.

Steps:
1. Open the Runtime Manager view (`RuntimeManagerView.cs`).
2. Click Refresh and verify that the detected adapters, runtime components and performance summary are displayed.
3. Start a text chat turn with a model that requires GPU offload so the primary engine is used.
4. Observe the runtime status line in the chat or server trace; if the primary engine fails, confirm that the launch ladder (`TextLaunchLadder.Plan`) walks through the rungs in order: Primary, DropFlashAttention, ReduceOffload, VulkanEngine (CUDA primary only), CpuOnly.
5. Verify that the status trace records each rung label and the final outcome, and that the ladder is capped at `TextLaunchLadder.MaxRungs` attempts.
6. While the model is resident, enter Gaming Mode from the Settings view.
7. Verify that `GamingGuardService` drives the shutdown: the runtime status reports the pending shutdown, and the retry loop in `GamingShutdownRetry` re-attempts release at the fixed interval until every local AI process has stopped.
8. Confirm that no new AI request can start while Gaming Mode is active: chat, media generation and performance probes are all refused.
9. Exit Gaming Mode and verify that the model is not silently reloaded until the next user message.

Expected results:
- The Runtime Manager displays accurate adapter and runtime component status.
- A failed primary launch is recovered by the next ladder rung without operator intervention, or reported as a definitive failure after the final rung.
- The ladder trace appears in the runtime status message.
- Gaming Mode hard-off stops all local AI workloads, the retry loop keeps trying until release is confirmed, and no AI workload starts while the marker is active.

Evidence to record:
- Screenshot of the Runtime Manager view after Refresh showing detected hardware and components.
- Server log excerpt showing the launch ladder rungs attempted for a failed GPU start.
- Server log excerpt showing the Gaming Mode shutdown retry loop and final release confirmation.
- Screenshot of the runtime status line after Gaming Mode exit, confirming the model is not yet reloaded.
- Date/time, operator, machine identifier, and the active GPU backend.

Status: NOT RUN

## 7. Image, video and voice creation: cancel, crash recovery and resume

Purpose: verify that a media job started from the Create workspace can be cancelled by the operator, survives a server restart as a durable record, and that an interrupted job is either resumed or shown as interrupted rather than silently lost.

Preconditions

- PaRuX.App and PaRuX.Server are running on the physical PC.
- A ComfyUI backend is reachable for image/video jobs, and a TTS voice is available for speech jobs.
- The Create workspace view is open (`src/PaRuX.App/Views/CreateWorkspaceView.cs`), and the operator can reach the server state directory where `media-jobs.json` is written.

Steps

1. From the Create workspace, start an image job. Confirm the run buttons lock and the status line shows the job running (`CreateWorkspaceView.BeginOperation`).
2. While the job is running, click "Cancel active job" (AutomationId `Create_Cancel_Active_Job_Button`). The status line should read "Cancellation requested…" and the job should stop; the run buttons unlock once no job is active (`CreateWorkspaceView.CancelActive`).
3. Start another image job and, mid-generation, terminate the PaRuX.Server process to simulate a crash.
4. Relaunch PaRuX.Server. `MediaJobService` reloads records from `media-jobs.json` (`MediaJobStore.Load`) and reattaches to any ComfyUI prompt still running; a record that is still in a non-terminal state should surface in the job list as interrupted or running, not disappear.
5. Open the Create workspace again. Confirm the interrupted job is visible with its status and that starting a new job is refused until the running one is cancelled or finished ("A Create job is still running. Cancel it first, or wait for it to finish.").
6. Start a speech (TTS) job and cancel it mid-synthesis; confirm the status reads "Speech was stopped; no file was kept." (`CreateWorkspaceView`).
7. Inspect `media-jobs.json` in the server state directory. Confirm it holds at most 64 records (`MediaJobStore.MaximumRecords`) and that a record carries `Status`, `Stage`, `PromptIds`/`ClientIds` and, for video jobs, the bounded clip plan (`MediaJobRecord`).
8. Corrupt `media-jobs.json` (e.g. truncate it) and restart PaRuX.Server. `MediaJobStore.Load` must move the unreadable file aside to a `media-jobs.json.damaged-*.bak` and start with an empty job list rather than crashing.

Expected result

- A running media job can be cancelled from the UI and stops, with the workspace returning to an idle state.
- A job interrupted by a server restart is persisted in `media-jobs.json`, reloaded on startup, and shown as interrupted/running so it can be resumed; it is not silently dropped.
- The store keeps at most 64 records and a damaged state file is quarantined to a `.damaged-*.bak` file, after which recovery starts empty.

Evidence to record

- Screenshot of the Create workspace showing a job running, then the "Cancellation requested…" status after clicking the cancel button.
- Screenshot of the Create workspace after a crash-and-relaunch showing the interrupted job and its status.
- Excerpt of `media-jobs.json` showing the record fields (`Status`, `Stage`, `PromptIds`, `ClientIds`) for the interrupted job.
- Screenshot or file listing showing the `media-jobs.json.damaged-*.bak` file after the corrupted-file test.
- Date/time, operator, machine identifier, and the media type (image, video or voice) under test.

Status: NOT RUN

## 8. Coding agent on a disposable repository: plan, edit, review, apply, rollback

Purpose: verify that the local coding agent can plan, edit, verify and roll back changes inside a disposable clone of the repository under controlled permission modes, without touching the operator's main workspace.

Preconditions

- PaRuX.App and PaRuX.Server are running on the physical PC.
- A disposable clone of the target repository exists on local disk and is open in a PaRuX conversation whose working path points at the clone.
- The operator can choose the coding run permission mode from the conversation's coding handoff dialog (`src/PaRuX.App/Views/ChatView.NativeCodingHandoff.cs`).
- Git is available for inspecting the diff and resetting the clone.

Steps

1. In the conversation, start a coding task against the disposable repository. When prompted, choose "Plan — read-only actions" (`CodingAgentPermissionMode.Plan`) and confirm. The agent should produce a plan without writing files or mutating state; verify with `git status` and `git diff` that the worktree is clean.
2. Start a second coding task in the same clone and choose "Ask — approve commands and file changes" (`CodingAgentPermissionMode.Ask`). Approve the exact commands and file mutations the agent proposes, then let it finish. Confirm in the chat that the agent reports `VerifiedByCommand` or equivalent evidence for the verification it ran.
3. Inspect the resulting diff with `git diff` in the disposable clone and review the changed files. Confirm the change set matches the task and that no unrelated files were modified.
4. If the result is acceptable, keep the changes. If not, roll the disposable clone back to its pre-task state with a normal git reset or by re-cloning, then confirm `git status` is clean.
5. Repeat the task once in Plan mode only and record that no file writes, package restores or mutating shell commands were executed.

Expected result

- In Plan mode the agent only reads and plans; the worktree remains clean.
- In Ask mode every command and file mutation is gated by an explicit user approval bound to the exact content.
- A finished edit produces a reviewable git diff and, when the agent runs a verification command, the checkpoint records it as an executed check.
- The disposable clone can be rolled back to a clean state without affecting the operator's main repository.

Evidence to record

- Screenshot of the permission-mode dialog showing the selected mode.
- `git status` / `git diff` output before and after the Plan-mode run (clean) and after the Ask-mode run (expected changes only).
- Excerpt of the chat showing the agent's plan, the approvals requested in Ask mode, and the verification evidence reported at completion.
- Screenshot or terminal capture of the rollback (git reset / re-clone) and the resulting clean `git status`.
- Date/time, operator, machine identifier, and the name of the disposable repository under test.

Status: NOT RUN

## 9. Android phone pairing and remote workflows

Purpose: verify that a real Android phone can pair over the local HTTPS LAN boundary, store the credential with Android KeyStore, and execute a remote Chat, a media generation, and one Computer Use task through `PaRuX.Server` while the workstation stays in the foreground. Android never receives raw Windows filesystem, shell or input privileges; every remote operation is mediated by the paired-device allowlist in `PaRuX.Server` and must respect the workstation's Gaming Mode hard-off.

Preconditions

- PaRuX is installed and running on the physical Windows PC; LAN access is enabled on one explicit private address via **Settings → Local Network** (see `src/PaRuX.App/Views/SettingsView.xaml.cs` and `src/PaRuX.Server/Program.cs`).
- A real Android 14/15 phone with the PaRuX Android release APK installed, on the same private network as the workstation.
- Camera permission granted to PaRuX Android for QR pairing; a manual endpoint/code/fingerprint entry path is available as fallback.
- The Android release build was produced by the repository Gradle pipeline (`src/PaRuX.Android`) so the production pairing, credential store and pinned-TLS HTTP client are exercised.

Steps

1. On the Windows PC, enable LAN access for one private IPv4 address. Confirm the confirmation dialog in `SettingsView.xaml.cs` shows the exact address and that Windows Firewall is set to Private networks only.
2. Click **Pair device** in the Local Network panel. Record the pairing code, expiry (5 minutes), endpoint and certificate SHA-256 fingerprint shown next to the QR (`RemoteAccessClient.StartPairingAsync` → `POST /api/v1/remote/pair/start`, `src/PaRuX.Server/Program.cs:538`).
3. On the phone, open PaRuX Android and scan the QR with the in-app camera. Confirm the app decodes the JSON payload locally (ZXing Core) and presents the endpoint/code/fingerprint for review.
4. Complete pairing. The phone pins the certificate fingerprint and stores the single-use device token encrypted with AES-256-GCM under an AndroidKeyStore non-exportable key (`DeviceCredentialStore` per `docs/PARUX_ANDROID_README.md`). Confirm no plaintext token or endpoint appears in app logs or SharedPreferences.
5. Open the paired PC from the Android home screen. Run a short Chat turn with the AUTO profile. Verify the reply streams locally from the workstation's model and that no cloud inference path is contacted.
6. Start a media generation (image or short video) from the phone and wait for completion. Cancel one running job to verify the cancellation path over the LAN. Confirm the resulting artifact is downloadable from Android and saved via `FileProvider` without exposing raw Windows paths.
7. Request a Computer Use task from Android. Confirm that Android only creates a durable `computer-use` AgentTask on the PC and that the normal task-scoped **Approve once** gate is required on the Windows side unless persistent Full Computer Use was pre-enabled. The phone must show a waiting/approval state, not perform any local tap or input.
8. On the Windows PC, enable Gaming Mode. Repeat one of the remote operations from step 5-7 and confirm Android receives the hard-off refusal and that no new task is queued.
9. On the Windows PC, revoke the paired device from **Settings → Local Network** (`DELETE /api/v1/remote/devices/{id}`). On the phone, attempt another operation and confirm the server returns HTTP 401/403 and the app refuses to fall back to any unauthenticated path.
10. Re-pair once using the manual entry path (type endpoint, code and fingerprint) to verify the fallback flow works without a camera.

Expected result

- Pairing succeeds over HTTPS on the explicit private address only; no wildcard, no public listener, no firewall port opened by PaRuX.
- The credential blob on Android is ciphertext-only; the AES key never leaves `AndroidKeyStore`.
- Chat, media generation, cancellation and Computer Use all complete through the paired-device allowlist, with no cloud inference and no raw Windows paths exposed to Android.
- Gaming Mode hard-off is honored by the server for every remote surface.
- Revocation takes effect immediately; the phone is refused with an authentication error and does not retry against a different credential.
- Manual pairing works as a fallback when the camera cannot be used.

Evidence to record

- Windows: screenshot of the Local Network panel with LAN enabled, the pairing code/QR, and the certificate fingerprint.
- Windows: screenshot of Gaming Mode enabled, then the refusal response shown in Android.
- Windows: screenshot of the device list before and after revocation.
- Android: screenshot of the pairing confirmation screen showing endpoint, code and pinned fingerprint.
- Android: screenshot of a Chat turn, a media job progress bar, and the saved artifact in the gallery.
- Android: screenshot of the Computer Use task in the waiting/approval state.
- Android: screenshot of the hard-off refusal and of the 401/403 refusal after revocation.
- Date/time, operator, machine identifier, Android build number, and the exact LAN address used.

Status: NOT RUN

## 10. Display scaling 100, 125 and 150 percent, narrow windows, keyboard-only navigation

### Purpose

Confirm that the PaRuX window renders correctly at 100, 125 and 150 percent display scaling, that its layout remains usable when the window is narrowed, and that the primary controls remain reachable by keyboard alone.

### Preconditions

- Windows 10 or 11 x64 with the built PaRuX installed and at least one model available for chat.
- Display scaling can be set to 100, 125 and 150 percent (Settings → Display, or a multi-monitor setup with mixed scaling).
- A physical keyboard is connected.
- The automated suite `tests/PaRuX.UITests/Tests/NavigationLayoutReachabilityTests.cs` (size matrix 560×560 / 800×720 / 1400×900) and `tests/PaRuX.UITests/Tests/SmokeTests.cs` (`Chat_header_adapts_and_recent_history_is_bounded_when_the_window_is_resized`) pass on the same machine, so this manual run only has to add the human judgement the suite cannot express.

### Steps

1. Set display scaling to 100 percent. Log out and back in so the change applies. Launch PaRuX and open Chat with a profile selected.
2. Repeat the launch at 125 percent and at 150 percent, each time after a logoff/logon.
3. At 150 percent, resize the window to roughly 560 pixels wide (drag the right edge). Check that the navigation items, the chat title and the profile selector stay visible and on-screen; the chat header stacks the profile selector below the title when the width is reduced (see `src/PaRuX.App/Views/ChatView.ResponsiveHeader.cs`).
4. Resize the window to about 800 pixels wide, then back to a wide size (1400 pixels or more). The profile selector returns to the inline position next to the title.
5. From the title bar, press `Alt` and then `Tab` to a focused control; from there, navigate the entire top-level UI using only `Tab`, `Shift+Tab`, the arrow keys and `Enter`/`Space`. Verify every left-nav item, the chat header controls, the composer and the profile toggles receive focus in a logical order, and that no control is only reachable by mouse. The automated suite already proves keyboard activation of the navigation items (`MainWindowPage.Click` falls back to focus + `Space` when a control has no reliable UIA point).

### Expected result

No clipped, overlapping or off-screen control at 100, 125 or 150 percent; the header and the composer reflow instead of truncating; and every primary control is reachable and operable with the keyboard alone, with a visible focus indicator on the focused element.

### Evidence to record

- A screenshot at each of the three scaling levels with the window at roughly 560 pixels wide.
- A screenshot at 150 percent with the window narrowed, showing the stacked header.
- A screenshot at 150 percent with the window wide (1400 pixels or more), showing the inline header.
- A short recording (or a sequence of screenshots) of the keyboard-only walk: title bar → navigation → chat header → composer → profile toggles, with the focus indicator visible.
- The display scaling value, screen resolution, and the window width in pixels used for each screenshot.
- Date/time, operator, and machine identifier.

Status: NOT RUN

## 11. Strict-local network egress capture

### Purpose

Capture, on the physical PC, direct evidence that a PaRuX chat or agent run sends no traffic anywhere except loopback, so that core inference is demonstrated to stay on this machine rather than inferred from code inspection or unit tests.

### Preconditions

- The automated egress gates already pass: `tests/PaRuX.Server.Tests/CloudFallbackTests.cs`, `tests/PaRuX.Server.Tests/StrictLocalInferenceIntegrationTests.cs` and `tests/PaRuX.Server.Tests/ManagedListenerOwnershipTests.cs` are green on the same build.
- An elevated PowerShell session is available (the elevated packet-capture layer in `docs/STRICT_LOCAL_EGRESS_EVIDENCE.md` cannot start from a non-elevated shell).
- One model is available locally and at least one full chat or agent run can be completed offline.
- The non-elevated sampler `scripts/sample-process-egress.ps1` is present and its `-SelfTest` proves the detector on this machine before use.

### Steps

1. Run the detector self-test first: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\sample-process-egress.ps1 -SelfTest`. It must flag a child dialing the TEST-NET address 192.0.2.1 and pass a loopback-only child, proving the sampler works here before any real run is trusted.
2. Start the non-elevated sampler against the server process for the whole workload, for example `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\sample-process-egress.ps1 -ProcessName PaRuX.Server -DurationSeconds 120 -OutFile .ai\artifacts\egress.json`, covering the full duration of one chat or agent run.
3. In an elevated PowerShell, start the packet capture exactly as described in `docs/STRICT_LOCAL_EGRESS_EVIDENCE.md`: `pktmon filter remove; pktmon start --capture --pkt-size 0 -f C:\path\egress.etl` (or the `netsh trace` equivalent), then run the same chat/agent workload against PaRuX Core with no internet-enabled tools.
4. Stop the capture (`pktmon stop`), convert it (`pktmon etl2txt egress.etl -o egress.txt`) and inspect every packet attributed to the PaRuX server/runtime processes.
5. List any name-resolution or other traffic produced by processes outside the PaRuX server and runtime; it is out of scope for the inference-egress claim and must be recorded, not hidden.

### Expected result

The sampler verdict is `LOOPBACK_ONLY_AT_SAMPLED_INSTANTS` with no non-loopback entries, and the elevated capture shows every packet attributed to the PaRuX server/runtime process with loopback source and destination; the chat or agent run completes normally throughout.

### Evidence to record

- The `egress.json` from `scripts/sample-process-egress.ps1` including its `method` field and the self-test output.
- The `egress.etl` and converted `egress.txt` from the elevated `pktmon`/`netsh trace` capture, plus the exact start/stop commands used.
- The workload description (chat or agent), model, start/end timestamps, operator and machine identifier.
- Any out-of-scope traffic listed separately from the PaRuX-attributed packets.

Status: NOT RUN

## 12. Backup, restore and data-safety checks

### Purpose

Verify that the "Backup & Restore" feature round-trips the user's own data on this physical machine and that a restore is safe to run: it replaces only what the archive covers, leaves excluded material untouched, and can roll back if the restore is interrupted. The archive is a single .zip built by `src/PaRuX.Server/BackupService.cs` (`ExportAsync`/`ImportAsync`) covering conversation history, Memory entries, RAG folder configuration, desktop app settings and the agent/task SQLite databases. It deliberately excludes downloaded models, runtimes, RAG-indexed file contents and artifact binary payloads.

### Preconditions

- One recent export already exists on this machine (Settings → Backup & Restore → Export), so the restore step has a real archive to import.
- The machine has a few conversations, Memory entries and at least one agent/task store with content, so a restore has something to restore.
- Enough free disk space for a second copy of the data root (%LOCALAPPDATA%\PaRuX), since restore snapshots the current state before replacing it.

### Steps

1. Open Settings → Backup & Restore and export; confirm the .zip is written and note its size.
2. Change something small and durable (e.g. a setting or a new Memory entry) after the export, so a successful restore visibly rolls that change back.
3. Import the exported .zip via the same panel and let the restore complete.
4. Verify the post-export change is gone (restore worked) and the pre-export state is back (conversations, Memory and settings match the backup).
5. Confirm the excluded material was not touched: the models directory and RAG-indexed files are unchanged in size and content.
6. (Optional, if time allows) start a restore and interrupt it, then confirm the automatic rollback brings the data root back to its pre-restore state.

### Expected result

The export produces a valid .zip; the import restores the covered stores and the post-export change is reverted; models, runtimes and RAG file contents are untouched; an interrupted restore rolls back without corrupting the data root.

### Evidence to record

- The exported .zip filename and size, plus the timestamp of the export.
- The exact post-export change made in step 2 and its state after the restore (rolled back or not).
- The restore result reported by the app (success, item counts, or error text).
- File listing/sizes of the models directory and RAG-indexed folder before and after the restore.
- If run, the interruption/rollback evidence: how the restore was stopped and the state of the data root afterwards.

Status: NOT RUN

