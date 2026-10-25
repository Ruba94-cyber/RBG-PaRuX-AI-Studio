# RBG PaRuX AI Studio

PaRuX is a Windows-first, local-first AI studio: chat, coding agent, image/video/audio generation and model management running on your own hardware, with a companion Android client. Core AI inference is local; cloud use is off by default and always explicit.

This repository publishes **release builds**. The current release is **v1.9.6**.

## What is in a release

| File | Purpose |
| --- | --- |
| `PaRuX-<version>-win-x64.msi` | Windows installer (Authenticode-signed) |
| `PaRuX-<version>-win-x64.update.json` | Signed update envelope read by the in-app updater |
| `PaRuX-<version>-win-x64.sha256.txt` | SHA-256 of the MSI |
| `PaRuX-<version>-release-provenance.json` | Build provenance (source commit, payload hashes) |
| `PaRuX-<version>-android.apk` and `-android-provenance.json` | Signed Android client |
| `SBOM.windows.cyclonedx.json` | CycloneDX software bill of materials |
| `PaRuX-signing-certificate.cer` | Public code-signing certificate (no private key) |

## Verifying a download

1. Compare the SHA-256 of the MSI with `PaRuX-<version>-win-x64.sha256.txt`.
2. The signing certificate is **self-issued** by the publisher (not issued by a public certificate authority), so Windows and SmartScreen will warn until you choose to trust it. Its SHA-1 thumbprint is `F73FDA46DD3A0A7DE82C0E479AE1F1E5091416F8` and the SHA-256 fingerprint of the `.cer` file is `A8F7445D921A1895901DFB8E9BACD23D25F96F425EE63D82D2269B4ECA004321`. Check both against a channel you trust before importing the certificate; importing it is optional and only needed to remove the warning.
3. The in-app updater additionally verifies a signed update envelope against a public key compiled into the app.

## Status

Release builds are produced and verified by the project's own scripts (asset verification, release gate, UI regression tests). Each release page lists exactly what was and was not validated. Hardware-specific and clean-install checks are reported honestly there; nothing is claimed that was not run.

## Licence

PaRuX source is licensed under the Functional Source License 1.1 with MIT future licence (FSL-1.1-MIT), see `LICENSE`. This repository contains release assets only; the source history is kept in a separate private archive.
