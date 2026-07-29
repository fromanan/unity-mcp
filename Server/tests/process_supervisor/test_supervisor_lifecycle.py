from __future__ import annotations

import os
import subprocess
import sys

import pytest

from process_supervisor.state import read_state
from process_supervisor.main import classify_server_exit


pytestmark = pytest.mark.skipif(os.name != "nt", reason="Windows supervisor test")


def _sleep_command(seconds: int) -> list[str]:
    # Use native Windows processes here. Microsoft Store Python redirectors can
    # intentionally break away from Job Objects, which would make a lifecycle
    # test leave its real interpreter sleeping after the launcher is killed.
    return [
        os.environ.get("COMSPEC", r"C:\Windows\System32\cmd.exe"),
        "/d",
        "/c",
        f"ping 127.0.0.1 -n {seconds + 1} >nul",
    ]


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
            os.environ.get("COMSPEC", r"C:\Windows\System32\cmd.exe"),
            "/d",
            "/c",
            "exit 7",
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
