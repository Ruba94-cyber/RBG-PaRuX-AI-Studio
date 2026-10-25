# PaRuX Agent for VS Code

This is a thin local IDE surface over the **same PaRuX Core task engine** used by the Windows app, CLI and Project Manager. It does not contain another agent runtime and it does not call OpenAI, Anthropic, LM Studio, Ollama or any cloud service.

## What it exposes

- Explorer **PaRuX Agents** tree with durable task state, progress and verifier status;
- create a native Coding Agent task for a selected local repository;
- explicit `Offline`, `Package Fetch` and `Trusted Restore` profiles for each newly-created task;
- view durable task logs;
- open the exact PaRuX task worktree in a new VS Code window;
- stop or safely resume a durable task.

Coding execution, subagents, AppContainer sandboxing, package-restore policy, checkpoints, verification/repair and repository authority remain owned by PaRuX Core/Desktop. The extension never receives a model prompt/tool protocol and never implements a second coding loop.

## Security boundary

The extension reads `%LOCALAPPDATA%\PaRuX\server\bootstrap.json` (or the platform-equivalent local-data path) and sends the Core token **only** when the bootstrap endpoint is `localhost`, `127.0.0.1` or `::1`. A non-loopback endpoint is rejected before any request is made. Workstation paths are obtained only through endpoints that Core itself restricts to the authenticated desktop owner.

`run_command` remains network-disabled regardless of the profile selected in VS Code. `Package Fetch`/`Trusted Restore` affect only PaRuX's deterministic `restore_packages` action and still require the global PaRuX Internet switch to be enabled.

## Local install

From PowerShell on the development PC:

```powershell
powershell -ExecutionPolicy Bypass -File integrations/vscode/install-local.ps1
```

Restart/reload VS Code, then open **Explorer → PaRuX Agents**. PaRuX Core must be running (start the PaRuX desktop app or `parux serve`).

## Tests

No npm install is required. With a local Node runtime:

```powershell
node --test integrations/vscode/test/*.test.js
```

The tests cover loopback-token confinement, profile normalization and the exact native coding-task payload. The Windows/.NET full product gate remains `tools/validate-software-complete.ps1`; GitHub Actions are intentionally not used for this project.
