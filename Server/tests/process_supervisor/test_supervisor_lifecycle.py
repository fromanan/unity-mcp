from __future__ import annotations

import os
from pathlib import Path
import subprocess
import sys
import time

import pytest

from process_supervisor.state import read_state
from process_supervisor.main import classify_server_exit


def _wait_for_state(state_path, timeout: float = 10.0):
    deadline = time.monotonic() + timeout
    last_error = None
    while time.monotonic() < deadline:
        try:
            if state_path.exists():
                state = read_state(state_path)
                if state.server_pid > 0 and state.supervisor_pid > 0:
                    return state
        except Exception as exc:
            last_error = exc
        time.sleep(0.05)
    raise AssertionError(f"Supervisor state was not ready: {last_error}")


def _wait_for_process_exit(process_id: int, timeout: float = 10.0) -> None:
    import psutil

    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if not psutil.pid_exists(process_id):
            return
        time.sleep(0.05)
    raise AssertionError(f"Process {process_id} remained alive")


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


def _output_command() -> list[str]:
    if os.name == "nt":
        return [
            os.environ.get("COMSPEC", r"C:\Windows\System32\cmd.exe"),
            "/d",
            "/c",
            "(echo server-stdout)&(echo server-stderr 1>&2)",
        ]
    return [
        sys.executable,
        "-c",
        "import sys; print('server-stdout'); print('server-stderr', file=sys.stderr)",
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


def test_log_file_captures_supervisor_events_and_server_output(tmp_path):
    parent = subprocess.Popen(_sleep_command(20))
    state_path = tmp_path / "state.json"
    log_path = tmp_path / "supervisor.log"
    supervisor = subprocess.Popen(
        [
            sys.executable,
            "-m",
            "process_supervisor.main",
            "--parent-pid",
            str(parent.pid),
            "--port",
            "58995",
            "--state-file",
            str(state_path),
            "--instance-token",
            "test-token",
            "--log-file",
            str(log_path),
            "--",
            *_output_command(),
        ],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    try:
        assert supervisor.wait(timeout=10) == 0
        contents = log_path.read_text(encoding="utf-8")
        assert '"event": "server_started"' in contents
        assert '"event": "server_exited"' in contents
        assert "server-stdout" in contents
        assert "server-stderr" in contents
    finally:
        if supervisor.poll() is None:
            supervisor.kill()
        if parent.poll() is None:
            parent.kill()


@pytest.mark.skipif(os.name != "nt", reason="Windows Job Object behavior")
def test_windows_hard_kill_supervisor_terminates_server_job(tmp_path):
    import psutil

    parent = subprocess.Popen(_sleep_command(30))
    state_path = tmp_path / "state.json"
    supervisor = subprocess.Popen(
        [
            sys.executable,
            "-m",
            "process_supervisor.main",
            "--parent-pid",
            str(parent.pid),
            "--port",
            "58994",
            "--state-file",
            str(state_path),
            "--instance-token",
            "test-token",
            "--",
            *_sleep_command(60),
        ],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    server_pid = None
    try:
        state = _wait_for_state(state_path)
        server_pid = state.server_pid
        assert psutil.pid_exists(server_pid)

        supervisor.kill()
        supervisor.wait(timeout=10)
        _wait_for_process_exit(server_pid)

        persisted = read_state(state_path)
        assert persisted.exit_reason is None
    finally:
        if supervisor.poll() is None:
            supervisor.kill()
        if server_pid and psutil.pid_exists(server_pid):
            psutil.Process(server_pid).kill()
        if parent.poll() is None:
            parent.kill()


@pytest.mark.skipif(os.name != "nt", reason="Windows WMI detachment behavior")
def test_windows_wmi_detached_caller_exit_preserves_supervisor(tmp_path):
    import psutil

    parent = subprocess.Popen(_sleep_command(30))
    state_path = tmp_path / "state.json"
    log_path = tmp_path / "supervisor.log"
    pid_path = tmp_path / "supervisor.pid"
    server_root = Path(__file__).resolve().parents[2]
    supervisor_command = subprocess.list2cmdline(
        [
            sys.executable,
            "-m",
            "process_supervisor.main",
            "--parent-pid",
            str(parent.pid),
            "--port",
            "58993",
            "--state-file",
            str(state_path),
            "--instance-token",
            "test-token",
            "--log-file",
            str(log_path),
            "--",
            *_sleep_command(60),
        ]
    )
    environment = os.environ.copy()
    environment["MCP_CHAOS_COMMAND"] = supervisor_command
    environment["MCP_CHAOS_CWD"] = str(server_root)
    environment["MCP_CHAOS_PID_PATH"] = str(pid_path)
    caller_script = (
        "$startup = New-CimInstance -ClassName Win32_ProcessStartup "
        "-Namespace root/cimv2 -ClientOnly; "
        "$startup.ShowWindow = 0; "
        "$result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create "
        "-Arguments @{CommandLine=$env:MCP_CHAOS_COMMAND; "
        "CurrentDirectory=$env:MCP_CHAOS_CWD; ProcessStartupInformation=$startup}; "
        "if ($result.ReturnValue -ne 0) { exit $result.ReturnValue }; "
        "[IO.File]::WriteAllText($env:MCP_CHAOS_PID_PATH, "
        "$result.ProcessId.ToString([Globalization.CultureInfo]::InvariantCulture)); "
        "Start-Sleep -Seconds 60"
    )
    caller = subprocess.Popen(
        ["powershell.exe", "-NoLogo", "-NoProfile", "-Command", caller_script],
        env=environment,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    supervisor_pid = None
    server_pid = None
    try:
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline and not pid_path.exists():
            time.sleep(0.05)
        assert pid_path.exists()
        supervisor_pid = int(pid_path.read_text(encoding="utf-8"))
        state = _wait_for_state(state_path, timeout=15)
        server_pid = state.server_pid

        caller.kill()
        caller.wait(timeout=10)
        time.sleep(0.5)
        assert psutil.pid_exists(supervisor_pid)
        assert psutil.pid_exists(server_pid)

        psutil.Process(supervisor_pid).kill()
        _wait_for_process_exit(supervisor_pid)
        _wait_for_process_exit(server_pid)
    finally:
        if caller.poll() is None:
            caller.kill()
        for process_id in (supervisor_pid, server_pid):
            if process_id and psutil.pid_exists(process_id):
                psutil.Process(process_id).kill()
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
