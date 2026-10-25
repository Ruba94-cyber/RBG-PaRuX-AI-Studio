# RBG PaRuX AI Studio Security Policy

## Reporting a vulnerability

Please report exploitable security issues privately rather than opening a public issue. Prefer GitHub's private vulnerability-reporting / Security Advisory flow for this repository (Security → Report a vulnerability) when available.

Include:

- a description of the issue and likely impact;
- reproduction steps or a proof of concept;
- affected version/commit and platform;
- whether the issue requires a PaRuX-managed, user-managed, LAN/Android or local-only configuration.

Do not include unrelated private data, model contents or credentials in a report.

## Security model

PaRuX is local-first. The Windows control plane listens on loopback by default. LAN access for the Android companion is opt-in, restricted to private assigned addresses and uses the project's pairing/device-token boundary. Core AI inference is designed to remain on the user's machine; Internet access is an explicit tool/update/download capability rather than an implicit cloud-inference fallback.

Runtime/component ownership is a security boundary:

- `PARUX_MANAGED`: PaRuX may install/update/repair/rollback/remove its own managed copy.
- `USER_MANAGED`: PaRuX may validate/use the explicitly selected installation but must not silently mutate it.
- `SYSTEM_DETECTED`: discovery is informational until the user chooses how PaRuX should use the component.

## In scope

Examples include:

- authentication/session bypass on loopback or paired-device APIs;
- LAN pairing/device-token bypass or access from non-private/public interfaces;
- privilege escalation from the Android companion into desktop-only shell/coding/computer-use capabilities;
- path traversal, unsafe archive extraction, command injection or arbitrary file overwrite/deletion;
- SSRF/DNS-rebinding/private-network access through web-search/fetch features where policy says it should be denied;
- imported Skills/MCP/tool definitions obtaining permissions they did not declare or the user did not authorize;
- prompt/web/model content crossing a trust boundary and causing implicit tool/shell/filesystem/network execution;
- PaRuX modifying, updating or deleting a `USER_MANAGED` component without explicit user intent;
- managed runtime/model downloads being accepted despite failed required integrity/provenance validation;
- unsafe interrupted install/update/rollback behavior that replaces a known-good runtime with a partial/corrupt one;
- a path that silently sends core AI inference to a proprietary/cloud endpoint;
- secrets/tokens exposed through logs, observability, backups or generated artifacts;
- backup import that permits path traversal, executable planting or unvalidated replacement of state outside the allowed backup surface;
- Gaming Mode bypass that leaves PaRuX-owned AI workloads running when the product claims they are suspended/released.

## Out of scope

- vulnerabilities that exist solely inside an independently user-managed third-party runtime and are not caused or amplified by PaRuX's integration; report those upstream as well;
- normal local resource usage that stays within documented controls unless it bypasses a security/resource boundary or creates a persistent denial of service;
- model-quality, hallucination or refusal behavior without a separate security-boundary bypass;
- attacks that require the user to deliberately disable the relevant PaRuX security control and do not cross another boundary.

## Security expectations for contributors

Security-relevant changes should include regression tests whenever the behavior is hermetically testable. Important areas include authentication, LAN access, path validation, archive handling, runtime/model downloads, component ownership, shell/process execution, permissions, imported tool definitions and backup/restore.

Never treat content from the Internet, models, datasets, generated artifacts or imported manifests as trusted instructions merely because it appears inside a PaRuX workflow.

## Supported versions

Security fixes are provided for the latest stable version published on the [GitHub Releases](https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases) page and for the current `main` development branch. Older releases are unsupported; upgrade to the latest stable release to receive security fixes. Until the first stable release is published, use the latest `main` state.
