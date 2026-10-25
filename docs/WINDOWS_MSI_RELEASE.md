# PaRuX Windows MSI release workflow

PaRuX is distributed primarily as a **versioned x64 MSI attached to GitHub Releases**. The Windows application remains a full-trust unpackaged WinUI desktop process; MSI is the installation and Major Upgrade mechanism, not an MSIX/AppContainer boundary.

GitHub Actions is intentionally **not required** by this workflow. Build, test, Authenticode signing, detached-manifest signing and release preparation are performed locally on a trusted Windows workstation during the current implementation/testing cycle.

## End-user installer verification and hash checking

Before launching a downloaded PaRuX-*-win-x64.msi, verify the SHA-256 digest against the release SHA256SUMS.txt:

`powershell
Get-FileHash -Algorithm SHA256 .\PaRuX-*.msi
Get-AuthenticodeSignature .\PaRuX-*.msi
`

Or from CMD:
`cmd
certutil -hashfile PaRuX-*.msi SHA256
`

Confirm that the calculated hash matches SHA256SUMS.txt and the Authenticode signature is Valid.

## Ownership boundary

The MSI owns only the installed PaRuX application tree under `Program Files\PaRuX`, Start Menu/Windows Installer registration and related machine install metadata.

Mutable product state is intentionally outside that tree (currently primarily `%LOCALAPPDATA%\PaRuX`) and must survive MSI repair, Major Upgrade, uninstall and reinstall. This includes Projects, history, settings, models, managed AI runtimes, adapters, generated media, download state and other durable local-AI data. A future explicit **Erase my PaRuX data** operation must remain a separate user action.

The verified baseline runtime embedded under `Program Files` is immutable MSI-owned source material. On first server load PaRuX re-verifies its staging manifest and copies the baseline llama.cpp build into the mutable user runtime. Existing user/managed runtime builds are never overwritten by that bootstrap.

## Toolchain

For the Windows MSI:

- .NET 8 SDK / Windows desktop build prerequisites;
- WiX Toolset v7 through `WixToolset.Sdk/7.0.0`;
- WiX v7 OSMF/EULA terms accepted explicitly by the person performing the build;
- Windows PowerShell 5.1 or newer;
- Windows SDK `signtool.exe` when Authenticode signing is configured;
- a PaRuX-owned encrypted RSA manifest-signing private key stored outside the repository for public `-Release` builds;
- network access to the pinned upstream runtime archives **or** already verified copies in `artifacts\cache\runtime`.

The MSI SBOM is Windows-scoped and therefore **does not require Java, Gradle or the Android SDK**. Android SBOM generation is a separate release-surface operation described below.

`tar.exe`/libarchive, included on supported modern Windows, is required only when building the multi-GB Full Offline ZIP because it provides the ZIP64 path used by the bundle builder.

Before using WiX v7 locally, review its current terms. Then, only if accepted in that shell:

```powershell
$env:PARUX_ACCEPT_WIX7_EULA = "1"
```

## 1. Create the PaRuX manifest trust anchor once

The in-app updater uses a PaRuX-owned RSA trust anchor independently of Authenticode. Generate this key **once**, outside the repository.

```powershell
$env:PARUX_RELEASE_KEY_PASSWORD = "a-long-unique-secret-kept-outside-git"

dotnet run --project .\tools\PaRuX.ReleaseTool -- keygen `
  --private "<secure-key-dir>\manifest-private.pem" `
  --public ".\artifacts\release\manifest-public.pem"
```

The private PKCS#8 PEM is encrypted with AES-256. `PaRuX.ReleaseTool` refuses a private-key path inside the repository. Back up the private PEM and password separately.

Copy **only the public key** value printed by the tool into `src/PaRuX.App/Services/ReleaseTrustAnchor.cs` and commit that public trust anchor. Never commit the private key or password.

