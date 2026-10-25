# Downloads domain: clean-room competitive audit

Compared: Locally Uncensored (LU) v3.0.6 (GNU AGPL-3.0, clone kept outside this repository, tag v3.0.6, commit bce78481) against PaRuX (MIT), branch feature/parux-competitive-integration-20261019, worktree HEAD 117fcf7, clean at read time.

Clean-room statement: the LU column is a behavioural specification written in our own words. No LU code, strings, prompts or algorithms are reproduced. Identifiers, paths, endpoints and flags are cited. Implementers receive only this file and the cleanRoomSpec fields.

Status vocabulary: implemented_working | implemented_unwired | partial | prototype | docs_only | missing | obsolete | duplicated | replace. Recommendation: keep | improve | implement | replace | drop. Priority: P0 (blocks the easy managed-studio goal) to P3.

## 1. Scope

In scope: transfer engine (concurrency, Range resume, partial files, digest checks, retry, pause and cancel, progress, speed, ETA), disk-space guards, existing-file handling, publish, removal, shared cache, update tracking, licence gating, gated repositories and tokens, provenance and revision pinning, backup of download metadata.

Out of scope: installers (LU install/download.rs is cited only for its digest and size rules), chat attachments, dataset downloads (cross-reference only), Ollama and ComfyUI pulls.

## 2. Method and evidence

LU, read only:
- src-tauri/src/commands/download.rs: download_model, do_download, claim_download, pause_download, cancel_download, clear_download_entry, space checks, orphan scan, resume_download, and its inline test modules.
- src-tauri/src/commands/install/download.rs: digest and size rules (test module at lines 320-430).
- src-tauri/src/commands/mlx.rs: Hugging Face token storage (lines 52-96).
- src/api/model-download.ts, src/stores/downloadStore.ts, src/api/discover.ts, src/types/downloads.ts, src/lib/download-limit.ts, and the test file names in section 10.
- Not done: no LU script, binary, npm, cargo or network use. The installed application and its data folder were not opened.

PaRuX, read only:
- src/PaRuX.Server/ModelDownloadService.cs (1374 lines; all decisive ranges), ModelDownloadProvenanceManifestStore.cs, ModelDestinationSpaceProvider.cs, AiStorageBudget.cs, HuggingFaceManagedModelUpdateSource.cs, ManagedModelRevisionStore.cs, Program.cs, BackupContracts.cs.
- src/PaRuX.Contracts/ModelManagerContracts.cs, ModelLicenseReviewRules.cs.
- src/PaRuX.App/Services/ModelDownloadClient.cs, ModelDownloadResumeCatalog.cs, ModelDownloadStatusPolicy.cs.
- tests/PaRuX.Server.Tests/ModelDownload*.cs and ModelDestinationSpaceProviderTests.cs: names and transport inspected, not executed.
- Local branch main: read only through git show, as historical evidence. Anything present only there is marked onLocalMainOnly and is not counted as current capability.

Evidence levels: static (code read), unit_tests (named test exists, in-process transport, not run in this audit), e2e_real (none found for downloads). This audit performed no real Hugging Face or LU transfer.

## 3. Mechanism comparison

