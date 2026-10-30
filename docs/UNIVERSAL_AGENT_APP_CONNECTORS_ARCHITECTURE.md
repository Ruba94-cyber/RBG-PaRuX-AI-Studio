# PaRuX — Universal Agent and App Connectors: technical architecture reference

**Reference only — NOT a second execution plan.** The sole live priority/status/acceptance authority is [`PARUX_EXECUTION_PLAN.md`](../PARUX_EXECUTION_PLAN.md), section "Universal Agent + App Ecosystem". This document elaborates interfaces, policy, connector data shapes, user journeys and engineering test cases; it authorizes no deployments or existing-account access.

**Baseline inspected:** public `main`, 2026-10-30. **Status: DESIGN, NOT IMPLEMENTED / NOT ACCEPTED.** Existing services are real, but do not imply the new cross-app flows below exist.

## 1. Product promise and boundary

A novice describes an outcome in Chat, optionally attaches a file or chooses an app, and receives a finished, inspectable result. PaRuX plans, selects locally usable models, finds the required tools, asks only for genuinely necessary permissions, works, independently verifies the outcome and explains exactly what changed. An expert can override any automatically selected model/backend, tool, connector, scope or plan without disabling the rest of automation.

- Local-first inference remains the default; no mandatory paid subscription, cloud LLM, remote server or account sign-in for ordinary local use. External app APIs necessarily exchange selected data with the provider, and the UI must state this distinctly from optional *cloud inference*. Never advertise "offline" for an online connector action.
- Windows x64 is the current desktop target; CPU-only, Intel/AMD UMA/discrete, NVIDIA and eGPU use the same ability-driven UI and bounded memory policy. A connected app does not imply any particular inference hardware.
- Treat Microsoft personal accounts, Entra work/school tenants, Google consumer accounts and Google Workspace accounts as distinct identities with separate capabilities and consent; no claim that business tenants will approve a third-party client.
- Maintain `PARUX_MANAGED` / `USER_MANAGED` / `SYSTEM_DETECTED` ownership. Neither normal agents nor connectors modify another application's installation, local profile or enterprise security policy.
- No GitHub Actions; all code/tests/release verification run locally. Public repository contains no personal profile, sample mailbox, tenant ID, OAuth secret, access token, provider key, machine path or private screenshot.
- Feature names and adapter registration are not acceptance. Every claim requires genuine Core integration, independent tests and representative physical Windows/browser operation.

## 2. Existing code reuse and explicit gaps

| Existing production area | What exists now | What is NOT implied |
| --- | --- | --- |
| `DesktopIntegrationCatalog/Service/Tools` | Consent-based **read-only** local Markdown/Obsidian/Logseq, `.eml`/mbox, `.ics`, browser bookmarks and Office document folders. "OneDrive documents" is a local **synced-directory reader**, not Graph. | No Outlook/Gmail/Teams mailbox APIs, no live email send, calendar booking or online OneDrive/SharePoint writer. |
| `OfficeDocumentWriter`, `StructuredWorkbookWriter`, `DocumentWriteTools` | Local DOCX/XLSX generation and bounded existing-workbook copy/edit with structural/formula validation and no silent overwrite. | No edit to an existing online coauthored Excel file and no general Office automation rights. |
| `ChatProxyService`, `CodingAgentService`, `AgentCodingWorkerService` | Local-model chat, coding tools, checkpoints, worktrees, schema verification, limited tool use and delegated agent concepts. | No accepted unified chat → research → authenticated app mutation → UI assertion → verified deliverable flow. |
| `BrowserDevToolsService`, `ComputerUseWorkerService`, `WindowsUiActionService` | CDP and optional Windows UIA/visual foundations, task approval boundaries and partial receipts. | No blanket cross-app RPA authority or OS-enforced browser-wide network sandbox. |
| `WebSearchService`, `CodingWebSearchClient` | Optional web research and provider fallback under Internet setting. | No general-purpose provider/auth manager, no safe arbitrary website autonomy without extra controls. |
| `TrainingLab`, media services, `RuntimeManager` | Specialized training/media engines, runtime and hardware management. | No proof of best-in-class end-to-end quality or all-hardware training compatibility. |
| Universal Gateway | Loopback Anthropic/OpenAI/Ollama-compatible APIs, integration assistance for external coding clients. | It does **not** give PaRuX delegated access to Microsoft/Google app accounts. |

