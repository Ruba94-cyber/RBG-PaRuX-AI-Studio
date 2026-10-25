# STT/TTS Model Decisions

**Update: implemented and live-verified since this was first written.** The
model comparison below (published benchmark/spec data, not a local
side-by-side run) still stands as the reasoning; the "not yet implemented"
status that follows it no longer applies - see Status at the bottom.

## STT: Parakeet-TDT-0.6B-v3 recommended over Whisper Large-v3

| | Parakeet-TDT-0.6B-v3 | Whisper Large-v3 |
|---|---|---|
| Params | 0.6B | 1.55B |
| Italian WER (FLEURS) | 3.69% | 5.45% |
| Italian WER (MLS) | 10.08% | 9.26% |
| Multilingual avg (FLEURS) | ~12.0% | ~12.6% |
| Languages | 25 (European) | 99+ |

Results are mixed depending on benchmark - Whisper wins on MLS, Parakeet
wins on FLEURS and the multilingual average - but Parakeet does it at
roughly a third of the parameter count, which matters directly for PaRuX's
shared-VRAM-budget design. Given Italian is the spec's stated priority and
Parakeet leads on the more commonly cited FLEURS benchmark specifically,
**Parakeet-TDT-0.6B-v3** is the recommended default, with Whisper Large-v3
as a documented fallback for the 74+ languages Parakeet doesn't cover (it
supports 25 European languages only - a real, honest limitation to surface
in-app if a user picks a language outside that set).

## TTS: Kokoro-82M recommended

Kokoro-82M: 82M parameters, Apache-2.0, ~2-3GB VRAM (or CPU-only), 54 voices
across 8 languages including Italian and English, faster than real-time.
The parameter count alone makes the case for a VRAM-constrained shared-GPU
design like PaRuX's - an 82M model is close to negligible overhead
alongside a loaded LLM, unlike heavier natural-voice-cloning options.

Chatterbox (MIT, 0.5B, strong naturalness - reportedly preferred over
ElevenLabs by 65.3% of listeners in its own reported test) is a credible
alternative if voice quality turns out to matter more than footprint once
this is actually tested with real PaRuX output - noted here rather than
dismissed, since "best practical quality" per the spec's own image-quality
language could reasonably extend to audio.

## Status: implemented and live-verified

`SpeechService` in `PaRuX.Server` manages a Python venv outside the repo
(`%LOCALAPPDATA%\PaRuX\ai-runtimes\speech\`), same arm's-length pattern as
Training Lab. `POST /api/v1/speech/transcribe` and
`POST /api/v1/speech/synthesize` are both live and tested.

**Real round-trip verification**: synthesized "PaRuX runs entirely on your
own computer." with Kokoro via the actual API (230KB response, real 24kHz
WAV audio, 3.375s), then transcribed that exact audio back with Parakeet
via the actual API - got back "PaRuX runs entirely on your own computer."
verbatim. Both directions genuinely work, not just import-check.

Two real environment issues were found and fixed during setup, both now
baked into `InstallEngineAsync` so a fresh install doesn't hit them again:

1. Python 3.13 (this machine's default) can't build `curated_tokenizers`
   (a Cython dependency of `misaki`, which `kokoro` requires) - its C API
   support hasn't caught up. Fixed by creating the venv with Python 3.11
   via `uv` instead (the same fix class as TRELLIS's Python-version issue
   earlier this session).
2. A `uv`-created venv has no `pip` binary by default, but `misaki`'s spaCy
   dependency shells out to `pip` internally to fetch its English language
   model on first use, and failed with "No package installer found" until
   `uv pip install pip --python .venv` was added as an explicit install
   step.
3. `nvidia/parakeet-tdt-0.6b-v3`'s `transformers` pipeline needs `librosa`
   for its feature extractor - not pulled in automatically, added to the
   install command explicitly after hitting the real `ImportError` live.

Not yet done: Windows/Android chat UI wiring (voice input button, spoken
replies), and the spec's structured benchmark suite (Italian 10s/30s,
English 10s timed runs) - the round-trip above proves correctness, not
formal benchmark numbers.
