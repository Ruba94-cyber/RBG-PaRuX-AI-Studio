# Git archaeology: main-only work and branch state

Domain: git-archaeology. Part file for the competitive audit (input to the merge step, not a standalone report).
Status: read-only. Nothing committed, fetched, checked out, merged, cherry-picked, built or tested. No Locally Uncensored source was read (this domain has no LU analysis).
Evidence date: 2026-10-19.

## 1. Scope and method

- Scope: the 117 commits on local `main` above `origin/main`; HEAD's 686 commits above `origin/main`; local `integration/main-v2`; remote feature branches; open PRs #6, #7, #8; local backup, merge-fix and legacy branches.
- Tools (read-only): `git log`, `git rev-list --count`, `git merge-base --is-ancestor`, `git cherry` (patch-id), `git merge-tree --write-tree` (writes tree objects only; no ref, index or worktree change), `git grep` and `git ls-tree` on refs, `git show --numstat`, `gh pr list` and `gh pr view`.
- Per-commit cost: each of the 117 main commits was simulated with `git merge-tree --write-tree --merge-base=<commit>^ HEAD <commit>`.
- Serena was not used: most compared code lives in refs other than HEAD, so narrow `git grep` on refs was the search.
- Tests: none run. "unit_tests" below means a test class exists on the named ref, not that it passed.
- Predecessor-brand check: count only. The token is decoded from the gate script's character-code array and is deliberately not written in this file.

## 2. Verified facts

- HEAD `117fcf7` on local `feature/parux-competitive-integration-20261019` (no origin ref under that name). The same commit is `origin/feature/parux-v2-local-first-integration-20261017`, the head of PR #8 (updated 2026-10-19 20:02Z), and the local branch `feature/parux-v2-local-first-integration-20261017` in worktree `parux-v2-integration`.
- `main` `92cc3c9`, local only. 117 commits above `origin/main` (`4f35240`), dated 2026-10-02 to 2026-10-06, linear history (0 merges), 117 patch-unique by `git cherry`.
- `integration/main-v2` `d323889`, local only, worktree `main-integration`, last commit 2026-10-18. It contains all of `main` by ancestry (`main..integration/main-v2` = 595; `integration/main-v2..main` = 0), with 4 merges above `origin/main`.
- Common root of HEAD, main and integration: `87f43e9`. Legacy unrelated-history lines (root `b9e894a`) share no merge base with HEAD or main.
- Merge bases: HEAD and main = `origin/main`; HEAD and integration = `90feeb9`. Commits after `90feeb9`: HEAD-only 100 (2026-10-18..19); integration-only 126 = 117 main + 5 local-only fixes + 4 merges.
- Date ranges: main 2026-10-02..06; V2 commits above `origin/main` on `origin/feature/parux-agent-platform-v2` 2026-10-10..12; HEAD hardening 2026-10-18..19.
- Stash list: empty. Worktree: only `docs/audits/` untracked.

Corrections to the earlier draft of this file: (a) main's commits are dated 2026-10-02..06, not 10-10..18; (b) PR #8's head is now HEAD `117fcf7`, 294 files (the draft said 97678fc, 40 commits ahead, 219 files); (c) HEAD does contain the explicit-API tests that cover PR #7's SSE fail-closed cases (section 7), which answers draft open question 4 by name, not by run.

## 3. Ref topology and stranded-work check

| Ref | Tip | Relation | Verdict |
|---|---|---|---|
| HEAD (`feature/parux-competitive-integration-20261019`) | 117fcf7 | 686 above origin/main; contains all of origin/feature/parux-agent-platform-v2; 0 main commits | Target. Also published as PR #8 head. |
| `main` | 92cc3c9 | 117 above origin/main; contained by ancestry in integration/main-v2 | Local only. |
| `integration/main-v2` | d323889 | Contains all of main; 126 commits not on HEAD | Local only. Holds all 5 local-only fixes. |
| `mergefix/backup` (b03e55e), `mergefix/verifier` (ebe5627) | tips | Ancestors of integration/main-v2 | Local only; redundant with integration. |
| `origin/feature/parux-agent-platform-v2` | 249f03e | Ancestor of HEAD; 0 commits missing | Fully contained. |
| `origin/feature/local-first-provider-contract-20261014` | 3e05155 | 38 above origin/main; 6 patch-equal to HEAD, 32 not; not an ancestor of HEAD or integration | Re-implemented on HEAD (row R21). |
| `backup/main-before-author-rewrite-20261006` | tip | 114 of 114 patches equal to main | Nothing stranded. |
| `backup/bic09-pre-rebase-20261005` | tip | 80 of 80 patches equal to main | Nothing stranded. |
| `backup/bic13-pre-rebase-20261005` | tip | 70 of 71 patches equal to main; one not examined | Check that one commit. |

