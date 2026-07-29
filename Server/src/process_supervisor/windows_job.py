from __future__ import annotations

import ctypes
import os
import subprocess
from ctypes import wintypes
from dataclasses import dataclass
from typing import Sequence

if os.name != "nt":  # pragma: no cover - imported only by the Windows supervisor
    raise RuntimeError("Windows Job Objects are only available on Windows")

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)

CREATE_SUSPENDED = 0x00000004
CREATE_NO_WINDOW = 0x08000000
CREATE_UNICODE_ENVIRONMENT = 0x00000400
STARTF_USESTDHANDLES = 0x00000100
JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200
JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000
JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS = 9
JOB_OBJECT_BASIC_PROCESS_ID_LIST_CLASS = 3
SYNCHRONIZE = 0x00100000
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
WAIT_OBJECT_0 = 0
WAIT_TIMEOUT = 258
INFINITE = 0xFFFFFFFF
STD_INPUT_HANDLE = -10
STD_OUTPUT_HANDLE = -11
STD_ERROR_HANDLE = -12


class IO_COUNTERS(ctypes.Structure):
    _fields_ = [
        ("ReadOperationCount", ctypes.c_ulonglong),
        ("WriteOperationCount", ctypes.c_ulonglong),
        ("OtherOperationCount", ctypes.c_ulonglong),
        ("ReadTransferCount", ctypes.c_ulonglong),
        ("WriteTransferCount", ctypes.c_ulonglong),
        ("OtherTransferCount", ctypes.c_ulonglong),
    ]


class JOBOBJECT_BASIC_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("PerProcessUserTimeLimit", ctypes.c_longlong),
        ("PerJobUserTimeLimit", ctypes.c_longlong),
        ("LimitFlags", wintypes.DWORD),
        ("MinimumWorkingSetSize", ctypes.c_size_t),
        ("MaximumWorkingSetSize", ctypes.c_size_t),
        ("ActiveProcessLimit", wintypes.DWORD),
        ("Affinity", ctypes.c_size_t),
        ("PriorityClass", wintypes.DWORD),
        ("SchedulingClass", wintypes.DWORD),
    ]


class JOBOBJECT_EXTENDED_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("BasicLimitInformation", JOBOBJECT_BASIC_LIMIT_INFORMATION),
        ("IoInfo", IO_COUNTERS),
        ("ProcessMemoryLimit", ctypes.c_size_t),
        ("JobMemoryLimit", ctypes.c_size_t),
        ("PeakProcessMemoryUsed", ctypes.c_size_t),
        ("PeakJobMemoryUsed", ctypes.c_size_t),
    ]


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("lpReserved", wintypes.LPWSTR),
        ("lpDesktop", wintypes.LPWSTR),
        ("lpTitle", wintypes.LPWSTR),
        ("dwX", wintypes.DWORD),
        ("dwY", wintypes.DWORD),
        ("dwXSize", wintypes.DWORD),
        ("dwYSize", wintypes.DWORD),
        ("dwXCountChars", wintypes.DWORD),
        ("dwYCountChars", wintypes.DWORD),
        ("dwFillAttribute", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD),
        ("wShowWindow", wintypes.WORD),
        ("cbReserved2", wintypes.WORD),
        ("lpReserved2", ctypes.POINTER(ctypes.c_byte)),
        ("hStdInput", wintypes.HANDLE),
        ("hStdOutput", wintypes.HANDLE),
        ("hStdError", wintypes.HANDLE),
    ]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("hProcess", wintypes.HANDLE),
        ("hThread", wintypes.HANDLE),
        ("dwProcessId", wintypes.DWORD),
        ("dwThreadId", wintypes.DWORD),
    ]


class PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("PageFaultCount", wintypes.DWORD),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
        ("PrivateUsage", ctypes.c_size_t),
    ]


kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
kernel32.CreateJobObjectW.restype = wintypes.HANDLE
kernel32.SetInformationJobObject.argtypes = [
    wintypes.HANDLE,
    ctypes.c_int,
    ctypes.c_void_p,
    wintypes.DWORD,
]
kernel32.SetInformationJobObject.restype = wintypes.BOOL
kernel32.QueryInformationJobObject.argtypes = [
    wintypes.HANDLE,
    ctypes.c_int,
    ctypes.c_void_p,
    wintypes.DWORD,
    ctypes.POINTER(wintypes.DWORD),
]
kernel32.QueryInformationJobObject.restype = wintypes.BOOL
kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
kernel32.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
kernel32.TerminateJobObject.restype = wintypes.BOOL
kernel32.CreateProcessW.argtypes = [
    wintypes.LPCWSTR,
    wintypes.LPWSTR,
    ctypes.c_void_p,
    ctypes.c_void_p,
    wintypes.BOOL,
    wintypes.DWORD,
    ctypes.c_void_p,
    wintypes.LPCWSTR,
    ctypes.POINTER(STARTUPINFOW),
    ctypes.POINTER(PROCESS_INFORMATION),
]
kernel32.CreateProcessW.restype = wintypes.BOOL
kernel32.ResumeThread.argtypes = [wintypes.HANDLE]
kernel32.ResumeThread.restype = wintypes.DWORD
kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
kernel32.WaitForSingleObject.restype = wintypes.DWORD
kernel32.GetExitCodeProcess.argtypes = [
    wintypes.HANDLE,
    ctypes.POINTER(wintypes.DWORD),
]
kernel32.GetExitCodeProcess.restype = wintypes.BOOL
kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CloseHandle.restype = wintypes.BOOL
kernel32.GetStdHandle.argtypes = [ctypes.c_int]
kernel32.GetStdHandle.restype = wintypes.HANDLE
kernel32.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
kernel32.TerminateProcess.restype = wintypes.BOOL
psapi.GetProcessMemoryInfo.argtypes = [
    wintypes.HANDLE,
    ctypes.POINTER(PROCESS_MEMORY_COUNTERS_EX),
    wintypes.DWORD,
]
psapi.GetProcessMemoryInfo.restype = wintypes.BOOL


@dataclass
class JobAccounting:
    active_processes: int
    current_private_bytes: int
    peak_job_memory_bytes: int


def _check_handle(handle, operation: str):
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error(), operation)
    return handle


def _check_bool(result, operation: str) -> None:
    if not result:
        raise ctypes.WinError(ctypes.get_last_error(), operation)