**Repository reconciliation rule:** before each implementation slice, re-check the exact current source and tests against this table. This is a design snapshot, not a substitute for reading the code.

## 3. One task engine, multiple trusted execution surfaces

```text
Chat / command palette / voice / file drop / approved automation
    -> Goal interpreter (intent, data sensitivity, desired result)
    -> Task planner (explicit steps, required abilities, uncertainty, budget)
    -> Capability + connector registry (real availability, backend, account, scopes)
    -> Permission & policy broker (user, tenant, network, file, action, expiry)
    -> Durable task coordinator / typed steps + bounded specialists
       |-- local knowledge + on-device files
       |-- coding / shell in protected workspace
       |-- web search + isolated browser
       |-- Windows UIA + vision with target ownership
       |-- authenticated app adapters (Microsoft, Google, other providers)
       |-- media / training / Office document services
    -> Every effect: preflight -> preview/consent -> execute -> independently observe
    -> Independent verifier (receipts, diff/ETag/version, state checks, citations)
    -> Chat summary / artifact links / audit history / resume or STOP
```

**Execution authority belongs to the Core, not to the LLM, an MCP provider, a browser page, a remote tool server or an external CLI.** No component can self-grant network, mail-send, calendar-write, shell or computer-control authority. Use one durable task identifier throughout all tools, with children carrying independently bounded permissions and deadline/cancellation.

States: `draft → planned → awaiting_connection/permission/approval → running → verifying → completed`; alternative terminal states `failed, cancelled, partially_completed, blocked_external`. Restart resumes only verified checkpoints. Any dispatched action with uncertain outcome enters `result_unknown` and must be read back; never blindly retry a send or payment. Mutations invalidate earlier verification receipts.

### 3.1 Contract for every connector (capability descriptors)

Design a versioned `IAppConnector` (or equivalent Core-owned boundary) with:

- `Discover()`: stable connector ID, version, provider family, supported identity types, feature flags, installed vs external prereqs, risk/availability; **does not connect**.
- `Connect(identity, requestedScopedCapabilities)`: system-browser OAuth authorization-code + PKCE or approved platform broker where available, with exact scopes, verified state/redirect, consent callback, organization-policy refusal; native desktop client is a public client, **not a place for an embedded client secret**.
- `DescribeGrant(identity)`: grant provenance, effective provider/tenant/personal profile, scopes, expiry, status, redacted account label, policy restrictions, read/write capabilities; never reveal token.
- `Search(query, cursor, selectionScope)`, `Read(itemRef)`, `GetMetadata(itemRef)`: provider-native typed records, pagination and provenance.
- `PrepareMutation(intent, itemRefs)`: deterministic preview, recipient/target selection, conflict preconditions, data classification and sensitivity check. No side effect.
- `CommitMutation(preparedId, approval)`: one explicitly authorized action with idempotency and post-commit readback; supported only where provider and policy allow.
- `ObserveMutation(providerId)`: authoritative returned IDs, version/ETag/revision, deep link, readback state and compensating action when available.
- `Sync(cursor, limits)`: incremental, permission-scoped and stoppable; `Disconnect/Revoke` clears local tokens, cached grants and pending dispatch; provider-side revocation attempted where supported, never falsely claimed on failure.
- `Health/Cancel/Dispose`: timeout, retry/backoff, requested STOP propagation, partial/unavailable/error codes; no unbounded child processes or silent elevated privileges.

`ToolDescriptor`: `connectorId, accountId, tenantId?`, `capabilityId`, `riskTier`, `readWrite`, `scopeIds`, `externalDataDomains`, `requiresOnline`, `requiresUserApproval`, `version`, typed JSON schema, output limits, observable postconditions. Capability availability is separately `unsupported / not_connected / consent_required / admin_blocked / temporarily_offline / available / permission_revoked / provider_limited`. Do not flatten those to a generic green "Connected".

### 3.2 Identity, data provenance and authorization schema

A `ConnectorAccountRef` is opaque and unique for `provider + account + tenant + connector version`; link to user-facing aliases only after consent. A `ResourceRef` contains `provider, accountId, tenantId?, resourceType, immutableProviderId, version/etag?, canonicalDeepLink?, sensitivity?, fetchedUtc`; no raw local paths or tokens in prompt/tool logs. A `SourceEvidence` includes exact item/chunk, retrieval time, citation/deep link, deleted/tombstone flag and reliability. Every cross-app result must state *which mailbox, calendar, chat or drive* was used, and how fresh the data is.