Remote PRs (gh, read-only, 2026-10-19):
- PR #6: open draft, base main, head `feature/parux-agent-platform-v2` (249f03e, contained in HEAD). 142 files, +12,770/-187, last updated 2026-10-12. Body: "DO NOT MERGE YET".
- PR #7: open draft, base main, head `feature/local-first-provider-contract-20261014` (3e05155). 15 files, +1,186/-0, last updated 2026-10-17. Body: "Not ready to merge".
- PR #8: open draft, base `feature/parux-agent-platform-v2`, head = HEAD (117fcf7). 294 files, +23,719/-1,665, updated 2026-10-19 20:02Z. Body: "NOT BUILT OR TESTED"; do not merge before compile, security review, integration and Windows acceptance.
- PRs #5 and #3 closed; PRs #1, #2 and #4 merged (2026-09-05, 09-06, 09-14).

Result: no commit from the three remote feature branches is missing from HEAD, except 32 patch-unique commits of PR #7, whose transport HEAD re-implements (R21). Five local-only fixes are the only stranded work. None is on main or HEAD:
- 8e42bc0 fix(merge): flush coding phase timing before releasing the task browser (integration only).
- 5f325f5 test(verifier): adapt coding evidence fixtures (integration, mergefix/verifier).
- ebe5627 fix(verifier): require the accepted-command claim for a coding pass (integration, mergefix/verifier).
- 684cfd2 fix(server): refuse to snapshot a store whose database file was removed (integration, mergefix/backup; 19 files).
- b03e55e docs(plan): record post-merge backup snapshot fix under BIC-15 (integration, mergefix/backup).

## 4. Main-only commits by theme

Each of the 117 commits is assigned to exactly one theme (coverage checked: 117 unique ids). 37 touch only `PARUX_EXECUTION_PLAN.md`.

