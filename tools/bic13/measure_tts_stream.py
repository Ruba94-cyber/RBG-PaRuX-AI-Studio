"""Measure streaming Kokoro protocol behavior with bounded, owned worker processes.

This is a runtime latency/protocol probe, not human-perceived speech-quality evidence. Every required
phase must pass for benchmark_complete=true. Existing outputs are never overwritten.
Usage: python measure_tts_stream.py <worker.py> <out.json> [fresh_wav_dir]
"""
import atexit
import base64
import binascii
import json
import os
import platform
import subprocess
import sys
import time
import wave
from pathlib import Path

from safety import BoundedLineReader, OwnedProcessTree, ensure_fresh_directory, read_before_deadline, reserve_output, save_reserved_json

READY_TIMEOUT_SECONDS = 1200
REQUEST_TIMEOUT_SECONDS = 900
SHUTDOWN_TIMEOUT_SECONDS = 30
SHORT = "The quick brown fox jumps over the lazy dog."
LONG = ("PaRuX keeps every model on your own machine. Voice replies are produced sentence by sentence, "
        "so you can start listening while the rest is still being generated. If you interrupt, the current "
        "reply stops and the next one starts cleanly. This longer passage exists to measure how steady the "
        "stream is across several sentences of speech.")


class Worker:
    def __init__(self, worker_path):
        self.started = time.perf_counter()
        env = dict(os.environ, HF_HUB_DISABLE_TELEMETRY="1", DO_NOT_TRACK="1",
                   TOKENIZERS_PARALLELISM="false", PYTHONIOENCODING="utf-8")
        self.owner = OwnedProcessTree.start([sys.executable, worker_path], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                            stderr=subprocess.DEVNULL, text=True, encoding="utf-8", errors="replace", env=env)
        self.reader = BoundedLineReader(self.owner.process.stdout)
        try:
            self.ready = json.loads(self.reader.read(READY_TIMEOUT_SECONDS, "worker ready frame"))
            if not isinstance(self.ready, dict) or self.ready.get("type") != "ready" or self.ready.get("sampleRate") != 24000:
                raise RuntimeError(f"invalid worker ready frame: {self.ready!r}")
            self.ready_seconds = time.perf_counter() - self.started
        except BaseException:
            self.owner.cleanup(timeout=SHUTDOWN_TIMEOUT_SECONDS)
            raise

    def request(self, rid, text, stop_after_audio_frames=None, keep_wav=None):
        sent = time.perf_counter()
        self.owner.process.stdin.write(json.dumps({"apiVersion": "1", "id": rid, "op": "synthesize",
                                                   "text": text, "voice": "af_heart"}) + "\n")
        self.owner.process.stdin.flush()
        frames, first, last, gaps, pcm = 0, None, None, [], bytearray()
        deadline = time.monotonic() + REQUEST_TIMEOUT_SECONDS
        while True:
            frame = json.loads(read_before_deadline(self.reader, deadline, f"response for {rid}"))
            if not isinstance(frame, dict) or frame.get("id") != rid:
                raise RuntimeError(f"worker response id/schema mismatch for {rid}: {frame!r}")
            now = time.perf_counter()
            kind = frame.get("type")
            if kind == "audio":
                try:
                    payload = base64.b64decode(frame["base64"], validate=True)
                except (KeyError, binascii.Error, ValueError) as exc:
                    raise RuntimeError(f"invalid audio frame for {rid}: {exc}") from exc
                if not payload or len(payload) % 2:
                    raise RuntimeError(f"invalid PCM payload for {rid}")
                if first is None:
                    first = now
                else:
                    gaps.append(now - last)
                last = now
                frames += 1
                pcm.extend(payload)
                if stop_after_audio_frames and frames >= stop_after_audio_frames:
                    return {"ended": "client-stop", "frames": frames, "ttfa_ms": (first - sent) * 1000,
                            "wrong_id": False}
            elif kind == "done":
                if frames == 0 or not pcm or first is None:
                    raise RuntimeError(f"worker completed request {rid} without audio")
                total = time.perf_counter() - sent
                audio_seconds = len(pcm) / 2 / 24000
                if keep_wav:
                    with wave.open(str(keep_wav), "wb") as w:
                        w.setnchannels(1); w.setsampwidth(2); w.setframerate(24000); w.writeframes(bytes(pcm))
                return {"ended": "done", "frames": frames, "ttfa_ms": (first - sent) * 1000,
                        "worker_reported_ttfa_ms": frame.get("ttfaMilliseconds"), "wall_seconds": total,
                        "audio_seconds": audio_seconds, "rtf_wall_per_audio": total / audio_seconds,
                        "max_interframe_gap_ms": max(gaps, default=0) * 1000,
                        "first_frame_to_done_ms": (time.perf_counter() - first) * 1000, "wrong_id": False}
            elif kind == "error":
                raise RuntimeError(f"worker error for {rid}: {frame.get('error')}")
            else:
                raise RuntimeError(f"unknown worker frame for {rid}: {frame!r}")

    def shutdown(self, timeout=SHUTDOWN_TIMEOUT_SECONDS):
        try:
            self.owner.process.stdin.write(json.dumps({"apiVersion": "1", "op": "shutdown"}) + "\n")
            self.owner.process.stdin.flush()
        except (BrokenPipeError, OSError):
            pass
        self.owner.process.stdin.close()
        try:
            code = self.owner.process.wait(timeout=timeout)
        except subprocess.TimeoutExpired as exc:
            raise TimeoutError("worker did not exit after shutdown request") from exc
        if code != 0:
            raise RuntimeError(f"worker exited with code {code} after shutdown")
        self.owner.cleanup(timeout=timeout)

    def cleanup(self):
        if not self.owner.closed:
            self.owner.cleanup(timeout=SHUTDOWN_TIMEOUT_SECONDS)