The configured `ReleaseTrustAnchor.PublicKeyPem` must match the maintainer's private signing key. A public `-Release` build fails closed if the embedded trust anchor is empty; do not generate a replacement key for an already configured anchor unless deliberately rotating it.

Key rotation requires shipping a PaRuX build that trusts the new public key before relying solely on the new private key for later releases.

## 2. Development/test MSI

```powershell
.\scripts\build-msi.ps1 -Version 1.9.6
```

The script publishes, as one Windows product:

1. `PaRuX.exe` Desktop;
2. `PaRuX.Server\PaRuX.Server.exe` — the active local backend, displayed as **PaRuX Server**;
3. `Cli\parux.exe`;
4. `UpdateHelper\PaRuX.UpdateHelper.exe` as a self-contained single-file elevated handoff helper;
5. `PaRuX.Server\winml\PaRuX.WinMlWorker.exe` for Windows ML inference;
6. license/notices/provenance files;
7. Windows-scoped generated CycloneDX SBOM;
8. verified baseline `llama.cpp b10549` Vulkan payload;
9. verified PaRuX-owned `uv 0.12.1` payload;
10. the WiX x64 MSI.

The source project and namespace intentionally remain `PaRuX.Server` for compatibility; that internal implementation name is not the installed/public process identity.

The version must be canonical Windows Installer `major.minor.patch` and cannot exceed `255.255.65535`.

Expected primary artifact:

```text
PaRuX-1.9.6-win-x64.msi
```

The WiX package has one stable `UpgradeCode`; every newer version is a Windows Installer **Major Upgrade** and downgrade is blocked.

### Release-payload preparation performed by WiX

Before WiX harvests `artifacts\publish\win-x64`, `PaRuX.Setup.wixproj` runs two fail-closed preparation steps.

First:

```powershell
.\scripts\generate-sbom.ps1 `
  -Scope windows `
  -OutputPath .\artifacts\publish\win-x64\PaRuX.Server\SBOM.cyclonedx.json `
  -ProductVersion 1.9.6
```

The Windows SBOM includes the resolved package graphs of the five projects published into the MSI — Desktop, Server, CLI, Update Helper and Windows ML worker — plus locked runtime/vendored components. `PaRuX.Server.Tests` and Android libraries are deliberately excluded because they are not installed by the Windows MSI.

Second:

```powershell
.\scripts\stage-msi-runtime.ps1 `
  -PublishDir .\artifacts\publish\win-x64
```

`packaging\windows-baseline-runtime.lock.json` pins the accepted upstream archives, sizes and SHA-256 values. Staging refuses a size/hash mismatch, extracts only verified ZIPs, checks required payload files and creates:

```text
PaRuX.Server\runtime-baseline\baseline-lock.json
PaRuX.Server\runtime-baseline\staging-manifest.json
PaRuX.Server\runtime-baseline\llama.cpp-b10549-vulkan\...
PaRuX.Server\runtime-tools\uv\uv.exe
```

The staging manifest inventories the final extracted payload with file sizes and SHA-256 values. At runtime, bundled llama.cpp and uv are trusted only through that manifest-verification path.

## 3. Android and repository-wide SBOMs

Android is a separate release surface. Generate its actual release dependency graph with:

```powershell
.\scripts\generate-sbom.ps1 `
  -Scope android `
  -OutputPath .\artifacts\sbom\SBOM.android.cyclonedx.json `
  -ProductVersion 1.9.6
```

This requires the Android/Gradle toolchain and reads `releaseRuntimeClasspath`. It is the correct place to verify that removed direct dependencies such as `androidx.security:security-crypto` and the unused DataStore declaration are absent.

For a repository/release-surface inventory containing both Windows and Android graphs:

```powershell
.\scripts\generate-sbom.ps1 `
  -Scope all `
  -OutputPath .\artifacts\sbom\SBOM.all.cyclonedx.json `
  -ProductVersion 1.9.6
```

There is intentionally no hand-maintained root `sbom.json`; generated resolved graphs are the source of truth.

