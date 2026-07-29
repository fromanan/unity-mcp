from __future__ import annotations

import json
import os
import tempfile
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any


@dataclass
class LaunchState:
    schema_version: int
    supervisor_pid: int
    server_pid: int
    unity_pid: int
    port: int
    instance_token: str
    job_name: str
    soft_memory_limit_bytes: int
    hard_memory_limit_bytes: int
    runtime_version: str = "unknown"
    launched_at_unix: float = 0
    active_processes: int = 1
    current_private_bytes: int = 0
    peak_job_memory_bytes: int = 0
    server_exit_code: int | None = None
    exit_reason: str | None = None

    def public_dict(self) -> dict[str, Any]:
        result = asdict(self)
        result["instance_token"] = ""
        result["has_instance_token"] = bool(self.instance_token)
        return result


def write_state(path: str | os.PathLike[str], state: LaunchState) -> None:
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(
        prefix=destination.name + ".",
        suffix=".tmp",
        dir=destination.parent,
    )
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(asdict(state), stream, indent=2, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temp_name, destination)
    except BaseException:
        try:
            os.unlink(temp_name)
        except OSError:
            pass
        raise


def read_state(
    path: str | os.PathLike[str],
    *,
    expected_unity_pid: int | None = None,
    expected_instance_token: str | None = None,
) -> LaunchState:
    with open(path, encoding="utf-8") as stream:
        payload = json.load(stream)
    if payload.get("schema_version") != 1:
        raise ValueError("Unsupported launch-state schema")
    state = LaunchState(**payload)
    if expected_unity_pid is not None and state.unity_pid != expected_unity_pid:
        raise ValueError("Launch state belongs to a different Unity process")
    if (
        expected_instance_token is not None
        and state.instance_token != expected_instance_token
    ):
        raise ValueError("Launch state instance token does not match")
    return state
