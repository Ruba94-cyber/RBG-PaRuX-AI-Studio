# PaRuX WorkloadOrchestrator & Gaming Resource Sharing

## Overview
The **WorkloadOrchestrator** subsystem in PaRuX AI Studio transforms a Windows PC into an always-available local AI server that automatically yields GPU VRAM, CPU cycles, and audio/input resources to local or remote gaming sessions, and restores AI workloads when gaming ends.

---

## Key Features

1. **State Machine (`WorkloadOrchestrator`)**:
   - `AiReady`: Standard operating mode where LLM (llama.cpp / ExLlamaV3), ComfyUI (diffusion generation), and TTS/STT run natively.
   - `EnteringGaming`: Detects launcher/display/stream triggers, suspends active AI jobs, and unloads model weights from VRAM.
   - `GamingLocal` / `GamingRemote`: PC is dedicated to gaming (Playnite, Steam Big Picture, Sunshine stream).
   - `GameSuspended`: Quick Resume mode where game processes are suspended via Windows Native (`NtSuspendProcess`) or Nyrna.
   - `ExitingGaming`: Entered on an explicit user "Return to AI" or when a remote (Sunshine) session's grace period expires. There is no automatic game-process-exit detection: when a local game quits, Gaming Mode stays on until the user returns to AI.
   - `RestoringAi`: Lifts the hard-off. The runtime is reloaded only on an explicit "Return to AI" or when the profile sets `restoreAiOnExit`; paused AI jobs and downloads never resume automatically.

2. **Pluggable Modular Providers**:
   - **Triggers**: `ManualTriggerProvider`, `WindowsDisplayTriggerProvider`, `SunshineSessionTriggerProvider`.
   - **Launchers**: `PlayniteLauncherProvider`, `SteamBigPictureLauncherProvider`, `CustomExecutableLauncherProvider`, `NoneLauncherProvider`.
   - **Input & Display**: `NativeInputProvider`, `XOutputInputProvider`, `SunshineInputProvider`, `CustomExecutableInputProvider`.
   - **Suspend / Quick Resume**: `WindowsNativeSuspendProvider` (Win32 P/Invoke `NtSuspendProcess`), `NyrnaGameSuspendProvider`, `NoneGameSuspendProvider`.

3. **Quick Resume & VRAM Recovery**:
   - Game processes can be frozen instantly using Win32 kernel primitives.
   - Post-suspension VRAM allocation is tracked using `nvidia-smi` / DXGI hardware probing.
   - AI workloads are safely restored if sufficient VRAM remains.

4. **REST API Endpoints (`/api/v1/gaming/*`)**:
   - `GET /api/v1/gaming/status`: Current state, active game PID/name, VRAM usage, and active profile.
   - `POST /api/v1/gaming/enter`: Request entry into gaming mode.
   - `POST /api/v1/gaming/exit`: Return PC resources to AI server.
   - `POST /api/v1/gaming/suspend`: Freeze current game process.
   - `POST /api/v1/gaming/resume`: Unfreeze game process.
   - `GET /api/v1/gaming/profiles`: List available profiles.
   - `POST /api/v1/gaming/profiles`: Save custom profile.
   - `GET /api/v1/gaming/providers`: List detected system providers.

5. **WPF User Interface (`SettingsView`)**:
   - Dedicated "Gaming & AI Sharing" rail in Settings.
   - Real-time status badge with state indicator.
   - Quick control buttons (`Enter Gaming`, `Return to AI`, `Suspend Game`, `Resume Game`).
   - Profile selection dropdown ("Local Console", "Remote Moonlight/Sunshine", "Custom").
   - System provider detection matrix.

---

## Profile Configuration Schema

```json
{
  "id": "local-console-default",
  "name": "Local Console (Playnite / Steam)",
  "sessionType": "local",
  "triggerProvider": "display",
  "launcherProvider": "playnite",
  "inputProvider": "xoutput",
  "suspendProvider": "windows-native",
  "quickResumePolicy": "always",
  "gracePeriodSeconds": 30,
  "restoreAiOnExit": true
}
```

---

`restoreAiOnExit` (default `false`) is the explicit resume policy: when a session ends without the user asking (remote grace-period expiry, failed-entry rollback) the Gaming marker is lifted but the AI runtime is not reloaded; it loads lazily on the next request. An explicit "Return to AI" through the orchestrator (`POST /api/v1/gaming/exit`) restores the runtime eagerly; the "Exit Gaming Mode" button in Settings and the chat header "Return to AI" button lift the hard-off and let the model load lazily on the next message. Tuning, benchmarks and runtime races are cancelled as soon as Gaming Mode starts and never resume afterwards.

## Safety & Non-Destructive Directives
- Games are **never** forcefully killed without explicit user configuration or interactive user UI action.
- Grace periods allow clean session state saving before resource reallocation.