## 4. Public release prerequisites

For Authenticode publisher trust:

```powershell
$env:PARUX_SIGN_PFX = "<secure-key-dir>\parux-code-signing.pfx"
$env:PARUX_SIGN_PFX_PASSWORD = "..."
```

For PaRuX's detached release signature:

```powershell
$env:PARUX_RELEASE_PRIVATE_KEY = "<secure-key-dir>\manifest-private.pem"
$env:PARUX_RELEASE_KEY_PASSWORD = "..."
```

Then:

```powershell
.\scripts\build-msi.ps1 -Version 1.9.6 -Release
```

In `-Release` mode the script refuses to proceed when the embedded public trust anchor is empty or the encrypted private manifest key is unavailable. The private key is used only by the local release tool and is never shipped.

### Authenticode policy

When a PFX is configured, the build signs and verifies the primary installed executables **before** WiX harvests them:

- `PaRuX.exe`;
- `PaRuX.Server.exe`;
- `parux.exe`;
- `PaRuX.UpdateHelper.exe`.

It then signs and verifies the final MSI. Signing the helper itself matters because the in-app updater elevates that executable and the UAC prompt should identify the same publisher as the released application.

A public unsigned MSI is still possible only with the deliberate escape hatch:

```powershell
.\scripts\build-msi.ps1 -Version 1.9.6 -Release -AllowUnsignedRelease
```

This does **not** bypass the PaRuX manifest-signature requirement. It only allows missing Authenticode. Users may see SmartScreen / `Unknown Publisher` warnings.

## 5. Detached manifest is generated from final MSI bytes

For `-Release`, `build-msi.ps1` automatically invokes `PaRuX.ReleaseTool` **after** any Authenticode operation and creates:

```text
PaRuX-1.9.6-win-x64.update.json
```

The envelope authenticates the exact UTF-8 manifest payload with RSA-SHA256. The signed manifest pins:

- canonical version;
- `win-x64` architecture;
- exact GitHub release MSI URL;
- exact MSI byte size;
- exact MSI SHA-256;
- issue timestamp;
- exact GitHub release-notes URL.

The updater rejects wrong repository/tag/version/asset, encoded/path variants, non-HTTPS URLs, invalid MSI version bounds, bad signature, wrong size or wrong digest.

**Never modify/re-sign the MSI after the `.update.json` envelope has been generated.** The signed digest is over the final MSI bytes.

## 6. Why the elevated Update Helper exists

The downloaded MSI initially lives under user-writable `%LOCALAPPDATA%`. Hashing it in the Desktop process and then directly starting elevated `msiexec` would leave a time-of-check/time-of-use window in which another same-user process could replace those bytes before the elevated installer opened them.

PaRuX therefore never elevates `msiexec` directly on the LocalAppData file.

After the user explicitly chooses **Download & install verified MSI**:

1. Desktop verifies GitHub release metadata, RSA envelope, signed version/URL/size and downloaded MSI SHA-256.
2. Desktop persists the exact verified `.update.json` beside the downloaded MSI.
3. Desktop starts installed `Program Files\PaRuX\UpdateHelper\PaRuX.UpdateHelper.exe` with `runas`.
4. The elevated helper independently verifies the embedded trust anchor + RSA envelope and requires a newer release.
5. It creates a unique protected staging directory, copies the MSI and verifies the protected copy again.
6. It copies/re-verifies its own helper image into protected staging and starts the staged worker.
7. Desktop closes only after that bootstrap succeeds.
8. The staged worker waits for that exact Desktop PID to exit, verifies again and starts `msiexec.exe /i <protected-msi> /norestart` without a shell.
9. Large staged MSI/envelope files are cleaned after Windows Installer exits.

A non-elevated caller cannot turn the helper into a generic admin MSI launcher: the helper accepts only a cryptographically signed, strictly newer PaRuX release matching the embedded trust anchor.

