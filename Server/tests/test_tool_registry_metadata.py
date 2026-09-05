import pytest
from unittest.mock import AsyncMock

from services.registry import get_registered_tools, mcp_for_unity_tool
import services.registry.tool_registry as tool_registry_module
import services.tools as tools_module
from services.tools.manage_tools import manage_tools


@pytest.fixture(autouse=True)
def restore_tool_registry_state():
    original_registry = list(tool_registry_module._tool_registry)
    try:
        yield
    finally:
        tool_registry_module._tool_registry[:] = original_registry


def test_tool_registry_defaults_unity_target_to_tool_name():
    @mcp_for_unity_tool()
    def _default_target_tool():
        return None

    registered_tools = get_registered_tools()
    tool_info = next(item for item in registered_tools if item["name"] == "_default_target_tool")
    assert tool_info["unity_target"] == "_default_target_tool"


def test_tool_registry_supports_server_only_and_alias_targets():
    @mcp_for_unity_tool(unity_target=None)
    def _server_only_tool():
        return None

    @mcp_for_unity_tool(unity_target="manage_script")
    def _manage_script_alias_tool():
        return None

    registered_tools = get_registered_tools()
    server_only = next(item for item in registered_tools if item["name"] == "_server_only_tool")
    alias_tool = next(item for item in registered_tools if item["name"] == "_manage_script_alias_tool")

    assert server_only["unity_target"] is None
    assert alias_tool["unity_target"] == "manage_script"


def test_tool_registry_does_not_leak_unity_target_into_tool_kwargs():
    @mcp_for_unity_tool(unity_target="manage_script", annotations={"title": "x"})
    def _non_leaking_target_tool():
        return None

    registered_tools = get_registered_tools()
    tool_info = next(item for item in registered_tools if item["name"] == "_non_leaking_target_tool")
    assert tool_info["unity_target"] == "manage_script"
    assert "unity_target" not in tool_info["kwargs"]
    assert tool_info["kwargs"]["annotations"] == {"title": "x"}


def test_tool_registry_rejects_invalid_unity_target_values():
    with pytest.raises(ValueError, match="Invalid unity_target"):
        @mcp_for_unity_tool(unity_target="")
        def _invalid_empty_target_tool():
            return None

    with pytest.raises(ValueError, match="Invalid unity_target"):
        @mcp_for_unity_tool(unity_target=123)  # type: ignore[arg-type]
        def _invalid_non_string_target_tool():
            return None


def test_server_registration_does_not_replace_canonical_tool_functions(monkeypatch):
    async def original(_ctx):
        return {"success": True}

    entry = {
        "func": original,
        "name": "isolated_tool",
        "description": "Isolated registration test",
        "unity_target": "isolated_tool",
        "group": "core",
        "kwargs": {"tags": {"group:core"}},
    }

    class FakeMcp:
        def __init__(self):
            self.registered = []

        def tool(self, **_kwargs):
            def register(func):
                self.registered.append(func)
                return func

            return register

        def disable(self, **_kwargs):
            return None

    monkeypatch.setattr(tools_module, "discover_modules", lambda *_args: [])
    monkeypatch.setattr(tools_module, "get_registered_tools", lambda: [entry])
    server = FakeMcp()

    tools_module.register_all_tools(server)
    tools_module.register_all_tools(server)

    assert entry["func"] is original
    assert len(server.registered) == 2


@pytest.mark.asyncio
async def test_manage_tools_skips_duplicate_visibility_transform():
    ctx = AsyncMock()
    ctx._get_visibility_rules.return_value = [
        {"tags": ["group:docs"], "enabled": True},
    ]

    result = await manage_tools(ctx, "activate", group="docs")

    assert result["unchanged"] is True
    ctx.enable_components.assert_not_awaited()


@pytest.mark.asyncio
async def test_registration_guard_fails_closed_before_unity_tool(monkeypatch):
    from models import MCPResponse
    import services.tools.preflight as preflight_module

    called = False

    async def blocked(_ctx, **_kwargs):
        return MCPResponse(success=False, error="infrastructure_error")

    async def unity_tool(_ctx):
        nonlocal called
        called = True
        return {"success": True}

    monkeypatch.setattr(preflight_module, "preflight", blocked)
    guarded = tools_module._with_unity_readiness_guard(
        "manage_gameobject",
        unity_tool,
        "manage_gameobject",
    )

    result = await guarded(AsyncMock())

    assert result["success"] is False
    assert called is False


@pytest.mark.asyncio
async def test_http_tool_sync_uses_session_visibility(monkeypatch):
    from core.config import config
    import transport.unity_transport as unity_transport

    async def fake_send(*_args, **_kwargs):
        return {
            "success": True,
            "data": {
                "tools": [
                    {"name": "manage_gameobject", "enabled": True},
                    {"name": "unity_docs", "enabled": False},
                ],
            },
        }

    monkeypatch.setattr(config, "transport_mode", "http")
    monkeypatch.setattr(unity_transport, "send_with_unity_instance", fake_send)
    monkeypatch.setattr(
        tools_module,
        "get_group_tool_names",
        lambda: {
            "core": ["manage_gameobject"],
            "docs": ["unity_docs"],
        },
    )
    ctx = AsyncMock()

    result = await tools_module.sync_tool_visibility_from_unity(
        ctx=ctx,
        instance_id="Zornhau@abc",
    )

    assert result["synced"] is True
    ctx.enable_components.assert_awaited_once_with(
        tags={"group:core"}, components={"tool"}
    )
    ctx.disable_components.assert_awaited_once_with(
        tags={"group:docs"}, components={"tool"}
    )
