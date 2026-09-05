import base64

import pytest

import services.tools.manage_shader as manage_shader_module
from .integration.test_helpers import DummyContext


@pytest.mark.asyncio
async def test_manage_shader_encodes_create_contents(monkeypatch):
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
        return {"success": True, "message": "Created", "data": {"path": "Assets/Test.shader"}}

    monkeypatch.setattr(
        manage_shader_module,
        "get_unity_instance_from_context",
        fake_instance,
    )
    monkeypatch.setattr(manage_shader_module, "send_with_unity_instance", fake_send)

    result = await manage_shader_module.manage_shader(
        DummyContext(),
        action="create",
        name="Test",
        path="Assets",
        contents="Shader \"Test\" {}",
    )

    assert result["success"] is True
    assert captured["instance"] == "Zornhau@abc123"
    assert captured["tool_name"] == "manage_shader"
    assert captured["params"]["contentsEncoded"] is True
    assert base64.b64decode(captured["params"]["encodedContents"]).decode("utf-8") == 'Shader "Test" {}'


@pytest.mark.asyncio
async def test_manage_shader_decodes_read_contents(monkeypatch):
    async def fake_instance(_ctx):
        return None

    async def fake_send(*_args):
        return {
            "success": True,
            "data": {
                "encodedContents": base64.b64encode(b"Shader content").decode("utf-8"),
                "contentsEncoded": True,
            },
        }

    monkeypatch.setattr(
        manage_shader_module,
        "get_unity_instance_from_context",
        fake_instance,
    )
    monkeypatch.setattr(manage_shader_module, "send_with_unity_instance", fake_send)

    result = await manage_shader_module.manage_shader(
        DummyContext(),
        action="read",
        name="Test",
        path="Assets",
    )

    assert result["success"] is True
    assert result["data"] == {"contents": "Shader content"}


@pytest.mark.asyncio
async def test_manage_shader_returns_structured_transport_error(monkeypatch):
    async def fake_instance(_ctx):
        return None

    async def fake_send(*_args):
        raise RuntimeError("connection closed")

    monkeypatch.setattr(
        manage_shader_module,
        "get_unity_instance_from_context",
        fake_instance,
    )
    monkeypatch.setattr(manage_shader_module, "send_with_unity_instance", fake_send)

    result = await manage_shader_module.manage_shader(
        DummyContext(),
        action="delete",
        name="Test",
        path="Assets",
    )

    assert result == {
        "success": False,
        "message": "Python error managing shader: connection closed",
    }