| Aspect | LU v3.0.6 | PaRuX worktree |
|---|---|---|
| Concurrency | Each started file gets its own task; no semaphore or cap found (download.rs:804-944) | 3 global slots and 1 per destination file name (ModelDownloadService.cs:55-61, 1018-1040) |
| Transfer | One HTTP stream per file; installer path reads 64 KiB chunks | One stream per job; 1 MiB buffer (ModelDownloadService.cs:1115) |
| Timeouts | Connect 30 s, read stall 120 s, no total deadline (download.rs:1373-1381, 1491) | One HttpClient with 30 min total timeout (ModelDownloadService.cs:70; Program.cs:102-104) |
| Resume | Partial "<name>.download"; Range bytes=N- sent; prefix counts only on 206 (download.rs:1002-1007, 1507-1510) | Partial "<name>.partial" plus identity sidecar; Range sent; continue only on 206 (ModelDownloadService.cs:833-927, 1062-1081) |
| Digest | SHA-256 streamed; resumed prefix re-hashed; expected from catalog or HF tree lfs.oid (download.rs:1580-1592, 1707-1719; discover.ts:1607-1621) | SHA-256 streamed; resumed prefix re-hashed; expected from HEAD X-Linked-ETag (ModelDownloadService.cs:1107-1110, 1146-1161, 1273-1280) |
| Publish | One rename over the target (download.rs:1721) | Delete target, then move partial (ModelDownloadService.cs:1171-1174) |
| Disk guard | Per file (+1 GiB) and per bundle (+2 GiB, in-flight bytes reserved) (download.rs:1179, 1192-1204, 1210, 1959-2017) | None at admission; 700 GiB managed quota only (AiStorageBudget.cs:13, 53; ModelDownloadService.cs:547-556) |
| Retry | Manual; error class decides whether Retry is offered (discover.ts:156-175) | Manual; no error class; status reads back off (ModelDownloadStatusPolicy.cs:5-16) |
| Pause | Yes: pausing, then paused with bytes kept (download.rs:1601-1625, 1738-1757) | No paused state; cancel keeps bytes; resume is explicit (ModelDownloadService.cs:978-987, 576-594) |
| Speed and ETA | Speed every 500 ms (download.rs:1646-1662); ETA not in the progress type (types/downloads.ts) | Bytes and total only (ModelManagerContracts.cs:193-216) |
| Bandwidth limit | Global MB/s limit shared by all model transfers (download.rs:1009-1056; lib/download-limit.ts) | None |
| Gated repos and tokens | Optional HF token held in memory, sent only to hub hosts (mlx.rs:52-96; download.rs:1357-1365, 1498-1504) | None |
| Licence gate | None in app; gated-error text points to hub licence acceptance (download.rs:1307-1321) | Affirmative review for non-permissive or unknown licences (ModelDownloadService.cs:683-745; ModelLicenseReviewRules.cs:17-29) |
| Revision pinning | resolve/main URLs (discover.ts:917, 1719) | Immutable commit required; pinned URLs (ModelDownloadService.cs:601-682, 949-954) |
| Provenance | Not persisted beyond download metadata | Per-file manifest; atomic write; corrupt manifest blocks writes (ModelDownloadProvenanceManifestStore.cs:40-191) |

## 4. Feature table

Priority-ranked. Columns: LU behaviour | PaRuX status and evidence | gap | recommendation. Full structured rows are in the audit response.

R01 (P0, improve) Transfer deadline and stall policy. LU: connect and per-read bounds, no total deadline (download.rs:1373-1381, 1491). PaRuX: partial. A 30 min total timeout covers the body read (ModelDownloadService.cs:70); an abort lands in the generic cancel or failure catch (1227-1249) and keeps the partial. Gap: any single attempt over 30 min is aborted; no stall timer; no test. Rec: remove the total deadline, add connect and stall timers.

R02 (P1, implement) Free-space guard. LU: implemented (download.rs:1544-1551, 1959-2017; reserves at 1179 and 1210; tests named a_full_drive_is_named_before_the_first_byte and a_bundle_keeps_two_gigabytes_free...). PaRuX: partial. Managed quota only; free bytes are reported in the budget status but not enforced (AiStorageBudget.cs:33-54; ModelDownloadService.cs:552; ModelDestinationSpaceProviderTests.cs:50). Gap: a transfer can fill the system drive; the only signal is a write error. Rec: reserve and check at admission, before each file of a set, and against in-flight bytes.

R03 (P1, implement) Gated or private repositories and tokens. LU: implemented: optional token, host-gated Bearer, gated message names the token field (mlx.rs:52-96; download.rs:1300-1321, 1498-1504; tests at 410-433). PaRuX: missing. No token path in download code; a candidate HEAD 401 fails admission (ModelDownloadService.cs:515-518, 537-543). Gap: licence-accept repositories cannot be fetched. Rec: token in a protected store, sent only to the hub, never in URLs or logs.

R04 (P1, implement) Gaming hard-off gate for downloads. LU: no gaming hook observed in the download path (LU-wide gaming mode not audited). PaRuX: missing on the worktree. No download hook in Orchestration/Gaming; AutomationEngine holds a ModelDownloadService reference (AutomationEngine.cs:35). Main line only: IGamingResumable, PausedForGaming, GamingDownloadPauseTests (onLocalMainOnly). Gap: the product rule in PARUX_EXECUTION_PLAN.md (Gaming boundary paragraph, about line 478) requires no silent download that harms play; not enforced here. Rec: a Core gate; port the concept, not the code.

