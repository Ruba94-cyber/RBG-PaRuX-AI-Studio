"""Measure STT on Kokoro-generated clean synthetic speech; not microphone/human-quality evidence.

Usage: python measure_stt_quality.py <tts_worker.py> <out.json> <fresh_wav_dir>
The reference-audio worker and isolated ASR child have explicit deadlines and launch-scoped cleanup.
"""
import base64
import binascii
import json
import os
import platform
import re
import subprocess
import sys
import time
import wave
from pathlib import Path

from safety import BoundedLineReader, OwnedProcessTree, ensure_fresh_directory, read_before_deadline, reserve_output, save_reserved_json

READY_TIMEOUT_SECONDS = 1200
FRAME_TIMEOUT_SECONDS = 300
ASR_TIMEOUT_SECONDS = 3600
SHUTDOWN_TIMEOUT_SECONDS = 30
SENTENCES = [
    "The weather in Rome is sunny today with a light breeze from the west.",
    "Please schedule a meeting with the design team on Thursday at three thirty.",
    "Local models keep your documents on your own computer and never upload them.",
    "Turn left at the second traffic light and the pharmacy is on your right.",
    "She sold seashells by the seashore and bought twelve oranges for her brother.",
    "Open the settings panel, select the voice tab, and press the test button.",
]


def normalise(text):
    return re.sub(r"[^a-z0-9' ]", " ", text.lower()).split()


def wer(ref, hyp):
    r, h = normalise(ref), normalise(hyp)
    d = list(range(len(h) + 1))
    for i, rw in enumerate(r, 1):
        prev, d[0] = d[0], i
        for j, hw in enumerate(h, 1):
            cur = min(d[j] + 1, d[j - 1] + 1, prev + (rw != hw))
            prev, d[j] = d[j], cur
    return d[len(h)] / max(1, len(r)), len(r)


def asr_child(wavs, result_path):
    import torch
    from transformers import pipeline
    started = time.perf_counter()
    asr = pipeline("automatic-speech-recognition", model="nvidia/parakeet-tdt-0.6b-v3", device=0)
    load_seconds = time.perf_counter() - started
    rows, errors, words = [], 0.0, 0
    for i, (sentence, path) in enumerate(zip(SENTENCES, wavs)):
        before = time.perf_counter()
        output = asr(path)
        seconds = time.perf_counter() - before
        text = output["text"] if isinstance(output, dict) else str(output)
        rate, count = wer(sentence, text)
        errors += rate * count
        words += count
        rows.append({"reference": sentence, "hypothesis": text, "wer": round(rate, 4),
                     "transcribe_seconds": round(seconds, 3), "first_call": i == 0})
    if len(rows) != len(SENTENCES) or words <= 0:
        raise RuntimeError("ASR child produced incomplete utterance results")
    result = {"model": "nvidia/parakeet-tdt-0.6b-v3", "model_load_seconds": round(load_seconds, 2),
              "utterances": rows, "overall_wer": round(errors / words, 4), "total_reference_words": words,
              "audio_source": "Kokoro af_heart synthetic speech; clean round trip, not natural/noisy microphone speech",
              "hardware": {"gpu": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None,
                           "torch": torch.__version__, "python": platform.python_version(), "os": platform.platform()}}
    with open(result_path, "x", encoding="utf-8", newline="\n") as handle:
        json.dump(result, handle, indent=2, allow_nan=False); handle.write("\n")