## 7. Full Offline bundle

The Standard MSI contains the baseline runtime/toolchain pieces but not every multi-GB model/embedding/reranker asset. A separate Full Offline bundle packages deliberately curated assets so a workstation can perform baseline local workflows after installation without network access.

The input asset lock is intentionally explicit. Each record identifies:

- logical role such as `text-baseline`, `embedding`, `reranker` or optional `speech`;
- source path relative to a trusted local assets directory;
- destination scope (`local-ai` or `ai-runtimes`);
- exact destination consumed by PaRuX;
- SHA-256;
- upstream provenance URL;
- license identifier/text reference.

The builder requires, at minimum, the current runtime destinations for:

```text
<models-dir>\<default-chat-model>.gguf
%LOCALAPPDATA%\PaRuX\ai-runtimes\embeddings\model.onnx
%LOCALAPPDATA%\PaRuX\ai-runtimes\embeddings\vocab.txt
%LOCALAPPDATA%\PaRuX\ai-runtimes\reranker\model.onnx
%LOCALAPPDATA%\PaRuX\ai-runtimes\reranker\vocab.txt
```

Example build after the MSI has been produced:

```powershell
.\scripts\build-full-offline-bundle.ps1 `
  -MsiPath ".\artifacts\installer\PaRuX-1.9.6-win-x64.msi" `
  -AssetsRoot "D:\PaRuX-release-assets" `
  -AssetsLock "D:\PaRuX-release-assets\full-offline-assets.lock.json" `
  -SbomPath ".\artifacts\publish\win-x64\PaRuX.Server\SBOM.cyclonedx.json" `
  -ProductVersion 1.9.6
```

For a release that promises a speech baseline as well, add `-RequireSpeech` and provide a valid locked speech asset entry.

Expected outputs:

```text
PaRuX-FullOffline-1.9.6-win-x64.zip
PaRuX-FullOffline-1.9.6-win-x64.zip.sha256
```

Inside the bundle:

- the MSI;
- locked assets in deterministic destination layout;
- Windows CycloneDX SBOM;
- open-source provenance/notices;
- runtime lock;
- Full Offline asset lock;
- `MANIFEST.sha256` covering every other file;
- `verify-offline-bundle.ps1`;
- `install-offline-assets.ps1`.

Before redistribution or use:

```powershell
.\verify-offline-bundle.ps1
```

The verifier rejects missing files, SHA mismatches, malformed/unsafe paths and unexpected untracked files.

After installing the included MSI:

```powershell
.\install-offline-assets.ps1
```

The asset installer verifies the **entire bundle first**, then verifies each locked asset again while copying into PaRuX-managed storage. A different existing destination file is not overwritten unless `-Force` is explicitly supplied.

The final acceptance test must disconnect the workstation from the network after MSI + asset installation and prove baseline Chat/Projects/Coding/RAG behavior. Create/Training GPU packs remain optional unless a dedicated GPU-specific offline bundle is released.

## 8. MSI acceptance harness

Clean install/uninstall:

```powershell
# Elevated PowerShell
.\scripts\test-msi.ps1 -CurrentMsi ".\artifacts\installer\PaRuX-1.9.6-win-x64.msi"
```

Major Upgrade + downgrade blocking:

```powershell
# Elevated PowerShell
.\scripts\test-msi.ps1 `
  -PreviousMsi "D:\releases\PaRuX-1.9.4-win-x64.msi" `
  -CurrentMsi ".\artifacts\installer\PaRuX-1.9.6-win-x64.msi"
