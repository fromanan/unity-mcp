from __future__ import annotations

import copy
import threading
import time
from dataclasses import dataclass
from typing import Any


def _now_unix_ms() -> int:
    return int(time.time() * 1000)


@dataclass
class EditorStateRecord:
    instance_id: str
    session_id: str
    project_root: str | None
    state: dict[str, Any] | None = None
    last_state_received_unix_ms: int | None = None
    last_editor_heartbeat_unix_ms: int | None = None
    last_editor_heartbeat_received_unix_ms: int | None = None
    last_message_received_unix_ms: int | None = None


class EditorStateStore:
    """Thread-safe, per-instance state received proactively from Unity plugins."""

    def __init__(self) -> None:
        self._lock = threading.RLock()
        self._records: dict[str, EditorStateRecord] = {}
        self._instance_by_session: dict[str, str] = {}

    def begin_session(
        self,
        instance_id: str,
        session_id: str,
        project_root: str | None,
    ) -> None:
        now = _now_unix_ms()
        with self._lock:
            previous = self._records.get(instance_id)
            if previous is not None:
                self._instance_by_session.pop(previous.session_id, None)

            self._records[instance_id] = EditorStateRecord(
                instance_id=instance_id,
                session_id=session_id,
                project_root=project_root,
                last_message_received_unix_ms=now,
            )
            self._instance_by_session[session_id] = instance_id

    def end_session(self, session_id: str) -> str | None:
        with self._lock:
            instance_id = self._instance_by_session.pop(session_id, None)
            if instance_id is None:
                return None

            record = self._records.get(instance_id)
            if record is not None and record.session_id == session_id:
                self._records.pop(instance_id, None)
                return instance_id
            return None

    def update_state(self, session_id: str, state: dict[str, Any]) -> bool:
        now = _now_unix_ms()
        with self._lock:
            record = self._record_for_session(session_id)
            if record is None:
                return False

            record.state = copy.deepcopy(state)
            record.last_state_received_unix_ms = now
            record.last_message_received_unix_ms = now
            return True

    def touch_editor_heartbeat(
        self,
        session_id: str,
        editor_heartbeat_unix_ms: int | None,
    ) -> bool:
        now = _now_unix_ms()
        with self._lock:
            record = self._record_for_session(session_id)
            if record is None:
                return False

            if editor_heartbeat_unix_ms is not None and editor_heartbeat_unix_ms > 0:
                heartbeat_unix_ms = int(editor_heartbeat_unix_ms)
                if heartbeat_unix_ms != record.last_editor_heartbeat_unix_ms:
                    record.last_editor_heartbeat_unix_ms = heartbeat_unix_ms
                    record.last_editor_heartbeat_received_unix_ms = now
            record.last_message_received_unix_ms = now
            return True

    def get(self, instance_id: str) -> EditorStateRecord | None:
        with self._lock:
            record = self._records.get(instance_id)
            return copy.deepcopy(record) if record is not None else None

    def get_project_root(self, instance_id: str) -> str | None:
        with self._lock:
            record = self._records.get(instance_id)
            return record.project_root if record is not None else None

    def clear(self) -> None:
        with self._lock:
            self._records.clear()
            self._instance_by_session.clear()

    def _record_for_session(self, session_id: str) -> EditorStateRecord | None:
        instance_id = self._instance_by_session.get(session_id)
        if instance_id is None:
            return None
        record = self._records.get(instance_id)
        if record is None or record.session_id != session_id:
            return None
        return record


editor_state_store = EditorStateStore()