R05 (P2, improve) Completeness of non-candidate downloads. LU: partial-to-full. Declared length is compared at the end; a short body keeps the partial (download.rs:1690-1695); unknown length never proves completion (download.rs:1058-1083). PaRuX: partial. Exact size only for candidates (ModelDownloadService.cs:1134-1135); manual downloads rely on transport end-of-body and an optional digest; a failed HEAD probe is swallowed (535-536) and verification becomes unavailable (1163-1166). Gap: manual downloads can complete with no digest. Rec: persist expected size and digest before bytes arrive; refuse a short completion for all jobs.

R06 (P2, improve) Quota admission when size is unknown. LU: partial. Unknown size keeps the space guard alive via the catalog estimate (download.rs:1078-1083; test a_missing_content_length_does_not_silently_disable_the_space_guard). PaRuX: partial. A manual start whose HEAD gives no length uses the caller's size or zero for the 700 GiB check (ModelDownloadService.cs:511-522, 547-556). The total is not rechecked during transfer; only the per-transfer cap applies (1125-1127; ModelDownloadServiceTests.cs:404). Gap: the managed budget can be exceeded. Rec: fail closed without a size, or reserve the cap and recheck during transfer.

R07 (P2, improve) Replacement, collisions and publish. LU: implemented. An in-flight same-name start is refused with a visible message (download.rs:968-1000; test two_different_models_sharing_a_file_name_collide_visibly); publish is one replacing rename (download.rs:1721). PaRuX: partial. Same-name jobs are serialised per destination (ModelDownloadService.cs:929-933), but a completed file of another source is overwritten after verification (1171-1174) with a window in which no final file exists (1172-1173). A start from another source deletes the old partial (882-889; ModelDownloadResumeTests.cs:130). Gap: silent replacement, non-atomic publish, resumable bytes discarded. Rec: explicit replacement, a single replace-move, consent before discarding another source's partial.

R08 (P2, improve) Existing final file: short-circuit and repair. LU: partial-to-full. An existing file is accepted only on an exact server size; otherwise it is fetched again. The offline case is accepted unverified and logged (download.rs:837-862, 1108-1119; test an_existing_file_counts_only_at_the_exact_byte). PaRuX: partial. No short-circuit at admission; a start replaces the final file after verification (ModelDownloadService.cs:1171-1174). Later byte changes are detected by size and mtime and make provenance unknown (ModelDownloadProvenanceManifestStore.cs:278-291; ModelDownloadProvenanceIntegrationTests.cs:211). Gap: complete files are re-downloaded; no cheap verified answer. Rec: compare with the stored provenance digest first; never claim verified without a check.

R09 (P2, improve) Redirect and host checks during transfer. LU: implemented: public-address check before the request and on each redirect (download.rs:1376, 1480). PaRuX: partial. Admission accepts only https huggingface.co (ModelDownloadService.cs:601-682); the transfer client uses the default handler, which follows redirects to any host (70); the probe disables redirects (71-74). Gap: defence-in-depth gap; no credential is sent today. Rec: allow-list each hop.

R10 (P2, improve) Failure classes and actionable messages. LU: implemented: gone (404/410), gated (401/403), rate limit (429), server (5xx) each get a message with a next step; Retry is gated by class (download.rs:1145-1174, 1300-1337; discover.ts:156-175). PaRuX: partial. Raw HttpRequestException text (ModelDownloadService.cs:1090, 1241-1248); no class. Gap: a user cannot tell permanent from transient. Rec: classify refusals and map them to messages and Retry rules.

R11 (P2, implement) Automatic retry with backoff. LU: missing (manual Retry only). PaRuX: missing in Core; the App backs off only on status reads (ModelDownloadStatusPolicy.cs:5-16). Gap: transient network errors need manual action in both. Rec: bounded retries with jitter, always resuming from the partial.

R12 (P2, implement) Pause as an explicit state. LU: implemented (download.rs:1601-1625, 1738-1757). PaRuX: partial. Cancel keeps .partial (ModelDownloadService.cs:978-987; ModelDownloadServiceTests.cs:611); resume only for cancelled or failed jobs (576-594); no paused status. Gap: a user cannot park a transfer without a terminal state. Rec: a paused state that keeps bytes.

R13 (P2, implement) Speed and ETA. LU: partial. Speed per download (download.rs:1646-1662; types/downloads.ts); ETA not in the type. PaRuX: partial. Bytes and total only (ModelManagerContracts.cs:193-216). Gap: no speed or time remaining for users. Rec: sliding-window speed; ETA derived from it.

R14 (P2, implement) Shared bandwidth limit. LU: implemented (download.rs:1009-1056; lib/download-limit.ts; tests the-download-speed-can-be-limited.test.tsx and Rust tests at 2741-2800). PaRuX: missing. Gap: transfers cannot be capped next to other work. Rec: one global limiter in the single read loop.

