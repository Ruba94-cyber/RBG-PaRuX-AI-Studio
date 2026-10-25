# Architecture

```text
PaRuX.App (WinUI)
    │ authenticated REST + SSE on loopback
    ▼
PaRuX.Server (per-user control plane)
    ├── text/vision provider adapter
    ├── image generation adapter
    └── runtime lifecycle and gaming guard

Optional future Android client
    │ HTTPS on one explicitly selected private IPv4 address
    │ certificate pin + unique revocable device token
    └──────────────────────────────► PaRuX.Server
```

`PaRuX.Contracts` is the versioned API boundary. The desktop process does not call a model provider directly. `PaRuX.Server` owns provider translation, runtime startup, request authentication, cancellation, gaming hard-off, and LAN policy.

LAN is disabled by default. Enabling it never replaces loopback: the desktop keeps its private bootstrap token while paired devices use separate hashed credentials. The server binds neither `0.0.0.0` nor public addresses, refuses non-private source addresses and browser origins, and keeps remote devices away from pairing administration and runtime controls. Media-job listings and cancellation are scoped to the authenticated desktop or paired device.

Conversation data is currently stored locally in SQLite by the Windows client. Moving shared history behind the server API remains a later synchronization step; the 1.0 LAN boundary does not silently share desktop history.

## Privacy boundary

The repository contains no machine profile, GPU name, display model, user path, model location, workflow file, certificate, or session token. Private adapters are discovered at runtime and remain outside the checkout.
