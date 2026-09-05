import pytest

import services.tools.manage_material as manage_material_module
from .integration.test_helpers import DummyContext


@pytest.mark.asyncio
async def test_manage_material_normalizes_payload_and_routes_instance(monkeypatch):
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
        return {"success": True, "data": {"path": params["materialPath"]}}

    monkeypatch.setattr(
        manage_material_module,
        "get_unity_instance_from_context",
        fake_instance,
    )
    monkeypatch.setattr(manage_material_module, "send_with_unity_instance", fake_send)

    result = await manage_material_module.manage_material(
        DummyContext(),
        action="CREATE",
        material_path="Assets/Test.mat",
        shader="Universal Render Pipeline/Lit",
        properties='{"_Smoothness": 0.5}',
        color='{"r": 1, "g": 0.5, "b": 0.25, "a": 1}',
        value='{"texture": "Assets/Test.png"}',
        slot="2",
    )

    assert result["success"] is True
    assert captured["instance"] == "Zornhau@abc123"
    assert captured["tool_name"] == "manage_material"
    assert captured["params"] == {
        "action": "create",
        "materialPath": "Assets/Test.mat",
        "shader": "Universal Render Pipeline/Lit",
        "properties": {"_Smoothness": 0.5},
        "value": {"texture": "Assets/Test.png"},
        "color": [1.0, 0.5, 0.25, 1.0],
        "slot": 2,
    }


@pytest.mark.asyncio
async def test_manage_material_rejects_invalid_color_before_transport(monkeypatch):
    called = False

    async def fake_instance(_ctx):
        return None

    async def fake_send(*_args):
        nonlocal called
        called = True

    monkeypatch.setattr(
        manage_material_module,
        "get_unity_instance_from_context",
        fake_instance,
    )
    monkeypatch.setattr(manage_material_module, "send_with_unity_instance", fake_send)

    result = await manage_material_module.manage_material(
        DummyContext(),
        action="set_material_color",
        material_path="Assets/Test.mat",
        color=[1.0, 0.5],
    )

    assert result["success"] is False
    assert "color" in result["message"].lower()
    assert called is False


@pytest.mark.asyncio
async def test_manage_material_rejects_javascript_placeholder_value(monkeypatch):
    async def fake_instance(_ctx):
        return None

    monkeypatch.setattr(
        manage_material_module,
        "get_unity_instance_from_context",
        fake_instance,
    )

    result = await manage_material_module.manage_material(
        DummyContext(),
        action="set_material_shader_property",
        material_path="Assets/Test.mat",
        property="_MainTex",
        value="[object Object]",
    )

    assert result == {
        "success": False,
        "message": "value received invalid input: '[object Object]'",
    }
