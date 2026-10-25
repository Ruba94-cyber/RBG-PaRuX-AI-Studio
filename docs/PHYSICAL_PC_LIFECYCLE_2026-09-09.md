# Lifecycle Lab physical-PC acceptance — 2026-09-09

Status: partial acceptance; no release or REFERENCE_VERIFIED claim.

Executed through the published WinUI application using real mouse/keyboard input.
The machine is the user's AMD/iGPU workstation; no NVIDIA hardware was assumed.

## Observed passes

- Hugging Face search for `alpaca` returned 15 results.
- `Choose file` opened the repository file selector and displayed the source license.
- Selecting and downloading the real `tatsu-lab/alpaca` Parquet completed. The on-disk
  file was 24,246,638 bytes; Refresh all showed completed and enabled Curate.
- Curate populated the source path. Inspect read 52,002 rows, zero duplicate rows
  and 31,351 empty fields. The prompt picker opened with instruction/input/output/text.
- Deterministic 80/10/10 split created train/validation/test JSONL files. Independent
  filesystem line counts were 41,602 / 5,200 / 5,200. The UI switched to the train
  source, inspected its 41,602 rows and populated the column pickers.

## Defect corrected and verified live

`LifecycleLabView.ChooseDatasetFileAsync` previously listed every repository sibling,
sorted by SizeBytes, then selected index zero. `DatasetDownloadService.GetDetailsAsync`
uses zero for unknown sibling sizes. On the real repository the dialog therefore
preselected `.gitattributes`, included README.md and described the real Parquet as 0 B.

The production View now filters to its supported CSV/JSON/JSONL/Parquet formats,
orders known sizes first with a stable filename tie-breaker, and labels unavailable
sizes `size unknown`. The dialog's picker can fit within the dialog instead of forcing
a 600-pixel minimum. No server download validation or inference behavior changed.

Republished the Windows application, relaunched it, repeated the search and opened
the selector and dropdown: only the real Parquet was listed and preselected, with
`size unknown`. The empty initial mapping pickers remained disabled.

This is a View-only fix. Per the acceptance mandate, regression validation is live;
no source-text pseudo-test or separate UI harness was added.

## Tool distinction

The automation helper returned stale focus metadata and cache errors for indexed
input. Resetting the JS session and using fresh screenshots with real input allowed
the workflow to proceed. These errors were not classified as product defects.

## Still to execute

Eval creation/import/run and SFT export; Training preflight and method selection;
AutoLab/adapters; remaining Projects/Settings/VS Code/Android/context/soak/media rows
from the handoff. Training runtime was reported as not installed; no training success
is claimed. Download status was refreshed explicitly; background refresh and restart
recovery have not been accepted by this pass.