| Theme | n | Commits | HEAD status and anchor |
|---|---|---|---|
| T01 Plan-only records (includes BIC-14 closure claim 12a6c81) | 37 | 12a6c81 6448f18 a19e35b 2f6b282 29ec840 d34d517 eec96b7 87a2daa 9e80941 375bff5 88f268d 5af29a9 b16c521 00cdc89 5b80a2d 19f7422 77d7456 6acc7e6 b8166aa 2155185 bf043a3 2faa898 4a24c05 a8f39c8 d16670f 3cd25af f912bbe b6e4261 b4764c9 e379673 565efa8 c4203f1 f82e970 424033d 4d74286 92cc3c9 9f3e712 | Plan differs: HEAD +9/-1 vs origin/main; main +120/-5 |
| T02 Licence and brand | 2 | 2b87631 b70f2c8 | LICENSE:1 is MIT on HEAD; brand token in 4 HEAD files, 0 on main |
| T03 BIC-16 evaluation and qualification | 5 | 84b0e7a 0953924 157c4ac 1d3edc1 dab90f1 | Partial: CandidateEvaluationJobService.cs, CandidateEvidenceService.cs; qualification suite main-only |
| T04 BIC-16 launch fence | 3 | 8b48b15 f1298fe f0417d4 | Missing: no ld1 or launch-fence symbols (0 hits) |
| T05 BIC-16 manual selection and transaction | 6 | 7a239f9 b4d2767 294aa5c bb14fa9 9400a35 e8b41f8 | Missing: ModelHubView.cs:1553, 1559, 2044 say evaluation does not promote |
| T06 BIC-16 routing consult, promote and rollback UI | 3 | 5afa263 9ed79d5 cb62ae8 | Missing |
| T07 Explicit model-repository discovery | 2 | 5caf22b 94bce14 | Partial: ModelDiscoveryService.cs:5, :14 |
| T08 GPU budget and DirectML (BIC-04 S1-S2, BIC-08) | 7 | fb214be 17edfeb 08fd3c8 10adc49 715ec9f 20e9a10 5d62334 | Partial: DXGI yes (GpuInventoryService.cs:7), PDH no, DirectML no |
| T09 Gaming hard-off (BIC-04 S3-S4) | 3 | 964c940 5a61b84 92a337b | Partial: GamingGuardService.cs:60 |
| T10 Ollama compatibility (BIC-05) | 2 | 46c7cad f3ae215 | Partial: OllamaCompatibilityEndpoints.cs:12, :86, :146, :157 |
| T11 Coding agent flow (BIC-09 S1-S5) | 7 | e28e24e 0290162 0a5c149 16011b6 f333adb 70d3b74 9f775b9 | Partial: no phase timer or batch read in CodingAgentService.cs |
| T12 Coding security follow-ups (BIC-09) | 4 | 85bb304 1325fcd 98f7c1d 529f3ae | Partial: no AgentSensitivePaths.cs |
| T13 Context graph and ledger (BIC-10) | 3 | 655470f 792a1f3 724de70 | Partial: SemanticIndexV2Service.cs; VirtualContextLedgerStore.cs:46, :56 |
| T14 Tokenizer budgets (BIC-11) | 2 | 16a8b7c f2f8d4e | Partial: RuntimeTokenCounter.cs:12 |
| T15 Office workbook acceptance (BIC-12) | 1 | 79153f3 | Writer present; acceptance tooling absent |
| T16 Speech, media and training evidence (BIC-13) | 6 | 6cb3ddd 66de8b0 9289a49 1bef67b 6f3dcae 438dd3e | Partial: ContinuousTtsWorker.cs:12; MediaReviewPacket.cs and tools/bic13 absent |
| T17 Backup and restore safety (BIC-15) | 4 | 8eb7d15 1721485 36836b6 cac7f26 | Partial: BackupService.cs:18, :559; 0 snapshot, rollback or opt-in hits |
| T18 Attestation and listener guards | 7 | bc9e6d6 e4f0644 d627bb0 9c1294f 7cf47b7 c7e0cb2 2d7ea82 | Partial: ManagedListenerOwnership.cs:136, :142; no attestation type |
| T19 Release engineering (BIC-07) | 5 | e844df1 07fe075 64de2d6 9dedde8 f9a4b2b | Partial: scripts/build-msi.ps1:260-264 |
| T20 FlaUI harness, navigation, persistence (BIC-06) | 8 | 287613b 3deac4f ed10270 4aa77a7 ba9a612 dfe021b b5b3a36 0628a0e | Partial: 11 files under tests/PaRuX.UITests on HEAD (5 infrastructure); 23 on main |

