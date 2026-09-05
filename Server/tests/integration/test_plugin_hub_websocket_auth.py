"""Tests for PluginHub WebSocket API key authentication gate."""

import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from core.config import config
from core.constants import API_KEY_HEADER
from services.api_key_service import ApiKeyService, ValidationResult
from models.models import ToolDefinitionModel
from transport.plugin_hub import PluginHub
from transport.models import (
    ClientLifecycleMessage,
    PluginReadyMessage,
    RegisterMessage,
    RegisterToolsMessage,
)
from transport.plugin_registry import PluginRegistry


@pytest.fixture(autouse=True)
def _reset_api_key_singleton():
    ApiKeyService._instance = None
    yield
    ApiKeyService._instance = None


@pytest.fixture(autouse=True)
def _reset_plugin_hub():
    """Ensure PluginHub class-level state doesn't leak between tests."""
    old_registry = PluginHub._registry
    old_connections = PluginHub._connections.copy()
    old_pending = PluginHub._pending.copy()
    old_connection_ids = PluginHub._connection_id_by_session.copy()
    old_pending_registrations = PluginHub._pending_registrations.copy()
    old_registration_timeout_tasks = PluginHub._registration_timeout_tasks.copy()
    old_reloading_sessions = PluginHub._reloading_sessions.copy()
    old_lock = PluginHub._lock
    old_loop = PluginHub._loop

    yield

    PluginHub._registry = old_registry
    PluginHub._connections = old_connections
    PluginHub._pending = old_pending
    PluginHub._connection_id_by_session = old_connection_ids
    PluginHub._pending_registrations = old_pending_registrations
    for task in PluginHub._registration_timeout_tasks.values():
        if task not in old_registration_timeout_tasks.values():
            task.cancel()
    PluginHub._registration_timeout_tasks = old_registration_timeout_tasks
    PluginHub._reloading_sessions = old_reloading_sessions
    PluginHub._lock = old_lock
    PluginHub._loop = old_loop


def _make_mock_websocket(headers=None, state_attrs=None):
    """Create a mock WebSocket with configurable headers and state."""
    ws = AsyncMock()
    ws.headers = headers or {}
    ws.state = SimpleNamespace(**(state_attrs or {}))
    ws.accept = AsyncMock()
    ws.close = AsyncMock()
    ws.send_json = AsyncMock()
    return ws


def _make_hub():
    """Create a PluginHub instance with a minimal ASGI scope."""
    scope = {"type": "websocket"}
    return PluginHub(scope, receive=AsyncMock(), send=AsyncMock())


def _init_api_key_service(validate_result=None):
    """Initialize ApiKeyService with a mocked validate method."""
    svc = ApiKeyService(validation_url="https://auth.example.com/validate")
    if validate_result is not None:
        svc.validate = AsyncMock(return_value=validate_result)
    return svc


class TestWebSocketAuthGate:
    @pytest.mark.asyncio
    async def test_no_api_key_remote_hosted_rejected(self, monkeypatch):
        """WebSocket without API key in remote-hosted mode -> close 4401."""
        monkeypatch.setattr(config, "http_remote_hosted", True)
        _init_api_key_service(ValidationResult(valid=True, user_id="u1"))

        ws = _make_mock_websocket(headers={})  # No X-API-Key header
        hub = _make_hub()

        await hub.on_connect(ws)

        ws.close.assert_called_once_with(code=4401, reason="API key required")
        ws.accept.assert_not_called()

    @pytest.mark.asyncio
    async def test_invalid_api_key_rejected(self, monkeypatch):
        """WebSocket with invalid API key -> close 4403."""
        monkeypatch.setattr(config, "http_remote_hosted", True)
        _init_api_key_service(ValidationResult(
            valid=False, error="Invalid API key"))

        ws = _make_mock_websocket(headers={API_KEY_HEADER: "sk-bad-key"})
        hub = _make_hub()

        await hub.on_connect(ws)

        ws.close.assert_called_once_with(code=4403, reason="Invalid API key")
        ws.accept.assert_not_called()

    @pytest.mark.asyncio
    async def test_valid_api_key_accepted(self, monkeypatch):
        """WebSocket with valid API key -> accepted, user_id stored in state."""
        monkeypatch.setattr(config, "http_remote_hosted", True)
        _init_api_key_service(
            ValidationResult(valid=True, user_id="user-42",
                             metadata={"plan": "pro"})
        )

        ws = _make_mock_websocket(headers={API_KEY_HEADER: "sk-valid-key"})
        hub = _make_hub()

        await hub.on_connect(ws)

        ws.accept.assert_called_once()
        ws.close.assert_not_called()
        assert ws.state.user_id == "user-42"
        assert ws.state.api_key_metadata == {"plan": "pro"}
        # Should have sent welcome message
        ws.send_json.assert_called_once()

    @pytest.mark.asyncio
    async def test_auth_service_unavailable_close_1013(self, monkeypatch):
        """Auth service error with 'unavailable' -> close 1013 (try again later)."""
        monkeypatch.setattr(config, "http_remote_hosted", True)
        _init_api_key_service(
            ValidationResult(
                valid=False, error="Auth service unavailable", cacheable=False)
        )

        ws = _make_mock_websocket(headers={API_KEY_HEADER: "sk-some-key"})
        hub = _make_hub()

        await hub.on_connect(ws)

        ws.close.assert_called_once_with(code=1013, reason="Try again later")
        ws.accept.assert_not_called()

    @pytest.mark.asyncio
    async def test_not_remote_hosted_accepts_without_key(self, monkeypatch):
        """When not remote-hosted, WebSocket accepted without API key."""
        monkeypatch.setattr(config, "http_remote_hosted", False)

        ws = _make_mock_websocket(headers={})
        hub = _make_hub()

        await hub.on_connect(ws)

        ws.accept.assert_called_once()
        ws.close.assert_not_called()


