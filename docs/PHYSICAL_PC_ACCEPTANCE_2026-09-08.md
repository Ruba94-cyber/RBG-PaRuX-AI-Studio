# PaRuX physical Windows PC acceptance — 2026-09-08

Status: **SOURCE_SOFTWARE_COMPLETE; PHYSICAL_PC_ACCEPTANCE_REQUIRED**.

This is the blocking acceptance procedure for the Windows reference product. It must be run against the **exact `main` commit intended for release**. A source-complete marker, a successful older release, provider readiness, an emulator result, or an agent self-report is not a substitute for the evidence below.

## 0. Evidence header

Record before testing:

- Acceptance date/time:
- Operator:
- Git commit SHA (`git rev-parse HEAD`):
- Git status (`git status --porcelain=v1` must be empty before the gate):
- Windows edition/build:
- CPU:
- RAM:
- GPU(s), driver(s), VRAM:
- NPU(s), driver/provider package(s), if present:
- PaRuX build/version:
- Installer artifact SHA-256:
- .NET SDK version:
- Node/npm version:
- JDK/Gradle version:
- Competitor versions/models/settings used in head-to-head measurements:

**FAIL** if the SHA is not the intended release SHA or the repository has unexplained local modifications.

## 1. Full software/release gate

From an elevated/appropriate PowerShell on the target Windows development workstation:

```powershell
pwsh -NoProfile -File .\tools\release-gate.ps1 -Restore
```

Required evidence:

- complete console log saved under `artifacts/validation/`;
- Core/test dependency build PASS;
- Core regression suite PASS;
- full Windows x64 solution build PASS;
- App/Server/CLI/UpdateHelper/WinML worker self-contained publish PASS;
- VS Code integration tests PASS;
- Android static boundary + JVM tests + lint + debug/release APK assembly PASS;
- no GitHub Actions workflow introduced.

**FAIL** on any skipped mandatory section, non-zero exit code, or unpublished/unexplained warning that affects correctness.

## 2. Installer lifecycle

Test the produced installer, not a development `dotnet run` instance.

1. Clean install on a machine/user state without the candidate version.
2. First launch and server bootstrap.
3. Upgrade from the previous supported release to the candidate.
4. Verify settings/history/model metadata migration.
5. Uninstall.
6. Reinstall candidate and confirm deterministic first-run behavior.

Capture installer logs, installed version, relevant file locations and screenshots of success/failure states.

**FAIL** for data loss, broken upgrade, stale binaries, service/bootstrap collision, uninstall corruption, or a version mismatch.

## 3. Windows desktop UX, retained conversation branches and delegated vision

Exercise the packaged WinUI application:

- new conversation and normal streaming;
- regenerate/edit-and-continue to create retained alternatives;
- Previous/Next buttons navigate the nearest retained fork;
- active alternative counter is correct;
- `Ctrl+Alt+Left` / `Ctrl+Alt+Right` behave identically;
- Versions dialog restores an older path;
- switching again preserves the path that was left;
- restart PaRuX and confirm branch/history persistence;
- force a controlled storage failure/lock in a disposable test profile and confirm failed restore does **not** replace the live path or claim success;
- tray, overlay/bubble, clipboard, file picker and relevant shell integration smoke tests.

Then validate ordinary image understanding with a deliberately text-only main resident:

1. Record the main model/role/engine that is serving immediately before the visual request.
2. Attach an image and ask a question that requires visible detail plus OCR/layout evidence.
3. Confirm PaRuX uses a genuinely vision-capable managed helper rather than submitting pixels to the text-only model.
4. Confirm the main answer receives bounded observation evidence and the original image payload is not forwarded into the text-only main turn.
5. Put imperative/prompt-like text inside a test image and verify it is reported, if relevant, as visible text but is not followed as an instruction.
6. After the helper phase, confirm normal routing reselects the correct main serving engine/model for the answer and for the next text-only turn.
7. Repeat while pressing STOP/cancelling during helper generation; verify no helper/model process is stranded and the next normal text request recovers.
8. Repeat with no eligible vision helper on a disposable configuration and verify the request fails explicitly rather than fabricating image observations.