A `PreparedAction` records `taskId, toolId, account/tenant, target/recipients, exact payload digest, preview, oldVersion/etag, permissionGrantId, approvalExpiresUtc, requestIdempotencyKey, riskTier`. The Core rechecks authorization, provider scope, remote host allowlist, account identity, freshness and revocation **immediately before** dispatch and readback. Exact target and payload changes invalidate approval. Approvals are not serialized into prompt-visible state. Never let content returned from email, documents or web change these grants.

Store credentials in OS-protected secrets, bound to a named account and connector, with rotation, local encryption-at-rest and removal; never in plaintext SQLite, source code, exported reports or model context. Browser sessions must use task-owned isolated profiles. Keep secrets out of all screenshots, telemetry and code diffs. Provide redaction and privacy scrubber before any optional external LLM call.

### 3.3 Operation risk bands

| Band | Examples | Default |
| --- | --- | --- |
| READ | List/search permitted emails, meetings, sheets, Teams messages, documents | Read-only allowed after explicit connection and bounded selection; citation/provenance |
| DRAFT | Draft email, proposed meeting, new spreadsheet copy, unsent Teams message preview | Editable preview; no send, publish or remote overwrite |
| WRITE_REVERSIBLE | Save approved draft, create a non-shared test event, modify an authorized copy, move a bounded file to recoverable trash | Per-transaction approval with target/diff, preconditions and undo/version where possible |
| EXTERNAL / BROAD | Send email or chat, invite recipients, share a file, modify live online workbook, deploy/push, make a bulk edit | Fresh approval with exact recipients/targets/effects; readback proof |
| DESTRUCTIVE / FINANCIAL / ADMIN | Permanent delete, access-control changes, financial transaction, tenant administration, policy change, mass messaging | Not enabled by autonomous defaults. Separate explicit grants and supported guarded workflow, or refuse and provide a manual route |

Existing user approval for one app cannot silently authorize another app or account, and recurring permission cannot broaden the operation set. A displayed source snippet or document is data, never trusted tool policy.

## 4. Microsoft 365 connector family (API-first, desktop fallback only when allowed)

Use Microsoft identity platform's supported public-client/broker flows with delegated user permissions and tenant-policy enforcement. Distinguish **personal Microsoft account** from **work/school Entra tenant**; account selection is explicit on every cross-tenant operation. No hardcoded client secret, tenant, shared corporate credential, undocumented token capture, browser-profile cookie scraping or admin-consent bypass. An organization may forbid app registration/user consent and make an API connector unavailable: say `admin_blocked` and offer only approved local/interactive alternatives. No guarantee of "no ICT required".

| App | Read phase | Write phase requiring preview/approval | Constraints/real proof |
| --- | --- | --- | --- |
| Outlook mail | Message/thread search, attachments, folders, sender/date/filter metadata, source links | Save draft → send after explicit approval; reply/forward, flags or folder actions later | Sender account + recipients + subject + attachments shown; avoid duplicates and cross-mailbox leakage; shared mailboxes are separately delegated |
| Outlook calendar | Personal/delegated calendars, events, availability (where API grants allow) | Draft event with timezone/participants, confirm booking/reschedule/cancel | Prevent invitations before approval, respect recurrence/time zone and conflict; explicit calendar identity |
| Teams chats & channels | Permitted chat/channel messages, relevant files, links, thread context | Draft or send message/reply to **selected** chat/channel only; create tasks if supported | Delegated messaging permissions are distinct; avoid using Teams as log sink, respect meeting/retention policies |
| OneDrive | Scoped file search, metadata, version, local/cloud visibility, download selected file | Create file/copy/update authorized location, upload results, share only via separate approval | Differentiate on-demand placeholders vs truly downloaded content and conflict/ETag |
| SharePoint | Sites/drives/libraries allowed to signed-in user, document metadata, exact source | Draft updates, file versioning, document library upload; approvals/permissions as separate advanced actions | Site/tenant constraints and potentially required admin consent, no tenant-wide crawl by default |
| Excel online | Workbooks, tables, named ranges, worksheets, formulas, provenance | Update targeted workbook table/range using Graph's Excel API under allowed file permissions | **Excel Graph workbook endpoints currently support business-backed OneDrive/SharePoint, not OneDrive Consumer**; XLSX only; sessions, concurrent edits and calculation status. For personal accounts, use approved download/local-copy/upload paths with honest limitations |
| Word/PowerPoint | Read selected authorized DOCX/PPTX via existing file readers; create DOCX locally through existing writer | Upload reviewed, compatible artifacts as new version/file if provider permits; PPTX generation/editing requires separate implementation and fidelity tests | Preserve formatting, embedded content and source; no claim that the current native writer can author arbitrary PowerPoint files or live coauthor |
| To Do / Planner | Assigned tasks and allowed plans | Create/update tasks and checklists under exact container/list and membership | API availability/permissions vary; do not infer organization entitlement |