R15 (P3, improve) Orphan partials after restart. LU: implemented: startup scan of stray .download files for known stems, with resume and discard (download.rs:2090-2183; downloadStore.ts:523-588). PaRuX: partial. A journal of PaRuX-started sources, listed only while .partial exists (ModelDownloadResumeCatalog.cs:11-20; ModelDownloadClient.cs:243-247; ModelDownloadResumeCatalogTests.cs:19, 140). Gap: partials from unjournaled sources are invisible. Rec: also scan managed folders for sidecars.

R16 (P3, keep) Integrity: streamed SHA-256 and resume re-hash. LU: implemented (download.rs:1580-1592, 1707-1719; install/download.rs tests at 320-360). PaRuX: implemented_working (unit): ModelDownloadService.cs:1107-1110, 1146-1161, 1252-1262; ModelDownloadServiceTests.cs:171, 507, 580; ModelDownloadProvenanceIntegrationTests.cs:273. Gap: none material; digest source differs (HEAD header versus catalog). Rec: keep.

R17 (P3, keep) Revision pinning, provenance manifest, update detection and backup. LU: partial. Digests come from resolve/main URLs; no persisted origin record (discover.ts:917, 1607-1621, 1719). PaRuX: implemented_working (unit): ModelDownloadService.cs:601-682, 1194-1218; ModelDownloadProvenanceManifestStore.cs:40-191 (atomic write; corrupt manifest blocks writes), 97-136 and 251-276 (backup import binds only on SHA-256 match); HuggingFaceManagedModelUpdateSource.cs:1-22 (update decided by content hash); BackupContracts.cs:157-158 (model bytes excluded by design). Gap: none material. Rec: keep.

R18 (P3, keep) Licence-consent gating. LU: missing in app; relies on hub acceptance (download.rs:1307-1321). PaRuX: implemented_working (unit): ModelDownloadService.cs:683-745; ModelLicenseReviewRules.cs:17-29; ModelDownloadSecurityTests.cs:59, 82; ModelLicenseConsentPolicyTests.cs. Rec: keep.

R19 (P3, keep) Resume identity and partial reuse. LU: partial. Reuse by file name, accepted on 206 only (download.rs:1002-1007, 1507-1510). PaRuX: implemented_working (unit): identity sidecar (ModelDownloadService.cs:833-927), legacy handling (899-908); ModelDownloadResumeTests.cs:49, 130, 207. Rec: keep.

R20 (P3, keep) Cancel, clear, remove and clean delete. LU: implemented: cancel deletes the partial, clear keeps it, deleting a model also removes its partial twin (download.rs:1772-1816, 1917-1939, 600-675). PaRuX: implemented_working (unit): cancel keeps the partial (ModelDownloadServiceTests.cs:611); remove only for terminal jobs of the same source, with a bounded wait (ModelDownloadService.cs:996-1016; tests 656, 696, 731); delete clears alias, repo id and provenance (ModelDownloadService.cs:318-342; tests 1047, 1134). Gap: test 656 is named for a paused download, but no paused state exists. Rec: keep.

R21 (P3, keep) Concurrency bounds. LU: missing (no cap found; bundles start several files at once). PaRuX: implemented_working (unit): ModelDownloadService.cs:55-61, 1018-1040; ModelDownloadServiceTests.cs:926, 948. Gap: cap is not user-configurable. Rec: keep.

R22 (P3, implement) Shared cache and deduplication. LU: missing (per-engine folders). PaRuX: missing (flat managed folder with alias and repo-id sidecars, ModelDownloadService.cs:196-258). Gap: duplicate multi-GB files across engines. Rec: a content-addressed store later; design needed first.

## 5. Top 10 proposals (priority order)

1. R01 (P0): replace the 30 min total timeout with connect and stall timers. A stall keeps the partial and reports "stalled", not "cancelled". Verify with a fake transport that stays slow for more than 30 minutes of simulated time.
2. R02 (P1): free-space check at admission and before each file of a set, with a fixed reserve and in-flight reservations. Report needed and free bytes.
3. R03 (P1): host-gated Hugging Face token in a protected store; a gated 401/403 explains the next step (add token, accept licence).
4. R04 (P1): Core pause gate tied to the Gaming hard-off signal. Paused transfers keep the partial; no silent resume.
5. R05 and R06 (P2): persist expected size and digest before bytes arrive; fail closed for manual downloads with unknown size; recheck the budget during transfer.
6. R07 (P2): explicit replacement policy; single replace-move; no deletion of another source's partial without consent.
7. R08 (P2): short-circuit existing files whose stored digest matches; never report verified without a check.
8. R09 (P2): redirect allow-list for the transfer client.
9. R10 and R11 (P2): failure classes with actionable messages; automatic bounded retry with backoff that resumes from the partial.
10. R12, R13 and R14 (P2): paused state, speed and ETA, shared bandwidth limiter.

