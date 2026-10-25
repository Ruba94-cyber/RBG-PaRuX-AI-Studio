# PaRuX local-first execution contract (2026-10-14)

Status: **implementation specification, not completed feature**. This document complements `PARUX_EXECUTION_PLAN.md`; it does not override BIC acceptance gates.

## Product invariant

PaRuX is a complete local AI studio. It must remain useful with networking disabled and without an external API key. Cloud/API providers are **optional, explicitly selected** and must never be silently substituted for a local model.

## Provider routing rules

1. Default provider is `local` for new installations and profiles.
2. `local-only` policy blocks all outbound inference requests, including fallback, regardless of client protocol or model alias.
3. `manual-api` allows a configured remote provider only when the user explicitly selects it for that task/session. Missing keys or failed requests return a visible error, not an implicit alternative. A task grant is usable only for the exact provider, normalized endpoint (scheme, host, port and base path), API key, model, task, message payload, per-token price and completion limit it was issued for; any other value, including another path on the same host, is refused before dispatch.
4. `approved-fallback` may be enabled separately by the user with an explicit scope (task or session), provider/model allowlist and spend/token ceiling. It must not be the default. Every remote decision without an approved ceiling is denied; an absent ceiling is never treated as unlimited.
5. Existing `parux-auto`, `parux-code`, `parux-reasoning`, `parux-fast` and `parux-vision` aliases remain local unless an explicit task policy permits remote execution.
6. API keys must be encrypted at rest (Windows DPAPI or stronger OS-bound store), redacted from logs and diagnostics, and never sent to local inference clients. A stored remote-provider key is attached only to the configured remote target, and repointing that target to another endpoint drops it.
7. Sending local files, prompts, memory, tool output or code to remote APIs requires an understandable disclosure and task-scoped consent; revocation must cancel pending remote work where feasible. An explicit remote payload is a list of text chat messages whose only properties are `role` and `content`; any other property is refused before dispatch, so nothing is sent that the consent did not describe. Explicit remote calls are admitted under the remote-provider scope, so withdrawing the remote opt-in cancels them, and an issued task grant can be revoked before it is consumed.
8. No remote provider may receive computer-control permissions merely by being selected as a language model. Tool execution remains behind PaRuX's existing authorization/sandbox layer.
9. Offline launch, chat, model management, coding tools and installed media pipelines must not depend on provider discovery endpoints or remote login.
10. Show actual provider, model, remote/local indicator, estimated and actual cost when known, and any fallback reason in task history.

## Implementation sequence

### P0: Inventory and regression baseline
- Inspect current provider definitions, routing, UI settings, secret storage, gateway endpoints and tests before modifying code.
- Record which of the rules above already exist; do not duplicate working functionality.
- Reproduce the external-project coding-agent failure described in the executive plan, using a synthetic target and a real UI workflow.
- Preserve existing user configuration and avoid unrelated refactors.

### P1: Provider policy enforcement
- Define a single policy contract shared by chat, agents, workflows, media and gateway.
- Enforce policy at the network dispatch boundary, not only in the UI.
- Add negative tests proving local-only never issues a remote request, including failed local inference, tool calls, retries and alias routing.
- Add explicit remote selection and opt-in fallback UX only where missing.

### P2: One coherent studio
- Consolidate provider/model selection, engine health, resource usage, active jobs and costs in Simple and Advanced modes.
- Preserve local llama.cpp/ExLlamaV3 and compatible engines; integrate existing tools instead of replacing stable runtimes.
- Keep Gaming Mode authoritative over local model lifecycle.

### P3: Agent and Computer Use acceptance
- Test create/edit/run/inspect/recover/approve flows on an external synthetic repository from PaRuX's UI.
- Enforce task-scoped permissions, rollback and independent outcome verification.
- Compare identical-model task success, TTFT, throughput and VRAM against established alternatives on the same PC.

## Acceptance checklist

- [ ] Offline clean-PC installation and startup work without credentials.
- [ ] Local-only mode makes zero outbound inference connections under fault injection.
- [ ] An explicitly chosen API provider works; keys are protected and revocable.
- [ ] No remote data disclosure without task-scoped authorization.
- [ ] User-visible routing and cost attribution are accurate.
- [ ] Coding agent successfully repairs an external synthetic project via the real UI, with verified tests.
- [ ] Windows physical-PC acceptance and remaining BIC gates pass.

## Constraints

No GitHub Actions or paid CI minutes. Run build, unit/integration/UI tests and benchmarks on the Windows development PC. Do not mark a task complete from documentation alone. Do not copy AGPL code into the MIT codebase without a reviewed licensing strategy. Preserve existing work and use a separate branch until verified.