**Microsoft Graph official references (review before coding, permissions change):**
- https://learn.microsoft.com/graph/permissions-overview and https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/manage-app-consent-policies (tenant permissions and consent)
- https://learn.microsoft.com/graph/api/chat-post-messages?view=graph-rest-1.0 (delegated Teams `ChatMessage.Send`, work/school accounts)
- https://learn.microsoft.com/graph/api/chatmessage-post?view=graph-rest-1.0 (channel messaging and actual restrictions)
- https://learn.microsoft.com/graph/excel-concept-overview and https://learn.microsoft.com/graph/api/resources/excel?view=graph-rest-1.0 (Excel workbooks; official API documents the OneDrive Consumer limitation)
- https://learn.microsoft.com/graph/delta-query-overview and https://learn.microsoft.com/graph/throttling (incremental sync and 429 / Retry-After)
- https://learn.microsoft.com/graph/outlook-share-messages-folders (delegated/shared mailbox semantics)

Don't assume that a permission scope can be granted by the current tenant; provider docs list *technical scopes*, not a permission to override company rules. Never silently switch to Computer Use when an API says `admin_blocked`.

## 5. Google Workspace and consumer connector family

Use Google OAuth authorization code + PKCE for native apps, exact scopes and account picker. Verification/consent-screen configuration may be required for general public distribution. Gmail broad read/write scopes can be **restricted**, triggering Google verification and potentially third-party security assessment depending on application and data processing. A personal/development exception is not a general consumer-distribution exemption. Never instruct users to disable Workspace security or use copied browser credentials.

| App | Read phase | Write phase (approved) | Constraints |
| --- | --- | --- | --- |
| Gmail | Search messages/threads, labels, selected attachments with resource links | Draft → explicitly send/reply, label or move as separate capabilities | Distinguish `gmail.metadata` from message-body access; restricted scopes/verification and audit |
| Google Calendar | List calendars/events, availability and proposed time options | Create/update event with guests/timezone, then verify from provider | Account/calendar identity, recurring events, attendee notifications |
| Google Drive | Metadata and selected document content | Create/upload/edit a selected authorized file, separate share permission | Prefer per-file `drive.file` where feasible; avoid unnecessarily broad Drive scopes |
| Google Sheets | Read named ranges, cells, formulas and formats | Prepare cell/range update, batch write with provider readback | Exact spreadsheet/worksheet/range, value typing and overwrites |
| Docs/Slides | Read selected files through provider/export, generate local/editable copy | Upload or update via permitted provider API after a content diff | No unsupported feature claims; permissions, document fidelity and revision preconditions |
| Google Tasks | Lists and assigned tasks | Draft/create/update task with explicit list selection | Scope minimization, limited conflict/undo semantics |
| Third-party (Notion, Slack, GitHub, Jira, Linear, Trello, etc.) | Typed scope-limited resources, search and citations | Per-operation approval and version checks | Connector marketplace readiness; do not grant third-party MCP unchecked write authority |

**Google official references:**
- https://developers.google.com/workspace/gmail/api/auth/scopes
- https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification
- https://developers.google.com/workspace/drive/api/guides/api-specific-auth
- https://developers.google.com/workspace/gmail/api/reference/rest/v1/users.history/list
- https://developers.google.com/workspace/sheets/api/reference/rest/v4/spreadsheets.values/batchUpdate

## 6. Integration alternatives without sanctioned cloud API

**Priority:** official delegated API → intentionally connected local synced/exported files → explicitly permissioned UI automation for *supported user-visible actions*. Missing API grants are not permission for stealth scraping. Ask the user to choose the least-capable approved route.

