# PaRuX Android

PaRuX Android is the native Kotlin/Jetpack Compose companion for a **paired PaRuX Windows workstation**. It is a secure LAN control plane, not a second AI workstation: model inference, coding, filesystem work, training, runtime installation and Computer Use execute on Windows through **PaRuX Server**.

Source: `src/PaRuX.Android/`. Wire/security boundary: [PARUX_ANDROID_API.md](PARUX_ANDROID_API.md).

## Product boundary

Android stays aligned with the Windows product at the **high-level workflow/policy layer**, while remaining intentionally more limited where an operation requires workstation paths, runtime installation, benchmark control or direct OS authority. Both clients use the same authoritative Core state rather than maintaining independent copies.

Android may request or supervise PaRuX Server-owned work, but it does not receive raw Windows filesystem, shell, mouse or keyboard privileges. The APK deliberately declares **no AccessibilityService**. A Computer Use request from Android creates a durable `computer-use` AgentTask on the paired PC; the Windows worker still requires the normal task-scoped **Approve once** gate unless persistent Full Computer Use was explicitly enabled on Windows.

The paired-device server policy is default-deny. Adding a new endpoint to PaRuX Server does not automatically make it remotely callable: it must be deliberately admitted by `PairedDeviceEndpointPolicy`. New V2 parity uses dedicated `/api/v2/mobile/*` projections rather than exposing path-bearing desktop V2 responses.

## Implemented surfaces

- **Chat** — streaming local replies, Standard/Uncensored profile, intelligence-aware AUTO/manual model routing, per-turn routed-model evidence, image attachments and synced conversations/Project scope.
- **Media / Jobs** — generate image/video work through PaRuX Server, progress/cancel, authenticated range-capable playback/download, save to gallery and share sheet.
- **Live Voice** — microphone/VAD → local Parakeet STT on the PC → scoped Chat → local Kokoro TTS → Android playback/barge-in.
- **Projects / PC Studio** — list/create Projects, attach/supervise durable AgentTasks, read logs/results/diffs/artifacts/Project Knowledge and continue work on the workstation.
- **Goal Mode V2** — define immutable acceptance criteria on Android, create the same versioned Goal specification used on Windows, launch the bound Windows task and read independent verification evidence. Android cannot self-grant task capabilities.
- **Execution Profiles** — read/change the same named `Fast`, `Balanced`, `Maximum Quality` and `Overnight` policy used by Windows and see its effective preferred context/reasoning envelope. Per-model tuning remains desktop-owned.
- **Observability V2** — sanitized workstation KPI/status/timeline and compact Engine Race summary, without raw benchmark/model paths.
- **Computer Use** — request/supervise a Windows `computer-use` AgentTask only. Android itself performs no taps/typing/scrolling and has no Accessibility privilege.
- **Models** — search/download managed GGUFs through PaRuX Server using source-issued metadata, select shared AUTO/manual Chat preference and see sanitized routing evidence. Raw workstation model paths and manual benchmark authoring remain desktop-only.
- **Tools** — Memory, existing RAG sources, sanitized mobile Skill creation, Research, Presentations, Speech, Avatar/3D generation after desktop engine setup, agent/Autopilot supervision, Workflow runs, existing Automation enable/disable/delete and read-only Context Graph inspection.
- **Gaming Mode / Universal STOP** — remote work obeys the workstation's hard-off state; Android cannot override Gaming Mode.

Desktop-only by design: runtime/component installation or repair, arbitrary Windows paths, Training Lab job authoring, manual benchmark/Engine Race control, plugin installation/trust, per-model Execution Profile overrides, Semantic Index/Visual Context/Virtual Context authoring, MCP administration, backup/restore, Local API administration, generic task-status mutation, Context Graph writes and direct OS input.

## Pairing, transport and credential storage

1. In PaRuX Windows open **Settings → Local Network** and enable LAN access on one explicit private address.
2. Create a short-lived pairing code/QR.
3. On Android scan the QR. CameraX captures frames and ZXing Core decodes QR locally on-device. A manual endpoint/code/fingerprint entry path remains available when a camera cannot be used.
4. Android pins the workstation certificate fingerprint and receives one unique revocable device token.
5. `DeviceCredentialStore` serializes endpoint/token/fingerprint/device name into one versioned credential payload and encrypts it with **AES-256-GCM**.
6. The AES key is generated directly inside `AndroidKeyStore`, is non-exportable, and never appears in SharedPreferences or the APK. The preference file stores only the versioned ciphertext blob plus random GCM IV; PaRuX-specific AAD binds the ciphertext format.