class WindowsJob:
    def __init__(self, name: str, hard_memory_limit_bytes: int = 0) -> None:
        self.name = name
        self.handle = _check_handle(
            kernel32.CreateJobObjectW(None, name), "CreateJobObjectW"
        )
        self.process_handle = None
        self.process_id = 0
        limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if hard_memory_limit_bytes > 0:
            limits.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_JOB_MEMORY
            limits.JobMemoryLimit = hard_memory_limit_bytes
        _check_bool(
            kernel32.SetInformationJobObject(
                self.handle,
                JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS,
                ctypes.byref(limits),
                ctypes.sizeof(limits),
            ),
            "SetInformationJobObject",
        )

    def launch_suspended(
        self, command: Sequence[str], *, cwd: str | None = None
    ) -> int:
        if not command:
            raise ValueError("command must not be empty")
        command_line = ctypes.create_unicode_buffer(subprocess.list2cmdline(command))
        startup = STARTUPINFOW()
        startup.cb = ctypes.sizeof(startup)
        startup.dwFlags = STARTF_USESTDHANDLES
        startup.hStdInput = kernel32.GetStdHandle(STD_INPUT_HANDLE)
        startup.hStdOutput = kernel32.GetStdHandle(STD_OUTPUT_HANDLE)
        startup.hStdError = kernel32.GetStdHandle(STD_ERROR_HANDLE)
        process = PROCESS_INFORMATION()
        flags = CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT
        _check_bool(
            kernel32.CreateProcessW(
                None,
                command_line,
                None,
                None,
                True,
                flags,
                None,
                cwd,
                ctypes.byref(startup),
                ctypes.byref(process),
            ),
            "CreateProcessW",
        )
        try:
            _check_bool(
                kernel32.AssignProcessToJobObject(self.handle, process.hProcess),
                "AssignProcessToJobObject",
            )
            if kernel32.ResumeThread(process.hThread) == 0xFFFFFFFF:
                raise ctypes.WinError(ctypes.get_last_error(), "ResumeThread")
        except BaseException:
            kernel32.TerminateProcess(process.hProcess, 1)
            kernel32.CloseHandle(process.hThread)
            kernel32.CloseHandle(process.hProcess)
            raise
        kernel32.CloseHandle(process.hThread)
        self.process_handle = process.hProcess
        self.process_id = int(process.dwProcessId)
        return self.process_id

    def wait(self, timeout_ms: int) -> bool:
        if not self.process_handle:
            return True
        result = kernel32.WaitForSingleObject(self.process_handle, timeout_ms)
        if result == WAIT_OBJECT_0:
            return True
        if result == WAIT_TIMEOUT:
            return False
        raise ctypes.WinError(ctypes.get_last_error(), "WaitForSingleObject")

    def exit_code(self) -> int | None:
        if not self.process_handle:
            return None
        code = wintypes.DWORD()
        _check_bool(
            kernel32.GetExitCodeProcess(self.process_handle, ctypes.byref(code)),
            "GetExitCodeProcess",
        )
        return int(code.value)

    def terminate(self, exit_code: int = 1) -> None:
        if self.handle:
            _check_bool(
                kernel32.TerminateJobObject(self.handle, exit_code),
                "TerminateJobObject",
            )

    def accounting(self) -> JobAccounting:
        limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
        _check_bool(
            kernel32.QueryInformationJobObject(
                self.handle,
                JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS,
                ctypes.byref(limits),
                ctypes.sizeof(limits),
                None,
            ),
            "QueryInformationJobObject",
        )
        process_ids = self._process_ids()
        return JobAccounting(
            active_processes=len(process_ids),
            current_private_bytes=sum(
                self._private_bytes(pid) for pid in process_ids
            ),
            peak_job_memory_bytes=int(limits.PeakJobMemoryUsed),
        )

    def _process_ids(self) -> list[int]:
        capacity = 64
        element = ctypes.sizeof(ctypes.c_size_t)
        buffer = ctypes.create_string_buffer(8 + capacity * element)
        _check_bool(
            kernel32.QueryInformationJobObject(
                self.handle,
                JOB_OBJECT_BASIC_PROCESS_ID_LIST_CLASS,
                buffer,
                ctypes.sizeof(buffer),
                None,
            ),
            "QueryInformationJobObject(ProcessIdList)",
        )
        assigned = ctypes.c_uint32.from_buffer(buffer, 0).value
        listed = ctypes.c_uint32.from_buffer(buffer, 4).value
        count = min(assigned, listed, capacity)
        array_type = ctypes.c_size_t * count
        values = array_type.from_buffer(buffer, 8)
        return [int(value) for value in values]

    @staticmethod
    def _private_bytes(pid: int) -> int:
        handle = kernel32.OpenProcess(
            PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid
        )
        if not handle:
            return 0
        try:
            counters = PROCESS_MEMORY_COUNTERS_EX()
            counters.cb = ctypes.sizeof(counters)
            if not psapi.GetProcessMemoryInfo(
                handle, ctypes.byref(counters), counters.cb
            ):
                return 0
            return int(counters.PrivateUsage)
        finally:
            kernel32.CloseHandle(handle)

    def close(self) -> None:
        if self.process_handle:
            kernel32.CloseHandle(self.process_handle)
            self.process_handle = None
        if self.handle:
            kernel32.CloseHandle(self.handle)
            self.handle = None

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, tb):
        self.close()


def open_process_for_wait(pid: int):
    return _check_handle(
        kernel32.OpenProcess(
            SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, False, pid
        ),
        "OpenProcess",
    )


def process_has_exited(handle) -> bool:
    result = kernel32.WaitForSingleObject(handle, 0)
    if result == WAIT_OBJECT_0:
        return True
    if result == WAIT_TIMEOUT:
        return False
    raise ctypes.WinError(ctypes.get_last_error(), "WaitForSingleObject")


def close_handle(handle) -> None:
    if handle:
        kernel32.CloseHandle(handle)
