import base64

import pytest

import services.tools.find_in_file as find_in_file_module
from .integration.test_helpers import DummyContext


@pytest.mark.parametrize(
    ("uri", "expected"),
    [
        ("mcpforunity://path/Assets/Scripts/Player.cs", ("Player", "Assets/Scripts")),
        ("C:/Project/Assets/Editor/Test%20Tool.cs", ("Test Tool", "Assets/Editor")),
        ("file:///tmp/standalone.cs", ("standalone", "tmp")),
    ],
)
def test_split_uri_normalizes_supported_paths(uri, expected):
    assert find_in_file_module._split_uri(uri) == expected


@pytest.mark.asyncio
async def test_find_in_file_decodes_content_and_caps_results(monkeypatch):
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
        contents = "First TODO\nsecond todo\nthird line"
        return {
            "success": True,
            "data": {
                "contentsEncoded": True,
                "encodedContents": base64.b64encode(contents.encode("utf-8")).decode("utf-8"),
            },
        }

    monkeypatch.setattr(
        find_in_file_module,
        "get_unity_instance_from_context",
        fake_instance,
    )
    monkeypatch.setattr(find_in_file_module, "send_with_unity_instance", fake_send)

    result = await find_in_file_module.find_in_file(
        DummyContext(),
        uri="Assets/Scripts/Player.cs",
        pattern="todo",
        max_results=1,
        ignore_case="yes",
    )

    assert result["success"] is True
    assert result["data"]["count"] == 1
    assert result["data"]["total_matches"] == 2
    assert result["data"]["matches"][0]["line"] == 1
    assert captured["instance"] == "Zornhau@abc123"
    assert captured["tool_name"] == "manage_script"
    assert captured["params"] == {
        "action": "read",
        "name": "Player",
        "path": "Assets/Scripts",
    }


@pytest.mark.asyncio
async def test_find_in_file_rejects_invalid_regex(monkeypatch):
    async def fake_instance(_ctx):
        return None

    async def fake_send(*_args):
        return {"success": True, "data": {"contents": "test"}}

    monkeypatch.setattr(
        find_in_file_module,
        "get_unity_instance_from_context",
        fake_instance,
    )
    monkeypatch.setattr(find_in_file_module, "send_with_unity_instance", fake_send)

    result = await find_in_file_module.find_in_file(
        DummyContext(),
        uri="Assets/Test.cs",
        pattern="(",
    )

    assert result["success"] is False
    assert result["message"].startswith("Invalid regex pattern:")


@pytest.mark.asyncio
async def test_find_in_file_preserves_structured_read_failure(monkeypatch):
    async def fake_instance(_ctx):
        return None

    async def fake_send(*_args):
        return {"success": False, "message": "Asset not found"}

    monkeypatch.setattr(
        find_in_file_module,
        "get_unity_instance_from_context",
        fake_instance,
    )
    monkeypatch.setattr(find_in_file_module, "send_with_unity_instance", fake_send)

    result = await find_in_file_module.find_in_file(
        DummyContext(),
        uri="Assets/Missing.cs",
        pattern="test",
    )

    assert result == {"success": False, "message": "Asset not found"}