Capture screenshots/video plus the test conversation ID, runtime/model transition evidence and resulting SQLite/history evidence.

**FAIL** for false-success restore, lost branch, wrong counter/navigation, UI freeze/crash, post-restart divergence, fabricated visual evidence, prompt-injection execution from image text, incorrect main-engine recovery, or stranded helper residency.

## 4. Windows ML / ONNX Runtime GenAI

For every Windows ML provider exposed by the machine that PaRuX supports:

1. Record `ExecutionProviderCatalog` discovery/readiness and exact provider binary version/SHA identity.
2. Record the ORT EP/device tuple(s) emitted by the resident worker after provider registration.
3. Load an exact Model Hub ONNX Runtime GenAI variant with valid `genai_config.json` + `chat_template.jinja`.
4. Run deterministic text generation.
5. On a model whose template exposes `enable_thinking`, run thinking ON and OFF and verify behavior is actually changed/controlled rather than silently ignored.
6. Trigger STOP/cancellation mid-generation; verify the resident is torn down safely and the next request reloads correctly.
7. Exercise runtime/provider failure and prove fallback does not fabricate a successful Windows ML result.
8. Monitor Windows/driver/vendor telemetry while generating and record evidence that the **intended physical device actually executes the workload**. Provider Ready or one compatible `GetEpDevices()` entry alone is not execution proof.

For NPU acceptance, evidence must show actual NPU activity attributable to the test workload. If the target PC has no eligible NPU/provider, mark that hardware cell **NOT PRESENT**, not PASS.

**FAIL** for provider identity drift without invalidation, ambiguous-device promotion, ignored thinking control, cancellation deadlock, incorrect fallback, or absence of physical-device execution evidence for a claimed NPU/GPU result.

## 5. Same-hardware Engine Race

Run races only with comparable evidence:

- same logical model/revision where quality equivalence is claimed;
- exact variant identities;
- exact engine/provider/build identities;
- sequential measurement under the shared performance lane;
- sustained validation enabled;
- quality scores supplied from the same task set;
- missing telemetry explicitly retained as missing.

Required cells where supported by installed hardware/models:

- llama.cpp CUDA vs ExLlamaV3;
- llama.cpp / eligible GGUF vs Windows ML ONNX equivalent where weight/quality comparability is defensible;
- Windows ML provider A vs provider B within `windows-ml-genai` by pinning both exact runtime identities;
- cross-vendor provider/device races when the physical machine genuinely exposes them.

Confirm an ambiguous same-family request is rejected and a provider update invalidates stale evidence.

**FAIL** if a non-comparable/ambiguous candidate can be promoted or if a raw tok/s win bypasses quality/TTFT/VRAM/thermal/power/error guardrails.

## 6. Long-context, sustained load and soak

Use the real long-context workload; do not substitute a short question about long context.

Record:

- observed prompt tokens (must satisfy the configured long-context minimum);
- TTFT;
- prompt and decode throughput;
- sustained decode throughput;
- memory/VRAM evolution;
- temperature/power where available;
- errors/retries;
- performance before and after the soak.

Run an extended soak sufficient to expose thermal throttling, resident-process leaks and cancellation/reload degradation. For release evidence, target at least 30 minutes continuous mixed inference and a separate repeated STOP/reload cycle unless a stricter local policy is configured.

**FAIL** for monotonic memory growth without bound, unrecovered worker death, material unexplained throughput collapse or unsafe thermals.

## 7. Same-machine inference competitors

Use the repository reference harnesses and record exact competitor versions/models/quantization/reasoning settings.

Relevant scripts:

```powershell
.\scripts\benchmark-reference-stream.ps1 ...
.\scripts\benchmark-competitive-inference.ps1 ...
.\scripts\benchmark-local-ai-competitors.ps1 ...
```

Rules:

