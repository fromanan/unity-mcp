from __future__ import annotations

import json

import pytest

from process_supervisor.state import LaunchState, read_state, write_state


def _state() -> LaunchState:
    return LaunchState(
        schema_version=1,
        supervisor_pid=100,
        server_pid=101,
        unity_pid=99,
        port=8080,
        instance_token="secret",
        job_name="Local\\test",
        soft_memory_limit_bytes=512,
        hard_memory_limit_bytes=0,
    )


def test_launch_state_round_trip_is_atomic(tmp_path):
    path = tmp_path / "launch.json"
    write_state(path, _state())
    assert read_state(path) == _state()
    assert list(tmp_path.glob("*.tmp")) == []


def test_read_state_rejects_stale_launch_identity(tmp_path):
    path = tmp_path / "launch.json"
    state = _state()
    write_state(path, state)

    with pytest.raises(ValueError, match="different Unity"):
        read_state(path, expected_unity_pid=state.unity_pid + 1)
    with pytest.raises(ValueError, match="token"):
        read_state(path, expected_instance_token="wrong-token")


def test_public_state_redacts_instance_token():
    payload = _state().public_dict()
    assert payload["instance_token"] == ""
    assert payload["has_instance_token"] is True


def test_rejects_unknown_schema(tmp_path):
    path = tmp_path / "launch.json"
    path.write_text(json.dumps({"schema_version": 2}), encoding="utf-8")
    with pytest.raises(ValueError, match="schema"):
        read_state(path)
