# PaRuX Universal Local AI Gateway

PaRuX exposes its local models to external AI clients (coding agents, chat apps, SDKs) through a
**loopback-only gateway**. Clients keep their own UI and agent loop; inference runs on the models
PaRuX manages on this PC. No cloud account and no installed Ollama are required.

| | |
|---|---|
| Base URL | `http://127.0.0.1:11435` (Settings › Integrations › Advanced to change the port) |
| Auth | PaRuX API key (`parux-sk-…`) as `Authorization: Bearer`, or `x-api-key` for Anthropic clients |
| Bind | `127.0.0.1` only. The gateway never listens on the LAN |
| Served paths | `/v1/*` model APIs and `/health`. Everything else, including PaRuX's control plane, returns 404 on this port |

## Protocols

| Protocol | Endpoint | Streaming | Tools |
|---|---|---|---|
| Anthropic Messages | `POST /v1/messages` | True SSE (`message_start` … `message_stop`, `ping` keep-alive) | `tool_use` / `tool_result` |
| Anthropic token count | `POST /v1/messages/count_tokens` | — | Counted with the served chat template (`X-PaRuX-Token-Count: exact`), or labelled `estimated` |
| OpenAI Responses | `POST /v1/responses` | True SSE (`response.created` … `response.completed` / `response.incomplete`) | `function_call` / `function_call_output`, `previous_response_id` |
| OpenAI Chat Completions | `POST /v1/chat/completions` | Provider SSE passthrough | `tool_calls` |
| Models | `GET /v1/models` | — | — |

The Ollama API (`/api/chat`, `/api/generate`, `/api/tags`) is also emulated on the Core port
(`51123`) for tools built for Ollama. It is served by PaRuX itself.

### Streaming model

All protocols are encoded from one internal stream: the local runtime's chat SSE, parsed into text
and tool-call deltas.

- **Text** reaches the client token by token. Measured: Anthropic first token in 0.5 s on a warm fast model.
- **Tool calls** are emitted as soon as their arguments are complete, which is when the next call starts or the model finishes. They are validated first, like a buffered answer.
- **Keep-alive:** while the model is still processing a long prompt, the stream sends protocol keep-alives every 10 s.
- **Cancellation:** a client that disconnects stops local generation (measured about 1 s). Gaming Mode cancels gateway requests like PaRuX's own chat.
- **Honest truncation:** a turn cut off by the token limit ends as `max_tokens` (Anthropic) or `response.incomplete` (Responses).
- **Reasoning:** Anthropic requests think only when they send `thinking`. Claude Code does, so it keeps reasoning. A plain request without it cannot lose its whole `max_tokens` budget to hidden reasoning.

## Models and routing

| Model id | Meaning |
|---|---|
| `parux-auto` | PaRuX chooses: tool-using agent requests go to the measured best coding model, other requests are routed by the task |
| `parux-code` | Best measured local coding/tool-use model |
| `parux-reasoning` | Local reasoning model |
| `parux-fast` | Fastest capable local model |
| `parux-vision` | Local vision-capable model |

Vendor ids that clients send by default (`claude-*`, `gpt-*`, `o*`, `codex*`) map to the
**vendor default** (`parux-auto`, adjustable in Advanced settings). Unknown ids are rejected.

**One instance is shared by every client.** Requests for the same role share the resident model
(measured: four concurrent clients, 0.6–0.8 s each, no reload). Requests for different roles are
serialized around a single swap (measured about 10 s for the 27B coder). Models stay resident until a
Gaming, media or OFF transition releases them, so a user switching between clients of the same kind
never waits for a reload.

The last routing decision (requested id, alias, role, model file) is shown in Settings › Integrations
and returned by `GET /api/v1/gateway/status` (desktop only).

## Client integrations (Settings › Integrations)

Each client is detected and shown with one of these states: *Not installed*, *Detected*,
*Available*, *Connected to PaRuX*, *Using another provider* or *Configuration error*.

**Connect** does the following:
1. Checks the gateway.
2. Issues a per-client key. The key is stored DPAPI-protected in PaRuX's manifest.
3. Writes only PaRuX's own entry, backing up anything it changes.
4. Runs a real test: the client itself must create `parux_<client>_test.txt` containing exactly `PARUX_<CLIENT>_OK` through the gateway.

Connect is idempotent.

**Disconnect** removes only PaRuX's entry and revokes the key. **Restore** puts the pre-connect file
back byte-for-byte. **Launch** opens the client in a console on the chosen folder, already
pointed at PaRuX.

| Client | Protocol | How PaRuX configures it | Your own setup |
|---|---|---|---|
| Claude Code (tested 2.1.283) | Anthropic Messages | Isolated profile (`CLAUDE_CONFIG_DIR` under the PaRuX data root). The key is injected at launch only and never written in clear text. Launch uses a lean tool set so requests fit a 16k local context | `~/.claude` is never touched |
| Codex CLI (tested 0.158; also the CLI bundled with Codex Desktop) | OpenAI Responses | Standalone profile `CODEX_HOME\parux.config.toml`, used with `codex --profile parux` | `config.toml` is never edited |
| OpenCode (tested 1.18.22) | OpenAI Chat Completions | Adds provider `parux` to `~/.config/opencode/opencode.json` after a backup. Symlinked files are written through the link | Other providers and your default model are kept. Files with comments are refused rather than rewritten |
| Any OpenAI- or Anthropic-compatible app | Either | Copy the base URL and create a key in the generic card | — |

The Codex profile file and `opencode.json` hold the key in clear text in your user profile, the
same way those clients store their own keys. A PaRuX key only reaches loopback model APIs: it
cannot read conversations, change settings or reach anything else.

Integration test runs are contained:
- Claude Code uses `acceptEdits`.
- Codex uses its `workspace-write` sandbox.
- OpenCode runs with shell and web fetch denied.

All three run in a scratch folder. Launched processes never resolve programs from the project
folder (`NoDefaultCurrentDirectoryInExePath`).

### Manual setup

```bash
# Claude Code
ANTHROPIC_BASE_URL=http://127.0.0.1:11435 ANTHROPIC_AUTH_TOKEN=parux-sk-... ANTHROPIC_MODEL=parux-auto claude
```

```bash
# Codex (profile file written by Connect)
codex --profile parux
```

```bash
# OpenCode (provider written by Connect)
opencode --model parux/parux-auto
```

## Verified results (2026-09-30, RTX 5080, Qwen3.8-27B IQ4_XS coder, Qwen3.5-4B fast)

| Check | Claude Code 2.1.283 | Codex 0.158.0-alpha.2.1 | OpenCode 1.18.22 |
|---|---|---|---|
| One-click Connect from the real UI (file test) | Pass, 33 s | Pass, 31 s | Pass, 20 s |
| File test over true streaming | Pass, 17 s | Pass, 16 s | Pass, 19 s |
| Agentic bug fix (read, edit, run `node test.js`), verified independently | Pass, 21 s, 5 turns | Pass, 29 s | Pass, 31 s |
| Disconnect / Restore leaves the user's config byte-identical | Yes | Yes | Yes (Restore) |

## Limits

- The coding model runs with a 16k context. Claude Code's full default tool set (about 18k tokens) does not fit, so PaRuX's Launch drops tools with no local use. Very long agent sessions depend on the clients' own compaction.
- Tool-call arguments are sent when each call is complete, not character by character. Text is streamed live.
- Responses continuation (`previous_response_id`) is bounded and in memory. It does not survive a PaRuX restart.
- Hosted tools such as web search, code interpreter and MCP connectors are not executed by PaRuX. Only function tools are forwarded.