```

The harness verifies Desktop, **PaRuX Server**, CLI, protected Update Helper, Start Menu shortcut, machine PATH registration, Major Upgrade, downgrade blocking and uninstall. It also creates a unique sentinel under `%LOCALAPPDATA%\PaRuX\_msi-acceptance` and proves MSI operations do not remove mutable product state.

For the zero-prerequisite gate, supplement the harness with manual/automated assertions that the installed MSI contains:

```text
PaRuX.Server\SBOM.cyclonedx.json
PaRuX.Server\runtime-baseline\staging-manifest.json
PaRuX.Server\runtime-baseline\llama.cpp-b10549-vulkan\llama-server.exe
PaRuX.Server\runtime-tools\uv\uv.exe
```

Then launch on a clean machine and verify the first-run baseline bootstrap, Component Inventory (`python`/`uv` are `PARUX_MANAGED`) and rejection of external Python/uv Locate/Skip attempts.

This harness still requires a real elevated Windows run. It is not a substitute for GPU inference, Android, voice or interactive UI validation.

## 9. Publish the GitHub Release

Use tag exactly:

```text
v1.9.6
```

Attach at minimum:

```text
PaRuX-1.9.6-win-x64.msi
PaRuX-1.9.6-win-x64.update.json
PaRuX-1.9.6-win-x64.sha256.txt
```

When publishing Full Offline as an additional release option, also attach its ZIP and `.sha256` companion.

The in-app Update Manager enables installation only when the release is newer, both expected update assets exist, the embedded public key is configured, RSA signature is valid, signed metadata matches the exact GitHub release asset and downloaded bytes match signed size/SHA-256.

Cancelling the PaRuX confirmation or UAC leaves the installed application unchanged. Desktop exits only after the elevated bootstrap helper reports that protected staging and the staged worker were created successfully.

## Portable build policy

A self-contained publish directory/ZIP remains useful for development, diagnostics and recovery experiments. It is **not** the primary public distribution and does not get a second self-overwrite updater. The supported application-update trust boundary is the versioned MSI product family above.

## Local release-candidate verification (no network, no upload)

Build the signed-envelope MSI from a clean, committed HEAD, build the Android APK without uploading, generate the SBOM, then bind everything to that commit:

```powershell
.\scripts\build-msi.ps1 -Release -AllowUnsignedRelease      # Authenticode optional; the RSA envelope is not
.\scripts\publish-android-apk.ps1 -Tag v1.9.6 -LocalOnly     # no tag needed, no gh call
.\scripts\generate-sbom.ps1 -Scope windows
.\scripts\verify-release-assets.ps1 -Version 1.9.6 -SourceCommit <HEAD> -RequireAndroid `
  -PublishDirectory .\artifacts\publish\win-x64 -ManifestOutput .\artifacts\installerelease-candidate-manifest.json
```

What binds the artifacts to the commit:

- `build-msi.ps1 -Release` records the SHA-256 of every file it harvests into the MSI (`payloadFiles`), plus `buildMode: release` and `sourceTreeClean: true`, in `PaRuX-<version>-release-provenance.json`.
- The verifier extracts the MSI as an administrative image (`msiexec /a` into a temporary directory; nothing is installed) and requires the extracted tree to equal `payloadFiles` exactly. The four primary binaries inside the MSI must embed `<version>+<commit>` with at least 12 hex characters matching `-SourceCommit`.
- `-PublishDirectory` is an additional comparison: that tree must also equal `payloadFiles`, so a publish directory from another commit paired with a valid MSI fails.
- The signed envelope authenticates the MSI bytes and commit; the SBOM hash (`-SbomPath`, or the default `artifacts\sbom` file) is recorded in the manifest when present.
- With `-RequireAndroid` the APK's own `versionName` (read with `aapt2`) must equal the release version and match Gradle, and `versionCode` must exceed the previous release's (`-PreviousAndroidVersionCode`, `-PreviousManifest`, or Git history of the Gradle file).
- `-ManifestOutput` is written only after every check passed and records version, commit, build mode, clean-tree fact, payload tree hash, Android versions and each asset's size and SHA-256.

Keep the previous version's MSI outside `artifacts\installer`, because `build-msi.ps1` clears that directory; it is the baseline for `test-msi.ps1 -PreviousMsi`.
