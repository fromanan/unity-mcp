import importlib

import pytest

from .integration.test_helpers import DummyContext


RESOURCE_CASES = [
    ("active_tool", "get_active_tool", "get_active_tool", {}),
    ("cameras", "get_cameras", "get_cameras", {}),
    ("layers", "get_layers", "get_layers", {}),
    ("menu_items", "get_menu_items", "get_menu_items", {"refresh": True, "search": ""}),
    ("prefab_stage", "get_prefab_stage", "get_prefab_stage", {}),
    ("renderer_features", "get_renderer_features", "get_renderer_features", {}),
    ("rendering_stats", "get_rendering_stats", "get_rendering_stats", {}),
    ("selection", "get_selection", "get_selection", {}),
    ("tags", "get_tags", "get_tags", {}),
    ("volumes", "get_volumes", "get_volumes", {}),
    ("windows", "get_windows", "get_windows", {}),
]


def test_resource_registration_does_not_replace_canonical_functions(monkeypatch):
    import services.resources as resources_module

    async def original(_ctx):
        return {"success": True}

    entry = {
        "func": original,
        "uri": "mcpforunity://isolated",
        "name": "isolated_resource",
        "description": "Isolated registration test",
        "kwargs": {},
    }

    class FakeMcp:
        def __init__(self):
            self.registered = []

        def resource(self, **_kwargs):
            def register(func):
                self.registered.append(func)
                return func

            return register

    monkeypatch.setattr(resources_module, "discover_modules", lambda *_args: [])
    monkeypatch.setattr(resources_module, "get_registered_resources", lambda: [entry])
    server = FakeMcp()

    resources_module.register_all_resources(server)
    resources_module.register_all_resources(server)

    assert entry["func"] is original
    assert len(server.registered) == 2


@pytest.mark.asyncio
@pytest.mark.parametrize(("module_name", "function_name", "command_name", "expected_params"), RESOURCE_CASES)
async def test_simple_resource_routes_selected_instance(
    monkeypatch,
    module_name,
    function_name,
    command_name,
    expected_params,
):
    module = importlib.import_module(f"services.resources.{module_name}")
    captured = {}

    async def fake_instance(_ctx):
        return "Zornhau@abc123"

    async def fake_send(command, instance, resource_name, params):
        captured.update(
            command=command,
            instance=instance,
            resource_name=resource_name,
            params=params,
        )
        return {"success": True}

    monkeypatch.setattr(module, "get_unity_instance_from_context", fake_instance)
    monkeypatch.setattr(module, "send_with_unity_instance", fake_send)

    result = await getattr(module, function_name)(DummyContext())

    assert result.success is True
    assert captured["instance"] == "Zornhau@abc123"
    assert captured["resource_name"] == command_name
    assert captured["params"] == expected_params


@pytest.mark.asyncio
async def test_prefab_resources_decode_paths_and_preserve_response(monkeypatch):
    import services.resources.prefab as prefab_module

    calls = []

    async def fake_instance(_ctx):
        return "Zornhau@abc123"

    async def fake_send(_command, instance, resource_name, params):
        calls.append((instance, resource_name, params))
        return {"success": True, "data": {"prefabPath": params["prefabPath"]}}

    monkeypatch.setattr(prefab_module, "get_unity_instance_from_context", fake_instance)
    monkeypatch.setattr(prefab_module, "send_with_unity_instance", fake_send)

    info = await prefab_module.get_prefab_info(
        DummyContext(),
        "Assets%2FPrefabs%2FPlayer.prefab",
    )
    hierarchy = await prefab_module.get_prefab_hierarchy(
        DummyContext(),
        "Assets%2FPrefabs%2FPlayer.prefab",
    )
    docs = await prefab_module.get_prefab_api_docs(DummyContext())

    assert info.success is True
    assert hierarchy.success is True
    assert docs.success is True
    assert calls == [
        (
            "Zornhau@abc123",
            "manage_prefabs",
            {"action": "get_info", "prefabPath": "Assets/Prefabs/Player.prefab"},
        ),
        (
            "Zornhau@abc123",
            "manage_prefabs",
            {"action": "get_hierarchy", "prefabPath": "Assets/Prefabs/Player.prefab"},
        ),
    ]
    assert "mcpforunity://prefab/{encoded_path}" in docs.data["resources"]


def test_prefab_resource_wraps_unexpected_response_type():
    import services.resources.prefab as prefab_module

    result = prefab_module._normalize_response("unexpected")

    assert result.success is False
    assert result.error == "Unexpected response type: str"


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("function_name", "expected_command", "expected_params"),
    [
        ("get_tests", "get_tests", {}),
        ("get_tests_for_mode", "get_tests_for_mode", {"mode": "EditMode"}),
    ],
)
async def test_test_resources_route_and_parse(monkeypatch, function_name, expected_command, expected_params):
    import services.resources.tests as tests_module

    captured = {}

    async def fake_instance(_ctx):
        return "Zornhau@abc123"

    async def fake_send(_command, instance, resource_name, params):
        captured.update(instance=instance, resource_name=resource_name, params=params)
        return {
            "success": True,
            "data": {
                "items": [],
                "cursor": 0,
                "nextCursor": None,
                "totalCount": 0,
                "pageSize": 50,
                "hasMore": False,
            },
        }

    monkeypatch.setattr(tests_module, "get_unity_instance_from_context", fake_instance)
    monkeypatch.setattr(tests_module, "send_with_unity_instance", fake_send)

    function = getattr(tests_module, function_name)
    result = (
        await function(DummyContext(), mode="EditMode")
        if function_name == "get_tests_for_mode"
        else await function(DummyContext())
    )

    assert result.success is True
    assert captured == {
        "instance": "Zornhau@abc123",
        "resource_name": expected_command,
        "params": expected_params,
    }


@pytest.mark.asyncio
async def test_tool_groups_resource_reports_group_counts(monkeypatch):
    import services.resources.tool_groups as tool_groups_module

    monkeypatch.setattr(
        tool_groups_module,
        "TOOL_GROUPS",
        {"core": "Core tools", "docs": "Documentation tools"},
    )
    monkeypatch.setattr(tool_groups_module, "DEFAULT_ENABLED_GROUPS", {"core"})
    monkeypatch.setattr(
        tool_groups_module,
        "get_group_tool_names",
        lambda: {"core": ["manage_scene", "manage_asset"], "docs": ["unity_docs"]},
    )

    result = await tool_groups_module.get_tool_groups(DummyContext())

    assert result["total_groups"] == 2
    assert result["default_enabled"] == ["core"]
    assert result["groups"] == [
        {
            "name": "core",
            "description": "Core tools",
            "default_enabled": True,
            "tool_count": 2,
        },
        {
            "name": "docs",
            "description": "Documentation tools",
            "default_enabled": False,
            "tool_count": 1,
        },
    ]
