from __future__ import annotations

import os
import sys
import time
import uuid

import pytest


pytestmark = pytest.mark.skipif(os.name != "nt", reason="Windows Job Object test")


def test_job_contains_and_terminates_child(tmp_path):
    from process_supervisor.windows_job import WindowsJob

    marker = tmp_path / "started.txt"
    command = [
        sys.executable,
        "-c",
        (
            "from pathlib import Path; import time; "
            f"Path({str(marker)!r}).write_text('started'); time.sleep(60)"
        ),
    ]
    with WindowsJob(f"Local\\MCPForUnity-Test-{uuid.uuid4().hex}") as job:
        job.launch_suspended(command)
        deadline = time.time() + 5
        while not marker.exists() and time.time() < deadline:
            time.sleep(0.05)
        assert marker.exists()
        # Microsoft Store Python may add a launcher child, which also proves
        # descendant processes inherit the job.
        assert job.accounting().active_processes >= 1
        job.terminate()
        assert job.wait(5000)
        assert job.exit_code() != 259


def test_job_hard_memory_limit_is_optional():
    from process_supervisor.windows_job import WindowsJob

    with WindowsJob(f"Local\\MCPForUnity-Test-{uuid.uuid4().hex}") as job:
        assert job.accounting().active_processes == 0