The deprecated `androidx.security:security-crypto` / `EncryptedSharedPreferences` path has been removed. Existing legacy encrypted preference blobs are invalidated fail-closed during upgrade: the user performs one fresh QR/manual pairing instead of PaRuX retaining the deprecated crypto stack or copying credentials through plaintext. Application backup is disabled (`android:allowBackup="false"`), so credential ciphertext/migration state is not restored onto another device.

The LAN listener is HTTPS only. The Android release manifest sets `usesCleartextTraffic=false`. There is no cloud relay, wildcard public listener, UPnP/NAT setup or proprietary cloud inference path.

## Media/file safety

Android's `FileProvider` is non-exported and restricted to `cache/shared-media/`; the rest of the cache cannot accidentally be shared. Workstation Artifact downloads are resolved server-side from PaRuX Server-owned Artifact ids and expose sanitized filenames rather than arbitrary Windows paths.

## Building locally

Requirements: JDK 17+, Android SDK with compile/target SDK 35.

The authoritative Windows release gate includes Android companion software validation. Standalone equivalent:

```powershell
cd src\PaRuX.Android
.\gradlew.bat --no-daemon clean test lint assembleDebug assembleRelease
```

This is the mandatory software Android boundary: JVM/unit tests, lint, debug packaging and release packaging must all succeed. `connectedAndroidTest` is optional emulator/device integration QA because it needs a runtime target; it is not a second physical release gate. Release minification remains enabled. A public signed APK/AAB still requires the owner's Android signing keystore, but signing secrets are publication credentials and not source-completeness requirements.

### Android release SBOM

Generate the resolved Android **release** dependency inventory from repository root:

```powershell
.\scripts\generate-sbom.ps1 `
  -Scope android `
  -OutputPath .\artifacts\sbom\SBOM.android.cyclonedx.json `
  -ProductVersion 1.5.0
```

This reads Gradle `releaseRuntimeClasspath`, not a manually curated dependency list. Inspect the generated graph before release; removed direct dependencies such as `androidx.security:security-crypto` and the unused DataStore declaration must not reappear as direct app dependencies. The Windows MSI uses a separate Windows-scoped SBOM.

## Validation truth

Android is part of PaRuX **software completeness** as a companion. The Windows release gate must build/test/lint both debug and release Android artifacts together with Windows/Core/VS Code.

On 2026-10-03, three instrumented workflows passed against a real isolated Core from an isolated Android 15 emulator, using the production pairing, credential and pinned-TLS HTTP client: pairing/refusals, generation, cancellation/recovery, artifact download and desktop-permission refusal; credential reuse after Core restart; and refusal after device revocation. Six direct Android Keystore tests also passed. These checks cover the tested transport workflows, rather than every workspace or physical-device behavior. Final release validation must use the committed release build.

Optional integration/exploratory QA from the final Windows PC may additionally exercise:

- emulator `connectedAndroidTest` when an AVD is available;
- pairing/manual pairing, certificate pinning and credential persistence;
- Chat/AUTO routing, Execution Profile parity, Goal Mode V2, sanitized Observability V2, PC Studio, media/jobs, Projects/Knowledge and notifications;
- cross-device Computer Use task creation/approval/result flow;
- on a real phone when convenient: camera QR, headset/microphone/speaker Live Voice, Android OEM background/power behavior and real Wi-Fi roaming/reconnect edge cases.

These checks improve confidence but **a physical Android phone is not a blocking acceptance prerequisite** for the Windows reference product. No Android source feature is intentionally left pending by this document. If optional emulator/phone QA later finds a defect, it becomes a normal source bug to fix; it does not create a permanent environment waiver.

The sole blocking environment validation for the current release remains `PHYSICAL_PC_ACCEPTANCE_REQUIRED` on the Windows workstation.

## Troubleshooting

| Symptom | Meaning / action |
|---|---|
| PaRuX PC unavailable | PC is off/asleep, LAN access is disabled, devices are on different networks, or pairing was revoked. |
| Certificate/fingerprint error | Re-pair from the workstation; Android intentionally refuses a changed certificate. |
| App asks to pair once after upgrading from a legacy credential-store build | Expected fail-closed migration from the removed `EncryptedSharedPreferences` implementation. Pair once again; credentials are then stored with direct Android Keystore AES-GCM. |
| HTTP 403 | The requested capability is outside the paired-device allowlist or source is outside the private HTTPS LAN boundary. |
| Chat waits for model | The selected managed model/runtime is loading or unavailable on Windows; Android never substitutes a cloud model. |
| Computer Use waits | Expected when task-scoped approval is required. Approve that exact task in PC Studio; no Android Accessibility setting exists. |
| 3D/Training/runtime setup unavailable | Configure/install the engine from PaRuX Windows; these are intentionally desktop-owned lifecycle actions. |