def synthesize_worker(tts_worker, wav_dir):
    env = dict(os.environ, HF_HUB_DISABLE_TELEMETRY="1",
               DO_NOT_TRACK="1", PYTHONIOENCODING="utf-8")
    owner = OwnedProcessTree.start([sys.executable, tts_worker], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=subprocess.DEVNULL, text=True, encoding="utf-8", errors="replace", env=env)
    reader = BoundedLineReader(owner.process.stdout)
    try:
        ready = json.loads(reader.read(READY_TIMEOUT_SECONDS, "TTS worker ready frame"))
        if not isinstance(ready, dict) or ready.get("type") != "ready" or ready.get("sampleRate") != 24000:
            raise RuntimeError(f"invalid TTS ready frame: {ready!r}")
        wavs = []
        for i, sentence in enumerate(SENTENCES):
            rid = f"stt-{i}"
            owner.process.stdin.write(json.dumps({"apiVersion": "1", "id": rid, "op": "synthesize",
                                                  "text": sentence, "voice": "af_heart"}) + "\n")
            owner.process.stdin.flush()
            pcm, count = bytearray(), 0
            deadline = time.monotonic() + FRAME_TIMEOUT_SECONDS
            while True:
                frame = json.loads(read_before_deadline(reader, deadline, f"TTS frame {rid}"))
                if not isinstance(frame, dict) or frame.get("id") != rid:
                    raise RuntimeError(f"TTS response id/schema mismatch for {rid}: {frame!r}")
                if frame.get("type") == "audio":
                    try:
                        chunk = base64.b64decode(frame["base64"], validate=True)
                    except (KeyError, binascii.Error, ValueError) as exc:
                        raise RuntimeError(f"invalid audio frame for {rid}: {exc}") from exc
                    if not chunk or len(chunk) % 2:
                        raise RuntimeError(f"invalid PCM payload for {rid}")
                    pcm.extend(chunk); count += 1
                elif frame.get("type") == "error":
                    raise RuntimeError(f"TTS worker error for {rid}: {frame.get('error')}")
                elif frame.get("type") == "done":
                    if count == 0 or not pcm:
                        raise RuntimeError(f"TTS request {rid} returned no audio")
                    break
                else:
                    raise RuntimeError(f"unknown TTS frame for {rid}: {frame!r}")
            wav = wav_dir / f"stt-ref-{i}.wav"
            with wave.open(str(wav), "wb") as out:
                out.setnchannels(1); out.setsampwidth(2); out.setframerate(24000); out.writeframes(bytes(pcm))
            wavs.append(str(wav))
        owner.process.stdin.write(json.dumps({"apiVersion": "1", "op": "shutdown"}) + "\n")
        owner.process.stdin.flush(); owner.process.stdin.close()
        try:
            code = owner.process.wait(timeout=SHUTDOWN_TIMEOUT_SECONDS)
        except subprocess.TimeoutExpired as exc:
            raise TimeoutError("TTS worker did not exit after shutdown") from exc
        if code != 0:
            raise RuntimeError(f"TTS worker exited with code {code}")
        owner.cleanup(timeout=SHUTDOWN_TIMEOUT_SECONDS)
        return wavs
    finally:
        if not owner.closed:
            owner.cleanup(timeout=SHUTDOWN_TIMEOUT_SECONDS)


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv and argv[0] == "--asr-child":
        asr_child(argv[1:-1], argv[-1])
        return 0
    if len(argv) != 3:
        raise SystemExit("Usage: measure_stt_quality.py <tts_worker.py> <out.json> <fresh_wav_dir>")
    tts_worker, out_path, wav_dir_arg = argv
    result_handle = reserve_output(out_path)
    wav_dir = None
    result = {"benchmark_complete": False, "status": "running", "audio_source": "synthetic clean Kokoro speech"}
    try:
        wav_dir = ensure_fresh_directory(wav_dir_arg)
        wavs = synthesize_worker(tts_worker, wav_dir)
        child_out = wav_dir / "asr-result.json"
        env = dict(os.environ, HF_HUB_DISABLE_TELEMETRY="1", DO_NOT_TRACK="1", PYTHONIOENCODING="utf-8")
        owner = OwnedProcessTree.start([sys.executable, str(Path(__file__).resolve()), "--asr-child", *wavs, str(child_out)],
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8",
                                       errors="replace", env=env)
        try:
            try:
                stdout, stderr = owner.communicate(timeout=ASR_TIMEOUT_SECONDS)
            except subprocess.TimeoutExpired as exc:
                raise TimeoutError(f"ASR child exceeded {ASR_TIMEOUT_SECONDS}s deadline") from exc
        finally:
            # Job cleanup also terminates descendants when the direct child exited
            # early or communicate/output handling raised before normal completion.
            owner.cleanup(timeout=SHUTDOWN_TIMEOUT_SECONDS)
        code = owner.process.returncode
        if code != 0:
            raise RuntimeError(f"isolated ASR child exited {code}: {(stderr or '')[-1000:]}")
        child_result = json.loads(child_out.read_text(encoding="utf-8"))
        if len(child_result.get("utterances", [])) != len(SENTENCES):
            raise RuntimeError("ASR child result is incomplete")
        result.update(child_result, benchmark_complete=True, status="complete")
        save_reserved_json(result_handle, result)
        result_handle.close()
        print(json.dumps({k: v for k, v in result.items() if k != "utterances"}))
        return 0
    except BaseException as exc:
        result.update(status="failed", benchmark_complete=False, error=f"{type(exc).__name__}: {exc}")
        save_reserved_json(result_handle, result)
        result_handle.close()
        raise


if __name__ == "__main__":
    raise SystemExit(main())
