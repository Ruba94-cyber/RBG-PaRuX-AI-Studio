# PaRuX Local Setup

PaRuX is local-first. The public repository contains the app, control plane, contracts, tests, and generic adapter discovery only. Machine-specific scripts, model paths, workflows, hardware profiles, tokens, and generated media remain outside the checkout.

## Runtime layout

```text
PaRuX.App -> authenticated loopback API -> PaRuX.Server -> local provider adapters
```

PaRuX starts heavy AI only when a request needs it. Ending a game leaves the system ready but does not preload a model.

## Optional environment variables

- `PARUX_LOCALAI_SCRIPT`: local lifecycle controller
- `PARUX_MEDIA_SCRIPT`: image/video adapter
- `PARUX_PROVIDER_ENDPOINT`: local OpenAI-compatible endpoint
- `PARUX_SERVER_PATH`: server executable override for development

Do not place personal values for these variables in tracked files. Use per-user environment variables or private local configuration.

## Build and test

```powershell
dotnet build PaRuX.sln -c Release -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj -c Release
```

`publish.cmd` creates a self-contained unpackaged Windows layout. WinUI XBF and PRI resources are copied explicitly so the portable executable can resolve `ms-appx` resources.

## Local data

PaRuX stores settings, SQLite history, attachments, generated media, logs, and the ephemeral server bootstrap under the current user's local application data. The generated-image folder can instead be selected in Settings. These locations are excluded from source control.

## Security and privacy

- Server binding is `127.0.0.1` only.
- Protected requests require a random session token.
- The bootstrap file is local runtime state and must never be shared.
- LAN mode is not enabled in the 0.5 preview.
- No provider is exposed directly.
- Audio is generated only after an explicit user request.

## Troubleshooting

- If the UI opens but a request fails, use Settings diagnostics to distinguish the PaRuX server from the provider runtime.
- If gaming is active, PaRuX deliberately refuses to start AI.
- If the provider is offline, retry after checking the private lifecycle adapter.
- If an image adapter fails, its error is returned as a recoverable chat error; the app should remain open.
- Logs must not be published without reviewing them for prompts and local paths.

## Backups and rollback

Back up private adapters and local settings separately from the repository. A code rollback does not require migrating 0.x user data; if schema compatibility is intentionally broken before 1.0, remove only the documented PaRuX local data after making a recoverable backup.

## Installer integrity and hash verification

PaRuX releases publish detached SHA-256 digests in SHA256SUMS.txt alongside each versioned MSI installer. Before running the installer, verify its cryptographic hash and Authenticode signature.

In Windows PowerShell:
`powershell
Get-FileHash -Algorithm SHA256 .\PaRuX-*.msi
Get-AuthenticodeSignature .\PaRuX-*.msi
`

In Command Prompt:
`cmd
certutil -hashfile PaRuX-*.msi SHA256
`

Compare the calculated SHA-256 digest against the published SHA256SUMS.txt entry. The Authenticode signature status must be Valid.
