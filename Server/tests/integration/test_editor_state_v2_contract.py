import time

import pytest

from services.registry import get_registered_resources

from .test_helpers import DummyContext


@pytest.mark.asyncio
async def test_editor_state_v2_is_registered_and_has_contract_fields(monkeypatch):
    """
    Canonical editor state resource should be `mcpforunity://editor/state` and conform to v2 contract fields.
    """
    # Import module to ensure it registers its decorator without disturbing global registry state.
    import services.resources.editor_state  # noqa: F401

    resources = get_registered_resources()

    state_res = next(
        (r for r in resources if r.get("uri") == "mcpforunity://editor/state"),
        None,
    )
    assert state_res is not None, (
        "Expected canonical editor state resource `mcpforunity://editor/state` to be registered. "
        "This is required so clients can poll readiness/staleness and avoid tool loops."
    )

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        # Minimal stub payload for v2 resource tests. The server layer should enrich with staleness/advice.
        assert command_type == "get_editor_state"
        return {
            "success": True,
            "data": {
                "schema_version": "unity-mcp/editor_state@2",
                "observed_at_unix_ms": 1730000000000,
                "sequence": 1,
                "compilation": {"is_compiling": False, "is_domain_reload_pending": False},
                "tests": {"is_running": False},
            },
        }

    # Patch transport so the resource can be invoked without Unity running.
    import transport.unity_transport as unity_transport
    monkeypatch.setattr(unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    result = await state_res["func"](DummyContext())
    payload = result.model_dump() if hasattr(result, "model_dump") else result
    assert isinstance(payload, dict)

    # Contract assertions (top-level)
    assert payload.get("success") is True
    data = payload.get("data")
    assert isinstance(data, dict)
    assert data.get("schema_version") == "unity-mcp/editor_state@2"
    assert "observed_at_unix_ms" in data
    assert "sequence" in data
    assert "advice" in data
    assert "staleness" in data
    assert "served_at_unix_ms" in data
    assert data["staleness"]["basis"] == "direct_unity_response"


@pytest.mark.asyncio
async def test_editor_state_uses_proactive_instance_cache_without_unity_roundtrip(
    monkeypatch,
    tmp_path,
):
    import services.resources.editor_state as editor_state
    from services.state.editor_state_store import editor_state_store

    instance_id = "Test@deadbeef"
    session_id = "session-1"
    now_ms = int(time.time() * 1000)
    editor_state_store.begin_session(instance_id, session_id, str(tmp_path))
    editor_state_store.update_state(
        session_id,
        {
            "schema_version": "unity-mcp/editor_state@2",
            "observed_at_unix_ms": now_ms - 100,
            "sequence": 7,
            "compilation": {
                "is_compiling": False,
                "is_domain_reload_pending": False,
            },
            "tests": {"is_running": False},
        },
    )
    editor_state_store.touch_editor_heartbeat(session_id, now_ms)

    async def fake_instance(_ctx):
        return instance_id

    async def fail_transport(*_args, **_kwargs):
        raise AssertionError("cached editor state must not call Unity")

    monkeypatch.setattr(editor_state, "get_unity_instance_from_context", fake_instance)
    monkeypatch.setattr(
        editor_state.unity_transport,
        "send_with_unity_instance",
        fail_transport,
    )

    try:
        result = await editor_state.get_editor_state(DummyContext())
        payload = result.model_dump() if hasattr(result, "model_dump") else result
        data = payload["data"]

        assert data["sequence"] == 7
        assert data["unity"]["instance_id"] == instance_id
        assert data["transport"]["unity_bridge_connected"] is True
        assert data["transport"]["last_editor_heartbeat_unix_ms"] == now_ms
        assert data["staleness"]["basis"] == "editor_main_thread_heartbeat"
        assert data["advice"]["ready_for_tools"] is True
    finally:
        editor_state_store.end_session(session_id)


@pytest.mark.asyncio
async def test_project_info_is_cached_per_explicit_instance(monkeypatch):
    import services.resources.project_info as project_info

    instance_id = "Test@cafebabe"
    calls = 0

    async def fake_instance(_ctx):
        return instance_id

    async def fake_send(*_args, **_kwargs):
        nonlocal calls
        calls += 1
        return {
            "success": True,
            "data": {
                "projectRoot": "C:/Project",
                "projectName": "Project",
                "unityVersion": "6000.3.19f1",
                "platform": "WindowsEditor",
                "assetsPath": "C:/Project/Assets",
            },
        }

    project_info.clear_project_info_cache()
    monkeypatch.setattr(project_info, "get_unity_instance_from_context", fake_instance)
    monkeypatch.setattr(project_info, "send_with_unity_instance", fake_send)

    try:
        first = await project_info.get_project_info(DummyContext())
        second = await project_info.get_project_info(DummyContext())

        assert calls == 1
        assert first.data.projectRoot == "C:/Project"
        assert second.data.projectRoot == "C:/Project"
        assert second.message == "Retrieved cached project information."
    finally:
        project_info.clear_project_info_cache()
