# .ai — local agent orchestration workspace

This directory contains the versioned templates for PaRuX's agentic development protocol.

Runtime files are intentionally ignored by Git so planning/test loops do not pollute the repository history. At task start, create the needed local runtime files from `.ai/templates/` if they do not already exist:

- `TASK.json`
- `STATE.json`
- `PLAN.md`
- `TEST_RESULT.json`
- `REVIEW_RESULT.json`
- `artifacts/` for full logs, screenshots, coverage and other diagnostic evidence

Keep runtime files compact. They are coordination state, not a diary or a permanent source of truth.

Agents should pass summaries/deltas between roles and open full artifacts only on demand.