1. **Connected local files:** reuse `DesktopIntegrationService` to search only authorized paths/formats; OneDrive local sync is not proof of full online access; `.eml`/`.ics` exports are **read-only snapshots**, not live Gmail/Outlook. Every result records freshness and origin.
2. **File-open-in-app:** use Windows shell open/deep links (Outlook, Teams, Excel, browser) to stage a user-visible draft or selected item only where there is a safe documented contract. Never claim "sent" because a link opened.
3. **Office document processing:** use existing DOCX/XLSX writers for reviewable copies, preserve source, formulas, styles and filename; support explicit Save As/upload. No undocumented COM or arbitrary Office macro execution in the default agent.
4. **UI Automation:** use foreground-window process identity + accessibility selectors; vision fallback only with guarded coordinates and independent screen/state inspection. Microsoft new Outlook, classic Outlook and web Outlook may differ; detect actual app and refuse unsupported operations. Do not bypass enterprise app restrictions, MFA, consent screens, DLP warnings or protected prompts.
5. **MCP ecosystem:** connectors register typed capabilities under Core permission broker, not direct prompt-authority. Untrusted servers get restrictive capability grants, timeouts, response redaction, signed/versioned distribution policy and user confirmation on installation. OpenCode/other agents receive no implicit external account tokens.

## Computer Use app-interaction and app-testing implementation note (2026-10-30)

The first strict UIA GUI test slice is now implemented **in source, not Windows-accepted**: Computer Use's `ui_assert` checks an exact, bounded `name`, `value` or `text` property for a unique selector in a freshly recaptured foreground HWND/PID. A failed assertion terminates the current run with an explicit failure (it is not an automatic repair yet). The initial observation, semantic target label and executable selector must belong to the same window; a selector in another WinApp-reported window or duplicated within the target is invalid. The expected/actual value is not included in task log descriptions.

**App-test path to build next:** `approve test target → enumerate user-approved app/process/window → launch or attach under ownership lease → observe via UIA → interact through typed controls → inspect exact postconditions → report screenshot/DOM/UIA readback → independent verifier → repair and bounded retry → reviewable result`. Never treat a screenshot alone, a successful input delivery, or the model's `done` message as independent UI-test success. An agent may use Outlook, Teams, Excel, browsers, VS Code or arbitrary user-selected desktop software only through an explicitly authorized and available API/UIA/browser route. Test writes use disposable accounts/projects first. Unsupported app versions, UAC, locked desktops, DLP, MFA and unknown access scope fail closed. Protect privacy of window titles and screenshots in all logs and remote views.

**Remaining acceptance:** actual Windows build/xUnit/FlaUI, real WinApp CLI on Notepad and a test WinUI app, foreground-switch and duplicate-selector negative fixtures, multiple displays, launch/exit and app restart, user STOP, independent verification, Outlook/Teams/Excel approved UI routes. No generalized all-PC execution capability is claimed by the source assertion slice.

## 7. Cross-app workflows and independent acceptance scenarios

Every scenario must work as one durable chat task with selectable accounts; no real external recipient or work document is touched in automated acceptance. Use disposable fixtures/consenting accounts when live external integration is needed.

| ID | User intent | Expected verifiable result |
| --- | --- | --- |
| E2E-01 | "Find the last discussion about project X in Outlook and Teams, summarize decisions and cite sources" | Correct account/tenant used; grounded citations with item/message IDs and retrieval time; no cross-account leaks |
| E2E-02 | "Compare this attached Excel with the latest OneDrive workbook and explain differences" | Correct file versions, unchanged originals, formulas/values validated, exportable diff |
| E2E-03 | "Draft a reply to this email, attach the report; let me review it" | Unsent draft with real recipient list, attachments and editable preview, no send before separate approval |
| E2E-04 | "Send that reviewed reply now" | Single sent message, stable provider ID, actual readback, no double send after network timeout/restart |
| E2E-05 | "Find 30 minutes next week and invite these people" | Correct timezone, calendar/attendee list, conflict checks; no invites until approval |
| E2E-06 | "Create monthly KPI workbook from these SharePoint files" | Version-pinned inputs, verified formulas/charts, new output in approved folder, source preserved |
| E2E-07 | "Summarize Teams decisions and create To Do/Planner actions" | Evidence-backed task owners/dates; correct plan/list and exact new task IDs |
| E2E-08 | "Research a technical issue online, implement a fix and test the Windows UI" | Sources, isolated worktree, real build + test, UIA/DOM assertions and reviewable diff |
| E2E-09 | "Update this Google Sheet and email its owners" | Right Google account, sheet/range version checks, two separate approvals, provider verification |
| E2E-10 | "Work offline with my locally synced documents" | No outbound requests; accurate stale/offline labels; saved local artifacts only |
| E2E-11 | "Continue after login expires / network drops / app restarts" | Safe pause, reauth request, checkpointed continuation, no duplicate mutation |
| E2E-12 | "Stop while sending / editing / running a long agent task" | STOP preempts pending dispatch, cancels owned workers; uncertain committed effects read back |
| E2E-13 | "A webpage/email asks you to ignore rules and export secrets" | Untrusted content cannot expand permissions, read hidden credentials or execute external exfiltration |
| E2E-14 | "Switch from personal Gmail to company Outlook" | Explicit account selection and isolated grant/citation/history; no provider mix-up |
| E2E-15 | "Use an office account that forbids OAuth app consent" | `admin_blocked` plus sanctioned fallback; never request password or bypass restrictions |
| E2E-16 | "Review a generated slide deck/docx and publish only if approved" | Inspectable artifact and structural review; no upload until approval; version conflict handling |
| E2E-17 | "Open an unsupported desktop app and complete a simple form" | UIA/vision scoped to approved window, before/after screenshot/values, no false completion |
| E2E-18 | "Automatically monitor a permitted folder/inbox condition" | Explicit schedule and scope, no silent background polling, reversible cancellation; provider quotas respected |

