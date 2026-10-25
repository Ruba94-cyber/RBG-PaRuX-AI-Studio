# PaRuX competitive source review — 10 October 2026

Status: **source-reviewed, adaptations in V2 draft branch, not Windows-accepted**.
Do not treat upstream stars, README performance claims, or architecture resemblance
as proof of a performance improvement in PaRuX. The branch is not release-ready.

## Exact sources reviewed

| Upstream | Immutable reviewed commit | License at root | Relevant reviewed files |
|---|---|---|---|
| [Jan](https://github.com/janhq/jan) | `f2cd837fabe99cd6b8bbe985891bab8782e4034b` | Apache-2.0 (README + LICENSE) | `src-tauri/src/core/agent/compaction.rs`, `context.rs`, `loop.rs`; `web-app/src/services/models/tokenCountToolContext.ts` |
| [Odysseus](https://github.com/odysseus-dev/odysseus) | `a8c147b238db01dbd00b57774ed8b453f1e43971` | AGPL-3.0-or-later | `src/agent_loop.py`, `src/model_context.py`, `tests/test_agent_evidence_loop.py`; README/roadmap |
| [OpenCode](https://github.com/anomalyco/opencode) | `055d95bb7e278c94baf06235a52cac79dd13ba67` | MIT | README, SDK/server protocol and permission documentation; read-only Core bridge in PaRuX |
| [OpenHands Software Agent SDK](https://github.com/OpenHands/software-agent-sdk) | `1cfba2117e92730b0fe1c32520a39aad8611bb8c` | MIT | `openhands-sdk/openhands/sdk/agent/stream_context.py`, context condensation and security test suites, README |

Upstream projects are changing quickly. Paths and behaviors below refer to the
pinned snapshots, not presumed current upstream head.

## Findings and decisions

### 1. Jan: proactive context recovery — ADAPTED

Jan can trigger conversation compaction before the provider refuses an oversized
request; its trigger is a fraction of the model window and can be overridden by
an explicit reserve. PaRuX already has stronger *managed-runtime* evidence in
one dimension: it can ask llama-server to count the **fully rendered**
system/user/JSON-schema prompt before inference, and it preserves tools and
checkpoint evidence independently.

**V2 change:** `CodingAgentProactiveContextPolicy` begins *lossless replay*
reduction at **85% of the usable input space** (`observedContext -
reasoning/output reserve`), only when an exact tokenizer count exists,
at least 3 observations exist, and there is a useful saving. It first shortens
replayed evidence, not task instructions, the schema, source files, or saved
artifacts. A second exact probe confirms the shortened prompt still fits;
if it cannot, PaRuX retains the previously admissible prompt. The pre-existing
hard-fit and explicit-overflow recovery remain the final protection.

**Not copied:** Jan's Rust transcript summarizer, model-summary fallback,
content splice and Rust invocation loop. Importing them would duplicate
existing PaRuX checkpoints, materially change the agent's trust model, and
introduce a second summarization inference cost. The new C# policy is
independently implemented using Jan's documented design insight, not a
verbatim translation.

### 2. Jan: project rules by directory — ADAPTED

Jan discovers per-directory project instructions in deterministic precedence.
PaRuX previously mixed `AGENTS.md` and `CLAUDE.md` into an unordered
manifest scan limited to depth two; deep or changed-scope instructions could be
lost behind unrelated manifests.

**V2 change:** `CodingProjectInstructionResolver` reads the root and relevant
ancestor directories **only within the task's authorized worktree**, root
before nearest scope, with `AGENTS.md` before `CLAUDE.md`, up to 10 files,
2,600 characters per file and 8,000 overall. It rejects traversal outside
root, directory junctions, file symlinks, unreadable or oversized files;
the manifest reader is separately sorted, bounded and symlink-safe. These
files remain **untrusted repository evidence**, never overriding PaRuX's
sandbox, permission checks, or mandatory independent verification.

This is C# code authored for PaRuX; no Jan source text was copied.

### 3. OpenHands: durable progress and stream retries — PARTIALLY ADAPTED

The OpenHands SDK identifies a stream item/attempt and distinguishes transient
token deltas from its committed durable event. PaRuX already has an ordered,
drainable `CodingStepStageReporter` and durable tool dispatch journal.

**V2 change:** `CodingStepStageReporter` now rejects delayed progress for a
step **older** than the last visible step, to prevent an asynchronous/retry
callback from regressing progress. Tests cover ordering and final draining.

**Open work:** end-to-end structured stream attempt IDs, cancellation
receipts and a UI regression test are not yet implemented. Do not claim
feature-equivalence with OpenHands.

### 4. Odysseus: model context and evidence loop — REFERENCE ONLY

Odysseus separates an endpoint/model's **known** context window from
unverified defaults, partitions model metadata by endpoint and tests repeated
tool/evidence loops. PaRuX retains its own measured `CodingContextBudgetResolver`,
`CodingAgentRepeatGuard`, typed tool policy and persistent checkpoint rather
than importing Odysseus's Python code or a guessed table of remote model limits.

**License constraint:** Odysseus is AGPL-3.0-or-later. No code, tests,
assets, algorithms transcribed from source, or Python modules are copied or
linked into PaRuX. A future actual code import must receive a distribution and
network-use copyleft compliance review first.

### 5. OpenCode: isolated executor — NOT YET IMPLEMENTED

Read-only health/session observation is present and the native backend
selection seam fails closed for an OpenCode execution request.
An OpenCode server does not inherit the PaRuX Windows AppContainer sandbox.
Calling an unattended full-access OpenCode agent is explicitly **not** an
acceptable implementation shortcut. Before execution, require independent OS
filesystem isolation, no-network by default, task-scoped grants, process-tree
STOP, verification and repair parity.

## Quality gates still to perform

1. Run `dotnet build`, xUnit and the Windows smoke/functional harness; no .NET
   SDK or interactive Windows UI is available in this tool environment.
2. Benchmark native context handling with **identical** local model and
   8K/32K windows: provider-overflow count, median/p95 step latency, repeat
   tool calls, token overhead, preserved artifact IDs, STOP/restart accuracy.
3. Exercise root/nested worktree instructions with changes in multiple
   repositories and junction/escape fixtures on a real Windows volume.
4. Verify live OpenCode observation fails closed, then build OS-supervised
   execution as a **separate** project with explicit risk acceptance.
5. Verify a real app: write code → build/test → launch → UIA interaction →
   functional assertion → independent verifier → reviewable diff.
6. Keep main, releases and GitHub Actions unchanged until release gates pass.

## Provenance and copying policy

Only PaRuX-authored C# algorithms and tests were added in this comparative
slice; no upstream Jan/OpenHands/OpenCode/Odysseus implementation files were
vendored. Jan and OpenHands are permitted examples under Apache-2.0/MIT,
but actual source reuse would require retaining their licenses and any notices.
Odysseus is AGPL and was intentionally **not** ported. Record exact license and
revision before introducing third-party source in a future commit.
