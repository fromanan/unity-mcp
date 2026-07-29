from __future__ import annotations

import os
import subprocess
import sys
import time

import pytest

from process_supervisor.state import read_state
from process_supervisor.main import classify_server_exit


def _sleep_command(seconds: int) -> list[str]:
    if os.name == "nt":
        # Use native Windows processes here. Microsoft Store Python redirectors
        # can intentionally break away from Job Objects.
        return [
            os.environ.get("COMSPEC", r"C:\Windows\System32\cmd.exe"),
            "/d",
            "/c",
            f"ping 127.0.0.1 -n {seconds + 1} >nul",
        ]
    return [sys.executable, "-c", f"import time; time.sleep({seconds})"]


def _exit_command(code: int) -> list[str]:
    if os.name == "nt":
        return [
            os.environ.get("COMSPEC", r"C:\Windows\System32\cmd.exe"),
            "/d",
            "/c",
            f"exit {code}",
        ]
    return [sys.executable, "-c", f"raise SystemExit({code})"]


def test_memory_limit_exit_is_recognizable():
    assert classify_server_exit(1000, 949) == "server_exited"
    assert classify_server_exit(1000, 950) == "memory_limit_exceeded"
    assert classify_server_exit(0, 5000) == "server_exited"


def test_parent_exit_terminates_server_job(tmp_path):
    parent = subprocess.Popen(_sleep_command(1))
    state_path = tmp_path / "state.json"
    supervisor = subprocess.Popen(
        [
            sys.executable,
            "-m",
            "process_supervisor.main",
            "--parent-pid",
            str(parent.pid),
            "--port",
            "58999",
            "--state-file",
            str(state_path),
            "--instance-token",
            "test-token",
            "--soft-memory-limit-mb",
            "512",
            "--hard-memory-limit-mb",
            "0",
            "--",
            *_sleep_command(60),
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    try:
        assert supervisor.wait(timeout=10) == 0
        state = read_state(state_path)
        assert state.exit_reason == "unity_parent_exited"
        assert state.active_processes == 0
    finally:
        if supervisor.poll() is None:
            supervisor.kill()
        if parent.poll() is None:
            parent.kill()


def test_normal_server_exit_stops_supervisor(tmp_path):
    parent = subprocess.Popen(_sleep_command(10))
    state_path = tmp_path / "state.json"
    supervisor = subprocess.Popen(
        [
            sys.executable,
            "-m",
            "process_supervisor.main",
            "--parent-pid",
            str(parent.pid),
            "--port",
            "58998",
            "--state-file",
            str(state_path),
            "--instance-token",
            "test-token",
            "--",
            *_exit_command(7),
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    try:
        assert supervisor.wait(timeout=10) == 7
        state = read_state(state_path)
        assert state.exit_reason == "server_exited"
        assert state.server_exit_code == 7
    finally:
        if supervisor.poll() is None:
            supervisor.kill()
        if parent.poll() is None:
            parent.kill()


@pytest.mark.skipif(os.name == "nt", reason="POSIX RSS-limit behavior")
def test_posix_hard_limit_terminates_process_group(tmp_path):
    parent = subprocess.Popen(_sleep_command(20))
    state_path = tmp_path / "state.json"
    allocate = (
        "import time; "
        "payload = bytearray(96 * 1024 * 1024); "
        "time.sleep(30)"
    )
    supervisor = subprocess.Popen(
        [
            sys.executable,
            "-m",
            "process_supervisor.main",
            "--parent-pid",
            str(parent.pid),
            "--port",
            "58997",
            "--state-file",
            str(state_path),
            "--instance-token",
            "test-token",
            "--hard-memory-limit-mb",
            "32",
            "--",
            sys.executable,
            "-c",
            allocate,
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    try:
        assert supervisor.wait(timeout=15) == 0
        state = read_state(state_path)
        assert state.exit_reason == "memory_limit_exceeded"
        assert state.peak_job_memory_bytes >= 32 * 1024 * 1024
    finally:
        if supervisor.poll() is None:
            supervisor.kill()
        if parent.poll() is None:
            parent.kill()


@pytest.mark.skipif(os.name == "nt", reason="POSIX signal behavior")
def test_posix_supervisor_signal_terminates_server_group(tmp_path):
    import psutil

    parent = subprocess.Popen(_sleep_command(20))
    state_path = tmp_path / "state.json"
    supervisor = subprocess.Popen(
        [
            sys.executable,
            "-m",
            "process_supervisor.main",
            "--parent-pid",
            str(parent.pid),
            "--port",
            "58996",
            "--state-file",
            str(state_path),
            "--instance-token",
            "test-token",
            "--",
            *_sleep_command(30),
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    server_pid = None
    try:
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if state_path.exists():
                server_pid = read_state(state_path).server_pid
                break
            time.sleep(0.05)
        assert server_pid is not None

        supervisor.terminate()
        assert supervisor.wait(timeout=12) == 0
        state = read_state(state_path)
        assert state.exit_reason == "supervisor_terminated"
        assert not psutil.pid_exists(server_pid)
    finally:
        if supervisor.poll() is None:
            supervisor.kill()
        if parent.poll() is None:
            parent.kill()
