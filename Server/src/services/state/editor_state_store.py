from __future__ import annotations

import asyncio
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
        self._change_events: dict[str, asyncio.Event] = {}

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
            previous_event = self._change_events.get(instance_id)
            if previous_event is not None:
                previous_event.set()
            self._change_events[instance_id] = asyncio.Event()

    def end_session(self, session_id: str) -> str | None:
        with self._lock:
            instance_id = self._instance_by_session.pop(session_id, None)
            if instance_id is None:
                return None

            record = self._records.get(instance_id)
            if record is not None and record.session_id == session_id:
                self._records.pop(instance_id, None)
                event = self._change_events.pop(instance_id, None)
                if event is not None:
                    event.set()
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
            event = self._change_events.get(record.instance_id)
            if event is not None:
                event.set()
            self._change_events[record.instance_id] = asyncio.Event()
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

    def get_state_received_timestamp(self, instance_id: str) -> int | None:
        with self._lock:
            record = self._records.get(instance_id)
            return record.last_state_received_unix_ms if record is not None else None

    def clear(self) -> None:
        with self._lock:
            for event in self._change_events.values():
                event.set()
            self._records.clear()
            self._instance_by_session.clear()
            self._change_events.clear()

    async def wait_for_state_change(
        self,
        instance_id: str,
        since_unix_ms: int | None,
        timeout: float,
    ) -> bool:
        """Wait without polling until a newer proactive state snapshot arrives."""
        with self._lock:
            record = self._records.get(instance_id)
            if record is None:
                return False
            if (
                record.last_state_received_unix_ms is not None
                and record.last_state_received_unix_ms != since_unix_ms
            ):
                return True
            event = self._change_events.setdefault(instance_id, asyncio.Event())
        try:
            await asyncio.wait_for(event.wait(), timeout=max(0.0, timeout))
        except asyncio.TimeoutError:
            return False
        with self._lock:
            record = self._records.get(instance_id)
            return bool(
                record is not None
                and record.last_state_received_unix_ms is not None
                and record.last_state_received_unix_ms != since_unix_ms
            )

    def _record_for_session(self, session_id: str) -> EditorStateRecord | None:
        instance_id = self._instance_by_session.get(session_id)
        if instance_id is None:
            return None
        record = self._records.get(instance_id)
        if record is None or record.session_id != session_id:
            return None
        return record


editor_state_store = EditorStateStore()
