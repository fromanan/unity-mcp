import pytest

import services.tools.execute_menu_item as execute_menu_item_module
from .integration.test_helpers import DummyContext


@pytest.mark.asyncio
async def test_execute_menu_item_routes_to_selected_unity_instance(monkeypatch):
    captured = {}

    async def fake_instance(_ctx):
        return "Zornhau@abc123"

    async def fake_send(command, instance, tool_name, params):
        captured.update(
            command=command,
            instance=instance,
            tool_name=tool_name,
            params=params,
        )
        return {"success": True, "message": "Executed"}

    monkeypatch.setattr(
        execute_menu_item_module,
        "get_unity_instance_from_context",
        fake_instance,
    )
    monkeypatch.setattr(execute_menu_item_module, "send_with_unity_instance", fake_send)

    result = await execute_menu_item_module.execute_menu_item(
        DummyContext(),
        menu_path="Assets/Refresh",
    )

    assert result.success is True
    assert captured["instance"] == "Zornhau@abc123"
    assert captured["tool_name"] == "execute_menu_item"
    assert captured["params"] == {"menuPath": "Assets/Refresh"}


@pytest.mark.asyncio
async def test_execute_menu_item_omits_missing_path(monkeypatch):
    captured = {}

    async def fake_instance(_ctx):
        return None

    async def fake_send(_command, _instance, _tool_name, params):
        captured.update(params)
        return {"success": False, "message": "Missing menu path"}

    monkeypatch.setattr(
        execute_menu_item_module,
        "get_unity_instance_from_context",
        fake_instance,
    )
    monkeypatch.setattr(execute_menu_item_module, "send_with_unity_instance", fake_send)

    result = await execute_menu_item_module.execute_menu_item(DummyContext())

    assert result.success is False
    assert captured == {}
