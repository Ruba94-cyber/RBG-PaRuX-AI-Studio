"""CPU-only synthetic checks for the BIC13 media measurement harness.

Every value in this module is explicitly synthetic. Tests stub subprocess and
GPU telemetry and never launch nvidia-smi, CUDA, a model runtime, or the UI.
"""
from __future__ import annotations

import importlib.util
import io
import json
import math
import subprocess
import struct
import sys
import tempfile
import threading
import unittest
import zlib
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).with_name("measure_media_cold_warm.py")
SPEC = importlib.util.spec_from_file_location("measure_media_cold_warm", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
measurement = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = measurement
SPEC.loader.exec_module(measurement)


SYNTHETIC_GPU_SAMPLE = {"memory_used_mib": 1400, "memory_total_mib": 8192,
                        "utilization_percent": 12, "fixture": "SYNTHETIC_NOT_BENCHMARK"}


class FakeTree:
    def __init__(self, stdout: str = "SYNTHETIC stdout", stderr: str = "", code: int = 0,
                 communicate_error: BaseException | None = None):
        self.process = mock.Mock(returncode=code, pid=4242)
        self.stdout = stdout
        self.stderr = stderr
        self.communicate_error = communicate_error
        self.cleanup_count = 0

    def communicate(self, timeout: float):
        if self.communicate_error:
            raise self.communicate_error
        return self.stdout, self.stderr

    def cleanup(self):
        self.cleanup_count += 1


class MediaMeasurementTests(unittest.TestCase):
    @staticmethod
    def write_png(path: Path, width: int, height: int) -> None:
        def chunk(kind: bytes, data: bytes) -> bytes:
            return (len(data).to_bytes(4, "big") + kind + data +
                    (zlib.crc32(kind + data) & 0xFFFFFFFF).to_bytes(4, "big"))

        ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
        raw = b"".join(b"\0" + bytes(width * 4) for _ in range(height))
        path.write_bytes(measurement.PNG_SIGNATURE + chunk(b"IHDR", ihdr) +
                         chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))

    def test_smi_validates_success_timeout_single_gpu_shape_and_ranges(self):
        completed = subprocess.CompletedProcess([], 0, "120, 8192, 5\n", "")
        with mock.patch.object(measurement.subprocess, "run", return_value=completed) as run:
            self.assertEqual(measurement.smi_used(), {
                "memory_used_mib": 120, "memory_total_mib": 8192, "utilization_percent": 5})
            run.assert_called_once()
            self.assertEqual(run.call_args.kwargs["timeout"], measurement.GPU_QUERY_TIMEOUT_SECONDS)

        with mock.patch.object(measurement.subprocess, "run",
                               return_value=subprocess.CompletedProcess([], 1, "", "SYNTHETIC failure")):
            with self.assertRaisesRegex(RuntimeError, "exited 1"):
                measurement.smi_used()
        with mock.patch.object(measurement.subprocess, "run",
                               side_effect=subprocess.TimeoutExpired("nvidia-smi", 1)):
            with self.assertRaises(TimeoutError):
                measurement.smi_used(timeout=1)
        for output in ("1, 2, 3\n4, 5, 6\n", "1, 2\n", "x, 2, 3\n", "3, 2, 1\n", "1, 2, 101\n"):
            with self.subTest(output=output), mock.patch.object(
                    measurement.subprocess, "run",
                    return_value=subprocess.CompletedProcess([], 0, output, "")):
                with self.assertRaises(RuntimeError):
                    measurement.smi_used()

    def test_poller_propagates_errors_and_never_invents_zero_samples(self):
        def broken_reader():
            raise RuntimeError("SYNTHETIC telemetry failure")

        poller = measurement.Poller(broken_reader, interval=0.001)
        poller.start()
        poller.join(timeout=1)
        self.assertFalse(poller.is_alive())
        self.assertEqual(poller.samples, [])
        with self.assertRaisesRegex(RuntimeError, "telemetry poller failed"):
            poller.raise_if_invalid()
        partial = poller.partial_summary(poller.error)
        self.assertEqual(partial["sample_count"], 0)
        self.assertNotIn("peak_memory_used_mib", partial)

    def test_poller_counts_observed_synthetic_samples(self):
        sampled = threading.Event()

        def reader():
            sampled.set()
            return dict(SYNTHETIC_GPU_SAMPLE)

        poller = measurement.Poller(reader, interval=10)
        poller.start()
        self.assertTrue(sampled.wait(timeout=1))
        poller.stop()
        summary = poller.summary()
        self.assertEqual(summary["sample_count"], 1)
        self.assertEqual(summary["peak_memory_used_mib"], 1400)
        self.assertEqual(summary["scope"], "system-wide single-GPU telemetry")

    def test_owned_command_stub_success_failure_and_timeout_cleanup(self):
        success = FakeTree(code=0)
        with mock.patch.object(measurement.OwnedProcessTree, "start", return_value=success):
            code, stdout, _ = measurement.run_owned_command(["SYNTHETIC.exe"], 2, {})
        self.assertEqual((code, stdout), (0, "SYNTHETIC stdout"))
        self.assertEqual(success.cleanup_count, 1)

        failed = FakeTree(stderr="SYNTHETIC nonzero", code=17)
        with mock.patch.object(measurement.OwnedProcessTree, "start", return_value=failed):
            code, _, stderr = measurement.run_owned_command(["SYNTHETIC.exe"], 2, {})
        self.assertEqual((code, stderr), (17, "SYNTHETIC nonzero"))
        self.assertEqual(failed.cleanup_count, 1)

        timed_out = FakeTree(communicate_error=subprocess.TimeoutExpired("SYNTHETIC.exe", 1))
        with mock.patch.object(measurement.OwnedProcessTree, "start", return_value=timed_out):
            with self.assertRaises(TimeoutError):
                measurement.run_owned_command(["SYNTHETIC.exe"], 1, {})
        self.assertEqual(timed_out.cleanup_count, 1)

    def test_output_directory_must_be_fresh_or_empty(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp) / "SYNTHETIC fresh output"
            measurement._prepare_output_directory(out)
            self.assertTrue(out.is_dir())
            (out / "preserve-me.json").write_text("SYNTHETIC prior evidence", encoding="utf-8")
            with self.assertRaisesRegex(RuntimeError, "nonempty"):
                measurement._prepare_output_directory(out)
            self.assertEqual((out / "preserve-me.json").read_text(encoding="utf-8"), "SYNTHETIC prior evidence")

    def test_png_validation_rejects_missing_bad_and_wrong_size_outputs(self):
        with tempfile.TemporaryDirectory() as temp:
            png = Path(temp) / "SYNTHETIC.png"
            self.write_png(png, 2, 3)
            self.assertEqual(len(measurement._validate_png(png, 2, 3)), 64)
            with self.assertRaisesRegex(RuntimeError, "dimensions"):
                measurement._validate_png(png, 3, 2)
            png.write_bytes(b"SYNTHETIC invalid PNG content" * 3)
            with self.assertRaisesRegex(RuntimeError, "invalid PNG header"):
                measurement._validate_png(png, 2, 3)
            with self.assertRaisesRegex(RuntimeError, "missing or too short"):
                measurement._validate_png(Path(temp) / "missing.png", 2, 3)

    def test_two_run_median_uses_true_unrounded_statistics_median(self):
        result = {"cold": [{"exit": 0, "sha256": "a", "wall_seconds": 10.101},
                           {"exit": 0, "sha256": "b", "wall_seconds": 20.203}],
                  "warm": [{"type": "done", "sha256": "a", "wall_seconds": 2.001},
                           {"type": "done", "sha256": "b", "wall_seconds": 4.003}]}
        measurement._record_summary(result)
        self.assertAlmostEqual(result["summary"]["cold_median_seconds"], 15.152)
        self.assertAlmostEqual(result["summary"]["warm_median_seconds"], 3.002)
        self.assertAlmostEqual(result["summary"]["warm_speedup_x"], 15.152 / 3.002)
        self.assertIn("statistics.median", result["summary"]["median_method"])

    def test_completion_requires_real_samples_and_all_requested_outputs(self):
        complete = {"cold": [{"exit": 0, "sha256": "a", "wall_seconds": 1,
                               "gpu_telemetry": {"sample_count": 1}}],
                    "warm": [{"type": "done", "sha256": "a", "request_id": "w0", "response_id": "w0",
                              "wall_seconds": 2, "worker_generation_ms": 1500,
                              "peak_allocated_bytes": 64, "peak_reserved_bytes": 128}],
                    "review_items": [],
                    "resident_load": {"process_to_ready_seconds": 1, "worker_load_ms": 700,
                                      "allocated_bytes": 64, "reserved_bytes": 128},
                    "resident_worker_exit_code": 0,
                    "resident_system_gpu_telemetry": {"sample_count": 1}}
        self.assertTrue(measurement._validate_completion(complete, 1, 1, False))
        no_samples = json.loads(json.dumps(complete))
        no_samples["resident_system_gpu_telemetry"]["sample_count"] = 0
        self.assertFalse(measurement._validate_completion(no_samples, 1, 1, False))
        missing_output = json.loads(json.dumps(complete))
        del missing_output["cold"][0]["sha256"]
        self.assertFalse(measurement._validate_completion(missing_output, 1, 1, False))

        for change in (
                lambda value: value["warm"][0].update(response_id="wrong"),
                lambda value: value["warm"][0].update(worker_generation_ms=math.nan),
                lambda value: value["warm"][0].update(peak_allocated_bytes=-1),
                lambda value: value["warm"][0].update(peak_allocated_bytes=129),
                lambda value: value["resident_load"].pop("worker_load_ms"),
                lambda value: value.update(resident_worker_exit_code=23)):
            incomplete = json.loads(json.dumps(complete))
            change(incomplete)
            self.assertFalse(measurement._validate_completion(incomplete, 1, 1, False))

    def test_resident_worker_rejects_wrong_warm_and_review_ids(self):
        for include_reviews, responses, expected in (
                (False, [{"type": "ready", "loadMilliseconds": 7, "allocatedBytes": 32, "reservedBytes": 64},
                         {"type": "done", "id": "other", "generationMilliseconds": 8,
                          "peakAllocatedBytes": 32, "peakReservedBytes": 64}], "response id mismatch"),
                (True, [{"type": "ready", "loadMilliseconds": 7, "allocatedBytes": 32, "reservedBytes": 64},
                        {"type": "done", "id": "w0", "generationMilliseconds": 8,
                         "peakAllocatedBytes": 32, "peakReservedBytes": 64},
                        {"type": "done", "id": "other", "generationMilliseconds": 8,
                         "peakAllocatedBytes": 32, "peakReservedBytes": 64}], "response id mismatch")):
            with self.subTest(include_reviews=include_reviews), tempfile.TemporaryDirectory() as temp:
                out = Path(temp)
                process = mock.Mock(returncode=None, pid=4242, stdin=io.StringIO(), wait=mock.Mock(return_value=0))
                tree = mock.Mock(process=process, cleanup=mock.Mock())

                class Reader:
                    def __init__(self, _stream):
                        self.index = 0

                    def read(self, _timeout, phase):
                        value = responses[self.index]
                        self.index += 1
                        if value["type"] == "done":
                            output_name = "warm-0.png" if phase.startswith("warm") else "review-text-open-sign.png"
                            self_outer.write_png(out / output_name, 2, 2)
                        return value

                self_outer = self
                result = {"requested_cold_runs": 0, "requested_warm_runs": 1,
                          "cold": [], "warm": [], "review_items": [],
                          "review_images_requested": include_reviews}
                with mock.patch.object(measurement.OwnedProcessTree, "start", return_value=tree), \
                        mock.patch.object(measurement, "JsonLineReader", Reader):
                    with self.assertRaisesRegex(RuntimeError, expected):
                        measurement._run_measurement(
                            result, Path("runner.py"), Path("worker.py"), Path("model"), out,
                            2, 2, 1, 1, 0, include_reviews,
                            lambda: dict(SYNTHETIC_GPU_SAMPLE), lambda *_args: (0, "", ""))
                tree.cleanup.assert_called_once()
                self.assertFalse(measurement._validate_completion(result, 0, 1, include_reviews))

    def test_resident_worker_nonzero_shutdown_exit_fails(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp)
            process = mock.Mock(returncode=None, pid=4242, stdin=io.StringIO(), wait=mock.Mock(return_value=23))
            tree = mock.Mock(process=process, cleanup=mock.Mock())
            responses = [{"type": "ready", "loadMilliseconds": 7, "allocatedBytes": 32, "reservedBytes": 64},
                         {"type": "done", "id": "w0", "generationMilliseconds": 8,
                          "peakAllocatedBytes": 32, "peakReservedBytes": 64}]

            class Reader:
                def __init__(self, _stream):
                    self.index = 0

                def read(self, _timeout, phase):
                    value = responses[self.index]
                    self.index += 1
                    if value["type"] == "done":
                        self_outer.write_png(out / "warm-0.png", 2, 2)
                    return value

            self_outer = self
            result = {"requested_cold_runs": 0, "requested_warm_runs": 1,
                      "cold": [], "warm": [], "review_items": [], "review_images_requested": False}
            with mock.patch.object(measurement.OwnedProcessTree, "start", return_value=tree), \
                    mock.patch.object(measurement, "JsonLineReader", Reader):
                with self.assertRaisesRegex(RuntimeError, "exited 23 after shutdown"):
                    measurement._run_measurement(
                        result, Path("runner.py"), Path("worker.py"), Path("model"), out,
                        2, 2, 1, 1, 0, False,
                        lambda: dict(SYNTHETIC_GPU_SAMPLE), lambda *_args: (0, "", ""))
            self.assertEqual(result["resident_worker_exit_code"], 23)
            tree.cleanup.assert_called_once()
            self.assertFalse(measurement._validate_completion(result, 0, 1, False))

    def test_main_saves_synthetic_partial_failure_and_exits_nonzero(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp) / "SYNTHETIC partial result"

            def fail_after_partial(result, *args, **kwargs):
                result["cold"].append({"run": 1, "exit": 1, "wall_seconds": 3.5,
                                       "error": "SYNTHETIC generation failure"})
                raise RuntimeError("SYNTHETIC timeout after one saved run")

            with mock.patch.object(measurement, "_run_measurement", side_effect=fail_after_partial), \
                    mock.patch.object(measurement.sys, "stderr", io.StringIO()), \
                    mock.patch.object(measurement.sys, "stdout", io.StringIO()):
                exit_code = measurement.main(["runner.py", "worker.py", "model", str(out), "64", "64", "1", "3", "2", "0"])
            saved = json.loads((out / "cold-warm.json").read_text(encoding="utf-8"))
            self.assertEqual(exit_code, 1)
            self.assertFalse(saved["benchmark_complete"])
            self.assertIn("SYNTHETIC timeout", saved["error"])
            self.assertEqual(saved["cold"][0]["error"], "SYNTHETIC generation failure")

    def test_cold_generation_nonzero_is_saved_as_incomplete(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp)
            result = {"requested_cold_runs": 1, "requested_warm_runs": 1,
                      "cold": [], "warm": [], "review_items": [],
                      "review_images_requested": False}

            sample_count = 0
            sampled = threading.Event()

            def gpu_reader():
                nonlocal sample_count
                sample_count += 1
                if sample_count >= 3:
                    sampled.set()
                return dict(SYNTHETIC_GPU_SAMPLE)

            def failing_command(args, timeout, env):
                self.assertTrue(sampled.wait(timeout=1))
                return 17, "", "SYNTHETIC generation failed"

            with self.assertRaisesRegex(RuntimeError, "exited 17"):
                measurement._run_measurement(
                    result, Path("runner.py"), Path("worker.py"), Path("model"), out,
                    64, 64, 1, 1, 1, False,
                    gpu_reader, failing_command)
            self.assertEqual(result["cold"][0]["exit"], 17)
            self.assertIn("error", result["cold"][0])
            self.assertFalse(measurement._validate_completion(result, 1, 1, False))

    def test_cold_timeout_retains_partial_phase_and_marks_timeout(self):
        with tempfile.TemporaryDirectory() as temp:
            result = {"requested_cold_runs": 1, "requested_warm_runs": 1,
                      "cold": [], "warm": [], "review_items": [],
                      "review_images_requested": False}
            sample_count = 0
            sampled = threading.Event()

            def gpu_reader():
                nonlocal sample_count
                sample_count += 1
                if sample_count >= 3:
                    sampled.set()
                return dict(SYNTHETIC_GPU_SAMPLE)

            def timeout_command(args, timeout, env):
                self.assertTrue(sampled.wait(timeout=1))
                raise TimeoutError("SYNTHETIC process timeout")

            with self.assertRaisesRegex(TimeoutError, "SYNTHETIC process timeout"):
                measurement._run_measurement(
                    result, Path("runner.py"), Path("worker.py"), Path("model"), Path(temp),
                    64, 64, 1, 1, 1, False, gpu_reader, timeout_command)
            self.assertEqual(len(result["cold"]), 1)
            self.assertEqual(result["cold"][0]["failure_kind"], "timeout")
            self.assertFalse(measurement._validate_completion(result, 1, 1, False))

    def test_resource_scopes_are_explicit(self):
        self.assertIn("system-wide", measurement.RESOURCE_MEASUREMENT_SCOPE["nvidia_smi"])
        self.assertIn("process-attributed", measurement.RESOURCE_MEASUREMENT_SCOPE["resident_worker_peaks"])
        self.assertEqual(measurement.DEFAULT_COLD_RUNS, 2)
        self.assertEqual(measurement.DEFAULT_WARM_RUNS, 3)
        self.assertEqual(measurement.COLD_GENERATION_TIMEOUT_SECONDS, 1500)
        self.assertEqual(measurement.WARM_GENERATION_TIMEOUT_SECONDS, 1500)


if __name__ == "__main__":
    unittest.main()