## 6. Strengths

LU: stall-based timeouts rather than a total deadline; free-space checks with reserves and in-flight accounting; exact-size judgement of existing files; host-gated tokens with per-hop redirect checks; separate pause, cancel and clear semantics; a shared bandwidth limit; startup orphan scan; gated-repository messages that name the setting.

PaRuX: immutable revision pinning with pinned URLs; per-file provenance manifest with atomic writes and fail-closed corruption handling; partial reuse bound to source identity; licence review gate with an affirmative, timestamped acknowledgement; bounded concurrency; cancel keeps bytes and remove is explicit; backup import rebinds provenance only on SHA-256 match; update detection by content hash rather than revision alone.

## 7. Risks

- R-a: the stall timer must ship with the timeout change, or stalled connections can hang indefinitely.
- R-b: a Gaming gate needs a reliable signal and must not deadlock the queue; partials must survive a pause.
- R-c: token handling. Tokens must never appear in URLs, logs, provenance or backups. LU keeps its token in process memory after the frontend pushes it (mlx.rs:52-96); PaRuX should use its own protected store.
- R-d: stricter size and digest rules can block legitimate downloads when a probe fails; the error must say so.
- R-e: LU's reserve values are LU product choices; PaRuX should set and justify its own.
- R-f: replacing delete-then-move must keep provenance conservative (forget the old record before replacing bytes).
- R-g: no real transfer evidence exists for PaRuX downloads. Under the CLAUDE.md Definition of Done, DONE needs a real run against a real repository.
- R-h: main-line features (gaming pause, restore snapshots) were written for an older line; port by reimplementation, not by copying.

## 8. Open questions

- Does the Model Hub skip already-installed files before calling Core? Not verified in the App code paths read.
- Do AutomationEngine-triggered downloads respect the Gaming hard-off? Not audited.
- Does the App show destination free bytes and warn the user? Not verified.
- Is ETA computed in LU's UI? Not verified.
- Where is LU's token persisted in the frontend? Not verified.
- Is a single-stream transfer fast enough for 40 GB files on typical CDNs? Needs measurement; segmented transfer is missing in both.
- Should candidate and manual download policies be unified?

## 9. Main-only evidence (not current capability)

- ModelDownloadService.cs on main: IGamingResumable, PausedForGaming, ResumeGamingPausedAsync; AiStorageBudget used with the destination provider at admission.
- tests/PaRuX.Server.Tests/GamingDownloadPauseTests.cs on main: loopback HTTP server with Range support and a throttled body. Used as a spec only.
- docs/BACKUP_SAFETY_SNAPSHOTS.md and tests/PaRuX.Server.Tests/BackupResilienceTests.cs on main: restore safety snapshots and rollback. Backup line, not model bytes.
- tools/bic13/record_provenance.py on main: not evaluated.

## 10. Test evidence index

PaRuX (not run; in-process transport): ModelDownloadServiceTests.cs (33 facts; FakeHandler and throttled stream); ModelDownloadResumeTests.cs (5); ModelDownloadResumeCatalogTests.cs (5); ModelDownloadSecurityTests.cs (14); ModelDownloadProvenanceIntegrationTests.cs (11); ModelDownloadProvenanceManifestStoreTests.cs (7); ModelDownloadStatusPolicyTests.cs (4); ModelDestinationSpaceProviderTests.cs (7).

LU (not run): commands/download.rs inline test modules (speed limit around lines 2723-2800; space checks 2937-2990; partial, existing-file and digest rules 2991-3083; claim and cancel rules 3204-3317); commands/install/download.rs lines 320-430; src/stores/__tests__/ downloadStore-retry, downloadStore-cancel-remembers, downloadStore-orphans, downloadStore-own-rows-survive-the-poll, downloadStore-pruefsumme-bleibt; src/api/__tests__/download-integrity.test.ts; src/lib/__tests__/the-download-speed-can-be-limited.test.tsx; src/components/layout/__tests__/download-failure-is-readable.test.ts.
