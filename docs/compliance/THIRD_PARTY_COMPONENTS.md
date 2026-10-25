# Third-party components and licence analysis

> Generated from the structured output of nine read-only domain audits (see `docs/audits/parts/*.md` for the narrative evidence).
> Evidence levels: **static** = code read, **unit_tests** = named automated tests exist (not necessarily executed by the auditor), **e2e_real** = a recorded real run, **none** = no evidence.
> The audit was static: nothing was built, run or fetched by the auditors. Statements about Locally Uncensored (LU) describe observable behaviour in the auditors' own words; no LU code is reproduced (LU is AGPL-3.0, PaRuX is MIT).

## Summary

LU v3.0.6 is AGPL-3.0-only with consistent metadata, per-package notices for its voice and computer-control stack that are tested against its lock files, and an executable npm licence gate. Its own gaps: the installed folder ships MIT llama.cpp DLLs with no licence text; its notices omit ComfyUI (GPL-3.0), llama.cpp, piper-tts (installed unpinned), the OFL fonts, FFmpeg wheels and the front-end npm closure; it ships 491 presenter preview images that its own code comment attributes to HeyGen, with no licence record. PaRuX is MIT with a real provenance model, a CycloneDX SBOM, and a model-licence consent gate with unit tests. Its release-facing gaps: the MSI notice labels WindowsAppSDK MIT (it is Microsoft terms) and says llama.cpp and uv are not embedded (the staging script embeds them); the NuGet notice omits four shipped packages; the Full Offline bundle redistributes model weights with licence strings only; the Android build excludes META-INF Apache and LGPL licence texts. No LU identifiers appear on HEAD or main, so no LU code is in PaRuX. Line-referenced detail is in the part file, which is 154 lines.

**Strengths on the LU side**

- AGPL-3.0-only is stated consistently in LICENSE, package.json, Cargo.toml, tauri.conf.json and README.md, with an explicit contributor-licence statement (CONTRIBUTING.md:139-141).
- Corresponding-source discipline: release.yml:226-236 builds only the tagged commit and cites AGPL section 6.
- Per-package notices for the voice and computer-control stack, tested against the pinned versions (src/api/__tests__/voice-and-computer-notices.test.ts, six cases).
- Executable licence gate for the npm production closure (src/api/__tests__/prod-licenses.test.ts) that rejects incompatible licences and fails on new undeclared packages; CI runs it (ci.yml:92).
- Per-package Python licence table with explicit LGPL and GPL-linked flags and the rule that such libraries remain separate installed files (third-party/VOICE-AND-COMPUTER-NOTICES.md:66-180).
- Restricted assets handled deliberately: non-commercial and LibriVox-derived voices excluded (notices :33); CC-BY credit lines shown in the UI (VOICE.md:150-158).
- Reproducible pins: the llama.cpp commit is pinned and asserted as a full 40-character SHA (scripts/build-llama.sh:55-56, 217-221).
- Model catalogue flags non-commercial and custom licences on the Qwen-Image bundles and warns on custom or missing licence text (src/api/model-bundles.ts:408-569; src/api/discover.ts:1292-1420).

**Strengths on the PaRuX side**

- MIT LICENSE with a matching MIT text in the installer wizard (LICENSE:1-3; installer/PaRuX.Setup/License.rtf).
- The MSI payload carries LICENSE.txt, THIRD_PARTY_NOTICES.md and OPEN_SOURCE_PROVENANCE.md, and publish validation requires them (scripts/build-msi.ps1:185-190, 206-207).
- CycloneDX SBOM generated at publish time for the Windows payload and shipped beside the Server (installer/PaRuX.Setup/PaRuX.Setup.wixproj:37; docs/WINDOWS_MSI_RELEASE.md:95-101). LU publishes no SBOM.
- Four-category provenance model (PaRuX-authored, vendored upstream, external runtime, reference-only) with explicit rules (OPEN_SOURCE_PROVENANCE.md:95-105).
- Reference-only AGPL projects are not copied, and SearXNG is used only as a user-run loopback service (src/PaRuX.Server/LocalSearxngSearchProvider.cs; OPEN_SOURCE_PROVENANCE.md:29-35, 65).
- The upstream reuse audit records licence and reviewed commit per project and states that no third-party source was copied verbatim (docs/AGENT_UPSTREAM_REUSE_AUDIT.md:10-16, 89).
- Model and dataset licence consent gate: allow-list, Hugging Face licence metadata client, acknowledgement evidence validated with UTC timestamps (src/PaRuX.Server/ModelDownloadService.cs:704-717), with 8 and 7 unit-test cases.
- The runtime lock pins llama.cpp and uv by version, URL and SHA-256, and the hash is verified before copying (packaging/windows-baseline-runtime.lock.json; scripts/stage-msi-runtime.ps1:65, 92-124).
- The Full Offline builder refuses assets without sourceUrl and licence string and re-verifies hashes after staging (scripts/build-full-offline-bundle.ps1:73-74, 146-160).
- No font, binary or model file is committed in the tree (checked by file-name pattern).
- The vendored llama.cpp conversion subset ships its MIT text beside the server (src/PaRuX.Server/PaRuX.Server.csproj:74-80; llama-cpp-conversion/LICENSE-llama.cpp).
- Clean-room hygiene: no LU identifiers on HEAD or main (git grep, positive control verified); copyleft strings confined to notices, docs and tests.