class TestUserIdFlowsToRegistration:
    @pytest.mark.asyncio
    async def test_user_id_passed_to_registry_on_register(self, monkeypatch):
        """After valid auth, the register message should pass user_id to registry."""
        monkeypatch.setattr(config, "http_remote_hosted", True)
        _init_api_key_service(
            ValidationResult(valid=True, user_id="user-99")
        )

        registry = PluginRegistry()
        loop = asyncio.get_running_loop()
        PluginHub.configure(registry, loop)

        # Simulate full flow: connect, then register
        ws = _make_mock_websocket(headers={API_KEY_HEADER: "sk-valid-key"})
        hub = _make_hub()

        await hub.on_connect(ws)
        assert ws.state.user_id == "user-99"

        # Simulate register message
        register_data = {
            "type": "register",
            "project_name": "TestProject",
            "project_hash": "abc123",
            "unity_version": "2022.3",
        }
        await hub.on_receive(ws, register_data)

        # Verify registry has the user_id
        sessions = await registry.list_sessions(user_id="user-99")
        assert len(sessions) == 1
        session = next(iter(sessions.values()))
        assert session.user_id == "user-99"
        assert session.project_name == "TestProject"
        assert session.project_hash == "abc123"

        from services.state.editor_state_store import editor_state_store
        state_record = editor_state_store.get("TestProject@abc123")
        assert state_record is not None
        assert state_record.session_id == session.session_id

        await hub.on_disconnect(ws, 1000)
        assert editor_state_store.get("TestProject@abc123") is None