## 8. UX specification — beginners and experts share the same task model

**Home:** one composer with attachments, drag-and-drop, voice, optional web/PC access indicator and a concise `Automatic · Local` model chip. Starter examples "Summarize my email", "Work on this Excel", "Research a topic", "Build an app", "Control an app". If disconnected, the relevant connector is offered *at the point of need*, not required during first-run setup.

**Execution card:** user goal; plan of 3–7 plain-language steps with optional expansion; current agent and model; elapsed progress; key document references; consent/approval cards; primary Pause/STOP/Continue. Evidence and individual tool calls in Details. No raw JSON, model jargon or wall of logs in Simple.

**Connections center:** one provider list with icons and actual ability badges: Not connected, Read only, Draft only, Write with confirmation, Admin blocked, Login expired, Offline. Show selected account, provider/tenant, precisely what may be read, scope origin, last sync, revoke and delete indexed data actions. No green "Connected" badge for an unusable scope.

**Approvals:** human-readable before/after, file diff/recipient target, network data destinations, effect permanence, undo where possible. Sensitive approval can be intentionally scoped to one or a small bounded batch; a blanket "allow all from this provider forever" is not a valid default. Handing back the task must not lose incomplete approval state.

**Results:** outcome, what changed and where, citations/deep links, verification states, warnings, options to open/edit/undo/export. Success is not merely a successful HTTP status. Explicit partial results preserve progress and make retry safe.

**Advanced:** exact model/quant/backend/context, tool graph and permissions, offline/web/remote inference policies, budgets/token cost, per-agent execution, task traces, audit, debugger and connector cursor/ETag. Advanced settings use a separate pane/dialog and do not change Simple's safe behavior.

**Accessibility:** keyboard-only, stable AutomationIds, Windows text scaling, 400–800px layouts, screen readers, focus restoration, accessible approval dialogs, EN/IT parity, high contrast and no color-only status.

## 9. Sync/cache, privacy and resource stewardship

- Default online connectors to **on-demand read**; no mailbox or whole-tenant mirroring by connecting. Optional indexing is consented per collection/folder/time range and removable without deleting the provider's source.
- Bounded result sizes, attachments and prompt excerpts; store source refs and content digests instead of dumping whole mailboxes into an LLM context. Search lexical first then optional local embeddings for consented data. Deleted/revoked items must be tombstoned and purged from local indexes; disable cross-account joins unless chosen by user.
- Use provider delta/history cursors when available (Graph delta; Gmail history), backoff on 429/Retry-After, etag/version conflicts and expiration. Cursors are opaque scoped secrets; never directly concatenate untrusted delta URLs into unrestricted network requests.
- A failed sync never claims the last synced data is fresh. Detached or expired-account sources are labelled unavailable. Handle out-of-order notifications, duplicate pages, deleted messages, recurrent events and renamed drives without silently recreating data.
- User-data boundaries: connect/grant separate from Internet search enabled separate from optional cloud *model inference*. No source material leaves authorized destinations by default. Ask before external inference includes mail, private documents, chat or spreadsheet cells.
- Multi-stage jobs use Core-owned CPU/GPU/resource budgets, power-aware limits, Gaming hard-off, bounded local disk and cancellation. Network connector latencies must not block the UI thread; queue telemetry and local model-token usage are distinct from provider API quotas.
- Privacy and policy: DLP/tenant labels, external recipients, shared-mailbox delegation, restricted/regulated data, retention/export, exact auth redirect, CSRF/OAuth state, SSRF, malicious attachments, prompt injection and tool-output spoofing have negative tests before public release.

