from services.state.editor_state_store import EditorStateStore


def test_editor_state_store_replaces_session_and_defensively_copies_state():
    store = EditorStateStore()
    instance_id = "Test@deadbeef"
    state = {"sequence": 1, "nested": {"value": "original"}}

    store.begin_session(instance_id, "old-session", "C:/Project")
    assert store.update_state("old-session", state) is True
    state["nested"]["value"] = "caller-mutated"
    assert store.get(instance_id).state["nested"]["value"] == "original"

    store.begin_session(instance_id, "new-session", "C:/Project")
    assert store.update_state("old-session", {"sequence": 99}) is False
    assert store.end_session("old-session") is None
    assert store.get(instance_id).session_id == "new-session"


def test_editor_state_store_tracks_main_thread_heartbeat_per_session():
    store = EditorStateStore()
    instance_id = "Test@heartbeat"
    store.begin_session(instance_id, "session", "C:/Project")

    assert store.touch_editor_heartbeat("session", 123456) is True
    record = store.get(instance_id)
    assert record.last_editor_heartbeat_unix_ms == 123456
    assert record.last_editor_heartbeat_received_unix_ms is not None
    assert record.last_message_received_unix_ms is not None

    assert store.end_session("session") == instance_id
    assert store.get(instance_id) is None


def test_repeated_heartbeat_does_not_advance_main_thread_freshness(monkeypatch):
    store = EditorStateStore()
    timestamps = iter([1000, 1100, 1200])
    monkeypatch.setattr(
        "services.state.editor_state_store._now_unix_ms",
        lambda: next(timestamps),
    )
    store.begin_session("Project@hash", "session", "C:/Project")

    assert store.touch_editor_heartbeat("session", 500) is True
    assert store.touch_editor_heartbeat("session", 500) is True

    record = store.get("Project@hash")
    assert record is not None
    assert record.last_editor_heartbeat_received_unix_ms == 1100
    assert record.last_message_received_unix_ms == 1200
