# PaRuX Codex-parity hardening — closeout evidence (2026-09-04)

This file is **dated closeout evidence, not a second roadmap**. Its detailed original review remains in Git history. The active status is defined by current code/tests, `docs/PARUX_MAIN_SOFTWARE_CLOSEOUT_2026-09-05.md` and `PARUX_EXECUTION_PLAN.md`.

## Current status

**SOURCE_SOFTWARE_COMPLETE; PHYSICAL_PC_ACCEPTANCE_REQUIRED**

GitHub Actions remain disabled and authoritative validation is local.

## Codex-parity scope closed

The 2026-09-04 hardening established the source foundations that remain in the current product:

- PaRuX-managed inference is authoritative; competitor launchers are benchmark targets rather than hidden chat runtimes;
- Windows coding execution is task-scoped/sandboxed and does not fall back to an unsandboxed AppContainer path;
- coding tasks use isolated deterministic worktrees and durable checkpoints;
- orchestrated checkpoint identity survives verifier/repository-context enrichment while direct chat checkpoints remain prompt/workspace bound;
- verifier repair re-enters the same task/worktree/checkpoint;
- `/code` is a native execution route rather than a prompt-only skill impersonating filesystem authority;
- Chat/Project/Coding Agent share Core-owned Project/repository/task identity;
- task workspace/repository authority is desktop-owned and paired clients cannot self-grant it;
- CLI and VS Code control the same durable Core-owned task/project state as desktop;
- resume is a typed Core operation rather than a generic status mutation;
- delegated children can only inherit/narrow parent authority;
- native multi-repository coding uses one durable bounded repository authority set, isolated worktrees, combined verification and atomic apply semantics;
- Virtual Context preserves durable constraints/identifiers/paths/errors/tests and bounded long-context evidence;
- repository intelligence spans C#, TS/JS, Python, Java/Kotlin, Go and Rust evidence plus git change coupling;
- browser DevTools use a dedicated PaRuX profile with bounded transport/evidence and network policy;
- verification discovery covers the supported build/test ecosystems with bounded traversal and generated/vendor pruning;
- coding-agent/multi-agent benchmarks use repository/command acceptance evidence rather than model self-report;
- obsolete OpenCode execution dependency is retired from operational paths.

Subsequent `main` hardening added or completed multi-repository/API drift fixes, durable `ask_user`, long-chat windowing, modern protocol/tool-result compatibility, media payload bounds, parallel MCP sibling execution and integrated Windows ML / ONNX Runtime GenAI serving. Those later changes supersede any narrower capability statement in the original 2026-09-04 snapshot.

## Local software gate

Run the authoritative full gate on the final Windows workstation:

```powershell
powershell -ExecutionPolicy Bypass -File tools/validate-software-complete.ps1 -IncludeAndroid
```

A failure is a software defect and must be fixed; it is not accepted as an environment exception.

Android source/API parity, JVM tests, lint and APK assembly are part of software validation. An emulator may be used on the same Windows PC for paired-client smoke/instrumentation where available. A **physical Android phone is not a separate release blocker**.

Authenticode/signing material is a maintainer publication credential and is likewise not application source completeness.

## Sole remaining acceptance

The only blocking evidence still requiring execution is the target **physical Windows PC**, covering real build/publish/install lifecycle, Windows UI/OS integration, sandbox/Computer Use, GPU/runtime/media/training, Windows ML providers, sustained resource behavior and same-machine quality/performance benchmarks.

**PHYSICAL_PC_ACCEPTANCE_REQUIRED**