def _stats(rows, key):
    values = [row[key] for row in rows]
    if not values or any(value is None for value in values):
        raise RuntimeError(f"cannot summarize incomplete metric {key}")
    values.sort()
    n = len(values)
    median = values[n // 2] if n % 2 else (values[n // 2 - 1] + values[n // 2]) / 2
    return {"min": min(values), "median": median, "max": max(values), "n": n}


def _require_done(row, phase):
    if row.get("ended") != "done" or row.get("frames", 0) <= 0 or row.get("wrong_id"):
        raise RuntimeError(f"required {phase} phase failed: {row}")
    return row


def main(argv=None):
    args = list(sys.argv[1:] if argv is None else argv)
    if len(args) not in (2, 3):
        raise SystemExit("Usage: measure_tts_stream.py <worker.py> <out.json> [fresh_wav_dir]")
    worker_path, out_path = args[:2]
    result_handle = reserve_output(out_path)
    wav_dir = None
    result = {"benchmark_complete": False, "status": "running",
              "hardware": {"python": platform.python_version(), "os": platform.platform()},
              "required_phases": ["cold", "warm_short", "warm_long_incremental", "barge_in", "follow_up_recovery"]}
    active = []
    finished = False

    def write_incomplete():
        if not finished and not result_handle.closed:
            result.update(status="failed_or_interrupted", benchmark_complete=False,
                          error=result.get("error", "process exited before all required phases completed"))
            save_reserved_json(result_handle, result)
            result_handle.close()

    atexit.register(write_incomplete)
    try:
        wav_dir = ensure_fresh_directory(args[2]) if len(args) == 3 else None
        w = Worker(worker_path); active.append(w)
        cold = _require_done(w.request("cold-1", SHORT, keep_wav=(wav_dir / "short-cold.wav" if wav_dir else None)), "cold")
        result["cold"] = {"process_ready_seconds": w.ready_seconds, "first_request": cold}
        warm_short = [_require_done(w.request(f"ws-{i}", SHORT), f"warm-short-{i}") for i in range(5)]
        warm_long = [_require_done(w.request(f"wl-{i}", LONG, keep_wav=(wav_dir / "long.wav" if wav_dir and i == 0 else None)),
                                   f"warm-long-{i}") for i in range(3)]
        if not all(row["frames"] > 1 and row["first_frame_to_done_ms"] > 0 for row in warm_long):
            raise RuntimeError("warm-long incremental streaming proof failed")
        result["warm_short"] = {"runs": warm_short, "ttfa_ms": _stats(warm_short, "ttfa_ms"),
                                 "rtf": _stats(warm_short, "rtf_wall_per_audio")}
        result["warm_long"] = {"runs": warm_long, "ttfa_ms": _stats(warm_long, "ttfa_ms"),
                               "rtf": _stats(warm_long, "rtf_wall_per_audio"), "incremental": True}
        interrupted = w.request("barge-1", LONG, stop_after_audio_frames=1)
        if interrupted.get("ended") != "client-stop" or interrupted.get("frames") != 1:
            raise RuntimeError(f"barge-in did not stop after first audio frame: {interrupted}")
        w.cleanup()
        result["barge_in"] = {"interrupted": interrupted, "process_exited": w.owner.process.poll() is not None,
                              "owned_tree_cleanup_verified": w.owner.closed,
                              "buffered_frames_discarded": "not-delivered-after-owned-tree-termination"}
        if not result["barge_in"]["process_exited"] or not result["barge_in"]["owned_tree_cleanup_verified"]:
            raise RuntimeError("barge-in owned process cleanup was not verified")
        active.remove(w)

        w2 = Worker(worker_path); active.append(w2)
        follow = _require_done(w2.request("follow-1", "Okay, continuing with the follow-up question.",
                                           keep_wav=(wav_dir / "followup.wav" if wav_dir else None)), "follow-up")
        result["follow_up_recovery"] = {"restart_ready_seconds": w2.ready_seconds, "request": follow,
                                         "total_recovery_to_first_audio_ms": w2.ready_seconds * 1000 + follow["ttfa_ms"],
                                         "correct_request_id_on_all_frames": not follow["wrong_id"]}
        w2.shutdown(); active.remove(w2)
        result["benchmark_complete"] = True
        result["status"] = "complete"
        synthetic_fixture = any(bool(worker.ready.get("syntheticFixture")) for worker in (w, w2))
        result["synthetic_fixture"] = synthetic_fixture
        result["performance_benchmark"] = not synthetic_fixture
        save_reserved_json(result_handle, result)
        result_handle.close(); finished = True
        print(json.dumps({k: result[k] for k in ("cold", "barge_in", "follow_up_recovery")}, allow_nan=False))
        return 0
    except BaseException as exc:
        result.update(status="failed", benchmark_complete=False, error=f"{type(exc).__name__}: {exc}")
        raise
    finally:
        errors = []
        for worker in list(active):
            try:
                worker.cleanup()
            except BaseException as exc:
                errors.append(f"{type(exc).__name__}: {exc}")
        if errors:
            result["cleanup_errors"] = errors
            result["benchmark_complete"] = False
            result["status"] = "failed"
            if not result_handle.closed:
                save_reserved_json(result_handle, result); result_handle.close()
        elif not result_handle.closed:
            save_reserved_json(result_handle, result); result_handle.close()


if __name__ == "__main__":
    raise SystemExit(main())
