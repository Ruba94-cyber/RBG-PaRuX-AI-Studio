"""Measure cold-process and resident-worker Z-Image-Turbo generation.

The script records partial results on failure and only reports a complete run
when every requested generation, PNG validation, and required GPU sample passes.
Resource telemetry is deliberately labeled by scope: NVIDIA samples are
system-wide for the single visible GPU; resident torch peaks are process values.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import math
import os
import platform
import queue
import signal
import statistics
import subprocess
import sys
import threading
import time
import zlib
from pathlib import Path
from typing import Any, Callable


PROMPT = "A watercolor painting of a small cottage by a lake at dawn, a wooden sign in front reading OPEN in clear capital letters"
SEED = 20261004
SAMPLE_INTERVAL_SECONDS = 0.5
GPU_QUERY_TIMEOUT_SECONDS = 5
COLD_GENERATION_TIMEOUT_SECONDS = 1500
WARM_GENERATION_TIMEOUT_SECONDS = 1500
RESIDENT_STARTUP_TIMEOUT_SECONDS = 1800
RESIDENT_SHUTDOWN_TIMEOUT_SECONDS = 60
DEFAULT_COLD_RUNS = 2
DEFAULT_WARM_RUNS = 3
RESOURCE_MEASUREMENT_SCOPE = {
    "nvidia_smi": "system-wide memory/utilization for exactly one visible GPU; memory.used delta is baseline-relative",
    "resident_worker_peaks": "process-attributed torch allocated/reserved peaks reported by resident-worker.py",
}
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def smi_used(timeout: float = GPU_QUERY_TIMEOUT_SECONDS) -> dict[str, int]:
    """Return one validated GPU sample, failing on all unavailable/ambiguous data."""
    try:
        completed = subprocess.run(
            ["nvidia-smi", "--query-gpu=memory.used,memory.total,utilization.gpu",
             "--format=csv,noheader,nounits"],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            timeout=timeout, check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise TimeoutError(f"nvidia-smi telemetry timed out after {timeout}s") from exc
    except OSError as exc:
        raise RuntimeError(f"nvidia-smi telemetry could not start: {exc}") from exc
    if completed.returncode != 0:
        raise RuntimeError(f"nvidia-smi exited {completed.returncode}: {completed.stderr[-500:]}")
    rows = [line.strip() for line in completed.stdout.splitlines() if line.strip()]
    if len(rows) != 1:
        raise RuntimeError(f"expected exactly one GPU telemetry row, received {len(rows)}")
    fields = [field.strip() for field in rows[0].split(",")]
    if len(fields) != 3:
        raise RuntimeError(f"expected three GPU telemetry columns, received {len(fields)}")
    if any(not field.isascii() or not field.isdecimal() for field in fields):
        raise RuntimeError(f"GPU telemetry fields must be nonnegative integers: {rows[0]!r}")
    used_mib, total_mib, utilization = (int(field) for field in fields)
    if total_mib <= 0 or used_mib > total_mib or not 0 <= utilization <= 100:
        raise RuntimeError(f"GPU telemetry is outside valid ranges: {rows[0]!r}")
    return {"memory_used_mib": used_mib, "memory_total_mib": total_mib,
            "utilization_percent": utilization}


class Poller(threading.Thread):
    """Sample system-wide GPU values and surface every sampler failure to caller."""
    def __init__(self, reader: Callable[[], dict[str, int]] = smi_used,
                 interval: float = SAMPLE_INTERVAL_SECONDS):
        super().__init__(daemon=True)
        self.reader = reader
        self.interval = interval
        self.stop_event = threading.Event()
        self.samples: list[dict[str, int]] = []
        self.error: BaseException | None = None

    def run(self) -> None:
        try:
            while not self.stop_event.is_set():
                sample = self.reader()
                self.samples.append(sample)
                if self.stop_event.wait(self.interval):
                    break
        except BaseException as exc:  # recorded and re-raised in the owning thread
            self.error = exc
            self.stop_event.set()

    def stop(self) -> None:
        self.stop_event.set()
        self.join(timeout=max(2.0, GPU_QUERY_TIMEOUT_SECONDS + 1.0))
        if self.is_alive():
            raise TimeoutError("GPU telemetry poller did not stop within its bound")
        self.raise_if_invalid()

    def raise_if_invalid(self) -> None:
        if self.error is not None:
            raise RuntimeError(f"GPU telemetry poller failed: {self.error}") from self.error
        if not self.samples:
            raise RuntimeError("GPU telemetry poller produced no samples")

    def summary(self) -> dict[str, Any]:
        self.raise_if_invalid()
        return {
            "scope": "system-wide single-GPU telemetry",
            "sample_count": len(self.samples),
            "peak_memory_used_mib": max(s["memory_used_mib"] for s in self.samples),
            "peak_memory_delta_from_baseline_mib": max(s["memory_used_mib"] for s in self.samples),
            "max_utilization_percent": max(s["utilization_percent"] for s in self.samples),
            "samples": self.samples,
        }

    def partial_summary(self, error: BaseException) -> dict[str, Any]:
        """Keep only measurements actually observed when sampling fails."""
        value: dict[str, Any] = {"scope": "system-wide single-GPU telemetry",
                                 "sample_count": len(self.samples), "samples": self.samples,
                                 "error": str(error)}
        if self.samples:
            value["peak_memory_used_mib"] = max(s["memory_used_mib"] for s in self.samples)
            value["max_utilization_percent"] = max(s["utilization_percent"] for s in self.samples)
        return value


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _validate_png(path: Path, width: int, height: int) -> str:
    """Check PNG chunk framing/checksums, IHDR dimensions, and IEND before hashing."""
    if not path.is_file() or path.stat().st_size < 33:
        raise RuntimeError(f"generation output is missing or too short to be a PNG: {path}")
    data = path.read_bytes()
    if data[:8] != PNG_SIGNATURE:
        raise RuntimeError(f"generation output has an invalid PNG header: {path}")
    offset = 8
    saw_header = saw_data = saw_end = False
    actual_width = actual_height = 0
    while offset + 12 <= len(data):
        chunk_length = int.from_bytes(data[offset:offset + 4], "big")
        chunk_type = data[offset + 4:offset + 8]
        chunk_end = offset + 12 + chunk_length
        if chunk_end > len(data):
            raise RuntimeError(f"generation output has a truncated PNG chunk: {path}")
        chunk_data = data[offset + 8:offset + 8 + chunk_length]
        expected_crc = int.from_bytes(data[offset + 8 + chunk_length:chunk_end], "big")
        actual_crc = zlib.crc32(chunk_type + chunk_data) & 0xFFFFFFFF
        if expected_crc != actual_crc:
            raise RuntimeError(f"generation output has a bad PNG chunk checksum: {path}")
        if not saw_header:
            if chunk_type != b"IHDR" or chunk_length != 13:
                raise RuntimeError(f"generation output has no valid leading PNG IHDR: {path}")
            actual_width = int.from_bytes(chunk_data[:4], "big")
            actual_height = int.from_bytes(chunk_data[4:8], "big")
            saw_header = True
        elif chunk_type == b"IHDR":
            raise RuntimeError(f"generation output contains a duplicate PNG IHDR: {path}")
        if chunk_type == b"IDAT" and chunk_length > 0:
            saw_data = True
        if chunk_type == b"IEND":
            if chunk_length != 0 or not saw_data or chunk_end != len(data):
                raise RuntimeError(f"generation output has an invalid PNG IEND: {path}")
            saw_end = True
            break
        offset = chunk_end
    if not (saw_header and saw_data and saw_end):
        raise RuntimeError(f"generation output is not a complete PNG: {path}")
    if (actual_width, actual_height) != (width, height):
        raise RuntimeError(f"PNG dimensions {actual_width}x{actual_height} do not match requested {width}x{height}: {path}")
    return _sha256(path)


def _prepare_output_directory(out_dir: Path) -> None:
    if out_dir.exists():
        if not out_dir.is_dir():
            raise RuntimeError(f"output path exists and is not a directory: {out_dir}")
        if any(out_dir.iterdir()):
            raise RuntimeError(f"refusing to reuse nonempty output directory: {out_dir}")
    else:
        out_dir.mkdir(parents=True)


class OwnedProcessTree:
    """Own one launched process and its bounded child tree, without name-based kills."""
    def __init__(self, process: subprocess.Popen[str], job_handle: int | None = None):
        self.process = process
        self.job_handle = job_handle
        self.closed = False

    @classmethod
    def start(cls, args: list[str], **kwargs: Any) -> "OwnedProcessTree":
        creationflags = kwargs.pop("creationflags", 0)
        popen_args: dict[str, Any] = dict(kwargs)
        if os.name == "nt":
            # Keep the first thread suspended until it belongs to our kill-on-close job.
            creationflags |= getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0) | 0x00000004
            popen_args["creationflags"] = creationflags
        else:
            popen_args["start_new_session"] = True
        process = subprocess.Popen(args, **popen_args)
        owner = cls(process)
        try:
            if os.name == "nt":
                owner._attach_windows_job()
        except BaseException:
            try:
                if owner.job_handle is not None:
                    owner.cleanup(timeout=5)
                else:
                    process.kill()
                    process.wait(timeout=5)
            except BaseException as cleanup_error:
                raise RuntimeError("could not contain or terminate newly launched process") from cleanup_error
            raise
        return owner

    def _attach_windows_job(self) -> None:
        # A private kill-on-close Job Object bounds descendants to this exact launch.
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateJobObjectW.restype = ctypes.c_void_p
        kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p]
        kernel32.SetInformationJobObject.restype = ctypes.c_int
        kernel32.SetInformationJobObject.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32]
        kernel32.AssignProcessToJobObject.restype = ctypes.c_int
        kernel32.AssignProcessToJobObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        kernel32.CloseHandle.restype = ctypes.c_int
        kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
        job = kernel32.CreateJobObjectW(None, None)
        if not job:
            raise OSError(ctypes.get_last_error(), "CreateJobObjectW failed")

        class BasicLimitInformation(ctypes.Structure):
            _fields_ = [("PerProcessUserTimeLimit", ctypes.c_longlong),
                        ("PerJobUserTimeLimit", ctypes.c_longlong),
                        ("LimitFlags", ctypes.c_uint32), ("MinimumWorkingSetSize", ctypes.c_size_t),
                        ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", ctypes.c_uint32),
                        ("Affinity", ctypes.c_size_t), ("PriorityClass", ctypes.c_uint32),
                        ("SchedulingClass", ctypes.c_uint32)]

        class IoCounters(ctypes.Structure):
            _fields_ = [(name, ctypes.c_ulonglong) for name in
                        ("ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
                         "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]

        class ExtendedLimitInformation(ctypes.Structure):
            _fields_ = [("BasicLimitInformation", BasicLimitInformation), ("IoInfo", IoCounters),
                        ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                        ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]

        info = ExtendedLimitInformation()
        info.BasicLimitInformation.LimitFlags = 0x00002000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not kernel32.SetInformationJobObject(job, 9, ctypes.byref(info), ctypes.sizeof(info)):
            error = ctypes.get_last_error()
            kernel32.CloseHandle(job)
            raise OSError(error, "SetInformationJobObject failed")
        if not kernel32.AssignProcessToJobObject(job, ctypes.c_void_p(int(self.process._handle))):
            error = ctypes.get_last_error()
            kernel32.CloseHandle(job)
            raise OSError(error, "AssignProcessToJobObject failed")
        self.job_handle = int(job)
        ntdll = ctypes.WinDLL("ntdll")
        ntdll.NtResumeProcess.restype = ctypes.c_long
        ntdll.NtResumeProcess.argtypes = [ctypes.c_void_p]
        status = ntdll.NtResumeProcess(ctypes.c_void_p(int(self.process._handle)))
        if status < 0:
            raise OSError(f"NtResumeProcess failed with NTSTATUS 0x{status & 0xFFFFFFFF:08X}")

    def communicate(self, timeout: float) -> tuple[str | None, str | None]:
        return self.process.communicate(timeout=timeout)

    def cleanup(self, timeout: float = 10.0) -> None:
        if self.closed:
            return
        deadline = time.monotonic() + timeout
        if os.name == "nt" and self.job_handle is not None:
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.TerminateJobObject.restype = ctypes.c_int
            kernel32.TerminateJobObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
            kernel32.QueryInformationJobObject.restype = ctypes.c_int
            kernel32.QueryInformationJobObject.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p,
                                                           ctypes.c_uint32, ctypes.c_void_p]
            kernel32.CloseHandle.restype = ctypes.c_int
            kernel32.CloseHandle.argtypes = [ctypes.c_void_p]

            class BasicAccountingInformation(ctypes.Structure):
                _fields_ = [("TotalUserTime", ctypes.c_longlong), ("TotalKernelTime", ctypes.c_longlong),
                            ("ThisPeriodTotalUserTime", ctypes.c_longlong),
                            ("ThisPeriodTotalKernelTime", ctypes.c_longlong),
                            ("TotalPageFaults", ctypes.c_uint32), ("TotalProcesses", ctypes.c_uint32),
                            ("ActiveProcesses", ctypes.c_uint32), ("TotalTerminatedProcesses", ctypes.c_uint32)]

            job = ctypes.c_void_p(self.job_handle)
            try:
                kernel32.TerminateJobObject(job, 1)
                accounting = BasicAccountingInformation()
                while time.monotonic() < deadline:
                    if not kernel32.QueryInformationJobObject(job, 1, ctypes.byref(accounting),
                                                              ctypes.sizeof(accounting), None):
                        raise OSError(ctypes.get_last_error(), "QueryInformationJobObject failed")
                    if accounting.ActiveProcesses == 0:
                        break
                    time.sleep(0.05)
                else:
                    raise TimeoutError("owned Windows Job Object retained active processes after termination")
                self.process.wait(timeout=max(0.1, deadline - time.monotonic()))
                if self.process.poll() is None:
                    raise RuntimeError(f"owned process did not exit: PID {self.process.pid}")
            finally:
                kernel32.CloseHandle(job)
                self.job_handle = None
        else:
            if os.name != "nt":
                try:
                    os.killpg(self.process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                try:
                    self.process.wait(timeout=min(2.0, max(0.1, deadline - time.monotonic())))
                except subprocess.TimeoutExpired:
                    pass
                while True:
                    try:
                        os.killpg(self.process.pid, 0)
                    except ProcessLookupError:
                        break
                    if time.monotonic() >= deadline:
                        try:
                            os.killpg(self.process.pid, signal.SIGKILL)
                        except ProcessLookupError:
                            break
                        try:
                            self.process.wait(timeout=max(0.1, deadline - time.monotonic()))
                        except subprocess.TimeoutExpired:
                            pass
                        try:
                            os.killpg(self.process.pid, 0)
                        except ProcessLookupError:
                            break
                        raise TimeoutError(f"owned process group remains after cleanup: {self.process.pid}")
                    time.sleep(0.05)
            elif self.process.poll() is None:
                self.process.kill()
                try:
                    self.process.wait(timeout=max(0.1, deadline - time.monotonic()))
                except subprocess.TimeoutExpired as exc:
                    raise TimeoutError(f"owned process did not exit: PID {self.process.pid}") from exc
        self.closed = True


def run_owned_command(args: list[str], timeout: float, env: dict[str, str]) -> tuple[int, str, str]:
    tree = OwnedProcessTree.start(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  text=True, encoding="utf-8", errors="replace", env=env)
    try:
        stdout, stderr = tree.communicate(timeout=timeout)
        return int(tree.process.returncode), stdout or "", stderr or ""
    except subprocess.TimeoutExpired as exc:
        raise TimeoutError(f"command timed out after {timeout}s: {args[0]}") from exc
    finally:
        tree.cleanup()


class JsonLineReader:
    """Bounded newline JSON reads for the resident worker protocol."""
    def __init__(self, stream: Any):
        self.lines: queue.Queue[str | None] = queue.Queue()

        def read_lines() -> None:
            try:
                for line in stream:
                    self.lines.put(line)
            finally:
                self.lines.put(None)

        threading.Thread(target=read_lines, daemon=True).start()

    def read(self, timeout: float, phase: str) -> dict[str, Any]:
        try:
            line = self.lines.get(timeout=timeout)
        except queue.Empty as exc:
            raise TimeoutError(f"timed out waiting for media worker during {phase} ({timeout}s)") from exc
        if line is None:
            raise RuntimeError(f"media worker closed stdout during {phase}")
        try:
            value = json.loads(line)
        except json.JSONDecodeError as exc:
            raise RuntimeError(f"invalid media worker JSON during {phase}: {line[:300]!r}") from exc
        if not isinstance(value, dict):
            raise RuntimeError(f"media worker returned a non-object during {phase}")
        return value


def _new_poller(reader: Callable[[], dict[str, int]], baseline: dict[str, int]) -> Poller:
    poller = Poller(reader)
    poller.start()
    poller.baseline = baseline["memory_used_mib"]
    return poller


def _stop_poller(poller: Poller, record: dict[str, Any]) -> None:
    poller.stop()
    summary = poller.summary()
    summary["baseline_memory_used_mib"] = poller.baseline
    summary["peak_memory_delta_from_baseline_mib"] = max(
        0, summary["peak_memory_used_mib"] - poller.baseline)
    record["gpu_telemetry"] = summary
    if "run" in record:
        record["system_peak_mib_minus_baseline"] = summary["peak_memory_delta_from_baseline_mib"]
        record["max_gpu_util_percent"] = summary["max_utilization_percent"]
        record["system_resource_scope"] = summary["scope"]


def _validate_completion(result: dict[str, Any], cold_runs: int, warm_runs: int,
                         include_review_items: bool) -> bool:
    cold = result["cold"]
    warm = result["warm"]
    reviews = result["review_items"]
    load = result.get("resident_load")
    load_ok = (isinstance(load, dict)
               and _finite_number_in_range(load.get("process_to_ready_seconds"), RESIDENT_STARTUP_TIMEOUT_SECONDS)
               and _finite_number_in_range(load.get("worker_load_ms"), RESIDENT_STARTUP_TIMEOUT_SECONDS * 1000)
               and _valid_memory_pair(load.get("allocated_bytes"), load.get("reserved_bytes")))

    def generated_row_ok(row: Any, duration_key: str, duration_limit: float) -> bool:
        return (isinstance(row, dict) and row.get("type") == "done" and bool(row.get("sha256"))
                and row.get("request_id") is not None and row.get("response_id") == row.get("request_id")
                and _finite_number_in_range(row.get(duration_key), duration_limit)
                and _finite_number_in_range(row.get("worker_generation_ms"), WARM_GENERATION_TIMEOUT_SECONDS * 1000)
                and _valid_memory_pair(row.get("peak_allocated_bytes"), row.get("peak_reserved_bytes")))

    review_ok = (not include_review_items or
                 (len(reviews) == 6 and all(generated_row_ok(
                     row, "seconds", WARM_GENERATION_TIMEOUT_SECONDS) for row in reviews)))
    return bool(
        len(cold) == cold_runs and all(row.get("exit") == 0 and row.get("sha256") and
                                       _finite_number_in_range(row.get("wall_seconds"), COLD_GENERATION_TIMEOUT_SECONDS) and
                                       row.get("gpu_telemetry", {}).get("sample_count", 0) > 0 for row in cold)
        and len(warm) == warm_runs and all(generated_row_ok(
            row, "wall_seconds", WARM_GENERATION_TIMEOUT_SECONDS) for row in warm)
        and load_ok and result.get("resident_worker_exit_code") == 0
        and result.get("resident_system_gpu_telemetry", {}).get("sample_count", 0) > 0
        and review_ok and not result.get("error")
    )


def _finite_number_in_range(value: Any, maximum: float) -> bool:
    return (not isinstance(value, bool) and isinstance(value, (int, float))
            and 0 <= value <= maximum and math.isfinite(value))


def _valid_memory_pair(allocated: Any, reserved: Any) -> bool:
    return (isinstance(allocated, int) and not isinstance(allocated, bool) and allocated >= 0
            and isinstance(reserved, int) and not isinstance(reserved, bool) and reserved >= allocated)


def _require_resident_load_metrics(ready: dict[str, Any], process_to_ready_seconds: float) -> dict[str, Any]:
    load_ms = ready.get("loadMilliseconds")
    allocated = ready.get("allocatedBytes")
    reserved = ready.get("reservedBytes")
    if (not _finite_number_in_range(process_to_ready_seconds, RESIDENT_STARTUP_TIMEOUT_SECONDS)
            or not _finite_number_in_range(load_ms, RESIDENT_STARTUP_TIMEOUT_SECONDS * 1000)
            or not _valid_memory_pair(allocated, reserved)):
        raise RuntimeError(f"resident worker returned missing or invalid load/resource metrics: {ready}")
    return {"process_to_ready_seconds": process_to_ready_seconds, "worker_load_ms": load_ms,
            "allocated_bytes": allocated, "reserved_bytes": reserved,
            "peak_scope": "resident worker process"}


def _require_resident_generation_metrics(done: dict[str, Any], phase: str) -> dict[str, Any]:
    generation_ms = done.get("generationMilliseconds")
    peak_allocated = done.get("peakAllocatedBytes")
    peak_reserved = done.get("peakReservedBytes")
    if (not _finite_number_in_range(generation_ms, WARM_GENERATION_TIMEOUT_SECONDS * 1000)
            or not _valid_memory_pair(peak_allocated, peak_reserved)):
        raise RuntimeError(f"{phase} returned missing or invalid generation/resource metrics: {done}")
    return {"worker_generation_ms": generation_ms, "peak_allocated_bytes": peak_allocated,
            "peak_reserved_bytes": peak_reserved}


def _record_summary(result: dict[str, Any]) -> None:
    cold_values = [row["wall_seconds"] for row in result["cold"]
                   if row.get("exit") == 0 and row.get("sha256") and isinstance(row.get("wall_seconds"), (int, float))]
    warm_values = [row["wall_seconds"] for row in result["warm"]
                   if row.get("type") == "done" and row.get("sha256") and isinstance(row.get("wall_seconds"), (int, float))]
    if cold_values and warm_values:
        cold_median = statistics.median(cold_values)
        warm_median = statistics.median(warm_values)
        result["summary"] = {
            "cold_median_seconds": cold_median,
            "warm_median_seconds": warm_median,
            "warm_speedup_x": cold_median / warm_median if warm_median > 0 else None,
            "speedup_definition": "cold_median_seconds / warm_median_seconds",
            "median_method": "statistics.median over unrounded per-run wall_seconds",
        }


def _run_measurement(result: dict[str, Any], runner: Path, worker: Path, model_dir: Path,
                     out_dir: Path, width: int, height: int, steps: int, warm_runs: int,
                     cold_runs: int, include_review_items: bool,
                     gpu_reader: Callable[[], dict[str, int]],
                     command_runner: Callable[[list[str], float, dict[str, str]], tuple[int, str, str]]) -> None:
    env = dict(os.environ, HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1",
               HF_HUB_DISABLE_TELEMETRY="1", DO_NOT_TRACK="1",
               TOKENIZERS_PARALLELISM="false", PYTHONIOENCODING="utf-8")
    result["resource_measurement_scope"] = dict(RESOURCE_MEASUREMENT_SCOPE)
    baseline = gpu_reader()
    result["gpu_baseline"] = baseline
    result["cold"] = []
    result["warm"] = []
    result["review_items"] = []

    for index in range(cold_runs):
        output = out_dir / f"cold-{index}.png"
        request = out_dir / f"cold-{index}.json"
        request.write_text(json.dumps({"prompt": PROMPT, "seed": SEED, "width": width, "height": height,
                                       "steps": steps, "model_dir": str(model_dir), "output": str(output)}),
                           encoding="utf-8")
        phase_baseline = gpu_reader()
        poller = _new_poller(gpu_reader, phase_baseline)
        started = time.perf_counter()
        row: dict[str, Any] = {"run": index + 1, "output": str(output)}
        phase_error: BaseException | None = None
        try:
            exit_code, _stdout, stderr = command_runner(
                [sys.executable, str(runner), "generate", str(request)], COLD_GENERATION_TIMEOUT_SECONDS, env)
            wall = time.perf_counter() - started
            row.update(exit=exit_code, wall_seconds=wall, stderr_tail=stderr[-500:] if exit_code else "")
            if exit_code != 0:
                raise RuntimeError(f"cold generation {index + 1} exited {exit_code}: {stderr[-300:]}")
            row["sha256"] = _validate_png(output, width, height)
        except BaseException as exc:
            if row.get("exit") is None:
                row["exit"] = getattr(exc, "returncode", None) if isinstance(exc, subprocess.CalledProcessError) else 1
            row["error"] = str(exc)
            if isinstance(exc, TimeoutError):
                row["failure_kind"] = "timeout"
            row["wall_seconds"] = time.perf_counter() - started
            phase_error = exc
        try:
            _stop_poller(poller, row)
        except BaseException as exc:
            row["gpu_telemetry_error"] = str(exc)
            row["gpu_telemetry"] = poller.partial_summary(exc)
            if poller.samples:
                row["system_peak_mib_minus_baseline"] = max(
                    0, row["gpu_telemetry"]["peak_memory_used_mib"] - poller.baseline)
                row["max_gpu_util_percent"] = row["gpu_telemetry"]["max_utilization_percent"]
                row["system_resource_scope"] = row["gpu_telemetry"]["scope"]
            if phase_error is None:
                phase_error = exc
        result["cold"].append(row)
        if phase_error is not None:
            raise phase_error

    phase_baseline = gpu_reader()
    poller = _new_poller(gpu_reader, phase_baseline)
    worker_tree: OwnedProcessTree | None = None
    stderr_path = out_dir / "resident-worker.stderr.log"
    worker_stderr = stderr_path.open("x", encoding="utf-8")
    try:
        startup = time.perf_counter()
        worker_tree = OwnedProcessTree.start(
            [sys.executable, str(worker), str(model_dir)], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=worker_stderr, text=True, encoding="utf-8", env=env)
        reader = JsonLineReader(worker_tree.process.stdout)
        ready = reader.read(RESIDENT_STARTUP_TIMEOUT_SECONDS, "resident startup")
        if ready.get("type") != "ready":
            raise RuntimeError(f"resident worker did not become ready: {ready}")
        result["resident_load"] = _require_resident_load_metrics(
            ready, time.perf_counter() - startup)
        for index in range(warm_runs):
            output = out_dir / f"warm-{index}.png"
            started = time.perf_counter()
            request_id = f"w{index}"
            row: dict[str, Any] = {"run": index + 1, "output": str(output), "type": "pending",
                                   "request_id": request_id}
            result["warm"].append(row)
            request = {"apiVersion": "1", "id": request_id, "op": "generate", "prompt": PROMPT,
                       "seed": SEED, "width": width, "height": height, "steps": steps, "output": str(output)}
            try:
                worker_tree.process.stdin.write(json.dumps(request) + "\n")
                worker_tree.process.stdin.flush()
                done = reader.read(WARM_GENERATION_TIMEOUT_SECONDS, f"warm generation {index + 1}")
                row.update(type=done.get("type"), response_id=done.get("id"),
                           wall_seconds=time.perf_counter() - started, error=done.get("error"))
                if done.get("type") != "done":
                    raise RuntimeError(f"resident generation {index + 1} failed: {done}")
                if done.get("id") != request_id:
                    raise RuntimeError(f"resident generation {index + 1} response id mismatch: "
                                       f"expected {request_id!r}, got {done.get('id')!r}")
                row.update(_require_resident_generation_metrics(done, f"warm generation {index + 1}"))
                row["sha256"] = _validate_png(output, width, height)
            except BaseException as exc:
                row.update(type="error", wall_seconds=time.perf_counter() - started,
                           error=row.get("error") or str(exc))
                if isinstance(exc, TimeoutError):
                    row["failure_kind"] = "timeout"
                raise

        if include_review_items:
            review_spec = [
                ("text-open-sign", "text-render", "A red neon sign that says OPEN on a dark brick wall, photorealistic", 11),
                ("text-parux-poster", "text-render", "A clean minimalist poster with the words PARUX STUDIO in bold white letters on a navy background", 12),
                ("text-chalkboard-menu", "text-render", "A cafe chalkboard menu with the title TODAY'S SPECIALS and three handwritten items", 13),
                ("general-lake-dawn", "general", "A misty mountain lake at dawn, pine forest, soft golden light", 21),
                ("general-portrait", "general", "A studio portrait of an elderly fisherman with a weathered face and a wool hat", 22),
                ("general-kitchen", "general", "A bright modern kitchen with a bowl of lemons on a wooden table", 23),
            ]
            for rid, task, prompt, seed in review_spec:
                output = out_dir / f"review-{rid}.png"
                request = {"apiVersion": "1", "id": rid, "op": "generate", "prompt": prompt, "seed": seed,
                           "width": width, "height": height, "steps": steps, "output": str(output)}
                started = time.perf_counter()
                row = {"id": rid, "request_id": rid, "task": task, "prompt": prompt, "seed": seed,
                       "width": width, "height": height, "steps": steps, "output": str(output),
                       "type": "pending"}
                result["review_items"].append(row)
                try:
                    worker_tree.process.stdin.write(json.dumps(request) + "\n")
                    worker_tree.process.stdin.flush()
                    done = reader.read(WARM_GENERATION_TIMEOUT_SECONDS, f"review-item generation {rid}")
                    row.update(type=done.get("type"), response_id=done.get("id"),
                               seconds=time.perf_counter() - started, error=done.get("error"))
                    if done.get("type") != "done":
                        raise RuntimeError(f"review generation {rid} failed: {done}")
                    if done.get("id") != rid:
                        raise RuntimeError(f"review generation {rid} response id mismatch: "
                                           f"expected {rid!r}, got {done.get('id')!r}")
                    row.update(_require_resident_generation_metrics(done, f"review generation {rid}"))
                    row["sha256"] = _validate_png(output, width, height)
                except BaseException as exc:
                    row.update(type="error", seconds=time.perf_counter() - started,
                               error=row.get("error") or str(exc))
                    if isinstance(exc, TimeoutError):
                        row["failure_kind"] = "timeout"
                    raise
        result["review_images_included"] = include_review_items
        result["human_quality_judgment"] = "not-performed"
        worker_tree.process.stdin.write(json.dumps({"apiVersion": "1", "op": "shutdown"}) + "\n")
        worker_tree.process.stdin.flush()
        worker_exit_code = worker_tree.process.wait(timeout=RESIDENT_SHUTDOWN_TIMEOUT_SECONDS)
        result["resident_worker_exit_code"] = worker_exit_code
        if worker_exit_code != 0:
            raise RuntimeError(f"resident worker exited {worker_exit_code} after shutdown")
    except subprocess.TimeoutExpired as exc:
        raise TimeoutError(f"resident worker timed out: {exc}") from exc
    finally:
        active_failure = sys.exc_info()[0] is not None
        cleanup_error: BaseException | None = None
        if worker_tree is not None:
            try:
                worker_tree.cleanup()
            except BaseException as exc:
                cleanup_error = exc
                result["resident_process_cleanup_error"] = str(exc)
            finally:
                worker_stderr.close()
        else:
            worker_stderr.close()
        telemetry_record: dict[str, Any] = {}
        try:
            _stop_poller(poller, telemetry_record)
            result["resident_system_gpu_telemetry"] = telemetry_record["gpu_telemetry"]
            result["resident_system_peak_mib_minus_baseline"] = telemetry_record["gpu_telemetry"]["peak_memory_delta_from_baseline_mib"]
            result["resident_max_gpu_util_percent"] = telemetry_record["gpu_telemetry"]["max_utilization_percent"]
        except BaseException as telemetry_error:
            result["resident_gpu_telemetry_error"] = str(telemetry_error)
            result["resident_system_gpu_telemetry"] = poller.partial_summary(telemetry_error)
            if poller.samples:
                result["resident_system_peak_mib_minus_baseline"] = max(
                    0, result["resident_system_gpu_telemetry"]["peak_memory_used_mib"] - poller.baseline)
                result["resident_max_gpu_util_percent"] = result["resident_system_gpu_telemetry"]["max_utilization_percent"]
            if not active_failure:
                raise
        if cleanup_error is not None and not active_failure:
            raise cleanup_error
    all_hashes = {row["sha256"] for row in result["cold"] + result["warm"] if row.get("sha256")}
    all_required_outputs_hashed = (len(result["cold"]) == result.get("requested_cold_runs", 0)
                                   and all(row.get("sha256") for row in result["cold"])
                                   and len(result["warm"]) == result.get("requested_warm_runs", 0)
                                   and all(row.get("sha256") for row in result["warm"]))
    result["bytes_identical_across_cold_and_warm"] = bool(
        all_required_outputs_hashed and len(all_hashes) == 1)


def _save_result(out_dir: Path, result: dict[str, Any]) -> None:
    _record_summary(result)
    result["benchmark_complete"] = _validate_completion(
        result, result["requested_cold_runs"], result["requested_warm_runs"], result["review_images_requested"])
    destination = out_dir / "cold-warm.json"
    # Output directory was verified empty before work; exclusive create prevents later overwrite.
    with destination.open("x", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2, allow_nan=False)
        handle.write("\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runner", type=Path)
    parser.add_argument("worker", type=Path)
    parser.add_argument("model_dir", type=Path)
    parser.add_argument("out_dir", type=Path)
    parser.add_argument("width", nargs="?", type=int, default=1024)
    parser.add_argument("height", nargs="?", type=int, default=1024)
    parser.add_argument("steps", nargs="?", type=int, default=9)
    parser.add_argument("warm_runs", nargs="?", type=int, default=DEFAULT_WARM_RUNS)
    parser.add_argument("cold_runs", nargs="?", type=int, default=DEFAULT_COLD_RUNS)
    parser.add_argument("include_review_items", nargs="?", type=int, choices=(0, 1), default=1)
    args = parser.parse_args(argv)
    if min(args.width, args.height, args.steps, args.warm_runs, args.cold_runs) <= 0:
        parser.error("width, height, steps, warm_runs, and cold_runs must all be positive")
    try:
        _prepare_output_directory(args.out_dir)
    except Exception as exc:
        print(f"media benchmark refused: {exc}", file=sys.stderr)
        return 2

    result: dict[str, Any] = {
        "prompt": PROMPT, "seed": SEED, "width": args.width, "height": args.height, "steps": args.steps,
        "hardware": {"python": platform.python_version(), "os": platform.platform()},
        "requested_cold_runs": args.cold_runs, "requested_warm_runs": args.warm_runs,
        "review_images_requested": bool(args.include_review_items), "cold": [], "warm": [], "review_items": [],
        "benchmark_complete": False, "bytes_identical_across_cold_and_warm": False,
    }
    try:
        _run_measurement(result, args.runner, args.worker, args.model_dir, args.out_dir,
                         args.width, args.height, args.steps, args.warm_runs, args.cold_runs,
                         bool(args.include_review_items), smi_used, run_owned_command)
    except BaseException as exc:
        result["error"] = f"{type(exc).__name__}: {exc}"
    try:
        _save_result(args.out_dir, result)
    except BaseException as exc:
        print(f"failed to persist media measurement evidence: {exc}", file=sys.stderr)
        return 1
    print(json.dumps(result.get("summary", {}), allow_nan=False),
          "identical:", result.get("bytes_identical_across_cold_and_warm", False))
    if result.get("error"):
        print(result["error"], file=sys.stderr)
    return 0 if result["benchmark_complete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
