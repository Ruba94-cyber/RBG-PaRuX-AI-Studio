"""Create deterministic synthetic vision and Kokoro-TTS datasets in a fresh output directory.

Vision uses PIL-drawn shapes. Train and held-out rows share the same 12 colour/shape classes;
held-out rows therefore measure examples from known classes, not unseen-combination generalization.
TTS rows are synthetic clean speech and are not human-quality or natural-microphone evidence.
"""
import base64
import binascii
import json
import os
import random
import subprocess
import sys
import time
import wave
from pathlib import Path

from PIL import Image, ImageDraw

from safety import BoundedLineReader, OwnedProcessTree, ensure_fresh_directory, read_before_deadline

READY_TIMEOUT_SECONDS = 1200
FRAME_TIMEOUT_SECONDS = 300
SHUTDOWN_TIMEOUT_SECONDS = 30

out, tts_worker = Path(sys.argv[1]), sys.argv[2]
seed = int(sys.argv[3]) if len(sys.argv) > 3 else 20261004
rng = random.Random(seed)
out = ensure_fresh_directory(out)
(out / "images").mkdir()
(out / "audio").mkdir()

COLORS = {"red": (220, 40, 40), "green": (40, 170, 70), "blue": (50, 80, 220), "yellow": (240, 210, 40)}
SHAPES = ["circle", "square", "triangle"]
PROMPT = "Which colour and shape is shown? Answer with exactly '<colour> <shape>'."