Priority-area mapping (the user's stated areas):
- Model Hub: T03, T05, T06, T07 (and UI commits dab90f1, 9ed79d5). HEAD has none of the BIC-16 transaction or promote/rollback code.
- Downloads: 5a61b84 changes src/PaRuX.Server/ModelDownloadService.cs for Gaming-pause behaviour. HEAD's copy has 0 Gaming references; integration/main-v2's copy has 18.
- Runtimes: RuntimeManagerService.cs, LocalRuntimeService.cs, LocalAiProcessManager.cs (4 whole-branch hunks), src/PaRuX.WinMlWorker (T08).
- Multimodal: T16, plus media claims in plan records 2155185, 4a24c05, 6acc7e6, 77d7456 (claims only; not verified here).

## 5. Integration cost (measured, read-only)

5.1 HEAD vs main, whole branch (`git merge-tree --write-tree --name-only`, exit 1): 14 conflicted paths and 32 conflict hunks.
- Plan: PARUX_EXECUTION_PLAN.md (1). App: Services/AgentCodingWorkerService.cs (1), Services/CodingAgentCheckpointStore.cs (4), Services/CodingAgentService.cs (11), Views/ChatView.NativeCodingHandoff.cs (2). Server: GamingGuardService.cs (2), GoalModeV2Store.cs (1), LocalAiProcessManager.cs (4), LocalRuntimeService.cs (1), Orchestration/AgentWorkerService.cs (1), Program.cs (1), RuntimeBuildRaceService.cs (1), RuntimePerformanceTuner.cs (1). Tests: StrictLocalInferenceIntegrationTests.cs (1).
- 52 paths changed on both sides since origin/main. 73 main commits and 119 HEAD commits touch those 14 paths.

5.2 HEAD vs integration/main-v2 (exit 1): 10 conflicted paths: Services/CodingAgentCheckpointStore.cs, Services/CodingAgentService.cs, GamingGuardService.cs, LocalAiProcessManager.cs, LocalRuntimeService.cs, Orchestration/AgentWorkerService.cs, Program.cs, RuntimeBuildRaceService.cs, RuntimePerformanceTuner.cs, StrictLocalInferenceIntegrationTests.cs. Integration already resolved four of the main conflicts on 2026-10-18: PARUX_EXECUTION_PLAN.md, AgentCodingWorkerService.cs, ChatView.NativeCodingHandoff.cs, GoalModeV2Store.cs.

5.3 Per-commit simulation (each main commit standalone against HEAD): 32 of 117 apply cleanly and 85 conflict; 33 of the conflicting commits conflict only in PARUX_EXECUTION_PLAN.md. Clean is not compilable: 9ed79d5 applies cleanly but uses CandidateRecommendationTransitionResponse and CandidateRecommendationStatusResponse, which exist only in main at src/PaRuX.Contracts/CandidateRecommendationContracts.cs:34 and :8. 5afa263 introduces them and conflicts on HEAD.

5.4 Silent relicense risk: integration/main-v2 changed LICENSE, README.md, CONTRIBUTING.md, OPEN_SOURCE_PROVENANCE.md and THIRD_PARTY_NOTICES.md after the merge base 90feeb9, and HEAD did not. A merge of integration into HEAD therefore takes the FSL text with no conflict. The same holds for main through 2b87631, which applies cleanly.

5.5 Semantic duplicates (both lines fixed the same concern; a textual merge keeps one side silently). All HEAD SHAs below were verified as ancestors of HEAD.
- Gaming: HEAD ac1c75c, 705ffb1, c498fae vs main 964c940 (integration adds 5 Gaming and runtime conflict paths).
- Coding checkpoints and verification: HEAD 27aeba7, b1c71b4, 9983b31, b6786de, 36aabdb vs main e28e24e, 0290162, 16011b6.
- Explicit remote API: HEAD 6820a1d, cd96baf, 205f235, 91eb6e4 vs PR #7 commits 41f147d, 78c98eb, ca5ef5d.

5.6 Plan file: HEAD +9/-1 since origin/main; main +120/-5 (BIC gate classification from 4d74286). A textual merge would mix two status histories. Re-derive statuses from code.

## 6. Recommendation: bringing the main-only work onto this branch

Path A (recommended, after the licence decision):
1. Decide the licence (R01). Push nothing that changes LICENSE or the licence surfaces until then.
2. Cherry-pick `b70f2c8` onto HEAD first. It applies cleanly standalone; it removes the brand token from the 4 HEAD files and adds the tree-wide guard. Re-run the count check: zero hits.
3. Create a fresh integration worktree and branch from HEAD, then merge `integration/main-v2`. Expect 10 conflicts (section 5.2). Keep HEAD's 100 hardening commits and main's additions. If the decision is MIT, revert the licence hunks deliberately in that merge.
4. Dedupe the three semantic-duplicate concerns (section 5.5). For each, keep one implementation and keep both sides' tests.
5. Reconcile PARUX_EXECUTION_PLAN.md by hand, importing the gate classification from 4d74286. Do not keep both histories.
6. Before any push: build and run the focused suites on that branch (Gaming hard-off, coding verification, explicit API, backup restore, brand guard, pairing). None was run in this audit.

Path B (fallback if the merge is refused, or a theme must stay isolated): cherry-pick the 117 commits in topological order (`git log --reverse --topo-order origin/main..main`), skip the 37 plan-only commits, and batch by theme:
- T02 `b70f2c8`; then T17 `8eb7d15`, `1721485`, `36836b6`, `cac7f26`, plus the local `684cfd2` and `b03e55e`.
- T08 `fb214be`, `17edfeb`, `08fd3c8`, `10adc49`; then `715ec9f`, `20e9a10`, `5d62334`.
- T10 `46c7cad`; then `f3ae215` after the Gaming dedupe.
- T04, then T05, then T06 (BIC-16 chain in main's order), with each server commit before its UI commit.
- T13 and T14 (BIC-10 and BIC-11).
- T18: `bc9e6d6`, `e4f0644`, `d627bb0`, `9c1294f`, then the listener follow-ups.
- T11 and T12 last, with the local verifier fixes `5f325f5`, `ebe5627` and `8e42bc0`, because they carry the most conflicts.

Skip or defer:
- The 37 plan-only commits as code (reconcile by hand).
- `2b87631`, unless the decision is FSL. Then only the licence surfaces, reviewed as one change.
- Any duplicate implementation (Gaming, coding checkpoints, explicit API) without a dedupe decision.
- Legacy unrelated-history lines: tag them first, then decide. Do not merge them.
- `backup/*` branches: already contained in main.

Keep (do not lose). Examples of HEAD-only hardening, all confirmed ancestors of HEAD:
- `bd6dc51` desktop owner required for backup export and runtime controls.
- `136c599` desktop-only routes restricted to the desktop owner.
- `c45044c` fail closed on an unreadable paired-device registry.
- `0b6ed2f` durable API-key store.
- `48df7e6` web reads routed through the validated egress transport.
- `013b539` runtime identity verified before stopping.
- `97678fc` sandbox link resolution before any folder grant.

## 7. Remote PR and branch housekeeping (part c)

- PR #6: its content is already on HEAD (its head is an ancestor). Close it as superseded once PR #8 is reviewed. Never merge it into main: it would bring the 498-commit V2 line.
- PR #8: its head equals HEAD. Keep it as a draft until the branch builds and the suites pass, then correct its description ("ports 15 files" is only partly accurate; see R21).
- PR #7: 32 of its 38 commits are not patch-equivalent to HEAD. HEAD re-implements the transport: ValidatedRemoteConnector.cs:29-30 (no redirects, no cookies), ExplicitApiInferenceTransport.cs:31 (uses it), ExplicitApiConnectValidationTests.cs:68. SSE and malformed-shape cases are covered by name: ExplicitApiStreamingReader.cs:40-42 and :54 with ExplicitApiStreamingReaderTests.cs:207 (missing DONE rejected), ExplicitApiMalformedResponseTests.cs:16 and :32. Close PR #7 after the explicit-API suites pass on HEAD.
- Local branches: keep `mergefix/*`, `integration/main-v2` and `backup/*` until the stranded commits are ported; then tag and prune.

## 8. Feature table (LU behaviour n/a)

| # | Feature | HEAD status and anchor | Gap | Recommendation | Priority |
|---|---|---|---|---|---|
| R01 | Licence (MIT vs FSL-1.1-MIT) | partial: LICENSE:1 MIT on HEAD; FSL on main and integration | Silent relicense via clean merge or cherry-pick (2b87631) | keep (HEAD MIT) until decided | P0 |
| R02 | Predecessor-brand guard | partial: token in 4 HEAD files (count only), 0 on main; gate keyword lines HEAD 2, main 5 | Tree-wide guard missing on HEAD | implement (b70f2c8, clean) | P0 |
| R03 | Integration path and canonical plan | duplicated: HEAD +100 and integration +126 above base 90feeb9; plan HEAD +9/-1, main +120/-5 | Choose one line; dedupe; one plan | implement (fresh branch from HEAD; merge integration after R01 and R02) | P0 |
| R06 | BIC-16 manual selection and transaction | missing: ModelHubView.cs:1553, 1559, 2044 (evaluation does not promote) | Gated, durable, crash-safe recommendation transitions | implement (S4 then S5) | P0 |
| R17 | Backup and restore safety (BIC-15) | partial: BackupService.cs:18, :559; 0 snapshot, rollback or opt-in hits | Pre-restore snapshot, opt-in over broken store, rollback | implement (8eb7d15 clean; then 1721485, 36836b6, cac7f26, 684cfd2) | P0 |
| R04 | BIC-16 evaluation evidence and qualification | partial: CandidateEvaluationJobService.cs, CandidateEvidenceService.cs; 14 Candidate test files | Proof binding to snapshots; frozen qualification profile | improve | P1 |
| R05 | BIC-16 launch fence (ld1) | missing (0 symbols) | Evaluated launch must equal the served launch | implement | P1 |
| R07 | BIC-16 routing consult, promote and rollback UI | missing; 9ed79d5 needs types from CandidateRecommendationContracts.cs:8, :34 | Server before UI | implement (5afa263, cb62ae8, then 9ed79d5) | P1 |
| R08 | Explicit model-repository discovery | partial: ModelDiscoveryService.cs:5, :14; ExplicitDiscoveryTests.cs main-only | Explicit flow; RemotePairingEndpointTests.cs modify/delete | improve | P1 |
| R09 | GPU budget, fingerprint, DirectML pin (BIC-04 S1-S2, BIC-08) | partial: GpuInventoryService.cs:7, :23 (DXGI); 0 PDH and 0 DirectML hits in src and tests | PDH subtraction; fingerprint; DirectML pin and execution check | improve | P1 |
| R10 | Gaming hard-off (BIC-04 S3-S4) | partial: GamingGuardService.cs:60; GamingHardOffTests.cs present; ModelDownloadService.cs 0 Gaming refs | Download pause; hard-off watch; dedupe with ac1c75c, 705ffb1, c498fae | improve | P1 |
| R12 | Coding agent flow (BIC-09) | partial: no phase timer or batch read; CodingAgentService.cs has 11 whole-branch hunks | Phase timing, batched read-only actions, continuation | improve (8e42bc0 with it) | P1 |
| R13 | Coding security follow-ups and verifier fixes | partial: AgentRepositoryReadTools.cs, WindowsCodingSandbox.cs; no AgentSensitivePaths.cs | Sensitive reads, STOP cascade, verifier accepted-command claim | implement (5f325f5, ebe5627 local) | P1 |
| R18 | Connected-peer attestation and HTTP guards | partial: ManagedListenerOwnership.cs:136, :142; ValidatedRemoteConnector.cs:29-32 | Peer PID, start time and image checked before requests | implement | P1 |
| R19 | Release engineering (BIC-07) | partial: scripts/build-msi.ps1:260-264 (SHA-256 file) | Payload-hash binding, embedded commit, 1.9.6, Android -LocalOnly | improve | P1 |
| R22 | Remote PRs, stranded local commits, legacy lines | duplicated: PR #6 and PR #8 heads contained in or equal to HEAD; 5 local-only commits | Port 5 stranded fixes; close drafts; tag legacy roots (b9e894a) | implement | P1 |
| R11 | Ollama compatibility delta (BIC-05) | partial: OllamaCompatibilityEndpoints.cs:12, :86, :146, :157 | Malformed-body and stream error-frame cases | improve | P2 |
| R14 | Context graph and token budgets (BIC-10, BIC-11) | partial: SemanticIndexV2Service.cs; VirtualContextLedgerStore.cs:46, :56; RuntimeTokenCounter.cs:12 | Incremental-parity and tokenizer-integrity tests; topology journal not compared line by line | improve | P2 |
| R16 | Speech, media and training evidence (BIC-13) | partial: ContinuousTtsWorker.cs:12; no MediaReviewPacket.cs, no tools/bic13 | Review packets; cold and warm measurement harness | improve | P2 |
| R20 | FlaUI harness, navigation, persistence (BIC-06) | partial: 11 files under tests/PaRuX.UITests on HEAD (5 infrastructure) vs 23 on main | Process ownership, restart and reachability tests | improve | P2 |
| R21 | Explicit remote API boundary (PR #7 and PR #8) | implemented_unwired: ExplicitApiTaskRunner.cs and ExplicitApiTaskGrants.cs referenced only by tests; ValidatedRemoteConnector.cs:29-32 | Verify tip cases on HEAD; close PR #7 | keep | P2 |
| R15 | Office workbook acceptance (BIC-12) | partial: StructuredWorkbookWriter.cs; no tools/PaRuX.WorkbookOfficeAcceptance | Real recalculation harness | implement (additive) | P3 |

## 9. Top 10 prioritised proposals

1. Decide the licence (R01) before any merge or push that touches licence surfaces.
2. Cherry-pick b70f2c8 (R02): the brand token is in 4 HEAD files; main has none.
3. Create the integration branch from HEAD and merge integration/main-v2 after step 1 (R03; 10 conflicts). Dedupe Gaming, coding checkpoints and explicit API (section 5.5).
4. Port the BIC-16 chain in order with its tests: evaluation and qualification (R04), launch fence (R05), manual selection and transaction (R06), routing and promote/rollback (R07).
5. Port backup restore safety with the local snapshot-refusal fix (R17: 684cfd2, b03e55e).
6. Port connected-peer attestation (R18), keeping the rule that an unverified runtime is never stopped.
7. Port the PDH and DXGI budget and the DirectML pin with their tests (R09).
8. Verify PR #7's cases on HEAD, then close PR #7 and PR #6; keep PR #8 draft until it builds (R21, R22).
9. Replace the 37 plan-only commits with one reconciled plan that imports the 4d74286 gate classification (R03).
10. Decide RemotePairingEndpointTests.cs (R08), then port the coding security follow-ups and verifier fixes (R13: 5f325f5, ebe5627).

## 10. Risks

- Two unpushed integration lines with different bases. Picking the wrong base loses one side's commits. HEAD has 100 commits integration lacks; integration has 126 that HEAD lacks.
- Silent relicense through a clean merge or cherry-pick (section 5.4).
- Semantic duplicates regress silently when a textual conflict is resolved toward one side (section 5.5).
- Unpushed local work: main (117), HEAD (686 above origin/main), integration (126 beyond HEAD's base), mergefix and backup branches. Losing the disk or a worktree would lose them. Create backup tags before any rewrite.
- Plan drift: the two plans disagree. Status claims (for example the real GPU cohort in eec96b7 and media evidence in plan records) are unverified here.
- Windows-only APIs (DXGI, PDH, native owner tables, WinML and DirectML) need real Windows runs. A clean textual merge proves nothing about them.
- Public-safety: the brand token (4 HEAD files) and the licence must be settled before any push. The memory rule requires a gitleaks scan before a push to the public repository.
- Concurrency: worktree `parux-v2-integration` sits on HEAD's tip and worktree `main-integration` holds integration/main-v2. Merging inside those worktrees risks collisions with another writer.

## 11. Open questions

1. Which licence applies to the public repository: MIT (HEAD) or FSL-1.1-MIT (main and integration)?
2. Which line is canonical: HEAD (hardening) or integration/main-v2 (main plus V2)? This audit recommends a fresh branch from HEAD that merges integration; the choice belongs to the user.
3. Should the legacy unrelated-history lines (root b9e894a) be tagged and archived? None of their content is in HEAD or main.
4. Downloads during Gaming Mode: main pauses them, HEAD does not. Which behaviour is intended?
5. RemotePairingEndpointTests.cs: main modifies it (3 tests); HEAD deleted it. Which pairing cases must survive? HEAD has ApiSecurityTests.cs, PairedDeviceRegistryDurabilityTests.cs and RemoteAccessServiceTests.cs.
6. Release decisions carried by main: candidate 1.9.6 and the Android -LocalOnly flag (e844df1).
7. Which real-run claims in the plan are accepted as evidence? None was re-run here.

## 12. Reproduction (read-only)

- `git log --format='%h %ad %s' --date=short origin/main..main` (117 commits); `git cherry origin/main main` (117 patch-unique).
- `git merge-tree --write-tree --name-only --no-messages HEAD main` (exit 1, 14 paths).
- `git merge-tree --write-tree --name-only --no-messages HEAD integration/main-v2` (exit 1, 10 paths).
- `git merge-tree --write-tree --merge-base=<commit>^ HEAD <commit>` for each of the 117 commits (32 clean, 85 conflicting).
- `git rev-list --left-right --count HEAD...integration/main-v2` (100 and 126); `git merge-base HEAD integration/main-v2` (90feeb9).
- `git show <ref>:LICENSE` for 90feeb9, HEAD, integration/main-v2 and main.
- Count-only brand check: `git grep -l -i -F` on HEAD, main and origin/main (4, 0 and 4 files; token not recorded).
- `gh pr list --state all --limit 30`; `gh pr view <n> --json changedFiles,additions,deletions,headRefOid` for PRs #6, #7 and #8 (read-only).