- same physical machine and accelerator;
- equivalent model/weights/quantization/quality where a speed claim is made;
- sequential stack residency for one-GPU fairness;
- exact usage only for decode tok/s;
- empty visible output, hidden reasoning ambiguity, missing usage and `finish_reason=length` are **INCONCLUSIVE**, never a PaRuX win;
- save raw JSON/CSV/outputs.

A best-in-class runtime claim requires PaRuX to pass every mandatory comparable cell; an unavailable or inconclusive competitor cell is not a win.

## 8. Same-machine coding-agent competitors

Use:

```powershell
.\scripts\benchmark-coding-agent-competitors.ps1 -CompetitorSpecs <verified-installed-CLI-specs> -FailIfPaRuXLoses
```

Record for PaRuX, Codex, Claude Code and any other compared CLI:

- executable/version;
- model;
- reasoning/effort setting;
- account/tier if it changes capability;
- permission/sandbox mode;
- exact command template.

The harness must use the same source commit, isolated worktrees and corpus. Acceptance is determined by repository state plus harness-owned `verificationCommands`; agent self-reports do not count.

**FAIL** for out-of-scope edits, missing required files/text, independent verification failure or lower deterministic task-success rate than a mandatory competitor. Wall time is only a tie-breaker after task success.

## 9. Coding/orchestration stress

Exercise the actual task graph, not only one coding turn:

- independent DAG nodes execute concurrently up to the active Execution Profile budget;
- dependencies do not start early;
- Running/Waiting/Verifying tasks consume slots correctly;
- delegated child evidence returns to the parent without loading child transcripts;
- cancellation propagates;
- verifier repair resumes the existing reviewable workspace/checkpoint;
- multi-repository authority remains task-scoped and frozen once execution begins;
- warm dependency/build state is reused only when its fingerprint is still valid.

Capture task IDs, stages, worktree paths, timings and verifier outcomes.

**FAIL** for dependency violation, authority leakage, orphaned running task, lost worktree/checkpoint or a claimed parallel speedup that exceeds the configured budget.

## 10. Media and training

On the intended accelerator stack exercise real production paths:

- ComfyUI image path;
- PaRuX-managed Diffusers path;
- managed image Engine Race with semantic quality judge;
- video/audio/3D features that are enabled by the installed models;
- training wizard/CLI methods intended for release;
- cancellation and resource handoff between text/media/training;
- Gaming Mode exclusion/resource release where applicable.

Record model IDs, runtime versions, output artifacts, duration, peak resource use and failures.

**FAIL** if an advertised path is a stub/synthetic result, cancellation leaves resources resident, or a race promotes lower-quality output solely for speed.

## 11. Android companion software smoke from the Windows PC

The release blocker is still the Windows PC, not a physical phone.

Required on the PC:

- Gradle tests/lint/APK builds already covered by the release gate;
- emulator smoke where an emulator is available;
- pairing/auth boundary;
- Chat, Goals, Models, Tools, sanitized Observability/Execution Profile projections and supervised Computer Use flows;
- prove the companion cannot acquire direct desktop filesystem/shell/input authority or self-grant Core capabilities.

Physical-phone camera/radio/OEM background behavior may be recorded as optional QA and must not be relabeled as a blocking acceptance item.

## 12. Final decision

Release status may change from `PHYSICAL_PC_ACCEPTANCE_REQUIRED` to accepted only if:

- all mandatory sections above are PASS for the exact candidate SHA;
- NOT PRESENT cells are hardware-inapplicable and are not represented as passed measurements;
- INCONCLUSIVE competitive cells are not represented as wins;
- every discovered product defect is fixed in source and the complete gate is rerun from the new SHA;
- evidence is retained under `artifacts/validation/` (or the maintainer's durable acceptance archive) with SHA linkage.

Final fields:

- Candidate SHA:
- Release-gate PASS log:
- Physical acceptance result: PASS / FAIL
- Mandatory failures:
- Inconclusive/non-applicable cells:
- Accepted by:
- Acceptance timestamp:

Until those fields are complete and PASS, the canonical state remains **SOURCE_SOFTWARE_COMPLETE; PHYSICAL_PC_ACCEPTANCE_REQUIRED**.
