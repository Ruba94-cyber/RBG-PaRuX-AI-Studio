"""Shared fail-closed output and owned-subprocess helpers for BIC13 tools."""
from __future__ import annotations

import ctypes
import json
import os
import queue
import subprocess
import threading
import time
from pathlib import Path
from typing import Any, TextIO


def ensure_fresh_directory(path: str | Path) -> Path:
    """Create an output directory, or accept only an existing empty directory."""
    target = Path(path)
    # An empty symlink target is not a fresh destination: following it could place
    # generated evidence outside the path the caller selected.
    if target.is_symlink() or (hasattr(target, "is_junction") and target.is_junction()):
        raise FileExistsError(f"refusing linked output directory: {target}")
    if target.exists() and not target.is_dir():
        raise FileExistsError(f"output path is not a directory: {target}")
    target.mkdir(parents=True, exist_ok=True)
    # Recheck after creation to catch a destination swapped for a link meanwhile.
    if target.is_symlink() or (hasattr(target, "is_junction") and target.is_junction()):
        raise FileExistsError(f"refusing linked output directory: {target}")
    if any(target.iterdir()):
        raise FileExistsError(f"refusing to reuse nonempty output directory: {target}")
    return target


def reserve_output(path: str | Path) -> TextIO:
    """Exclusively create and retain a destination handle; never truncate prior evidence."""
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(target, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    return os.fdopen(fd, "w", encoding="utf-8", newline="\n")


def save_reserved_json(handle: TextIO, value: Any) -> None:
    handle.seek(0)
    handle.truncate()
    json.dump(value, handle, indent=2, allow_nan=False)
    handle.write("\n")
    handle.flush()
    os.fsync(handle.fileno())


def read_before_deadline(reader: "BoundedLineReader", deadline: float, what: str) -> str:
    """Read one line only while the caller's absolute monotonic deadline remains."""
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise TimeoutError(f"deadline expired waiting for {what}")
    return reader.read(remaining, what)


class OwnedProcessTree:
    """Bound and clean up only the process tree launched through this instance."""
    def __init__(self, process: subprocess.Popen[str], job_handle: int | None = None):
        self.process = process
        self.job_handle = job_handle
        self.closed = False

    @classmethod
    def start(cls, args: list[str], **kwargs: Any) -> "OwnedProcessTree":
        if os.name != "nt":
            raise NotImplementedError("owned process trees require Windows Job Objects on this platform")
        flags = kwargs.pop("creationflags", 0)
        kwargs["creationflags"] = flags | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0) | 0x00000004
        proc = subprocess.Popen(args, **kwargs)
        owner = cls(proc)
        try:
            if os.name == "nt":
                owner._attach_windows_job()
        except BaseException:
            if owner.job_handle is not None:
                owner.cleanup(timeout=10)
            else:
                proc.kill()
                proc.wait(timeout=10)
            raise
        return owner

    def _attach_windows_job(self) -> None:
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateJobObjectW.restype = ctypes.c_void_p
        kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p]
        kernel.SetInformationJobObject.restype = ctypes.c_int
        kernel.SetInformationJobObject.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32]
        kernel.AssignProcessToJobObject.restype = ctypes.c_int
        kernel.AssignProcessToJobObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        kernel.CloseHandle.restype = ctypes.c_int
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]

        class BasicLimit(ctypes.Structure):
            _fields_ = [("PerProcessUserTimeLimit", ctypes.c_longlong), ("PerJobUserTimeLimit", ctypes.c_longlong),
                        ("LimitFlags", ctypes.c_uint32), ("MinimumWorkingSetSize", ctypes.c_size_t),
                        ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", ctypes.c_uint32),
                        ("Affinity", ctypes.c_size_t), ("PriorityClass", ctypes.c_uint32),
                        ("SchedulingClass", ctypes.c_uint32)]

        class IoCounters(ctypes.Structure):
            _fields_ = [(name, ctypes.c_ulonglong) for name in
                        ("ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
                         "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]

        class ExtendedLimit(ctypes.Structure):
            _fields_ = [("BasicLimitInformation", BasicLimit), ("IoInfo", IoCounters),
                        ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                        ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]

        job = kernel.CreateJobObjectW(None, None)
        if not job:
            raise OSError(ctypes.get_last_error(), "CreateJobObjectW failed")
        info = ExtendedLimit()
        info.BasicLimitInformation.LimitFlags = 0x00002000  # kill all members when the private job closes
        if not kernel.SetInformationJobObject(job, 9, ctypes.byref(info), ctypes.sizeof(info)):
            error = ctypes.get_last_error(); kernel.CloseHandle(job)
            raise OSError(error, "SetInformationJobObject failed")
        if not kernel.AssignProcessToJobObject(job, ctypes.c_void_p(int(self.process._handle))):
            error = ctypes.get_last_error(); kernel.CloseHandle(job)
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
        if os.name != "nt":
            raise NotImplementedError("owned process cleanup requires Windows Job Objects on this platform")
        deadline = time.monotonic() + timeout
        if self.job_handle is not None:
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.TerminateJobObject.restype = ctypes.c_int
            kernel.TerminateJobObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
            kernel.QueryInformationJobObject.restype = ctypes.c_int
            kernel.QueryInformationJobObject.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p,
                                                         ctypes.c_uint32, ctypes.c_void_p]
            kernel.CloseHandle.restype = ctypes.c_int
            kernel.CloseHandle.argtypes = [ctypes.c_void_p]

            class Accounting(ctypes.Structure):
                _fields_ = [("TotalUserTime", ctypes.c_longlong), ("TotalKernelTime", ctypes.c_longlong),
                            ("ThisPeriodTotalUserTime", ctypes.c_longlong),
                            ("ThisPeriodTotalKernelTime", ctypes.c_longlong), ("TotalPageFaults", ctypes.c_uint32),
                            ("TotalProcesses", ctypes.c_uint32), ("ActiveProcesses", ctypes.c_uint32),
                            ("TotalTerminatedProcesses", ctypes.c_uint32)]
            job = ctypes.c_void_p(self.job_handle)
            try:
                if not kernel.TerminateJobObject(job, 1) and self.process.poll() is None:
                    raise OSError(ctypes.get_last_error(), "TerminateJobObject failed")
                account = Accounting()
                while time.monotonic() < deadline:
                    if not kernel.QueryInformationJobObject(job, 1, ctypes.byref(account), ctypes.sizeof(account), None):
                        raise OSError(ctypes.get_last_error(), "QueryInformationJobObject failed")
                    if account.ActiveProcesses == 0:
                        break
                    time.sleep(0.05)
                else:
                    raise TimeoutError("owned job retained active processes after termination")
                self.process.wait(timeout=max(0.1, deadline - time.monotonic()))
            finally:
                kernel.CloseHandle(job)
                self.job_handle = None
        elif self.process.poll() is None:
            self.process.kill()
            self.process.wait(timeout=max(0.1, deadline - time.monotonic()))
        if self.process.poll() is None:
            raise RuntimeError(f"owned process did not exit: PID {self.process.pid}")
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            if stream is not None and not stream.closed:
                stream.close()
        self.closed = True


class BoundedLineReader:
    """Read child text lines on a daemon thread and enforce caller-side deadlines."""
    _EOF = object()

    def __init__(self, stream: Any):
        self._queue: queue.Queue[Any] = queue.Queue()
        self._stream = stream
        self._thread = threading.Thread(target=self._read, daemon=True)
        self._thread.start()

    def _read(self) -> None:
        try:
            for line in self._stream:
                self._queue.put(line)
        except BaseException as exc:
            self._queue.put(exc)
        finally:
            self._queue.put(self._EOF)

    def read(self, timeout: float, what: str) -> str:
        try:
            value = self._queue.get(timeout=timeout)
        except queue.Empty as exc:
            raise TimeoutError(f"timed out waiting {timeout:g}s for {what}") from exc
        if value is self._EOF:
            raise EOFError(f"worker closed stdout while waiting for {what}")
        if isinstance(value, BaseException):
            raise RuntimeError(f"worker stdout read failed while waiting for {what}: {value}") from value
        return value

    def has_buffered_data(self) -> bool:
        return not self._queue.empty()
