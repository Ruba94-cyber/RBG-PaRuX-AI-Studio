# PaRuX Skills

A **Skill** is a declarative, inspectable capability — not a saved prompt. It declares which model role it prefers, which tools it may use, whether it needs Internet/Memory/RAG, what permissions it requires, and what platforms it supports. The server-owned registry (`PaRuX.Server/SkillRegistryService.cs`) is the source of truth shared by Windows and Android.

## Manifest

Built-in skills are JSON files under [`skills/built-in/`](../skills/built-in/). User-created and imported skills use the same `SkillManifest` contract and persist in `skills.db`; built-in files remain read-only.

Important fields:

| Field | Meaning |
|---|---|
| `id`, `displayName`, `description`, `category`, `version`, `author` | Identity and Inspector metadata. |
| `source` | `builtin`, `user`, or `imported`. |
| `platforms` | `Windows` and/or `Android`. |
| `preferredModelRole`, `allowedModelRoles` | Model-role policy. |
| `requiredCapabilities` | Runtime prerequisites. |
| `allowedTools` | Explicit tool allowlist; `*` means unrestricted and therefore requires every privileged permission. |
| `internetPolicy`, `memoryPolicy`, `ragPolicy` | `off`, `optional`, or `required`. |
| `slashCommand`, `triggerExamples` | Explicit/AUTO routing metadata. |
| `workflow` | Inspectable workflow summary, never hidden chain-of-thought. |
| `permissions` | Privileged capabilities described below. |
| `timeoutSeconds`, `retryPolicy`, `fallbackSkillId`, `confirmationPolicy` | Execution policy. |
| `parameters` | User-configurable skill parameters. |

## Permission matrix

`SkillPermissions` is enforced server-side when a user or imported skill is saved. Declaring a privileged tool in `allowedTools` without its matching permission is rejected before persistence.

| Permission | Covers |
|---|---|
| `readFiles` | File/directory reads, glob/grep/search operations. |
| `writeFiles` | Create, patch, rename or move file operations. |
| `deleteFiles` | File/path deletion and removal. |
| `shell` | Shell, terminal and command execution. |
| `network` | Web/HTTP/network access. `internetPolicy: required` also requires this permission. |
| `browser` | Browser-control tools. Browser tools require both `browser` and `network`. |
| `computerUse` | Desktop, mouse, keyboard and screen-control tools. |
| `externalProcesses` | Starting external processes/applications. |

The policy intentionally classifies only high-confidence privileged tool names/prefixes. Generic subsystem labels such as `git` or `memory` are not automatically treated as shell access. A wildcard `allowedTools: ["*"]` requires the entire privileged permission set.

The built-in catalog is covered by a regression test that feeds every built-in manifest through the same permission policy. Web Search and Deep Research explicitly declare `network: true` and `browser: true`; Computer Use explicitly declares `computerUse: true`.

## Lifecycle

- `GET /api/v1/skills` — full catalog, including user/imported skills and enabled overrides.
- `GET /api/v1/skills/{id}` — one complete manifest.
- `POST /api/v1/skills` — create/update a user skill; permission validation runs before persistence.
- `POST /api/v1/skills/import` — import an untrusted skill; permission validation runs before persistence.
- `POST /api/v1/skills/{id}/enabled` — enable/disable a skill.
- `DELETE /api/v1/skills/{id}` — delete user/imported skills only.
- `POST /api/v1/skills/route` — preview routing without running a chat turn.

A built-in ID can never be shadowed by a user/imported skill.

## Routing

Selection order is deterministic and inspectable:

1. **Forced** — `ChatCompletionRequest.SkillId` selects a named enabled skill.
2. **Slash command** — `/research`, `/presentation`, `/image`, `/code`, etc.
3. **AUTO** — case-insensitive matching against `triggerExamples`; `general` is the fallback.

The selected skill is surfaced through the existing additive SSE `skill_selected` event. Skills can also constrain Memory, RAG, Internet and outbound MCP tool schemas through the Context Engine.

## Outbound MCP boundary

A skill's `allowedTools` is an allowlist over outbound MCP schemas and dispatch. The Context Engine advertises only allowed schemas and `McpClientRegistry.CallToolAsync` checks the same allowlist again before dispatch. Tool-call count, schema-character and result-character budgets are separately bounded.

Permission validation is an additional boundary: an imported/user manifest cannot obtain a clearly privileged tool merely by naming it in `allowedTools` while leaving the corresponding permission false.

Configured external MCP servers remain user-controlled local processes. PaRuX does not auto-discover or silently enable them.

## Imported skills

Imported skills are untrusted by construction (`source: imported`). The client should display their requested permissions before enabling them. The server still validates the manifest independently, so UI disclosure is not the only security boundary.

## Android

Android does not gain Windows-only capability merely because a skill exists. Platform declarations and the server-side restrictions on Coding Agent, arbitrary shell, VS Code control and Computer Use remain authoritative. Skills are a routing/capability layer, not a way around platform permissions.

## Development rule

Do not add a built-in skill for a backend that does not actually exist. When a capability is partial, the manifest and UI must represent that state accurately rather than advertising a fake happy path. New privileged tool names should be added to `SkillPermissionPolicy` with regression tests when their semantics are unambiguous.