class TestReadyHandshakeLifecycle:
    @pytest.mark.asyncio
    async def test_provisional_reconnect_keeps_previous_session_until_ready(self, monkeypatch):
        monkeypatch.setattr(config, "http_remote_hosted", False)
        registry = PluginRegistry()
        PluginHub.configure(registry, asyncio.get_running_loop())
        hub = _make_hub()
        old_ws = _make_mock_websocket()
        new_ws = _make_mock_websocket()
        replace_global_tools = AsyncMock()
        monkeypatch.setattr(
            PluginHub,
            "_replace_global_tools_for_session",
            replace_global_tools,
        )

        await hub._handle_register(
            old_ws,
            RegisterMessage(
                project_name="Project",
                project_hash="hash",
                unity_version="6000.3",
            ),
        )
        old_session_id = await registry.get_session_id_by_hash("hash")

        await hub._handle_register(
            new_ws,
            RegisterMessage(
                project_name="Project",
                project_hash="hash",
                unity_version="6000.3",
                connection_id="generation-2",
                capabilities=["plugin_ready_ack_v1"],
            ),
        )

        assert await registry.get_session_id_by_hash("hash") == old_session_id
        provisional = PluginHub._pending_registrations[id(new_ws)]
        await hub._handle_register_tools(
            new_ws,
            RegisterToolsMessage(
                tools=[ToolDefinitionModel(name="ready_tool", description="ready")],
            ),
        )
        await hub._handle_plugin_ready(
            new_ws,
            PluginReadyMessage(
                session_id=provisional.session_id,
                connection_id="generation-2",
            ),
        )

        assert await registry.get_session_id_by_hash("hash") == provisional.session_id
        promoted = await registry.get_session(provisional.session_id)
        assert "ready_tool" in promoted.tools
        replace_global_tools.assert_awaited()
        promoted_tools, promoted_owner = replace_global_tools.await_args.args
        assert promoted_owner == provisional.session_id
        assert [tool.name for tool in promoted_tools] == ["ready_tool"]
        old_ws.close.assert_awaited_once_with(code=1001)
        assert new_ws.send_json.await_args_list[-1].args[0]["type"] == "plugin_ready_ack"

        await hub.on_disconnect(old_ws, 1005)
        assert await registry.get_session_id_by_hash("hash") == provisional.session_id

    @pytest.mark.asyncio
    async def test_provisional_disconnect_rolls_back_without_orphan_session(self, monkeypatch):
        monkeypatch.setattr(config, "http_remote_hosted", False)
        registry = PluginRegistry()
        PluginHub.configure(registry, asyncio.get_running_loop())
        hub = _make_hub()
        ws = _make_mock_websocket()

        await hub._handle_register(
            ws,
            RegisterMessage(
                project_name="Project",
                project_hash="hash",
                unity_version="6000.3",
                connection_id="generation-1",
                capabilities=["plugin_ready_ack_v1"],
            ),
        )
        await hub.on_disconnect(ws, 1005)

        assert not PluginHub._pending_registrations
        assert await registry.get_session_id_by_hash("hash") is None
        assert not PluginHub._connections

    @pytest.mark.asyncio
    async def test_activation_failure_restores_previous_routable_session(self, monkeypatch):
        monkeypatch.setattr(config, "http_remote_hosted", False)
        registry = PluginRegistry()
        PluginHub.configure(registry, asyncio.get_running_loop())
        hub = _make_hub()
        old_ws = _make_mock_websocket()
        new_ws = _make_mock_websocket()

        await hub._handle_register(
            old_ws,
            RegisterMessage(
                project_name="Project",
                project_hash="hash",
                unity_version="6000.3",
            ),
        )
        old_session_id = await registry.get_session_id_by_hash("hash")

        await hub._handle_register(
            new_ws,
            RegisterMessage(
                project_name="Project",
                project_hash="hash",
                unity_version="6000.3",
                connection_id="generation-2",
                capabilities=["plugin_ready_ack_v1"],
            ),
        )
        provisional = PluginHub._pending_registrations[id(new_ws)]

        from services.state.external_changes_scanner import external_changes_scanner

        original_start_tracking = external_changes_scanner.start_tracking
        calls = 0

        async def fail_new_activation(instance_id, project_path, session_id):
            nonlocal calls
            calls += 1
            if calls == 1:
                raise RuntimeError("simulated activation failure")
            await original_start_tracking(instance_id, project_path, session_id)

        monkeypatch.setattr(
            external_changes_scanner,
            "start_tracking",
            fail_new_activation,
        )

        with pytest.raises(RuntimeError, match="simulated activation failure"):
            await hub._handle_plugin_ready(
                new_ws,
                PluginReadyMessage(
                    session_id=provisional.session_id,
                    connection_id="generation-2",
                ),
            )

        assert await registry.get_session_id_by_hash("hash") == old_session_id
        assert PluginHub._connections[old_session_id] is old_ws
        old_ws.close.assert_not_awaited()

    @pytest.mark.asyncio
    async def test_provisional_registration_times_out_and_closes(self, monkeypatch):
        monkeypatch.setattr(config, "http_remote_hosted", False)
        monkeypatch.setattr(PluginHub, "REGISTRATION_TIMEOUT", 0.01)
        registry = PluginRegistry()
        PluginHub.configure(registry, asyncio.get_running_loop())
        hub = _make_hub()
        ws = _make_mock_websocket()

        await hub._handle_register(
            ws,
            RegisterMessage(
                project_name="Project",
                project_hash="hash",
                unity_version="6000.3",
                connection_id="generation-1",
                capabilities=["plugin_ready_ack_v1"],
            ),
        )
        await asyncio.sleep(0.03)

        assert not PluginHub._pending_registrations
        assert not PluginHub._registration_timeout_tasks
        ws.close.assert_awaited_once_with(
            code=1008,
            reason="Plugin registration timed out",
        )

    @pytest.mark.asyncio
    async def test_planned_reload_returns_retryable_response(self, monkeypatch):
        monkeypatch.setattr(config, "http_remote_hosted", False)
        registry = PluginRegistry()
        PluginHub.configure(registry, asyncio.get_running_loop())
        hub = _make_hub()
        ws = _make_mock_websocket()
        await hub._handle_register(
            ws,
            RegisterMessage(
                project_name="Project",
                project_hash="hash",
                unity_version="6000.3",
            ),
        )
        session_id = await registry.get_session_id_by_hash("hash")

        await hub._handle_client_lifecycle(
            ws,
            ClientLifecycleMessage(state="reloading", session_id=session_id),
        )
        result = await PluginHub.send_command(session_id, "manage_scene", {})

        assert result["success"] is False
        assert result["data"]["reason"] == "unity_reloading"
