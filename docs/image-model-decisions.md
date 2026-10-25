# Image Model Decisions

Per the spec's mandate to evaluate Krea 2 and FLUX.2 Klein as candidates
rather than accumulate models by popularity. This is a **research-based**
decision record — VRAM, license, and architecture facts below come from each
project's own published specs (checked August 2026), not a local
side-by-side generation run. That distinction matters: no image quality
comparison was executed on this pass, and this document says so rather than
implying one happened.

## Current state (verified, in production)

**Z-Image Turbo, NVFP4-quantized** (`z_image_turbo_nvfp4.safetensors`,
Qwen 3-4B text encoder, `ae.safetensors` VAE) — Alibaba Tongyi-MAI, 6B
params, 8-step distilled. NVFP4 is NVIDIA's FP4 format built specifically
for Blackwell tensor cores, which this machine's RTX 5080 is. This was
already the configured default before this pass, and it's a genuinely
well-matched choice for this exact hardware generation — fast (Alibaba
reports ~2.3s for 1024x1024 on an RTX 4090; this GPU is newer), low VRAM,
permissively licensed. Kept as-is.

## Krea 2 — evaluated, not adopted

12.9B-parameter diffusion transformer (Raw + Turbo checkpoints), released
June 2026. Two real disqualifiers for this deployment:

- **VRAM**: full BF16 needs 24GB+; even the community FP8 quantization
  needs ~12GB. This machine's RTX 5080 has 16GB total, shared with
  whatever LLM is currently loaded (PaRuX's Resource Manager has to fit
  everything in that budget) — 12GB for one image model leaves too little
  headroom to be a sane default.
- **License**: the Krea 2 Community License caps free commercial use at
  under $1M annual revenue and 50 seats — a real restriction, not a
  formality, and inconsistent with keeping PaRuX's model-license posture
  clean and simple to reason about.

Not adopted. Worth revisiting only if a future default machine profile has
meaningfully more VRAM headroom.

## FLUX.2 Klein 4B — integrated and live-verified

Apache-2.0 (fully permissive, unlike the larger 9B Klein variants), native
ComfyUI support (confirmed: this ComfyUI install's `CLIPLoader` accepts a
`"flux2"` type and ships `EmptyFlux2LatentImage`/`Flux2Scheduler`/
`FluxKontextImageScale` nodes out of the box), and a genuinely good fit for
this hardware - the distilled variant used here needs ~8.4GB VRAM, small
enough to coexist with a loaded LLM.

**Integrated in this pass**, filling exactly the `IMAGE_EDIT` role the spec
named it for. The three model files (diffusion model, `qwen_3_4b` text
encoder, `flux2-vae`, ~16GB total) were downloaded, and the ComfyUI node
graph was taken directly from this install's own bundled blueprint
(`blueprints/Image Edit (Flux.2 Klein 4B).json`) rather than guessed,
flattened into the API execution format
(`config/flux2-klein-edit-api.json`). `Generate-Media.ps1` gained an
`-Type Edit` mode (uploads the source image to ComfyUI, submits the
workflow, polls for the result); `MediaGenerationService`/`MediaJobRequest`
gained a matching `"edit"` type with `sourceImageBase64`.

**Live-verified end-to-end**: a real generated photo of a red apple, edited
with the instruction "make the apple green instead of red" through the
actual `/api/v1/jobs` API, produced a correctly-edited image - the apple is
green, while composition, lighting, water droplets, and the wooden table
background are all preserved. Not a mock, not a partial pipeline - a real
request through the real API produced a real, correct result.

## Recommendation status

FLUX.2 Klein 4B is now PaRuX's `IMAGE_EDIT` model, verified working.
Z-Image Turbo NVFP4 remains `IMAGE_FAST`/`IMAGE_HIGH` for generation.
Krea 2 remains not adopted on this hardware profile (see above).
