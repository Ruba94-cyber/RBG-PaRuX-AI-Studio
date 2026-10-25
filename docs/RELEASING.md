# Releasing PaRuX

Current policy: **PARTIAL — active software gaps; PHYSICAL_PC_ACCEPTANCE_REQUIRED**.

The canonical status and open software work are defined by `PARUX_EXECUTION_PLAN.md`, especially the active BIC-01–BIC-17 backlog (opened 2026-09-10 and extended 2026-09-22). Older `SOURCE_SOFTWARE_COMPLETE` markers are dated historical evidence and must not be interpreted as the status of current `main`.

Releases are built **locally on maintainer-controlled machines** and uploaded manually. GitHub Actions are intentionally disabled. The authoritative local pre-release gate is `tools/release-gate.ps1`; it validates the repository and executable software checks available on the host, but passing it does not by itself close open BIC items or physical acceptance.

## Release assets

A normal release may contain:

- `PaRuX-<version>-win-x64.msi` plus SHA-256 evidence;
- the Android companion APK when the maintainer has a release keystore available;
- `SHA256SUMS.txt` and the release metadata/envelope used by the updater.

The Windows reference product does not depend on publishing the Android APK to complete software/physical-PC acceptance.

## One-time operator prerequisites

### WiX

WiX v7 requires the maintainer to accept its OSMF/EULA terms explicitly. `scripts/build-msi.ps1` therefore requires:

```powershell
$env:PARUX_ACCEPT_WIX7_EULA = "1"
```

This operator licensing decision is not an unfinished PaRuX feature.

### Android publication signing

A public upgradeable Android release requires the maintainer's Android signing keystore (`PARUX_ANDROID_KEYSTORE` plus passwords). The software gate deliberately validates an unsigned release build, because repository correctness must not depend on owner secrets.

The physical Android phone and public signing identity are **not blockers for the Windows reference acceptance**.

### Windows Authenticode

`scripts/build-msi.ps1` supports `PARUX_SIGN_PFX` / `PARUX_SIGN_PFX_PASSWORD`. If no certificate exists, a locally built acceptance MSI may be unsigned and Windows can display “Unknown publisher”. This is publication/trust material, not missing product code.

### Signed update envelopes

A public `-Release` build requires `PARUX_RELEASE_PRIVATE_KEY` / `PARUX_RELEASE_KEY_PASSWORD`; the matching public trust anchor is embedded in PaRuX. The detached update signature remains mandatory even when the explicit `-AllowUnsignedRelease` option permits an MSI without Authenticode. Private signing material must never be committed.

## Mandatory local software gate

On the target Windows workstation run the full gate without diagnostic skips:

```powershell
powershell -ExecutionPolicy Bypass -File tools\release-gate.ps1 -Restore
```

The gate is responsible for current-status truth, Release x64 solution build, Core regression tests, self-contained publication of every shipped Windows executable, static runtime/security/coding/Engine-Race invariants, VS Code tests when Node is present, and clean Android JVM/lint/debug+release assembly. It also rejects GitHub Actions workflows by project policy.

A red gate is a software defect. Fix it and rerun; do not reinterpret source inspection as success. A green gate is necessary evidence, not a declaration that BIC-01–BIC-17 or hardware-dependent acceptance are automatically complete.

## Physical Windows acceptance after software backlog closure

After the applicable software backlog is implemented and its executable gates pass, the mandatory reference environment is the **same physical Windows PC**. That run must cover:

- MSI install/start/upgrade/uninstall lifecycle;
- real WinUI/tray/overlay/clipboard/file-picker/browser/Computer-Use behavior;
- AppContainer/sandbox process and network boundaries;
- real model/runtime load/unload, VRAM/RAM pressure, concurrency, cancellation/recovery and sustained thermals;
- media/audio/3D/training execution on the installed GPU/runtime stack;
- Windows ML provider discovery/readiness and resident ONNX Runtime GenAI generation/cancellation/reload/fallback;
- exact-model Engine Race/AUTO promotion behavior;
- same-machine competitor performance/quality benchmarks.

The Android toolchain is exercised by the software gate. If an AVD is available on this Windows PC, emulator `connectedAndroidTest`/paired-client smoke may be run as extra confidence. A real phone may later test QR camera, Wi-Fi roaming, audio/headset and OEM background-management edge cases, but those checks are optional non-blocking QA unless a future canonical plan explicitly changes that boundary.

## Cutting a release after acceptance

1. Ensure every release-blocking BIC item in `PARUX_EXECUTION_PLAN.md` is closed with its required evidence.
2. Ensure the full local gate and physical-PC matrix are green.
3. Bump Windows/Android version metadata as appropriate.
4. Update `CHANGELOG.md` with real known limitations.
5. Commit the validated source/version changes and confirm `git status` is clean. Build the public MSI locally from that exact commit:

```powershell
$env:PARUX_ACCEPT_WIX7_EULA = "1"
.\scripts\build-msi.ps1 -Version 1.9.6 -Release
```

   If no Authenticode certificate is configured, deliberately add `-AllowUnsignedRelease` and disclose the unsigned Windows publisher status. This does not waive the signed update-envelope requirement.

6. Optionally build package-manager metadata after a stable public MSI URL exists.
7. Optionally build/sign the Android release when maintainer signing material is available.
8. Verify hashes/signatures for every artifact that will actually be published.
9. Tag/push and manually attach the intended assets to the GitHub Release.

## Acceptance truth

Distribution credentials or optional companion-device QA must never be turned into fake source blockers. Conversely, a failure discovered on the target workstation is a real product defect and must return to source before release acceptance can pass.

**Current blocking status: `PARTIAL` software backlog plus `PHYSICAL_PC_ACCEPTANCE_REQUIRED`; neither may be collapsed into the other.**