def draw(color, shape, path, local_rng):
    img = Image.new("RGB", (224, 224), (245, 245, 245))
    d = ImageDraw.Draw(img)
    size = local_rng.randint(70, 110)
    x = local_rng.randint(10, 224 - size - 10)
    y = local_rng.randint(10, 224 - size - 10)
    fill = COLORS[color]
    if shape == "circle":
        d.ellipse([x, y, x + size, y + size], fill=fill)
    elif shape == "square":
        d.rectangle([x, y, x + size, y + size], fill=fill)
    else:
        d.polygon([(x + size // 2, y), (x, y + size), (x + size, y + size)], fill=fill)
    img.save(path)


TTS_TRAIN = [
    "The library opens at nine every morning.", "Please close the window before you leave.",
    "Our train departs from platform four.", "She planted tomatoes in the garden.",
    "The museum has a new exhibition this week.", "He always drinks tea after dinner.",
    "We walked along the river at sunset.", "The meeting has been moved to Friday.",
    "A small bird sat on the fence.", "Turn the volume down a little, please.",
    "The recipe needs two cups of flour.", "They are painting the kitchen blue.",
    "Remember to save your work often.", "The bakery sells fresh bread daily.",
    "It rained all night and the streets were quiet.", "My brother plays the violin very well.",
    "The package should arrive by Tuesday.", "Let us review the plan together.",
    "The mountain path was covered in snow.", "She wrote a letter to her grandmother.",
    "Check the battery before the long trip.", "The children built a castle from sand.",
    "He found an old map in the attic.", "Dinner will be ready in ten minutes.",
]
TTS_HELD = [
    "The garden gate was painted green.", "Please bring the documents to the office.",
    "Birds sing early in the spring.", "The coffee shop closes at six.",
    "He repaired the bicycle on Sunday.", "A cold wind blew across the harbor.",
]


def main():
    owner = None
    failure = None
    try:
        combos = [(c, s) for c in COLORS for s in SHAPES]
        train, held = [], []
        for i in range(120):
            c, s = combos[i % len(combos)]
            rel = f"images/train-{i:03d}.png"
            draw(c, s, out / rel, rng)
            train.append({"image": rel, "prompt": PROMPT, "response": f"{c} {s}"})
        for i in range(36):
            c, s = combos[i % len(combos)]
            rel = f"images/heldout-{i:03d}.png"
            draw(c, s, out / rel, rng)
            held.append({"image": rel, "prompt": PROMPT, "response": f"{c} {s}"})
        for name, rows in (("vision-train.jsonl", train), ("vision-heldout.jsonl", held)):
            with (out / name).open("x", encoding="utf-8", newline="\n") as f:
                f.writelines(json.dumps(r) + "\n" for r in rows)

        env = dict(os.environ, HF_HUB_DISABLE_TELEMETRY="1",
                   DO_NOT_TRACK="1", PYTHONIOENCODING="utf-8")
        owner = OwnedProcessTree.start([sys.executable, tts_worker], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                       stderr=subprocess.DEVNULL, text=True, encoding="utf-8", errors="replace", env=env)
        reader = BoundedLineReader(owner.process.stdout)
        ready = json.loads(reader.read(READY_TIMEOUT_SECONDS, "TTS worker ready frame"))
        if not isinstance(ready, dict) or ready.get("type") != "ready" or ready.get("sampleRate") != 24000:
            raise RuntimeError(f"invalid TTS ready frame: {ready!r}")

        def speak(request_id, text, path):
            owner.process.stdin.write(json.dumps({"apiVersion": "1", "id": request_id, "op": "synthesize",
                                                  "text": text, "voice": "af_heart"}) + "\n")
            owner.process.stdin.flush()
            pcm = bytearray()
            frames = 0
            deadline = time.monotonic() + FRAME_TIMEOUT_SECONDS
            while True:
                frame = json.loads(read_before_deadline(reader, deadline,
                                                        f"TTS frame for request {request_id}"))
                if not isinstance(frame, dict) or frame.get("id") != request_id:
                    raise RuntimeError(f"TTS response id/schema mismatch for {request_id}: {frame!r}")
                kind = frame.get("type")
                if kind == "audio":
                    try:
                        chunk = base64.b64decode(frame["base64"], validate=True)
                    except (KeyError, binascii.Error, ValueError) as exc:
                        raise RuntimeError(f"invalid audio frame for {request_id}: {exc}") from exc
                    if not chunk or len(chunk) % 2:
                        raise RuntimeError(f"invalid PCM payload for {request_id}")
                    pcm.extend(chunk)
                    frames += 1
                elif kind == "error":
                    raise RuntimeError(f"TTS worker error for {request_id}: {frame.get('error')}")
                elif kind == "done":
                    if frames == 0 or not pcm:
                        raise RuntimeError(f"TTS request {request_id} completed without audio")
                    break
                else:
                    raise RuntimeError(f"unknown TTS frame for {request_id}: {frame!r}")
            with wave.open(str(path), "wb") as w:
                w.setnchannels(1); w.setsampwidth(2); w.setframerate(24000); w.writeframes(bytes(pcm))

        for name, sentences in (("tts-train", TTS_TRAIN), ("tts-heldout", TTS_HELD)):
            rows = []
            for i, text in enumerate(sentences):
                rel = f"audio/{name}-{i:03d}.wav"
                speak(f"{name}-{i}", text, out / rel)
                rows.append({"text": text, "audio": rel})
            with (out / (name + ".jsonl")).open("x", encoding="utf-8", newline="\n") as f:
                f.writelines(json.dumps(r) + "\n" for r in rows)

        owner.process.stdin.write(json.dumps({"apiVersion": "1", "op": "shutdown"}) + "\n")
        owner.process.stdin.flush()
        owner.process.stdin.close()
        try:
            exit_code = owner.process.wait(timeout=SHUTDOWN_TIMEOUT_SECONDS)
        except subprocess.TimeoutExpired as exc:
            raise TimeoutError("TTS worker did not exit after shutdown request") from exc
        if exit_code != 0:
            raise RuntimeError(f"TTS worker exited with code {exit_code}")
        owner.cleanup(timeout=SHUTDOWN_TIMEOUT_SECONDS)
        owner = None
        metadata = {"status": "complete", "synthetic": True, "seed": seed,
                    "vision_rows": {"train": len(train), "heldout": len(held)},
                    "vision_split_limitation": "same 12 color/shape combinations occur in train and held-out; no unseen-combination generalization claim",
                    "tts_rows": {"train": len(TTS_TRAIN), "heldout": len(TTS_HELD)},
                    "tts_audio_scope": "synthetic clean Kokoro speech; no human-quality or natural-microphone claim"}
        with (out / "dataset-metadata.json").open("x", encoding="utf-8", newline="\n") as f:
            json.dump(metadata, f, indent=2); f.write("\n")
        print("synthetic multimodal datasets complete", json.dumps(metadata))
        return 0
    except BaseException as exc:
        failure = f"{type(exc).__name__}: {exc}"
        raise
    finally:
        cleanup_error = None
        if owner is not None:
            try:
                owner.cleanup(timeout=SHUTDOWN_TIMEOUT_SECONDS)
            except BaseException as exc:
                cleanup_error = f"cleanup failed: {type(exc).__name__}: {exc}"
        if failure is not None or cleanup_error is not None:
            state = {"status": "failed", "complete": False, "synthetic": True,
                     "error": failure, "cleanup_error": cleanup_error}
            marker = out / "dataset-metadata.json"
            if not marker.exists():
                with marker.open("x", encoding="utf-8", newline="\n") as f:
                    json.dump(state, f, indent=2); f.write("\n")


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except BaseException:
        raise
