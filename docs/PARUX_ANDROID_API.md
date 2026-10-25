# PaRuX Android API / Security Boundary

Current as of **2026-10-03**.

**PARTIAL — active software gaps; PHYSICAL_PC_ACCEPTANCE_REQUIRED**. See the current [canonical execution plan](../PARUX_EXECUTION_PLAN.md).

This document describes the paired-device boundary used by `src/PaRuX.Android/`. The authoritative enforcement is code: `PairedDeviceEndpointPolicy` plus `PairedDeviceAbuseMiddleware`. Documentation is never an allowlist by itself.

## Transport and identity

- Desktop Core is loopback and uses the per-process PaRuX token.
- Optional Android/LAN Core is HTTPS on one explicit private address.
- Android uses a unique revocable device token plus certificate-fingerprint pinning.
- Pair completion is the only unauthenticated LAN endpoint and requires the short-lived single-use pairing code.
- Browser-origin LAN requests are rejected.
- Source/listener must be private; no wildcard public listener, UPnP/NAT opening or cloud relay.
- A valid device token proves **identity, not desktop privilege**.

Remote requests remain deny-by-default unless their exact method/path shape is deliberately admitted by `PairedDeviceEndpointPolicy`.

## Admitted paired-client capability groups

### Chat / conversations / routing

Paired clients may use the Core-owned Chat/profile/routing preference and persisted-conversation surfaces. The mobile client receives sanitized routing/model evidence; workstation paths and raw benchmark authoring are not exposed.

### Media / voice

Paired clients may create/list/read/cancel media jobs, stream a completed asset through the authenticated media endpoint, and use already-configured speech/avatar/3D engines. Engine/runtime installation and repair remain desktop-only.

### Internet-backed explicit tools

Research/presentations and the shared Internet toggle may be used when admitted. The Internet switch is an explicit network-tool policy, not a cloud-inference switch.

### Memory / RAG / local knowledge

Android can work with Core-owned Memory and existing RAG source ids. It cannot register an arbitrary Windows folder path. Local desktop integrations for notes/mail/calendar/documents likewise remain path-authored on Windows.

### Managed model discovery/download

Android can search and request managed model downloads through Core-owned destination/source validation. It cannot choose arbitrary destination/model paths, author low-level benchmark inputs or mutate runtime installations.

### Skills

The mobile Skill creation path is sanitized to prompt/routing-safe capability. It cannot self-grant filesystem, shell, process, browser automation, Computer Use or arbitrary privileged tool access.

### Durable agents / Computer Use

Android can create/supervise permitted AgentTasks, inspect logs/results through sanitized projections, cancel work and approve exactly one task-scoped Computer Use request where policy allows. It has no AccessibilityService and no direct Windows input primitive.

Persistent Full Computer Use can only be configured on Windows. Workflow/Autopilot-created Computer Use tasks pass through the same durable Windows approval boundary.

### Projects / artifacts / workflows / automations

Android can use Core-owned Project/task/knowledge/artifact ids, run permitted Workflows, supervise existing Automations and inspect read-only Context Graph projections. Artifact downloads resolve server-side from durable ids and never accept an arbitrary Windows source path.

### Safe V2 parity

High-level parity is exposed through dedicated sanitized `/api/v2/mobile/*` projections, including:

- effective Execution Profile quality/policy;
- aggregate/sanitized Observability;
- Goal Mode V2 specification/version/task/verification flow.

Generic desktop V2 endpoints remain unavailable when they could expose model/worktree/filesystem paths, privileged runtime controls or authoring authority. Mobile Goal creation cannot self-grant Core capabilities.

## Explicit desktop-only classes

Paired devices do not receive authority for:

- runtime/component install/update/locate/remove/rollback;
- arbitrary model role/import/benchmark paths;
- Training Lab job authoring/engine installation;
- RAG source creation from a filesystem path;
- backup/restore;
- MCP administration and Local API administration;
- generic task status/stage mutation;
- Context Graph writes;
- plugin install/trust/inspection;
- per-model Execution Profile authoring;
- Virtual Context ledger authoring;
- Semantic Index repository administration;
- Visual Context capture/annotation authoring;
- manual Engine Race/performance benchmark authoring;
- arbitrary filesystem/shell/process/window/input primitives.

Android's client-side `MobileEndpointBoundary` mirrors important restrictions for better UX, but server enforcement never trusts the client mirror.

## Abuse / file controls

Paired requests use per-device throttling/concurrency controls. Expensive work uses tighter admission. Path-id matchers accept bounded route shapes instead of arbitrary nested paths.

Backup import is desktop-owned and independently decompression-budgeted before validate-then-swap restore.

Android's `FileProvider` is non-exported and limited to the dedicated shared-media cache. Completed media is downloaded on demand through the authenticated Core media endpoint rather than transporting repeated Base64 payloads in job polling.

## Android client requirements

- release manifest disallows cleartext traffic;
- pairing certificate fingerprint is pinned; changes fail closed until re-pair;
- credential payload is protected by a non-exportable Android Keystore AES-GCM key;
- backup of app credentials is disabled;
- no HTTP logging interceptor in the release network stack;
- QR decoding is local CameraX + ZXing Core;
- no direct unmanaged model/media runtime URLs are used by the companion;
- no AccessibilityService or raw Windows input authority exists.

## Validation truth

Android is part of PaRuX **software completeness**. The authoritative Windows gate must run its JVM/unit tests, lint and debug/release APK assembly together with Core/Windows/VS Code validation.

An isolated Android 15 emulator exercised the production pinned-TLS client against an isolated real Windows Core on 2026-10-03: wrong fingerprint/code refusal, pairing, streamed generation, cancellation and recovery, authenticated artifact download, denied desktop-only device administration, saved credentials after Core restart and refusal after revocation. Three instrumented workflow tests and six Android Keystore tests passed. This is client/network evidence; it does not establish camera, voice, roaming or every Android workspace on a physical phone. The release artifacts still require the final committed-build gate.

An emulator on the same final Windows workstation may additionally run `connectedAndroidTest` and paired-client smoke/instrumentation. A physical phone can later exercise camera QR, Wi-Fi roaming, OEM background management and real microphone/headset behavior as exploratory QA.

Those phone-specific checks are **not a second blocking release gate**. Any defect they discover is still a normal source bug, but absence of a physical Android phone does not prevent the Windows reference product from completing acceptance.

The sole mandatory environment acceptance is:

**PHYSICAL_PC_ACCEPTANCE_REQUIRED**