## Findings

| Pri | Component / question | PaRuX status | Gap | Action |
|---|---|---|---|---|
| P0 | R1: Microsoft runtimes redistributed in the Windows MSI | partial | A Microsoft-terms component is labelled MIT, Windows ML and OnnxRuntimeGenAI.WinML have no notice row, and section 3 distribution conditions are not recorded. | improve |
| P1 | R2: llama.cpp and uv archives staged into the MSI | partial | The notices contradict the build. MIT and Apache texts are not in the payload unless the archives carry them (not verified: no download). | improve |
| P1 | R21: Full Offline bundle redistributing model weights and runtimes | partial | Model weights are redistributed with licence strings but no licence or attribution texts. The notices do not describe the redistribution, and the licence of the curated model is not in the repository. | implement |
| P1 | R20: Android APK licence texts | partial | Apache-2.0 section 4 asks that recipients receive a copy of the licence, and the exclusion can remove those copies. The artifact responsible for the exclusion was not identified because the Gradle graph was not run. | improve |
| P1 | R3: Notice coverage of the shipped NuGet graph | partial | The notice table and the SBOM can drift; no test compares them; transitive packages are covered only by family wording. | implement |
| P1 | R4: ExLlamaV3 managed runtime pack | partial | A third-party runtime with an owner, wheel index and CUDA build has no licence, version record or notice; its upstream licence was not read. | implement |
| P1 | R5: Upstream identity and licence in the lifecycle UI and component inventory | partial | The notice's own rule is not implemented. Users are not told that ComfyUI is GPL-3.0 software that PaRuX does not author. | implement |
| P1 | R6: Model and dataset licence review, consent and credit | implemented_working | No attribution or credit field exists in model or dataset contracts (the only Attribution property is SkillContracts.cs:19). The consent flow has no end-to-end run. | improve |
| P1 | R7: AGPL-3.0 reuse boundary and provenance register for LU | partial | LU is not registered as a reference-only AGPL project with the spec-only rule, and the Odysseus identifier differs between files. | improve |
| P2 | R9: Licence capture for managed Python environments and FFmpeg | partial | No record per environment; the licence of the FFmpeg build behind imageio-ffmpeg is unrecorded; no GPL or LGPL check runs before install. | implement |
| P2 | R13: Transitive dependency licence gate (NuGet, Gradle, Cargo, PyPI) | partial | PaRuX has an SBOM but no executable licence test; the notice and the SBOM can drift; the Gradle graph has no licence check. | implement |
| P2 | R19: Speech and voice model licences and credits | partial | Speech models have no notice row or credit path, and the Parakeet licence is unrecorded. | implement |
| P2 | R10: Contributor inbound licence | missing | Contributions are licensed only implicitly. | implement |
| P2 | R11: Provenance register for reference-only and vendored sources | partial | No LU entry; the vendored commit is missing; the Odysseus identifier differs between files. | improve |
| P2 | R8: SearXNG search backend | implemented_working | None for licence; optionally label AGPL-3.0-or-later where the endpoint is configured. | keep |
| P2 | R12: Non-commercial or community image-model licences | missing | Missing by design. If a non-commercial model is ever offered, no labelled consent path exists yet. | keep |
| P3 | R14: Vendor installers and external engines fetched at run time | implemented_working | None. PaRuX keeps vendor installers out of its install path. | keep |
| P3 | R15: Brand, logo and trademark boundary | partial | No written rule says that third-party names or logos are not reused; the status of identity assets is implicit. | improve |
| P3 | R16: Vendored llama.cpp conversion subset | implemented_working | The upstream commit of the vendored subset is not recorded (OPEN_SOURCE_PROVENANCE.md:13-27). | keep |
| P3 | R17: Licence presentation in the product and installer | implemented_working | No in-app viewer for the shipped notices; the presence check was not run. | improve |
| P3 | R18: Build and signing tooling licences | partial | The policy is documented; its enforcement was not verified. | improve |

## Reuse boundary for Locally Uncensored

Locally Uncensored is GNU AGPL-3.0-only. PaRuX is MIT. Consequences applied in this programme:

- No Locally Uncensored source, prompt, table or algorithm is copied into PaRuX.
- Behaviour was studied by reading and documented as specifications in the auditors' own words; implementers worked from those specifications and never opened the Locally Uncensored tree.
- Locally Uncensored is not bundled, linked, or run by PaRuX. Any future benchmark that runs a Locally Uncensored binary treats it as a separate unmodified program and records the version and licence.
- Public external formats and APIs (GGUF, safetensors, Hugging Face Hub API, ComfyUI API, llama.cpp server flags) are facts, not Locally Uncensored's expression.

See `OPEN_SOURCE_PROVENANCE.md` and `THIRD_PARTY_NOTICES.md` for the shipped-component records; the gaps listed above are tracked work items.