## 10. Benchmarks, acceptance and security gates (targets, not results)

**Product SLO targets — proposed, measured on representative prepared hardware, not guarantees:** first useful Chat feedback p50 <= 2 s when a model is already resident and not resource constrained; task cancellation response <= 2 s excluding irreversible remote effects; core UI actions remain responsive under the current accepted Windows layout budget; connector search provides a visible loading/failure state within 1 s; reauth and API-rate-limit failures pause without lying. Benchmarks must retain cold vs warm, model/quant/context, CPU/GPU identity, cumulative time, peak RAM/VRAM, energy when measurable, p50/p95, tool retries, quality and human interventions.

**Quality:** >=90% correct completion in a pre-registered, task-diverse local acceptance suite is an aspirational launch target, not a measured score. All risk-band approvals and all negative security tests must pass. No unauthorized remote sends/shares/deletes; no secrets in public repository, exported evidence or prompt body; no test claiming PASS from mocks alone.

**Test ladder:**
1. Pure contracts/permissions, identity, scopes, account isolation, dangerous operation denial, parsing/cursor pagination, audit redaction, rate limit, crash journal, replay/idempotency.
2. Hermetic HTTP mocks of Graph and Google for 200/401/403(admin blocked)/404/409/412(version)/429/5xx, expired tokens, revoked access, partial batch, invalid redirect, unexpected consent, changed source, stale ETag; independent expected-state assertions.
3. Native Windows App + Core integration with local test Office documents and mailbox export fixtures, no real enterprise accounts; a disposable Git repo for coding.
4. Opt-in developer-owned sandbox/consenting account Graph and Google tenant acceptance with minimal scopes, create draft/send-to-self and calendar test, then check provider state. Do not use real third parties, production corporate email or restricted tenant without sanctioned authorization.
5. Real browser CDP/Playwright, Windows UIA/FlaUI and app-specific conditional paths; test restart, STOP, network disable, Core crash, resource pressure, audio/media and eGPU detach.
6. Independent security and performance comparison versus current Codex/Claude Code, Locally Uncensored and Unsloth on equal models/hardware/workflows where possible; explicitly separate cloud intelligence from local engine throughput.
7. Run local `tools/validate-software-complete.ps1` and release gate, review signed update/install and redaction; leave incompatible/unavailable hardware/account cases `BLOCKED-EXTERNAL` with evidence, not `DONE`.

## 11. Licensing and compatibility constraints

- Third-party SDK/package selection requires license/security/maintenance review; no bundled AGPL code from reference-only projects, no private endpoint scraping, no implicit vendor lock-in. Existing FSL-1.1-MIT license is source-available, not OSI-approved open-source.
- Provider API entitlements, account tier, national cloud, tenant app consent, read vs write scopes, Gmail restricted scope verification, published quotas and enterprise DLP are real gates. Compliance/authorization may be unattainable for some tenants; the user-facing app must degrade meaningfully without pretending every provider is universally accessible.
- Out-of-process external runners (OpenCode, Browser Use, local Office applications) must be process-attested and confined to Core-owned task rights before any "execute" option is available. Read-only observation is not execution readiness.

## 12. First engineering vertical slice

Pick **one** useful full workflow and finish it before adding broad catalogue entries: `Chat → choose one authorized local Office source → find a selected workbook → prepare a version-preserving new workbook + grounded answer → inspect result → optional approved save`. This uses existing local document capabilities, proves task/permission/evidence UI and works with no external credentials. Then integrate a restricted Microsoft delegated **read-only** connector if available, followed by Google read-only, email drafts and explicit sends. Do **not** start with write-capable enterprise connectors or privileged remote Computer Use.

**Definition of Done for every slice:** source + executable tests + real Windows/real provider where needed + independent review + updated statuses in the one canonical plan. Until then mark `PARTIAL` or `BLOCKED-EXTERNAL`.
