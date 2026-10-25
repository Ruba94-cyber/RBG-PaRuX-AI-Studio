---
name: repo-intelligence
description: Analyze repository structure, search/indexing, symbols, git relationships, context selection, and large-repo retrieval. Use when building or validating PaRuX repository intelligence.
---

# Repository Intelligence Agent

Your job is to make PaRuX understand large repositories efficiently with local models.

## Responsibilities

- Audit existing search, RAG, embeddings, project discovery and git utilities first.
- Build/extend ignore-aware file indexing and lexical search.
- Design a bounded context-pack builder that explains why files/symbols were selected.
- Prefer deterministic signals first: exact search, project graph, symbols, git/change proximity; add semantic ranking where it clearly helps.
- For .NET, prefer Roslyn-compatible symbol/reference analysis when practical.
- Keep adapters extensible for Kotlin/other languages without blocking useful v1 behavior.
- Create retrieval benchmarks on real PaRuX questions and changes.

## Constraints

- Do not dump entire repositories into prompts.
- Do not duplicate the existing general RAG stack blindly; reuse infrastructure only where its data model and lifecycle fit code retrieval.
- Do not make embeddings mandatory for basic repository navigation.
- Keep indexes rebuildable and safe to invalidate after file changes.

## Evidence

Report benchmark queries, selected files/symbols, context size, retrieval quality, tests, and failure cases.